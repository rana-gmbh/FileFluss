// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.NextCloud;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;
using static FileFluss.Core.Tests.Providers.WebDavFamily.WebDavFamilyTestSupport;

namespace FileFluss.Core.Tests.Providers.WebDavFamily;

public sealed class NextCloudProviderTests
{
    private const string Server = "https://cloud.example.com";
    private const string SharesApi = "/ocs/v2.php/apps/files_sharing/api/v1/shares";

    private readonly FakeHttpHandler _http = new();
    private readonly MemoryCredentialStore _store = new();
    private readonly Guid _id = Guid.NewGuid();

    private NextCloudProvider Connected(string? userId = "jane.doe")
    {
        _store.Save($"nextcloud.{_id}", new NextCloudCredentials(Server, "jane@example.com", "app-pw", "Jane Doe", userId));
        return new NextCloudProvider(_id, _store, _http);
    }

    private static string Ocs(int statusCode, string data, string message = "OK")
    {
        var status = statusCode is 100 or 200 ? "ok" : "failure";
        return "{\"ocs\":{\"meta\":{\"status\":\"" + status + "\",\"statuscode\":" + statusCode + ",\"message\":\"" + message + "\"},\"data\":" + data + "}}";
    }

    // MARK: - Sign-in

    [Fact]
    public async Task RequiresAuthentication()
    {
        using var provider = new NextCloudProvider(_id, _store, _http);
        Assert.False(provider.IsAuthenticated);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
        await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/a", ShareLinkOptions.Default));
    }

