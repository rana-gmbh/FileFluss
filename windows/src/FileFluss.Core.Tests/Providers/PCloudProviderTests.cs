// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.PCloud;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

public sealed class PCloudProviderTests : IDisposable
{
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();
    private readonly List<string> _tempFiles = [];

    private string Key => $"pcloud.{_accountId}";

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            File.Delete(file);
        }
    }

    // MARK: - Setup helpers

    private PCloudProvider Provider(string hostname = "eapi.pcloud.com")
    {
        _store.Save(Key, new PCloudCredentials("TOKEN", hostname, 42));
        return new PCloudProvider(_accountId, _store, _http);
    }

    private PCloudCredentials? Stored() => _store.Load<PCloudCredentials>(Key);

    private static NameValueCollection Query(FakeHttpHandler.RecordedRequest request) => HttpUtility.ParseQueryString(request.Uri.Query);

    private FakeHttpHandler.RecordedRequest Call(string method)
        => _http.Requests.Single(r => r.Uri.AbsolutePath == "/" + method);

    private IReadOnlyList<FakeHttpHandler.RecordedRequest> Calls(string method)
        => _http.Requests.Where(r => r.Uri.AbsolutePath == "/" + method).ToList();

    private string TempFile(int size)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ff-pcloud-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(i => (byte)('a' + (i % 26))).ToArray());
        File.SetLastWriteTimeUtc(path, new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc));
        _tempFiles.Add(path);
        return path;
    }

    private const string FileStat = """{ "result": 0, "metadata": { "name": "a.txt", "isfolder": false, "fileid": 77, "size": 3, "modified": 1700000000 } }""";
    private const string FolderStat = """{ "result": 0, "metadata": { "name": "Docs", "isfolder": true, "folderid": 9, "modified": 1700000000 } }""";
    private const string Ok = """{ "result": 0 }""";

    // MARK: - Authentication state

    [Fact]
    public async Task WithoutStoredCredentialsEveryCallNeedsSignIn()
    {
        using var provider = new PCloudProvider(_accountId, _store, _http);
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
        Assert.Empty(_http.Requests);
        Assert.Empty(provider.GetEditableConnectValues());
    }

    [Fact]
    public void IsRegisteredForItsType()
    {
        using var provider = CloudProviderRegistry.Create(CloudProviderType.PCloud, _accountId, _store);
        Assert.IsType<PCloudProvider>(provider);
    }

    [Fact]
    public void AddingSignsInThroughTheBrowser()
    {
        using var provider = new PCloudProvider(_accountId, _store, _http);
        var mode = Assert.Single(provider.ConnectSpec.Modes);
        Assert.True(mode.OpensBrowser);
        Assert.Equal(
            "Click Connect to sign in with your pCloud account. Your browser will open for authentication.",
            Assert.Single(mode.Fields).Label);
        Assert.DoesNotContain(mode.Fields, f => f.HasValue);
        Assert.True(provider.ConnectSpec.SupportsInlineReauthentication);
    }

    [Fact]
    public void EditingOffersReauthorize()
    {
        using var provider = Provider();
        var mode = Assert.Single(provider.ConnectSpec.Modes);
        Assert.True(mode.OpensBrowser);
        Assert.StartsWith("Click Re-authorize", Assert.Single(mode.Fields).Label, StringComparison.Ordinal);
        Assert.True(provider.ConnectSpec.SupportsInlineReauthentication);
    }

    // MARK: - Listing

    [Fact]
    public async Task ListsAFolderOnTheAccountsRegion()
    {
        _http.OnJson(HttpMethod.Get, "/listfolder", """
            { "result": 0, "metadata": { "name": "My Docs", "folderid": 5, "isfolder": true, "contents": [
                { "name": "Photos", "isfolder": true, "folderid": 12, "modified": 1700000000, "created": 1600000000 },
                { "name": "a.pdf", "isfolder": false, "fileid": 34, "size": 1234, "modified": 1710000000, "hash": 18446744073709551615, "contenttype": "application/pdf" },
                { "name": ".env", "isfolder": false, "fileid": 35 }
            ] } }
            """);
        using var provider = Provider("api.pcloud.com");

        var items = await provider.ListDirectoryAsync("/My Docs/");

        Assert.Equal(3, items.Count);
        Assert.Equal("d12", items[0].Id);
        Assert.Equal("/My Docs/Photos", items[0].Path);
        Assert.True(items[0].IsDirectory);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), items[0].ModificationDate);
        Assert.Equal("f34", items[1].Id);
        Assert.Equal(1234, items[1].Size);
        Assert.Equal("18446744073709551615", items[1].Checksum);
        Assert.True(items[2].IsHidden);
        Assert.Equal(CloudFileItem.UnknownDate, items[2].ModificationDate);

        var request = _http.Requests.Single();
        Assert.Equal("https://api.pcloud.com/listfolder?auth=TOKEN&path=%2FMy%20Docs&timeformat=timestamp", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task FolderSizeSumsARecursiveListing()
    {
        _http.OnJson(HttpMethod.Get, "/listfolder", """
            { "result": 0, "metadata": { "isfolder": true, "contents": [
                { "name": "a", "isfolder": false, "size": 10 },
                { "name": "sub", "isfolder": true, "contents": [ { "name": "b", "isfolder": false, "size": 5 }, { "name": "deep", "isfolder": true, "contents": [ { "name": "c", "isfolder": false, "size": 1 } ] } ] }
            ] } }
            """);
        using var provider = Provider();

        Assert.Equal(16, await provider.FolderSizeAsync("/x"));
        Assert.Equal("1", Query(Call("listfolder"))["recursive"]);
    }

    [Fact]
    public async Task MetadataComesFromStat()
    {
        _http.OnJson(HttpMethod.Get, "/stat", FileStat);
        using var provider = Provider();

        var item = await provider.GetFileMetadataAsync("/Docs/a.txt");

        Assert.Equal("/Docs/a.txt", item.Path);
        Assert.Equal("f77", item.Id);
        Assert.Equal("timestamp", Query(Call("stat"))["timeformat"]);
    }

    // MARK: - Errors

    [Theory]
    [InlineData(1000, CloudErrorKind.NotAuthenticated)]
    [InlineData(2000, CloudErrorKind.Unauthorized)]
    [InlineData(2003, CloudErrorKind.Unauthorized)]
    [InlineData(2005, CloudErrorKind.NotFound)]
    [InlineData(2009, CloudErrorKind.NotFound)]
    [InlineData(2008, CloudErrorKind.QuotaExceeded)]
    [InlineData(4000, CloudErrorKind.RateLimited)]
    [InlineData(5000, CloudErrorKind.ServerError)]
    public async Task ResultCodesMapLikeMacOS(int code, CloudErrorKind kind)
    {
        _http.OnJson(HttpMethod.Get, "/listfolder", $$"""{ "result": {{code}}, "error": "pCloud says no" }""");
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/Docs"));

        Assert.Equal(kind, error.Kind);
        if (kind == CloudErrorKind.NotFound)
        {
            Assert.Equal("/Docs", error.ItemPath);
        }
    }

    [Fact]
    public async Task HttpFailuresAreServerErrors()
    {
        _http.OnText(HttpMethod.Get, "/listfolder", "oops", HttpStatusCode.BadGateway);
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.ServerError, error.Kind);
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task ANonJsonBodyIsAnInvalidResponse()
    {
        _http.OnText(HttpMethod.Get, "/listfolder", "<html>maintenance</html>", mediaType: "text/html");
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.InvalidResponse, error.Kind);
    }

    // MARK: - Transfers

    [Fact]
    public async Task DownloadsThroughTheCdnLink()
    {
        var bytes = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        _http.OnJson(HttpMethod.Get, "/getfilelink", """{ "result": 0, "path": "/cBZ/a%20b.bin", "hosts": ["c123.pcloud.com", "c456.pcloud.com"] }""");
        _http.OnBytes(HttpMethod.Get, "c123.pcloud.com", bytes);
        using var provider = Provider();
        var target = Path.Combine(Path.GetTempPath(), $"ff-pcloud-dl-{Guid.NewGuid():N}.bin");
        _tempFiles.Add(target);
        long reported = 0;

        await provider.DownloadFileAsync("/Docs/a b.bin", target, delta => Interlocked.Add(ref reported, delta));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        Assert.Equal(bytes.Length, reported);
        Assert.Equal("/Docs/a b.bin", Query(Call("getfilelink"))["path"]);
        var cdn = _http.Requests.Single(r => r.Uri.Host == "c123.pcloud.com");
        Assert.Equal("https://c123.pcloud.com/cBZ/a%20b.bin", cdn.Uri.AbsoluteUri);
        Assert.DoesNotContain("TOKEN", cdn.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadOfAMissingFileIsNotFound()
    {
        _http.OnJson(HttpMethod.Get, "/getfilelink", """{ "result": 2009, "error": "File not found." }""");
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DownloadFileAsync("/gone.bin", Path.Combine(Path.GetTempPath(), "never.bin")));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task NonAsciiNamesGoInTheFilePartAsRawUtf8()
    {
        // pCloud names the file after the part's filename and keeps an RFC 2047 word literally:
        // .NET's MultipartFormDataContent turned this into "=?utf-8?B?…?=" on the server.
        const string name = "20240917 Leihvertrag EBK Brückenstr 47 EG Goethestraße.docx";
        var local = TempFile(10);
        _http.OnJson(HttpMethod.Post, "/uploadfile", $$"""{ "result": 0, "metadata": [ { "name": "{{name}}" } ] }""");
        using var provider = Provider();

        await provider.UploadFileAsync(local, "/Docs/" + name);

        var request = Call("uploadfile");
        Assert.Contains($"name=\"file\"; filename=\"{name}\"", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("=?utf-8?", request.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("filename*", request.Body, StringComparison.Ordinal);
        Assert.Equal(name, Query(request)["filename"]);
    }

    [Fact]
    public async Task UploadsAsOneStreamedMultipartPart()
    {
        var local = TempFile(5000);
        _http.OnJson(HttpMethod.Post, "/uploadfile", """{ "result": 0, "metadata": [ { "name": "a & b.txt" } ] }""");
        using var provider = Provider();
        long reported = 0;

        await provider.UploadFileAsync(local, "/Docs/a & b.txt", delta => Interlocked.Add(ref reported, delta));

        Assert.Equal(5000, reported);
        var request = Call("uploadfile");
        var query = Query(request);
        Assert.Equal("TOKEN", query["auth"]);
        Assert.Equal("/Docs", query["path"]);
        Assert.Equal("a & b.txt", query["filename"]);
        Assert.Equal("1", query["nopartial"]);
        Assert.Equal(new DateTimeOffset(2025, 6, 7, 8, 9, 10, TimeSpan.Zero).ToUnixTimeSeconds().ToString(), query["mtime"]);
        Assert.NotNull(query["ctime"]);
        Assert.StartsWith("multipart/form-data; boundary=", request.Headers["Content-Type"], StringComparison.Ordinal);
        Assert.Contains("name=\"file\"; filename=\"a & b.txt\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("Content-Type: application/octet-stream", request.Body, StringComparison.Ordinal);
        Assert.Contains(await File.ReadAllTextAsync(local), request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadFailuresUsePCloudsResultCode()
    {
        var local = TempFile(10);
        _http.OnJson(HttpMethod.Post, "/uploadfile", """{ "result": 2008, "error": "User is over quota." }""");
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(local, "/x.bin"));

        Assert.Equal(CloudErrorKind.QuotaExceeded, error.Kind);
    }

    // MARK: - Deleting

    [Fact]
    public async Task DeletingAFolderFallsThroughFromDeletefile()
    {
        _http.OnJson(HttpMethod.Get, "/deletefile", """{ "result": 2009, "error": "File not found." }""");
        _http.OnJson(HttpMethod.Get, "/deletefolderrecursive", Ok);
        using var provider = Provider();

        await provider.DeleteItemAsync("/Docs");

        Assert.Single(Calls("deletefile"));
        Assert.Equal("/Docs", Query(Call("deletefolderrecursive"))["path"]);
    }

    [Fact]
    public async Task DeletingAFileRepeatsUntilShadowCopiesAreGone()
    {
        var deletes = 0;
        _http.On(HttpMethod.Get, "/deletefile", (_, _) =>
            FakeHttpHandler.Json(Interlocked.Increment(ref deletes) <= 2 ? Ok : """{ "result": 2009 }"""));
        using var provider = Provider();

        await provider.DeleteItemAsync("/a.txt");

        Assert.Equal(3, deletes);
        Assert.Empty(Calls("deletefolderrecursive"));
        Assert.Empty(Calls("stat"));
    }

    [Fact]
    public async Task ALockedFileIsRetriedAfterABackoff()
    {
        var deletes = 0;
        _http.On(HttpMethod.Get, "/deletefile", (_, _) => FakeHttpHandler.Json(Interlocked.Increment(ref deletes) switch
        {
            1 => """{ "result": 2055, "error": "Metadata locked." }""",
            2 => Ok,
            _ => """{ "result": 2009 }""",
        }));
        using var provider = Provider();

        await provider.DeleteItemAsync("/a.txt");

        Assert.Equal(3, deletes);
    }

    [Fact]
    public async Task AFileThatSurvivesSixDeletesIsAnError()
    {
        _http.OnJson(HttpMethod.Get, "/deletefile", Ok);
        _http.OnJson(HttpMethod.Get, "/stat", FileStat);
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DeleteItemAsync("/a.txt"));

        Assert.Equal(CloudErrorKind.ServerError, error.Kind);
        Assert.Equal(6, Calls("deletefile").Count);
    }

    // MARK: - Rename, move, copy, dates

    [Fact]
    public async Task RenamesFilesAndFoldersWithTheMatchingCall()
    {
        _http.On(HttpMethod.Get, "/stat", (request, _) =>
            FakeHttpHandler.Json(HttpUtility.ParseQueryString(request.RequestUri!.Query)["path"] == "/Docs" ? FolderStat : FileStat));
        _http.OnJson(HttpMethod.Get, "/renamefile", Ok);
        _http.OnJson(HttpMethod.Get, "/renamefolder", Ok);
        using var provider = Provider();

        await provider.RenameItemAsync("/Docs/a.txt", "b.txt");
        await provider.RenameItemAsync("/Docs", "Papers");

        Assert.Equal("/Docs/b.txt", Query(Call("renamefile"))["topath"]);
        Assert.Equal("/Papers", Query(Call("renamefolder"))["topath"]);
    }

    [Fact]
    public async Task MovesAndCopiesServerSide()
    {
        _http.On(HttpMethod.Get, "/stat", (request, _) =>
            FakeHttpHandler.Json(HttpUtility.ParseQueryString(request.RequestUri!.Query)["path"] == "/Docs" ? FolderStat : FileStat));
        _http.OnJson(HttpMethod.Get, "/renamefile", Ok);
        _http.OnJson(HttpMethod.Get, "/copyfolder", Ok);
        using var provider = Provider();

        await provider.MoveItemAsync("/Docs/a.txt", "/Archive/a.txt");
        await provider.CopyItemAsync("/Docs", "/Backup/Docs");

        Assert.Equal("/Archive/a.txt", Query(Call("renamefile"))["topath"]);
        var copy = Query(Call("copyfolder"));
        Assert.Equal("/Docs", copy["path"]);
        Assert.Equal("/Backup/Docs", copy["topath"]);
    }

    [Fact]
    public async Task SetsTheModificationTimeWithTheRawPath()
    {
        _http.OnJson(HttpMethod.Get, "/setfilemtime", Ok);
        using var provider = Provider();

        await provider.SetModificationDateAsync("/Docs/a b+c.txt", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        var query = Query(Call("setfilemtime"));
        Assert.Equal("/Docs/a b+c.txt", query["path"]);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).ToUnixTimeSeconds().ToString(), query["newtime"]);
    }

    [Fact]
    public async Task CreatesFoldersIdempotently()
    {
        _http.OnJson(HttpMethod.Get, "/createfolderifnotexists", Ok);
        using var provider = Provider();

        await provider.CreateDirectoryAsync("/Docs/New");

        Assert.Equal("/Docs/New", Query(Call("createfolderifnotexists"))["path"]);
    }

    // MARK: - Quota

    [Fact]
    public async Task QuotaComesFromUserinfo()
    {
        _http.OnJson(HttpMethod.Get, "/userinfo", """{ "result": 0, "email": "me@example.com", "userid": 42, "quota": 10737418240, "usedquota": 1073741824 }""");
        using var provider = Provider();

        var quota = await provider.StorageQuotaAsync();

        Assert.Equal(1_073_741_824, quota!.UsedBytes);
        Assert.Equal(10_737_418_240, quota.TotalBytes);
    }

    [Fact]
    public async Task AZeroQuotaHasNoUpperBound()
    {
        _http.OnJson(HttpMethod.Get, "/userinfo", """{ "result": 0, "quota": 0, "usedquota": 5 }""");
        using var provider = Provider();

        Assert.Null((await provider.StorageQuotaAsync())!.TotalBytes);
    }

    // MARK: - Share links

    [Fact]
    public async Task CreatesAPublicLinkWithPremiumOptionsAndADirectUrl()
    {
        _http.OnJson(HttpMethod.Get, "/getfilepublink", """{ "result": 0, "link": "https://u.pcloud.link/publink/show?code=XZabc", "linkid": 555, "code": "XZabc" }""");
        _http.OnJson(HttpMethod.Get, "/getpublinkdownload", """{ "result": 0, "path": "/dl/a.pdf", "hosts": ["p-def.pcloud.com"] }""");
        using var provider = Provider();
        var expiry = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.FromHours(1));

        var link = await provider.CreateShareLinkAsync("/Docs/a.pdf", new ShareLinkOptions { Password = "pw", Expiry = expiry });

        Assert.Equal(new Uri("https://u.pcloud.link/publink/show?code=XZabc"), link.Url);
        Assert.Equal(new Uri("https://p-def.pcloud.com/dl/a.pdf"), link.DirectDownloadUrl);
        Assert.Equal(link.DirectDownloadUrl, link.PreferredUrl);
        Assert.True(link.HasPassword);
        Assert.Equal(expiry, link.ExpiresAt);
        var query = Query(Call("getfilepublink"));
        Assert.Equal("/Docs/a.pdf", query["path"]);
        Assert.Equal("pw", query["linkpassword"]);
        Assert.Equal("2026-12-31T22:59:59Z", query["expire"]);
        Assert.Equal("XZabc", Query(Call("getpublinkdownload"))["code"]);
    }

    [Fact]
    public async Task ALinkWithoutADirectUrlIsStillALink()
    {
        _http.OnJson(HttpMethod.Get, "/getfilepublink", """{ "result": 0, "link": "https://u.pcloud.link/publink/show?code=Q", "code": "Q" }""");
        _http.OnJson(HttpMethod.Get, "/getpublinkdownload", """{ "result": 7001, "error": "Invalid link code." }""");
        using var provider = Provider();

        var link = await provider.CreateShareLinkAsync("/a.pdf", ShareLinkOptions.Default);

        Assert.Null(link.DirectDownloadUrl);
        Assert.False(link.HasPassword);
        Assert.Null(Query(Call("getfilepublink"))["linkpassword"]);
    }

    [Fact]
    public async Task ARefusedLinkSurfacesPCloudsWording()
    {
        _http.OnJson(HttpMethod.Get, "/getfilepublink", """{ "result": 2116, "error": "Setting a password requires a Premium account." }""");
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/a.pdf", new ShareLinkOptions { Password = "x" }));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("Setting a password requires a Premium account.", error.Message);
    }

    [Fact]
    public async Task FindsTheExistingLinkByFileIdBeforePath()
    {
        _http.OnJson(HttpMethod.Get, "/stat", FileStat);
        _http.OnJson(HttpMethod.Get, "/listpublinks", """
            { "result": 0, "publinks": [
                { "linkid": 1, "code": "WRONG", "link": "https://u.pcloud.link/publink/show?code=WRONG", "path": "/Docs/a.txt", "metadata": { "fileid": 999 } },
                { "linkid": 2, "code": "RIGHT", "haspassword": true, "expire": "Thu, 31 Dec 2026 23:59:59 +0000", "metadata": { "fileid": 77, "path": "a.txt" } }
            ] }
            """);
        _http.OnJson(HttpMethod.Get, "/getpublinkdownload", """{ "result": 0, "path": "/dl", "hosts": ["p.pcloud.com"] }""");
        using var provider = Provider();

        var link = await provider.ExistingShareLinkAsync("/Docs/a.txt");

        Assert.Equal(new Uri("https://my.pcloud.com/publink/show?code=RIGHT"), link!.Url);
        Assert.True(link.HasPassword);
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero), link.ExpiresAt);
        Assert.Equal("timestamp", Query(Call("listpublinks"))["timeformat"]);
    }

    [Fact]
    public async Task FallsBackToPathThenNameWhenLinksCarryNoId()
    {
        _http.OnJson(HttpMethod.Get, "/stat", """{ "result": 2009 }""");
        _http.OnJson(HttpMethod.Get, "/listpublinks", """
            { "result": 0, "publinks": [ { "linkid": 3, "link": "https://u.pcloud.link/x", "expire": 0, "metadata": { "path": "Docs/a.txt" } } ] }
            """);
        using var provider = Provider();

        var link = await provider.ExistingShareLinkAsync("/Docs/a.txt");

        Assert.Equal(new Uri("https://u.pcloud.link/x"), link!.Url);
        Assert.Null(link.ExpiresAt);
    }

    [Fact]
    public async Task NoMatchingLinkMeansNotShared()
    {
        _http.OnJson(HttpMethod.Get, "/stat", FileStat);
        _http.OnJson(HttpMethod.Get, "/listpublinks", """{ "result": 0, "publinks": [ { "linkid": 1, "fileid": 5, "path": "/other.txt" } ] }""");
        using var provider = Provider();

        Assert.Null(await provider.ExistingShareLinkAsync("/Docs/a.txt"));
    }

    [Fact]
    public async Task ListingLinksFailingIsAnError()
    {
        _http.OnJson(HttpMethod.Get, "/stat", FileStat);
        _http.OnJson(HttpMethod.Get, "/listpublinks", """{ "result": 2000, "error": "Log in failed." }""");
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ExistingShareLinkAsync("/Docs/a.txt"));

        Assert.Equal("Log in failed.", error.Message);
    }

    [Fact]
    public async Task UpdatingClearsWhatTheOptionsLeaveOutAndKeepsTheUrl()
    {
        _http.OnJson(HttpMethod.Get, "/stat", FileStat);
        _http.OnJson(HttpMethod.Get, "/listpublinks", """
            { "result": 0, "publinks": [ { "linkid": 8, "code": "C", "link": "https://u.pcloud.link/c", "fileid": 77, "haspassword": false, "expires": 1798761599 } ] }
            """);
        _http.OnJson(HttpMethod.Get, "/changepublink", Ok);
        _http.OnJson(HttpMethod.Get, "/getpublinkdownload", """{ "result": 1 }""");
        using var provider = Provider();

        var link = await provider.UpdateShareLinkAsync("/Docs/a.txt", ShareLinkOptions.Default);

        Assert.Equal(new Uri("https://u.pcloud.link/c"), link.Url);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1798761599), link.ExpiresAt);
        var change = Query(Call("changepublink"));
        Assert.Equal("8", change["linkid"]);
        Assert.Equal("1", change["deletepassword"]);
        Assert.Equal("1", change["deleteexpire"]);
        Assert.Null(change["linkpassword"]);
        Assert.Equal(2, Calls("listpublinks").Count);
    }

    [Fact]
    public async Task UpdatingSetsNewOptions()
    {
        _http.OnJson(HttpMethod.Get, "/stat", FileStat);
        _http.OnJson(HttpMethod.Get, "/listpublinks", """{ "result": 0, "publinks": [ { "linkid": 8, "link": "https://u.pcloud.link/c", "fileid": 77 } ] }""");
        _http.OnJson(HttpMethod.Get, "/changepublink", Ok);
        using var provider = Provider();

        var link = await provider.UpdateShareLinkAsync("/Docs/a.txt", new ShareLinkOptions { Password = "new", Expiry = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        Assert.True(link.HasPassword);
        var change = Query(Call("changepublink"));
        Assert.Equal("new", change["linkpassword"]);
        Assert.Equal("2027-01-01T00:00:00Z", change["expire"]);
        Assert.Null(change["deletepassword"]);
    }

    [Fact]
    public async Task RemovingDeletesTheLinkById()
    {
        _http.OnJson(HttpMethod.Get, "/stat", FileStat);
        _http.OnJson(HttpMethod.Get, "/listpublinks", """{ "result": 0, "publinks": [ { "linkid": 8, "fileid": 77 } ] }""");
        _http.OnJson(HttpMethod.Get, "/deletepublink", Ok);
        using var provider = Provider();

        await provider.RemoveShareLinkAsync("/Docs/a.txt");

        Assert.Equal("8", Query(Call("deletepublink"))["linkid"]);
    }

    [Fact]
    public async Task RemovingWithoutALinkSaysSo()
    {
        _http.OnJson(HttpMethod.Get, "/stat", FileStat);
        _http.OnJson(HttpMethod.Get, "/listpublinks", """{ "result": 0, "publinks": [] }""");
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.RemoveShareLinkAsync("/Docs/a.txt"));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Empty(Calls("deletepublink"));
    }

    [Theory]
    [InlineData("Thu, 31 Dec 2026 23:59:59 +0000", "2026-12-31T23:59:59+00:00")]
    [InlineData("Fri, 1 Jan 2027 10:00:00 +0200", "2027-01-01T08:00:00+00:00")]
    [InlineData("2027-01-01T08:00:00Z", "2027-01-01T08:00:00+00:00")]
    public void ParsesLinkDatesInEitherFormat(string raw, string expected)
        => Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), PCloudDates.ParseText(raw));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("not a date")]
    public void NeverExpiringOrUnreadableDatesAreNull(string raw) => Assert.Null(PCloudDates.ParseText(raw));

    // MARK: - Connect

    /// <summary>
    /// A browser round trip that redirects to <paramref name="redirectUri"/> with
    /// <paramref name="query"/> appended to the state the authorize URL carried.
    /// </summary>
    private static Func<Func<string, Uri>, CancellationToken, Task<OAuthCallback>> Redirect(string query, string redirectUri = "http://localhost", Action<Uri>? authorize = null)
        => (build, _) =>
        {
            var authorizeUri = build(redirectUri);
            authorize?.Invoke(authorizeUri);
            var state = HttpUtility.ParseQueryString(authorizeUri.Query)["state"];
            return Task.FromResult(new OAuthCallback(redirectUri, new Uri($"{redirectUri}/?state={Uri.EscapeDataString(state!)}&{query}")));
        };

    private const string TokenAnswer = """{ "result": 0, "access_token": "OAUTH", "token_type": "bearer", "locationid": 2, "userid": 4711 }""";

    [Fact]
    public async Task ConnectSignsInInTheBrowserAndStoresAnOAuthToken()
    {
        _http.OnJson(HttpMethod.Get, "eapi.pcloud.com/oauth2_token", TokenAnswer);
        _http.OnJson(HttpMethod.Get, "eapi.pcloud.com/userinfo", """{ "result": 0, "userid": 4711, "email": "me@example.com", "quota": 1, "usedquota": 0 }""");
        Uri? authorizeUri = null;
        using var provider = new PCloudProvider(_accountId, _store, _http)
        {
            Authenticator = Redirect("code=the-code&hostname=EAPI.pcloud.com&locationid=2", authorize: uri => authorizeUri = uri),
        };
        var prompt = new ScriptedConnectPrompt();

        var result = await provider.ConnectAsync(ConnectValues.Empty("browser"), prompt);

        Assert.Equal("me@example.com", result.AccountLabel);
        Assert.True(provider.IsAuthenticated);
        Assert.Equal(new PCloudCredentials("OAUTH", "eapi.pcloud.com", 4711, IsOAuth: true), Stored());

        Assert.Equal("https://my.pcloud.com/oauth2/authorize", authorizeUri!.GetLeftPart(UriPartial.Path));
        var authorize = HttpUtility.ParseQueryString(authorizeUri.Query);
        Assert.Equal(PCloudApiClient.ClientId, authorize["client_id"]);
        Assert.Equal("code", authorize["response_type"]);
        Assert.Equal("http://localhost", authorize["redirect_uri"]);
        Assert.False(string.IsNullOrEmpty(authorize["state"]));
        Assert.Null(authorize["client_secret"]);

        var exchange = Call("oauth2_token");
        Assert.Equal("eapi.pcloud.com", exchange.Uri.Host);
        var query = Query(exchange);
        Assert.Equal(PCloudApiClient.ClientId, query["client_id"]);
        Assert.Equal(PCloudApiClient.ClientSecret, query["client_secret"]);
        Assert.Equal("the-code", query["code"]);

        var userinfo = Query(Call("userinfo"));
        Assert.Equal("OAUTH", userinfo["access_token"]);
        Assert.Null(userinfo["auth"]);
        Assert.NotNull(prompt.StatusLines.First());
        Assert.Null(prompt.StatusLines.Last());
    }

    [Fact]
    public async Task ATokenAnswerThatDoesNotNameTheUserIsCompletedFromUserinfo()
    {
        _http.OnJson(HttpMethod.Get, "/oauth2_token", """{ "result": 0, "access_token": "OAUTH" }""");
        _http.OnJson(HttpMethod.Get, "/userinfo", """{ "result": 0, "userid": 99, "email": "me@example.com" }""");
        using var provider = new PCloudProvider(_accountId, _store, _http) { Authenticator = Redirect("code=c&hostname=api.pcloud.com") };

        await provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt());

        Assert.Equal(new PCloudCredentials("OAUTH", "api.pcloud.com", 99, IsOAuth: true), Stored());
        Assert.All(Calls("userinfo"), r => Assert.Equal("OAUTH", Query(r)["access_token"]));
    }

    [Theory]
    [InlineData("eapi.pcloud.com", "eapi.pcloud.com")]
    [InlineData("api.pcloud.com", "api.pcloud.com")]
    [InlineData("API.pcloud.com", "api.pcloud.com")]
    public void PCloudsOwnApiHostsAreAccepted(string named, string expected)
        => Assert.Equal(expected, PCloudApiClient.OAuthHost(named));

    [Theory]
    [InlineData("evil.example.com")]
    [InlineData("eapi.pcloud.com.evil.example")]
    [InlineData("pcloud.com")]
    [InlineData("eapi.pcloud.com:8443")]
    [InlineData("")]
    [InlineData(null)]
    public void AnyOtherHostIsRefused(string? named)
    {
        var error = Assert.Throws<CloudProviderException>(() => PCloudApiClient.OAuthHost(named));
        Assert.Equal(CloudErrorKind.InvalidResponse, error.Kind);
    }

    /// <summary>The code and the app secret go to the host the redirect names — never to a foreign one.</summary>
    [Fact]
    public async Task ARedirectNamingAForeignHostSendsNothing()
    {
        using var provider = Provider();
        provider.Authenticator = Redirect("code=c&hostname=evil.example.com");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidResponse, error.Kind);
        Assert.Empty(_http.Requests);
        Assert.Equal(new PCloudCredentials("TOKEN", "eapi.pcloud.com", 42), Stored());
    }

    [Theory]
    [InlineData("""{ "result": 0, "access_token": "t", "token_type": "bearer", "uid": 7 }""", "t", 7UL)]
    [InlineData("""{ "result": 0, "access_token": "t", "userid": 8 }""", "t", 8UL)]
    [InlineData("""{ "result": 0, "access_token": "t", "uid": "9" }""", "t", 9UL)]
    [InlineData("""{ "result": 0, "access_token": "t", "userid": "10" }""", "t", 10UL)]
    [InlineData("""{ "result": 0, "auth": "a", "userid": 11 }""", "a", 11UL)]
    [InlineData("""{ "result": 0, "access_token": "t", "auth": "a", "uid": 12, "userid": 13 }""", "t", 12UL)]
    public void TheTokenAnswerIsReadWithEitherSpelling(string json, string token, ulong userId)
    {
        var (accessToken, uid) = PCloudApiClient.ParseTokenResponse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(token, accessToken);
        Assert.Equal(userId, uid);
    }

    [Theory]
    [InlineData("""{ "result": 0, "access_token": "t" }""")]
    [InlineData("""{ "result": 0, "access_token": "t", "userid": "not a number" }""")]
    [InlineData("""{ "result": 0, "access_token": "t", "userid": null }""")]
    public void AnAnswerWithoutAUserLeavesItOpen(string json)
        => Assert.Null(PCloudApiClient.ParseTokenResponse(Encoding.UTF8.GetBytes(json)).UserId);

    [Fact]
    public void PCloudsOwnRefusalIsPassedOn()
    {
        var error = Assert.Throws<CloudProviderException>(() =>
            PCloudApiClient.ParseTokenResponse(Encoding.UTF8.GetBytes("""{ "result": 2012, "error": "Invalid 'code' provided." }""")));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("Invalid 'code' provided.", error.Message);
    }

    [Theory]
    [InlineData("<html>maintenance</html>")]
    [InlineData("[1, 2]")]
    public void AnAnswerThatIsNoJsonObjectIsAnInvalidResponse(string body)
        => Assert.Equal(CloudErrorKind.InvalidResponse, Assert.Throws<CloudProviderException>(() => PCloudApiClient.ParseTokenResponse(Encoding.UTF8.GetBytes(body))).Kind);

    [Fact]
    public async Task AFailedExchangeLeavesTheStoredSignInInPlace()
    {
        _http.OnJson(HttpMethod.Get, "/oauth2_token", """{ "result": 2012, "error": "Invalid 'code' provided." }""");
        using var provider = Provider();
        provider.Authenticator = Redirect("code=stale&hostname=eapi.pcloud.com");
        var prompt = new ScriptedConnectPrompt();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), prompt));

        Assert.Equal("Invalid 'code' provided.", error.Message);
        Assert.Equal(new PCloudCredentials("TOKEN", "eapi.pcloud.com", 42), Stored());
        Assert.True(provider.IsAuthenticated);
        Assert.Null(prompt.StatusLines.Last());
    }

    [Fact]
    public async Task AnOAuthErrorCallbackIsUnauthorized()
    {
        using var provider = new PCloudProvider(_accountId, _store, _http) { Authenticator = Redirect("error=access_denied") };

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
        Assert.Empty(_http.Requests);
        Assert.Null(Stored());
    }

    [Fact]
    public async Task ACallbackForAnotherSignInIsRefused()
    {
        using var provider = new PCloudProvider(_accountId, _store, _http)
        {
            Authenticator = (_, _) => Task.FromResult(new OAuthCallback("http://localhost", new Uri("http://localhost/?code=c&hostname=api.pcloud.com&state=forged"))),
        };

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task ALabelLookupFailureStillConnects()
    {
        _http.OnJson(HttpMethod.Get, "/oauth2_token", TokenAnswer);
        _http.OnJson(HttpMethod.Get, "/userinfo", """{ "result": 4000, "error": "Too many login tries." }""");
        using var provider = new PCloudProvider(_accountId, _store, _http) { Authenticator = Redirect("code=c&hostname=api.pcloud.com") };

        var result = await provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt());

        Assert.Null(result.AccountLabel);
        Assert.NotNull(Stored());
    }

    /// <summary>pCloud matches the redirect exactly, port included: port 80 is the registered bare http://localhost.</summary>
    [Fact]
    public void TheRegisteredRedirectPortsStartWithPort80()
    {
        Assert.Equal([80, 53682, 53683], PCloudApiClient.RedirectPorts);
        Assert.Equal(["http://localhost", "http://localhost:53682", "http://localhost:53683"], PCloudApiClient.RedirectPorts.Select(OAuthLoopback.RedirectOrigin));
    }

    // MARK: - Tokens from before the browser sign-in

    [Fact]
    public async Task ABrowserSignInTokenIsSentAsAccessToken()
    {
        _store.Save(Key, new PCloudCredentials("OAUTH", "api.pcloud.com", 1, IsOAuth: true));
        _http.OnJson(HttpMethod.Get, "/listfolder", """{ "result": 0, "metadata": { "isfolder": true, "contents": [] } }""");
        _http.OnJson(HttpMethod.Post, "/uploadfile", Ok);
        using var provider = new PCloudProvider(_accountId, _store, _http);

        await provider.ListDirectoryAsync("/");
        await provider.UploadFileAsync(TempFile(3), "/a.bin");

        Assert.Equal("https://api.pcloud.com/listfolder?access_token=OAUTH&path=%2F&timeformat=timestamp", Call("listfolder").Uri.AbsoluteUri);
        var upload = Query(Call("uploadfile"));
        Assert.Equal("OAUTH", upload["access_token"]);
        Assert.Null(upload["auth"]);
    }

    /// <summary>
    /// Accounts added with a password or a pasted pcauth cookie have no isOAuth field: they
    /// keep sending auth, and their stored JSON comes back exactly as it was.
    /// </summary>
    [Fact]
    public async Task AnAccountSavedBeforeKeepsSendingAuth()
    {
        const string legacy = """{"accessToken":"PCAUTH","hostname":"api.pcloud.com","userId":42}""";
        _store.Save(Key, Encoding.UTF8.GetBytes(legacy));
        _http.OnJson(HttpMethod.Get, "/listfolder", """{ "result": 0, "metadata": { "isfolder": true, "contents": [] } }""");
        using var provider = new PCloudProvider(_accountId, _store, _http);

        await provider.ListDirectoryAsync("/");

        var query = Query(Call("listfolder"));
        Assert.Equal("PCAUTH", query["auth"]);
        Assert.Null(query["access_token"]);

        var credentials = _store.Load<PCloudCredentials>(Key)!;
        Assert.Null(credentials.IsOAuth);
        var other = new MemoryCredentialStore();
        other.Save(Key, credentials);
        Assert.Equal(legacy, Encoding.UTF8.GetString(other.Load(Key)!));
    }

    [Fact]
    public async Task DisconnectForgetsTheToken()
    {
        using var provider = Provider();

        await provider.DisconnectAsync();

        Assert.False(provider.IsAuthenticated);
        Assert.Null(Stored());
    }

    [Fact]
    public void StoredCredentialsUseTheMacOSFieldNames()
    {
        Assert.Equal(
            """{"accessToken":"t","hostname":"api.pcloud.com","userId":7}""",
            JsonSerializer.Serialize(new PCloudCredentials("t", "api.pcloud.com", 7)));
        Assert.Equal(
            """{"accessToken":"t","hostname":"api.pcloud.com","userId":7,"isOAuth":true}""",
            JsonSerializer.Serialize(new PCloudCredentials("t", "api.pcloud.com", 7, IsOAuth: true)));
    }
}

/// <summary>pCloud's token answer without a token, in the app's own words — English here, so it shares the language switch's collection.</summary>
[Collection("Localization")]
public sealed class PCloudTokenAnswerTextTests
{
    [Theory]
    [InlineData("""{ "result": 2012 }""")]
    [InlineData("""{ "result": 0 }""")]
    [InlineData("""{ "result": 0, "access_token": "" }""")]
    [InlineData("""{ "access_token": "t" }""")]
    public void NoTokenSaysSo(string json)
    {
        Localization.Use(AppLanguage.English);

        var error = Assert.Throws<CloudProviderException>(() => PCloudApiClient.ParseTokenResponse(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("pCloud didn't issue a token.", error.Message);
    }
}
