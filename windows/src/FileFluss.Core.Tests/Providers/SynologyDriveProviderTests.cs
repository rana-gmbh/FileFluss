// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.SynologyDrive;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

public sealed class SynologyDriveProviderTests : IDisposable
{
    private static readonly Guid AccountId = Guid.Parse("2f3a4b6f-9b0c-4a8e-9a43-0d6c1e516a2f");

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ff-syno-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttpHandler _http = new();
    private readonly MemoryCredentialStore _store = new();
    private readonly ConcurrentDictionary<(string Api, string Method), Func<HttpRequestMessage, string?, HttpResponseMessage>> _api = new();
    private int _logins;

    public SynologyDriveProviderTests()
    {
        Directory.CreateDirectory(_temp);
        _http.On(HttpMethod.Get, "/webapi/auth.cgi", (_, _) => Ok($"{{\"sid\":\"sid-{Interlocked.Increment(ref _logins)}\"}}"));
        _http.On(HttpMethod.Get, "/webapi/entry.cgi", Dispatch);
        _http.On(HttpMethod.Post, "/webapi/entry.cgi", Dispatch);
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static HttpResponseMessage Ok(string data) => FakeHttpHandler.Json($"{{\"success\":true,\"data\":{data}}}");

    private static HttpResponseMessage OkEmpty() => FakeHttpHandler.Json("{\"success\":true}");

    private static HttpResponseMessage Fail(int code) => FakeHttpHandler.Json($"{{\"success\":false,\"error\":{{\"code\":{code}}}}}");

    private static string? Param(Uri uri, string name) => HttpUtility.ParseQueryString(uri.Query)[name];

    private static string? Param(HttpRequestMessage request, string name) => Param(request.RequestUri!, name);

    private HttpResponseMessage Dispatch(HttpRequestMessage request, string? body)
        => _api.TryGetValue((Param(request, "api") ?? string.Empty, Param(request, "method") ?? string.Empty), out var respond)
            ? respond(request, body)
            : new HttpResponseMessage((HttpStatusCode)599);

    private void Api(string api, string method, Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _api[(api, method)] = (request, _) => respond(request);

    private IEnumerable<FakeHttpHandler.RecordedRequest> Calls(string api, string method)
        => _http.Requests.Where(r => Param(r.Uri, "api") == api && Param(r.Uri, "method") == method);

    private SynologyDriveProvider Connected()
    {
        _store.Save($"synologyDrive.{AccountId}", new SynologyDriveCredentials("https://nas.local:5001", "robert", "pw", true, "robert@nas.local"));
        return new SynologyDriveProvider(AccountId, _store, _http) { PollInterval = TimeSpan.FromMilliseconds(1) };
    }

    private static ConnectValues Values(string server = "nas.local", string? otp = null, string password = "correct horse")
    {
        var values = new Dictionary<string, string>
        {
            ["serverUrl"] = server,
            ["username"] = " robert ",
            ["password"] = password,
            ["allowSelfSigned"] = "true",
        };
        if (otp is not null)
        {
            values["otp"] = otp;
        }

        return new ConnectValues("default", values);
    }

    // MARK: - Server URL

    [Theory]
    [InlineData("nas.local", "https://nas.local:5001")]
    [InlineData(" my-nas.synology.me ", "https://my-nas.synology.me:5001")]
    [InlineData("https://nas.local", "https://nas.local:5001")]
    [InlineData("https://nas.local/", "https://nas.local:5001")]
    [InlineData("http://nas.local", "http://nas.local:5000")]
    [InlineData("https://nas.local:8443/", "https://nas.local:8443")]
    [InlineData("https://nas.local:443", "https://nas.local:443")]
    [InlineData("192.168.1.10:5001", "https://192.168.1.10:5001")]
    [InlineData("https://[fd00::1]", "https://[fd00::1]:5001")]
    public void ServerUrlDefaultsToHttpsAndTheDsmPort(string typed, string expected)
        => Assert.Equal(expected, SynologyDriveApiClient.NormalizeServerUrl(typed));

    [Fact]
    public void FormMirrorsTheMacSheet()
    {
        using var provider = new SynologyDriveProvider(AccountId, _store, _http);
        var fields = provider.ConnectSpec.Modes[0].Fields.Where(f => f.HasValue).ToDictionary(f => f.Key);
        Assert.Equal(["serverUrl", "username", "password", "otp", "allowSelfSigned"], fields.Keys);
        Assert.Equal(ConnectFieldKind.OneTimeCode, fields["otp"].Kind);
        Assert.False(fields["otp"].Required);
        Assert.Equal("true", fields["allowSelfSigned"].DefaultValue);
        Assert.True(provider.ShareLinkCapabilities.CanQueryExisting);
        Assert.True(provider.ShareLinkCapabilities.SupportsPassword);
        Assert.Null(provider.MaxUploadFileSize);
    }

    // MARK: - Connecting

    [Fact]
    public async Task ConnectLogsInAndKeepsTheSession()
    {
        Api("SYNO.FileStation.List", "list_share", _ => Ok("{\"shares\":[]}"));
        using var provider = new SynologyDriveProvider(AccountId, _store, _http);

        var result = await provider.ConnectAsync(Values(), new ScriptedConnectPrompt());

        Assert.Equal("robert@nas.local", result.AccountLabel);
        Assert.Equal("Synology Drive (robert@nas.local)", CloudAccount.ComposeDisplayName(CloudProviderType.SynologyDrive, result.AccountLabel));
        var login = _http.Single(HttpMethod.Get, "auth.cgi");
        Assert.Equal("https://nas.local:5001/webapi/auth.cgi", login.Uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("SYNO.API.Auth", Param(login.Uri, "api"));
        Assert.Equal("6", Param(login.Uri, "version"));
        Assert.Equal("robert", Param(login.Uri, "account"));
        Assert.Equal("correct horse", Param(login.Uri, "passwd"));
        Assert.Equal("FileStation", Param(login.Uri, "session"));
        Assert.Equal("sid", Param(login.Uri, "format"));
        Assert.Null(Param(login.Uri, "otp_code"));

        var stored = _store.Load<SynologyDriveCredentials>($"synologyDrive.{AccountId}");
        Assert.Equal("https://nas.local:5001", stored?.ServerUrl);
        Assert.True(stored?.AllowSelfSignedCertificate);
        Assert.DoesNotContain(provider.GetEditableConnectValues(), v => v.Value.Contains("horse", StringComparison.Ordinal));
        Assert.Equal("robert", provider.GetEditableConnectValues()["username"]);

        // The session from sign-in is used straight away.
        await provider.ListDirectoryAsync("/");
        Assert.Equal(1, _logins);
        Assert.Equal("sid-1", Param(Calls("SYNO.FileStation.List", "list_share").Single().Uri, "_sid"));
    }

    [Fact]
    public async Task ConnectAsksForTheOtpCodeWhenDsmWantsOne()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "/webapi/auth.cgi", (request, _) => Param(request, "otp_code") switch
        {
            null => Fail(403),
            "123456" => Ok("{\"sid\":\"otp-sid\"}"),
            _ => Fail(404),
        });
        var prompt = new ScriptedConnectPrompt();
        prompt.Codes.Enqueue(" 123456 ");
        using var provider = new SynologyDriveProvider(AccountId, _store, handler);

        var result = await provider.ConnectAsync(Values(server: "otp-nas.local"), prompt);

        Assert.Equal("robert@otp-nas.local", result.AccountLabel);
        Assert.True(provider.IsAuthenticated);
        Assert.Equal([null, "123456"], handler.Requests.Select(r => Param(r.Uri, "otp_code")));
        Assert.NotNull(_store.Load($"synologyDrive.{AccountId}"));
    }

    [Fact]
    public async Task FailedSignInLeavesStoredCredentialsAlone()
    {
        using var provider = Connected();
        var before = _store.Load($"synologyDrive.{AccountId}");
        var failing = new FakeHttpHandler().OnJson(HttpMethod.Get, "/webapi/auth.cgi", "{\"success\":false,\"error\":{\"code\":400}}");
        using var failingProvider = new SynologyDriveProvider(AccountId, _store, failing);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => failingProvider.ConnectAsync(Values(password: "wrong"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal(before, _store.Load($"synologyDrive.{AccountId}"));
        Assert.True(failingProvider.IsAuthenticated);
    }

    [Fact]
    public async Task OtpRequiredWithoutAnAnswerFailsWithDsmsExplanation()
    {
        var failing = new FakeHttpHandler().OnJson(HttpMethod.Get, "/webapi/auth.cgi", "{\"success\":false,\"error\":{\"code\":403}}");
        using var provider = new SynologyDriveProvider(AccountId, _store, failing);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(Values(), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Null(_store.Load($"synologyDrive.{AccountId}"));
        Assert.False(provider.IsAuthenticated);
    }

    // MARK: - Device token (macOS #66, #67)

    /// <summary>A DSM that wants an OTP unless the request carries the device token it issued.</summary>
    private static FakeHttpHandler TwoFactorDsm(string tokenField = "did") => new FakeHttpHandler().On(HttpMethod.Get, "/webapi/auth.cgi", (request, _) =>
    {
        if (Param(request, "otp_code") is { } otp)
        {
            return otp == "123456" && Param(request, "enable_device_token") == "yes" && Param(request, "device_name") == "FileFluss"
                ? Ok($"{{\"sid\":\"otp-sid\",\"{tokenField}\":\"device-token-1\"}}")
                : Fail(404);
        }

        return Param(request, "device_id") == "device-token-1" && Param(request, "device_name") == "FileFluss"
            ? Ok("{\"sid\":\"token-sid\"}")
            : Fail(403);
    });

    [Theory]
    [InlineData("did")]
    [InlineData("device_id")]
    public async Task OtpLoginKeepsTheDeviceTokenForLaterLogins(string tokenField)
    {
        var dsm = TwoFactorDsm(tokenField);
        using (var provider = new SynologyDriveProvider(AccountId, _store, dsm))
        {
            await provider.ConnectAsync(Values(otp: "123 456"), new ScriptedConnectPrompt());
        }

        var stored = _store.Load<SynologyDriveCredentials>($"synologyDrive.{AccountId}");
        Assert.Equal("device-token-1", stored?.DeviceId);

        // A restart: the stored account logs in again without a code, presenting the token.
        dsm.On(HttpMethod.Get, "/webapi/entry.cgi", (_, _) => Ok("{\"shares\":[]}"));
        using var relaunched = new SynologyDriveProvider(AccountId, _store, dsm);
        await relaunched.ListDirectoryAsync("/");

        var relogin = dsm.Requests.Where(r => r.Uri.AbsolutePath == "/webapi/auth.cgi").Last();
        Assert.Null(Param(relogin.Uri, "otp_code"));
        Assert.Equal("device-token-1", Param(relogin.Uri, "device_id"));
        Assert.Equal("token-sid", Param(dsm.Requests.Last().Uri, "_sid"));
    }

    [Fact]
    public async Task OtpCodesTypedOrPastedWithSpacesAreSentWithout()
    {
        var dsm = TwoFactorDsm();
        var prompt = new ScriptedConnectPrompt();
        prompt.Codes.Enqueue(" 123 456 ");
        using var provider = new SynologyDriveProvider(AccountId, _store, dsm);

        await provider.ConnectAsync(Values(), prompt);

        Assert.Equal([null, "123456"], dsm.Requests.Select(r => Param(r.Uri, "otp_code")));
        Assert.Equal("123456", SynologyDriveApiClient.NormalizeOtp("12 34\t56"));
        Assert.Equal(string.Empty, SynologyDriveApiClient.NormalizeOtp(null));
    }

    [Fact]
    public async Task EditingTheSameAccountCarriesTheDeviceTokenAlong()
    {
        _store.Save($"synologyDrive.{AccountId}", new SynologyDriveCredentials("https://nas.local:5001", "robert", "old", true, "robert@nas.local", "device-token-1"));
        var dsm = TwoFactorDsm();
        using var provider = new SynologyDriveProvider(AccountId, _store, dsm);

        // A new password and the certificate setting toggled, no OTP to hand: the token does it.
        var values = Values(server: "https://nas.local:5001/", password: "new password").AsDictionary().ToDictionary();
        values["allowSelfSigned"] = "false";
        await provider.ConnectAsync(new ConnectValues("default", values), new ScriptedConnectPrompt());

        Assert.Equal("device-token-1", Param(dsm.Requests.Single().Uri, "device_id"));
        var stored = _store.Load<SynologyDriveCredentials>($"synologyDrive.{AccountId}");
        Assert.Equal("device-token-1", stored?.DeviceId);
        Assert.Equal("new password", stored?.Password);
        Assert.False(stored?.AllowSelfSignedCertificate);
    }

    [Theory]
    [InlineData("other-nas.local", " robert ")]
    [InlineData("nas.local", "someone-else")]
    public async Task ADeviceTokenNeverGoesToAnotherServerOrUser(string server, string username)
    {
        _store.Save($"synologyDrive.{AccountId}", new SynologyDriveCredentials("https://nas.local:5001", "robert", "pw", true, "robert@nas.local", "device-token-1"));
        var dsm = TwoFactorDsm();
        using var provider = new SynologyDriveProvider(AccountId, _store, dsm);
        var values = Values(server: server).AsDictionary().ToDictionary();
        values["username"] = username;

        await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(new ConnectValues("default", values), new ScriptedConnectPrompt()));

        Assert.All(dsm.Requests, r => Assert.Null(Param(r.Uri, "device_id")));
        Assert.Equal("device-token-1", _store.Load<SynologyDriveCredentials>($"synologyDrive.{AccountId}")?.DeviceId);
    }

    [Fact]
    public void CredentialsSavedBeforeDeviceTokensStillLoad()
    {
        _store.Save($"synologyDrive.{AccountId}", Encoding.UTF8.GetBytes(
            """{"serverURL":"https://nas.local:5001","username":"robert","password":"secret","allowSelfSignedCertificate":false,"displayName":"robert@nas.local"}"""));

        var decoded = _store.Load<SynologyDriveCredentials>($"synologyDrive.{AccountId}");

        Assert.Equal("robert", decoded?.Username);
        Assert.Null(decoded?.DeviceId);
        using var provider = new SynologyDriveProvider(AccountId, _store, _http);
        Assert.True(provider.IsAuthenticated);
    }

    // MARK: - Sessions

    [Fact]
    public async Task ExpiredSessionLogsInAgainOnce()
    {
        Api("SYNO.FileStation.List", "list", request => Param(request, "_sid") == "sid-1" ? Fail(119) : Ok("{\"files\":[]}"));
        using var provider = Connected();

        Assert.Empty(await provider.ListDirectoryAsync("/home"));

        Assert.Equal(2, _logins);
        Assert.Equal(["sid-1", "sid-2"], Calls("SYNO.FileStation.List", "list").Select(r => Param(r.Uri, "_sid")));
    }

    [Fact]
    public async Task ConcurrentCallersShareOneLogin()
    {
        using var release = new ManualResetEventSlim();
        var logins = 0;
        var handler = new FakeHttpHandler();
        handler.On(HttpMethod.Get, "/webapi/auth.cgi", (_, _) =>
        {
            Interlocked.Increment(ref logins);
            release.Wait(TimeSpan.FromSeconds(10));
            return Ok("{\"sid\":\"shared\"}");
        });
        handler.On(HttpMethod.Get, "/webapi/entry.cgi", (_, _) => Ok("{\"files\":[]}"));
        _store.Save($"synologyDrive.{AccountId}", new SynologyDriveCredentials("https://nas.local:5001", "robert", "pw", true, "robert@nas.local"));
        using var provider = new SynologyDriveProvider(AccountId, _store, handler);

        var calls = Enumerable.Range(0, 4).Select(i => Task.Run(() => provider.ListDirectoryAsync($"/home/{i}"))).ToList();
        await Task.Delay(200);
        release.Set();
        await Task.WhenAll(calls);

        Assert.Equal(1, logins);
        Assert.All(handler.Requests.Where(r => r.Uri.AbsolutePath == "/webapi/entry.cgi"), r => Assert.Equal("shared", Param(r.Uri, "_sid")));
    }

    [Fact]
    public async Task WrongPasswordOnReloginAsksToSignInAgain()
    {
        var handler = new FakeHttpHandler().OnJson(HttpMethod.Get, "/webapi/auth.cgi", "{\"success\":false,\"error\":{\"code\":400}}");
        _store.Save($"synologyDrive.{AccountId}", new SynologyDriveCredentials("https://nas.local:5001", "robert", "old", true, "robert@nas.local"));
        using var provider = new SynologyDriveProvider(AccountId, _store, handler);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
    }

    // MARK: - Listing

    [Fact]
    public async Task RootListsSharesAndFoldersListTheirContents()
    {
        Api("SYNO.FileStation.List", "list_share", _ => Ok("""
            {"total":2,"offset":0,"shares":[
              {"isdir":true,"name":"home","path":"/home","additional":{"time":{"atime":1,"crtime":1,"ctime":1,"mtime":1700000000}}},
              {"isdir":true,"name":"photo","path":"/photo"}
            ]}
            """));
        Api("SYNO.FileStation.List", "list", _ => Ok("""
            {"total":3,"offset":0,"files":[
              {"isdir":true,"name":"Docs","path":"/home/Docs","additional":{"size":4096,"time":{"mtime":1710000000}}},
              {"isdir":false,"name":"report.pdf","path":"/home/report.pdf","additional":{"size":123456,"time":{"mtime":1720000000},"type":"PDF"}},
              {"isdir":false,"name":".profile","path":"/home/.profile","additional":{"size":12}}
            ]}
            """));
        using var provider = Connected();

        var shares = await provider.ListDirectoryAsync("/");
        Assert.Equal(["home", "photo"], shares.Select(s => s.Name));
        Assert.All(shares, s => Assert.True(s.IsDirectory));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), shares[0].ModificationDate);
        Assert.Equal(CloudFileItem.UnknownDate, shares[1].ModificationDate);
        Assert.Equal("[\"time\",\"real_path\"]", Param(Calls("SYNO.FileStation.List", "list_share").Single().Uri, "additional"));

        var items = await provider.ListDirectoryAsync("/home");
        Assert.Equal(["Docs", "report.pdf", ".profile"], items.Select(i => i.Name));
        Assert.True(items[0].IsDirectory);
        var report = items[1];
        Assert.Equal("/home/report.pdf", report.Path);
        Assert.Equal(123456, report.Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1720000000), report.ModificationDate);
        Assert.True(items[2].IsHidden);

        var list = Calls("SYNO.FileStation.List", "list").Single();
        Assert.Equal("/home", Param(list.Uri, "folder_path"));
        Assert.Equal("2", Param(list.Uri, "version"));
        Assert.Equal("[\"size\",\"time\",\"type\"]", Param(list.Uri, "additional"));
    }