    [Fact]
    public async Task AppPasswordSignInReadsNameAndUserIdFromOcs()
    {
        _http.OnJson(HttpMethod.Get, "/ocs/v1.php/cloud/user", Ocs(100, """{"id":"jane.doe","display-name":"Jane Doe","email":"jane@example.com"}"""));
        using var provider = new NextCloudProvider(_id, _store, _http);

        var result = await provider.ConnectAsync(
            new ConnectValues("appPassword", new Dictionary<string, string> { ["serverUrl"] = "cloud.example.com/", ["username"] = "jane@example.com", ["appPassword"] = "app-pw" }),
            new ScriptedConnectPrompt());

        Assert.Equal("Jane Doe", result.AccountLabel);
        var stored = _store.Load<NextCloudCredentials>($"nextcloud.{_id}")!;
        Assert.Equal((Server, "jane@example.com", "app-pw", "jane.doe"), (stored.ServerUrl, stored.Username, stored.AppPassword, stored.UserId));

        var request = Assert.Single(_http.Requests);
        Assert.Equal("true", request.Header("OCS-APIRequest"));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("jane@example.com:app-pw")), request.Header("Authorization"));

        // Files live under the user id, not the login name used to sign in.
        _http.On(Propfind, "/remote.php/dav/files/jane.doe/", (_, _) => Multistatus(WebDavParserTests.NextcloudListing));
        var items = await provider.ListDirectoryAsync("/Documents");
        Assert.Equal(3, items.Count);
        Assert.Equal("https://cloud.example.com/remote.php/dav/files/jane.doe/Documents", _http.Requests.Last().Uri.AbsoluteUri);
        Assert.Contains("<oc:size/>", _http.Requests.Last().Body);
    }

    [Fact]
    public async Task WrongAppPasswordLeavesStoredCredentialsUntouched()
    {
        using var provider = Connected();
        _http.On(HttpMethod.Get, "/ocs/v1.php/cloud/user", (_, _) => Status(HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(
            new ConnectValues("appPassword", new Dictionary<string, string> { ["serverUrl"] = Server, ["username"] = "jane", ["appPassword"] = "nope" }),
            new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal("app-pw", _store.Load<NextCloudCredentials>($"nextcloud.{_id}")!.AppPassword);
        Assert.True(provider.IsAuthenticated);
    }

    [Fact]
    public async Task LoginFlowV2OpensTheBrowserAndPollsUntilTheAppPasswordArrives()
    {
        _http.OnJson(HttpMethod.Post, "/index.php/login/v2", """
            {"poll":{"token":"poll-token-123","endpoint":"https://cloud.example.com/login/v2/poll"},"login":"https://cloud.example.com/login/v2/flow/abc"}
            """);
        var polls = 0;
        _http.On(HttpMethod.Post, "/login/v2/poll", (_, _) => ++polls < 3
            ? Status(HttpStatusCode.NotFound)
            : FakeHttpHandler.Json("""{"server":"https://cloud.example.com/","loginName":"jane","appPassword":"minted-app-pw"}"""));
        _http.OnText(HttpMethod.Get, "/ocs/v1.php/cloud/user", """
            <?xml version="1.0"?>
            <ocs><meta><status>ok</status><statuscode>100</statuscode></meta>
              <data><enabled>1</enabled><id>jane</id><quota><free>1</free><used>2</used></quota><display-name>Jane D.</display-name></data></ocs>
            """, mediaType: "text/xml");

        using var provider = new NextCloudProvider(_id, _store, _http) { LoginPollInterval = TimeSpan.FromMilliseconds(1) };
        var opened = new List<Uri>();
        provider.OpenBrowser = opened.Add;
        var prompt = new ScriptedConnectPrompt();

        var result = await provider.ConnectAsync(
            new ConnectValues("browser", new Dictionary<string, string> { ["serverUrl"] = "https://cloud.example.com" }),
            prompt);

        Assert.Equal("Jane D.", result.AccountLabel);
        Assert.Equal(new Uri("https://cloud.example.com/login/v2/flow/abc"), Assert.Single(opened));
        Assert.Equal(3, polls);
        Assert.Null(prompt.StatusLines.Last());
        Assert.NotNull(prompt.StatusLines.First());

        var init = _http.Single(HttpMethod.Post, "/index.php/login/v2");
        Assert.Equal("FileFluss", init.Header("User-Agent"));
        Assert.Contains("application/json", init.Header("Accept"));
        var poll = _http.Requests.First(r => r.Uri.AbsolutePath == "/login/v2/poll");
        Assert.Equal("token=poll-token-123", poll.Body);

        var stored = _store.Load<NextCloudCredentials>($"nextcloud.{_id}")!;
        Assert.Equal((Server, "jane", "minted-app-pw", "Jane D.", "jane"), (stored.ServerUrl, stored.Username, stored.AppPassword, stored.DisplayName, stored.UserId));
    }

    [Fact]
    public async Task LoginFlowNotOfferedByTheServer()
    {
        _http.On(HttpMethod.Post, "/index.php/login/v2", (_, _) => Status(HttpStatusCode.NotFound));
        using var provider = new NextCloudProvider(_id, _store, _http);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(
            new ConnectValues("browser", new Dictionary<string, string> { ["serverUrl"] = Server }),
            new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Null(_store.Load($"nextcloud.{_id}"));
    }

    [Fact]
    public async Task LoginFlowGivesUpAtTheDeadline()
    {
        _http.OnJson(HttpMethod.Post, "/index.php/login/v2", """{"poll":{"token":"t","endpoint":"https://cloud.example.com/login/v2/poll"},"login":"https://cloud.example.com/login/v2/flow/x"}""");
        _http.On(HttpMethod.Post, "/login/v2/poll", (_, _) => Status(HttpStatusCode.NotFound));
        using var provider = new NextCloudProvider(_id, _store, _http)
        {
            LoginPollInterval = TimeSpan.FromMilliseconds(1),
            LoginDeadline = TimeSpan.FromMilliseconds(50),
            OpenBrowser = _ => { },
        };

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(
            new ConnectValues("browser", new Dictionary<string, string> { ["serverUrl"] = Server }),
            new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.False(provider.IsAuthenticated);
    }

    [Fact]
    public void ConnectFormOffersBrowserAndAppPasswordModes()
    {
        using var adding = new NextCloudProvider(_id, _store, _http);
        var modes = adding.ConnectSpec.Modes;
        Assert.Equal(["browser", "appPassword"], modes.Select(m => m.Id));
        Assert.Equal(["Browser Login", "App Password"], modes.Select(m => m.Title));
        Assert.True(modes[0].OpensBrowser);
        Assert.Equal(["serverUrl"], modes[0].Fields.Where(f => f.HasValue).Select(f => f.Key));
        Assert.Equal(["serverUrl", "username", "appPassword"], modes[1].Fields.Where(f => f.HasValue).Select(f => f.Key));
        Assert.False(adding.ConnectSpec.SupportsInlineReauthentication);

        using var editing = Connected();
        Assert.StartsWith("Click Re-authorize", editing.ConnectSpec.Modes[0].Fields[1].Label);
        Assert.Equal(new Dictionary<string, string> { ["serverUrl"] = Server, ["username"] = "jane@example.com" }, editing.GetEditableConnectValues());
    }

    // MARK: - Files

    [Fact]
    public async Task CredentialsWithoutAUserIdFallBackToTheLoginName()
    {
        using var provider = Connected(userId: null);
        _http.On(Propfind, "/remote.php/dav/files/", (_, _) => Multistatus("<d:multistatus xmlns:d=\"DAV:\"/>"));

        await provider.ListDirectoryAsync("/");

        Assert.Equal("https://cloud.example.com/remote.php/dav/files/jane%40example.com/", Assert.Single(_http.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task MkcolIsSingleAnd405MeansItExists()
    {
        _http.On(Mkcol, "/remote.php/dav/files/jane.doe/Existing", (_, _) => Status(HttpStatusCode.MethodNotAllowed));
        using var provider = Connected();

        await provider.CreateDirectoryAsync("/Existing");

        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task UploadSendsMtimeAndMapsSizeErrors()
    {
        using var temp = new DavTempFolder();
        var local = temp.File("a.bin", 1000, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        _http.On(HttpMethod.Put, "/remote.php/dav/files/jane.doe/a.bin", (_, _) => Status(HttpStatusCode.RequestEntityTooLarge));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(local, "/a.bin"));

        Assert.Equal(CloudErrorKind.FileTooLarge, error.Kind);
        Assert.Equal(1000, error.FileBytes);
        Assert.Equal("1704164645", Assert.Single(_http.Requests).Header("X-OC-Mtime"));
        Assert.Null(provider.MaxUploadFileSize);
    }

    [Fact]
    public async Task SearchUsesDavSearchWithAnEscapedQuery()
    {
        _http.On(Search, "/remote.php/dav/files/jane.doe/Documents", (_, _) => Multistatus(WebDavParserTests.NextcloudListing));
        using var provider = Connected();

        var hits = await provider.SearchFilesAsync("Plan & <Budget>", "/Documents");

        var request = Assert.Single(_http.Requests);
        Assert.Contains("<d:literal>%Plan &amp; &lt;Budget&gt;%</d:literal>", request.Body);
        Assert.Contains("<d:href>https://cloud.example.com/remote.php/dav/files/jane.doe/Documents</d:href>", request.Body);
        Assert.Contains("<d:depth>infinity</d:depth>", request.Body);

        // Search results carry their own paths (anywhere below the scope).
        Assert.Contains(hits!, h => h.Path == "/Documents/Projekt Übersicht & Plan.pdf");
        Assert.Contains(hits!, h => h.Path == "/Documents" && h.IsDirectory);
    }

    [Fact]
    public async Task ModificationDateViaProppatch()
    {
        _http.On(Proppatch, "/remote.php/dav/files/jane.doe/a.txt", (_, _) => Multistatus("<d:multistatus xmlns:d=\"DAV:\"/>"));
        using var provider = Connected();

        await provider.SetModificationDateAsync("/a.txt", new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero));

        Assert.Contains("Sat, 01 Mar 2025 12:00:00 GMT", Assert.Single(_http.Requests).Body);
    }

    // MARK: - Quota

    [Fact]
    public async Task QuotaFromTheOcsUserEndpoint()
    {
        _http.OnText(HttpMethod.Get, "/ocs/v1.php/cloud/user", """
            <?xml version="1.0"?>
            <ocs>
             <meta><status>ok</status><statuscode>100</statuscode><message>OK</message></meta>
             <data>
              <id>jane.doe</id>
              <quota><free>4000</free><used>1000</used><total>5000</total><relative>20</relative><quota>5000</quota></quota>
              <display-name>Jane Doe</display-name>
             </data>
            </ocs>
            """, mediaType: "text/xml");
        using var provider = Connected();

        var quota = await provider.StorageQuotaAsync();

        Assert.Equal((1000L, (long?)5000), (quota!.UsedBytes, quota.TotalBytes));
        Assert.Contains("application/xml", Assert.Single(_http.Requests).Header("Accept"));
    }

    [Theory]
    [InlineData("<ocs><data><quota><used>7</used><total>-3</total><quota>-3</quota></quota></data></ocs>", 7L, null)]
    [InlineData("<ocs><data><total>99</total><quota><used>7</used></quota></data></ocs>", 7L, null)]
    [InlineData("""{"ocs":{"meta":{"statuscode":100},"data":{"quota":{"used":11,"total":22}}}}""", 11L, 22L)]
    public void QuotaParsing(string body, long used, long? total)
    {
        var quota = NextCloudApi.ParseQuota(body);
        Assert.Equal((used, total), (quota!.UsedBytes, quota.TotalBytes));
    }

    // MARK: - Share links

    [Fact]
    public async Task CreateShareLinkSendsTheOcsForm()
    {
        _http.OnJson(HttpMethod.Post, SharesApi, Ocs(200, """{"id":"17","share_type":3,"url":"https://cloud.example.com/s/AbC123","token":"AbC123","expiration":"2026-12-31 00:00:00"}"""));
        using var provider = Connected();

        var link = await provider.CreateShareLinkAsync("/Docs/a b.txt", new ShareLinkOptions
        {
            Password = "pw&1",
            Expiry = new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.Zero),
            AllowDownload = false,
        });

        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://cloud.example.com/ocs/v2.php/apps/files_sharing/api/v1/shares?format=json", request.Uri.AbsoluteUri);
        Assert.Equal("true", request.Header("OCS-APIRequest"));
        Assert.Equal("application/x-www-form-urlencoded", request.Header("Content-Type"));
        Assert.Equal(
            "path=%2FDocs%2Fa%20b.txt&shareType=3&password=pw%261&expireDate=2026-12-31&attributes=" + Uri.EscapeDataString("""[{"scope":"permissions","key":"download","value":false}]"""),
            request.Body);

        Assert.Equal(new Uri("https://cloud.example.com/s/AbC123"), link.Url);
        Assert.Null(link.DirectDownloadUrl); // download switched off
        Assert.True(link.HasPassword);
        Assert.Equal(new DateTime(2026, 12, 31), link.ExpiresAt!.Value.DateTime);
    }

    [Fact]
    public async Task CreateShareLinkOffersTheDirectDownloadSuffix()
    {
        _http.OnJson(HttpMethod.Post, SharesApi, Ocs(100, """{"id":18,"share_type":"3","url":"https://cloud.example.com/s/XyZ","expiration":null}"""));
        using var provider = Connected();

        var link = await provider.CreateShareLinkAsync("/a.txt", ShareLinkOptions.Default);

        Assert.Equal(new Uri("https://cloud.example.com/s/XyZ/download"), link.DirectDownloadUrl);
        Assert.Null(link.ExpiresAt);
        Assert.False(link.HasPassword);
        Assert.Equal("path=%2Fa.txt&shareType=3", Assert.Single(_http.Requests).Body);
    }

    [Fact]
    public async Task OcsFailureSurfacesTheServerMessage()
    {
        _http.On(HttpMethod.Post, SharesApi, (_, _) => FakeHttpHandler.Json(Ocs(403, "[]", "Public link sharing is disabled by the administrator"), HttpStatusCode.Forbidden));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/a.txt", ShareLinkOptions.Default));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("Public link sharing is disabled by the administrator", error.Message);
    }

    [Fact]
    public async Task ExistingShareLinkPicksThePublicLinkAmongOtherShares()
    {
        _http.OnJson(HttpMethod.Get, SharesApi, Ocs(100, """
            [
              {"id":"5","share_type":0,"share_with":"bob","url":null},
              {"id":42,"share_type":"3","url":"https://cloud.example.com/s/Link42","password":"$2y$hash","permissions":"17","attributes":null,"expiration":""}
            ]
            """));
        using var provider = Connected();

        var link = await provider.ExistingShareLinkAsync("/Docs");

        Assert.Equal(new Uri("https://cloud.example.com/s/Link42"), link!.Url);
        Assert.Equal(new Uri("https://cloud.example.com/s/Link42/download"), link.DirectDownloadUrl);
        Assert.True(link.HasPassword);
        Assert.Null(link.ExpiresAt);
        Assert.Equal(
            "https://cloud.example.com/ocs/v2.php/apps/files_sharing/api/v1/shares?path=%2FDocs&reshares=false&format=json",
            Assert.Single(_http.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task UnsharedFileHasNoLink()
    {
        _http.OnJson(HttpMethod.Get, SharesApi, Ocs(200, "[]"));
        using var provider = Connected();
        Assert.Null(await provider.ExistingShareLinkAsync("/a.txt"));
    }

    [Fact]
    public async Task UpdateSendsEveryFieldAndKeepsPermissions()
    {
        var lookups = 0;
        _http.On(HttpMethod.Get, SharesApi, (_, _) =>
        {
            lookups++;
            var attributes = lookups == 1 ? "\"[{\\\"scope\\\":\\\"permissions\\\",\\\"key\\\":\\\"download\\\",\\\"value\\\":false}]\"" : "null";
            return FakeHttpHandler.Json(Ocs(100, "[{\"id\":\"42\",\"share_type\":3,\"url\":\"https://cloud.example.com/s/Link42\",\"permissions\":15,\"attributes\":" + attributes + "}]"));
        });
        _http.OnJson(HttpMethod.Put, SharesApi + "/42", Ocs(100, "[]"));
        using var provider = Connected();

        var link = await provider.UpdateShareLinkAsync("/Docs", new ShareLinkOptions { AllowDownload = true });

        var put = _http.Single(HttpMethod.Put, SharesApi);
        Assert.Equal("https://cloud.example.com/ocs/v2.php/apps/files_sharing/api/v1/shares/42?format=json", put.Uri.AbsoluteUri);

        // Empty values clear password and expiry; the folder's bitmask (15) survives; the
        // download attribute is switched back on because the link had it off.
        Assert.Equal(
            "password=&expireDate=&permissions=15&attributes=" + Uri.EscapeDataString("""[{"scope":"permissions","key":"download","value":true}]"""),
            put.Body);

        // PUT answered with empty data → the share was read back.
        Assert.Equal(2, lookups);
        Assert.Equal(new Uri("https://cloud.example.com/s/Link42/download"), link.DirectDownloadUrl);
    }

    [Fact]
    public async Task UpdateWithoutALinkSaysSo()
    {
        _http.OnJson(HttpMethod.Get, SharesApi, Ocs(100, "[]"));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.UpdateShareLinkAsync("/a.txt", ShareLinkOptions.Default));
        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
    }

    [Fact]
    public async Task RemoveDeletesOnlyAnExistingLink()
    {
        var shared = true;
        _http.On(HttpMethod.Get, SharesApi, (_, _) => FakeHttpHandler.Json(Ocs(100, shared ? """[{"id":"9","share_type":3,"url":"https://cloud.example.com/s/x"}]""" : "[]")));
        _http.OnJson(HttpMethod.Delete, SharesApi + "/9", Ocs(200, "[]"));
        using var provider = Connected();

        await provider.RemoveShareLinkAsync("/a.txt");
        shared = false;
        await provider.RemoveShareLinkAsync("/a.txt");

        Assert.Single(_http.All(HttpMethod.Delete));
        Assert.True(provider.ShareLinkCapabilities is { CanCreate: true, CanUpdate: true, SupportsPassword: true, SupportsExpiry: true, SupportsDownloadToggle: true });
    }

    [Theory]
    [InlineData("""[{"scope":"permissions","key":"download","value":false}]""", true)]
    [InlineData("""[{"scope": "permissions", "key": "download", "value": false}]""", true)]
    [InlineData("""[{"scope":"permissions","key":"download","value":true}]""", false)]
    [InlineData(null, false)]
    public void DownloadAttributeDetection(string? attributes, bool disabled)
        => Assert.Equal(disabled, NextCloudApi.DownloadDisabled(attributes));
}
