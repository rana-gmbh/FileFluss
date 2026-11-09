// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.GitHub;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Providers.WebDavFamily;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// Port of the macOS <c>GitHubProviderTests</c> — the path grammar that maps a panel location
/// onto owner / repository / file, and the JSON behind the repository overview, both silent
/// failure modes — plus the HTTP behaviour of the Windows provider.
/// </summary>
// Asserts English UI text, so it shares the collection of the tests that switch the language.
[Collection("Localization")]
public sealed class GitHubProviderTests : IDisposable
{
    private const string Api = "https://api.github.com";

    private readonly DavTempFolder _temp = new();
    private readonly FakeHttpHandler _http = new();
    private readonly MemoryCredentialStore _store = new();
    private readonly Guid _id = Guid.NewGuid();

    public void Dispose() => _temp.Dispose();

    private GitHubProvider Connected()
    {
        _store.Save($"github.{_id}", new GitHubCredentials("ghp_secret", "robert"));
        return new GitHubProvider(_id, _store, _http);
    }

    private static string Repo(string owner, string name, bool isPrivate = false, string branch = "main", string pushed = "2026-10-20T08:15:00Z", long size = 10, bool archived = false)
        => $$"""{"name":"{{name}}","owner":{"login":"{{owner}}"},"private":{{(isPrivate ? "true" : "false")}},"archived":{{(archived ? "true" : "false")}},"default_branch":"{{branch}}","pushed_at":"{{pushed}}","size":{{size}}}""";

    private void ServeRepositories(params string[] repositories)
        => _http.OnJson(HttpMethod.Get, Api + "/user/repos", "[" + string.Join(',', repositories) + "]");

    private const string TreeJson = """
        {
          "sha": "abc123",
          "tree": [
            { "path": "README.md",                "type": "blob", "size": 1024, "sha": "b1" },
            { "path": "FileFluss",                "type": "tree", "sha": "t1" },
            { "path": "FileFluss/App",            "type": "tree", "sha": "t2" },
            { "path": "FileFluss/App/main.swift", "type": "blob", "size": 2048, "sha": "b2" },
            { "path": "FileFluss/logo.png",       "type": "blob", "size": 4096, "sha": "b3" },
            { "path": "Vendor/sparkle",           "type": "commit", "sha": "c1" },
            { "path": "latest",                   "type": "symlink", "size": 7, "sha": "s1" }
          ],
          "truncated": false
        }
        """;

    // MARK: - Paths

    [Fact]
    public void EachLevelOfTheHierarchyParsesToItsOwnCase()
    {
        Assert.Equal(GitHubPath.Root, GitHubPath.Parse("/"));
        Assert.Equal(GitHubPath.Root, GitHubPath.Parse(""));
        Assert.Equal(new GitHubPath(GitHubPathKind.Owner, "rana-gmbh"), GitHubPath.Parse("/rana-gmbh"));
        Assert.Equal(new GitHubPath(GitHubPathKind.Repository, "rana-gmbh", "FileFluss"), GitHubPath.Parse("/rana-gmbh/FileFluss"));
        Assert.Equal(new GitHubPath(GitHubPathKind.Tree, "rana-gmbh", "FileFluss"), GitHubPath.Parse("/rana-gmbh/FileFluss/Source"));
        Assert.Equal(
            new GitHubPath(GitHubPathKind.Tree, "rana-gmbh", "FileFluss", TreePath: "FileFluss/App/AppState.swift"),
            GitHubPath.Parse("/rana-gmbh/FileFluss/Source/FileFluss/App/AppState.swift"));
        Assert.Equal(
            new GitHubPath(GitHubPathKind.Archive, "rana-gmbh", "FileFluss", Filename: "FileFluss-main.zip"),
            GitHubPath.Parse("/rana-gmbh/FileFluss/FileFluss-main.zip"));
    }

    /// <summary>Anything that doesn't name a real location comes back null, so the provider reports "not found" instead of browsing somewhere else.</summary>
    [Fact]
    public void PathsThatNameNothingAreRejected()
    {
        Assert.Null(GitHubPath.Parse("/rana-gmbh/FileFluss/README.md"));
        Assert.Null(GitHubPath.Parse("/rana-gmbh/FileFluss/Archives/old.zip"));
        Assert.Null(GitHubPath.Parse("/rana-gmbh/FileFluss/sub/thing.zip"));
    }