    [Fact]
    public async Task MetadataComesFromGetInfo()
    {
        Api("SYNO.FileStation.List", "getinfo", request => Param(request, "path") == "[\"/home/a \\\"b\\\".txt\"]"
            ? Ok("{\"files\":[{\"isdir\":false,\"name\":\"a \\\"b\\\".txt\",\"path\":\"/home/a \\\"b\\\".txt\",\"additional\":{\"size\":7,\"time\":{\"mtime\":1700000000}}}]}")
            : Ok("{\"files\":[{\"code\":408,\"path\":\"/home/missing.txt\"}]}"));
        using var provider = Connected();

        var item = await provider.GetFileMetadataAsync("/home/a \"b\".txt");
        Assert.Equal(7, item.Size);
        Assert.Equal("a \"b\".txt", item.Name);

        var missing = await Assert.ThrowsAsync<CloudProviderException>(() => provider.GetFileMetadataAsync("/home/missing.txt"));
        Assert.Equal(CloudErrorKind.NotFound, missing.Kind);
        Assert.Equal("/home/missing.txt", missing.ItemPath);
    }

    [Fact]
    public async Task NoSuchFileIsNotFound()
    {
        Api("SYNO.FileStation.Delete", "delete", _ => Fail(408));
        Api("SYNO.FileStation.List", "list", _ => Fail(105));
        using var provider = Connected();

        var gone = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DeleteItemAsync("/home/gone"));
        Assert.Equal(CloudErrorKind.NotFound, gone.Kind);

