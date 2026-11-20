// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text;
using System.Text.Json;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Jottacloud;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

public class JottacloudProviderTests
{
    private const string TokenEndpoint = "https://id.example.test/token";
    private const string WellKnown = "https://id.example.test/.well-known/openid-configuration";
    private const string ArchiveRoot = "https://jfs.jottacloud.com/jfs/user/Jotta/Archive";

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();

    private static string PersonalToken(string username = "user", string authToken = "secret")
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["username"] = username,
            ["well_known_link"] = WellKnown,
            ["auth_token"] = authToken,
        });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Tokens(string access, string refresh, int expires = 3600)
        => $$"""{"access_token":"{{access}}","refresh_token":"{{refresh}}","expires_in":{{expires}}}""";

    private JottacloudProvider Signed(string refreshToken = "refresh-1")
    {
        _store.Save($"jottacloud.{_accountId}", new JottacloudCredentials("user", refreshToken, TokenEndpoint));
        return new JottacloudProvider(_accountId, _store, _http);
    }

    private JottacloudCredentials? Stored() => _store.Load<JottacloudCredentials>($"jottacloud.{_accountId}");

    // MARK: - Connect

    [Fact]
    public async Task ConnectExchangesThePersonalTokenAndStoresTheRefreshToken()
    {
        _http.OnJson(HttpMethod.Get, WellKnown, $$"""{"token_endpoint":"{{TokenEndpoint}}"}""");
        _http.OnJson(HttpMethod.Post, TokenEndpoint, Tokens("access-1", "refresh-1"));
        using var provider = new JottacloudProvider(_accountId, _store, _http);

        var result = await provider.ConnectAsync(new ConnectValues("default", new Dictionary<string, string> { ["token"] = PersonalToken() }), new ScriptedConnectPrompt());

        Assert.Equal("user", result.AccountLabel);
        Assert.True(provider.IsAuthenticated);
        Assert.Equal(new JottacloudCredentials("user", "refresh-1", TokenEndpoint), Stored());
        var body = _http.Single(HttpMethod.Post, TokenEndpoint).Body!;
        Assert.Contains("grant_type=password", body);
        Assert.Contains("client_id=jottacli", body);
        Assert.Contains("username=user", body);
        Assert.Contains("password=secret", body);
    }

    [Fact]
    public async Task ConnectFallsBackToTheDefaultEndpointWhenWellKnownFails()
    {
        _http.OnText(HttpMethod.Get, WellKnown, "nope", HttpStatusCode.InternalServerError);
        _http.OnJson(HttpMethod.Post, JottacloudProvider.DefaultTokenUrl, Tokens("a", "r"));
        using var provider = new JottacloudProvider(_accountId, _store, _http);

        await provider.ConnectAsync(new ConnectValues("default", new Dictionary<string, string> { ["token"] = PersonalToken() }), new ScriptedConnectPrompt());

        Assert.Equal(JottacloudProvider.DefaultTokenUrl, Stored()!.TokenEndpoint);
    }

    [Fact]
    public async Task FailedConnectLeavesStoredCredentialsUntouched()
    {
        _http.OnJson(HttpMethod.Get, WellKnown, $$"""{"token_endpoint":"{{TokenEndpoint}}"}""");
        _http.OnJson(HttpMethod.Post, TokenEndpoint, """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        using var provider = Signed("old-refresh");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() =>
            provider.ConnectAsync(new ConnectValues("default", new Dictionary<string, string> { ["token"] = PersonalToken() }), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal("old-refresh", Stored()!.RefreshToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("eyJmb28iOiJiYXIifQ")]
    public async Task MalformedPersonalTokenIsInvalidCredentials(string token)
    {
        using var provider = new JottacloudProvider(_accountId, _store, _http);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() =>
            provider.ConnectAsync(new ConnectValues("default", new Dictionary<string, string> { ["token"] = token }), new ScriptedConnectPrompt()));
        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Empty(_http.Requests);
    }

    // MARK: - Tokens

    [Fact]
    public async Task ConcurrentCallsShareOneRefreshAndPersistTheRotatedToken()
    {
        var refreshes = 0;
        _http.On(HttpMethod.Post, TokenEndpoint, (_, _) =>
        {
            Interlocked.Increment(ref refreshes);
            Thread.Sleep(150);
            return FakeHttpHandler.Json(Tokens("access-2", "refresh-2"));
        });
        _http.OnText(HttpMethod.Get, ArchiveRoot, "<folder name=\"Archive\"><files/></folder>", mediaType: "text/xml");
        using var provider = Signed("refresh-1");

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.ListDirectoryAsync("/")));

        Assert.Equal(1, refreshes);
        Assert.Equal("refresh-2", Stored()!.RefreshToken);
        Assert.All(_http.Requests.Where(r => r.Method == HttpMethod.Get), r => Assert.Equal("Bearer access-2", r.Headers["Authorization"]));
        Assert.Contains("refresh_token=refresh-1", _http.Single(HttpMethod.Post, TokenEndpoint).Body);
    }

    [Fact]
    public async Task RevokedRefreshTokenAsksToSignInAgain()
    {
        _http.OnJson(HttpMethod.Post, TokenEndpoint, """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        using var provider = Signed();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.True(error.NeedsReauthentication);
        Assert.NotNull(Stored());
    }

    [Fact]
    public async Task A401RefreshesOnceThenReportsUnauthorized()
    {
        var refreshes = 0;
        _http.On(HttpMethod.Post, TokenEndpoint, (_, _) => FakeHttpHandler.Json(Tokens($"access-{Interlocked.Increment(ref refreshes)}", "refresh-1")));
        _http.OnText(HttpMethod.Get, ArchiveRoot, "denied", HttpStatusCode.Unauthorized);
        using var provider = Signed();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
        Assert.Equal(2, refreshes);
        Assert.Equal(2, _http.Requests.Count(r => r.Method == HttpMethod.Get));
    }

    // MARK: - Listing and paths

    [Fact]
    public async Task ListingAddsEachSubFoldersOwnDate()
    {
        _http.OnJson(HttpMethod.Post, TokenEndpoint, Tokens("a", "refresh-1"));
        _http.OnText(HttpMethod.Get, ArchiveRoot + "/Docs/Sub folder", "<folder name=\"Sub folder\"><modified>2024-06-01-T08:00:00Z</modified></folder>");
        _http.OnText(HttpMethod.Get, ArchiveRoot + "/Docs", """
            <folder name="Docs">
              <folders><folder name="Sub folder"/></folders>
              <files><file name="a &amp; b.txt"><currentRevision><state>COMPLETED</state><size>5</size><modified>2024-01-01-T00:00:00Z</modified></currentRevision></file></files>
            </folder>
            """);
        using var provider = Signed();

        var items = await provider.ListDirectoryAsync("/Docs/");

        var folder = Assert.Single(items, i => i.IsDirectory);
        Assert.Equal("/Docs/Sub folder", folder.Path);
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 8, 0, 0, TimeSpan.Zero), folder.ModificationDate);
        var file = Assert.Single(items, i => !i.IsDirectory);
        Assert.Equal("/Docs/a & b.txt", file.Path);
        Assert.Contains(_http.Requests, r => r.Uri.AbsoluteUri == "https://jfs.jottacloud.com/jfs/user/Jotta/Archive/Docs/Sub%20folder");
    }

    [Fact]
    public void UrlsEscapeEachSegmentAndQueryValue()
    {
        Assert.Equal("https://jfs.jottacloud.com/jfs/user/Jotta/Archive/a%20%26%20b/c%2Bd.txt", JottacloudProvider.JfsUrl("user", "/a & b/c+d.txt"));
        Assert.Equal("https://jfs.jottacloud.com/jfs/user/Jotta/Archive", JottacloudProvider.JfsUrl("user", "/"));
        Assert.Equal(
            "https://jfs.jottacloud.com/jfs/user/Jotta/Archive/x?mv=/user/Jotta/Archive/new%20name%26.txt",
            JottacloudProvider.JfsUrl("user", "/x", ("mv", JottacloudProvider.AbsoluteJfsPath("user", "/new name&.txt"))));
        Assert.Equal("/jfs/Jotta/Archive/dir/f.txt", JottacloudProvider.AllocatePath("/dir/f.txt"));
        Assert.Equal("2024-01-15-T10:30:00Z", JottacloudProvider.JottaTime(new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task MissingFolderIsNotFound()
    {
        _http.OnJson(HttpMethod.Post, TokenEndpoint, Tokens("a", "refresh-1"));
        _http.OnText(HttpMethod.Get, ArchiveRoot + "/gone", "<error/>", HttpStatusCode.NotFound);
        using var provider = Signed();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/gone"));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal("/gone", error.ItemPath);
    }

    [Fact]
    public async Task MoveCopyRenameAndMkdirUseTheJfsQueryForms()
    {
        _http.OnJson(HttpMethod.Post, TokenEndpoint, Tokens("a", "refresh-1"));
        _http.OnText(HttpMethod.Post, ArchiveRoot, "<folder/>");
        using var provider = Signed();

        await provider.MoveItemAsync("/a.txt", "/Docs/a.txt");
        await provider.CopyItemAsync("/a.txt", "/Docs/b.txt");
        await provider.RenameItemAsync("/Docs/a.txt", "c.txt");
        await provider.CreateDirectoryAsync("/New");

        var posts = _http.Requests.Where(r => r.Method == HttpMethod.Post && r.Uri.Host == "jfs.jottacloud.com").Select(r => r.Uri.PathAndQuery).ToList();
        Assert.Equal(
            [
                "/jfs/user/Jotta/Archive/a.txt?mv=/user/Jotta/Archive/Docs/a.txt",
                "/jfs/user/Jotta/Archive/a.txt?cp=/user/Jotta/Archive/Docs/b.txt",
                "/jfs/user/Jotta/Archive/Docs/a.txt?mv=/user/Jotta/Archive/Docs/c.txt",
                "/jfs/user/Jotta/Archive/New?mkDir=true",
            ],
            posts);
    }

    [Fact]
    public async Task DeleteFallsBackToTheFolderForm()
    {
        _http.OnJson(HttpMethod.Post, TokenEndpoint, Tokens("a", "refresh-1"));
        _http.OnText(HttpMethod.Post, "dl=true", "<error/>", HttpStatusCode.BadRequest);
        _http.OnText(HttpMethod.Post, "dlDir=true", "<folder/>");
        using var provider = Signed();

        await provider.DeleteItemAsync("/Old");

        Assert.Contains(_http.Requests, r => r.Uri.PathAndQuery == "/jfs/user/Jotta/Archive/Old?dlDir=true");
    }

    // MARK: - Transfers

    [Fact]
    public async Task UploadAllocatesThenStreamsTheBytes()
    {
        var local = Path.Combine(Path.GetTempPath(), $"ff-jotta-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(local, "hello jotta");
        try
        {
            _http.OnJson(HttpMethod.Post, TokenEndpoint, Tokens("a", "refresh-1"));
            _http.OnJson(HttpMethod.Post, "files/v1/allocate", """{"state":"ALLOCATED","upload_url":"https://up.example.test/upload/123","resume_pos":0}""");
            _http.OnJson(HttpMethod.Post, "up.example.test/upload/123", """{"state":"COMPLETED"}""");
            using var provider = Signed();
            long reported = 0;

            await provider.UploadFileAsync(local, "/Docs/hello.txt", d => Interlocked.Add(ref reported, d));

            Assert.Equal(11, reported);
            using var allocation = JsonDocument.Parse(_http.Single(HttpMethod.Post, "files/v1/allocate").Body!);
            Assert.Equal("/jfs/Jotta/Archive/Docs/hello.txt", allocation.RootElement.GetProperty("path").GetString());
            Assert.Equal(11, allocation.RootElement.GetProperty("bytes").GetInt64());
            Assert.Equal(await JottacloudProvider.StreamingMd5Async(local, CancellationToken.None), allocation.RootElement.GetProperty("md5").GetString());
            Assert.EndsWith("Z", allocation.RootElement.GetProperty("modified").GetString());
            var upload = _http.Single(HttpMethod.Post, "up.example.test");
            Assert.Equal("hello jotta", upload.Body);
            Assert.Equal("Bearer a", upload.Headers["Authorization"]);
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public async Task DedupedUploadSendsNoBytesButCompletesTheCount()
    {
        var local = Path.Combine(Path.GetTempPath(), $"ff-jotta-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(local, "12345");
        try
        {
            _http.OnJson(HttpMethod.Post, TokenEndpoint, Tokens("a", "refresh-1"));
            _http.OnJson(HttpMethod.Post, "files/v1/allocate", """{"state":"COMPLETED"}""");
            using var provider = Signed();
            long reported = 0;

            await provider.UploadFileAsync(local, "/x.txt", d => reported += d);

            Assert.Equal(5, reported);
            Assert.Equal(2, _http.Requests.Count);
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public async Task UploadRejectedForSizeIsFileTooLarge()
    {
        var local = Path.Combine(Path.GetTempPath(), $"ff-jotta-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(local, "12345");
        try
        {
            _http.OnJson(HttpMethod.Post, TokenEndpoint, Tokens("a", "refresh-1"));
            _http.OnText(HttpMethod.Post, "files/v1/allocate", "too big", HttpStatusCode.RequestEntityTooLarge);
            using var provider = Signed();

            var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(local, "/x.txt"));
            Assert.Equal(CloudErrorKind.FileTooLarge, error.Kind);
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public async Task DownloadStreamsTheBinaryMode()
    {
        var local = Path.Combine(Path.GetTempPath(), $"ff-jotta-{Guid.NewGuid():N}.bin");
        try
        {
            _http.OnJson(HttpMethod.Post, TokenEndpoint, Tokens("a", "refresh-1"));
            _http.OnBytes(HttpMethod.Get, "mode=bin", [1, 2, 3, 4]);
            using var provider = Signed();
            long reported = 0;

            await provider.DownloadFileAsync("/f.bin", local, d => reported += d);

            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(local));
            Assert.Equal(4, reported);
            Assert.Contains(_http.Requests, r => r.Uri.PathAndQuery == "/jfs/user/Jotta/Archive/f.bin?mode=bin");
        }
        finally
        {
            File.Delete(local);
        }
    }

    // MARK: - Quota and sharing

    [Fact]
    public async Task QuotaReadsTheAccountRoot()
    {
        _http.OnJson(HttpMethod.Post, TokenEndpoint, Tokens("a", "refresh-1"));
        _http.OnText(HttpMethod.Get, "jfs.jottacloud.com/jfs/user", "<user><capacity>-1</capacity><usage>42</usage></user>");
        using var provider = Signed();

        var quota = await provider.StorageQuotaAsync();

        Assert.NotNull(quota);
        Assert.Equal(42, quota.UsedBytes);
        Assert.Null(quota.TotalBytes);
    }

    [Fact]
    public async Task ShareLinkPointsAtTheLandingPageAndTheDirectDownload()
    {
        _http.OnJson(HttpMethod.Post, TokenEndpoint, Tokens("a", "refresh-1"));
        _http.OnText(HttpMethod.Get, "mode=enableShare", "<file name=\"a.txt\"><publicURI>uri123</publicURI><publicSharePath>/s/abc</publicSharePath></file>");
        using var provider = Signed();

        var link = await provider.CreateShareLinkAsync("/a.txt", ShareLinkOptions.Default);

        Assert.Equal("https://www.jottacloud.com/s/abc", link.Url.AbsoluteUri);
        Assert.Equal("https://www.jottacloud.com/opin/io/downloadPublic/user/uri123", link.DirectDownloadUrl!.AbsoluteUri);
    }

    [Fact]
    public async Task UnsharedFileHasNoExistingLinkAndRefusalsCarryJfsMessage()
    {
        _http.OnJson(HttpMethod.Post, TokenEndpoint, Tokens("a", "refresh-1"));
        _http.OnText(HttpMethod.Get, "mode=enableShare", "<error><message>Sharing is not available</message></error>", HttpStatusCode.Forbidden);
        _http.OnText(HttpMethod.Get, ArchiveRoot + "/a.txt", "<file name=\"a.txt\"/>");
        using var provider = Signed();

        Assert.Null(await provider.ExistingShareLinkAsync("/a.txt"));
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/a.txt", ShareLinkOptions.Default));
        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("Sharing is not available", error.Message);
    }

    [Fact]
    public void NotAuthenticatedWithoutStoredCredentials()
    {
        using var provider = new JottacloudProvider(_accountId, _store, _http);
        Assert.False(provider.IsAuthenticated);
        Assert.False(provider.ConnectSpec.SupportsInlineReauthentication);
        Assert.Contains(provider.ConnectSpec.Modes[0].Fields, f => f.Kind == ConnectFieldKind.Link && f.Url == JottacloudProvider.TokenPageUrl);
    }

    [Fact]
    public void RegistryCreatesTheProvider()
    {
        using var provider = CloudProviderRegistry.Create(CloudProviderType.Jottacloud, _accountId, _store);
        Assert.IsType<JottacloudProvider>(provider);
    }
}
