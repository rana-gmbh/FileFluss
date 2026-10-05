// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net.Http.Headers;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.GoPro;

/// <summary>
/// One camera's local HTTP server (Open GoPro API) — port of the macOS <c>GoProAPIClient</c>.
///
/// <para>Over the camera's Wi-Fi access point or USB the API is plain <c>http://ip:8080</c>
/// without authentication. "Camera on Home Network" (COHN) serves the same endpoints over
/// HTTPS with Basic auth and a self-signed certificate. SD-card media live under
/// <c>/videos/DCIM/&lt;dir&gt;/&lt;file&gt;</c> with a JSON index at <c>/gopro/media/list</c>.
/// Read-only by design: list, download, delete, thumbnails. HERO9 and newer are the baseline.</para>
///
/// <para>GoPro shipped two HTTP dialects: HERO9+ use the Open GoPro <c>/gopro/…</c> namespace,
/// HERO5–8 the older <c>/gp/gpControl/…</c> and <c>/gp/gpMediaList</c>. The media list JSON
/// and the download path are identical, so only a few endpoints differ. The dialect is
/// detected on the first ping and cached.</para>
/// </summary>
internal sealed class GoProApiClient
{
    internal enum Dialect
    {
        Modern,
        Legacy,
    }

    /// <summary>The link is local: fail fast rather than hang the UI when the camera sleeps or is unplugged.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The media list covers the whole card and can be large; it gets the ordinary API bound.</summary>
    private static readonly TimeSpan MediaListTimeout = CloudHttp.DefaultApiTimeout;

    /// <summary>
    /// Browsing into a folder right after listing the root must not re-fetch the card's whole
    /// index; one fetch answers every path for a few seconds.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly Lock _gate = new();
    private Dialect? _dialect;
    private GoProMediaList? _cachedList;
    private DateTimeOffset _cachedAt;

    /// <summary>The plain local API over the camera's access point or USB link.</summary>
    public GoProApiClient(HttpClient http, string ipAddress, int port = GoProDiscovery.Port)
    {
        _http = http;
        IpAddress = ipAddress;
        _baseUri = new UriBuilder(Uri.UriSchemeHttp, ipAddress, port).Uri;
    }

    private GoProApiClient(HttpClient http, string ipAddress, int port, AuthenticationHeaderValue authorization)
    {
        _http = http;
        IpAddress = ipAddress;
        _baseUri = new UriBuilder(Uri.UriSchemeHttps, ipAddress, port).Uri;
        _authorization = authorization;

        // COHN exists only on the modern API.
        _dialect = Dialect.Modern;
    }

    /// <summary>COHN: the same endpoints over HTTPS with Basic auth; <paramref name="http"/> trusts the camera's self-signed certificate for this host only.</summary>
    public static GoProApiClient Cohn(HttpClient http, string ipAddress, string username, string password, int port = 443)
        => new(http, ipAddress, port, CloudHttp.Basic(username, password));

    public string IpAddress { get; }

    internal Dialect? CurrentDialect
    {
        get
        {
            lock (_gate)
            {
                return _dialect;
            }
        }
    }

    // MARK: - Reachability / lifecycle

    /// <summary>Liveness probe; also detects and caches the dialect. Free once the dialect is known.</summary>
    public async Task<bool> PingAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
        => await ResolveDialectAsync(cancellationToken, timeout).ConfigureAwait(false) is not null;

    /// <summary>Probes the modern, then the legacy status endpoint. Null when the camera does not answer.</summary>
    private async Task<Dialect?> ResolveDialectAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (CurrentDialect is { } known)
        {
            return known;
        }

        Dialect? found = null;
        if (await TryGetAsync("/gopro/camera/state", cancellationToken, timeout).ConfigureAwait(false))
        {
            found = Dialect.Modern;
        }
        else if (await TryGetAsync("/gp/gpControl/status", cancellationToken, timeout).ConfigureAwait(false))
        {
            found = Dialect.Legacy;
        }

        if (found is not null)
        {
            lock (_gate)
            {
                _dialect ??= found;
                return _dialect;
            }
        }

