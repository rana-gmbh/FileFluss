// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.WebDav;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;
using static FileFluss.Core.Tests.Providers.WebDavFamily.WebDavFamilyTestSupport;

namespace FileFluss.Core.Tests.Providers.WebDavFamily;

public sealed class WebDavProviderTests : IDisposable
{
    private const string Base = "https://dav.example.com/dav";
    private static readonly string BasicAlice = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret"));

    private readonly DavTempFolder _temp = new();
    private readonly FakeHttpHandler _http = new();
    private readonly MemoryCredentialStore _store = new();
    private readonly Guid _id = Guid.NewGuid();

    public void Dispose() => _temp.Dispose();

    private WebDavProvider Connected()
    {
        _store.Save($"webdav.{_id}", new WebDavCredentials(Base, "alice", "s3cret", "alice"));
        return new WebDavProvider(_id, _store, _http);
    }

    private static HttpResponseMessage OptionsAllowing(params string[] methods)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) };
        foreach (var method in methods)
        {
            response.Content.Headers.Allow.Add(method);
        }

        return response;
    }

    private const string RootSelf = """
        <d:multistatus xmlns:d="DAV:"><d:response><d:href>/dav/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response></d:multistatus>
        """;

    // MARK: - Lifecycle and connect

    [Fact]
    public async Task RequiresAuthentication()
    {
        // Port of the macOS "…Provider requires authentication" tests.
        using var provider = new WebDavProvider(_id, _store, _http);
        Assert.False(provider.IsAuthenticated);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
        Assert.Null(await provider.StorageQuotaAsync());
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task ConnectProbesTheUrlAndStoresTheWritableRoot()
    {
        _http.On(Propfind, "https://dav.example.com/files/", (_, _) => Multistatus(RootSelf));
        _http.On(HttpMethod.Options, "https://dav.example.com/files/", (_, _) => OptionsAllowing("OPTIONS", "PROPFIND", "MKCOL", "PUT"));
        using var provider = new WebDavProvider(_id, _store, _http);

        var result = await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["serverUrl"] = " https://dav.example.com/files/ ", ["username"] = "alice", ["password"] = "s3cret" }),
            new ScriptedConnectPrompt());

        Assert.Equal("alice", result.AccountLabel);
        Assert.True(provider.IsAuthenticated);
        var stored = _store.Load<WebDavCredentials>($"webdav.{_id}")!;
        Assert.Equal("https://dav.example.com/files", stored.ServerUrl);
        Assert.Equal("s3cret", stored.Password);

        var probe = _http.Single(Propfind, "https://dav.example.com/files/");
        Assert.Equal("0", probe.Header("Depth"));
        Assert.Equal(BasicAlice, probe.Header("Authorization"));
        Assert.Contains("<d:propfind", probe.Body);
        Assert.Equal(new Dictionary<string, string> { ["serverUrl"] = "https://dav.example.com/files", ["username"] = "alice" }, provider.GetEditableConnectValues());
    }

    [Fact]
    public async Task ConnectFallsBackToTheUsualSubPaths()
    {
        _http.On(Propfind, "https://dav.example.com/webdav/", (_, _) => Multistatus(RootSelf));
        _http.On(HttpMethod.Options, "https://dav.example.com/webdav/", (_, _) => OptionsAllowing("MKCOL"));
        _http.On(Propfind, "https://dav.example.com/", (_, _) => Status(HttpStatusCode.NotFound));
        using var provider = new WebDavProvider(_id, _store, _http);

        await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["serverUrl"] = "dav.example.com", ["username"] = "alice", ["password"] = "s3cret" }),
            new ScriptedConnectPrompt());

        // No scheme typed → https assumed; the bare host 404s, /webdav answers and is writable.
        Assert.Equal("https://dav.example.com/webdav", _store.Load<WebDavCredentials>($"webdav.{_id}")!.ServerUrl);
    }

    [Fact]
    public async Task ReadOnlyEndpointIsTheFallbackWhenNothingBetterAnswers()
    {
        _http.On(Propfind, "https://dav.example.com/remote.php/dav/files/alice%40example.com/", (_, _) => Status(HttpStatusCode.NotFound));
        _http.On(Propfind, "https://dav.example.com/", (_, _) => Multistatus(RootSelf));
        _http.On(HttpMethod.Options, "https://dav.example.com/", (_, _) => OptionsAllowing("PROPFIND", "GET"));
        using var provider = new WebDavProvider(_id, _store, _http);

        await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["serverUrl"] = "https://dav.example.com", ["username"] = "alice@example.com", ["password"] = "pw" }),
            new ScriptedConnectPrompt());

        Assert.Equal("https://dav.example.com", _store.Load<WebDavCredentials>($"webdav.{_id}")!.ServerUrl);

        // The Nextcloud candidate carries the user name percent-encoded.
        Assert.Contains(_http.Requests, r => r.Uri.AbsoluteUri == "https://dav.example.com/remote.php/dav/files/alice%40example.com/");
    }

    [Fact]
    public async Task WrongPasswordLeavesStoredCredentialsUntouched()
    {
        using var provider = Connected();
        _http.On(Propfind, "https://other.example.com/", (_, _) => Status(HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["serverUrl"] = "https://other.example.com", ["username"] = "alice", ["password"] = "wrong" }),
            new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal(Base, _store.Load<WebDavCredentials>($"webdav.{_id}")!.ServerUrl);
        Assert.Equal(Base, provider.GetEditableConnectValues()["serverUrl"]);
    }

    [Fact]
    public void ConnectFormMatchesTheMacSheets()
    {
        using var adding = new WebDavProvider(_id, _store, _http);
        var addFields = adding.ConnectSpec.Modes.Single().Fields;
        Assert.Equal(ConnectFieldKind.Info, addFields[0].Kind);
        Assert.Equal(["serverUrl", "username", "password"], addFields.Where(f => f.HasValue).Select(f => f.Key));
        Assert.Equal("Server URL (e.g. https://dav.example.com/files)", addFields[1].Label);

        using var editing = Connected();
        Assert.Equal("Server URL", editing.ConnectSpec.Modes.Single().Fields[0].Label);
    }

    // MARK: - Listing and metadata

    [Fact]
    public async Task ListingSendsDepthOnePropfindToTheEncodedPath()
    {
        const string xml = """
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/dav/My%20Docs/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
              <d:response><d:href>/dav/My%20Docs/a%2Bb.txt</d:href><d:propstat><d:prop><d:getcontentlength>12</d:getcontentlength><d:getlastmodified>Tue, 07 Jan 2025 10:15:30 GMT</d:getlastmodified></d:prop></d:propstat></d:response>
            </d:multistatus>
            """;
        _http.On(Propfind, "/dav/My Docs", (_, _) => Multistatus(xml));
        using var provider = Connected();

        var items = await provider.ListDirectoryAsync("/My Docs/");

        var item = Assert.Single(items);
        Assert.Equal("a+b.txt", item.Name);
        Assert.Equal("/My Docs/a+b.txt", item.Path);
        Assert.Equal(12, item.Size);

        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://dav.example.com/dav/My%20Docs", request.Uri.AbsoluteUri);
        Assert.Equal("1", request.Header("Depth"));
        Assert.Equal(BasicAlice, request.Header("Authorization"));
        Assert.StartsWith("application/xml", request.Header("Content-Type"));
    }

    [Fact]
    public async Task RootListingGoesToTheBaseWithATrailingSlash()
    {
        _http.On(Propfind, Base, (_, _) => Multistatus(WebDavParserTests.ApacheRootListing));
        using var provider = Connected();

        var items = await provider.ListDirectoryAsync("/");

        Assert.Equal(2, items.Count);
        Assert.Equal(Base + "/", Assert.Single(_http.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task MetadataUsesDepthZero()
    {
        const string xml = """
            <d:multistatus xmlns:d="DAV:"><d:response><d:href>/dav/report.txt</d:href><d:propstat><d:prop><d:getcontentlength>42</d:getcontentlength></d:prop></d:propstat></d:response></d:multistatus>
            """;
        _http.On(Propfind, "/dav/report.txt", (_, _) => Multistatus(xml));
        using var provider = Connected();

        var item = await provider.GetFileMetadataAsync("/report.txt");

        Assert.Equal(("report.txt", "/report.txt", 42L), (item.Name, item.Path, item.Size));
        Assert.Equal("0", Assert.Single(_http.Requests).Header("Depth"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CloudErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, CloudErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, CloudErrorKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, CloudErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InsufficientStorage, CloudErrorKind.QuotaExceeded)]
    [InlineData(HttpStatusCode.Conflict, CloudErrorKind.ServerError)]
    public async Task StatusMapping(HttpStatusCode status, CloudErrorKind expected)
    {
        _http.On(Propfind, "/dav/Missing", (_, _) => Status(status));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/Missing"));

        Assert.Equal(expected, error.Kind);
        if (expected == CloudErrorKind.NotFound)
        {
            Assert.Equal("/Missing", error.ItemPath);
        }
    }

    [Fact]
    public async Task SameHostRedirectKeepsTheAuthorizationHeader()
    {
        _http.On(Propfind, "/dav/Docs/", (_, _) => Multistatus(RootSelf));
        _http.On(Propfind, "/dav/Docs", (_, _) => Redirect(HttpStatusCode.MovedPermanently, "https://dav.example.com/dav/Docs/"));
        using var provider = Connected();

        await provider.ListDirectoryAsync("/Docs");

        var requests = _http.All(Propfind);
        Assert.Equal(2, requests.Length);
        Assert.Equal(BasicAlice, requests[1].Header("Authorization"));
        Assert.Equal("1", requests[1].Header("Depth"));
        Assert.Contains("<d:propfind", requests[1].Body);
    }

    [Fact]
    public async Task RedirectToAnotherHostDropsTheCredentials()
    {
        _http.On(Propfind, "https://elsewhere.example.net/", (_, _) => Multistatus(RootSelf));
        _http.On(Propfind, "/dav/Docs", (_, _) => Redirect(HttpStatusCode.TemporaryRedirect, "https://elsewhere.example.net/Docs/"));
        using var provider = Connected();

        await provider.ListDirectoryAsync("/Docs");

        Assert.Null(_http.All(Propfind)[1].Header("Authorization"));
    }

    // MARK: - Transfers

    [Fact]
    public async Task DownloadStreamsToTheFileWithProgress()
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', 300_000));
        _http.OnBytes(HttpMethod.Get, "/dav/Docs/big.bin", payload);
        using var provider = Connected();
        var target = Path.Combine(_temp.Path, "big.bin");
        long reported = 0;

        await provider.DownloadFileAsync("/Docs/big.bin", target, d => Interlocked.Add(ref reported, d));

        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        Assert.Equal(payload.Length, reported);
        Assert.False(File.Exists(target + ".fileflusspart"));
    }

    [Fact]
    public async Task DownloadOfAMissingFileIsNotFound()
    {
        _http.On(HttpMethod.Get, "/dav/gone.txt", (_, _) => Status(HttpStatusCode.NotFound));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DownloadFileAsync("/gone.txt", Path.Combine(_temp.Path, "gone.txt")));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task UploadIsOneStreamedPutWithTheModificationTime()
    {
        var modified = new DateTime(2024, 11, 3, 14, 30, 5, DateTimeKind.Utc);
        var local = _temp.File("upload.bin", 200_000, modified);
        _http.On(HttpMethod.Put, "/dav/Docs/upload.bin", (_, _) => Status(HttpStatusCode.Created));
        using var provider = Connected();
        long reported = 0;

        await provider.UploadFileAsync(local, "/Docs/upload.bin", d => Interlocked.Add(ref reported, d));

        var put = Assert.Single(_http.Requests);
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal(new DateTimeOffset(modified).ToUnixTimeSeconds().ToString(), put.Header("X-OC-Mtime"));
        Assert.Equal("application/octet-stream", put.Header("Content-Type"));
        Assert.Equal(200_000, put.Body!.Length);
        Assert.Equal(200_000, reported);
    }

    [Fact]
    public async Task RedirectedUploadResendsTheBodyWithoutCountingItTwice()
    {
        var local = _temp.File("r.bin", 150_000);
        _http.On(HttpMethod.Put, "/dav/new/r.bin", (_, _) => Status(HttpStatusCode.Created));
        _http.On(HttpMethod.Put, "/dav/r.bin", (_, _) => Redirect(HttpStatusCode.TemporaryRedirect, "https://dav.example.com/dav/new/r.bin"));
        using var provider = Connected();
        long reported = 0;

        await provider.UploadFileAsync(local, "/r.bin", d => reported += d);

        var puts = _http.All(HttpMethod.Put);
        Assert.Equal(2, puts.Length);
        Assert.All(puts, p => Assert.Equal(150_000, p.Body!.Length));
        Assert.Equal(BasicAlice, puts[1].Header("Authorization"));
        Assert.Equal(150_000, reported);

        // The file stream behind each attempt is closed again (no lingering lock).
        await using (new FileStream(local, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, CloudErrorKind.FileTooLarge)]
    [InlineData(HttpStatusCode.UnprocessableEntity, CloudErrorKind.ServerError)] // 422 only means "too large" for files > 2 GB
    [InlineData(HttpStatusCode.InsufficientStorage, CloudErrorKind.QuotaExceeded)]
    public async Task UploadRejections(HttpStatusCode status, CloudErrorKind expected)
    {
        var local = _temp.File("x.bin", 10);
        _http.On(HttpMethod.Put, "/dav/x.bin", (_, _) => Status(status, "<error/>"));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(local, "/x.bin"));
        Assert.Equal(expected, error.Kind);
    }

    [Fact]
    public void SinglePutUploadsArePreflightedBelow4GiB()
    {
        using var provider = Connected();
        Assert.Equal(4_000_000_000, provider.MaxUploadFileSize);
    }

    // MARK: - Changes

    [Fact]
    public async Task MkcolTriesBothFormsAndTreats405AsExisting()
    {
        _http.On(Mkcol, "/dav/New Folder", (_, _) => Status(HttpStatusCode.MethodNotAllowed));
        using var provider = Connected();

        await provider.CreateDirectoryAsync("/New Folder");

        var requests = _http.All(Mkcol);
        Assert.Equal(["https://dav.example.com/dav/New%20Folder", "https://dav.example.com/dav/New%20Folder/"], requests.Select(r => r.Uri.AbsoluteUri));
        Assert.Equal(string.Empty, requests[0].Body);
    }

    [Fact]
    public async Task MkcolCreatedNeedsOneRequest()
    {
        _http.On(Mkcol, "/dav/New", (_, _) => Status(HttpStatusCode.Created));
        using var provider = Connected();

        await provider.CreateDirectoryAsync("/New");

        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task MkcolConflictIsAnError()
    {
        _http.On(Mkcol, "/dav/a/b", (_, _) => Status(HttpStatusCode.Conflict));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateDirectoryAsync("/a/b"));
        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public async Task MoveAndCopyAreServerSideWithAbsoluteDestinations()
    {
        _http.On(Move, "/dav/", (_, _) => Status(HttpStatusCode.Created));
        _http.On(Copy, "/dav/", (_, _) => Status(HttpStatusCode.NoContent));
        using var provider = Connected();

        await provider.MoveItemAsync("/Docs/a.txt", "/Archive/ä b.txt");
        await provider.CopyItemAsync("/Docs", "/Docs copy");
        await provider.RenameItemAsync("/Docs/old.txt", "new name.txt");

        var moves = _http.All(Move);
        Assert.Equal("https://dav.example.com/dav/Docs/a.txt", moves[0].Uri.AbsoluteUri);
        Assert.Equal("https://dav.example.com/dav/Archive/%C3%A4%20b.txt", moves[0].Header("Destination"));
        Assert.Equal("F", moves[0].Header("Overwrite"));
        Assert.Equal("https://dav.example.com/dav/Docs/new%20name.txt", moves[1].Header("Destination"));

        var copy = Assert.Single(_http.All(Copy));
        Assert.Equal("https://dav.example.com/dav/Docs%20copy", copy.Header("Destination"));
    }

    [Fact]
    public async Task DeleteMapsFailures()
    {
        _http.On(HttpMethod.Delete, "/dav/ok.txt", (_, _) => Status(HttpStatusCode.NoContent));
        _http.On(HttpMethod.Delete, "/dav/locked.txt", (_, _) => Status((HttpStatusCode)423));
        using var provider = Connected();

        await provider.DeleteItemAsync("/ok.txt");
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DeleteItemAsync("/locked.txt"));
        Assert.Equal(423, error.StatusCode);
    }

    [Fact]
    public async Task ModificationDateIsABestEffortProppatch()
    {
        _http.On(Proppatch, "/dav/a.txt", (_, _) => Status(HttpStatusCode.Forbidden));
        using var provider = Connected();

        await provider.SetModificationDateAsync("/a.txt", new DateTimeOffset(2025, 1, 7, 10, 15, 30, TimeSpan.Zero));

        var patch = Assert.Single(_http.Requests);
        Assert.Contains("<d:getlastmodified>Tue, 07 Jan 2025 10:15:30 GMT</d:getlastmodified>", patch.Body);
        Assert.Contains("<d:propertyupdate xmlns:d=\"DAV:\">", patch.Body);
    }

    // MARK: - Search and quota

    [Fact]
    public async Task SearchWalksTheTreeAndMatchesNamesCaseInsensitively()
    {
        _http.On(Propfind, "/dav/Archive", (_, _) => Multistatus("""
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/dav/Archive/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat></d:response>
              <d:response><d:href>/dav/Archive/Old%20Report.txt</d:href></d:response>
            </d:multistatus>
            """));
        _http.On(Propfind, Base, (_, _) => Multistatus(WebDavParserTests.ApacheRootListing));
        using var provider = Connected();

        var hits = await provider.SearchFilesAsync("REPORT", null);

        Assert.Equal(["/report 2024.txt", "/Archive/Old Report.txt"], hits!.Select(h => h.Path));
    }

    [Fact]
    public async Task QuotaComesFromRfc4331Properties()
    {
        _http.On(Propfind, Base, (_, _) => Multistatus("""
            <D:multistatus xmlns:D="DAV:"><D:response><D:href>/dav/</D:href><D:propstat><D:prop><D:quota-available-bytes>9000</D:quota-available-bytes><D:quota-used-bytes>1000</D:quota-used-bytes></D:prop></D:propstat></D:response></D:multistatus>
            """));
        using var provider = Connected();

        var quota = await provider.StorageQuotaAsync();

        Assert.Equal((1000L, (long?)10_000), (quota!.UsedBytes, quota.TotalBytes));
        var request = Assert.Single(_http.Requests);
        Assert.Equal(Base, request.Uri.AbsoluteUri);
        Assert.Contains("quota-used-bytes", request.Body);
        Assert.Equal(9000, await provider.AvailableSpaceAsync("/"));
    }

    [Fact]
    public async Task QuotaIsAbsentWhenTheServerRefuses()
    {
        _http.On(Propfind, Base, (_, _) => Status(HttpStatusCode.MethodNotAllowed));
        using var provider = Connected();
        Assert.Null(await provider.StorageQuotaAsync());
    }

    [Fact]
    public async Task DisconnectForgetsTheCredentials()
    {
        using var provider = Connected();
        await provider.DisconnectAsync();
        Assert.False(provider.IsAuthenticated);
        Assert.Null(_store.Load($"webdav.{_id}"));
    }
}

public sealed class GmxCloudProviderTests
{
    private readonly FakeHttpHandler _http = new();
    private readonly MemoryCredentialStore _store = new();
    private readonly Guid _id = Guid.NewGuid();

    [Fact]
    public async Task ConnectUsesTheMediaCenterEndpoint()
    {
        _http.On(Propfind, "https://webdav.mc.gmx.net/", (_, _) => Multistatus(WebDavParserTests.GmxListing));
        _http.On(HttpMethod.Options, "https://webdav.mc.gmx.net/", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) };
            response.Content.Headers.Allow.Add("MKCOL");
            return response;
        });
        using var provider = new GmxCloudProvider(_id, _store, _http);

        var result = await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["email"] = "max@gmx.de", ["password"] = "pw" }),
            new ScriptedConnectPrompt());

        Assert.Equal("max@gmx.de", result.AccountLabel);
        var stored = _store.Load<WebDavCredentials>($"gmxCloud.{_id}")!;
        Assert.Equal(("https://webdav.mc.gmx.net", "max@gmx.de"), (stored.ServerUrl, stored.Username));
        Assert.Equal(new Dictionary<string, string> { ["email"] = "max@gmx.de" }, provider.GetEditableConnectValues());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("max@gmx.de:pw")), _http.Requests.First().Header("Authorization"));
    }

    [Fact]
    public async Task ListingAndLimits()
    {
        _store.Save($"gmxCloud.{_id}", new WebDavCredentials("https://webdav.mc.gmx.net", "max@gmx.de", "pw", "max@gmx.de"));
        _http.On(Propfind, "https://webdav.mc.gmx.net/Bilder", (_, _) => Multistatus(WebDavParserTests.GmxListing));
        using var provider = new GmxCloudProvider(_id, _store, _http);

        var items = await provider.ListDirectoryAsync("/Bilder");

        Assert.Equal(["Urlaub 2024.jpg", "Neuer Ordner"], items.Select(i => i.Name));
        Assert.Equal(4_000_000_000, provider.MaxUploadFileSize);
        Assert.Equal("GMX Email", provider.ConnectSpec.Modes.Single().Fields.First(f => f.HasValue).Label);

        // The macOS GMX provider never offered PROPPATCH mtimes.
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.SetModificationDateAsync("/Bilder/x.jpg", DateTimeOffset.UtcNow));
        Assert.Equal(CloudErrorKind.NotImplemented, error.Kind);
    }
}

public sealed class WebDavFamilyRegistryTests
{
    [Theory]
    [InlineData(CloudProviderType.WebDav, typeof(WebDavProvider))]
    [InlineData(CloudProviderType.GmxCloud, typeof(GmxCloudProvider))]
    [InlineData(CloudProviderType.NextCloud, typeof(FileFluss.Core.Providers.NextCloud.NextCloudProvider))]
    [InlineData(CloudProviderType.Koofr, typeof(FileFluss.Core.Providers.Koofr.KoofrProvider))]
    [InlineData(CloudProviderType.KDrive, typeof(FileFluss.Core.Providers.KDrive.KDriveProvider))]
    public void RegistryCreatesTheProviderWithoutNetwork(CloudProviderType type, Type expected)
    {
        Assert.True(CloudProviderRegistry.IsAvailable(type));
        using var provider = CloudProviderRegistry.Create(type, Guid.NewGuid(), new MemoryCredentialStore());
        Assert.IsType(expected, provider);
        Assert.Equal(type, provider.ProviderType);
        Assert.False(provider.IsAuthenticated);
        Assert.NotEmpty(provider.ConnectSpec.Modes);
    }
}
