// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Specialized;
using System.Net;
using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.GoogleDrive;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

public sealed class GoogleDrivePickerProviderTests : IDisposable
{
    private const string PickerClientId = "368567371288-hvv93hksp81tblrrk4ra9vi5svi71dv2.apps.googleusercontent.com";

    private const string PhotosChildren = """
        {"files":[
          {"id":"y2024","name":"2024","mimeType":"application/vnd.google-apps.folder","modifiedTime":"2024-02-02T00:00:00Z"},
          {"id":"a1","name":"a.jpg","mimeType":"image/jpeg","size":"2048","modifiedTime":"2024-02-03T04:05:06.789Z","md5Checksum":"md5a"},
          {"id":"doc7","name":"Notes","mimeType":"application/vnd.google-apps.document"}
        ]}
        """;

    private readonly MemoryCredentialStore _store = new();
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly FakeHttpHandler _http = new();
    private readonly string _temp = Directory.CreateTempSubdirectory("ff-gpicker-").FullName;

    private string Key => $"googleDrivePicker.{_accountId}";

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

    private GoogleDrivePickerProvider Provider(DateTimeOffset? expiresAt = null, bool stored = true)
    {
        if (stored)
        {
            _store.Save(Key, new GoogleDrivePickerCredentials(
                "access-1",
                "refresh-1",
                expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
                "me@example.com",
                "Me Myself",
                [new GoogleDrivePickedRoot("r2", "Zeta"), new GoogleDrivePickedRoot("r1", "Photos")]));
        }

        return new GoogleDrivePickerProvider(_accountId, _store, _http);
    }

    private static NameValueCollection Query(Uri uri) => HttpUtility.ParseQueryString(uri.Query);

    private static string? Q(HttpRequestMessage request) => Query(request.RequestUri!)["q"];

    /// <summary>Children listings by parent id: r1 → Photos' children, y2024 → one file, anything else empty.</summary>
    private void Listings() => _http.On(HttpMethod.Get, "googleapis.com/drive/v3/files?", (request, _) => Q(request) switch
    {
        "'r1' in parents and trashed = false" => FakeHttpHandler.Json(PhotosChildren),
        "'y2024' in parents and trashed = false" => FakeHttpHandler.Json("""{"files":[{"id":"p1","name":"p1.jpg","mimeType":"image/jpeg","size":"7"}]}"""),
        _ => FakeHttpHandler.Json("""{"files":[]}"""),
    });

    // MARK: - Listing

