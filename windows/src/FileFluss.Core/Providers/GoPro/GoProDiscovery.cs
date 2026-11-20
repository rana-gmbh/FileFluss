// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.GoPro;

/// <summary>How FileFluss reached a camera; stored with the account so a reconnect knows where to look first.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GoProConnectionMode>))]
public enum GoProConnectionMode
{
    /// <summary>Plugged in over USB; the camera exposes the HTTP API on a USB-Ethernet link.</summary>
    [JsonStringEnumMemberName("wiredUSB")] WiredUsb,

    /// <summary>The PC joined the camera's own Wi-Fi access point (fixed address 10.5.5.9).</summary>
    [JsonStringEnumMemberName("wifiAP")] WifiAp,

    /// <summary>The camera is on the user's home Wi-Fi (COHN): HTTPS + Basic auth at an address the GoPro app provisioned.</summary>
    [JsonStringEnumMemberName("cohn")] Cohn,
}

/// <summary>A camera found by <see cref="GoProDiscovery.ScanAsync"/>, ready to connect to.</summary>
public sealed record GoProCamera(string Name, string IpAddress, GoProConnectionMode Mode)
{
    public string Id => IpAddress;
}

/// <summary>
/// Finds GoPro cameras without any external dependency — port of the macOS
/// <c>GoProDiscovery</c>.
/// <list type="bullet">
/// <item><b>Wired USB</b>: a plugged-in camera hands the PC a DHCP lease on a /24 inside
/// 172.16.0.0/12 and sits at host <c>.51</c> of it. Every local IPv4 address in that range
/// yields one candidate (on macOS from <c>getifaddrs</c>, here from
/// <see cref="NetworkInterface"/>).</item>
/// <item><b>Wi-Fi AP</b>: the camera's own access point always answers at 10.5.5.9.</item>
/// <item><b>Bonjour</b> (<c>_gopro-web._tcp</c>), best-effort, for cameras on the home network.
/// Windows has no Bonjour browser in .NET, so this sends one mDNS query per interface from an
/// ephemeral port (RFC 6762 "legacy unicast"), which responders answer directly.</item>
/// </list>
/// Each candidate is confirmed with a real status request, so a dead address is never offered.
/// </summary>
public sealed class GoProDiscovery
{
    internal const string BonjourServiceType = "_gopro-web._tcp";
    internal const string WifiApAddress = "10.5.5.9";
    internal const int Port = 8080;

    /// <summary>How long the Bonjour browse listens, as on macOS.</summary>
    internal static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Bound on each confirmation probe. A camera on a USB or access-point link answers in
    /// milliseconds; an absent 10.5.5.9 behind a default gateway would otherwise hold the scan
    /// for two full request timeouts.
    /// </summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    private readonly HttpMessageHandler? _handler;
    private readonly Func<IReadOnlyList<IPAddress>> _localAddresses;
    private readonly Func<TimeSpan, CancellationToken, Task<IReadOnlyList<string>>> _bonjour;

    public GoProDiscovery()
        : this(null, null, null)
    {
    }

    /// <summary>Test seam: scripted HTTP, local addresses and Bonjour results.</summary>
    internal GoProDiscovery(
        HttpMessageHandler? handler,
        Func<IReadOnlyList<IPAddress>>? localAddresses,
        Func<TimeSpan, CancellationToken, Task<IReadOnlyList<string>>>? bonjour)
    {
        _handler = handler;
        _localAddresses = localAddresses ?? LocalIPv4Addresses;
        _bonjour = bonjour ?? ((timeout, token) => GoProBonjour.BrowseAsync(BonjourServiceType, timeout, token));
    }

    public static GoProDiscovery Default { get; } = new();

    /// <summary>Reachable cameras, confirmed. The access point first, then USB, then Bonjour finds.</summary>
    public async Task<IReadOnlyList<GoProCamera>> ScanAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var http = CreateHttpClient();

        // Browse while the fixed candidates are probed; Bonjour needs its whole window anyway.
        var bonjour = BrowseSafelyAsync(timeout ?? DefaultScanTimeout, cancellationToken);

        var candidates = new List<(string Ip, GoProConnectionMode Mode)> { (WifiApAddress, GoProConnectionMode.WifiAp) };
        candidates.AddRange(UsbCandidateAddresses(_localAddresses()).Select(ip => (ip, GoProConnectionMode.WiredUsb)));
        candidates = candidates.DistinctBy(c => c.Ip).ToList();
        var confirmed = await ConfirmAsync(http, candidates, cancellationToken).ConfigureAwait(false);

