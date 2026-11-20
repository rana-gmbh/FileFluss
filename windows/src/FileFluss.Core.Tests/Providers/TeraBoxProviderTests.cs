// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.TeraBox;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

public sealed class TeraBoxProviderTests : IDisposable
{
    private static readonly TeraBoxAppCredentials App = new("cid", "csecret", "psecret");

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ff-terabox-" + Guid.NewGuid().ToString("N"));

    public TeraBoxProviderTests() => Directory.CreateDirectory(_temp);

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private string Key => $"terabox.{_accountId}";

    private TeraBoxProvider SignedIn(DateTimeOffset? expiresAt = null)
    {
        _store.Save(Key, new TeraBoxCredentials("AT", "RT", expiresAt ?? DateTimeOffset.UtcNow.AddDays(1), "api.terabox.test", "up.terabox.test", 12345));
        return new TeraBoxProvider(_accountId, _store, _http, App) { Delay = (_, _) => Task.CompletedTask };
    }

    private sealed class RecordingPrompt : IConnectPrompt
    {
        public List<(byte[] Png, string Caption)> Images { get; } = [];

        public bool Dismiss { get; init; }

        public void ShowStatus(string? message)
        {
        }

        public Task<string?> RequestCodeAsync(string title, string message, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options, CancellationToken cancellationToken) => Task.FromResult<int?>(0);

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task ShowImageAsync(byte[] png, string caption, Task until, CancellationToken cancellationToken)
        {
            Images.Add((png, caption));
            return Dismiss ? Task.CompletedTask : until;
        }
    }

    // MARK: - Connect