    [Fact]
    public async Task RootListsThePickedFoldersByStoredNameWithLiveDates()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files/r1?", """{"id":"r1","name":"Photos (renamed on the web)","mimeType":"application/vnd.google-apps.folder","modifiedTime":"2024-01-01T00:00:00Z"}""");
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files/r2?", """{"error":{"message":"File not found"}}""", HttpStatusCode.NotFound);

        var items = await provider.ListDirectoryAsync("/");

        Assert.Equal(new[] { "/Photos", "/Zeta" }, items.Select(i => i.Path));
        Assert.Equal("d:r1", items[0].Id);
        Assert.Equal("Photos", items[0].Name);
        Assert.True(items[0].IsDirectory);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), items[0].ModificationDate);
        Assert.Equal(CloudFileItem.UnknownDate, items[1].ModificationDate);

        var fetch = Query(_http.Single(HttpMethod.Get, "/files/r1?").Uri);
        Assert.Equal("id,name,mimeType,size,modifiedTime,md5Checksum", fetch["fields"]);
        Assert.Equal("true", fetch["supportsAllDrives"]);
    }

    [Fact]
    public async Task FoldersAreListedLiveOnEveryCall()
    {
        var provider = Provider();
        Listings();

        var first = await provider.ListDirectoryAsync("/Photos");
        var nested = await provider.ListDirectoryAsync("/Photos/2024");
        await provider.ListDirectoryAsync("/Photos/2024");

        Assert.Equal(new[] { "/Photos/2024", "/Photos/a.jpg", "/Photos/Notes" }, first.Select(i => i.Path));
        Assert.Equal("dy2024", first[0].Id);
        Assert.Equal("fa1", first[1].Id);
        Assert.Equal(2048, first[1].Size);
        Assert.Equal(new DateTimeOffset(2024, 2, 3, 4, 5, 6, 789, TimeSpan.Zero), first[1].ModificationDate);
        Assert.Equal("md5a", first[1].Checksum);
        Assert.Equal("/Photos/2024/p1.jpg", Assert.Single(nested).Path);

        // No cache: each nested listing re-lists the root's children to find "2024".
        var queries = _http.Requests.Select(r => Query(r.Uri)["q"]).ToList();
        Assert.Equal(3, queries.Count(q => q == "'r1' in parents and trashed = false"));
        Assert.Equal(2, queries.Count(q => q == "'y2024' in parents and trashed = false"));

        var listing = Query(_http.Requests.First().Uri);
        Assert.Equal("nextPageToken,files(id,name,mimeType,size,modifiedTime,md5Checksum)", listing["fields"]);
        Assert.Equal("1000", listing["pageSize"]);
        Assert.Equal("folder,name", listing["orderBy"]);
        Assert.Equal("true", listing["supportsAllDrives"]);
        Assert.Equal("true", listing["includeItemsFromAllDrives"]);
        Assert.Equal("Bearer access-1", _http.Requests.First().Headers["Authorization"]);
    }

    [Fact]
    public async Task UnknownRootsAndNamesAreNotFound()
    {
        var provider = Provider();
        Listings();

        var unknownRoot = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/Elsewhere"));
        var unknownChild = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/Photos/nope"));

        Assert.Equal(CloudErrorKind.NotFound, unknownRoot.Kind);
        Assert.Equal("/Elsewhere", unknownRoot.ItemPath);
        Assert.Equal("/Photos/nope", unknownChild.ItemPath);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, CloudErrorKind.NotAuthenticated)]
    [InlineData(HttpStatusCode.NotFound, CloudErrorKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, CloudErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, CloudErrorKind.ServerError)]
    public async Task ErrorStatusesMapAsOnMacOS(HttpStatusCode status, CloudErrorKind expected)
    {
        var provider = Provider();
        _http.On(HttpMethod.Get, "googleapis.com/drive/v3/files?", (_, _) => new HttpResponseMessage(status));

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/Photos"));

        Assert.Equal(expected, error.Kind);
    }

    [Fact]
    public async Task UnauthorizedRefreshesOnceAndRetries()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Post, "oauth2.googleapis.com/token", """{"access_token":"access-2","expires_in":3600}""");
        _http.On(HttpMethod.Get, "googleapis.com/drive/v3/files?", (request, _) => request.Headers.Authorization!.Parameter == "access-2"
            ? FakeHttpHandler.Json(PhotosChildren)
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var items = await provider.ListDirectoryAsync("/Photos");

        Assert.Equal(3, items.Count);
        Assert.Equal(PickerClientId, HttpUtility.ParseQueryString(_http.Single(HttpMethod.Post, "/token").Body!)["client_id"]);
    }

    [Fact]
    public async Task ExpiredTokenRefreshesOnceForConcurrentCallersAndKeepsTheRoots()
    {
        var provider = Provider(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var refreshes = 0;
        _http.On(HttpMethod.Post, "oauth2.googleapis.com/token", (_, _) =>
        {
            Interlocked.Increment(ref refreshes);
            Thread.Sleep(150);
            return FakeHttpHandler.Json("""{"access_token":"access-2","refresh_token":"refresh-2","expires_in":3600}""");
        });
        Listings();

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => provider.ListDirectoryAsync("/Photos"))));

        Assert.Equal(1, refreshes);
        var form = HttpUtility.ParseQueryString(_http.Single(HttpMethod.Post, "/token").Body!);
        Assert.Equal(PickerClientId, form["client_id"]);
        Assert.Equal("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", form["client_secret"]);
        Assert.Equal("refresh-1", form["refresh_token"]);
        Assert.Equal("refresh_token", form["grant_type"]);

        var stored = _store.Load<GoogleDrivePickerCredentials>(Key)!;
        Assert.Equal("access-2", stored.AccessToken);
        Assert.Equal("refresh-2", stored.RefreshToken);
        Assert.Equal(new[] { "Zeta", "Photos" }, stored.Roots.Select(r => r.Name));
    }

    // MARK: - Download / upload

    [Fact]
    public async Task WorkspaceDocumentsExportAsPdfHere()
    {
        var provider = Provider();
        Listings();
        _http.OnBytes(HttpMethod.Get, "/files/doc7/export", "%PDF"u8.ToArray());
        var local = Path.Combine(_temp, "Notes");
        long bytes = 0;

        await provider.DownloadFileAsync("/Photos/Notes", local, delta => bytes += delta);

        Assert.Equal("%PDF", await File.ReadAllTextAsync(local + ".pdf"));
        Assert.Equal(4, bytes);
        var export = Query(_http.Single(HttpMethod.Get, "/export").Uri);
        Assert.Equal("application/pdf", export["mimeType"]);
        Assert.Null(export["supportsAllDrives"]);
    }

    [Fact]
    public async Task RegularFilesDownloadWithAltMedia()
    {
        var provider = Provider();
        Listings();
        _http.OnBytes(HttpMethod.Get, "/files/a1?alt=media", [1, 2, 3]);
        var local = Path.Combine(_temp, "a.jpg");

        await provider.DownloadFileAsync("/Photos/a.jpg", local);

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(local));
    }

    [Fact]
    public async Task UploadLooksUpTheNameCreatesAndAlwaysStampsTheDates()
    {
        var provider = Provider();
        var local = Path.Combine(_temp, "x.txt");
        await File.WriteAllTextAsync(local, "12345");
        File.SetLastWriteTimeUtc(local, new DateTime(2021, 12, 31, 23, 59, 58, DateTimeKind.Utc));
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", """{"files":[]}""");
        _http.OnJson(HttpMethod.Post, "googleapis.com/upload/drive/v3/files?", """{"id":"x9","name":"x.txt","mimeType":"text/plain"}""");
        _http.OnJson(HttpMethod.Patch, "googleapis.com/drive/v3/files/x9?", "{}");
        long bytes = 0;

        await provider.UploadFileAsync(local, "/Photos/x.txt", delta => bytes += delta);

        Assert.Equal(5, bytes);
        var find = Query(_http.Single(HttpMethod.Get, "/files?").Uri);
        Assert.Equal("'r1' in parents and name = 'x.txt' and trashed = false", find["q"]);
        Assert.Equal("files(id)", find["fields"]);
        Assert.Equal("1", find["pageSize"]);
        Assert.Equal("true", find["supportsAllDrives"]);
        Assert.Null(find["includeItemsFromAllDrives"]);

        var create = _http.Single(HttpMethod.Post, "upload/drive/v3/files?");
        Assert.Equal("multipart", Query(create.Uri)["uploadType"]);
        Assert.Contains("\"parents\":[\"r1\"]", create.Body);
        Assert.Contains("\"name\":\"x.txt\"", create.Body);

        var stamp = _http.Single(HttpMethod.Patch, "googleapis.com/drive/v3/files/x9?");
        Assert.Contains("\"modifiedTime\":\"2021-12-31T23:59:58.000Z\"", stamp.Body);
    }

    [Fact]
    public async Task UploadReplacesAnExistingFileByPatch()
    {
        var provider = Provider();
        var local = Path.Combine(_temp, "a.jpg");
        await File.WriteAllTextAsync(local, "new");
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", """{"files":[{"id":"a1"}]}""");
        _http.OnJson(HttpMethod.Patch, "googleapis.com/upload/drive/v3/files/a1?", """{"id":"a1","name":"a.jpg","mimeType":"image/jpeg"}""");
        _http.OnJson(HttpMethod.Patch, "googleapis.com/drive/v3/files/a1?", "{}");

        await provider.UploadFileAsync(local, "/Photos/a.jpg");

        Assert.Contains("\r\n\r\n{}\r\n--", _http.Single(HttpMethod.Patch, "upload/drive/v3/files/a1?").Body);
        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task NothingCanBeCreatedNextToThePickedRoots()
    {
        var provider = Provider();
        var local = Path.Combine(_temp, "x.txt");
        await File.WriteAllTextAsync(local, "x");

        Assert.Equal(CloudErrorKind.NotImplemented, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(local, "/x.txt"))).Kind);
        Assert.Equal(CloudErrorKind.NotImplemented, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateDirectoryAsync("/New"))).Kind);
        Assert.Empty(_http.Requests);
    }

    // MARK: - Mutations

    [Fact]
    public async Task CreateDirectoryPostsIntoTheParentAndIsIdempotent()
    {
        var provider = Provider();
        Listings();
        _http.OnJson(HttpMethod.Post, "googleapis.com/drive/v3/files", """{"id":"n1","name":"New","mimeType":"application/vnd.google-apps.folder"}""");

        await provider.CreateDirectoryAsync("/Photos/2024");
        await provider.CreateDirectoryAsync("/Photos");
        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Post);

        await provider.CreateDirectoryAsync("/Photos/New");

        var create = _http.Single(HttpMethod.Post, "/files");
        Assert.Equal("""{"name":"New","mimeType":"application/vnd.google-apps.folder","parents":["r1"]}""", create.Body);
        Assert.Equal(string.Empty, create.Uri.Query);
    }

    [Fact]
    public async Task MovePatchesParentsAndName()
    {
        var provider = Provider();
        Listings();
        _http.OnJson(HttpMethod.Patch, "googleapis.com/drive/v3/files/a1?", """{"id":"a1"}""");

        await provider.MoveItemAsync("/Photos/a.jpg", "/Zeta/b.jpg");

        var move = _http.Single(HttpMethod.Patch, "/files/a1?");
        var query = Query(move.Uri);
        Assert.Equal("r2", query["addParents"]);
        Assert.Equal("r1", query["removeParents"]);
        Assert.Equal("true", query["supportsAllDrives"]);
        Assert.Equal("id", query["fields"]);
        Assert.Equal("""{"name":"b.jpg"}""", move.Body);
    }

    [Fact]
    public async Task CopyPostsToTheCopyEndpointButNotForFolders()
    {
        var provider = Provider();
        Listings();
        _http.OnJson(HttpMethod.Post, "googleapis.com/drive/v3/files/a1/copy", """{"id":"c1","name":"c.jpg","mimeType":"image/jpeg"}""");

        await provider.CopyItemAsync("/Photos/a.jpg", "/Zeta/c.jpg");
        var folder = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/Photos/2024", "/Zeta/2024"));

        Assert.Equal("""{"name":"c.jpg","parents":["r2"]}""", _http.Single(HttpMethod.Post, "/copy").Body);
        Assert.Equal(CloudErrorKind.NotImplemented, folder.Kind);
    }

    [Fact]
    public async Task RenamingAndDeletingARootUpdateTheStoredRoots()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Patch, "googleapis.com/drive/v3/files/r1", """{"id":"r1","name":"Pictures","mimeType":"application/vnd.google-apps.folder"}""");
        _http.On(HttpMethod.Delete, "googleapis.com/drive/v3/files/r2", (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));

        await provider.RenameItemAsync("/Photos", "Pictures");
        await provider.DeleteItemAsync("/Zeta");

        Assert.Equal("""{"name":"Pictures"}""", _http.Single(HttpMethod.Patch, "/files/r1").Body);
        Assert.Equal("?supportsAllDrives=true", _http.Single(HttpMethod.Delete, "/files/r2").Uri.Query);
        var stored = _store.Load<GoogleDrivePickerCredentials>(Key)!;
        Assert.Equal(new[] { new GoogleDrivePickedRoot("r1", "Pictures") }, stored.Roots);
        Assert.Equal(new[] { new GoogleDrivePickedRoot("r1", "Pictures") }, provider.Roots);
    }

    [Fact]
    public async Task MetadataOfARootKeepsItsStoredName()
    {
        var provider = Provider();
        Listings();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files/r1?", """{"id":"r1","name":"Live","mimeType":"application/vnd.google-apps.folder","modifiedTime":"2024-01-01T00:00:00Z"}""");
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files/a1?", """{"id":"a1","name":"a.jpg","mimeType":"image/jpeg","size":"2048"}""");

        var root = await provider.GetFileMetadataAsync("/Photos");
        var file = await provider.GetFileMetadataAsync("/Photos/a.jpg");

        Assert.Equal("/Photos", root.Path);
        Assert.Equal("d:r1", root.Id);
        Assert.Equal("/Photos/a.jpg", file.Path);
        Assert.Equal(2048, file.Size);
    }

    [Fact]
    public async Task SetModificationDatePatchesModifiedTime()
    {
        var provider = Provider();
        Listings();
        _http.OnJson(HttpMethod.Patch, "googleapis.com/drive/v3/files/a1?", "{}");

        await provider.SetModificationDateAsync("/Photos/a.jpg", new DateTimeOffset(2020, 2, 29, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("""{"modifiedTime":"2020-02-29T12:00:00.000Z"}""", _http.Single(HttpMethod.Patch, "/files/a1").Body);
    }

    [Fact]
    public async Task QuotaAndCapabilities()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/about", """{"storageQuota":{"limit":"200","usage":"50"}}""");

        var quota = await provider.StorageQuotaAsync();

        Assert.Equal(50, quota!.UsedBytes);
        Assert.Equal(150, quota.FreeBytes);
        Assert.False(provider.ShareLinkCapabilities.CanCreate);
        Assert.Null(await provider.SearchFilesAsync("x", null));
    }

    // MARK: - Connecting

    [Fact]
    public void ConnectFormMatchesMacOS()
    {
        var add = Provider(stored: false).ConnectSpec;
        var edit = Provider().ConnectSpec;

        Assert.True(add.Modes.Single().OpensBrowser);
        Assert.Equal(new[] { ConnectFieldKind.Info, ConnectFieldKind.Info }, add.Modes.Single().Fields.Select(f => f.Kind));
        Assert.Equal("For new Google Drive accounts — no user limit", add.Modes.Single().Fields[0].Label);

        // Edit has no Re-authorize on macOS: remove and add the account to change folders.
        Assert.False(edit.SupportsInlineReauthentication);
        Assert.False(edit.Modes.Single().OpensBrowser);
        Assert.StartsWith("To change which folders FileFluss can access", Assert.Single(edit.Modes.Single().Fields).Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectSignsInThenRunsThePickerOverLoopbackAndStoresTheRoots()
    {
        var provider = Provider(stored: false);
        Uri? authorize = null;
        provider.Authenticator = (build, _) =>
        {
            authorize = build("http://localhost:6001");
            var state = Query(authorize)["state"];
            return Task.FromResult(new OAuthCallback("http://localhost:6001", new Uri($"http://localhost:6001/?state={state}&code=abc")));
        };
        _http.OnJson(HttpMethod.Post, "oauth2.googleapis.com/token", """{"access_token":"picker-access","refresh_token":"picker-refresh","expires_in":3600}""");
        _http.OnJson(HttpMethod.Get, "googleapis.com/oauth2/v2/userinfo", """{"email":"jane@example.com","name":"Jane Doe"}""");

        // The real picker server, with the "browser" played by an HttpClient.
        Task? browser = null;
        string? servedPage = null;
        provider.PickFolders = (accessToken, ct) => new GoogleDrivePickerServer(page => browser = Task.Run(async () =>
        {
            using var client = LoopbackClient();
            servedPage = await client.GetStringAsync(page);
            await client.GetStringAsync(ResultUrl(page, "picked", """[{"id":"f1","name":"Work Files"},{"id":"f2","name":"A+B"}]"""));
        })).PickFoldersAsync(accessToken, ct);
        var prompt = new ScriptedConnectPrompt();

        var result = await provider.ConnectAsync(ConnectValues.Empty("browser"), prompt);
        await browser!;

        Assert.Equal("Jane Doe", result.AccountLabel);
        Assert.Equal("Google Drive (Jane Doe)", result.DisplayName);
        Assert.True(provider.IsAuthenticated);
        Assert.Contains("\"picker-access\"", servedPage);
        Assert.Equal(3, prompt.StatusLines.Count);
        Assert.Null(prompt.StatusLines[^1]);

        var query = Query(authorize!);
        Assert.Equal(PickerClientId, query["client_id"]);
        Assert.Equal("https://www.googleapis.com/auth/drive.file https://www.googleapis.com/auth/userinfo.profile https://www.googleapis.com/auth/userinfo.email openid", query["scope"]);
        Assert.Equal("http://localhost:6001", query["redirect_uri"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("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", HttpUtility.ParseQueryString(_http.Single(HttpMethod.Post, "/token").Body!)["client_secret"]);

        var stored = _store.Load<GoogleDrivePickerCredentials>(Key)!;
        Assert.Equal("picker-access", stored.AccessToken);
        Assert.Equal("picker-refresh", stored.RefreshToken);
        Assert.Equal("jane@example.com", stored.UserEmail);
        Assert.Equal(new[] { new GoogleDrivePickedRoot("f1", "Work Files"), new GoogleDrivePickedRoot("f2", "A+B") }, stored.Roots);
    }

    [Fact]
    public async Task CancellingThePickerStoresNothing()
    {
        var provider = Provider();
        var before = _store.Load(Key);
        provider.Authenticator = (build, _) =>
        {
            var state = Query(build("http://localhost:6001"))["state"];
            return Task.FromResult(new OAuthCallback("http://localhost:6001", new Uri($"http://localhost:6001/?state={state}&code=abc")));
        };
        _http.OnJson(HttpMethod.Post, "oauth2.googleapis.com/token", """{"access_token":"other","expires_in":3600}""");
        _http.OnJson(HttpMethod.Get, "googleapis.com/oauth2/v2/userinfo", """{"email":"x@example.com"}""");
        provider.PickFolders = (_, _) => Task.FromResult<IReadOnlyList<GoogleDrivePickedRoot>>([]);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.Cancelled, error.Kind);
        Assert.Equal(before, _store.Load(Key));
        Assert.Equal(2, provider.Roots.Count);
    }

    [Fact]
    public async Task FailedSignInStoresNothing()
    {
        var provider = Provider(stored: false);
        provider.Authenticator = (_, _) => Task.FromException<OAuthCallback>(CloudProviderException.NotAuthenticated());
        var picked = false;
        provider.PickFolders = (_, _) =>
        {
            picked = true;
            return Task.FromResult<IReadOnlyList<GoogleDrivePickedRoot>>([new GoogleDrivePickedRoot("f", "F")]);
        };

        await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt()));

        Assert.False(picked);
        Assert.Null(_store.Load(Key));
        Assert.False(provider.IsAuthenticated);
    }

    [Fact]
    public void RegistryCreatesTheProvider()
    {
        using var provider = CloudProviderRegistry.Create(CloudProviderType.GoogleDrivePicker, Guid.NewGuid(), new MemoryCredentialStore());

        Assert.IsType<GoogleDrivePickerProvider>(provider);
    }

    // MARK: - Picker server

    [Fact]
    public async Task PickerServerServesThePageOnlyAtItsSecretPathAndReturnsTheChoice()
    {
        Task? browser = null;
        string? page = null;
        HttpStatusCode? probe = null;
        HttpStatusCode? guess = null;
        string? done = null;
        var server = new GoogleDrivePickerServer(url => browser = Task.Run(async () =>
        {
            using var client = LoopbackClient();
            page = await client.GetStringAsync(url);
            probe = (await client.GetAsync(new Uri(url, "/favicon.ico"))).StatusCode;
            guess = (await client.GetAsync(new Uri(url, "/"))).StatusCode;
            done = await client.GetStringAsync(ResultUrl(url, "picked", """[{"id":"f1","name":"Tax 2024"}]"""));
        }), TimeSpan.FromSeconds(30));

        var roots = await server.PickFoldersAsync("ya29.token</script><script>alert(1)", CancellationToken.None);
        await browser!;

        Assert.Equal(new[] { new GoogleDrivePickedRoot("f1", "Tax 2024") }, roots);
        Assert.Equal(HttpStatusCode.NotFound, probe);
        Assert.Equal(HttpStatusCode.NotFound, guess);
        Assert.Contains("All set", done);

        // The token sits in a JSON string literal that can't close the script element.
        Assert.DoesNotContain("</script><script>alert", page);
        Assert.Contains("const TOKEN = \"ya29.token\\u003C/script\\u003E", page);
        Assert.Contains("\"AIzaSyDu59GsA1aYauH7lziUSoWkmWj-COWzjis\"", page);
        Assert.Contains("const APPID = \"368567371288\"", page);
        Assert.Contains("setAppId(APPID)", page);
        Assert.Contains("/result\"", page);
    }

    [Fact]
    public async Task PickerServerTreatsCancelAndTimeoutAsNothingPicked()
    {
        Task? browser = null;
        var cancelling = new GoogleDrivePickerServer(url => browser = Task.Run(async () =>
        {
            using var client = LoopbackClient();
            await client.GetStringAsync(new Uri(url.AbsoluteUri + "/result?action=cancel"));
        }), TimeSpan.FromSeconds(30));
        var silent = new GoogleDrivePickerServer(_ => { }, TimeSpan.FromMilliseconds(200));

        var cancelled = await cancelling.PickFoldersAsync("t", CancellationToken.None);
        await browser!;
        var timedOut = await silent.PickFoldersAsync("t", CancellationToken.None);

        Assert.Empty(cancelled);
        Assert.Empty(timedOut);
    }

    [Fact]
    public async Task PickerServerHonoursCancellation()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var server = new GoogleDrivePickerServer(_ => { }, TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.PickFoldersAsync("t", cancel.Token));
    }

    [Theory]
    [InlineData("/s/result?action=picked&data=%5B%7B%22id%22%3A%22a%22%2C%22name%22%3A%22My+Folder%22%7D%5D", "My Folder")]
    [InlineData("/s/result?action=picked&data=%5B%7B%22id%22%3A%22a%22%2C%22name%22%3A%22C%2B%2B%22%7D%5D", "C++")]
    public void PickerResultDecodesFormEncoding(string target, string expectedName)
        => Assert.Equal(expectedName, Assert.Single(GoogleDrivePickerServer.ParseResult(target)).Name);

    [Fact]
    public void PickerResultIgnoresGarbage()
    {
        Assert.Empty(GoogleDrivePickerServer.ParseResult("/s/result?action=picked&data=not-json"));
        Assert.Empty(GoogleDrivePickerServer.ParseResult("/s/result?action=picked"));
        Assert.Empty(GoogleDrivePickerServer.ParseResult("/s/result?action=cancel&data=%5B%5D"));
    }

    private static HttpClient LoopbackClient() => new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>What the page's <c>report()</c> requests: URLSearchParams encoding (spaces as '+').</summary>
    private static Uri ResultUrl(Uri page, string action, string json)
        => new(page.AbsoluteUri + "/result?action=" + action + "&data=" + Uri.EscapeDataString(json).Replace("%20", "+", StringComparison.Ordinal));
}
