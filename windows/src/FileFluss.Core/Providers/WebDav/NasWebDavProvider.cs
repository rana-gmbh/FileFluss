// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.WebDav;

/// <summary>
/// A NAS maker whose boxes FileFluss reaches through their built-in WebDAV service (the macOS
/// <c>NASWebDAVBrand</c>). Neither UGOS Pro nor ADM publishes a file API for third-party apps
/// — WebDAV is the route both vendors document — so these accounts are WebDAV underneath,
/// with each maker's default ports and a self-signed certificate allowed, the way the boxes ship.
/// </summary>
/// <param name="DefaultHttpsPort">
/// The WebDAV port the service listens on out of the box. The web UI ports (9000/9443,
/// 8000/8001) don't serve WebDAV, so a bare host name gets these.
/// </param>
internal sealed record NasWebDavBrand(
    CloudProviderType Type,
    string KeychainPrefix,
    int DefaultHttpsPort,
    int DefaultHttpPort,
    string Instructions,
    string ServerPlaceholder)
{
    /// <summary>UGREEN NAS (UGOS Pro): Control Panel → File Service → WebDAV.</summary>
    public static readonly NasWebDavBrand Ugreen = new(
        CloudProviderType.Ugreen,
        "ugreen",
        5006,
        5005,
        "Turn on WebDAV on the NAS first (Control Panel → File Service → WebDAV), then enter its address and your UGOS account. The default port is 5006 for HTTPS.",
        "Server URL (e.g. https://my-nas.local:5006)");

    /// <summary>ASUSTOR NAS (ADM): Services → WebDAV.</summary>
    public static readonly NasWebDavBrand Asustor = new(
        CloudProviderType.Asustor,
        "asustor",
        9802,
        9800,
        "Turn on WebDAV on the NAS first (Services → WebDAV in ADM), then enter its address and your ADM account. The default port is 9802 for HTTPS.",
        "Server URL (e.g. https://my-nas.local:9802)");
}

/// <summary>
/// UGREEN and ASUSTOR NAS accounts (the macOS <c>NASWebDAVProvider</c>): plain WebDAV with
/// the maker's default port filled in and an optional self-signed-certificate exemption that
/// is scoped to the host the user typed.
/// </summary>
public abstract class NasWebDavProvider : WebDavProviderBase
{
    internal const string ServerUrlKey = "serverUrl";
    internal const string UsernameKey = "username";
    internal const string PasswordKey = "password";
    internal const string AllowSelfSignedKey = "allowSelfSigned";

    private readonly HttpMessageHandler? _handler;
    private readonly Lock _clientsGate = new();
    private readonly List<HttpClient> _httpClients = [];
    private volatile Session? _session;

    private protected NasWebDavProvider(NasWebDavBrand brand, Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials, handler)
    {
        Brand = brand;
        _handler = handler;
        if (credentials.Load<WebDavCredentials>(CredentialKey) is { } stored)
        {
            _session = new Session(stored, NewClient(stored, CreateHttp(stored)));
        }
    }

    private protected NasWebDavBrand Brand { get; }

    public override CloudProviderType ProviderType => Brand.Type;

    protected override string CredentialKeyPrefix => Brand.KeychainPrefix;

    private protected override WebDavClient? Dav => _session?.Client;

    private string LogCategory => Brand.KeychainPrefix;

    /// <summary>Single-PUT uploads, like any WebDAV server: stay under the 32-bit Content-Length boundary.</summary>
    public override long? MaxUploadFileSize => 4_000_000_000;

    // MARK: - Connecting

    public override ConnectSpec ConnectSpec => new(
        ConnectField.Info(Brand.Instructions),
        new ConnectField(ServerUrlKey, ConnectFieldKind.Url, "Server URL") { Placeholder = Brand.ServerPlaceholder },
        new ConnectField(UsernameKey, ConnectFieldKind.Text, "Username"),
        new ConnectField(PasswordKey, ConnectFieldKind.Password, "Password"),
        new ConnectField(AllowSelfSignedKey, ConnectFieldKind.Checkbox, "Allow self-signed certificate")
        {
            Required = false,
            DefaultValue = "true",
            Help = "Most home NAS units use a self-signed certificate. Turn this off only if you've installed a real CA-signed cert (e.g. Let's Encrypt).",
        });

    public override IReadOnlyDictionary<string, string> GetEditableConnectValues()
        => _session?.Credentials is { } c
            ? new Dictionary<string, string>
            {
                [ServerUrlKey] = c.ServerUrl,
                [UsernameKey] = c.Username,
                [AllowSelfSignedKey] = c.AllowSelfSignedCertificate == true ? "true" : "false",
            }
            : new Dictionary<string, string>();

    /// <summary>
    /// Finds the DAV root behind the address (with the maker's port filled in) and signs in.
    /// Named "&lt;Maker&gt; (user@host)", the way Synology accounts are.
    /// </summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeServerUrl(values.GetTrimmed(ServerUrlKey), Brand);
        var username = values.GetTrimmed(UsernameKey);
        var password = values.Get(PasswordKey);
        var allowSelfSigned = values.GetBool(AllowSelfSignedKey, fallback: true);
        var host = HostOf(normalized) ?? Brand.KeychainPrefix;

