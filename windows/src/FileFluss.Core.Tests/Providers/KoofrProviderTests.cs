// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Koofr;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;
using static FileFluss.Core.Tests.Providers.WebDavFamily.WebDavFamilyTestSupport;

namespace FileFluss.Core.Tests.Providers.WebDavFamily;

public sealed class KoofrProviderTests : IDisposable
{
    private const string Api = "https://app.koofr.net/api/v2";
    private const string Mount = "/mounts/m-primary";

    private readonly DavTempFolder _temp = new();
    private readonly FakeHttpHandler _http = new();
    private readonly MemoryCredentialStore _store = new();
    private readonly Guid _id = Guid.NewGuid();

    public void Dispose() => _temp.Dispose();

    private KoofrProvider Connected()
    {
        _store.Save($"koofr.{_id}", new KoofrCredentials("ana@example.com", "app-pw", "m-primary", "Ana Novak"));
        return new KoofrProvider(_id, _store, _http);
    }

    private static string AuthHeader => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ana@example.com:app-pw"));

    // MARK: - Connect

    [Fact]
    public async Task RequiresAuthentication()
    {
        using var provider = new KoofrProvider(_id, _store, _http);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
        Assert.Null(await provider.StorageQuotaAsync());
    }

    [Fact]
    public async Task ConnectPicksThePrimaryMountAndTheFullName()
    {
        _http.OnJson(HttpMethod.Get, Api + "/user", """{"id":"u1","firstName":"Ana","lastName":"Novak","email":"ana@example.com"}""");
        _http.OnJson(HttpMethod.Get, Api + "/mounts", """
            {"mounts":[{"id":"m-dropbox","name":"Dropbox","isPrimary":false},{"id":"m-primary","name":"Koofr","isPrimary":true}]}
            """);
        using var provider = new KoofrProvider(_id, _store, _http);

        var result = await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["email"] = "ana@example.com", ["appPassword"] = "app-pw" }),
            new ScriptedConnectPrompt());

        Assert.Equal("Ana Novak", result.AccountLabel);
        var stored = _store.Load<KoofrCredentials>($"koofr.{_id}")!;
        Assert.Equal(("ana@example.com", "app-pw", "m-primary", "Ana Novak"), (stored.Email, stored.AppPassword, stored.PrimaryMountId, stored.DisplayName));
        Assert.All(_http.Requests, r => Assert.Equal(AuthHeader, r.Header("Authorization")));
        Assert.Equal(new Dictionary<string, string> { ["email"] = "ana@example.com" }, provider.GetEditableConnectValues());
    }

    [Fact]
    public async Task ConnectWithoutANameUsesTheEmail()
    {
        _http.OnJson(HttpMethod.Get, Api + "/user", """{"id":"u1","firstName":"","lastName":"","email":"ana@example.com"}""");
        _http.OnJson(HttpMethod.Get, Api + "/mounts", """{"mounts":[{"id":"only","name":"Koofr","isPrimary":false}]}""");
        using var provider = new KoofrProvider(_id, _store, _http);

        var result = await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["email"] = "ana@example.com", ["appPassword"] = "app-pw" }),
            new ScriptedConnectPrompt());

        Assert.Equal("ana@example.com", result.AccountLabel);
        Assert.Equal("only", _store.Load<KoofrCredentials>($"koofr.{_id}")!.PrimaryMountId);
    }

    [Fact]
    public async Task WrongPasswordLeavesStoredCredentialsUntouched()
    {
        using var provider = Connected();
        _http.On(HttpMethod.Get, Api + "/user", (_, _) => Status(HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["email"] = "ana@example.com", ["appPassword"] = "wrong" }),
            new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal("app-pw", _store.Load<KoofrCredentials>($"koofr.{_id}")!.AppPassword);
    }

    [Fact]
    public void ConnectFormMatchesTheMacSheet()
    {
        using var provider = new KoofrProvider(_id, _store, _http);
        var fields = provider.ConnectSpec.Modes.Single().Fields;
        Assert.StartsWith("Create an app password at koofr.net", fields[0].Label);
        Assert.Equal(["email", "appPassword"], fields.Where(f => f.HasValue).Select(f => f.Key));
    }

    // MARK: - Files

    [Fact]
    public async Task ListingEncodesThePathAndParsesMillisecondDates()
    {
        _http.OnJson(HttpMethod.Get, Mount + "/files/list", """
            {"files":[
              {"name":"Sub","type":"dir","modified":1736244930000,"size":0,"contentType":""},
              {"name":"notes & ideas.txt","type":"file","modified":1736244930123,"size":512,"contentType":"text/plain","hash":"abc123"}
            ]}
            """);
        using var provider = Connected();

        var items = await provider.ListDirectoryAsync("/Docs & Notes");

        Assert.Equal(Api + Mount + "/files/list?path=%2FDocs%20%26%20Notes", Assert.Single(_http.Requests).Uri.AbsoluteUri);
        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsDirectory);
        Assert.Equal("/Docs & Notes/Sub", items[0].Path);
        var file = items[1];
        Assert.Equal(("/Docs & Notes/notes & ideas.txt", 512L, "abc123"), (file.Path, file.Size, file.Checksum));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1736244930123), file.ModificationDate);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CloudErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, CloudErrorKind.NotFound)]
    [InlineData(HttpStatusCode.InsufficientStorage, CloudErrorKind.QuotaExceeded)]
    public async Task StatusMapping(HttpStatusCode status, CloudErrorKind expected)
    {
        _http.On(HttpMethod.Get, Mount + "/files/list", (_, _) => Status(status, """{"error":{"code":"x","message":"nope"}}"""));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/x"));
        Assert.Equal(expected, error.Kind);
    }

    [Fact]
    public async Task DownloadComesFromTheContentHost()
    {
        _http.OnBytes(HttpMethod.Get, "https://app.koofr.net/content/api/v2/mounts/m-primary/files/get", Encoding.UTF8.GetBytes("hello"));
        using var provider = Connected();
        var target = Path.Combine(_temp.Path, "a.txt");
        long reported = 0;

        await provider.DownloadFileAsync("/Docs/a.txt", target, d => reported += d);

        Assert.Equal("hello", await File.ReadAllTextAsync(target));
        Assert.Equal(5, reported);
        Assert.EndsWith("files/get?path=%2FDocs%2Fa.txt", Assert.Single(_http.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task UploadIsAMultipartPutWithTheModificationTime()
    {
        var modified = new DateTime(2025, 1, 7, 10, 15, 30, 250, DateTimeKind.Utc);
        var local = _temp.File("report 1.txt", 5000, modified);
        _http.OnJson(HttpMethod.Post, "/content/api/v2/mounts/m-primary/files/put", """{"name":"report 1.txt"}""");
        using var provider = Connected();
        long reported = 0;

        await provider.UploadFileAsync(local, "/Docs/report 1.txt", d => reported += d);

        var request = Assert.Single(_http.Requests);
        Assert.Equal(
            "https://app.koofr.net/content/api/v2/mounts/m-primary/files/put?path=%2FDocs&filename=report%201.txt&autorename=false&overwrite=true&modified=1736244930250",
            request.Uri.AbsoluteUri);
        Assert.StartsWith("multipart/form-data", request.Header("Content-Type"));
        Assert.Matches("name=\"?file\"?;", request.Body);
        Assert.Contains("abcdefghijklmnopqrstuvwxyz", request.Body);
        Assert.Equal(5000, reported);
    }

    [Fact]
    public async Task CreateFolderIsIdempotent()
    {
        _http.OnJson(HttpMethod.Get, "Existing", """{"name":"Existing","type":"dir","modified":0,"size":0}""");
        _http.On(HttpMethod.Get, Mount + "/files/info", (_, _) => Status(HttpStatusCode.NotFound));
        _http.On(HttpMethod.Post, Mount + "/files/folder", (_, _) => Status(HttpStatusCode.OK, "{}"));
        using var provider = Connected();

        await provider.CreateDirectoryAsync("/Docs/Existing");
        Assert.Empty(_http.All(HttpMethod.Post));

        await provider.CreateDirectoryAsync("/Docs/New One");
        var create = Assert.Single(_http.All(HttpMethod.Post));
        Assert.EndsWith("/files/folder?path=%2FDocs", create.Uri.AbsoluteUri);
        Assert.Equal("""{"name":"New One"}""", create.Body);
    }

    [Fact]
    public async Task RenameAndDelete()
    {
        _http.On(HttpMethod.Put, Mount + "/files/rename", (_, _) => Status(HttpStatusCode.OK));
        _http.On(HttpMethod.Delete, Mount + "/files/remove", (_, _) => Status(HttpStatusCode.OK));
        using var provider = Connected();

        await provider.RenameItemAsync("/Docs/a.txt", "b.txt");
        await provider.DeleteItemAsync("/Docs/b.txt");

        var rename = _http.Single(HttpMethod.Put, "/files/rename");
        Assert.EndsWith("rename?path=%2FDocs%2Fa.txt", rename.Uri.AbsoluteUri);
        Assert.Equal("""{"name":"b.txt"}""", rename.Body);
        Assert.EndsWith("remove?path=%2FDocs%2Fb.txt", _http.Single(HttpMethod.Delete, "/files/remove").Uri.AbsoluteUri);
    }

    [Fact]
    public async Task NoServerSideMoveOrCopy()
    {
        using var provider = Connected();
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.MoveItemAsync("/a", "/b"));
        Assert.Equal(CloudErrorKind.NotImplemented, error.Kind);
        Assert.Null(await provider.SearchFilesAsync("x", null));
    }

    [Fact]
    public async Task QuotaFromThePrimaryMount()
    {
        _http.OnJson(HttpMethod.Get, Api + Mount, """{"id":"m-primary","spaceTotal":10737418240,"spaceUsed":1073741824}""");
        using var provider = Connected();

        var quota = await provider.StorageQuotaAsync();

        Assert.Equal((1_073_741_824L, (long?)10_737_418_240), (quota!.UsedBytes, quota.TotalBytes));
    }

    // MARK: - Share links

    [Fact]
    public async Task CreateLinkForARootFileOffersTheDirectContentUrl()
    {
        _http.OnJson(HttpMethod.Post, Mount + "/links", """
            {"id":"lnk1","path":"/a.txt","url":"https://app.koofr.net/links/abc-123","shortUrl":"https://k00.fr/abc","hasPassword":false,"validTo":0}
            """);
        _http.OnJson(HttpMethod.Get, Mount + "/files/info", """{"name":"a.txt","type":"file","modified":0,"size":3}""");
        using var provider = Connected();

        var link = await provider.CreateShareLinkAsync("/a.txt", new ShareLinkOptions { Password = "ignored" });

        Assert.Equal(new Uri("https://app.koofr.net/links/abc-123"), link.Url);
        Assert.Equal("https://app.koofr.net/content/links/abc-123/files/get?path=%2F", link.DirectDownloadUrl!.AbsoluteUri);
        Assert.False(link.HasPassword);
        Assert.Null(link.ExpiresAt);
        Assert.Equal("""{"path":"/a.txt"}""", _http.Single(HttpMethod.Post, "/links").Body);
    }

    [Fact]
    public async Task NestedItemsGetOnlyTheLandingPage()
    {
        _http.OnJson(HttpMethod.Post, Mount + "/links", """
            {"id":"lnk2","path":"/Docs/a.txt","url":"https://app.koofr.net/links/def","hasPassword":true,"validTo":1767225600000}
            """);
        using var provider = Connected();

        var link = await provider.CreateShareLinkAsync("/Docs/a.txt", ShareLinkOptions.Default);

        Assert.Null(link.DirectDownloadUrl);
        Assert.True(link.HasPassword);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1767225600000), link.ExpiresAt);
        Assert.Single(_http.Requests); // no info lookup below the root
    }

    [Fact]
    public async Task ExistingLinkIsMatchedOnTheNormalisedPath()
    {
        _http.OnJson(HttpMethod.Get, Mount + "/links", """
            {"links":[{"id":"x","path":"/Other","url":"https://app.koofr.net/links/x"},{"id":"y","path":"Docs/Report/","url":"https://app.koofr.net/links/y"}]}
            """);
        using var provider = Connected();

        var link = await provider.ExistingShareLinkAsync("/Docs/Report");
        Assert.Equal(new Uri("https://app.koofr.net/links/y"), link!.Url);
        Assert.Null(await provider.ExistingShareLinkAsync("/Nope"));
    }

    [Fact]
    public async Task BareArrayLinkListIsAcceptedButGarbageIsAnError()
    {
        Assert.Single(KoofrApiClient.ParseLinks("""[{"id":"a","path":"/a","url":"https://app.koofr.net/links/a"}]"""));
        Assert.Throws<CloudProviderException>(() => KoofrApiClient.ParseLinks("""{"unexpected":true}"""));

        _http.OnText(HttpMethod.Get, Mount + "/links", "<html>oops</html>");
        using var provider = Connected();
        await Assert.ThrowsAsync<CloudProviderException>(() => provider.ExistingShareLinkAsync("/a"));
    }

    [Fact]
    public async Task RemoveDeletesTheMatchingLinkOnly()
    {
        _http.OnJson(HttpMethod.Get, Mount + "/links", """{"links":[{"id":"y 1","path":"/a.txt","url":"https://app.koofr.net/links/y"}]}""");
        _http.On(HttpMethod.Delete, Mount + "/links/", (_, _) => Status(HttpStatusCode.OK));
        using var provider = Connected();

        await provider.RemoveShareLinkAsync("/a.txt");
        await provider.RemoveShareLinkAsync("/b.txt");

        var delete = Assert.Single(_http.All(HttpMethod.Delete));
        Assert.EndsWith("/links/y%201", delete.Uri.AbsoluteUri);
        Assert.True(provider.ShareLinkCapabilities is { CanCreate: true, CanQueryExisting: true, CanUpdate: false, CanRemove: true, HasOptions: false });
    }

    [Fact]
    public async Task SharingRefusalSurfacesKoofrsMessage()
    {
        _http.On(HttpMethod.Post, Mount + "/links", (_, _) => Status(HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"Link sharing is not available on your plan"}}"""));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/a.txt", ShareLinkOptions.Default));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("Link sharing is not available on your plan", error.Message);
    }

    [Theory]
    [InlineData("""{"message":"top"}""", "top")]
    [InlineData("""{"error":"flat"}""", "flat")]
    [InlineData("""{"error":{"message":"nested"}}""", "nested")]
    [InlineData("""{"error":{"code":"Only_Code"}}""", "Only_Code")]
    [InlineData("""{"error":{}}""", null)]
    [InlineData("not json", null)]
    public void ServerMessageShapes(string body, string? expected)
        => Assert.Equal(expected, KoofrApiClient.ServerMessage(body));
}
