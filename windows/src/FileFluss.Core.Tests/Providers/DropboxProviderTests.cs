// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Dropbox;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

public sealed class DropboxProviderTests : IDisposable
{
    private const string TokenUrl = "api.dropboxapi.com/oauth2/token";

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ff-dropbox-" + Guid.NewGuid().ToString("N"));

    public DropboxProviderTests()
    {
        Directory.CreateDirectory(_temp);
    }

    private string Key => $"dropbox.{_accountId}";

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // MARK: - Lifecycle

    [Fact]
    public async Task WithoutStoredCredentialsTheProviderIsSignedOut()
    {
        using var provider = new DropboxProvider(_accountId, _store, _http);

        Assert.False(provider.IsAuthenticated);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
        Assert.Null(await provider.StorageQuotaAsync());
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public void RestoresStoredCredentialsWithoutNetwork()
    {
        using var provider = Provider();

        Assert.True(provider.IsAuthenticated);
        Assert.Equal("access-1", provider.ApiClient!.Credentials.AccessToken);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public void RegistryCreatesTheDropboxProvider()
    {
        using var provider = CloudProviderRegistry.Create(CloudProviderType.Dropbox, _accountId, _store);

        Assert.IsType<DropboxProvider>(provider);
        Assert.True(provider.ConnectSpec.SupportsInlineReauthentication);
        Assert.Null(provider.MaxUploadFileSize);
    }

    [Fact]
    public async Task DisconnectForgetsTheCredentials()
    {
        using var provider = Provider();

        await provider.DisconnectAsync();

        Assert.False(provider.IsAuthenticated);
        Assert.Null(_store.Load(Key));
    }

    // MARK: - Listing

    [Fact]
    public async Task ListingParsesEntriesAndFollowsTheCursor()
    {
        _http.OnJson(HttpMethod.Post, "/files/list_folder/continue", """
            {"entries":[{".tag":"file","name":".hidden","path_lower":"/.hidden","path_display":"/.hidden","id":"id:H","size":1,"server_modified":"2020-01-01T00:00:00Z"}],
             "cursor":"c2","has_more":false}
            """);
        _http.OnJson(HttpMethod.Post, "/files/list_folder", """
            {"entries":[
              {".tag":"folder","name":"Photos","path_lower":"/photos","path_display":"/Photos","id":"id:F1"},
              {".tag":"file","name":"a.txt","path_lower":"/a.txt","path_display":"/a.txt","id":"id:A1","size":1234,
               "server_modified":"2015-05-12T15:50:38Z","content_hash":"abc123"},
              {".tag":"deleted","name":"gone.txt","path_lower":"/gone.txt"}
            ],"cursor":"c1","has_more":true}
            """);
        using var provider = Provider();

        var items = await provider.ListDirectoryAsync("/");

        Assert.Equal(["Photos", "a.txt", ".hidden"], items.Select(i => i.Name));
        var folder = items[0];
        Assert.True(folder.IsDirectory);
        Assert.Equal("/Photos", folder.Path);
        Assert.Equal("id:F1", folder.Id);
        Assert.Equal(0, folder.Size);
        Assert.False(folder.HasKnownModificationDate);

        var file = items[1];
        Assert.False(file.IsDirectory);
        Assert.Equal(1234, file.Size);
        Assert.Equal(new DateTimeOffset(2015, 5, 12, 15, 50, 38, TimeSpan.Zero), file.ModificationDate);
        Assert.Equal("abc123", file.Checksum);
        Assert.True(items[2].IsHidden);

        var first = _http.Requests.First();
        Assert.Equal("https://api.dropboxapi.com/2/files/list_folder", first.Uri.ToString());
        Assert.Equal("Bearer access-1", first.Headers["Authorization"]);
        Assert.Equal("application/json", first.Headers["Content-Type"]);
        var body = Json(first.Body);
        Assert.Equal(string.Empty, body.GetProperty("path").GetString());
        Assert.False(body.GetProperty("recursive").GetBoolean());
        Assert.False(body.GetProperty("include_deleted").GetBoolean());
        Assert.Equal(2000, body.GetProperty("limit").GetInt32());
        Assert.Equal("c1", Json(_http.Single(HttpMethod.Post, "/list_folder/continue").Body).GetProperty("cursor").GetString());
    }

    [Fact]
    public async Task OperationsMapPathsToTheirEndpoints()
    {
        _http.OnJson(HttpMethod.Post, "/files/list_folder", """{"entries":[],"cursor":"c","has_more":false}""");
        _http.OnJson(HttpMethod.Post, "/files/get_metadata", """{".tag":"file","name":"a.txt","path_display":"/Photos/2026/a.txt","id":"id:A","size":5}""");
        _http.OnJson(HttpMethod.Post, "/files/move_v2", "{}");
        _http.OnJson(HttpMethod.Post, "/files/copy_v2", "{}");
        _http.OnJson(HttpMethod.Post, "/files/delete_v2", "{}");
        using var provider = Provider();

        await provider.ListDirectoryAsync("/Photos/2026/");
        var metadata = await provider.GetFileMetadataAsync("/Photos/2026/a.txt");
        await provider.MoveItemAsync("/Photos/2026/a.txt", "/Archive/a.txt");
        await provider.CopyItemAsync("/Archive/a.txt", "/Backup/a.txt");
        await provider.RenameItemAsync("/Photos/2026/a.txt", "b.txt");
        await provider.RenameItemAsync("/top.txt", "renamed.txt");
        await provider.DeleteItemAsync("/Backup/a.txt");

        Assert.Equal("/Photos/2026", Json(_http.Single(HttpMethod.Post, "/files/list_folder").Body).GetProperty("path").GetString());
        Assert.Equal("/Photos/2026/a.txt", metadata.Path);
        Assert.Equal("/Photos/2026/a.txt", Json(_http.Single(HttpMethod.Post, "/files/get_metadata").Body).GetProperty("path").GetString());

        var moves = _http.Requests.Where(r => r.Uri.ToString().EndsWith("/files/move_v2", StringComparison.Ordinal)).Select(r => Json(r.Body)).ToList();
        Assert.Equal(3, moves.Count);
        Assert.Equal("/Archive/a.txt", moves[0].GetProperty("to_path").GetString());
        Assert.False(moves[0].GetProperty("autorename").GetBoolean());
        Assert.Equal("/Photos/2026/b.txt", moves[1].GetProperty("to_path").GetString());
        Assert.Equal("/renamed.txt", moves[2].GetProperty("to_path").GetString());

        var copy = Json(_http.Single(HttpMethod.Post, "/files/copy_v2").Body);
        Assert.Equal("/Archive/a.txt", copy.GetProperty("from_path").GetString());
        Assert.Equal("/Backup/a.txt", copy.GetProperty("to_path").GetString());
        Assert.Equal("/Backup/a.txt", Json(_http.Single(HttpMethod.Post, "/files/delete_v2").Body).GetProperty("path").GetString());
    }

    [Fact]
    public async Task CreateFolderSucceedsQuietlyWhenItExists()
    {
        _http.OnJson(HttpMethod.Post, "/files/get_metadata", """{".tag":"folder","name":"Docs","path_display":"/Docs","id":"id:D"}""");
        using var provider = Provider();

        await provider.CreateDirectoryAsync("/Docs");

        Assert.DoesNotContain(_http.Requests, r => r.Uri.ToString().Contains("create_folder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateFolderCreatesWhenMissing()
    {
        _http.OnJson(HttpMethod.Post, "/files/get_metadata", """{"error_summary":"path/not_found/..","error":{".tag":"path","path":{".tag":"not_found"}}}""", HttpStatusCode.Conflict);
        _http.OnJson(HttpMethod.Post, "/files/create_folder_v2", """{"metadata":{"name":"Docs"}}""");
        using var provider = Provider();

        await provider.CreateDirectoryAsync("/Docs");

        var body = Json(_http.Single(HttpMethod.Post, "/files/create_folder_v2").Body);
        Assert.Equal("/Docs", body.GetProperty("path").GetString());
        Assert.False(body.GetProperty("autorename").GetBoolean());
    }

    [Fact]
    public async Task SearchSendsScopeAndParsesMatches()
    {
        _http.OnJson(HttpMethod.Post, "/files/search_v2", """
            {"matches":[{"metadata":{".tag":"metadata","metadata":{".tag":"file","name":"report.pdf","path_display":"/Work/report.pdf","id":"id:R","size":99}}}],
             "has_more":false}
            """);
        using var provider = Provider();

        var hits = await provider.SearchFilesAsync("report", "/Work");

        var hit = Assert.Single(hits!);
        Assert.Equal("/Work/report.pdf", hit.Path);
        Assert.Equal(99, hit.Size);
        var body = Json(_http.Single(HttpMethod.Post, "/files/search_v2").Body);
        Assert.Equal("report", body.GetProperty("query").GetString());
        Assert.Equal("/Work", body.GetProperty("options").GetProperty("path").GetString());
        Assert.Equal(100, body.GetProperty("options").GetProperty("max_results").GetInt32());
    }

    // MARK: - Transfers

    [Fact]
    public async Task DownloadEscapesTheApiArgAndWritesTheFile()
    {
        var payload = Encoding.UTF8.GetBytes("file contents");
        _http.OnBytes(HttpMethod.Post, "content.dropboxapi.com/2/files/download", payload);
        using var provider = Provider();
        var target = Path.Combine(_temp, "out.txt");
        long reported = 0;

        await provider.DownloadFileAsync("/Grüße/😀.txt", target, delta => Interlocked.Add(ref reported, delta));

        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        Assert.Equal(payload.Length, reported);
        var arg = _http.Single(HttpMethod.Post, "/files/download").Headers["Dropbox-API-Arg"];
        Assert.All(arg, c => Assert.True(c < 128, $"non-ASCII {(int)c} in header"));
        Assert.Contains("\\u00fc", arg, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/Grüße/😀.txt", Json(arg).GetProperty("path").GetString());
    }

    [Fact]
    public void EscapeNonAsciiWritesSurrogatePairsForAstralCharacters()
    {
        Assert.Equal("{\"p\":\"a\\u00e9\\ud83d\\ude00\"}", DropboxApiClient.EscapeNonAscii("{\"p\":\"aé😀\"}"));
    }

    [Fact]
    public async Task DownloadOfAMissingFileIsNotFound()
    {
        _http.OnJson(HttpMethod.Post, "/files/download", """{"error_summary":"path/not_found/","error":{".tag":"path","path":{".tag":"not_found"}}}""", HttpStatusCode.Conflict);
        using var provider = Provider();
        var target = Path.Combine(_temp, "missing.txt");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DownloadFileAsync("/missing.txt", target));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal("/missing.txt", error.ItemPath);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task SmallUploadSendsTheFileWithCommitInfo()
    {
        var local = WriteFile("hello.txt", "hello world");
        File.SetLastWriteTimeUtc(local, new DateTime(2024, 3, 1, 10, 20, 30, 500, DateTimeKind.Utc));
        _http.OnJson(HttpMethod.Post, "content.dropboxapi.com/2/files/upload", """{"name":"hello.txt"}""");
        using var provider = Provider();
        long reported = 0;

        await provider.UploadFileAsync(local, "/Docs/hello.txt", delta => Interlocked.Add(ref reported, delta));

        var request = _http.Single(HttpMethod.Post, "/files/upload");
        Assert.Equal("hello world", request.Body);
        Assert.Equal("application/octet-stream", request.Headers["Content-Type"]);
        var arg = Json(request.Headers["Dropbox-API-Arg"]);
        Assert.Equal("/Docs/hello.txt", arg.GetProperty("path").GetString());
        Assert.Equal("add", arg.GetProperty("mode").GetString());
        Assert.False(arg.GetProperty("autorename").GetBoolean());
        Assert.False(arg.GetProperty("mute").GetBoolean());
        Assert.Equal("2024-03-01T10:20:30Z", arg.GetProperty("client_modified").GetString());
        Assert.Equal(11, reported);
    }

    [Fact]
    public async Task LargeUploadUsesASessionWithChunkBoundaries()
    {
        var local = WriteFile("big.bin", "0123456789");
        _http.OnJson(HttpMethod.Post, "/files/upload_session/start", """{"session_id":"S1"}""");
        _http.OnText(HttpMethod.Post, "/files/upload_session/append_v2", "null", mediaType: "application/json");
        _http.OnJson(HttpMethod.Post, "/files/upload_session/finish", """{"name":"big.bin"}""");
        using var provider = Provider();
        provider.ApiClient!.SimpleUploadLimit = 4;
        provider.ApiClient.SessionChunkSize = 4;
        long reported = 0;

        await provider.UploadFileAsync(local, "/big.bin", delta => Interlocked.Add(ref reported, delta));

        var requests = _http.Requests.ToList();
        Assert.Equal(
            ["/2/files/upload_session/start", "/2/files/upload_session/append_v2", "/2/files/upload_session/append_v2", "/2/files/upload_session/finish"],
            requests.Select(r => r.Uri.AbsolutePath));
        Assert.Equal(string.Empty, requests[0].Body ?? string.Empty);
        Assert.False(Json(requests[0].Headers["Dropbox-API-Arg"]).GetProperty("close").GetBoolean());

        Assert.Equal("0123", requests[1].Body);
        var append1 = Json(requests[1].Headers["Dropbox-API-Arg"]);
        Assert.Equal("S1", append1.GetProperty("cursor").GetProperty("session_id").GetString());
        Assert.Equal(0, append1.GetProperty("cursor").GetProperty("offset").GetInt64());
        Assert.False(append1.GetProperty("close").GetBoolean());

        Assert.Equal("4567", requests[2].Body);
        Assert.Equal(4, Json(requests[2].Headers["Dropbox-API-Arg"]).GetProperty("cursor").GetProperty("offset").GetInt64());

        Assert.Equal("89", requests[3].Body);
        var finish = Json(requests[3].Headers["Dropbox-API-Arg"]);
        Assert.Equal(8, finish.GetProperty("cursor").GetProperty("offset").GetInt64());
        Assert.Equal("/big.bin", finish.GetProperty("commit").GetProperty("path").GetString());
        Assert.Equal("add", finish.GetProperty("commit").GetProperty("mode").GetString());
        Assert.Equal(10, reported);
    }

    // MARK: - Errors and tokens

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, CloudErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, CloudErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, CloudErrorKind.ServerError)]
    [InlineData(HttpStatusCode.Conflict, CloudErrorKind.ServerError)]
    public async Task StatusCodesMapToErrorKinds(HttpStatusCode status, CloudErrorKind expected)
    {
        _http.OnJson(HttpMethod.Post, "/files/list_folder", """{"error_summary":"path/conflict/"}""", status);
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/x"));

        Assert.Equal(expected, error.Kind);
    }

    [Fact]
    public async Task NotFoundConflictNamesThePath()
    {
        _http.OnJson(HttpMethod.Post, "/files/list_folder", """{"error_summary":"path/not_found/..","error":{".tag":"path","path":{".tag":"not_found"}}}""", HttpStatusCode.Conflict);
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/missing"));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal("/missing", error.ItemPath);
    }

    [Fact]
    public async Task A401RefreshesOnceRetriesAndPersists()
    {
        var calls = 0;
        _http.On(HttpMethod.Post, "/files/list_folder", (_, _) => Interlocked.Increment(ref calls) == 1
            ? FakeHttpHandler.Json("""{"error_summary":"expired_access_token/"}""", HttpStatusCode.Unauthorized)
            : FakeHttpHandler.Json("""{"entries":[],"cursor":"c","has_more":false}"""));
        _http.OnJson(HttpMethod.Post, TokenUrl, """{"access_token":"access-2","expires_in":14400,"token_type":"bearer"}""");
        using var provider = Provider();

        await provider.ListDirectoryAsync("/");

        var lists = _http.Requests.Where(r => r.Uri.AbsolutePath == "/2/files/list_folder").ToList();
        Assert.Equal(["Bearer access-1", "Bearer access-2"], lists.Select(r => r.Headers["Authorization"]));
        var form = HttpUtility.ParseQueryString(_http.Single(HttpMethod.Post, TokenUrl).Body!);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("refresh-1", form["refresh_token"]);
        Assert.Equal(DropboxApiClient.AppKey, form["client_id"]);
        Assert.Equal(DropboxApiClient.AppSecret, form["client_secret"]);

        var stored = _store.Load<DropboxCredentials>(Key)!;
        Assert.Equal("access-2", stored.AccessToken);
        Assert.Equal("refresh-1", stored.RefreshToken);
        Assert.Equal("Robert", stored.DisplayName);
    }

    [Fact]
    public async Task A401AfterRefreshIsUnauthorized()
    {
        _http.OnJson(HttpMethod.Post, "/files/list_folder", """{"error_summary":"invalid_access_token/"}""", HttpStatusCode.Unauthorized);
        _http.OnJson(HttpMethod.Post, TokenUrl, """{"access_token":"access-2","expires_in":14400,"token_type":"bearer"}""");
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
        Assert.True(error.NeedsReauthentication);
    }

    [Fact]
    public async Task ARejectedRefreshIsNotAuthenticated()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        using var provider = Provider(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
        Assert.DoesNotContain(_http.Requests, r => r.Uri.AbsolutePath.StartsWith("/2/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentCallersShareOneRefresh()
    {
        var refreshes = 0;
        using var gate = new ManualResetEventSlim();
        _http.On(HttpMethod.Post, TokenUrl, (_, _) =>
        {
            Interlocked.Increment(ref refreshes);
            gate.Wait(TimeSpan.FromSeconds(10));
            return FakeHttpHandler.Json("""{"access_token":"access-2","refresh_token":"refresh-2","expires_in":14400,"token_type":"bearer"}""");
        });
        _http.OnJson(HttpMethod.Post, "/files/list_folder", """{"entries":[],"cursor":"c","has_more":false}""");
        using var provider = Provider(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var calls = Enumerable.Range(0, 8).Select(_ => Task.Run(() => provider.ListDirectoryAsync("/"))).ToList();
        await Task.Delay(200);
        gate.Set();
        await Task.WhenAll(calls);

        Assert.Equal(1, refreshes);
        Assert.All(
            _http.Requests.Where(r => r.Uri.AbsolutePath == "/2/files/list_folder"),
            r => Assert.Equal("Bearer access-2", r.Headers["Authorization"]));
        Assert.Equal("refresh-2", _store.Load<DropboxCredentials>(Key)!.RefreshToken);
    }

    // MARK: - Quota

    [Fact]
    public async Task QuotaParsesUsageAndAllocation()
    {
        _http.OnJson(HttpMethod.Post, "/users/get_space_usage", """{"used":1000,"allocation":{".tag":"individual","allocated":2000}}""");
        using var provider = Provider();

        var quota = await provider.StorageQuotaAsync();

        Assert.Equal(1000, quota!.UsedBytes);
        Assert.Equal(2000, quota.TotalBytes);
        Assert.Equal(1000, quota.FreeBytes);
        var request = _http.Single(HttpMethod.Post, "/users/get_space_usage");
        Assert.Equal("null", request.Body);
        Assert.Equal("application/json", request.Headers["Content-Type"]);
    }

    [Fact]
    public async Task QuotaWithoutAnEnforcedAllocationHasNoTotal()
    {
        _http.OnJson(HttpMethod.Post, "/users/get_space_usage", """{"used":5,"allocation":{".tag":"team","allocated":0}}""");
        using var provider = Provider();

        var quota = await provider.StorageQuotaAsync();

        Assert.Equal(5, quota!.UsedBytes);
        Assert.Null(quota.TotalBytes);
    }

    [Fact]
    public async Task QuotaFailureIsUnknownRatherThanAnError()
    {
        _http.OnText(HttpMethod.Post, "/users/get_space_usage", "missing_scope", HttpStatusCode.BadRequest);
        using var provider = Provider();

        Assert.Null(await provider.StorageQuotaAsync());
    }

    // MARK: - Share links

    [Fact]
    public async Task CreateShareLinkReturnsPreviewAndDirectLinks()
    {
        _http.OnJson(HttpMethod.Post, "/sharing/create_shared_link_with_settings", """
            {".tag":"file","url":"https://www.dropbox.com/scl/fi/abc/a.txt?rlkey=k&dl=0","name":"a.txt",
             "expires":"2026-12-31T23:00:00Z","link_permissions":{"require_password":true,"allow_download":true}}
            """);
        using var provider = Provider();

        var link = await provider.CreateShareLinkAsync("/a.txt", new ShareLinkOptions
        {
            Password = "Secret123",
            Expiry = new DateTimeOffset(2026, 12, 31, 23, 0, 0, 250, TimeSpan.Zero),
        });

        Assert.Equal("https://www.dropbox.com/scl/fi/abc/a.txt?rlkey=k&dl=0", link.Url.OriginalString);
        Assert.Equal("https://www.dropbox.com/scl/fi/abc/a.txt?rlkey=k&dl=1", link.DirectDownloadUrl!.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero), link.ExpiresAt);
        Assert.True(link.HasPassword);
        Assert.Null(link.Note);

        var body = Json(_http.Single(HttpMethod.Post, "/sharing/create_shared_link_with_settings").Body);
        Assert.Equal("/a.txt", body.GetProperty("path").GetString());
        var settings = body.GetProperty("settings");
        Assert.True(settings.GetProperty("require_password").GetBoolean());
        Assert.Equal("Secret123", settings.GetProperty("link_password").GetString());
        Assert.Equal("2026-12-31T23:00:00Z", settings.GetProperty("expires").GetString());
        Assert.True(settings.GetProperty("allow_download").GetBoolean());
        Assert.Equal("viewer", settings.GetProperty("access").GetString());
    }

    [Fact]
    public async Task CreateShareLinkOmitsPasswordFieldsWhenNoneWasAsked()
    {
        _http.OnJson(HttpMethod.Post, "/sharing/create_shared_link_with_settings", """{"url":"https://www.dropbox.com/s/x/a.txt?dl=0","link_permissions":{"allow_download":false}}""");
        using var provider = Provider();

        var link = await provider.CreateShareLinkAsync("/a.txt", new ShareLinkOptions { AllowDownload = false });

        var settings = Json(_http.Single(HttpMethod.Post, "/sharing/").Body).GetProperty("settings");
        Assert.False(settings.TryGetProperty("require_password", out _));
        Assert.False(settings.TryGetProperty("link_password", out _));
        Assert.False(settings.TryGetProperty("expires", out _));
        Assert.False(settings.GetProperty("allow_download").GetBoolean());
        Assert.Null(link.DirectDownloadUrl);
        Assert.False(link.HasPassword);
    }

    [Fact]
    public async Task AnAlreadySharedFileHandsBackItsLinkWithANote()
    {
        _http.OnJson(HttpMethod.Post, "/sharing/create_shared_link_with_settings", """
            {"error_summary":"shared_link_already_exists/metadata/..",
             "error":{".tag":"shared_link_already_exists","shared_link_already_exists":{".tag":"metadata","metadata":{".tag":"file","url":"https://www.dropbox.com/s/old/a.txt?dl=0"}}}}
            """, HttpStatusCode.Conflict);
        using var provider = Provider();

        var link = await provider.CreateShareLinkAsync("/a.txt", new ShareLinkOptions { Password = "Secret123" });

        Assert.Equal("https://www.dropbox.com/s/old/a.txt?dl=0", link.Url.OriginalString);
        Assert.Equal("This file already had a public link, so the requested settings were not applied.", link.Note);
        Assert.False(link.HasPassword);
    }

    [Fact]
    public async Task AnUnverifiedExistingLinkIsLookedUp()
    {
        _http.OnJson(HttpMethod.Post, "/sharing/create_shared_link_with_settings", """
            {"error_summary":"shared_link_already_exists/unverified/..",
             "error":{".tag":"shared_link_already_exists","shared_link_already_exists":{".tag":"unverified"}}}
            """, HttpStatusCode.Conflict);
        _http.OnJson(HttpMethod.Post, "/sharing/list_shared_links", """{"links":[{"url":"https://www.dropbox.com/s/found/a.txt?dl=0"}],"has_more":false}""");
        using var provider = Provider();

        var link = await provider.CreateShareLinkAsync("/a.txt", ShareLinkOptions.Default);

        Assert.Equal("https://www.dropbox.com/s/found/a.txt?dl=0", link.Url.OriginalString);
        Assert.Null(link.Note);
        Assert.True(Json(_http.Single(HttpMethod.Post, "/sharing/list_shared_links").Body).GetProperty("direct_only").GetBoolean());
    }

    [Fact]
    public async Task AMissingSharingScopeAsksForANewSignIn()
    {
        _http.OnText(HttpMethod.Post, "/sharing/create_shared_link_with_settings", """{"error_summary":"missing_scope/..","error":{".tag":"missing_scope","required_scope":"sharing.write"}}""", HttpStatusCode.Unauthorized);
        _http.OnJson(HttpMethod.Post, TokenUrl, """{"access_token":"access-2","expires_in":14400,"token_type":"bearer"}""");
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/a.txt", ShareLinkOptions.Default));

        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
    }

    [Fact]
    public async Task APlanRestrictionSurfacesDropboxsSummary()
    {
        _http.OnJson(HttpMethod.Post, "/sharing/create_shared_link_with_settings", """{"error_summary":"settings_error/not_authorized/..","error":{".tag":"settings_error"}}""", HttpStatusCode.Conflict);
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/a.txt", new ShareLinkOptions { Password = "x" }));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("settings_error/not_authorized/..", error.Message);
    }

    [Fact]
    public async Task ExistingShareLinkIsNullForAnUnsharedFile()
    {
        _http.OnJson(HttpMethod.Post, "/sharing/list_shared_links", """{"links":[],"has_more":false}""");
        using var provider = Provider();

        Assert.Null(await provider.ExistingShareLinkAsync("/a.txt"));
    }

    [Fact]
    public async Task UpdateShareLinkModifiesByUrlAndClearsExpiry()
    {
        _http.OnJson(HttpMethod.Post, "/sharing/list_shared_links", """{"links":[{"url":"https://www.dropbox.com/s/k/a.txt?dl=0"}],"has_more":false}""");
        _http.OnJson(HttpMethod.Post, "/sharing/modify_shared_link_settings", """{"url":"https://www.dropbox.com/s/k/a.txt?dl=0","link_permissions":{"require_password":false,"allow_download":true}}""");
        using var provider = Provider();

        var link = await provider.UpdateShareLinkAsync("/a.txt", ShareLinkOptions.Default);

        var body = Json(_http.Single(HttpMethod.Post, "/sharing/modify_shared_link_settings").Body);
        Assert.Equal("https://www.dropbox.com/s/k/a.txt?dl=0", body.GetProperty("url").GetString());
        Assert.False(body.GetProperty("settings").GetProperty("require_password").GetBoolean());
        Assert.True(body.GetProperty("remove_expiration").GetBoolean());
        Assert.False(link.HasPassword);
    }

    [Fact]
    public async Task UpdateWithoutALinkSaysCreateOneFirst()
    {
        _http.OnJson(HttpMethod.Post, "/sharing/list_shared_links", """{"links":[],"has_more":false}""");
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.UpdateShareLinkAsync("/a.txt", ShareLinkOptions.Default));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
    }

    [Fact]
    public async Task RemoveShareLinkRevokesTheExistingUrlOrDoesNothing()
    {
        var links = """{"links":[{"url":"https://www.dropbox.com/s/k/a.txt?dl=0"}],"has_more":false}""";
        _http.On(HttpMethod.Post, "/sharing/list_shared_links", (_, body) => FakeHttpHandler.Json(
            body!.Contains("/a.txt", StringComparison.Ordinal) ? links : """{"links":[],"has_more":false}"""));
        _http.OnJson(HttpMethod.Post, "/sharing/revoke_shared_link", "null");
        using var provider = Provider();

        await provider.RemoveShareLinkAsync("/a.txt");
        await provider.RemoveShareLinkAsync("/b.txt");

        var revoke = Assert.Single(_http.Requests, r => r.Uri.AbsolutePath == "/2/sharing/revoke_shared_link");
        Assert.Equal("https://www.dropbox.com/s/k/a.txt?dl=0", Json(revoke.Body).GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("https://www.dropbox.com/s/a/f.txt?dl=0", "https://www.dropbox.com/s/a/f.txt?dl=1")]
    [InlineData("https://www.dropbox.com/s/a/f.txt?dl=1", "https://www.dropbox.com/s/a/f.txt?dl=1")]
    [InlineData("https://www.dropbox.com/s/a/f.txt", "https://www.dropbox.com/s/a/f.txt?dl=1")]
    [InlineData("https://www.dropbox.com/scl/fi/a/f.txt?rlkey=xyz&dl=0", "https://www.dropbox.com/scl/fi/a/f.txt?rlkey=xyz&dl=1")]
    [InlineData("https://www.dropbox.com/scl/fi/a/f.txt?rlkey=xyz", "https://www.dropbox.com/scl/fi/a/f.txt?rlkey=xyz&dl=1")]
    public void DirectDownloadUrlFlipsTheDlParameter(string preview, string direct)
        => Assert.Equal(direct, DropboxApiClient.DirectDownloadUrl(preview)!.AbsoluteUri);

    [Fact]
    public void CapabilitiesAreTheFullSet()
    {
        using var provider = Provider();
        var capabilities = provider.ShareLinkCapabilities;

        Assert.True(capabilities.CanCreate && capabilities.CanQueryExisting && capabilities.CanUpdate && capabilities.CanRemove);
        Assert.True(capabilities.SupportsPassword && capabilities.SupportsExpiry && capabilities.SupportsDownloadToggle);
        Assert.True(capabilities.PasswordRequiresPaidPlan && capabilities.ExpiryRequiresPaidPlan);
    }

    // MARK: - Connecting

    [Fact]
    public async Task ConnectRunsPkceOAuthAndStoresTheCredentials()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":14400,"token_type":"bearer","account_id":"dbid:9"}""");
        _http.OnJson(HttpMethod.Post, "/users/get_current_account", """{"account_id":"dbid:9","name":{"display_name":"Robert Rudolph"}}""");
        using var provider = new DropboxProvider(_accountId, _store, _http);
        Uri? authorizeUri = null;
        provider.Authenticator = (build, _) =>
        {
            authorizeUri = build("http://localhost:53682");
            var state = HttpUtility.ParseQueryString(authorizeUri.Query)["state"];
            return Task.FromResult(new OAuthCallback("http://localhost:53682", new Uri($"http://localhost:53682/?code=the-code&state={Uri.EscapeDataString(state!)}")));
        };
        var prompt = new ScriptedConnectPrompt();

        var result = await provider.ConnectAsync(ConnectValues.Empty("browser"), prompt);

        Assert.Equal("Robert Rudolph", result.AccountLabel);
        Assert.True(provider.IsAuthenticated);
        Assert.Equal("https://www.dropbox.com/oauth2/authorize", authorizeUri!.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(authorizeUri.Query);
        Assert.Equal(DropboxApiClient.AppKey, query["client_id"]);
        Assert.Equal("http://localhost:53682", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("offline", query["token_access_type"]);
        Assert.Equal(DropboxApiClient.OAuthScopes, query["scope"]);

        var form = HttpUtility.ParseQueryString(_http.Single(HttpMethod.Post, TokenUrl).Body!);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("the-code", form["code"]);
        Assert.Equal("http://localhost:53682", form["redirect_uri"]);
        Assert.Equal(DropboxApiClient.AppSecret, form["client_secret"]);
        Assert.Equal(query["code_challenge"], Pkce.Challenge(form["code_verifier"]!));

        var account = _http.Single(HttpMethod.Post, "/users/get_current_account");
        Assert.Equal("Bearer new-access", account.Headers["Authorization"]);
        Assert.Equal("null", account.Body);
        Assert.Equal("application/json", account.Headers["Content-Type"]);

        var stored = _store.Load<DropboxCredentials>(Key)!;
        Assert.Equal("new-access", stored.AccessToken);
        Assert.Equal("new-refresh", stored.RefreshToken);
        Assert.Equal("dbid:9", stored.AccountId);
        Assert.Equal("Robert Rudolph", stored.DisplayName);
        Assert.NotNull(prompt.StatusLines.First());
        Assert.Null(prompt.StatusLines.Last());
    }

    [Fact]
    public async Task ConnectStillAddsTheAccountWhenTheNameLookupFails()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":14400,"token_type":"bearer"}""");
        _http.OnText(HttpMethod.Post, "/users/get_current_account", "Bad HTTP Content-Type header", HttpStatusCode.BadRequest);
        using var provider = new DropboxProvider(_accountId, _store, _http) { Authenticator = Callback("the-code") };

        var result = await provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt());

        Assert.Equal("Unknown", result.AccountLabel);
        Assert.Equal("Dropbox", CloudAccount.ComposeDisplayName(CloudProviderType.Dropbox, result.AccountLabel));
        Assert.Equal("unknown", _store.Load<DropboxCredentials>(Key)!.AccountId);
    }

    [Fact]
    public async Task AFailedConnectLeavesStoredCredentialsUntouched()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        using var provider = Provider();
        provider.Authenticator = Callback("bad-code");
        var before = _store.Load(Key);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal(before, _store.Load(Key));
        Assert.Equal("access-1", provider.ApiClient!.Credentials.AccessToken);
    }

    [Fact]
    public async Task AForeignStateIsRejectedBeforeAnyTokenExchange()
    {
        using var provider = Provider();
        provider.Authenticator = (build, _) =>
        {
            build("http://localhost:1");
            return Task.FromResult(new OAuthCallback("http://localhost:1", new Uri("http://localhost:1/?code=c&state=forged")));
        };
        var before = _store.Load(Key);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
        Assert.Empty(_http.Requests);
        Assert.Equal(before, _store.Load(Key));
    }

    // MARK: - Account label self-heal

    [Fact]
    public async Task RefreshAccountLabelHealsAStuckUnknownName()
    {
        _http.OnJson(HttpMethod.Post, "/users/get_current_account", """{"account_id":"dbid:1","name":{"display_name":"  Robert Rudolph "}}""");
        using var provider = Provider(displayName: "Unknown");

        var label = await provider.RefreshAccountLabelAsync();

        Assert.Equal("Robert Rudolph", label);
        Assert.Equal("Robert Rudolph", _store.Load<DropboxCredentials>(Key)!.DisplayName);
    }

    [Fact]
    public async Task RefreshAccountLabelKeepsTheNameWhenNothingChangedOrTheLookupFails()
    {
        var answer = """{"account_id":"dbid:1","name":{"display_name":"Robert"}}""";
        var status = HttpStatusCode.OK;
        _http.On(HttpMethod.Post, "/users/get_current_account", (_, _) => FakeHttpHandler.Json(answer, status));
        using var provider = Provider(displayName: "Robert");

        Assert.Null(await provider.RefreshAccountLabelAsync());

        answer = """{"account_id":"dbid:1","name":{"display_name":"Unknown"}}""";
        Assert.Null(await provider.RefreshAccountLabelAsync());

        status = HttpStatusCode.InternalServerError;
        Assert.Null(await provider.RefreshAccountLabelAsync());
        Assert.Equal("Robert", _store.Load<DropboxCredentials>(Key)!.DisplayName);
    }

    // MARK: - Helpers

    private DropboxProvider Provider(DateTimeOffset? expiresAt = null, string displayName = "Robert")
    {
        _store.Save(Key, new DropboxCredentials("access-1", "refresh-1", expiresAt ?? DateTimeOffset.UtcNow.AddHours(4), "dbid:1", displayName));
        return new DropboxProvider(_accountId, _store, _http);
    }

    private static Func<Func<string, Uri>, CancellationToken, Task<OAuthCallback>> Callback(string code)
        => (build, _) =>
        {
            var uri = build("http://localhost:4000");
            var state = HttpUtility.ParseQueryString(uri.Query)["state"];
            return Task.FromResult(new OAuthCallback("http://localhost:4000", new Uri($"http://localhost:4000/?code={code}&state={Uri.EscapeDataString(state!)}")));
        };

    private string WriteFile(string name, string contents)
    {
        var path = Path.Combine(_temp, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static JsonElement Json(string? body)
    {
        using var document = JsonDocument.Parse(body ?? "null");
        return document.RootElement.Clone();
    }
}