    [Fact]
    public async Task WithoutProvisionedAppCredentialsConnectIsNotImplemented()
    {
        var provider = new TeraBoxProvider(_accountId, _store, _http, null);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty(), new RecordingPrompt()));

        Assert.Equal(CloudErrorKind.NotImplemented, error.Kind);
        Assert.Empty(_http.Requests);
        Assert.False(TeraBoxAppCredentials.Default.IsConfigured);
    }

    [Fact]
    public async Task DeviceCodeFlowShowsTheQrPollsAndStoresTokens()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        _http.OnJson(HttpMethod.Get, "/oauth/devicecode?client_id=cid", $$"""
            { "errno": 0, "data": { "device_code": "DEV", "qrcode_url": "data:image/png;base64,{{Convert.ToBase64String(png)}}", "expires_in": 300, "interval": 5 } }
            """);
        var polls = 0;
        _http.On(HttpMethod.Post, "/oauth/gettoken", (_, _) => Interlocked.Increment(ref polls) == 1
            ? FakeHttpHandler.Json("""{ "errno": 400001 }""")
            : FakeHttpHandler.Json("""{ "errno": 0, "data": { "access_token": "AT", "refresh_token": "RT", "expires_in": 172800 } }"""));
        _http.OnJson(HttpMethod.Post, "/oauth/tokeninfo", """{ "errno": 0, "data": { "api_domain": "api.terabox.test", "upload_domain": "up.terabox.test", "user_id": 12345 } }""");
        var provider = new TeraBoxProvider(_accountId, _store, _http, App) { Delay = (_, _) => Task.CompletedTask };
        var prompt = new RecordingPrompt();

        var result = await provider.ConnectAsync(ConnectValues.Empty(), prompt);

        Assert.Equal("12345", result.AccountLabel);
        Assert.True(provider.IsAuthenticated);
        Assert.Equal(png, prompt.Images.Single().Png);
        Assert.Equal(2, polls);

        var stored = _store.Load<TeraBoxCredentials>(Key)!;
        Assert.Equal(("AT", "RT", "api.terabox.test", "up.terabox.test", 12345L), (stored.AccessToken, stored.RefreshToken, stored.ApiDomain, stored.UploadDomain, stored.UserId));
        Assert.True(stored.ExpiresAt > DateTimeOffset.UtcNow.AddDays(1));

        var exchange = _http.Requests.First(r => r.Uri.AbsolutePath == "/oauth/gettoken");
        Assert.Contains("name=\"grant_type\"", exchange.Body, StringComparison.Ordinal);
        Assert.Contains("device_code", exchange.Body, StringComparison.Ordinal);
        Assert.Contains("name=\"sign\"", exchange.Body, StringComparison.Ordinal);
        Assert.Contains("access_token", _http.Single(HttpMethod.Post, "/oauth/tokeninfo").Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredQrFailsAndLeavesStoredTokensAlone()
    {
        SignedIn();
        var before = _store.Load(Key);
        _http.OnJson(HttpMethod.Get, "/oauth/devicecode", """{ "data": { "device_code": "DEV", "qrcode_url": "", "expires_in": 0, "interval": 5 } }""");
        var provider = new TeraBoxProvider(_accountId, _store, _http, App) { Delay = (_, _) => Task.CompletedTask };

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty(), new RecordingPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal(before, _store.Load(Key));
    }

    [Fact]
    public async Task DismissingTheQrStopsPolling()
    {
        _http.OnJson(HttpMethod.Get, "/oauth/devicecode", $$"""{ "data": { "device_code": "DEV", "qrcode_url": "{{Convert.ToBase64String([1, 2, 3])}}", "expires_in": 300, "interval": 2 } }""")
             .OnJson(HttpMethod.Post, "/oauth/gettoken", """{ "errno": 400001 }""");
        var provider = new TeraBoxProvider(_accountId, _store, _http, App) { Delay = (span, token) => Task.Delay(TimeSpan.FromMilliseconds(20), token) };

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty(), new RecordingPrompt { Dismiss = true }));

        Assert.Equal(CloudErrorKind.Cancelled, error.Kind);
        Assert.Null(_store.Load(Key));
    }

    [Fact]
    public void SignIsMd5OfTheJoinedSecrets()
    {
        var expected = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("cid_1700000000_csecret_psecret")));
        Assert.Equal(expected, TeraBoxApiClient.Sign(App, "1700000000"));
    }

    [Fact]
    public void QrDataUrisDecode()
    {
        Assert.Equal([1, 2, 3], TeraBoxApiClient.DecodeDataUriPng("data:image/png;base64,AQID"));
        Assert.Equal([1, 2, 3], TeraBoxApiClient.DecodeDataUriPng("AQID"));
        Assert.Null(TeraBoxApiClient.DecodeDataUriPng("data:image/png;base64,***"));
    }

    // MARK: - Listing and errors

    [Fact]
    public async Task ListingParsesEntriesAndSendsTheToken()
    {
        _http.OnJson(HttpMethod.Get, "/openapi/api/list", """
            { "errno": 0, "list": [
              { "server_filename": "Docs", "isdir": 1, "fs_id": 11, "path": "/FileFluss/Docs", "server_mtime": 1696600109 },
              { "server_filename": "a b.txt", "isdir": 0, "size": 2048, "fs_id": 12, "path": "/FileFluss/a b.txt", "server_mtime": "1696600200", "md5": "abc123" }
            ] }
            """);
        var provider = SignedIn();

        var items = await provider.ListDirectoryAsync("/FileFluss");

        Assert.Equal(["Docs", "a b.txt"], items.Select(i => i.Name));
        Assert.True(items[0].IsDirectory);
        Assert.Equal(("12", "/FileFluss/a b.txt", 2048L, "abc123"), (items[1].Id, items[1].Path, items[1].Size, items[1].Checksum));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1696600200), items[1].ModificationDate);
        Assert.Equal(
            "https://api.terabox.test/openapi/api/list?dir=%2FFileFluss&order=name&desc=0&num=1000&page=1&access_tokens=AT",
            _http.Requests.Single().Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("""{ "errno": -9 }""", HttpStatusCode.OK, CloudErrorKind.NotFound)]
    [InlineData("""{ "errno": -6 }""", HttpStatusCode.OK, CloudErrorKind.Unauthorized)]
    [InlineData("""{ "errno": 31066 }""", HttpStatusCode.OK, CloudErrorKind.ServerError)]
    [InlineData("""{ "error": "invalid token" }""", HttpStatusCode.Unauthorized, CloudErrorKind.Unauthorized)]
    [InlineData("oops", HttpStatusCode.BadGateway, CloudErrorKind.ServerError)]
    public async Task ErrorsMap(string body, HttpStatusCode status, CloudErrorKind expected)
    {
        _http.OnJson(HttpMethod.Get, "/openapi/api/list", body, status);
        var provider = SignedIn();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/missing"));
        Assert.Equal(expected, error.Kind);
    }

    [Fact]
    public async Task MetadataComesFromTheParentListing()
    {
        _http.OnJson(HttpMethod.Get, "/openapi/api/list", """{ "list": [ { "server_filename": "x.bin", "isdir": 0, "size": 5, "fs_id": 1, "path": "/d/x.bin" } ] }""");
        var provider = SignedIn();

        Assert.Equal(5, (await provider.GetFileMetadataAsync("/d/x.bin")).Size);
        var missing = await Assert.ThrowsAsync<CloudProviderException>(() => provider.GetFileMetadataAsync("/d/y.bin"));
        Assert.Equal(CloudErrorKind.NotFound, missing.Kind);
    }

    // MARK: - Token refresh

    [Fact]
    public async Task ConcurrentCallersShareOneRefreshAndItIsPersisted()
    {
        var refreshes = 0;
        _http.On(HttpMethod.Post, "/oauth/refreshtoken", (_, _) =>
        {
            Interlocked.Increment(ref refreshes);
            Thread.Sleep(200);
            return FakeHttpHandler.Json("""{ "errno": 0, "data": { "access_token": "AT2", "refresh_token": "RT2", "expires_in": 172800 } }""");
        });
        _http.OnJson(HttpMethod.Get, "/openapi/api/list", """{ "errno": 0, "list": [] }""");
        var provider = SignedIn(DateTimeOffset.UtcNow.AddHours(-1));

        await Task.WhenAll(
            Task.Run(() => provider.ListDirectoryAsync("/")),
            Task.Run(() => provider.ListDirectoryAsync("/")),
            Task.Run(() => provider.ListDirectoryAsync("/")));

        Assert.Equal(1, refreshes);
        var stored = _store.Load<TeraBoxCredentials>(Key)!;
        Assert.Equal(("AT2", "RT2"), (stored.AccessToken, stored.RefreshToken));
        Assert.All(
            _http.Requests.Where(r => r.Uri.AbsolutePath == "/openapi/api/list"),
            r => Assert.EndsWith("access_tokens=AT2", r.Uri.AbsoluteUri, StringComparison.Ordinal));
        Assert.Contains("RT", _http.Single(HttpMethod.Post, "/oauth/refreshtoken").Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedRefreshAsksForSignIn()
    {
        _http.OnJson(HttpMethod.Post, "/oauth/refreshtoken", """{ "errno": 200003 }""");
        var provider = SignedIn(DateTimeOffset.UtcNow.AddHours(-1));

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.True(error.NeedsReauthentication);
    }

    // MARK: - Transfers

    [Fact]
    public async Task UploadSendsMd5ListShardsAndCreate()
    {
        var local = Path.Combine(_temp, "big.bin");
        var data = new byte[TeraBoxApiClient.SliceSize + 10];
        new Random(7).NextBytes(data);
        await File.WriteAllBytesAsync(local, data);
        var expectedMd5s = new[]
        {
            Convert.ToHexStringLower(MD5.HashData(data.AsSpan(0, TeraBoxApiClient.SliceSize))),
            Convert.ToHexStringLower(MD5.HashData(data.AsSpan(TeraBoxApiClient.SliceSize))),
        };
        _http.OnJson(HttpMethod.Post, "/openapi/api/precreate", """{ "errno": 0, "uploadid": "UP 1", "return_type": 1 }""")
             .OnJson(HttpMethod.Post, "/rest/2.0/pcs/superfile2", """{ "md5": "x" }""")
             .OnJson(HttpMethod.Post, "/openapi/api/create", """{ "errno": 0 }""");
        var provider = SignedIn();

        long reported = 0;
        await provider.UploadFileAsync(local, "/FileFluss/big.bin", d => Interlocked.Add(ref reported, d));

        Assert.Equal(data.Length, reported);
        var precreate = HttpUtility.ParseQueryString(_http.Single(HttpMethod.Post, "/precreate").Body!);
        Assert.Equal("/FileFluss/big.bin", precreate["path"]);
        Assert.Equal(expectedMd5s, JsonSerializer.Deserialize<string[]>(precreate["block_list"]!)!);

        var shards = _http.Requests.Where(r => r.Uri.AbsolutePath == "/rest/2.0/pcs/superfile2").ToList();
        Assert.Equal(2, shards.Count);
        Assert.All(shards, s => Assert.Equal("up.terabox.test", s.Uri.Host));
        Assert.Equal(["0", "1"], shards.Select(s => HttpUtility.ParseQueryString(s.Uri.Query)["partseq"]));
        Assert.Equal("UP 1", HttpUtility.ParseQueryString(shards[0].Uri.Query)["uploadid"]);
        Assert.Contains("Content-Disposition: form-data; name=\"file\"; filename=\"blob\"", shards[1].Body, StringComparison.Ordinal);

        var create = HttpUtility.ParseQueryString(_http.Single(HttpMethod.Post, "/openapi/api/create").Body!);
        Assert.Equal((data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), "UP 1", "3"), (create["size"], create["uploadid"], create["rtype"]));
    }

    [Fact]
    public async Task RapidUploadSkipsTheShards()
    {
        var local = Path.Combine(_temp, "empty.txt");
        await File.WriteAllBytesAsync(local, []);
        _http.OnJson(HttpMethod.Post, "/openapi/api/precreate", """{ "errno": 0, "return_type": 2 }""");
        var provider = SignedIn();

        await provider.UploadFileAsync(local, "/empty.txt");

        var precreate = HttpUtility.ParseQueryString(_http.Single(HttpMethod.Post, "/precreate").Body!);
        Assert.Equal(["d41d8cd98f00b204e9800998ecf8427e"], JsonSerializer.Deserialize<string[]>(precreate["block_list"]!)!);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task DownloadResolvesTheDlinkAndSendsTheTeraBoxAgent()
    {
        _http.OnJson(HttpMethod.Get, "/openapi/api/filemetas", """{ "errno": 0, "info": [ { "dlink": "https://d.terabox.test/file/abc?fid=1" } ] }""")
             .OnBytes(HttpMethod.Get, "https://d.terabox.test/file/abc", [9, 8, 7, 6]);
        var provider = SignedIn();
        var target = Path.Combine(_temp, "out.bin");

        long reported = 0;
        await provider.DownloadFileAsync("/FileFluss/a b.txt", target, d => reported += d);

        Assert.Equal([9, 8, 7, 6], await File.ReadAllBytesAsync(target));
        Assert.Equal(4, reported);
        var metas = _http.Single(HttpMethod.Get, "/openapi/api/filemetas");
        Assert.Contains("target=%5B%22%252FFileFluss%252Fa%2520b.txt%22%5D&dlink=1", metas.Uri.AbsoluteUri, StringComparison.Ordinal);
        var file = _http.Single(HttpMethod.Get, "https://d.terabox.test/");
        Assert.Equal("pan.baidu.com", file.Headers["User-Agent"]);
        Assert.Equal("https://d.terabox.test/file/abc?fid=1&access_tokens=AT", file.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task DownloadWithoutDlinkIsNotFound()
    {
        _http.OnJson(HttpMethod.Get, "/openapi/api/filemetas", """{ "errno": 0, "info": [] }""");
        var provider = SignedIn();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DownloadFileAsync("/nope", Path.Combine(_temp, "x")));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    // MARK: - Mutations and quota

    [Fact]
    public async Task RenameAndMoveSendEscapedFileLists()
    {
        _http.OnJson(HttpMethod.Post, "/openapi/api/filemanager", """{ "errno": 0 }""");
        var provider = SignedIn();

        await provider.RenameItemAsync("/a \"b\".txt", "c.txt");
        await provider.MoveItemAsync("/c.txt", "/Archive/d.txt");

        var requests = _http.Requests.ToList();
        Assert.Contains("opera=rename", requests[0].Uri.Query, StringComparison.Ordinal);
        using (var rename = JsonDocument.Parse(HttpUtility.ParseQueryString(requests[0].Body!)["filelist"]!))
        {
            Assert.Equal("/a \"b\".txt", rename.RootElement[0].GetProperty("path").GetString());
            Assert.Equal("c.txt", rename.RootElement[0].GetProperty("newname").GetString());
        }

        using var move = JsonDocument.Parse(HttpUtility.ParseQueryString(requests[1].Body!)["filelist"]!);
        Assert.Equal("/Archive", move.RootElement[0].GetProperty("dest").GetString());
        Assert.Equal("d.txt", move.RootElement[0].GetProperty("newname").GetString());
    }

    [Fact]
    public async Task CreatingAnExistingFolderSucceedsQuietly()
    {
        _http.OnJson(HttpMethod.Post, "/openapi/api/create", """{ "errno": -8 }""");
        var provider = SignedIn();

        await provider.CreateDirectoryAsync("/Docs");

        var body = HttpUtility.ParseQueryString(_http.Single(HttpMethod.Post, "/create").Body!);
        Assert.Equal(("/Docs", "1", "0"), (body["path"], body["isdir"], body["rtype"]));
    }

    [Fact]
    public async Task DeleteReportsErrno()
    {
        _http.OnJson(HttpMethod.Post, "/openapi/api/filemanager", """{ "errno": -9 }""");
        var provider = SignedIn();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DeleteItemAsync("/gone.txt"));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal("[\"/gone.txt\"]", HttpUtility.ParseQueryString(_http.Requests.Single().Body!)["filelist"]);
    }

    [Fact]
    public async Task QuotaParsesAndFailsSoft()
    {
        _http.OnJson(HttpMethod.Get, "/openapi/api/quota", """{ "errno": 0, "total": 1099511627776, "used": 1073741824 }""");
        var provider = SignedIn();

        var quota = await provider.StorageQuotaAsync();
        Assert.Equal((1073741824L, (long?)1099511627776L), (quota!.UsedBytes, quota.TotalBytes));

        var failing = new FakeHttpHandler().OnText(HttpMethod.Get, "/openapi/api/quota", "down", HttpStatusCode.ServiceUnavailable);
        Assert.Null(await new TeraBoxProvider(_accountId, _store, failing, App).StorageQuotaAsync());
    }

    [Fact]
    public void FormHasNothingToFillIn()
    {
        var provider = new TeraBoxProvider(_accountId, _store, _http, null);
        Assert.All(provider.ConnectSpec.Modes.SelectMany(m => m.Fields), f => Assert.False(f.HasValue));
        Assert.Equal(CloudProviderType.TeraBox, provider.ProviderType);
    }
}
