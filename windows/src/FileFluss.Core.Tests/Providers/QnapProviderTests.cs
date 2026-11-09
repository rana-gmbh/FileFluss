// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Qnap;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>QNAP File Station (port of the macOS NASProviderTests' QNAP part, plus the Windows plumbing).</summary>
public sealed class QnapProviderTests : IDisposable
{
    private static readonly Guid AccountId = Guid.Parse("8d0c3f52-4a5e-4c9b-9a1e-0f5b8c2d7e61");

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ff-qnap-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttpHandler _http = new();
    private readonly MemoryCredentialStore _store = new();
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, string?, HttpResponseMessage>> _functions = new();
    private Func<System.Collections.Specialized.NameValueCollection, string> _login;
    private int _logins;

    public QnapProviderTests()
    {
        Directory.CreateDirectory(_temp);
        _login = _ => LoginOk($"sid-{Interlocked.Increment(ref _logins)}");
        _http.On(HttpMethod.Post, "/cgi-bin/authLogin.cgi", (_, body) => Xml(_login(HttpUtility.ParseQueryString(body ?? string.Empty))));
        _http.On(HttpMethod.Get, "/cgi-bin/filemanager/utilRequest.cgi", Dispatch);
        _http.On(HttpMethod.Post, "/cgi-bin/filemanager/utilRequest.cgi", Dispatch);
        _http.On(HttpMethod.Get, "/cgi-bin/authLogout.cgi", (_, _) => Xml("<QDocRoot/>"));
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static string LoginOk(string sid, string extra = "")
        => $"<QDocRoot version=\"1.0\"><authPassed><![CDATA[1]]></authPassed><authSid><![CDATA[{sid}]]></authSid>{extra}</QDocRoot>";

    private static string LoginRefused(string extra = "")
        => $"<QDocRoot version=\"1.0\"><authPassed><![CDATA[0]]></authPassed>{extra}</QDocRoot>";

    private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

    private static string? Param(Uri uri, string name) => HttpUtility.ParseQueryString(uri.Query)[name];

    private HttpResponseMessage Dispatch(HttpRequestMessage request, string? body)
        => _functions.TryGetValue(Param(request.RequestUri!, "func") ?? string.Empty, out var respond)
            ? respond(request, body)
            : new HttpResponseMessage((HttpStatusCode)599);

    private void Func(string name, Func<HttpRequestMessage, HttpResponseMessage> respond) => _functions[name] = (request, _) => respond(request);

    private void FuncJson(string name, string json) => Func(name, _ => FakeHttpHandler.Json(json));

    private IEnumerable<FakeHttpHandler.RecordedRequest> Calls(string func)
        => _http.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("utilRequest.cgi", StringComparison.Ordinal) && Param(r.Uri, "func") == func);

    private IEnumerable<System.Collections.Specialized.NameValueCollection> LoginForms()
        => _http.Requests.Where(r => r.Uri.AbsolutePath == "/cgi-bin/authLogin.cgi").Select(r => HttpUtility.ParseQueryString(r.Body ?? string.Empty));

    private QnapCredentials StoredCredentials(string? qtoken = null, string? vtoken = null)
        => new("https://nas.local", "robert", "pw", true, "robert@nas.local", "CLIENT-1", qtoken, vtoken);

    private QnapProvider Connected(string? qtoken = null, string? vtoken = null)
    {
        _store.Save($"qnap.{AccountId}", StoredCredentials(qtoken, vtoken));
        return new QnapProvider(AccountId, _store, _http) { PollInterval = TimeSpan.FromMilliseconds(1) };
    }

    private static ConnectValues Values(string server = "nas.local", string? code = null, string password = "correct horse", string username = " robert ")
    {
        var values = new Dictionary<string, string>
        {
            ["serverUrl"] = server,
            ["username"] = username,
            ["password"] = password,
            ["allowSelfSigned"] = "true",
        };
        if (code is not null)
        {
            values["securityCode"] = code;
        }

        return new ConnectValues("default", values);
    }

