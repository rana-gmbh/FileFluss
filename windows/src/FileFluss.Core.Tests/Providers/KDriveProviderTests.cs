// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text.Json;
using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.KDrive;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;
using static FileFluss.Core.Tests.Providers.WebDavFamily.WebDavFamilyTestSupport;

namespace FileFluss.Core.Tests.Providers.WebDavFamily;

public sealed class KDriveProviderTests : IDisposable
{
    private const string Token = "kd-token";

    private readonly DavTempFolder _temp = new();
    private readonly FakeHttpHandler _http = new();
    private readonly MemoryCredentialStore _store = new();
    private readonly Guid _id = Guid.NewGuid();

    /// <summary>"GET /2/drive/101" → responder. kDrive paths nest, so routes match the exact path.</summary>
    private readonly Dictionary<string, Func<HttpRequestMessage, string?, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public KDriveProviderTests()
    {
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            _http.On(method, "https://api.infomaniak.com/", (request, body) =>
                _routes.TryGetValue($"{request.Method.Method} {request.RequestUri!.AbsolutePath}", out var respond)
                    ? respond(request, body)
                    : new HttpResponseMessage((HttpStatusCode)599) { Content = new StringContent("no route") });
        }
    }

    public void Dispose() => _temp.Dispose();

    private void Route(string methodAndPath, string json, HttpStatusCode status = HttpStatusCode.OK)
        => _routes[methodAndPath] = (_, _) => FakeHttpHandler.Json(json, status);

    private void Route(string methodAndPath, Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
        => _routes[methodAndPath] = respond;

    private int Count(string methodAndPath)
        => _http.Requests.Count(r => $"{r.Method.Method} {r.Uri.AbsolutePath}" == methodAndPath);

    private static string Data(string json) => "{\"result\":\"success\",\"data\":" + json + "}";

    private static string File(long id, string name, string type = "file", long size = 0, long modified = 1736244930)
        => $$"""{"id":{{id}},"name":"{{name}}","type":"{{type}}","size":{{size}},"last_modified_at":{{modified}},"created_at":1700000000}""";

    private KDriveProvider Connected(long driveId = 101)
    {
        _store.Save($"kdrive.{_id}", new KDriveCredentials(Token, driveId, "Me Myself"));
        var provider = new KDriveProvider(_id, _store, _http);
        provider.Client!.Delay = (_, _) => Task.CompletedTask;
        return provider;
    }

    private void DriveInfo(long driveId = 101, long rootId = 5)
        => Route($"GET /2/drive/{driveId}", Data($$"""{"id":{{driveId}},"name":"Me's kDrive","root_file_id":{{rootId}},"size":2000000,"used_size":500000}"""));

    private void Profile()
        => Route("GET /1/profile", Data("""{"id":5,"email":"me@example.com","display_name":"Me Myself","current_account_id":77}"""));

    private void Drives(params (long Id, string Name)[] drives)
        => Route("GET /2/drive", Data("[" + string.Join(',', drives.Select(d => $$"""{"id":{{d.Id}},"name":"{{d.Name}}"}""")) + "]"));

    private static ConnectValues TokenValues(string token = " kd-token\n")
        => new("default", new Dictionary<string, string> { ["apiToken"] = token });

    // MARK: - Connect

    [Fact]
    public async Task RequiresAuthentication()
    {
        using var provider = new KDriveProvider(_id, _store, _http);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
        Assert.Null(await provider.StorageQuotaAsync());
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task SingleDriveConnectsWithoutAsking()
    {
        Profile();
        Drives((101, "Me's kDrive"));
        DriveInfo();
        Route("GET /2/drive/101/files/5/files", Data("[]"));
        using var provider = new KDriveProvider(_id, _store, _http);
        var prompt = new RecordingPrompt();

        var result = await provider.ConnectAsync(TokenValues(), prompt);

        Assert.Equal("Me Myself · Me's kDrive", result.AccountLabel);
        Assert.Equal(0, prompt.Choices);
        var stored = _store.Load<KDriveCredentials>($"kdrive.{_id}")!;
        Assert.Equal((Token, 101L, "Me Myself"), (stored.ApiToken, stored.DriveId, stored.UserEmail));
        Assert.All(_http.Requests, r => Assert.Equal("Bearer " + Token, r.Header("Authorization")));
        Assert.Equal("account_id=77", _http.Single(HttpMethod.Get, "/2/drive?").Uri.Query.TrimStart('?'));

        // The root id came with the connect: listing goes straight to it.
        await provider.ListDirectoryAsync("/");
        Assert.Equal(1, Count("GET /2/drive/101"));
        Assert.Equal(1, Count("GET /2/drive/101/files/5/files"));
    }

    [Fact]
    public async Task SeveralDrivesAskWhichOneToAdd()
    {
        Profile();
        Drives((101, "Personal"), (102, "Common Documents"));
        DriveInfo(102, 9);
        using var provider = new KDriveProvider(_id, _store, _http);
        var prompt = new RecordingPrompt(1);

        var result = await provider.ConnectAsync(TokenValues(), prompt);

        Assert.Equal(["Personal", "Common Documents"], prompt.LastOptions);
        Assert.Equal("Me Myself · Common Documents", result.AccountLabel);
        Assert.Equal(102, _store.Load<KDriveCredentials>($"kdrive.{_id}")!.DriveId);
    }

    [Fact]
    public async Task CancellingTheDrivePickerConnectsNothing()
    {
        Profile();
        Drives((101, "Personal"), (102, "Common Documents"));
        using var provider = new KDriveProvider(_id, _store, _http);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(TokenValues(), new RecordingPrompt(null)));

        Assert.Equal(CloudErrorKind.Cancelled, error.Kind);
        Assert.False(provider.IsAuthenticated);
        Assert.Null(_store.Load($"kdrive.{_id}"));
    }

    [Fact]
    public async Task EditingKeepsTheWorkspace()
    {
        Profile();
        Drives((101, "Personal"), (102, "Common Documents"));
        DriveInfo(102, 9);
        using var provider = Connected(102);
        var prompt = new RecordingPrompt(0);

        await provider.ConnectAsync(TokenValues("new-token"), prompt);

        Assert.Equal(0, prompt.Choices);
        var stored = _store.Load<KDriveCredentials>($"kdrive.{_id}")!;
        Assert.Equal(("new-token", 102L), (stored.ApiToken, stored.DriveId));
        Assert.Equal("API Token", provider.ConnectSpec.Modes.Single().Fields[0].Label);
    }

    [Fact]
    public async Task RevokedTokenLeavesStoredCredentialsUntouched()
    {
        Route("GET /1/profile", """{"result":"error","error":{"code":"not_authorized"}}""", HttpStatusCode.Unauthorized);
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(TokenValues("bad"), new RecordingPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal(Token, _store.Load<KDriveCredentials>($"kdrive.{_id}")!.ApiToken);
    }

    [Fact]
    public void AddFormKeepsTheTokenPageLink()
    {
        using var provider = new KDriveProvider(_id, _store, _http);
        var fields = provider.ConnectSpec.Modes.Single().Fields;
        var link = Assert.Single(fields, f => f.Kind == ConnectFieldKind.Link);
        Assert.Equal(("Open Infomaniak token page", "https://manager.infomaniak.com/v3/ng/profile/user/token/list"), (link.Label, link.Url));
        Assert.Equal(["apiToken"], fields.Where(f => f.HasValue).Select(f => f.Key));
    }

    // MARK: - Listing and the path cache

    [Fact]
    public async Task RootIsLookedUpLazilyAndListingsArePaginated()
    {
        DriveInfo();
        Route("GET /2/drive/101/files/5/files", (request, _) =>
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            Assert.Equal(("name", "asc", "2"), (query["order_by"], query["order"], query["per_page"]));
            return FakeHttpHandler.Json(query["page"] == "1"
                ? Data($"[{File(10, "Docs", "dir")},{File(11, "a.txt", size: 300)}]")
                : Data($"[{File(12, ".env", size: 1)}]"));
        });
        Route("GET /2/drive/101/files/10/files", Data("[]"));
        using var provider = Connected();
        provider.Client!.PageSize = 2;

        var items = await provider.ListDirectoryAsync("/");

        Assert.Equal(["Docs", "a.txt", ".env"], items.Select(i => i.Name));
        Assert.Equal(("d10", "/Docs", true), (items[0].Id, items[0].Path, items[0].IsDirectory));
        Assert.Equal(("f11", 300L), (items[1].Id, items[1].Size));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1736244930), items[1].ModificationDate);
        Assert.True(items[2].IsHidden);
        Assert.Equal(2, Count("GET /2/drive/101/files/5/files"));

        // Children are cached: the next level needs no walk from the root.
        await provider.ListDirectoryAsync("/Docs");
        Assert.Equal(2, Count("GET /2/drive/101/files/5/files"));
        Assert.Equal(1, Count("GET /2/drive/101"));
    }

    [Fact]
    public async Task ConcurrentFirstCallsShareOneRootLookup()
    {
        Route("GET /2/drive/101", (_, _) =>
        {
            Thread.Sleep(100); // keep the lookup in flight while the other callers arrive
            return FakeHttpHandler.Json(Data("""{"id":101,"name":"x","root_file_id":5}"""));
        });
        Route("GET /2/drive/101/files/5/files", Data("[]"));
        using var provider = Connected();

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => provider.ListDirectoryAsync("/"))));

        Assert.Equal(1, Count("GET /2/drive/101"));
        Assert.Equal(6, Count("GET /2/drive/101/files/5/files"));
    }

    [Fact]
    public async Task UnknownRootFieldFallsBackToOne()
    {
        Route("GET /2/drive/101", Data("""{"id":101,"name":"x"}"""));
        Route("GET /2/drive/101/files/1/files", Data("[]"));
        using var provider = Connected();

        Assert.Empty(await provider.ListDirectoryAsync("/"));
    }

    [Fact]
    public async Task DeleteEvictsTheWholeSubtree()
    {
        DriveInfo();
        Route("GET /2/drive/101/files/5/files", Data($"[{File(10, "Docs", "dir")}]"));
        Route("GET /2/drive/101/files/10/files", Data($"[{File(20, "Sub", "dir")}]"));
        Route("GET /2/drive/101/files/20/files", Data($"[{File(30, "deep.txt")}]"));
        Route("DELETE /2/drive/101/files/10", Data("true"));
        using var provider = Connected();

        await provider.ListDirectoryAsync("/Docs/Sub");
        Assert.Equal(1, Count("GET /2/drive/101/files/5/files"));

        await provider.DeleteItemAsync("/Docs");
        Assert.Equal(1, Count("DELETE /2/drive/101/files/10"));

        // "/Docs/Sub" and "/Docs/Sub/deep.txt" went with it: resolving walks from the root again.
        Route("GET /2/drive/101/files/5/files", Data("[]"));
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.GetFileMetadataAsync("/Docs/Sub/deep.txt"));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal("/Docs", error.ItemPath);
    }

    [Fact]
    public async Task ACompletedListingRetiresVanishedChildren()
    {
        DriveInfo();
        var rootListing = Data($"[{File(10, "Docs", "dir")}]");
        Route("GET /2/drive/101/files/5/files", (_, _) => FakeHttpHandler.Json(rootListing));
        Route("GET /2/drive/101/files/10/files", Data("[]"));
        using var provider = Connected();

        await provider.ListDirectoryAsync("/");
        await provider.ListDirectoryAsync("/Docs");

        // Renamed in the web UI meanwhile.
        rootListing = Data($"[{File(10, "Papers", "dir")}]");
        await provider.ListDirectoryAsync("/");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/Docs"));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task RenameRecachesTheNewPath()
    {
        DriveInfo();
        Route("GET /2/drive/101/files/5/files", Data($"[{File(10, "Docs", "dir")}]"));
        Route("POST /2/drive/101/files/10/rename", Data("true"));
        Route("GET /2/drive/101/files/10/files", Data("[]"));
        using var provider = Connected();

        await provider.RenameItemAsync("/Docs", "Papers");
        await provider.ListDirectoryAsync("/Papers");

        Assert.Equal("""{"name":"Papers"}""", _http.Single(HttpMethod.Post, "/rename").Body);
        Assert.Equal(1, Count("GET /2/drive/101/files/5/files"));
        Assert.Equal(1, Count("GET /2/drive/101/files/10/files"));
    }

    [Fact]
    public async Task CreateFolderCachesTheNewId()
    {
        DriveInfo();
        var created = false;
        Route("GET /2/drive/101/files/5/files", (_, _) => FakeHttpHandler.Json(created ? Data($"[{File(30, "New", "dir")}]") : Data("[]")));
        Route("POST /2/drive/101/files/5/directory", (_, _) =>
        {
            created = true;
            return FakeHttpHandler.Json(Data("{}"));
        });
        Route("GET /2/drive/101/files/30/files", Data("[]"));
        using var provider = Connected();

        await provider.CreateDirectoryAsync("/New");
        await provider.CreateDirectoryAsync("/New"); // idempotent
        await provider.ListDirectoryAsync("/New");

        Assert.Equal("""{"name":"New"}""", Assert.Single(_http.All(HttpMethod.Post)).Body);
        Assert.Equal(1, Count("GET /2/drive/101/files/30/files"));
    }

    [Fact]
    public async Task UnauthorizedTokenAsksForSignIn()
    {
        Route("GET /2/drive/101", "{}", HttpStatusCode.Unauthorized);
        Route("GET /2/drive/101/files/1/files", "{}", HttpStatusCode.Unauthorized);
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.True(error.NeedsReauthentication);
    }

    [Fact]
    public async Task MetadataFolderSizeAndQuota()
    {
        DriveInfo();
        Route("GET /2/drive/101/files/5/files", Data($"[{File(10, "Docs", "dir")},{File(11, "a.bin", size: 100)}]"));
        Route("GET /2/drive/101/files/10/files", Data($"[{File(12, "b.bin", size: 23)}]"));
        Route("GET /2/drive/101/files/11", Data(File(11, "a.bin", size: 100)));
        using var provider = Connected();

        var meta = await provider.GetFileMetadataAsync("/a.bin");
        Assert.Equal(("f11", "/a.bin", 100L), (meta.Id, meta.Path, meta.Size));

        Assert.Equal(123, await provider.FolderSizeAsync("/"));

        var quota = await provider.StorageQuotaAsync();
        Assert.Equal((500_000L, (long?)2_000_000), (quota!.UsedBytes, quota.TotalBytes));
    }

    [Fact]
    public async Task NoServerSideMove()
    {
        using var provider = Connected();
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.MoveItemAsync("/a", "/b"));
        Assert.Equal(CloudErrorKind.NotImplemented, error.Kind);
    }

    // MARK: - Transfers

    [Fact]
    public async Task UploadRetriesTheUploadServicesEarly404WithoutDoubleCountingProgress()
    {
        DriveInfo();
        var local = _temp.File("a+b c.txt", 70_000, new DateTime(2025, 1, 7, 10, 15, 30, DateTimeKind.Utc));
        var attempts = 0;
        Route("POST /3/drive/101/upload", (_, _) => ++attempts < 3
            ? FakeHttpHandler.Json("""{"result":"error","error":{"code":"object_not_found"}}""", HttpStatusCode.NotFound)
            : FakeHttpHandler.Json(Data("""{"id":77}""")));
        using var provider = Connected();
        var delays = new List<TimeSpan>();
        provider.Client!.Delay = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };
        long reported = 0;

        await provider.UploadFileAsync(local, "/a+b c.txt", d => reported += d);

        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)], delays);
        Assert.Equal(70_000, reported);

        var upload = _http.All(HttpMethod.Post).Last();
        var query = HttpUtility.ParseQueryString(upload.Uri.Query);
        Assert.Equal(("5", "a+b c.txt", "70000", "version", "1736244930"), (query["directory_id"], query["file_name"], query["total_size"], query["conflict"], query["last_modified_at"]));
        Assert.Contains("file_name=a%2Bb%20c.txt", upload.Uri.AbsoluteUri); // '+' must not turn into a space
        Assert.NotNull(query["created_at"]);
        Assert.Equal("application/octet-stream", upload.Header("Content-Type"));
        Assert.Equal(70_000, upload.Body!.Length);
    }

    [Fact]
    public async Task UploadGivesUpAfterThreeRetries()
    {
        DriveInfo();
        var local = _temp.File("x.txt", 10);
        Route("POST /3/drive/101/upload", "{}", HttpStatusCode.NotFound);
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(local, "/x.txt"));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal(4, _http.All(HttpMethod.Post).Length);
    }

    [Fact]
    public async Task DownloadByFileId()
    {
        DriveInfo();
        Route("GET /2/drive/101/files/5/files", Data($"[{File(11, "a.txt", size: 3)}]"));
        Route("GET /2/drive/101/files/11/download", (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("abc"u8.ToArray()) });
        using var provider = Connected();
        var target = Path.Combine(_temp.Path, "a.txt");

        await provider.DownloadFileAsync("/a.txt", target);

        Assert.Equal("abc", await System.IO.File.ReadAllTextAsync(target));
    }

    // MARK: - Share links

    private void SharedFile()
    {
        DriveInfo();
        Route("GET /2/drive/101/files/5/files", Data($"[{File(11, "a.txt")}]"));
    }

    [Fact]
    public async Task CreateLinkLeavesUnsetOptionsOut()
    {
        SharedFile();
        Route("POST /2/drive/101/files/11/link", Data("""{"url":"https://kdrive.infomaniak.com/app/share/101/abc","right":"public","valid_until":null,"capabilities":{"can_edit":false,"can_download":false}}"""));
        using var provider = Connected();

        var link = await provider.CreateShareLinkAsync("/a.txt", ShareLinkOptions.Default);

        var body = JsonDocument.Parse(_http.Single(HttpMethod.Post, "/link").Body!).RootElement;
        Assert.Equal(["right", "can_download", "can_edit"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal("public", body.GetProperty("right").GetString());
        Assert.Equal(new Uri("https://kdrive.infomaniak.com/app/share/101/abc"), link.Url);
        Assert.Null(link.DirectDownloadUrl);
        Assert.False(link.HasPassword);
        Assert.NotNull(link.Note); // asked for download, the workspace granted view-only
    }

    [Fact]
    public async Task CreateLinkWithPasswordAndExpiry()
    {
        SharedFile();
        Route("POST /2/drive/101/files/11/link", Data("""{"url":"https://kdrive.infomaniak.com/app/share/101/def","right":"password","valid_until":1767225600}"""));
        using var provider = Connected();
        var expiry = DateTimeOffset.FromUnixTimeSeconds(1767225600);

        var link = await provider.CreateShareLinkAsync("/a.txt", new ShareLinkOptions { Password = "pw", Expiry = expiry, AllowDownload = false });

        var body = JsonDocument.Parse(_http.Single(HttpMethod.Post, "/link").Body!).RootElement;
        Assert.Equal("password", body.GetProperty("right").GetString());
        Assert.Equal("pw", body.GetProperty("password").GetString());
        Assert.Equal(1767225600, body.GetProperty("valid_until").GetInt64());
        Assert.False(body.GetProperty("can_download").GetBoolean());
        Assert.True(link.HasPassword);
        Assert.Equal(expiry, link.ExpiresAt);
        Assert.Null(link.Note);
    }

    [Fact]
    public async Task PaidFeatureRefusalSurfacesInfomaniaksText()
    {
        SharedFile();
        Route("POST /2/drive/101/files/11/link", """{"result":"error","error":{"code":"forbidden_for_free_drive","description":"Expiry dates require kSuite"}}""", HttpStatusCode.Forbidden);
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/a.txt", new ShareLinkOptions { Expiry = DateTimeOffset.UtcNow.AddDays(3) }));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("Expiry dates require kSuite", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"result":"error","error":{"code":"object_not_found"}}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"result":"error","error":{"code":"link_not_found","description":"No link"}}""")]
    [InlineData(HttpStatusCode.OK, """{"result":"success","data":null}""")]
    public async Task TheManyWaysOfSayingNotShared(HttpStatusCode status, string body)
    {
        SharedFile();
        Route("GET /2/drive/101/files/11/link", body, status);
        using var provider = Connected();

        Assert.Null(await provider.ExistingShareLinkAsync("/a.txt"));
    }

    [Fact]
    public async Task ExistingLinkIsReadBack()
    {
        SharedFile();
        Route("GET /2/drive/101/files/11/link", Data("""{"url":"https://kdrive.infomaniak.com/app/share/101/abc","right":"password","capabilities":{"can_download":false}}"""));
        using var provider = Connected();

        var link = await provider.ExistingShareLinkAsync("/a.txt");

        Assert.True(link!.HasPassword);
        Assert.Null(link.Note); // a lookup asked for nothing
    }

    [Fact]
    public async Task UpdateSaysEveryFieldOutLoudAndRereadsWhenTheServerAnswersTrue()
    {
        SharedFile();
        Route("PUT /2/drive/101/files/11/link", Data("true"));
        Route("GET /2/drive/101/files/11/link", Data("""{"url":"https://kdrive.infomaniak.com/app/share/101/abc","right":"public","valid_until":null}"""));
        using var provider = Connected();

        var link = await provider.UpdateShareLinkAsync("/a.txt", ShareLinkOptions.Default);

        Assert.Equal(
            """{"right":"public","password":null,"valid_until":null,"can_download":true,"can_edit":false}""",
            _http.Single(HttpMethod.Put, "/link").Body);
        Assert.Equal(new Uri("https://kdrive.infomaniak.com/app/share/101/abc"), link.Url);
        Assert.Equal(1, Count("GET /2/drive/101/files/11/link"));
    }

    [Fact]
    public async Task RemoveTreatsNoLinkAsDone()
    {
        SharedFile();
        Route("DELETE /2/drive/101/files/11/link", "{}", HttpStatusCode.NotFound);
        using var provider = Connected();

        await provider.RemoveShareLinkAsync("/a.txt");

        Route("DELETE /2/drive/101/files/11/link", """{"result":"error","error":{"code":"server_error","message":"Try later"}}""", HttpStatusCode.InternalServerError);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.RemoveShareLinkAsync("/a.txt"));
        Assert.Equal("Try later", error.Message);
        Assert.True(provider.ShareLinkCapabilities is { CanUpdate: true, SupportsExpiry: true, ExpiryRequiresPaidPlan: true, PasswordRequiresPaidPlan: false });
    }

    /// <summary>Records drive-picker calls; answers with a fixed choice (null = cancel).</summary>
    private sealed class RecordingPrompt(int? answer = 0) : IConnectPrompt
    {
        public int Choices { get; private set; }

        public IReadOnlyList<string>? LastOptions { get; private set; }

        public void ShowStatus(string? message)
        {
        }

        public Task<string?> RequestCodeAsync(string title, string message, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options, CancellationToken cancellationToken)
        {
            Choices++;
            LastOptions = options;
            return Task.FromResult(answer);
        }

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task ShowImageAsync(byte[] png, string caption, Task until, CancellationToken cancellationToken) => until;
    }
}