        // Bonjour finds are labelled as wired, as on macOS; de-duplicated against the fixed candidates.
        var extra = (await bonjour.ConfigureAwait(false))
            .Where(ip => candidates.All(c => c.Ip != ip))
            .Distinct()
            .Select(ip => (ip, GoProConnectionMode.WiredUsb))
            .ToList();
        var cameras = confirmed.Concat(await ConfirmAsync(http, extra, cancellationToken).ConfigureAwait(false)).ToList();
        SupportLogger.Shared.Log($"[GoPro] scan found {cameras.Count} camera(s)", "GoPro");
        return cameras;
    }

    /// <summary>
    /// A reachable address for a saved connection: the last-known one if it still answers, else
    /// a fresh scan's camera of the same mode, else any camera. Null when nothing answers.
    /// </summary>
    public async Task<string?> ResolveAsync(string? lastKnownIp, GoProConnectionMode mode, CancellationToken cancellationToken = default)
    {
        if (lastKnownIp is not null)
        {
            using var http = CreateHttpClient();
            if (await new GoProApiClient(http, lastKnownIp, Port).PingAsync(cancellationToken, ProbeTimeout).ConfigureAwait(false))
            {
                return lastKnownIp;
            }
        }

        var cameras = await ScanAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return (cameras.FirstOrDefault(c => c.Mode == mode) ?? cameras.FirstOrDefault())?.IpAddress;
    }

    private async Task<List<GoProCamera>> ConfirmAsync(HttpClient http, IReadOnlyList<(string Ip, GoProConnectionMode Mode)> candidates, CancellationToken cancellationToken)
    {
        var probes = candidates.Select(async candidate =>
        {
            var reachable = await new GoProApiClient(http, candidate.Ip, Port).PingAsync(cancellationToken, ProbeTimeout).ConfigureAwait(false);
            var label = candidate.Mode == GoProConnectionMode.WifiAp ? "GoPro Camera (Wi-Fi)" : "GoPro Camera (USB)";
            return reachable ? new GoProCamera(label, candidate.Ip, candidate.Mode) : null;
        }).ToList();
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        return results.OfType<GoProCamera>().ToList();
    }

    private async Task<IReadOnlyList<string>> BrowseSafelyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _bonjour(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or IOException or InvalidOperationException or NetworkInformationException)
        {
            SupportLogger.Shared.Log($"[GoPro] Bonjour browse failed: {e.Message}", "GoPro");
            return [];
        }
    }

    private HttpClient CreateHttpClient()
        => _handler is null ? CloudHttp.Create() : new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    // MARK: - USB interface enumeration

    /// <summary>
    /// The <c>.51</c> host of every local IPv4 address inside 172.16.0.0/12 — the range GoPro's
    /// USB-Ethernet gadget leases from. The PC's own address is never a candidate.
    /// </summary>
    internal static IReadOnlyList<string> UsbCandidateAddresses(IEnumerable<IPAddress> localAddresses)
    {
        var results = new List<string>();
        foreach (var address in localAddresses)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            var ip = address.ToString();
            if (!IsInGoProUsbRange(ip))
            {
                continue;
            }

            var octets = ip.Split('.');
            var camera = $"{octets[0]}.{octets[1]}.{octets[2]}.51";
            if (camera != ip && !results.Contains(camera))
            {
                results.Add(camera);
            }
        }

        return results;
    }

    /// <summary>True for 172.16.0.0 – 172.31.255.255.</summary>
    public static bool IsInGoProUsbRange(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4 || !parts.All(p => int.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        var first = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var second = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        return first == 172 && second is >= 16 and <= 31;
    }

    /// <summary>Every IPv4 unicast address on this PC's interfaces, loopback excluded.</summary>
    internal static IReadOnlyList<IPAddress> LocalIPv4Addresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                    {
                        addresses.Add(unicast.Address);
                    }
                }
            }
        }
        catch (NetworkInformationException e)
        {
            SupportLogger.Shared.Log($"[GoPro] Interface enumeration failed: {e.Message}", "GoPro", LogLevel.Error);
        }

        return addresses;
    }
}