        var denied = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/home"));
        Assert.Equal(CloudErrorKind.ServerError, denied.Kind);
        Assert.Equal(105, denied.StatusCode);
    }

    // MARK: - Transfers

    [Fact]
    public async Task UploadStreamsMultipartWithTheFileLast()
    {
        string? contentType = null;
        long? length = null;
        string? sent = null;
        _api[("SYNO.FileStation.Upload", "upload")] = (request, body) =>
        {
            contentType = request.Content?.Headers.ContentType?.ToString();
            length = request.Content?.Headers.ContentLength;
            sent = body;
            return OkEmpty();
        };
        var local = Path.Combine(_temp, "notes.txt");
        await File.WriteAllTextAsync(local, "hello synology");
        using var provider = Connected();

        long reported = 0;
        await provider.UploadFileAsync(local, "/home/Docs/new notes.txt", d => reported += d);

        Assert.NotNull(contentType);
        Assert.StartsWith("multipart/form-data; boundary=----FileFlussSynology", contentType);
        var boundary = contentType["multipart/form-data; boundary=".Length..];
        var expected =
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"path\"\r\n\r\n/home/Docs\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"create_parents\"\r\n\r\ntrue\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"overwrite\"\r\n\r\ntrue\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"new notes.txt\"\r\nContent-Type: application/octet-stream\r\n\r\n" +
            "hello synology" +
            $"\r\n--{boundary}--\r\n";
        Assert.Equal(expected, sent);
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), length);
        Assert.Equal(14, reported);
        var upload = _http.Single(HttpMethod.Post, "entry.cgi");
        Assert.Equal("sid-1", Param(upload.Uri, "_sid"));
    }

    [Fact]
    public async Task UploadRetriesOnceWhenTheSessionExpired()
    {
        Api("SYNO.FileStation.Upload", "upload", request => Param(request, "_sid") == "sid-1" ? Fail(119) : OkEmpty());
        var local = Path.Combine(_temp, "a.bin");
        await File.WriteAllBytesAsync(local, [1, 2, 3]);
        using var provider = Connected();

        await provider.UploadFileAsync(local, "/home/a.bin");

        Assert.Equal(2, _http.Requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task DownloadSavesTheFileAndTurnsAJsonFailureIntoAnError()
    {
        var bytes = Enumerable.Range(0, 70_000).Select(i => (byte)i).ToArray();
        Api("SYNO.FileStation.Download", "download", request => Param(request, "path") switch
        {
            "/home/a.bin" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
            "/home/data.json" => FakeHttpHandler.Json("{\"items\":[1,2,3]}"),
            _ => Fail(408),
        });
        using var provider = Connected();

        var target = Path.Combine(_temp, "a.bin");
        long reported = 0;
        await provider.DownloadFileAsync("/home/a.bin", target, d => reported += d);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        Assert.Equal(bytes.Length, reported);
        var call = Calls("SYNO.FileStation.Download", "download").First();
        Assert.Equal("download", Param(call.Uri, "mode"));

        // A real JSON file is still a file.
        var json = Path.Combine(_temp, "data.json");
        await provider.DownloadFileAsync("/home/data.json", json);
        Assert.Equal("{\"items\":[1,2,3]}", await File.ReadAllTextAsync(json));

        var missingTarget = Path.Combine(_temp, "missing.bin");
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DownloadFileAsync("/home/missing.bin", missingTarget));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.False(File.Exists(missingTarget));
    }

    // MARK: - Mutations

    [Fact]
    public async Task MutationsMapToFileStationCalls()
    {
        Api("SYNO.FileStation.Delete", "delete", _ => OkEmpty());
        Api("SYNO.FileStation.CreateFolder", "create", _ => Ok("{\"folders\":[{\"isdir\":true,\"name\":\"New\",\"path\":\"/home/New\"}]}"));
        Api("SYNO.FileStation.Rename", "rename", _ => Ok("{\"files\":[{\"isdir\":false,\"name\":\"b.txt\",\"path\":\"/home/b.txt\"}]}"));
        using var provider = Connected();

        await provider.DeleteItemAsync("/home/old");
        await provider.CreateDirectoryAsync("/home/New");
        await provider.RenameItemAsync("/home/a.txt", "b.txt");

        var delete = Calls("SYNO.FileStation.Delete", "delete").Single();
        Assert.Equal("/home/old", Param(delete.Uri, "path"));
        Assert.Equal("true", Param(delete.Uri, "recursive"));
        var create = Calls("SYNO.FileStation.CreateFolder", "create").Single();
        Assert.Equal("/home", Param(create.Uri, "folder_path"));
        Assert.Equal("New", Param(create.Uri, "name"));
        Assert.Equal("true", Param(create.Uri, "force_parent"));
        var rename = Calls("SYNO.FileStation.Rename", "rename").Single();
        Assert.Equal("/home/a.txt", Param(rename.Uri, "path"));
        Assert.Equal("b.txt", Param(rename.Uri, "name"));
    }

    [Fact]
    public async Task MovePollsTheTaskThenRenamesToTheTargetName()
    {
        Api("SYNO.FileStation.CopyMove", "start", _ => Ok("{\"taskid\":\"FileStation_1\"}"));
        var polls = 0;
        Api("SYNO.FileStation.CopyMove", "status", _ => Ok(Interlocked.Increment(ref polls) < 3 ? "{\"finished\":false}" : "{\"finished\":true}"));
        Api("SYNO.FileStation.Rename", "rename", _ => OkEmpty());
        Api("SYNO.FileStation.List", "getinfo", _ => Ok("{\"files\":[{\"code\":408,\"path\":\"/photo/archive/a.txt\"}]}"));
        using var provider = Connected();

        await provider.MoveItemAsync("/home/a.txt", "/photo/archive/b.txt");

        var start = Calls("SYNO.FileStation.CopyMove", "start").Single();
        Assert.Equal("3", Param(start.Uri, "version"));
        Assert.Equal("/home/a.txt", Param(start.Uri, "path"));
        Assert.Equal("/photo/archive", Param(start.Uri, "dest_folder_path"));
        Assert.Equal("true", Param(start.Uri, "remove_src"));
        Assert.Equal("true", Param(start.Uri, "overwrite"));
        Assert.Equal(3, polls);
        var rename = Calls("SYNO.FileStation.Rename", "rename").Single();
        Assert.Equal("/photo/archive/a.txt", Param(rename.Uri, "path"));
        Assert.Equal("b.txt", Param(rename.Uri, "name"));

        await provider.CopyItemAsync("/home/c.txt", "/photo/c.txt");
        Assert.Equal("false", Param(Calls("SYNO.FileStation.CopyMove", "start").Last().Uri, "remove_src"));
        Assert.Single(Calls("SYNO.FileStation.Rename", "rename"));
    }

    [Fact]
    public async Task ARenamedCopyOntoAnOccupiedNameIsLeftToTheByteCopy()
    {
        // CopyMove overwrites under the source name: "Keep both" into a folder that already has
        // a.txt would destroy the a.txt the user chose to keep.
        Api("SYNO.FileStation.List", "getinfo", _ => Ok("{\"files\":[{\"path\":\"/photo/a.txt\",\"name\":\"a.txt\",\"isdir\":false}]}"));
        Api("SYNO.FileStation.CopyMove", "start", _ => Ok("{\"taskid\":\"x\"}"));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/home/a.txt", "/photo/a (2).txt"));
        var sameFolder = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/home/a.txt", "/home/a (2).txt"));

        Assert.Equal(CloudErrorKind.NotImplemented, error.Kind);
        Assert.Equal(CloudErrorKind.NotImplemented, sameFolder.Kind);
        Assert.Empty(Calls("SYNO.FileStation.CopyMove", "start"));
    }

    [Fact]
    public async Task AFailedCopyMoveTaskIsAnError()
    {
        Api("SYNO.FileStation.CopyMove", "start", _ => Ok("{\"taskid\":\"t\"}"));
        Api("SYNO.FileStation.CopyMove", "status", _ => Ok("{\"finished\":true,\"error\":{\"code\":1000}}"));
        using var provider = Connected();

        await Assert.ThrowsAsync<CloudProviderException>(() => provider.MoveItemAsync("/home/a.txt", "/photo/a.txt"));
    }

    [Fact]
    public async Task SearchRunsATaskAndStopsIt()
    {
        Api("SYNO.FileStation.Search", "start", _ => Ok("{\"taskid\":\"search-1\"}"));
        Api("SYNO.FileStation.Search", "status", _ => Ok("{\"finished\":true}"));
        Api("SYNO.FileStation.Search", "list", _ => Ok("{\"files\":[{\"isdir\":false,\"name\":\"report.pdf\",\"path\":\"/home/report.pdf\",\"additional\":{\"size\":5}}]}"));
        Api("SYNO.FileStation.Search", "stop", _ => OkEmpty());
        using var provider = Connected();

        var results = await provider.SearchFilesAsync("report", "/home");

        Assert.Equal("report.pdf", Assert.Single(results!).Name);
        var start = Calls("SYNO.FileStation.Search", "start").Single();
        Assert.Equal("/home", Param(start.Uri, "folder_path"));
        Assert.Equal("report", Param(start.Uri, "pattern"));
        Assert.Equal("200", Param(Calls("SYNO.FileStation.Search", "list").Single().Uri, "limit"));
        Assert.Single(Calls("SYNO.FileStation.Search", "stop"));
    }

    [Fact]
    public async Task FolderSizeWalksTheTree()
    {
        Api("SYNO.FileStation.List", "list", request => Param(request, "folder_path") == "/home"
            ? Ok("{\"files\":[{\"isdir\":true,\"name\":\"d\",\"path\":\"/home/d\"},{\"isdir\":false,\"name\":\"a\",\"path\":\"/home/a\",\"additional\":{\"size\":10}}]}")
            : Ok("{\"files\":[{\"isdir\":false,\"name\":\"b\",\"path\":\"/home/d/b\",\"additional\":{\"size\":5}}]}"));
        using var provider = Connected();

        Assert.Equal(15, await provider.FolderSizeAsync("/home"));
    }

    // MARK: - Share links

    [Fact]
    public async Task CreateShareLinkSendsJsonParameters()
    {
        Api("SYNO.FileStation.Sharing", "create", _ => Ok("{\"links\":[{\"id\":\"AbC123\",\"url\":\"https://nas.local:5001/sharing/AbC123\",\"qrcode\":\"x\",\"error\":0}]}"));
        using var provider = Connected();
        var expiry = new DateTimeOffset(2026, 12, 24, 18, 0, 0, TimeSpan.Zero);

        var link = await provider.CreateShareLinkAsync("/home/report.pdf", new ShareLinkOptions { Password = "pw", Expiry = expiry });

        Assert.Equal(new Uri("https://nas.local:5001/sharing/AbC123"), link.Url);
        Assert.Null(link.DirectDownloadUrl);
        Assert.True(link.HasPassword);
        Assert.Equal(expiry, link.ExpiresAt);
        Assert.NotNull(link.Note);
        var create = Calls("SYNO.FileStation.Sharing", "create").Single();
        Assert.Equal("3", Param(create.Uri, "version"));
        Assert.Equal("\"/home/report.pdf\"", Param(create.Uri, "path"));
        Assert.Equal("pw", Param(create.Uri, "password"));
        Assert.Equal("\"2026-12-24\"", Param(create.Uri, "date_expired"));
    }

    [Fact]
    public async Task SharingErrorsUseTheirOwnMessages()
    {
        Api("SYNO.FileStation.Sharing", "create", _ => Fail(2001));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/home/a", ShareLinkOptions.Default));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Contains("maximum number of sharing links", error.Message);
    }

    [Fact]
    public async Task ExistingLinkIsFoundByPathUpdatedAndRemovedById()
    {
        Api("SYNO.FileStation.Sharing", "list", _ => Ok("""
            {"total":2,"offset":0,"links":[
              {"id":"other","url":"https://nas.local:5001/sharing/other","path":"/home/other.txt","date_expired":"","has_password":false},
              {"id":"AbC123","url":"https://nas.local:5001/sharing/AbC123","path":"/home/report.pdf","date_expired":"2026-12-24","has_password":true}
            ]}
            """));
        Api("SYNO.FileStation.Sharing", "edit", _ => OkEmpty());
        Api("SYNO.FileStation.Sharing", "getinfo", _ => Ok("""{"links":[{"id":"AbC123","url":"https://nas.local:5001/sharing/AbC123","path":"/home/report.pdf","date_expired":"0","has_password":false}]}"""));
        Api("SYNO.FileStation.Sharing", "delete", _ => OkEmpty());
        using var provider = Connected();

        var existing = await provider.ExistingShareLinkAsync("/home/report.pdf");
        Assert.NotNull(existing);
        Assert.Equal(new Uri("https://nas.local:5001/sharing/AbC123"), existing.Url);
        Assert.True(existing.HasPassword);
        Assert.Equal(new DateTimeOffset(2026, 12, 24, 0, 0, 0, TimeSpan.Zero), existing.ExpiresAt);
        Assert.Null(await provider.ExistingShareLinkAsync("/home/never-shared.txt"));

        var updated = await provider.UpdateShareLinkAsync("/home/report.pdf", ShareLinkOptions.Default);
        var edit = Calls("SYNO.FileStation.Sharing", "edit").Single();
        Assert.Equal("\"AbC123\"", Param(edit.Uri, "id"));
        Assert.Equal(string.Empty, Param(edit.Uri, "password"));
        Assert.Equal("\"0\"", Param(edit.Uri, "date_expired"));
        Assert.Equal("0", Param(edit.Uri, "expire_times"));
        Assert.False(updated.HasPassword);
        Assert.Null(updated.ExpiresAt);

        await provider.RemoveShareLinkAsync("/home/report.pdf");
        Assert.Equal("\"AbC123\"", Param(Calls("SYNO.FileStation.Sharing", "delete").Single().Uri, "id"));

        var notShared = await Assert.ThrowsAsync<CloudProviderException>(() => provider.RemoveShareLinkAsync("/home/never-shared.txt"));
        Assert.Equal(CloudErrorKind.CommandFailed, notShared.Kind);
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("2026-12-24", "2026-12-24T00:00:00+00:00")]
    [InlineData("2026-12-24 13:30:00", "2026-12-24T13:30:00+00:00")]
    public void ExpiryDatesReadBack(string? raw, string? expected)
        => Assert.Equal(expected is null ? null : DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), SynologyDriveApiClient.ParseExpiryDate(raw));
}