    // MARK: - Addresses and encoding

    [Theory]
    [InlineData("nas.local", "https://nas.local")]
    [InlineData(" https://nas.local/ ", "https://nas.local")]
    [InlineData("http://nas.local", "http://nas.local:8080")]
    [InlineData("http://192.168.1.5:80", "http://192.168.1.5:80")]
    [InlineData("https://nas.myqnapcloud.com:8081", "https://nas.myqnapcloud.com:8081")]
    public void ServerUrlDefaultsToHttpsAndQtsHttpPort(string typed, string expected)
        => Assert.Equal(expected, QnapApiClient.NormalizeServerUrl(typed));

    [Fact]
    public void FormEncodingIsStrict()
    {
        // QTS reads "+" as a space; "a+b.txt" must not become "a b.txt".
        Assert.Equal("path=%2FPublic%2Fa%2Bb%20%C3%BC.txt&x=1", QnapApiClient.FormEncode([("path", "/Public/a+b ü.txt"), ("x", "1")]));
        Assert.Equal("cMOkc3M=", QnapApiClient.EncodePassword("päss"));
    }

    [Fact]
    public void LoginReplyIsReadFromTheTopLevel()
    {
        var reply = QnapApiClient.TopLevelValues("""
            <?xml version="1.0" encoding="UTF-8"?>
            <QDocRoot version="1.0"><doQuick><![CDATA[]]></doQuick><authPassed><![CDATA[1]]></authPassed>
            <authSid><![CDATA[abc123]]></authSid><nested><inner>x</inner></nested><qtoken>tok</qtoken></QDocRoot>
            """);
        Assert.Equal("abc123", QnapApiClient.SessionId(reply));
        Assert.Equal("tok", reply["qtoken"]);
        Assert.Equal(string.Empty, reply["nested"]);
        Assert.Null(QnapApiClient.SessionId(QnapApiClient.TopLevelValues("<QDocRoot><authPassed>0</authPassed><authSid>abc</authSid></QDocRoot>")));
    }

    [Fact]
    public void FormMirrorsTheMacSheet()
    {
        using var provider = new QnapProvider(AccountId, _store, _http);
        var fields = provider.ConnectSpec.Modes.Single().Fields.Where(f => f.HasValue).ToDictionary(f => f.Key);
        Assert.Equal(["serverUrl", "username", "password", "securityCode", "allowSelfSigned"], fields.Keys);
        Assert.Equal(ConnectFieldKind.OneTimeCode, fields["securityCode"].Kind);
        Assert.False(fields["securityCode"].Required);
        Assert.Equal("2-step verification code (only if it's on)", fields["securityCode"].Label);
        Assert.Equal("true", fields["allowSelfSigned"].DefaultValue);
        Assert.True(provider.ShareLinkCapabilities.CanCreate);
        Assert.True(provider.ShareLinkCapabilities.CanQueryExisting);
        Assert.False(provider.ShareLinkCapabilities.CanUpdate);
        Assert.True(provider.ShareLinkCapabilities.SupportsPassword);
    }

    [Fact]
    public void RegistryCreatesQnapWithoutNetwork()
    {
        Assert.True(CloudProviderRegistry.IsAvailable(CloudProviderType.Qnap));
        using var provider = CloudProviderRegistry.Create(CloudProviderType.Qnap, Guid.NewGuid(), new MemoryCredentialStore());
        Assert.IsType<QnapProvider>(provider);
        Assert.False(provider.IsAuthenticated);
        Assert.Equal("QNAP", CloudProviderType.Qnap.DisplayName());
        Assert.Equal(ProviderCategory.Nas, CloudProviderType.Qnap.Category());
        Assert.Equal(ProviderCategory.Nas, CloudProviderType.SynologyDrive.Category());
        Assert.Equal("\"qnap\"", System.Text.Json.JsonSerializer.Serialize(CloudProviderType.Qnap));
        Assert.Equal("\"ugreen\"", System.Text.Json.JsonSerializer.Serialize(CloudProviderType.Ugreen));
        Assert.Equal("\"asustor\"", System.Text.Json.JsonSerializer.Serialize(CloudProviderType.Asustor));
    }