        return null;
    }

    /// <summary>
    /// A real request to the status endpoint, whatever the cached dialect — COHN presets the
    /// dialect, so <see cref="PingAsync"/> would answer without asking the camera. Throws the
    /// mapped error (401 → Unauthorized, transport → NetworkError).
    /// </summary>
    public Task VerifyAsync(CancellationToken cancellationToken)
        => GetAsync("/gopro/camera/state", cancellationToken);

    /// <summary>Turns on wired (USB) control so the HTTP API answers over the USB-Ethernet link. Harmless over Wi-Fi; best-effort.</summary>
    public async Task EnableWiredControlAsync(CancellationToken cancellationToken)
        => await TryGetAsync("/gopro/camera/control/wired_usb?p=1", cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Keeps the camera's connection awake during idle browsing. Best-effort: the next real
    /// request surfaces a disconnect. Only the modern API has an HTTP keep-alive (HERO5–8 use a
    /// UDP heartbeat that is not sent — browsing traffic suffices).
    /// </summary>
    public async Task KeepAliveAsync(CancellationToken cancellationToken)
    {
        if (await ResolveDialectAsync(cancellationToken).ConfigureAwait(false) == Dialect.Modern)
        {
            await TryGetAsync("/gopro/camera/keep_alive", cancellationToken).ConfigureAwait(false);
        }
    }

    // MARK: - Listing

    public async Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken)
        => (await MediaListAsync(cancellationToken).ConfigureAwait(false)).Items(path);

    /// <summary>A file's metadata, or an on-card folder's; NotFound otherwise.</summary>
    public async Task<CloudFileItem> GetFileInfoAsync(string path, CancellationToken cancellationToken)
    {
        var list = await MediaListAsync(cancellationToken).ConfigureAwait(false);
        return list.File(path) ?? list.Folder(path) ?? throw CloudProviderException.NotFound(path);
    }

    public async Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken)
        => (await ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false)).Sum(i => i.Size);

    private async Task<GoProMediaList> MediaListAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cachedList is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            {
                return _cachedList;
            }
        }

        var endpoint = await ResolveDialectAsync(cancellationToken).ConfigureAwait(false) == Dialect.Legacy ? "/gp/gpMediaList" : "/gopro/media/list";
        var json = await GetStringAsync(endpoint, cancellationToken, MediaListTimeout).ConfigureAwait(false);
        var list = GoProMediaList.Parse(json);
        lock (_gate)
        {
            _cachedList = list;
            _cachedAt = DateTimeOffset.UtcNow;
        }

        return list;
    }

    private void InvalidateCache()
    {
        lock (_gate)
        {
            _cachedList = null;
        }
    }

    // MARK: - Download

    public async Task DownloadAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var components = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length != 2)
        {
            throw CloudProviderException.NotFound(remotePath);
        }

        // Photos and videos alike are served from /videos/DCIM/<dir>/<file>.
        using var request = Request($"/videos/DCIM/{CloudHttp.Escape(components[0])}/{CloudHttp.Escape(components[1])}");
        using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapHttpError((int)response.StatusCode, remotePath);
        }

        await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    // MARK: - Delete

    /// <summary>
    /// Deletes one file by its on-card "&lt;dir&gt;/&lt;file&gt;" path. A folder is emptied file
    /// by file — the camera has no folder delete, and the card folder itself is the camera's.
    /// </summary>
    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 1)
        {
            var files = (await MediaListAsync(cancellationToken).ConfigureAwait(false)).Media
                .FirstOrDefault(d => d.Name == components[0])
                ?? throw CloudProviderException.NotFound(path);
            try
            {
                foreach (var file in files.Files)
                {
                    await DeleteFileAsync($"{files.Name}/{file.Name}", cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                InvalidateCache();
            }

            return;
        }

        try
        {
            await DeleteFileAsync(path.Trim('/'), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            InvalidateCache();
        }
    }

    private async Task DeleteFileAsync(string relativePath, CancellationToken cancellationToken)
    {
        var encoded = CloudHttp.Escape(relativePath);
        if (await ResolveDialectAsync(cancellationToken).ConfigureAwait(false) == Dialect.Legacy)
        {
            // HERO5–8 want a leading slash and the gpControl storage command.
            await GetAsync($"/gp/gpControl/command/storage/delete?p=/{encoded}", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await GetAsync($"/gopro/media/delete?path={encoded}", cancellationToken).ConfigureAwait(false);
        }
    }

    // MARK: - Thumbnail

    /// <summary>Raw JPEG bytes of a media item's thumbnail, or null.</summary>
    public async Task<byte[]?> ThumbnailAsync(string path, CancellationToken cancellationToken)
    {
        var encoded = CloudHttp.Escape(path.Trim('/'));
        try
        {
            return await ResolveDialectAsync(cancellationToken).ConfigureAwait(false) == Dialect.Legacy
                ? await GetAsync($"/gp/gpMediaMetadata?p={encoded}&t=screennail", cancellationToken).ConfigureAwait(false)
                : await GetAsync($"/gopro/media/thumbnail?path={encoded}", cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException)
        {
            return null;
        }
    }

    // MARK: - HTTP

    private HttpRequestMessage Request(string pathAndQuery)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, pathAndQuery));
        if (_authorization is not null)
        {
            request.Headers.Authorization = _authorization;
        }

        return request;
    }

    private async Task<byte[]> GetAsync(string pathAndQuery, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var request = Request(pathAndQuery);
        using var response = await _http.SendApiAsync(request, cancellationToken, timeout ?? RequestTimeout).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapHttpError((int)response.StatusCode, null);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GetStringAsync(string pathAndQuery, CancellationToken cancellationToken, TimeSpan timeout)
    {
        using var request = Request(pathAndQuery);
        using var response = await _http.SendApiAsync(request, cancellationToken, timeout).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapHttpError((int)response.StatusCode, null);
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryGetAsync(string pathAndQuery, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        try
        {
            await GetAsync(pathAndQuery, cancellationToken, timeout).ConfigureAwait(false);
            return true;
        }
        catch (CloudProviderException e)
        {
            SupportLogger.Shared.Log($"[GoPro] GET {pathAndQuery.Split('?')[0]} at {IpAddress} failed: {e.Message}", "GoPro");
            return false;
        }
    }

    private static CloudProviderException MapHttpError(int status, string? path) => status switch
    {
        401 or 403 => CloudProviderException.Unauthorized(),
        404 => CloudProviderException.NotFound(path ?? string.Empty),
        429 => CloudProviderException.RateLimited(),
        _ => CloudProviderException.ServerError(status),
    };
}