    [Fact]
    public void AZipCommittedToTheRepositoryStaysATreeEntry()
        => Assert.Equal(
            new GitHubPath(GitHubPathKind.Tree, "owner", "repo", TreePath: "Fixtures/sample.zip"),
            GitHubPath.Parse("/owner/repo/Source/Fixtures/sample.zip"));

    [Fact]
    public void ASlashedBranchNameIsFlattenedForTheArchiveFilename()
    {
        Assert.Equal("FileFluss-main.zip", GitHubPath.ArchiveFilename("FileFluss", "main"));
        Assert.Equal("FileFluss-release-1.5.zip", GitHubPath.ArchiveFilename("FileFluss", "release/1.5"));
        Assert.Equal(
            new GitHubPath(GitHubPathKind.Archive, "owner", "FileFluss", Filename: "FileFluss-release-1.5.zip"),
            GitHubPath.Parse("/owner/FileFluss/" + GitHubPath.ArchiveFilename("FileFluss", "release/1.5")));
    }

    // MARK: - Decoding

    [Fact]
    public void VisibilityBranchPushDateAndSizeSurviveDecoding()
    {
        var repositories = GitHubResponseParser.Repositories("""
            [
              { "name": "FileFluss", "owner": { "login": "rana-gmbh" }, "private": true, "archived": false,
                "default_branch": "main", "pushed_at": "2026-10-20T08:15:00Z", "updated_at": "2026-10-20T09:00:00Z",
                "created_at": "2024-02-01T10:00:00Z", "size": 20480 },
              { "name": "homebrew-filefluss", "owner": { "login": "rana-gmbh" }, "private": false, "archived": true,
                "default_branch": "release/1.5", "pushed_at": "2026-09-01T12:00:00Z", "size": 12 }
            ]
            """);

        Assert.Equal(2, repositories.Count);
        var app = repositories[0];
        Assert.Equal("rana-gmbh/FileFluss", app.FullName);
        Assert.True(app.IsPrivate);
        Assert.False(app.IsArchived);
        Assert.Equal("main", app.DefaultBranch);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 15, 0, TimeSpan.Zero), app.PushedAt);

        // GitHub reports kilobytes; the estimate the UI shows is bytes.
        Assert.Equal(20480L * 1024, app.EstimatedBytes);

        var tap = repositories[1];
        Assert.False(tap.IsPrivate);
        Assert.True(tap.IsArchived);
        Assert.Equal("release/1.5", tap.DefaultBranch);
    }

    /// <summary>A repository never pushed to has a null pushed_at; unhandled it would date to 1970, "ancient" rather than "empty" to a sync.</summary>
    [Fact]
    public void ARepositoryWithNoCommitsFallsBackToARealDate()
    {
        var repository = GitHubResponseParser.Repositories("""
            [{ "name": "fresh", "owner": { "login": "robert" }, "private": true,
               "pushed_at": null, "updated_at": null, "created_at": "2026-10-27T07:00:00Z" }]
            """)[0];

        Assert.Equal(new DateTimeOffset(2026, 10, 27, 7, 0, 0, TimeSpan.Zero), repository.PushedAt);
        Assert.Equal("main", repository.DefaultBranch);
        Assert.Equal(0, repository.SizeKb);
    }

    [Fact]
    public void MalformedJsonIsAnInvalidResponse()
    {
        Assert.Equal(CloudErrorKind.InvalidResponse, Assert.Throws<CloudProviderException>(() => GitHubResponseParser.Repositories("{ not json")).Kind);
        Assert.Equal(CloudErrorKind.InvalidResponse, Assert.Throws<CloudProviderException>(() => GitHubResponseParser.Repositories("""[{"name":"x"}]""")).Kind);
    }

    [Fact]
    public void SubmodulesAndSymlinksAreLeftOutOfTheTree()
    {
        var tree = GitHubResponseParser.Tree(TreeJson);
        Assert.False(tree.IsTruncated);
        Assert.Equal(5, tree.Entries.Count);
        Assert.DoesNotContain(tree.Entries, e => e.Path is "Vendor/sparkle" or "latest");
    }

    [Fact]
    public void ADirectoryListingComesOutOfTheFlatRecursiveTree()
    {
        var tree = GitHubResponseParser.Tree(TreeJson);
        Assert.Equal(["FileFluss", "README.md"], tree.Children("").Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["App", "logo.png"], tree.Children("FileFluss").Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["main.swift"], tree.Children("FileFluss/App").Select(e => e.Name));
        Assert.Empty(tree.Children("Missing"));
    }

    [Fact]
    public void FolderSizesAddUpEveryBlobUnderneath()
    {
        var tree = GitHubResponseParser.Tree(TreeJson);
        Assert.Equal(1024 + 2048 + 4096, tree.RecursiveSize(""));
        Assert.Equal(2048 + 4096, tree.RecursiveSize("FileFluss"));
        Assert.Equal(2048, tree.RecursiveSize("FileFluss/App"));
        Assert.Equal(0, tree.RecursiveSize("Missing"));
    }

    [Fact]
    public void ATruncatedTreeReportsItselfAsTruncated()
        => Assert.True(GitHubResponseParser.Tree("""{ "sha": "x", "tree": [ { "path": "a", "type": "blob", "size": 1, "sha": "b" } ], "truncated": true }""").IsTruncated);

    // MARK: - Capabilities

    /// <summary>GoPro, the other read-only provider, keeps delete — you clear a camera down after copying the footage off it.</summary>
    [Fact]
    public void AGitHubAccountIsReadOnlyAndOffersNoDelete()
    {
        Assert.True(CloudProviderType.GitHub.IsReadOnly());
        Assert.False(CloudProviderType.GitHub.AllowsDeletion());
        Assert.False(CloudProviderType.GitHub.CanCreateFolders());
        Assert.True(CloudProviderType.GoPro.IsReadOnly());
        Assert.True(CloudProviderType.GoPro.AllowsDeletion());
        Assert.True(CloudProviderType.Dropbox.AllowsDeletion());
    }

    [Fact]
    public void GitHubIsListedUnderCodeHostingWithTheMacRawValue()
    {
        Assert.Equal(ProviderCategory.CodeHosting, CloudProviderType.GitHub.Category());
        Assert.Equal("GitHub", CloudProviderType.GitHub.DisplayName());
        Assert.Equal("GitHub", CloudProviderType.GitHub.LogoAssetName());
        Assert.Equal("\"github\"", System.Text.Json.JsonSerializer.Serialize(CloudProviderType.GitHub));
    }

    [Fact]
    public void RegistryCreatesTheProvider()
    {
        Assert.True(CloudProviderRegistry.IsAvailable(CloudProviderType.GitHub));
        using var provider = CloudProviderRegistry.Create(CloudProviderType.GitHub, _id, _store);
        Assert.IsType<GitHubProvider>(provider);
        Assert.False(provider.IsAuthenticated);
    }

    // MARK: - Connect

    [Fact]
    public async Task ConnectVerifiesTheTokenAndNamesTheAccountAfterTheLogin()
    {
        _http.OnJson(HttpMethod.Get, Api + "/user", """{"login":"robert","id":1}""");
        using var provider = new GitHubProvider(_id, _store, _http);

        var result = await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["token"] = "  ghp_secret \n" }), new ScriptedConnectPrompt());

        Assert.Equal("robert", result.AccountLabel);
        var stored = _store.Load<GitHubCredentials>($"github.{_id}")!;
        Assert.Equal(("ghp_secret", "robert"), (stored.Token, stored.Login));
        var request = Assert.Single(_http.Requests);
        Assert.Equal("Bearer ghp_secret", request.Header("Authorization"));
        Assert.Equal("2022-11-28", request.Header("X-GitHub-Api-Version"));
        Assert.Equal("application/vnd.github+json", request.Header("Accept"));
        Assert.True(provider.IsAuthenticated);
    }

    [Fact]
    public async Task ARejectedTokenLeavesStoredCredentialsUntouched()
    {
        using var provider = Connected();
        _http.On(HttpMethod.Get, Api + "/user", (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["token"] = "wrong" }), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
        Assert.Equal("ghp_secret", _store.Load<GitHubCredentials>($"github.{_id}")!.Token);
    }

    [Fact]
    public async Task TheConnectFormMatchesTheMacSheetAndEditingKeepsTheToken()
    {
        using (var fresh = new GitHubProvider(_id, _store, _http))
        {
            var fields = fresh.ConnectSpec.Modes.Single().Fields;
            Assert.StartsWith("Create a personal access token on GitHub", fields[0].Label);
            Assert.Equal("https://github.com/settings/tokens", fields.Single(f => f.Kind == ConnectFieldKind.Link).Url);
            var token = Assert.Single(fields, f => f.HasValue);
            Assert.Equal(("token", ConnectFieldKind.Password, "Personal Access Token"), (token.Key, token.Kind, token.Label));
        }

        using var provider = Connected();
        var edit = Assert.Single(provider.ConnectSpec.Modes.Single().Fields);
        Assert.Equal("To use a different personal access token, remove this account and add it again.", edit.Label);

        var result = await provider.ConnectAsync(ConnectValues.Empty(), new ScriptedConnectPrompt());
        Assert.Equal("robert", result.AccountLabel);
        Assert.Empty(_http.Requests);
    }

    // MARK: - Listing

    [Fact]
    public async Task TheRootListsOwnersWithTheirRepositoryCount()
    {
        ServeRepositories(
            Repo("rana-gmbh", "FileFluss", isPrivate: true, pushed: "2026-10-20T08:15:00Z"),
            Repo("rana-gmbh", "NetFluss", pushed: "2026-10-25T08:15:00Z"),
            Repo("robert", "dotfiles", pushed: "2026-01-01T00:00:00Z"));
        using var provider = Connected();

        var owners = await provider.ListDirectoryAsync("/");

        Assert.Equal(["rana-gmbh", "robert"], owners.Select(o => o.Name));
        Assert.Equal(["/rana-gmbh", "/robert"], owners.Select(o => o.Path));
        Assert.All(owners, o => Assert.Equal(CloudItemRole.VirtualContainer, o.Role));
        Assert.Equal(["2 repositories", "1 repository"], owners.Select(o => o.KindOverride));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 8, 15, 0, TimeSpan.Zero), owners[0].ModificationDate);

        var request = Assert.Single(_http.Requests);
        Assert.Equal(Api + "/user/repos?per_page=100&page=1&affiliation=owner%2Ccollaborator%2Corganization_member&sort=pushed", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task RepositoriesArePagedUntilAShortPage()
    {
        var fullPage = "[" + string.Join(',', Enumerable.Range(0, 100).Select(i => Repo("rana-gmbh", $"repo{i:D3}"))) + "]";
        _http.On(HttpMethod.Get, Api + "/user/repos", (request, _) => FakeHttpHandler.Json(
            request.RequestUri!.Query.Contains("page=1&", StringComparison.Ordinal) ? fullPage : "[" + Repo("robert", "last") + "]"));
        using var provider = Connected();

        var owners = await provider.ListDirectoryAsync("/");

        Assert.Equal([100, 1], owners.Select(o => o.KindOverride == "1 repository" ? 1 : 100));
        Assert.Equal(2, _http.Requests.Count);
        Assert.Contains("page=2&", _http.Requests.Last().Uri.Query, StringComparison.Ordinal);

        // The list is cached: browsing back to the root costs no request.
        await provider.ListDirectoryAsync("/rana-gmbh");
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task AnOwnerListsItsRepositoriesMarkedPrivateOrPublic()
    {
        ServeRepositories(
            Repo("rana-gmbh", "NetFluss"),
            Repo("rana-gmbh", "FileFluss", isPrivate: true),
            Repo("rana-gmbh", "Old", isPrivate: true, archived: true),
            Repo("robert", "dotfiles"));
        using var provider = Connected();

        var repositories = await provider.ListDirectoryAsync("/Rana-GmbH");

        Assert.Equal(["FileFluss", "NetFluss", "Old"], repositories.Select(r => r.Name));
        Assert.Equal(["Private repository", "Public repository", "Private repository (archived)"], repositories.Select(r => r.KindOverride));
        Assert.All(repositories, r => Assert.Equal(CloudItemRole.DriveRoot, r.Role));
        Assert.Equal("/rana-gmbh/FileFluss", repositories[0].Path);

        var missing = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/nobody"));
        Assert.Equal(CloudErrorKind.NotFound, missing.Kind);
    }

    [Fact]
    public async Task ARepositoryOffersItsSourceAndASnapshotArchive()
    {
        ServeRepositories(Repo("rana-gmbh", "FileFluss", branch: "release/1.5", size: 20480, isPrivate: true));
        _http.OnJson(HttpMethod.Get, "/git/trees/", TreeJson);
        using var provider = Connected();

        var items = await provider.ListDirectoryAsync("/rana-gmbh/FileFluss");

        Assert.Equal(["Source", "FileFluss-release-1.5.zip"], items.Select(i => i.Name));
        Assert.True(items[0].IsDirectory);
        Assert.Equal("Branch: release/1.5", items[0].KindOverride);
        Assert.False(items[1].IsDirectory);
        Assert.Equal("/rana-gmbh/FileFluss/FileFluss-release-1.5.zip", items[1].Path);
        Assert.Equal("Snapshot archive (no history)", items[1].KindOverride);
        Assert.Equal(20480L * 1024, items[1].Size);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 15, 0, TimeSpan.Zero), items[1].ModificationDate);
        Assert.Equal(Api + "/repos/rana-gmbh/FileFluss/git/trees/release/1.5?recursive=1", _http.Requests.Last().Uri.AbsoluteUri);
    }

    /// <summary>An empty repository has no tree (404): browsable, but with no archive GitHub would refuse to produce.</summary>
    [Fact]
    public async Task AnEmptyRepositoryOffersNoArchive()
    {
        ServeRepositories(Repo("robert", "fresh"));
        _http.On(HttpMethod.Get, "/git/trees/", (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        using var provider = Connected();

        var items = await provider.ListDirectoryAsync("/robert/fresh");

        Assert.Equal(["Source"], items.Select(i => i.Name));
        Assert.Empty(await provider.ListDirectoryAsync("/robert/fresh/Source"));
    }

    [Fact]
    public async Task TheSourceFolderListsFoldersFirstFromTheCachedTree()
    {
        ServeRepositories(Repo("rana-gmbh", "FileFluss"));
        _http.OnJson(HttpMethod.Get, "/git/trees/", TreeJson);
        using var provider = Connected();

        var top = await provider.ListDirectoryAsync("/rana-gmbh/FileFluss/Source");
        var inside = await provider.ListDirectoryAsync("/rana-gmbh/FileFluss/Source/FileFluss");

        Assert.Equal(["FileFluss", "README.md"], top.Select(i => i.Name));
        Assert.Equal(["App", "logo.png"], inside.Select(i => i.Name));
        Assert.Equal("/rana-gmbh/FileFluss/Source/FileFluss/logo.png", inside[1].Path);
        Assert.Equal(4096, inside[1].Size);
        Assert.Single(_http.Requests, r => r.Uri.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal));

        var file = await provider.GetFileMetadataAsync("/rana-gmbh/FileFluss/Source/FileFluss/App/main.swift");
        Assert.Equal(2048, file.Size);
        Assert.Equal(1024 + 2048 + 4096 + 10 * 1024, await provider.FolderSizeAsync("/rana-gmbh/FileFluss"));
    }

    /// <summary>Above ~100k entries GitHub truncates the tree; a partial repository must never be shown, so each folder comes from the Contents API.</summary>
    [Fact]
    public async Task ATruncatedTreeFallsBackToTheContentsApi()
    {
        ServeRepositories(Repo("torvalds", "linux", branch: "master"));
        _http.OnJson(HttpMethod.Get, "/git/trees/", """{"tree":[{"path":"a","type":"blob","size":1,"sha":"x"}],"truncated":true}""");
        _http.OnJson(HttpMethod.Get, "/contents/", """
            [{"path":"Documentation/admin guide","type":"dir","sha":"d1"},{"path":"Documentation/Changes","type":"file","size":42,"sha":"f1"}]
            """);
        using var provider = Connected();

        var items = await provider.ListDirectoryAsync("/torvalds/linux/Source/Documentation");

        Assert.Equal(["admin guide", "Changes"], items.Select(i => i.Name));
        Assert.Equal(Api + "/repos/torvalds/linux/contents/Documentation?ref=master", _http.Requests.Last().Uri.AbsoluteUri);
    }

    // MARK: - Downloads

    [Fact]
    public async Task TheSnapshotArchiveStreamsTheDefaultBranchZipball()
    {
        ServeRepositories(Repo("rana-gmbh", "FileFluss", branch: "main"));
        var zip = Encoding.ASCII.GetBytes("PK\u0003\u0004 snapshot bytes");
        _http.OnBytes(HttpMethod.Get, "/zipball/", zip);
        using var provider = Connected();
        var target = Path.Combine(_temp.Path, "FileFluss-main.zip");
        long reported = 0;

        await provider.DownloadFileAsync("/rana-gmbh/FileFluss/FileFluss-main.zip", target, delta => reported += delta);

        Assert.Equal(zip, await File.ReadAllBytesAsync(target));
        Assert.Equal(zip.Length, reported);
        var request = _http.Requests.Last();
        Assert.Equal(Api + "/repos/rana-gmbh/FileFluss/zipball/main", request.Uri.AbsoluteUri);
        Assert.Equal("application/vnd.github+json", request.Header("Accept"));
        Assert.Equal("Bearer ghp_secret", request.Header("Authorization"));
    }

    [Fact]
    public async Task AFileStreamsItsRawBlobFromTheDefaultBranch()
    {
        ServeRepositories(Repo("rana-gmbh", "FileFluss", branch: "dev"));
        _http.OnBytes(HttpMethod.Get, "/contents/", Encoding.UTF8.GetBytes("# FileFluss"));
        using var provider = Connected();
        var target = Path.Combine(_temp.Path, "README.md");

        await provider.DownloadFileAsync("/rana-gmbh/FileFluss/Source/Docs/Read me.md", target);

        Assert.Equal("# FileFluss", await File.ReadAllTextAsync(target));
        var request = _http.Requests.Last();
        Assert.Equal(Api + "/repos/rana-gmbh/FileFluss/contents/Docs/Read%20me.md?ref=dev", request.Uri.AbsoluteUri);
        Assert.Equal("application/vnd.github.raw", request.Header("Accept"));
    }

    [Fact]
    public async Task FoldersAndUnknownPathsCannotBeDownloaded()
    {
        using var provider = Connected();
        foreach (var path in new[] { "/", "/rana-gmbh", "/rana-gmbh/FileFluss", "/rana-gmbh/FileFluss/Source", "/rana-gmbh/FileFluss/README.md" })
        {
            var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DownloadFileAsync(path, Path.Combine(_temp.Path, "x")));
            Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        }

        Assert.Empty(_http.Requests);
    }

    // MARK: - Errors

    [Fact]
    public async Task AnExhaustedRateLimitIsReportedAsSuch()
    {
        _http.On(HttpMethod.Get, Api + "/user/repos", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            return response;
        });
        using var provider = Connected();

        Assert.Equal(CloudErrorKind.RateLimited, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"))).Kind);
    }

    [Fact]
    public async Task AForbiddenRepositoryAndARevokedTokenKeepTheirMeaning()
    {
        _http.On(HttpMethod.Get, Api + "/user/repos", (_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") });
        using (var provider = Connected())
        {
            var forbidden = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
            Assert.Equal((CloudErrorKind.ServerError, 403), (forbidden.Kind, forbidden.StatusCode));
        }

        var http = new FakeHttpHandler().On(HttpMethod.Get, Api + "/user/repos", (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        using var revoked = new GitHubProvider(_id, _store, http);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => revoked.ListDirectoryAsync("/"));
        Assert.True(error.NeedsReauthentication);
    }

    [Fact]
    public async Task NothingWritesBack()
    {
        using var provider = Connected();
        var file = _temp.File("a.txt", 3);

        await AssertNotImplemented(() => provider.UploadFileAsync(file, "/rana-gmbh/FileFluss/Source/a.txt"));
        await AssertNotImplemented(() => provider.DeleteItemAsync("/rana-gmbh/FileFluss"));
        await AssertNotImplemented(() => provider.CreateDirectoryAsync("/rana-gmbh/FileFluss/Source/new"));
        await AssertNotImplemented(() => provider.RenameItemAsync("/rana-gmbh/FileFluss", "Other"));
        await AssertNotImplemented(() => provider.MoveItemAsync("/rana-gmbh/FileFluss", "/robert/FileFluss"));
        Assert.False(provider.ShareLinkCapabilities.CanCreate);
        Assert.Empty(_http.Requests);

        static async Task AssertNotImplemented(Func<Task> action)
            => Assert.Equal(CloudErrorKind.NotImplemented, (await Assert.ThrowsAsync<CloudProviderException>(action)).Kind);
    }

    [Fact]
    public async Task UsageIsTheSumOfEveryRepositoryWithNoUpperBound()
    {
        ServeRepositories(Repo("a", "one", size: 3), Repo("b", "two", size: 5));
        using var provider = Connected();

        var quota = await provider.StorageQuotaAsync();

        Assert.Equal(new CloudStorageQuota(8 * 1024, null) { FetchedAt = quota!.FetchedAt }, quota);
        Assert.Null(quota.FreeBytes);
    }

    [Fact]
    public async Task WithoutATokenNothingIsAsked()
    {
        using var provider = new GitHubProvider(_id, _store, _http);
        Assert.Equal(CloudErrorKind.NotAuthenticated, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"))).Kind);
        Assert.Null(await provider.StorageQuotaAsync());
    }
}
