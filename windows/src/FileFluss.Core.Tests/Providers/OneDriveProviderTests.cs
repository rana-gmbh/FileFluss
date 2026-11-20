// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text.Json;
using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.OneDrive;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

public sealed class OneDriveProviderTests : IDisposable
{
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private const string TokenUrl = "login.microsoftonline.com/common/oauth2/v2.0/token";

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();
    private readonly List<string> _tempFiles = [];

    private string Key => $"onedrive.{_accountId}";

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            File.Delete(file);
        }
    }

    // MARK: - Setup helpers

    private OneDriveProvider Provider(DateTimeOffset? expiresAt = null, string refreshToken = "refresh-1")
    {
        _store.Save(Key, new OneDriveCredentials("access-1", refreshToken, expiresAt ?? DateTimeOffset.UtcNow.AddHours(1), "Robert Rudolph"));
        return new OneDriveProvider(_accountId, _store, _http);
    }

    private OneDriveCredentials? Stored() => _store.Load<OneDriveCredentials>(Key);

    private string TempFile(int size)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ff-onedrive-{Guid.NewGuid():N}.bin");
        using (var stream = File.Create(path))
        {
            stream.SetLength(size);
        }

        File.SetLastWriteTimeUtc(path, new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc));
        _tempFiles.Add(path);
        return path;
    }

    private static string TokenJson(string access, string? refresh = "refresh-2")
        => JsonSerializer.Serialize(new { access_token = access, refresh_token = refresh, expires_in = 3600, token_type = "Bearer" });

    private static HttpMethod Patch => HttpMethod.Patch;

    // MARK: - Authentication state

    [Fact]
    public async Task WithoutStoredCredentialsEveryCallNeedsSignIn()
    {
        using var provider = new OneDriveProvider(_accountId, _store, _http);
        Assert.False(provider.IsAuthenticated);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
        Assert.Null(await provider.StorageQuotaAsync());
    }

    [Fact]
    public void RestoresStoredCredentialsWithoutNetwork()
    {
        using var provider = Provider();
        Assert.True(provider.IsAuthenticated);
        Assert.Equal("Robert Rudolph", provider.UserEmail);
        Assert.Empty(_http.Requests);
        Assert.True(provider.ConnectSpec.SupportsInlineReauthentication);
    }

    [Fact]
    public void IsRegisteredForItsType()
    {
        using var provider = CloudProviderRegistry.Create(CloudProviderType.OneDrive, _accountId, _store);
        Assert.IsType<OneDriveProvider>(provider);
    }

    // MARK: - Listing

    [Fact]
    public async Task ListsRootFollowingNextLinks()
    {
        _http.OnJson(HttpMethod.Get, "skiptoken=XYZ", """
            { "value": [ { "id": "C3", "name": ".hidden", "size": 1, "file": { "hashes": { "quickXorHash": "QX" } } } ] }
            """);
        _http.OnJson(HttpMethod.Get, "/me/drive/root/children", """
            {
              "value": [
                { "id": "F1", "name": "Photos", "size": 2048, "lastModifiedDateTime": "2026-01-02T03:04:05Z", "folder": { "childCount": 3 } },
                { "id": "A!1", "name": "report.pdf", "size": 1234, "lastModifiedDateTime": "2026-02-03T04:05:06.789Z",
                  "file": { "mimeType": "application/pdf", "hashes": { "sha1Hash": "SHA", "quickXorHash": "QX" } } }
              ],
              "@odata.nextLink": "https://graph.microsoft.com/v1.0/me/drive/root/children?$skiptoken=XYZ"
            }
            """);
        using var provider = Provider();

        var items = await provider.ListDirectoryAsync("/");

        Assert.Equal(3, items.Count);
        var folder = items[0];
        Assert.Equal("dF1", folder.Id);
        Assert.Equal("/Photos", folder.Path);
        Assert.True(folder.IsDirectory);
        Assert.Equal(2048, folder.Size);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), folder.ModificationDate);

        var file = items[1];
        Assert.Equal("fA!1", file.Id);
        Assert.False(file.IsDirectory);
        Assert.Equal(1234, file.Size);
        Assert.Equal(new DateTimeOffset(2026, 2, 3, 4, 5, 6, 789, TimeSpan.Zero), file.ModificationDate);
        Assert.Equal("SHA", file.Checksum);

        Assert.Equal("QX", items[2].Checksum);
        Assert.True(items[2].IsHidden);
        Assert.Equal(CloudFileItem.UnknownDate, items[2].ModificationDate);

        var first = _http.Requests.First();
        Assert.Equal($"{Graph}/me/drive/root/children?$top=1000&$orderby=name%20asc", first.Uri.AbsoluteUri);
        Assert.Equal("Bearer access-1", first.Headers["Authorization"]);
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task NestedPathsArePathAddressedAndEncoded()
    {
        _http.OnJson(HttpMethod.Get, "/children", """{ "value": [ { "id": "X", "name": "a.txt", "size": 5, "file": {} } ] }""");
        using var provider = Provider();

        var items = await provider.ListDirectoryAsync("/My Docs/a+b#1/");

        Assert.Equal("/My Docs/a+b#1/a.txt", items.Single().Path);
        Assert.Equal($"{Graph}/me/drive/root:/My%20Docs/a%2Bb%231:/children?$top=1000&$orderby=name%20asc", _http.Requests.Single().Uri.AbsoluteUri);
    }

    [Fact]
    public async Task NextLinkToAnotherHostIsNotFollowedWithTheToken()
    {
        _http.OnJson(HttpMethod.Get, "/me/drive/root/children", """
            { "value": [], "@odata.nextLink": "https://evil.example/steal" }
            """);
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.InvalidResponse, error.Kind);
        Assert.DoesNotContain(_http.Requests, r => r.Uri.Host == "evil.example");
    }

    [Fact]
    public async Task MetadataOfAFile()
    {
        _http.OnJson(HttpMethod.Get, "root:/Docs/a.txt:", """{ "id": "ID1", "name": "a.txt", "size": 42, "lastModifiedDateTime": "2026-05-06T07:08:09Z", "file": {} }""");
        using var provider = Provider();

        var item = await provider.GetFileMetadataAsync("/Docs/a.txt");

        Assert.Equal("/Docs/a.txt", item.Path);
        Assert.Equal(42, item.Size);
        Assert.Equal($"{Graph}/me/drive/root:/Docs/a.txt:", _http.Requests.Single().Uri.AbsoluteUri);
    }

    // MARK: - Errors

    [Fact]
    public async Task NotFoundCarriesThePath()
    {
        _http.OnJson(HttpMethod.Get, "root:/gone.txt:", """{ "error": { "code": "itemNotFound", "message": "Item not found" } }""", HttpStatusCode.NotFound);
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.GetFileMetadataAsync("/gone.txt"));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal("/gone.txt", error.ItemPath);
    }

    [Fact]
    public async Task OtherFailuresSurfaceGraphsOwnMessage()
    {
        _http.OnJson(HttpMethod.Delete, "root:/locked.docx:", """
            { "error": { "code": "accessDenied", "message": "The caller does not have permission.", "innerError": { "code": "resourceLocked" } } }
            """, HttpStatusCode.Forbidden);
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DeleteItemAsync("/locked.docx"));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("The caller does not have permission. (accessDenied / resourceLocked)", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, CloudErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InsufficientStorage, CloudErrorKind.QuotaExceeded)]
    [InlineData(HttpStatusCode.BadGateway, CloudErrorKind.ServerError)]
    [InlineData(HttpStatusCode.Forbidden, CloudErrorKind.Unauthorized)]
    public async Task StatusCodesWithoutAGraphBodyMapLikeMacOS(HttpStatusCode status, CloudErrorKind kind)
    {
        _http.OnText(HttpMethod.Get, "/children", string.Empty, status);
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(kind, error.Kind);
    }

    // MARK: - Token refresh

    [Fact]
    public async Task A401ForceRefreshesOnceAndPersistsTheRotatedTokens()
    {
        var calls = 0;
        _http.On(HttpMethod.Get, "/children", (request, _) =>
            Interlocked.Increment(ref calls) == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : FakeHttpHandler.Json("""{ "value": [] }"""));
        _http.OnJson(HttpMethod.Post, TokenUrl, TokenJson("access-2"));
        using var provider = Provider();

        await provider.ListDirectoryAsync("/");

        var lists = _http.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/children", StringComparison.Ordinal)).ToList();
        Assert.Equal(["Bearer access-1", "Bearer access-2"], lists.Select(r => r.Headers["Authorization"]));
        var refresh = HttpUtility.ParseQueryString(_http.Single(HttpMethod.Post, TokenUrl).Body!);
        Assert.Equal("refresh_token", refresh["grant_type"]);
        Assert.Equal("refresh-1", refresh["refresh_token"]);
        Assert.Equal("23eeca59-6b12-4ea7-8999-8d07e2af558e", refresh["client_id"]);
        Assert.Equal("access-2", Stored()!.AccessToken);
        Assert.Equal("refresh-2", Stored()!.RefreshToken);
        Assert.Equal("Robert Rudolph", Stored()!.UserEmail);
    }

    [Fact]
    public async Task A401AfterRefreshMeansSignInAgain()
    {
        _http.OnText(HttpMethod.Get, "/children", string.Empty, HttpStatusCode.Unauthorized);
        _http.OnJson(HttpMethod.Post, TokenUrl, TokenJson("access-2"));
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
        Assert.True(error.NeedsReauthentication);
    }

    [Fact]
    public async Task ARejectedRefreshTokenNeedsSignIn()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, """{ "error": "invalid_grant" }""", HttpStatusCode.BadRequest);
        using var provider = Provider(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
        Assert.Equal("refresh-1", Stored()!.RefreshToken);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneRefresh()
    {
        var refreshes = 0;
        _http.On(HttpMethod.Post, TokenUrl, (_, _) =>
        {
            Interlocked.Increment(ref refreshes);
            Thread.Sleep(150);
            return FakeHttpHandler.Json(TokenJson("access-2"));
        });
        _http.OnJson(HttpMethod.Get, "/children", """{ "value": [] }""");
        using var provider = Provider(expiresAt: DateTimeOffset.UtcNow.AddSeconds(30)); // inside the 60 s margin

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => provider.ListDirectoryAsync("/"))));

        Assert.Equal(1, refreshes);
        Assert.All(
            _http.Requests.Where(r => r.Method == HttpMethod.Get),
            r => Assert.Equal("Bearer access-2", r.Headers["Authorization"]));
        Assert.Equal("access-2", Stored()!.AccessToken);
    }

    [Fact]
    public async Task ARefreshThatKeepsTheRefreshTokenKeepsTheOldOne()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, TokenJson("access-2", refresh: null));
        _http.OnJson(HttpMethod.Get, "/children", """{ "value": [] }""");
        using var provider = Provider(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await provider.ListDirectoryAsync("/");

        Assert.Equal("refresh-1", Stored()!.RefreshToken);
        Assert.Equal("access-2", Stored()!.AccessToken);
    }

    // MARK: - Transfers

    [Fact]
    public async Task DownloadsTheContentEndpoint()
    {
        var bytes = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();
        _http.OnBytes(HttpMethod.Get, "root:/Docs/a b.bin:/content", bytes);
        using var provider = Provider();
        var target = Path.Combine(Path.GetTempPath(), $"ff-onedrive-dl-{Guid.NewGuid():N}.bin");
        _tempFiles.Add(target);
        long reported = 0;

        await provider.DownloadFileAsync("/Docs/a b.bin", target, delta => Interlocked.Add(ref reported, delta));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        Assert.Equal(bytes.Length, reported);
        Assert.Equal($"{Graph}/me/drive/root:/Docs/a%20b.bin:/content", _http.Requests.Single().Uri.AbsoluteUri);
    }

    [Fact]
    public async Task DownloadOfAMissingFileIsNotFound()
    {
        _http.OnJson(HttpMethod.Get, ":/content", """{ "error": { "code": "itemNotFound", "message": "nope" } }""", HttpStatusCode.NotFound);
        using var provider = Provider();
        var target = Path.Combine(Path.GetTempPath(), $"ff-onedrive-dl-{Guid.NewGuid():N}.bin");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DownloadFileAsync("/missing.bin", target));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task SmallFilesUseOnePutAndThenStampTimestamps()
    {
        var local = TempFile(1000);
        _http.OnJson(HttpMethod.Put, "root:/Docs/a.txt:/content", """{ "id": "N", "name": "a.txt" }""", HttpStatusCode.Created);
        _http.OnJson(Patch, "root:/Docs/a.txt:", """{ "id": "N", "name": "a.txt" }""");
        using var provider = Provider();
        long reported = 0;

        await provider.UploadFileAsync(local, "/Docs/a.txt", delta => Interlocked.Add(ref reported, delta));

        Assert.Equal(1000, reported);
        var put = _http.Single(HttpMethod.Put, ":/content");
        Assert.Equal("application/octet-stream", put.Headers["Content-Type"]);
        Assert.Equal("Bearer access-1", put.Headers["Authorization"]);
        var patch = _http.Single(Patch, "root:/Docs/a.txt:");
        using var json = JsonDocument.Parse(patch.Body!);
        var info = json.RootElement.GetProperty("fileSystemInfo");
        Assert.Equal("2025-06-07T08:09:10Z", info.GetProperty("lastModifiedDateTime").GetString());
        Assert.True(info.TryGetProperty("createdDateTime", out _));
    }

    [Fact]
    public async Task A401DuringASmallUploadRetriesWithoutDoubleCountingProgress()
    {
        var local = TempFile(2000);
        var puts = 0;
        _http.On(HttpMethod.Put, ":/content", (_, _) =>
            Interlocked.Increment(ref puts) == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : FakeHttpHandler.Json("{}", HttpStatusCode.Created));
        _http.OnJson(HttpMethod.Post, TokenUrl, TokenJson("access-2"));
        _http.OnJson(Patch, "root:/a.txt:", "{}");
        using var provider = Provider();
        long reported = 0;

        await provider.UploadFileAsync(local, "/a.txt", delta => Interlocked.Add(ref reported, delta));

        Assert.Equal(2, puts);
        Assert.Equal(2000, reported);
    }

    [Fact]
    public async Task TimestampFailuresDoNotFailTheUpload()
    {
        var local = TempFile(10);
        _http.OnJson(HttpMethod.Put, ":/content", "{}", HttpStatusCode.Created);
        _http.OnText(Patch, "root:/a.txt:", "boom", HttpStatusCode.BadRequest);
        using var provider = Provider();

        await provider.UploadFileAsync(local, "/a.txt");
    }

    [Fact]
    public async Task LargeFilesGoThroughAnUploadSessionIn320KiBMultiples()
    {
        Assert.Equal(0, OneDriveApiClient.UploadChunkSize % (320 * 1024));
        var size = OneDriveApiClient.UploadChunkSize + 12_345;
        var local = TempFile(size);
        _http.OnJson(HttpMethod.Post, "root:/Videos/big.mov:/createUploadSession", """{ "uploadUrl": "https://upload.onedrive.test/up/abc?x=1" }""");
        _http.On(HttpMethod.Put, "upload.onedrive.test", (request, _) =>
            request.Content!.Headers.ContentRange!.To == size - 1
                ? FakeHttpHandler.Json("""{ "id": "N", "name": "big.mov" }""", HttpStatusCode.Created)
                : FakeHttpHandler.Json("""{ "nextExpectedRanges": [] }""", HttpStatusCode.Accepted));
        _http.OnJson(Patch, "root:/Videos/big.mov:", "{}");
        using var provider = Provider();
        long reported = 0;

        await provider.UploadFileAsync(local, "/Videos/big.mov", delta => Interlocked.Add(ref reported, delta));

        Assert.Equal(size, reported);
        var session = _http.Single(HttpMethod.Post, "createUploadSession");
        Assert.Equal("""{"item":{"@microsoft.graph.conflictBehavior":"replace"}}""", session.Body);
        var chunks = _http.Requests.Where(r => r.Method == HttpMethod.Put).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal($"bytes 0-{OneDriveApiClient.UploadChunkSize - 1}/{size}", chunks[0].Headers["Content-Range"]);
        Assert.Equal(OneDriveApiClient.UploadChunkSize, chunks[0].Body!.Length); // the file is zeros: one char per byte
        Assert.Equal(12_345, chunks[1].Body!.Length);
        Assert.Equal($"bytes {OneDriveApiClient.UploadChunkSize}-{size - 1}/{size}", chunks[1].Headers["Content-Range"]);
        Assert.All(chunks, c => Assert.False(c.Headers.ContainsKey("Authorization")));
        Assert.Contains(_http.Requests, r => r.Method == Patch);
    }

    [Fact]
    public async Task AFailedChunkFailsTheUpload()
    {
        var local = TempFile((int)OneDriveApiClient.SimpleUploadLimit + 1);
        _http.OnJson(HttpMethod.Post, "createUploadSession", """{ "uploadUrl": "https://upload.onedrive.test/up/abc" }""");
        _http.OnText(HttpMethod.Put, "upload.onedrive.test", string.Empty, HttpStatusCode.InsufficientStorage);
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(local, "/big.bin"));

        Assert.Equal(CloudErrorKind.QuotaExceeded, error.Kind);
        Assert.DoesNotContain(_http.Requests, r => r.Method == Patch);
    }

    // MARK: - Changes

    [Fact]
    public async Task CreatesAMissingFolderUnderItsParent()
    {
        _http.OnJson(HttpMethod.Get, "root:/Docs/New:", """{ "error": { "code": "itemNotFound" } }""", HttpStatusCode.NotFound);
        _http.OnJson(HttpMethod.Post, "root:/Docs:/children", """{ "id": "N", "name": "New", "folder": {} }""", HttpStatusCode.Created);
        using var provider = Provider();

        await provider.CreateDirectoryAsync("/Docs/New");

        var post = _http.Single(HttpMethod.Post, "/children");
        using var json = JsonDocument.Parse(post.Body!);
        Assert.Equal("New", json.RootElement.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("folder").ValueKind);
        Assert.Equal("fail", json.RootElement.GetProperty("@microsoft.graph.conflictBehavior").GetString());
    }

    [Fact]
    public async Task CreatingAnExistingFolderSucceedsQuietly()
    {
        _http.OnJson(HttpMethod.Get, "root:/Docs:", """{ "id": "D", "name": "Docs", "folder": {} }""");
        using var provider = Provider();

        await provider.CreateDirectoryAsync("/Docs");

        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task CreatingAFolderThatAppearedMeanwhileSucceeds()
    {
        _http.OnJson(HttpMethod.Get, "root:/Raced:", "{}", HttpStatusCode.NotFound);
        _http.OnJson(HttpMethod.Post, "/me/drive/root/children", """{ "error": { "code": "nameAlreadyExists" } }""", HttpStatusCode.Conflict);
        using var provider = Provider();

        await provider.CreateDirectoryAsync("/Raced");
    }

    [Fact]
    public async Task RenamesWithAPatch()
    {
        _http.OnJson(Patch, "root:/Docs/old.txt:", """{ "id": "N", "name": "new.txt" }""");
        using var provider = Provider();

        await provider.RenameItemAsync("/Docs/old.txt", "new.txt");

        Assert.Equal("""{"name":"new.txt"}""", _http.Single(Patch, "old.txt").Body);
    }

    [Fact]
    public async Task SetsTheModificationDateThroughFileSystemInfo()
    {
        _http.OnJson(Patch, "root:/a.txt:", """{ "id": "N", "name": "a.txt" }""");
        using var provider = Provider();

        await provider.SetModificationDateAsync("/a.txt", new DateTimeOffset(2026, 3, 4, 7, 6, 7, 890, TimeSpan.FromHours(2)));

        Assert.Equal("""{"fileSystemInfo":{"lastModifiedDateTime":"2026-03-04T05:06:07.890Z"}}""", _http.Single(Patch, "a.txt").Body);
    }

    [Fact]
    public async Task MoveAndCopyFallBackToTransfersLikeOnMacOS()
    {
        using var provider = Provider();

        Assert.Equal(CloudErrorKind.NotImplemented, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.MoveItemAsync("/a", "/b"))).Kind);
        Assert.Equal(CloudErrorKind.NotImplemented, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/a", "/b"))).Kind);
    }

    [Fact]
    public async Task FolderSizeUsesTheItemSizeAndWalksOnlyWhenItIsZero()
    {
        _http.OnJson(HttpMethod.Get, "root:/Big:", """{ "id": "B", "name": "Big", "size": 99999, "folder": {} }""");
        _http.OnJson(HttpMethod.Get, "root:/Empty:/children", """{ "value": [ { "id": "f", "name": "x", "size": 7, "file": {} } ] }""");
        _http.OnJson(HttpMethod.Get, "root:/Empty:", """{ "id": "E", "name": "Empty", "size": 0, "folder": {} }""");
        using var provider = Provider();

        Assert.Equal(99999, await provider.FolderSizeAsync("/Big"));
        Assert.Equal(7, await provider.FolderSizeAsync("/Empty"));
    }

    // MARK: - Search and quota

    [Fact]
    public async Task SearchEscapesTheQueryAndPlacesHitsInTheirFolders()
    {
        _http.OnJson(HttpMethod.Get, "/search(", """
            {
              "value": [
                { "id": "1", "name": "it's.txt", "size": 3, "file": {}, "parentReference": { "path": "/drive/root:/Work/Q%201" } },
                { "id": "2", "name": "top.txt", "size": 4, "file": {}, "parentReference": { "path": "/drive/root:" } },
                { "id": "3", "name": "lost.txt", "size": 5, "file": {} }
              ]
            }
            """);
        using var provider = Provider();

        var hits = await provider.SearchFilesAsync("it's", null);

        Assert.Equal(["/Work/Q 1/it's.txt", "/top.txt", "/lost.txt"], hits!.Select(h => h.Path));
        Assert.Equal($"{Graph}/me/drive/root/search(q='it%27%27s')?$top=100", _http.Requests.Single().Uri.AbsoluteUri);
    }

    [Fact]
    public async Task SearchWithinAFolderIsPathAddressed()
    {
        _http.OnJson(HttpMethod.Get, "/search(", """{ "value": [] }""");
        using var provider = Provider();

        await provider.SearchFilesAsync("x", "/Work");

        Assert.Equal($"{Graph}/me/drive/root:/Work:/search(q='x')?$top=100", _http.Requests.Single().Uri.AbsoluteUri);
    }

    [Fact]
    public async Task QuotaTreatsZeroTotalAsUnlimited()
    {
        _http.OnJson(HttpMethod.Get, "/me/drive?$select=quota", """{ "quota": { "total": 0, "used": 500 } }""");
        using var provider = Provider();

        var quota = await provider.StorageQuotaAsync();

        Assert.Equal(500, quota!.UsedBytes);
        Assert.Null(quota.TotalBytes);
    }

    [Fact]
    public async Task QuotaParsesTotalAndUsed()
    {
        _http.OnJson(HttpMethod.Get, "/me/drive?$select=quota", """{ "quota": { "total": 1099511627776, "used": 1073741824, "remaining": 1 } }""");
        using var provider = Provider();

        var quota = await provider.StorageQuotaAsync();

        Assert.Equal(1_073_741_824, quota!.UsedBytes);
        Assert.Equal(1_099_511_627_776, quota.TotalBytes);
        Assert.Equal(1_099_511_627_776 - 1_073_741_824, quota.FreeBytes);
    }

    [Fact]
    public async Task QuotaIsUnknownWhenGraphRefuses()
    {
        _http.OnText(HttpMethod.Get, "/me/drive?$select=quota", "no", HttpStatusCode.Forbidden);
        using var provider = Provider();

        Assert.Null(await provider.StorageQuotaAsync());
    }

    // MARK: - Share links

    private void ItemIdRoute(string path = "root:/Docs/a.pdf:", string id = "ITEM!7")
        => _http.OnJson(HttpMethod.Get, path, $$"""{ "id": "{{id}}", "name": "a.pdf", "file": {} }""");

    [Fact]
    public async Task CreatesAnAnonymousViewLinkById()
    {
        _http.OnJson(HttpMethod.Post, "/items/ITEM!7/createLink", """
            { "id": "P1", "hasPassword": true, "expirationDateTime": "2026-12-31T23:59:59Z",
              "link": { "type": "view", "scope": "anonymous", "webUrl": "https://1drv.ms/b/s!abc" } }
            """, HttpStatusCode.Created);
        ItemIdRoute();
        using var provider = Provider();

        var link = await provider.CreateShareLinkAsync("/Docs/a.pdf", new ShareLinkOptions
        {
            Password = "pw",
            Expiry = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero),
        });

        Assert.Equal(new Uri("https://1drv.ms/b/s!abc"), link.Url);
        Assert.Null(link.DirectDownloadUrl);
        Assert.True(link.IsPublic);
        Assert.True(link.HasPassword);
        Assert.Null(link.Note);
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero), link.ExpiresAt);
        var post = _http.Single(HttpMethod.Post, "createLink");
        Assert.Equal($"{Graph}/me/drive/items/ITEM!7/createLink", post.Uri.AbsoluteUri);
        Assert.Equal("""{"type":"view","scope":"anonymous","password":"pw","expirationDateTime":"2026-12-31T23:59:59Z"}""", post.Body);
    }

    [Fact]
    public async Task AnOrganisationOnlyLinkIsReportedAsNotPublic()
    {
        _http.OnJson(HttpMethod.Post, "createLink", """
            { "id": "P1", "link": { "type": "view", "scope": "organization", "webUrl": "https://contoso.sharepoint.com/:b:/x" } }
            """);
        ItemIdRoute();
        using var provider = Provider();

        var link = await provider.CreateShareLinkAsync("/Docs/a.pdf", ShareLinkOptions.Default);

        Assert.False(link.IsPublic);
        Assert.Contains("view/organization", link.Note);
        Assert.False(link.HasPassword);
        Assert.Equal("""{"type":"view","scope":"anonymous"}""", _http.Single(HttpMethod.Post, "createLink").Body);
    }

    [Fact]
    public async Task ARefusedLinkSurfacesGraphsMessage()
    {
        _http.OnJson(HttpMethod.Post, "createLink", """
            { "error": { "code": "invalidRequest", "message": "Setting a password requires a Microsoft 365 subscription." } }
            """, HttpStatusCode.BadRequest);
        ItemIdRoute();
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/Docs/a.pdf", new ShareLinkOptions { Password = "x" }));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.StartsWith("Setting a password requires a Microsoft 365 subscription.", error.Message);
    }

    [Fact]
    public async Task ABareForbiddenOnCreateIsNotASignInProblem()
    {
        _http.OnText(HttpMethod.Post, "createLink", string.Empty, HttpStatusCode.Forbidden);
        ItemIdRoute();
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/Docs/a.pdf", ShareLinkOptions.Default));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.False(error.NeedsReauthentication);
    }

    [Fact]
    public async Task ExistingLinkPrefersTheAnonymousPermission()
    {
        _http.OnJson(HttpMethod.Get, "/items/ITEM!7/permissions", """
            { "value": [
                { "id": "named", "roles": ["write"], "grantedTo": { "user": { "displayName": "Ann" } } },
                { "id": "org", "link": { "type": "view", "scope": "organization", "webUrl": "https://x/org" } },
                { "id": "anon", "link": { "type": "view", "scope": "anonymous", "webUrl": "https://x/anon" } }
            ] }
            """);
        ItemIdRoute();
        using var provider = Provider();

        var link = await provider.ExistingShareLinkAsync("/Docs/a.pdf");

        Assert.Equal(new Uri("https://x/anon"), link!.Url);
        Assert.True(link.IsPublic);
    }

    [Fact]
    public async Task ExistingLinkIsNullWhenUnsharedOrWhenTheAccountWontSay()
    {
        _http.OnJson(HttpMethod.Get, "/items/ITEM!7/permissions", """{ "value": [] }""");
        _http.OnJson(HttpMethod.Get, "/items/DENIED/permissions", """{ "error": { "code": "accessDenied", "message": "Access denied" } }""", HttpStatusCode.Forbidden);
        ItemIdRoute();
        ItemIdRoute("root:/Docs/b.pdf:", "DENIED");
        using var provider = Provider();

        Assert.Null(await provider.ExistingShareLinkAsync("/Docs/a.pdf"));
        Assert.Null(await provider.ExistingShareLinkAsync("/Docs/b.pdf"));
    }

    [Fact]
    public async Task UpdatingRevokesTheOldPermissionAndSaysTheUrlChanged()
    {
        _http.OnJson(HttpMethod.Get, "/items/ITEM!7/permissions", """
            { "value": [ { "id": "aTowIy5m=", "link": { "type": "view", "scope": "anonymous", "webUrl": "https://x/old" } } ] }
            """);
        _http.OnText(HttpMethod.Delete, "/items/ITEM!7/permissions/", string.Empty, HttpStatusCode.NoContent);
        _http.OnJson(HttpMethod.Post, "createLink", """{ "id": "new", "link": { "type": "view", "scope": "anonymous", "webUrl": "https://x/new" } }""");
        ItemIdRoute();
        using var provider = Provider();

        var link = await provider.UpdateShareLinkAsync("/Docs/a.pdf", ShareLinkOptions.Default);

        Assert.Equal(new Uri("https://x/new"), link.Url);
        Assert.False(string.IsNullOrEmpty(link.Note));
        var methods = _http.Requests.Where(r => r.Uri.AbsolutePath.Contains("/items/", StringComparison.Ordinal)).Select(r => r.Method.Method).ToList();
        Assert.Equal(["GET", "DELETE", "POST"], methods);
        Assert.EndsWith("/permissions/aTowIy5m=", _http.Single(HttpMethod.Delete, "/permissions/").Uri.AbsoluteUri);
    }

    [Fact]
    public async Task RemovingWithoutALinkSaysSo()
    {
        _http.OnJson(HttpMethod.Get, "/items/ITEM!7/permissions", """{ "value": [ { "id": "named" } ] }""");
        ItemIdRoute();
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.RemoveShareLinkAsync("/Docs/a.pdf"));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task RemovingWhenTheAccountRefusesExplainsWhatToDo()
    {
        _http.OnJson(HttpMethod.Get, "/permissions", """{ "error": { "code": "accessDenied", "message": "Access denied" } }""", HttpStatusCode.Forbidden);
        ItemIdRoute();
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.RemoveShareLinkAsync("/Docs/a.pdf"));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.DoesNotContain("Access denied", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareCapabilitiesMatchMacOS()
    {
        using var provider = Provider();
        var caps = provider.ShareLinkCapabilities;
        Assert.True(caps.CanCreate && caps.CanQueryExisting && caps.CanUpdate && caps.CanRemove);
        Assert.True(caps.SupportsPassword && caps.SupportsExpiry && caps.PasswordRequiresPaidPlan && caps.ExpiryRequiresPaidPlan);
        Assert.False(caps.SupportsDownloadToggle);
    }

    // MARK: - Connect

    private static Func<Func<string, Uri>, CancellationToken, Task<OAuthCallback>> Browser(Action<Uri> sawAuthorizeUrl, Func<string?, string>? query = null)
        => (authorizeUrl, _) =>
        {
            const string redirect = "http://localhost:53682";
            var url = authorizeUrl(redirect);
            sawAuthorizeUrl(url);
            var state = HttpUtility.ParseQueryString(url.Query)["state"];
            var callbackQuery = query?.Invoke(state) ?? $"code=CODE123&state={state}";
            return Task.FromResult(new OAuthCallback(redirect, new Uri($"{redirect}/?{callbackQuery}")));
        };

    [Fact]
    public async Task ConnectRunsPkceLoopbackSignInAndStoresTokens()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, TokenJson("fresh-access", "fresh-refresh"));
        _http.OnJson(HttpMethod.Get, "/v1.0/me", """{ "displayName": null, "mail": "robert@example.com", "userPrincipalName": "upn@example.com" }""");
        using var provider = new OneDriveProvider(_accountId, _store, _http);
        Uri? authorize = null;
        provider.BrowserSignIn = Browser(url => authorize = url);
        var prompt = new ScriptedConnectPrompt();

        var result = await provider.ConnectAsync(ConnectValues.Empty("browser"), prompt);

        Assert.Equal("robert@example.com", result.AccountLabel);
        Assert.True(provider.IsAuthenticated);
        Assert.Equal("fresh-access", Stored()!.AccessToken);
        Assert.Equal("fresh-refresh", Stored()!.RefreshToken);
        Assert.Equal("robert@example.com", Stored()!.UserEmail);
        Assert.Null(prompt.StatusLines.Last());
        Assert.NotNull(prompt.StatusLines.First());

        Assert.Equal("login.microsoftonline.com", authorize!.Host);
        Assert.Equal("/common/oauth2/v2.0/authorize", authorize.AbsolutePath);
        var query = HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal("23eeca59-6b12-4ea7-8999-8d07e2af558e", query["client_id"]);
        Assert.Equal("http://localhost:53682", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("query", query["response_mode"]);
        Assert.Equal("https://graph.microsoft.com/Files.ReadWrite.All https://graph.microsoft.com/User.Read offline_access", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("select_account", query["prompt"]);

        var exchange = HttpUtility.ParseQueryString(_http.Single(HttpMethod.Post, TokenUrl).Body!);
        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal("CODE123", exchange["code"]);
        Assert.Equal("http://localhost:53682", exchange["redirect_uri"]);
        Assert.Equal(query["code_challenge"], Pkce.Challenge(exchange["code_verifier"]!));
        Assert.Equal("Bearer fresh-access", _http.Single(HttpMethod.Get, "/v1.0/me").Headers["Authorization"]);
    }

    [Fact]
    public async Task ConnectNamesTheAccountUnknownWhenMeRefuses()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, TokenJson("fresh-access"));
        _http.OnText(HttpMethod.Get, "/v1.0/me", string.Empty, HttpStatusCode.Forbidden);
        using var provider = new OneDriveProvider(_accountId, _store, _http);
        provider.BrowserSignIn = Browser(_ => { });

        var result = await provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt());

        Assert.Equal("Unknown", result.AccountLabel);
        Assert.Equal("OneDrive", CloudAccount.ComposeDisplayName(CloudProviderType.OneDrive, result.AccountLabel));
    }

    [Fact]
    public async Task AFailedTokenExchangeLeavesStoredCredentialsUntouched()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, """{ "error": "invalid_grant" }""", HttpStatusCode.BadRequest);
        using var provider = Provider();
        provider.BrowserSignIn = Browser(_ => { });

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal("access-1", Stored()!.AccessToken);
        Assert.Equal("refresh-1", Stored()!.RefreshToken);
    }

    [Fact]
    public async Task ACallbackForAnotherSignInIsRejected()
    {
        using var provider = Provider();
        provider.BrowserSignIn = Browser(_ => { }, _ => "code=STOLEN&state=forged");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
        Assert.Empty(_http.Requests);
        Assert.Equal("access-1", Stored()!.AccessToken);
    }

    [Fact]
    public async Task ADeniedConsentIsRejected()
    {
        using var provider = new OneDriveProvider(_accountId, _store, _http);
        provider.BrowserSignIn = Browser(_ => { }, state => $"error=access_denied&state={state}");

        await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt()));

        Assert.Null(Stored());
        Assert.False(provider.IsAuthenticated);
    }

    [Fact]
    public async Task DisconnectForgetsTheTokens()
    {
        using var provider = Provider();

        await provider.DisconnectAsync();

        Assert.False(provider.IsAuthenticated);
        Assert.Null(Stored());
    }

    [Fact]
    public async Task ARefreshFinishingAfterANewSignInDoesNotOverwriteIt()
    {
        using var refreshArrived = new ManualResetEventSlim();
        using var releaseRefresh = new ManualResetEventSlim();
        _http.On(HttpMethod.Post, TokenUrl, (_, body) =>
        {
            if (body!.Contains("grant_type=refresh_token", StringComparison.Ordinal))
            {
                refreshArrived.Set();
                releaseRefresh.Wait(TimeSpan.FromSeconds(10));
                return FakeHttpHandler.Json(TokenJson("stale-session"));
            }

            return FakeHttpHandler.Json(TokenJson("fresh-access", "fresh-refresh"));
        });
        _http.OnJson(HttpMethod.Get, "/v1.0/me", """{ "displayName": "Robert" }""");
        _http.OnJson(HttpMethod.Get, "/children", """{ "value": [] }""");
        using var provider = Provider(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        provider.BrowserSignIn = Browser(_ => { });

        var listing = Task.Run(() => provider.ListDirectoryAsync("/"));
        Assert.True(refreshArrived.Wait(TimeSpan.FromSeconds(10)));
        await provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt());
        releaseRefresh.Set();
        await listing;

        Assert.Equal("fresh-access", Stored()!.AccessToken);
    }
}
