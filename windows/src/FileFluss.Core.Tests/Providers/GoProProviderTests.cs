// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.GoPro;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>Port of the macOS GoProMediaListTests: the media-list decoder and tree builder against representative firmware JSON.</summary>
public sealed class GoProMediaListTests
{
    internal const string SampleJson = """
        {
          "id": "1696600000",
          "media": [
            {
              "d": "100GOPRO",
              "fs": [
                { "n": "GH010397.MP4", "cre": "1696600100", "mod": "1696600109", "s": "11587660" },
                { "n": "GOPR0987.JPG", "cre": "1696600200", "mod": "1696600200", "s": "4194304" }
              ]
            },
            {
              "d": "101GOPRO",
              "fs": [
                { "n": "GH010001.MP4", "cre": "1696700000", "mod": "1696700000", "s": "2048" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void RootListsEachOnCardDirectoryAsAFolder()
    {
        var root = GoProMediaList.Parse(SampleJson).Items("/");

        Assert.Equal(2, root.Count);
        Assert.All(root, i => Assert.True(i.IsDirectory));
        Assert.Equal(["100GOPRO", "101GOPRO"], root.Select(i => i.Name).Order());

        // Folder size is the sum of its files (11587660 + 4194304).
        var first = root.Single(i => i.Name == "100GOPRO");
        Assert.Equal(15_781_964, first.Size);
        Assert.Equal("/100GOPRO", first.Path);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_696_600_200), first.ModificationDate);
    }

    [Fact]
    public void DirectoryListsItsFilesWithFullPathsAndParsedSizes()
    {
        var files = GoProMediaList.Parse(SampleJson).Items("/100GOPRO");

        Assert.Equal(2, files.Count);
        Assert.All(files, i => Assert.False(i.IsDirectory));
        var video = files.Single(i => i.Name == "GH010397.MP4");
        Assert.Equal("/100GOPRO/GH010397.MP4", video.Path);
        Assert.Equal(11_587_660, video.Size);

        // mod = 1696600109 → that exact instant.
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_696_600_109), video.ModificationDate);
    }

    [Fact]
    public void UnknownDirectoryYieldsNoItems()
        => Assert.Empty(GoProMediaList.Parse(SampleJson).Items("/999GOPRO"));

    [Fact]
    public void SingleFileLookupByPath()
    {
        var list = GoProMediaList.Parse(SampleJson);
        var item = list.File("/101GOPRO/GH010001.MP4");

        Assert.NotNull(item);
        Assert.Equal(2048, item!.Size);
        Assert.False(item.IsDirectory);
        Assert.Null(list.File("/101GOPRO/missing.mp4"));

        // A directory path is not a file.
        Assert.Null(list.File("/101GOPRO"));
        Assert.True(list.Folder("/101GOPRO")!.IsDirectory);
    }

    [Fact]
    public void ToleratesNumericSizeAndDateFields()
    {
        var item = GoProMediaList.Parse("""{ "media": [ { "d": "100GOPRO", "fs": [ { "n": "A.JPG", "s": 1234, "mod": 1696600109 } ] } ] }""").Items("/100GOPRO").Single();

        Assert.Equal(1234, item.Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_696_600_109), item.ModificationDate);
    }

    [Fact]
    public void MissingDatesFallBackToUnknown()
    {
        var item = GoProMediaList.Parse("""{ "media": [ { "d": "100GOPRO", "fs": [ { "n": "A.JPG", "s": "10" } ] } ] }""").Items("/100GOPRO").Single();

        Assert.Equal(CloudFileItem.UnknownDate, item.ModificationDate);
    }

    [Theory]
    [InlineData("""{ "id": "x" }""")]
    [InlineData("<html/>")]
    public void MalformedListsAreInvalidResponses(string json)
        => Assert.Equal(CloudErrorKind.InvalidResponse, Assert.Throws<CloudProviderException>(() => GoProMediaList.Parse(json)).Kind);
}

/// <summary>Port of the macOS GoProDiscoveryTests plus the Windows enumeration, scan and mDNS parsing.</summary>
public sealed class GoProDiscoveryTests
{
    [Fact]
    public void RecognisesTheGoProUsbAddressRange()
    {
        Assert.True(GoProDiscovery.IsInGoProUsbRange("172.16.0.1"));
        Assert.True(GoProDiscovery.IsInGoProUsbRange("172.24.106.50"));
        Assert.True(GoProDiscovery.IsInGoProUsbRange("172.31.255.254"));

        // Outside the range.
        Assert.False(GoProDiscovery.IsInGoProUsbRange("172.15.0.1"));
        Assert.False(GoProDiscovery.IsInGoProUsbRange("172.32.0.1"));
        Assert.False(GoProDiscovery.IsInGoProUsbRange("192.168.1.10"));
        Assert.False(GoProDiscovery.IsInGoProUsbRange("10.5.5.9"));
        Assert.False(GoProDiscovery.IsInGoProUsbRange("not.an.ip"));
    }

    [Fact]
    public void UsbCandidatesAreTheDot51OfEachUsbSubnet()
    {
        var candidates = GoProDiscovery.UsbCandidateAddresses(
        [
            IPAddress.Parse("172.24.106.50"),
            IPAddress.Parse("192.168.1.10"),
            IPAddress.Parse("172.20.110.51"),
            IPAddress.Parse("172.24.106.52"),
            IPAddress.IPv6Loopback,
        ]);

        Assert.Equal(["172.24.106.51"], candidates);
    }

    [Fact]
    public async Task ScanConfirmsCandidatesAndDetectsBothDialects()
    {
        var http = new FakeHttpHandler()
            .OnJson(HttpMethod.Get, "http://172.24.106.51:8080/gopro/camera/state", "{}")
            .OnText(HttpMethod.Get, "http://192.168.1.77:8080/gopro/camera/state", "no", HttpStatusCode.NotFound)
            .OnJson(HttpMethod.Get, "http://192.168.1.77:8080/gp/gpControl/status", "{}");
        var discovery = new GoProDiscovery(
            http,
            () => [IPAddress.Parse("172.24.106.50"), IPAddress.Parse("192.168.1.10")],
            (_, _) => Task.FromResult<IReadOnlyList<string>>(["192.168.1.77", "172.24.106.51"]));

        var cameras = await discovery.ScanAsync();

        Assert.Equal(
            [
                new GoProCamera("GoPro Camera (USB)", "172.24.106.51", GoProConnectionMode.WiredUsb),
                new GoProCamera("GoPro Camera (USB)", "192.168.1.77", GoProConnectionMode.WiredUsb),
            ],
            cameras);

        // The access point was probed and stayed silent.
        Assert.Contains(http.Requests, r => r.Uri.Host == "10.5.5.9");
    }

    [Fact]
    public async Task ResolvePrefersTheLastKnownAddress()
    {
        var http = new FakeHttpHandler().OnJson(HttpMethod.Get, "http://172.24.106.51:8080/gopro/camera/state", "{}");
        var discovery = new GoProDiscovery(http, () => [], (_, _) => Task.FromResult<IReadOnlyList<string>>([]));

        Assert.Equal("172.24.106.51", await discovery.ResolveAsync("172.24.106.51", GoProConnectionMode.WiredUsb));
        Assert.Null(await discovery.ResolveAsync("172.30.1.51", GoProConnectionMode.WiredUsb));
    }

    // MARK: - mDNS

    private static byte[] Name(string dotted)
    {
        var bytes = new List<byte>();
        foreach (var label in dotted.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0);
        return [.. bytes];
    }

    private static byte[] Record(byte[] name, int type, byte[] data)
        => [.. name, 0, (byte)type, 0, 1, 0, 0, 0x11, 0x94, (byte)(data.Length >> 8), (byte)data.Length, .. data];

    private static byte[] Response(int answers, int additional, params byte[][] records)
        => [0x12, 0x34, 0x84, 0x00, 0, 0, 0, (byte)answers, 0, 0, 0, (byte)additional, .. records.SelectMany(r => r)];

    [Fact]
    public void MdnsResponsesYieldTheAnnouncedAddress()
    {
        // PTR _gopro-web._tcp.local → "GoPro 1234" + pointer back to the service name at offset 12.
        var ptr = Record(Name("_gopro-web._tcp.local"), 12, [10, .. Encoding.ASCII.GetBytes("GoPro 1234"), 0xC0, 12]);
        var a = Record(Name("gopro.local"), 1, [192, 168, 1, 77]);
        var sender = IPAddress.Parse("192.168.1.99");

        Assert.Equal(["192.168.1.77"], GoProBonjour.ParseResponse(Response(1, 1, ptr, a), "_gopro-web._tcp.local", sender));
        Assert.Equal(["192.168.1.99"], GoProBonjour.ParseResponse(Response(1, 0, ptr), "_gopro-web._tcp.local", sender));
        Assert.Empty(GoProBonjour.ParseResponse(Response(0, 1, a), "_gopro-web._tcp.local", sender));
        Assert.Empty(GoProBonjour.ParseResponse(GoProBonjour.BuildQuery("_gopro-web._tcp.local"), "_gopro-web._tcp.local", sender));
        Assert.Empty(GoProBonjour.ParseResponse([0x12, 0x34, 0x84, 0, 0, 0, 0, 5], "_gopro-web._tcp.local", sender));
    }

    [Fact]
    public void MdnsQueryAsksForThePtrRecord()
    {
        var query = GoProBonjour.BuildQuery("_gopro-web._tcp.local");

        Assert.Equal([0, 0, 0, 1, 0, 0, 0, 0, 0, 0], query[2..12]);
        Assert.Equal([.. Name("_gopro-web._tcp.local"), 0, 12, 0, 1], query[12..]);
    }
}

public sealed class GoProProviderTests : IDisposable
{
    private const string Usb = "http://172.24.106.51:8080";

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ff-gopro-" + Guid.NewGuid().ToString("N"));
    private IReadOnlyList<string> _bonjour = [];

    public GoProProviderTests() => Directory.CreateDirectory(_temp);

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private string Key => $"gopro.{_accountId}";

    private GoProDiscovery Discovery()
        => new(_http, () => [IPAddress.Parse("172.24.106.50")], (_, _) => Task.FromResult(_bonjour));

    private GoProProvider Provider() => new(_accountId, _store, _http, Discovery());

    private GoProProvider Paired(string ip = "172.24.106.51")
    {
        _store.Save(Key, new GoProConnection("GoPro Camera (USB)", GoProConnectionMode.WiredUsb, ip));
        return Provider();
    }

    private void ModernCamera(string origin = Usb)
        => _http.OnJson(HttpMethod.Get, $"{origin}/gopro/camera/state", "{}")
                .OnJson(HttpMethod.Get, $"{origin}/gopro/media/list", GoProMediaListTests.SampleJson);

    private static ConnectValues Cohn(string ip, string user, string password)
        => new("cohn", new Dictionary<string, string> { ["ip"] = ip, ["username"] = user, ["password"] = password });

    // MARK: - Connect

    [Fact]
    public async Task ScanModePicksAmongSeveralCamerasAndStoresTheChoice()
    {
        ModernCamera();
        _http.OnText(HttpMethod.Get, "http://192.168.1.77:8080/gopro/camera/state", "no", HttpStatusCode.NotFound)
             .OnJson(HttpMethod.Get, "http://192.168.1.77:8080/gp/gpControl/status", "{}");
        _bonjour = ["192.168.1.77"];
        var prompt = new ScriptedConnectPrompt();
        prompt.Choices.Enqueue(1);
        var provider = Provider();

        var result = await provider.ConnectAsync(ConnectValues.Empty("scan"), prompt);

        Assert.Equal("GoPro Camera (USB)", result.DisplayName);
        Assert.True(provider.IsAuthenticated);
        var stored = _store.Load<GoProConnection>(Key)!;
        Assert.Equal(("192.168.1.77", GoProConnectionMode.WiredUsb), (stored.LastKnownIp, stored.Mode));
        Assert.Contains("\"wiredUSB\"", Encoding.UTF8.GetString(_store.Load(Key)!), StringComparison.Ordinal);
        Assert.Contains(prompt.StatusLines, line => line is not null);

        // Wired cameras get wired control switched on before use.
        Assert.Contains(_http.Requests, r => r.Uri.PathAndQuery == "/gopro/camera/control/wired_usb?p=1" && r.Uri.Host == "192.168.1.77");
    }

    [Fact]
    public async Task ScanWithoutCamerasFails()
    {
        var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("scan"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Null(_store.Load(Key));
    }

    [Fact]
    public async Task CohnModeVerifiesOverHttpsWithBasicAuth()
    {
        _http.OnJson(HttpMethod.Get, "https://192.168.1.20/gopro/camera/state", "{}");
        var provider = Provider();

        var result = await provider.ConnectAsync(Cohn(" https://192.168.1.20:443/ ", "gopro", "s3cret"), new ScriptedConnectPrompt());

        Assert.Equal("GoPro Camera (Home Wi-Fi)", result.DisplayName);
        var probe = _http.Single(HttpMethod.Get, "/gopro/camera/state");
        Assert.Equal("https://192.168.1.20/gopro/camera/state", probe.Uri.AbsoluteUri);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("gopro:s3cret")), probe.Headers["Authorization"]);
        var stored = _store.Load<GoProConnection>(Key)!;
        Assert.Equal((GoProConnectionMode.Cohn, "192.168.1.20", "gopro", "s3cret"), (stored.Mode, stored.LastKnownIp, stored.CohnUsername, stored.CohnPassword));
    }

    [Fact]
    public async Task CohnWithWrongPasswordLeavesTheStoredCameraAlone()
    {
        var provider = Paired();
        var before = _store.Load(Key);
        _http.OnText(HttpMethod.Get, "https://192.168.1.20/gopro/camera/state", "denied", HttpStatusCode.Unauthorized);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(Cohn("192.168.1.20", "gopro", "wrong"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal(before, _store.Load(Key));
    }

    [Fact]
    public async Task CohnUnreachableIsANetworkError()
    {
        var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(Cohn("192.168.1.21", "gopro", "x"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.NetworkError, error.Kind);
        Assert.False(provider.IsAuthenticated);
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20")]
    [InlineData("https://192.168.1.20/", "192.168.1.20")]
    [InlineData("192.168.1.20:443", "192.168.1.20")]
    [InlineData(" gopro.local ", "gopro.local")]
    public void CohnAddressesAreNormalized(string input, string expected)
        => Assert.Equal(expected, GoProProvider.NormalizeHost(input));

    // MARK: - Browsing

    [Fact]
    public async Task ListingResolvesTheCameraAndCachesTheCardIndex()
    {
        ModernCamera();
        var provider = Paired();

        var root = await provider.ListDirectoryAsync("/");
        var files = await provider.ListDirectoryAsync("/100GOPRO");

        Assert.Equal(["100GOPRO", "101GOPRO"], root.Select(i => i.Name));
        Assert.Equal(["GH010397.MP4", "GOPR0987.JPG"], files.Select(i => i.Name));
        Assert.Single(_http.Requests, r => r.Uri.AbsolutePath == "/gopro/media/list");
        Assert.Equal(15_781_964, await provider.FolderSizeAsync("/100GOPRO"));
        Assert.Equal(11_587_660, (await provider.GetFileMetadataAsync("/100GOPRO/GH010397.MP4")).Size);
        Assert.True((await provider.GetFileMetadataAsync("/101GOPRO")).IsDirectory);
    }

    [Fact]
    public async Task AnUnreachableCameraIsANetworkError()
    {
        var provider = Paired("172.30.1.51");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.NetworkError, error.Kind);
    }

    [Fact]
    public async Task ANewAddressIsRediscoveredAndRemembered()
    {
        // The camera moved from 172.20.9.51 to the subnet the PC now has (172.24.106.0/24).
        ModernCamera();
        var provider = Paired("172.20.9.51");

        Assert.Equal(2, (await provider.ListDirectoryAsync("/")).Count);

        Assert.Equal("172.24.106.51", _store.Load<GoProConnection>(Key)!.LastKnownIp);
    }

    [Fact]
    public async Task ADroppedConnectionIsReResolvedOnTheNextCall()
    {
        ModernCamera();
        _http.On(HttpMethod.Get, $"{Usb}/videos/DCIM/", (_, _) => throw new HttpRequestException("connection reset"));
        var provider = Paired();
        await provider.ListDirectoryAsync("/");
        await provider.ListDirectoryAsync("/100GOPRO");
        int Probes() => _http.Requests.Count(r => r.Uri.AbsolutePath == "/gopro/camera/state");
        var probesBefore = Probes();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DownloadFileAsync("/100GOPRO/GH010397.MP4", Path.Combine(_temp, "v.mp4")));
        Assert.Equal(CloudErrorKind.NetworkError, error.Kind);
        Assert.Equal(probesBefore, Probes());

        await provider.ListDirectoryAsync("/");
        Assert.True(Probes() > probesBefore);
    }

    // MARK: - Transfers and deletes

    [Fact]
    public async Task DownloadStreamsFromDcimWithProgress()
    {
        ModernCamera();
        var payload = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();
        _http.OnBytes(HttpMethod.Get, $"{Usb}/videos/DCIM/100GOPRO/GH010397.MP4", payload);
        var provider = Paired();
        var target = Path.Combine(_temp, "GH010397.MP4");

        long reported = 0;
        await provider.DownloadFileAsync("/100GOPRO/GH010397.MP4", target, d => reported += d);

        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        Assert.Equal(payload.Length, reported);
    }

    [Fact]
    public async Task DownloadOfAMissingFileIsNotFound()
    {
        ModernCamera();
        _http.OnText(HttpMethod.Get, "/videos/DCIM/", "gone", HttpStatusCode.NotFound);
        var provider = Paired();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DownloadFileAsync("/100GOPRO/X.MP4", Path.Combine(_temp, "x")));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.False(File.Exists(Path.Combine(_temp, "x")));
    }

    [Fact]
    public async Task DeleteUsesTheModernEndpointAndRefreshesTheIndex()
    {
        ModernCamera();
        _http.OnJson(HttpMethod.Get, $"{Usb}/gopro/media/delete", "{}");
        var provider = Paired();
        await provider.ListDirectoryAsync("/100GOPRO");

        await provider.DeleteItemAsync("/100GOPRO/GH010397.MP4");
        await provider.ListDirectoryAsync("/100GOPRO");

        var delete = _http.Single(HttpMethod.Get, "/gopro/media/delete");
        Assert.Equal("/gopro/media/delete?path=100GOPRO%2FGH010397.MP4", delete.Uri.PathAndQuery);
        Assert.Equal(2, _http.Requests.Count(r => r.Uri.AbsolutePath == "/gopro/media/list"));
    }

    [Fact]
    public async Task DeletingAFolderDeletesEachFile()
    {
        ModernCamera();
        _http.OnJson(HttpMethod.Get, $"{Usb}/gopro/media/delete", "{}");
        var provider = Paired();

        await provider.DeleteItemAsync("/100GOPRO");

        Assert.Equal(
            ["/gopro/media/delete?path=100GOPRO%2FGH010397.MP4", "/gopro/media/delete?path=100GOPRO%2FGOPR0987.JPG"],
            _http.Requests.Where(r => r.Uri.AbsolutePath == "/gopro/media/delete").Select(r => r.Uri.PathAndQuery));
    }

    [Fact]
    public async Task LegacyCamerasUseTheGpControlDialect()
    {
        _http.OnText(HttpMethod.Get, $"{Usb}/gopro/camera/state", "no", HttpStatusCode.NotFound)
             .OnJson(HttpMethod.Get, $"{Usb}/gp/gpControl/status", "{}")
             .OnJson(HttpMethod.Get, $"{Usb}/gp/gpMediaList", GoProMediaListTests.SampleJson)
             .OnJson(HttpMethod.Get, $"{Usb}/gp/gpControl/command/storage/delete", "{}")
             .OnBytes(HttpMethod.Get, $"{Usb}/gp/gpMediaMetadata", [0xFF, 0xD8]);
        var provider = Paired();

        Assert.Equal(2, (await provider.ListDirectoryAsync("/")).Count);
        await provider.DeleteItemAsync("/101GOPRO/GH010001.MP4");
        var thumbnail = await provider.ThumbnailAsync("/101GOPRO/GH010001.MP4");

        Assert.Equal(
            "/gp/gpControl/command/storage/delete?p=/101GOPRO%2FGH010001.MP4",
            _http.Single(HttpMethod.Get, "/storage/delete").Uri.PathAndQuery);
        Assert.Equal([0xFF, 0xD8], thumbnail);
        Assert.Equal("/gp/gpMediaMetadata?p=101GOPRO%2FGH010001.MP4&t=screennail", _http.Single(HttpMethod.Get, "/gpMediaMetadata").Uri.PathAndQuery);
    }

    [Fact]
    public async Task ACameraIsReadOnly()
    {
        var provider = Paired();
        var local = Path.Combine(_temp, "a.txt");
        await File.WriteAllTextAsync(local, "x");

        Assert.True(CloudProviderType.GoPro.IsReadOnly());
        Assert.Equal(CloudErrorKind.NotImplemented, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(local, "/100GOPRO/a.txt"))).Kind);
        Assert.Equal(CloudErrorKind.NotImplemented, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateDirectoryAsync("/new"))).Kind);
        Assert.Equal(CloudErrorKind.NotImplemented, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.RenameItemAsync("/100GOPRO/a", "b"))).Kind);
        Assert.Equal(CloudErrorKind.NotImplemented, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.MoveItemAsync("/100GOPRO/a", "/b"))).Kind);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public void FormOffersScanAndCohn()
    {
        var spec = Provider().ConnectSpec;

        Assert.Equal(["scan", "cohn"], spec.Modes.Select(m => m.Id));
        Assert.DoesNotContain(spec.Modes[0].Fields, f => f.HasValue);
        Assert.Equal(["ip", "username", "password"], spec.Modes[1].Fields.Where(f => f.HasValue).Select(f => f.Key));
        Assert.Equal(ConnectFieldKind.Password, spec.Modes[1].Fields.Single(f => f.Key == "password").Kind);
    }

    [Fact]
    public async Task DisconnectForgetsTheCamera()
    {
        var provider = Paired();
        await provider.DisconnectAsync();

        Assert.False(provider.IsAuthenticated);
        Assert.Null(_store.Load(Key));
    }
}
