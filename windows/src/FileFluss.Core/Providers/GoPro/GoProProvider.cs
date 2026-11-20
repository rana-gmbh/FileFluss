// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.GoPro;

/// <summary>
/// Persisted connection details for a paired camera. The address is dynamic (USB DHCP,
/// access point), so only the last-known one is stored as a hint and always re-confirmed —
/// or re-discovered — before use. COHN credentials sit with it in the credential store.
/// </summary>
internal sealed record GoProConnection(
    string CameraName,
    GoProConnectionMode Mode,
    string? LastKnownIp,
    string? CohnUsername = null,
    string? CohnPassword = null);

/// <summary>
/// A GoPro camera over the Open GoPro HTTP API — port of the macOS <c>GoProProvider</c>.
/// Read-only (<see cref="CloudProviderTypeExtensions.IsReadOnly"/>): media can be browsed,
/// downloaded and deleted; uploads, folders and renames throw NotImplemented. The address is
/// resolved lazily and re-resolved after a dropped connection, so an account survives the
/// camera sleeping, being unplugged or getting a new DHCP lease.
/// </summary>
[CloudProvider(CloudProviderType.GoPro)]
public sealed class GoProProvider : CloudProvider
{
    internal const string ScanMode = "scan";
    internal const string CohnMode = "cohn";
    internal const string IpField = "ip";
    internal const string UsernameField = "username";
    internal const string PasswordField = "password";

    private const string DefaultCameraName = "GoPro Camera";
    private const string CohnCameraName = "GoPro Camera (Home Wi-Fi)";
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private readonly HttpMessageHandler? _handler;
    private readonly GoProDiscovery _discovery;
    private readonly HttpClient _http;
    private readonly Dictionary<string, HttpClient> _cohnClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _resolveGate = new(1, 1);
    private readonly Lock _gate = new();
    private GoProConnection? _connection;
    private GoProApiClient? _client;
    private CancellationTokenSource? _keepAlive;