    // MARK: - Connecting

    [Fact]
    public async Task ConnectSignsInWithThePasswordAndKeepsTheSession()
    {
        _login = _ => LoginOk("sid-connect", "<qtoken><![CDATA[remember-me]]></qtoken>");
        FuncJson("get_tree", """[{"id":"/Public","text":"Public","iconCls":"folder"}]""");
        using var provider = new QnapProvider(AccountId, _store, _http);

        var result = await provider.ConnectAsync(Values(), new ScriptedConnectPrompt());

        Assert.Equal("robert@nas.local", result.AccountLabel);
        Assert.Equal("QNAP (robert@nas.local)", CloudAccount.ComposeDisplayName(CloudProviderType.Qnap, result.AccountLabel));
        var login = _http.Single(HttpMethod.Post, "authLogin.cgi");
        Assert.Equal("https://nas.local/cgi-bin/authLogin.cgi", login.Uri.AbsoluteUri);
        Assert.DoesNotContain("pwd", login.Uri.Query, StringComparison.Ordinal);
        var form = HttpUtility.ParseQueryString(login.Body!);
        Assert.Equal("robert", form["user"]);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("correct horse")), form["pwd"]);
        Assert.Equal("1", form["remme"]);
        Assert.Equal("FileFluss", form["client_app"]);
        Assert.False(string.IsNullOrEmpty(form["client_id"]));
        Assert.Null(form["security_code"]);
        Assert.Null(form["qtoken"]);

        var stored = _store.Load<QnapCredentials>($"qnap.{AccountId}")!;
        Assert.Equal("https://nas.local", stored.ServerUrl);
        Assert.Equal("remember-me", stored.Qtoken);
        Assert.True(stored.AllowSelfSignedCertificate);
        Assert.Equal(
            new Dictionary<string, string> { ["serverUrl"] = "https://nas.local", ["username"] = "robert", ["allowSelfSigned"] = "true" },
            provider.GetEditableConnectValues());

        // The session from sign-in is used straight away.
        Assert.Equal(["Public"], (await provider.ListDirectoryAsync("/")).Select(i => i.Name));
        Assert.Single(LoginForms());
        Assert.Equal("sid-connect", Param(Calls("get_tree").Single().Uri, "sid"));
    }

    [Fact]
    public async Task TwoStepVerificationAsksForTheCodeAndKeepsTheVtoken()
    {
        _login = form => form["security_code"] switch
        {
            null => LoginRefused("<need_2sv><![CDATA[1]]></need_2sv>"),
            "123456" when form["dont_verify_2sv_again"] == "1" => LoginOk("sid-2sv", "<vtoken><![CDATA[skip-code]]></vtoken>"),
            _ => LoginRefused("<need_2sv>1</need_2sv>"),
        };
        var prompt = new ScriptedConnectPrompt();
        prompt.Codes.Enqueue(" 123 456 ");
        using var provider = new QnapProvider(AccountId, _store, _http);

        await provider.ConnectAsync(Values(), prompt);

        Assert.Equal([null, "123456"], LoginForms().Select(f => f["security_code"]));
        Assert.Equal("skip-code", _store.Load<QnapCredentials>($"qnap.{AccountId}")!.Vtoken);
    }

    [Fact]
    public async Task ATypedCodeThatIsWrongSaysSo()
    {
        _login = _ => LoginRefused("<need_2sv>1</need_2sv>");
        using var provider = new QnapProvider(AccountId, _store, _http);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(Values(code: "000 000"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("The 2-step verification code is incorrect.", error.Message);
        Assert.Equal("000000", LoginForms().Single()["security_code"]);
        Assert.Null(_store.Load($"qnap.{AccountId}"));
    }

    [Fact]
    public async Task TwoStepWithoutAnAnswerFailsAsTwoFactorRequired()
    {
        _login = _ => LoginRefused("<need_2sv>1</need_2sv>");
        using var provider = new QnapProvider(AccountId, _store, _http);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(Values(), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.TwoFactorRequired, error.Kind);
        Assert.False(provider.IsAuthenticated);
    }

    [Theory]
    [InlineData("<errorValue>-3</errorValue>", CloudErrorKind.CommandFailed)]
    [InlineData("<user_enable>0</user_enable>", CloudErrorKind.CommandFailed)]
    [InlineData("", CloudErrorKind.InvalidCredentials)]
    public async Task FailedSignInLeavesStoredCredentialsAlone(string reply, CloudErrorKind expected)
    {
        using var connected = Connected(qtoken: "old-token");
        var before = _store.Load($"qnap.{AccountId}");
        _login = _ => LoginRefused(reply);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => connected.ConnectAsync(Values(password: "wrong"), new ScriptedConnectPrompt()));

        Assert.Equal(expected, error.Kind);
        Assert.Equal(before, _store.Load($"qnap.{AccountId}"));
        Assert.True(connected.IsAuthenticated);
    }

    [Fact]
    public async Task EditingTheSameAccountKeepsItsClientIdAndVtoken()
    {
        _store.Save($"qnap.{AccountId}", StoredCredentials(qtoken: "old-qtoken", vtoken: "skip-code"));
        using var provider = new QnapProvider(AccountId, _store, _http);

        await provider.ConnectAsync(Values(server: "https://nas.local/", password: "new password"), new ScriptedConnectPrompt());

        var form = LoginForms().Single();
        Assert.Equal("CLIENT-1", form["client_id"]);
        Assert.Equal("skip-code", form["vtoken"]);
        // The qtoken stands in for the old password and must not log in after a password change.
        Assert.Null(form["qtoken"]);
        Assert.Null(_store.Load<QnapCredentials>($"qnap.{AccountId}")!.Qtoken);
    }

    [Fact]
    public async Task TokensNeverGoToAnotherNas()
    {
        _store.Save($"qnap.{AccountId}", StoredCredentials(vtoken: "skip-code"));
        using var provider = new QnapProvider(AccountId, _store, _http);

        await provider.ConnectAsync(Values(server: "other-nas.local"), new ScriptedConnectPrompt());

        var form = LoginForms().Single();
        Assert.Null(form["vtoken"]);
        Assert.NotEqual("CLIENT-1", form["client_id"]);
    }

    // MARK: - Sessions

    [Fact]
    public async Task ARelaunchLogsInWithTheRememberedToken()
    {
        _login = form => form["qtoken"] == "remember-me" ? LoginOk("token-sid") : LoginRefused();
        FuncJson("get_tree", "[]");
        using var provider = Connected(qtoken: "remember-me");

        await provider.ListDirectoryAsync("/");

        var form = LoginForms().Single();
        Assert.Null(form["pwd"]);
        Assert.Equal("CLIENT-1", form["client_id"]);
        Assert.Equal("token-sid", Param(Calls("get_tree").Single().Uri, "sid"));
    }

    [Fact]
    public async Task ARefusedTokenFallsBackToThePasswordAndTheNewTokenIsSaved()
    {
        _login = form => form["qtoken"] is not null
            ? LoginRefused()
            : LoginOk("pw-sid", "<qtoken>fresh-token</qtoken>");
        FuncJson("get_tree", "[]");
        using var provider = Connected(qtoken: "expired", vtoken: "skip-code");

        await provider.ListDirectoryAsync("/");

        var forms = LoginForms().ToList();
        Assert.Equal(2, forms.Count);
        Assert.Equal("skip-code", forms[1]["vtoken"]);
        Assert.NotNull(forms[1]["pwd"]);
        Assert.Equal("fresh-token", _store.Load<QnapCredentials>($"qnap.{AccountId}")!.Qtoken);
    }

    [Fact]
    public async Task ALostSessionLogsInAgainOnce()
    {
        Func("get_list", request => Param(request.RequestUri!, "sid") == "sid-1"
            ? FakeHttpHandler.Json("{\"status\":3}")
            : FakeHttpHandler.Json("{\"total\":0,\"datas\":[]}"));
        using var provider = Connected();

        Assert.Empty(await provider.ListDirectoryAsync("/Public"));
        Assert.Equal(["sid-1", "sid-2"], Calls("get_list").Select(r => Param(r.Uri, "sid")));
    }

    [Fact]
    public async Task ARelaunchThatNeedsACodeAsksToSignInAgain()
    {
        _login = _ => LoginRefused("<need_2sv>1</need_2sv>");
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
    }

    // MARK: - Listing

    [Fact]
    public async Task SharesAreTheTopLevelWithoutDevicesAndImages()
    {
        FuncJson("get_tree", """
            [
              {"id":"/Public","text":"Public","iconCls":"folder"},
              {"id":"/homes","text":"homes","iconCls":"folder"},
              {"id":"/iso","text":"disc.iso","iconCls":"iso"},
              {"id":"/USBDisk1","text":"USBDisk1","iconCls":"usb"},
              {"id":"","text":"broken"}
            ]
            """);
        using var provider = Connected();

        var shares = await provider.ListDirectoryAsync("/");

        Assert.Equal(["/Public", "/homes"], shares.Select(s => s.Path));
        Assert.All(shares, s => Assert.True(s.IsDirectory));
        var request = Calls("get_tree").Single();
        Assert.Equal("share_root", Param(request.Uri, "node"));
    }

    [Fact]
    public async Task ListingParsesRowsDropsSystemFoldersAndPages()
    {
        var page = 0;
        Func("get_list", request =>
        {
            page++;
            var start = int.Parse(Param(request.RequestUri!, "start")!, System.Globalization.CultureInfo.InvariantCulture);
            if (start == 0)
            {
                var rows = Enumerable.Range(0, 1000).Select(i =>
                    i switch
                    {
                        0 => """{"filename":"Photos","isfolder":1,"filesize":"4096","epochmt":1736244000}""",
                        1 => """{"filename":"@Recycle","isfolder":1}""",
                        2 => """{"filename":".@__thumb","isfolder":1}""",
                        3 => """{"filename":"report 2024.pdf","isfolder":"0","filesize":"1048576","epochmt":"1736244000"}""",
                        4 => """{"filename":".hidden","isfolder":0,"filesize":3,"mt":"2025/01/07 10:00:00"}""",
                        _ => $$"""{"filename":"f{{i}}.txt","isfolder":0,"filesize":{{i}}}""",
                    });
                return FakeHttpHandler.Json($"{{\"total\":1001,\"datas\":[{string.Join(',', rows)}]}}");
            }

            return FakeHttpHandler.Json("""{"total":1001,"datas":[{"filename":"last.txt","isfolder":0,"filesize":1}]}""");
        });
        using var provider = Connected();

        var items = await provider.ListDirectoryAsync("/Public/");

        Assert.Equal(2, page);
        Assert.Equal(999, items.Count); // 1001 rows, two system folders dropped
        Assert.Equal(["0", "1000"], Calls("get_list").Select(r => Param(r.Uri, "start")));
        Assert.All(Calls("get_list"), r => Assert.Equal("/Public", Param(r.Uri, "path")));

        var photos = items[0];
        Assert.Equal("/Public/Photos", photos.Path);
        Assert.True(photos.IsDirectory);
        Assert.Equal(0, photos.Size);
        var report = items.Single(i => i.Name == "report 2024.pdf");
        Assert.Equal(1_048_576, report.Size);
        Assert.Equal(new DateTimeOffset(2025, 1, 7, 10, 0, 0, TimeSpan.Zero), report.ModificationDate);
        var hidden = items.Single(i => i.Name == ".hidden");
        Assert.True(hidden.IsHidden);
        Assert.True(hidden.HasKnownModificationDate);
        Assert.Equal("/Public/last.txt", items[^1].Path);
    }

    [Theory]
    [InlineData(5, CloudErrorKind.NotFound)]
    [InlineData(4, CloudErrorKind.Unauthorized)]
    [InlineData(9, CloudErrorKind.QuotaExceeded)]
    [InlineData(8, CloudErrorKind.CommandFailed)]
    [InlineData(77, CloudErrorKind.ServerError)]
    public async Task FileStationStatusesMapToErrors(int status, CloudErrorKind expected)
    {
        FuncJson("delete", $"{{\"status\":{status}}}");
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DeleteItemAsync("/Public/a.txt"));

        Assert.Equal(expected, error.Kind);
        var request = Calls("delete").Single();
        Assert.Equal("/Public", Param(request.Uri, "path"));
        Assert.Equal("a.txt", Param(request.Uri, "file_name"));
    }

    [Fact]
    public async Task CreatingAFolderThatExistsIsFine()
    {
        FuncJson("createdir", "{\"status\":33}");
        using var provider = Connected();

        await provider.CreateDirectoryAsync("/Public/New");

        var request = Calls("createdir").Single();
        Assert.Equal("/Public", Param(request.Uri, "dest_path"));
        Assert.Equal("New", Param(request.Uri, "dest_folder"));
    }

    [Fact]
    public async Task MetadataComesFromStat()
    {
        FuncJson("stat", """{"status":1,"datas":[{"filename":"","exist":1,"isfolder":0,"filesize":"12","epochmt":1736244000}]}""");
        using var provider = Connected();

        var item = await provider.GetFileMetadataAsync("/Public/a.txt");

        Assert.Equal("a.txt", item.Name);
        Assert.Equal(12, item.Size);
        Assert.Equal("/", (await provider.GetFileMetadataAsync("/")).Path);

        FuncJson("stat", """{"status":1,"datas":[{"exist":0}]}""");
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.GetFileMetadataAsync("/Public/gone.txt"));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    // MARK: - Transfers

    [Fact]
    public async Task DownloadStreamsTheFile()
    {
        Func("download", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello qnap")) };
            response.Content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename=\"a.txt\"");
            return response;
        });
        using var provider = Connected();
        var target = Path.Combine(_temp, "a.txt");
        long reported = 0;

        await provider.DownloadFileAsync("/Public/Docs/a.txt", target, n => reported += n);

        Assert.Equal("hello qnap", await File.ReadAllTextAsync(target));
        Assert.Equal(10, reported);
        var request = Calls("download").Single();
        Assert.Equal("/Public/Docs", Param(request.Uri, "source_path"));
        Assert.Equal("a.txt", Param(request.Uri, "source_file"));
    }

    [Fact]
    public async Task ARefusedDownloadNeverReplacesTheFileAlreadyThere()
    {
        // File Station answers a refusal with HTTP 200 and a JSON status instead of the file.
        FuncJson("download", "{\"status\":5}");
        using var provider = Connected();
        var target = Path.Combine(_temp, "keep.txt");
        await File.WriteAllTextAsync(target, "precious");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DownloadFileAsync("/Public/gone.txt", target));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal("precious", await File.ReadAllTextAsync(target));
        Assert.Equal(["keep.txt"], Directory.GetFiles(_temp).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ASmallFileWithoutContentDispositionStillArrives()
    {
        Func("download", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"name\":\"not a status\"}") });
        using var provider = Connected();
        var target = Path.Combine(_temp, "data.json");

        await provider.DownloadFileAsync("/Public/data.json", target);

        Assert.Equal("{\"name\":\"not a status\"}", await File.ReadAllTextAsync(target));
        Assert.Equal(["data.json"], Directory.GetFiles(_temp).Select(Path.GetFileName));
    }

    [Fact]
    public async Task UploadIsAStreamedMultipartPostRetriedOnceAfterALostSession()
    {
        var local = Path.Combine(_temp, "up.bin");
        await File.WriteAllTextAsync(local, "0123456789");
        _functions["upload"] = (request, _) => Param(request.RequestUri!, "sid") == "sid-1"
            ? FakeHttpHandler.Json("{\"status\":3}")
            : FakeHttpHandler.Json("{\"status\":1}");
        using var provider = Connected();
        long reported = 0;

        await provider.UploadFileAsync(local, "/Public/Docs/ä+b \"q\".txt", n => Interlocked.Add(ref reported, n));

        var uploads = _http.Requests.Where(r => r.Method == HttpMethod.Post && Param(r.Uri, "func") == "upload").ToList();
        Assert.Equal(2, uploads.Count);
        Assert.Equal(10, reported); // the re-sent body isn't counted twice
        var last = uploads[^1];
        Assert.Equal("/Public/Docs", Param(last.Uri, "dest_path"));
        Assert.Equal("1", Param(last.Uri, "overwrite"));
        Assert.Equal("-Public-Docs-ä+b \"q\".txt", Param(last.Uri, "progress"));
        Assert.Contains("%2B", last.Uri.Query, StringComparison.Ordinal);

        var contentType = last.Headers["Content-Type"];
        Assert.StartsWith("multipart/form-data; boundary=----FileFlussQNAP", contentType, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", contentType, StringComparison.Ordinal);
        Assert.Contains("Content-Disposition: form-data; name=\"fileName\"\r\n\r\nä+b \"q\".txt\r\n", last.Body, StringComparison.Ordinal);
        Assert.Contains("Content-Disposition: form-data; name=\"file\"; filename=\"ä+b %22q%22.txt\"", last.Body, StringComparison.Ordinal);
        Assert.Contains("0123456789", last.Body, StringComparison.Ordinal);
    }

    // MARK: - Copy, move, search, size

    [Fact]
    public async Task CopyToANewNameLandsThenRenames()
    {
        FuncJson("copy", "{\"status\":1}");
        FuncJson("rename", "{\"status\":1}");
        Func("stat", request => Param(request.RequestUri!, "path") == "/Backup" && Param(request.RequestUri!, "file_name") == "a.txt" && Calls("copy").Any()
            ? FakeHttpHandler.Json("""{"status":1,"datas":[{"exist":1,"isfolder":0}]}""")
            : FakeHttpHandler.Json("""{"status":1,"datas":[{"exist":0}]}"""));
        using var provider = Connected();

        await provider.CopyItemAsync("/Public/a.txt", "/Backup/b.txt");

        var copy = Calls("copy").Single();
        Assert.Equal("/Public", Param(copy.Uri, "source_path"));
        Assert.Equal("/Backup", Param(copy.Uri, "dest_path"));
        Assert.Equal("0", Param(copy.Uri, "mode"));
        var rename = Calls("rename").Single();
        Assert.Equal("/Backup", Param(rename.Uri, "path"));
        Assert.Equal("a.txt", Param(rename.Uri, "source_name"));
        Assert.Equal("b.txt", Param(rename.Uri, "dest_name"));
    }

    [Fact]
    public async Task KeepBothInTheSameFolderIsLeftToTheCaller()
    {
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/Public/a.txt", "/Public/a (2).txt"));

        Assert.Equal(CloudErrorKind.NotImplemented, error.Kind);
        Assert.Empty(Calls("copy"));
    }

    [Fact]
    public async Task SearchCoversEveryShareAndSkipsSystemFolders()
    {
        FuncJson("get_tree", """[{"id":"/Public","text":"Public","iconCls":"folder"},{"id":"/Media","text":"Media","iconCls":"folder"}]""");
        FuncJson("search_ext", """
            {"total":3,"datas":[
              {"filename":"/Public/Docs/plan.txt","isfolder":0,"filesize":5},
              {"filename":"/Public/@Recycle/plan.txt","isfolder":0},
              {"filename":"/Media/plans","isfolder":1}
            ]}
            """);
        using var provider = Connected();

        var hits = await provider.SearchFilesAsync("plan", null);

        Assert.Equal(["/Public/Docs/plan.txt", "/Media/plans"], hits!.Select(h => h.Path));
        var request = Calls("search_ext").Single();
        Assert.Equal("2", Param(request.Uri, "folderCount"));
        Assert.Equal("/Public,/Media", Param(request.Uri, "folders"));
        Assert.Equal("plan", Param(request.Uri, "keyword"));
    }

    [Fact]
    public async Task FolderSizeAsksTheNas()
    {
        FuncJson("get_file_size", "{\"status\":1,\"size\":\"123456\"}");
        using var provider = Connected();

        Assert.Equal(123_456, await provider.FolderSizeAsync("/Public/Docs"));
    }

    // MARK: - Share links

    [Fact]
    public async Task ShareLinkCarriesTheHostTheAccountUsesAndTheAccessCode()
    {
        FuncJson("get_share_link", """{"status":1,"links":[{"link_url":"https://nas.local/share.cgi?ssid=abc"}]}""");
        using var provider = Connected();
        var expiry = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

        var link = await provider.CreateShareLinkAsync("/Public/a.txt", new ShareLinkOptions { Password = "open sesame", Expiry = expiry });

        Assert.Equal("https://nas.local/share.cgi?ssid=abc", link.Url.AbsoluteUri);
        Assert.True(link.HasPassword);
        Assert.Equal(expiry, link.ExpiresAt);
        Assert.NotNull(link.Note);
        var request = Calls("get_share_link").Single();
        Assert.Equal("nas.local", Param(request.Uri, "hostname"));
        Assert.Equal("true", Param(request.Uri, "ssl"));
        Assert.Equal(expiry.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), Param(request.Uri, "expire_time"));
        Assert.Equal("open sesame", Param(request.Uri, "access_code"));
        Assert.Equal("true", Param(request.Uri, "access_enabled"));
    }

    [Fact]
    public async Task ExistingLinksAreFoundByPathAndRemovedBySsid()
    {
        FuncJson("get_share_list", """
            {"total":3,"datas":[
              {"filename":"/Public/a.txt","ssid":"multi","multiple":"1","download_link":"https://nas.local/m"},
              {"filename":"/Public/a.txt","ssid":"s1","download_link":"https://nas.local/share.cgi?ssid=s1","expire_time":"0","access_code":"----"},
              {"filename":"/Public/b.txt","ssid":"s2","download_link":"https://nas.local/share.cgi?ssid=s2","expire_time":1796083200,"access_code":"1234"}
            ]}
            """);
        FuncJson("delete_share", "{\"status\":1}");
        using var provider = Connected();

        var a = await provider.ExistingShareLinkAsync("/Public/a.txt");
        Assert.Equal("https://nas.local/share.cgi?ssid=s1", a!.Url.AbsoluteUri);
        Assert.False(a.HasPassword);
        Assert.Null(a.ExpiresAt);

        var b = await provider.ExistingShareLinkAsync("/Public/b.txt");
        Assert.True(b!.HasPassword);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1796083200), b.ExpiresAt);

        Assert.Null(await provider.ExistingShareLinkAsync("/Public/none.txt"));

        await provider.RemoveShareLinkAsync("/Public/b.txt");
        Assert.Equal("s2", Param(Calls("delete_share").Single().Uri, "ssids"));

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.RemoveShareLinkAsync("/Public/none.txt"));
        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
    }

    [Fact]
    public async Task DisconnectLogsOutAndForgetsTheAccount()
    {
        FuncJson("get_tree", "[]");
        using var provider = Connected();
        await provider.ListDirectoryAsync("/");

        await provider.DisconnectAsync();

        Assert.False(provider.IsAuthenticated);
        Assert.Null(_store.Load($"qnap.{AccountId}"));
        var logout = _http.Single(HttpMethod.Get, "authLogout.cgi");
        Assert.Equal("sid-1", Param(logout.Uri, "sid"));
    }
}