        var http = CreateHttp(allowSelfSigned ? host : null);
        WebDavCredentials resolved;
        try
        {
            resolved = await WebDavClient.AuthenticateAsync(http, normalized, username, password, LogCategory, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NetworkError || (e.Kind == CloudErrorKind.ServerError && e.StatusCode == 0))
        {
            // Nothing answered at all: the WebDAV service is off, the port is wrong, or the
            // certificate was refused. Say which knobs to check rather than "network error".
            ReleaseHttp(http);
            throw CloudProviderException.CommandFailed(
                Localization.L("No WebDAV service answered at {0}. Turn on WebDAV on the NAS, check the port, and allow a self-signed certificate if the NAS uses one.", normalized),
                e);
        }
        catch
        {
            // Stored credentials and the live session stay as they were.
            ReleaseHttp(http);
            throw;
        }

        var credentials = new WebDavCredentials(
            resolved.ServerUrl,
            username,
            password,
            $"{username}@{host}",
            allowSelfSigned ? true : null);
        Credentials.Save(CredentialKey, credentials);
        _session = new Session(credentials, NewClient(credentials, http));
        SupportLogger.Shared.Log($"[{Brand.KeychainPrefix}] Authenticated as {credentials.DisplayName}", LogCategory);
        return new ConnectResult(credentials.DisplayName);
    }

    public override Task DisconnectAsync()
    {
        _session = null;
        return base.DisconnectAsync();
    }

    // MARK: - Optional operations

    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => RequireDav().SetModificationDateAsync(path, date, cancellationToken);

    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
        => await RequireDav().SearchByWalkingAsync(query, path, cancellationToken).ConfigureAwait(false);

    public override Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
        => Dav is { } dav ? dav.StorageQuotaAsync(cancellationToken) : Task.FromResult<CloudStorageQuota?>(null);

    // MARK: - Server address

    /// <summary>
    /// Adds <c>https://</c> when no scheme was typed and the maker's WebDAV port when no port
    /// was, so "192.168.1.20" reaches the WebDAV service rather than the web UI. An explicit
    /// port is kept as typed. No trailing slash.
    /// </summary>
    internal static string NormalizeServerUrl(string raw, NasWebDavBrand brand)
    {
        var text = raw.Trim();
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text;
        }

        text = text.TrimEnd('/');
        var http = text.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        return ServerAddress.WithDefaultPort(text, http ? brand.DefaultHttpPort : brand.DefaultHttpsPort);
    }

    private static string? HostOf(string serverUrl)
        => Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;

    // MARK: - Plumbing

    private HttpClient CreateHttp(WebDavCredentials credentials)
        => CreateHttp(credentials.AllowSelfSignedCertificate == true ? HostOf(credentials.ServerUrl) : null);

    /// <summary>
    /// One HttpClient per credential set: with "Allow self-signed certificate" on, an invalid
    /// certificate is accepted for the NAS host the user typed and no other. Redirects are
    /// followed by <see cref="WebDavHttp"/> so the Authorization header survives them.
    /// </summary>
    private HttpClient CreateHttp(string? trustedHost)
    {
        var http = _handler is not null
            ? new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }
            : CloudHttp.Create(trustedSelfSignedHost: trustedHost, allowAutoRedirect: false);
        lock (_clientsGate)
        {
            _httpClients.Add(http);
        }

        return http;
    }

    private void ReleaseHttp(HttpClient http)
    {
        lock (_clientsGate)
        {
            _httpClients.Remove(http);
        }

        http.Dispose();
    }

    private static WebDavClient NewClient(WebDavCredentials credentials, HttpClient http)
        => new(http, credentials.ServerUrl, credentials.Username, credentials.Password, WebDavDialect.Generic);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_clientsGate)
            {
                foreach (var http in _httpClients)
                {
                    http.Dispose();
                }

                _httpClients.Clear();
            }
        }

        base.Dispose(disposing);
    }

    private sealed record Session(WebDavCredentials Credentials, WebDavClient Client);
}

/// <summary>A UGREEN NAS (UGOS Pro) through its WebDAV service.</summary>
[CloudProvider(CloudProviderType.Ugreen)]
public sealed class UgreenNasProvider : NasWebDavProvider
{
    public UgreenNasProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal UgreenNasProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(NasWebDavBrand.Ugreen, accountId, credentials, handler)
    {
    }
}

/// <summary>An ASUSTOR NAS (ADM) through its WebDAV service.</summary>
[CloudProvider(CloudProviderType.Asustor)]
public sealed class AsustorNasProvider : NasWebDavProvider
{
    public AsustorNasProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal AsustorNasProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(NasWebDavBrand.Asustor, accountId, credentials, handler)
    {
    }
}