/// <summary>
/// A minimal, best-effort mDNS browse: one PTR query for the service per local IPv4 interface,
/// sent from an ephemeral port so responders answer by unicast (RFC 6762 §6.7) and the system's
/// own mDNS responder keeps port 5353. Collects the A records of the answers.
/// </summary>
internal static class GoProBonjour
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<IReadOnlyList<string>> BrowseAsync(string serviceType, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var serviceName = serviceType + ".local";
        var query = BuildQuery(serviceName);
        var found = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var sockets = new List<UdpClient>();
        try
        {
            foreach (var local in GoProDiscovery.LocalIPv4Addresses())
            {
                UdpClient? udp = null;
                try
                {
                    udp = new UdpClient(new IPEndPoint(local, 0));
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    await udp.SendAsync(query, MulticastEndpoint, cancellationToken).ConfigureAwait(false);
                    sockets.Add(udp);
                    udp = null;
                }
                catch (SocketException)
                {
                    // An interface that can't multicast (VPN, disconnected adapter) is skipped.
                }
                finally
                {
                    udp?.Dispose();
                }
            }

            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(timeout);
            await Task.WhenAll(sockets.Select(socket => ReceiveAsync(socket, serviceName, found, window.Token))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            foreach (var socket in sockets)
            {
                socket.Dispose();
            }
        }

        return found.Keys.ToList();
    }

    private static async Task ReceiveAsync(UdpClient socket, string serviceName, ConcurrentDictionary<string, byte> found, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                foreach (var ip in ParseResponse(result.Buffer, serviceName, result.RemoteEndPoint.Address))
                {
                    found.TryAdd(ip, 0);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>A DNS query (one question, PTR, class IN) for <paramref name="name"/>.</summary>
    internal static byte[] BuildQuery(string name)
    {
        using var stream = new MemoryStream();
        var id = RandomNumberGenerator.GetInt32(1, ushort.MaxValue);
        stream.Write([(byte)(id >> 8), (byte)id, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]);
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.Write([0, 0, 12, 0, 1]);
        return stream.ToArray();
    }

    /// <summary>
    /// The IPv4 addresses a response announces for <paramref name="serviceName"/>: its A
    /// records, or the sender when it answered the PTR without them. Empty for anything else,
    /// including malformed packets.
    /// </summary>
    internal static IReadOnlyList<string> ParseResponse(byte[] packet, string serviceName, IPAddress sender)
    {
        try
        {
            if (packet.Length < 12 || (packet[2] & 0x80) == 0)
            {
                return [];
            }

            var questions = U16(packet, 4);
            var records = U16(packet, 6) + U16(packet, 8) + U16(packet, 10);
            var offset = 12;
            for (var i = 0; i < questions; i++)
            {
                (_, offset) = ReadName(packet, offset);
                offset += 4;
            }

            var answersService = false;
            var addresses = new List<string>();
            for (var i = 0; i < records; i++)
            {
                string recordName;
                (recordName, offset) = ReadName(packet, offset);
                var type = U16(packet, offset);
                var length = U16(packet, offset + 8);
                var data = offset + 10;
                if (data + length > packet.Length)
                {
                    break;
                }

                if (type == 12 && string.Equals(recordName, serviceName, StringComparison.OrdinalIgnoreCase))
                {
                    answersService = true;
                }
                else if (type == 1 && length == 4)
                {
                    var address = new IPAddress(packet.AsSpan(data, 4)).ToString();
                    if (!addresses.Contains(address))
                    {
                        addresses.Add(address);
                    }
                }

                offset = data + length;
            }

            if (!answersService)
            {
                return [];
            }

            if (addresses.Count > 0)
            {
                return addresses;
            }

            return sender.AddressFamily == AddressFamily.InterNetwork ? [sender.ToString()] : [];
        }
        catch (Exception e) when (e is IndexOutOfRangeException or InvalidDataException)
        {
            return [];
        }
        catch (ArgumentOutOfRangeException)
        {
            return [];
        }
    }

    /// <summary>A possibly compressed domain name; returns it dotted and the offset after it.</summary>
    private static (string Name, int Next) ReadName(byte[] packet, int offset)
    {
        var labels = new List<string>();
        var next = -1;
        for (var jumps = 0; jumps < 64; jumps++)
        {
            var length = packet[offset];
            if (length == 0)
            {
                return (string.Join('.', labels), next < 0 ? offset + 1 : next);
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (next < 0)
                {
                    next = offset + 2;
                }

                offset = ((length & 0x3F) << 8) | packet[offset + 1];
                continue;
            }

            labels.Add(Encoding.UTF8.GetString(packet, offset + 1, length));
            offset += length + 1;
        }

        throw new InvalidDataException("DNS name compression loop");
    }

    private static int U16(byte[] packet, int offset) => (packet[offset] << 8) | packet[offset + 1];
}