    public GoProProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null, null)
    {
    }

    internal GoProProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler, GoProDiscovery? discovery)
        : base(accountId, credentials)
    {
        _handler = handler;
        _discovery = discovery ?? GoProDiscovery.Default;
        _http = handler is null ? CloudHttp.Create() : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _connection = credentials.Load<GoProConnection>(CredentialKey);
    }

    public override CloudProviderType ProviderType => CloudProviderType.GoPro;

    protected override string CredentialKeyPrefix => "gopro";

    public override bool IsAuthenticated
    {
        get
        {
            lock (_gate)
            {
                return _connection is not null;
            }
        }
    }

    // MARK: - Connecting

    public override ConnectSpec ConnectSpec { get; } = new(
    [
        new ConnectMode(ScanMode, "USB or Wi-Fi",
        [
            ConnectField.Info("Turn the camera on, then either connect it by USB or join its Wi-Fi network. Scan to find it."),
        ]),
        new ConnectMode(CohnMode, "Home Wi-Fi (COHN)",
        [
            ConnectField.Info("Set up \"Camera on Home Network\" in the GoPro Quik app, then enter the camera's IP address and the COHN username and password it shows."),
            new ConnectField(IpField, ConnectFieldKind.Text, "Camera IP address"),
            new ConnectField(UsernameField, ConnectFieldKind.Text, "COHN Username"),
            new ConnectField(PasswordField, ConnectFieldKind.Password, "COHN Password"),
        ]),
    ]);

    /// <summary>
    /// Scan mode discovers cameras (USB, the camera's Wi-Fi, Bonjour) and asks which one when
    /// there are several; COHN mode connects to the address the user entered. Either way the
    /// camera must answer before anything is stored. The account is named after the camera.
    /// </summary>
    public override Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
        => values.Mode == CohnMode
            ? ConnectCohnAsync(NormalizeHost(values.GetTrimmed(IpField)), values.GetTrimmed(UsernameField), values.Get(PasswordField), cancellationToken)
            : ConnectScannedAsync(prompt, cancellationToken);

    private async Task<ConnectResult> ConnectScannedAsync(IConnectPrompt prompt, CancellationToken cancellationToken)
    {
        IReadOnlyList<GoProCamera> cameras;
        prompt.ShowStatus(Localization.L("Scanning…"));
        try
        {
            cameras = await _discovery.ScanAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            prompt.ShowStatus(null);
        }

        if (cameras.Count == 0)
        {
            throw CloudProviderException.CommandFailed(Localization.L("No camera found. Make sure it's on and connected by USB or Wi-Fi, then scan again."));
        }

        var camera = cameras[0];
        if (cameras.Count > 1)
        {
            var choice = await prompt.ChooseAsync(
                DefaultCameraName,
                Localization.L("Several cameras were found. Choose the one to add."),
                cameras.Select(c => $"{c.Name} — {c.IpAddress}").ToList(),
                cancellationToken).ConfigureAwait(false);
            if (choice is not { } index || index < 0 || index >= cameras.Count)
            {
                throw CloudProviderException.Cancelled();
            }

            camera = cameras[index];
        }

        var client = new GoProApiClient(_http, camera.IpAddress);
        if (camera.Mode == GoProConnectionMode.WiredUsb)
        {
            await client.EnableWiredControlAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!await client.PingAsync(cancellationToken).ConfigureAwait(false))
        {
            throw CannotConnect();
        }

        var connection = new GoProConnection(camera.Name, camera.Mode, camera.IpAddress);
        Adopt(connection, client);
        SupportLogger.Shared.Log($"[GoPro] connected to {camera.Name} at {camera.IpAddress}", "GoPro");
        return new ConnectResult(null) { DisplayName = camera.Name };
    }

    /// <summary>
    /// COHN: HTTPS + Basic auth at the address provisioned through the GoPro Quik app. macOS
    /// "pings" here too, but its COHN client presets the dialect so the ping never reaches the
    /// camera; this asks the status endpoint for real, so a wrong address or password fails now
    /// instead of on the first listing.
    /// </summary>
    private async Task<ConnectResult> ConnectCohnAsync(string ipAddress, string username, string password, CancellationToken cancellationToken)
    {
        if (ipAddress.Length == 0)
        {
            throw CannotConnect();
        }

        var client = GoProApiClient.Cohn(CohnHttp(ipAddress), ipAddress, username, password);
        try
        {
            await client.VerifyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.Unauthorized)
        {
            throw CloudProviderException.InvalidCredentials();
        }
        catch (CloudProviderException e) when (e.Kind != CloudErrorKind.NetworkError)
        {
            throw CannotConnect();
        }

        var connection = new GoProConnection(CohnCameraName, GoProConnectionMode.Cohn, ipAddress, username, password);
        Adopt(connection, client);
        SupportLogger.Shared.Log($"[GoPro] connected via COHN at {ipAddress}", "GoPro");
        return new ConnectResult(null) { DisplayName = connection.CameraName };
    }

    private void Adopt(GoProConnection connection, GoProApiClient client)
    {
        Credentials.Save(CredentialKey, connection);
        lock (_gate)
        {
            _connection = connection;
            _client = client;
        }

        StartKeepAlive();
    }

    public override Task DisconnectAsync()
    {
        StopKeepAlive();
        lock (_gate)
        {
            _client = null;
            _connection = null;
        }

        return base.DisconnectAsync();
    }

    /// <summary>"https://192.168.1.20:443/" → "192.168.1.20": people paste what the Quik app shows. COHN is always port 443.</summary>
    internal static string NormalizeHost(string input)
    {
        var host = input.Trim();
        foreach (var scheme in (string[])["https://", "http://"])
        {
            if (host.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                host = host[scheme.Length..];
            }
        }

        host = host.TrimEnd('/');
        var colon = host.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon == host.LastIndexOf(':') &&
            System.Net.IPAddress.TryParse(host[..colon], out var v4) &&
            v4.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            host = host[..colon];
        }

        return host;
    }

    private static CloudProviderException CannotConnect()
        => CloudProviderException.Network(new HttpRequestException(Localization.L("Could not connect to the server.")));

    /// <summary>One HTTPS client per camera address, kept for the provider's lifetime.</summary>
    private HttpClient CohnHttp(string ipAddress)
    {
        lock (_gate)
        {
            if (!_cohnClients.TryGetValue(ipAddress, out var client))
            {
                // The camera presents a self-signed certificate; trust it for this address only,
                // so nobody else on the network can impersonate the camera and collect the Basic
                // credentials sent with every request.
                client = _handler is null
                    ? CloudHttp.Create(trustedSelfSignedHost: ipAddress)
                    : new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
                _cohnClients[ipAddress] = client;
            }

            return client;
        }
    }

    // MARK: - Live client

    /// <summary>
    /// The client for the camera, rebuilt when there is none: COHN reconnects to its fixed
    /// address; USB and access-point cameras are re-resolved (last address, then a scan) and
    /// the new address is stored as the next hint. Single-flight, so concurrent callers share
    /// one scan.
    /// </summary>
    private async Task<GoProApiClient> LiveClientAsync(CancellationToken cancellationToken)
    {
        GoProApiClient? client;
        lock (_gate)
        {
            if (_connection is null)
            {
                throw CloudProviderException.NotAuthenticated();
            }

            client = _client;
        }

        if (client is not null && await client.PingAsync(cancellationToken).ConfigureAwait(false))
        {
            return client;
        }

        await _resolveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GoProConnection connection;
            lock (_gate)
            {
                connection = _connection ?? throw CloudProviderException.NotAuthenticated();
                if (_client is not null && !ReferenceEquals(_client, client))
                {
                    return _client; // Another caller reconnected while this one waited.
                }
            }

            GoProApiClient fresh;
            if (connection.Mode == GoProConnectionMode.Cohn)
            {
                // A fixed address on the home network: rebuild the HTTPS client rather than
                // scanning USB and access-point subnets.
                if (connection is not { LastKnownIp: { } ip, CohnUsername: { } user, CohnPassword: { } password })
                {
                    throw CloudProviderException.NotAuthenticated();
                }

                fresh = GoProApiClient.Cohn(CohnHttp(ip), ip, user, password);
            }
            else
            {
                var ip = await _discovery.ResolveAsync(connection.LastKnownIp, connection.Mode, cancellationToken).ConfigureAwait(false)
                         ?? throw CannotConnect();
                fresh = new GoProApiClient(_http, ip);
                if (connection.Mode == GoProConnectionMode.WiredUsb)
                {
                    await fresh.EnableWiredControlAsync(cancellationToken).ConfigureAwait(false);
                }

                if (ip != connection.LastKnownIp)
                {
                    var updated = connection with { LastKnownIp = ip };
                    lock (_gate)
                    {
                        _connection = updated;
                    }

                    Credentials.Save(CredentialKey, updated);
                }
            }

            if (!await fresh.PingAsync(cancellationToken).ConfigureAwait(false))
            {
                throw CannotConnect();
            }

            lock (_gate)
            {
                _client = fresh;
            }

            StartKeepAlive();
            return fresh;
        }
        finally
        {
            _resolveGate.Release();
        }
    }

    /// <summary>
    /// Runs an operation on the live client. A transport failure drops the client, so the next
    /// call re-resolves the camera instead of retrying an address that has gone away (macOS
    /// keeps a client whose dialect is cached and never notices the address change).
    /// </summary>
    private async Task<T> WithClientAsync<T>(Func<GoProApiClient, Task<T>> operation, CancellationToken cancellationToken)
    {
        var client = await LiveClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(client).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NetworkError)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_client, client))
                {
                    _client = null;
                }
            }

            throw;
        }
    }

    private Task WithClientAsync(Func<GoProApiClient, Task> operation, CancellationToken cancellationToken)
        => WithClientAsync(
            async client =>
            {
                await operation(client).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    private void StartKeepAlive()
    {
        CancellationToken token;
        lock (_gate)
        {
            _keepAlive?.Cancel();
            _keepAlive?.Dispose();
            _keepAlive = new CancellationTokenSource();
            token = _keepAlive.Token;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(KeepAliveInterval, token).ConfigureAwait(false);
                        GoProApiClient? client;
                        lock (_gate)
                        {
                            client = _client;
                        }

                        if (client is null)
                        {
                            return;
                        }

                        await client.KeepAliveAsync(token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            },
            CancellationToken.None);
    }

    private void StopKeepAlive()
    {
        lock (_gate)
        {
            _keepAlive?.Cancel();
            _keepAlive?.Dispose();
            _keepAlive = null;
        }
    }

    // MARK: - Read operations

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => WithClientAsync(client => client.ListDirectoryAsync(path, cancellationToken), cancellationToken);

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => WithClientAsync(client => client.DownloadAsync(remotePath, localPath, onBytes, cancellationToken), cancellationToken);

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => WithClientAsync(client => client.DeleteAsync(path, cancellationToken), cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        if (IsRoot(path))
        {
            return Task.FromResult(new CloudFileItem("/", string.Empty, "/", true, 0, CloudFileItem.UnknownDate));
        }

        return WithClientAsync(client => client.GetFileInfoAsync(path, cancellationToken), cancellationToken);
    }

    public override Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
        => WithClientAsync(client => client.FolderSizeAsync(path, cancellationToken), cancellationToken);

    /// <summary>Raw thumbnail JPEG of a media item, or null. Not part of the provider contract; callers that know they hold a GoPro may use it.</summary>
    public async Task<byte[]?> ThumbnailAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return await WithClientAsync(client => client.ThumbnailAsync(path, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException)
        {
            return null;
        }
    }

    // MARK: - Write operations (unsupported — the camera is an import source)

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopKeepAlive();
            _http.Dispose();
            lock (_gate)
            {
                foreach (var client in _cohnClients.Values)
                {
                    client.Dispose();
                }

                _cohnClients.Clear();
            }
        }

        base.Dispose(disposing);
    }
}
