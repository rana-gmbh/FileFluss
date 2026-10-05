// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.Jottacloud;

/// <summary>
/// Jottacloud. Jottacloud has no official API; this talks to the same undocumented endpoints
/// the rclone "jottacloud" backend uses (observed behaviour, may change):
///
/// <list type="bullet">
/// <item>Auth (OIDC, client_id "jottacli"): the well-known token endpoint, <c>grant_type=password</c>
/// for the personal-token exchange and <c>refresh_token</c> afterwards.</item>
/// <item>File tree (XML, "JFS"): <c>https://jfs.jottacloud.com/jfs/{user}/{device}/{mountpoint}/{path}</c> —
/// GET lists, <c>?mkDir=true</c> creates, <c>?dl=true</c>/<c>?dlDir=true</c> trash, <c>?mv=</c>/<c>?cp=</c>
/// move and copy, <c>?mode=bin</c> downloads; <c>jfs/{user}</c> reports capacity and usage.</item>
/// <item>Upload: <c>POST api.jottacloud.com/files/v1/allocate</c> with path, size and MD5, then the
/// bytes to the returned upload URL — unless allocate reports COMPLETED (MD5 dedupe).</item>
/// </list>
///
/// The file area is device "Jotta", mountpoint "Archive" — the general file store that rclone
/// exposes and the web "Files" view shows.
/// </summary>
[CloudProvider(CloudProviderType.Jottacloud)]
public sealed class JottacloudProvider : CloudProvider
{
    internal const string JfsBase = "https://jfs.jottacloud.com/jfs/";
    internal const string ApiBase = "https://api.jottacloud.com/";

    /// <summary>Host the public share links point at — the one the web UI serves.</summary>
    internal const string WwwBase = "https://www.jottacloud.com/";

    internal const string DefaultTokenUrl = "https://id.jottacloud.com/auth/realms/jottacloud/protocol/openid-connect/token";

    internal const string TokenPageUrl = "https://www.jottacloud.com/web/secure";

    private const string Device = "Jotta";
    private const string Mountpoint = "Archive";
    private const string ClientId = "jottacli";
    private const string LogCategory = "jottacloud";

    /// <summary>Sub-folder date lookups run at most this many at once.</summary>
    private const int FolderDateConcurrency = 8;

    private readonly HttpClient _http;
    private readonly Lock _gate = new();

    /// <summary>
    /// Single-flight refresh: the refresh token rotates, so two parallel refreshes would spend
    /// the same token twice and Jottacloud would revoke the whole family.
    /// </summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private JottacloudCredentials? _credentials;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiry;

    public JottacloudProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal JottacloudProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _http = handler is null
            ? CloudHttp.Create()
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var stored = Credentials.Load<JottacloudCredentials>(CredentialKey);
        if (stored is { Username.Length: > 0, RefreshToken.Length: > 0, TokenEndpoint.Length: > 0 })
        {
            _credentials = stored;
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.Jottacloud;

    protected override string CredentialKeyPrefix => "jottacloud";

    public override bool IsAuthenticated
    {
        get
        {
            lock (_gate)
            {
                return _credentials is not null;
            }
        }
    }

    /// <summary>
    /// Sharing is a plain on/off toggle: <c>enableShare</c> takes no password, no expiry and no
    /// view-only mode, and the link can be read back and switched off again. With nothing a
    /// change could apply to, <see cref="ShareLinkCapabilities.CanUpdate"/> stays false.
    /// </summary>
    public override ShareLinkCapabilities ShareLinkCapabilities { get; } = new()
    {
        CanCreate = true,
        CanQueryExisting = true,
        CanUpdate = false,
        CanRemove = true,
    };

    public override ConnectSpec ConnectSpec => new(
        ConnectField.Info("Generate a Personal Login Token in your Jottacloud account, then paste it below."),
        ConnectField.Link("Open Jottacloud token page", TokenPageUrl),
        new ConnectField("token", ConnectFieldKind.Password, "Personal Login Token"),
        // The macOS text says "stored in your keychain", which is wrong here.
        ConnectField.Info("Jottacloud has no public API, so FileFluss uses the same interface as the official command-line tool. The token is exchanged for a login that's stored in Windows Credential Manager."));

    private string Username
    {
        get
        {
            lock (_gate)
            {
                return _credentials?.Username ?? throw CloudProviderException.NotAuthenticated();
            }
        }
    }

    // MARK: - Connecting

    /// <summary>
    /// Exchanges a pasted Personal Login Token — base64url JSON carrying the username, a
    /// well-known OIDC link and an auth token used as the password of a password grant — for an
    /// OAuth refresh token, and stores it.
    /// </summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var login = DecodeLoginToken(values.GetTrimmed("token"));
        if (login is not { Username.Length: > 0, AuthToken.Length: > 0 })
        {
            throw CloudProviderException.InvalidCredentials();
        }

        var tokenEndpoint = await DiscoverTokenEndpointAsync(login.WellKnownLink, cancellationToken).ConfigureAwait(false);
        var (status, tokens) = await RequestTokenAsync(
            tokenEndpoint,
            [
                ("grant_type", "password"),
                ("client_id", ClientId),
                ("username", login.Username),
                ("password", login.AuthToken),
                ("scope", "openid offline_access"),
            ],
            cancellationToken).ConfigureAwait(false);

        if (tokens is null)
        {
            SupportLogger.Shared.Log($"Token request failed: HTTP {(int)status}", LogCategory, LogLevel.Error);
            throw (int)status is 400 or 401 or 403 ? CloudProviderException.InvalidCredentials() : CloudProviderException.ServerError((int)status);
        }

        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            throw CloudProviderException.InvalidCredentials();
        }

        var credentials = new JottacloudCredentials(login.Username, tokens.RefreshToken, tokenEndpoint);
        Credentials.Save(CredentialKey, credentials);
        lock (_gate)
        {
            _credentials = credentials;
            _accessToken = string.IsNullOrEmpty(tokens.AccessToken) ? null : tokens.AccessToken;
            _accessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn ?? 3600);
        }

        SupportLogger.Shared.Log($"Connected account {login.Username}", LogCategory);
        return new ConnectResult(login.Username);
    }

    public override Task DisconnectAsync()
    {
        lock (_gate)
        {
            _credentials = null;
            _accessToken = null;
        }

        return base.DisconnectAsync();
    }

    internal static JottacloudLoginToken? DecodeLoginToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            var base64 = token.Replace('-', '+').Replace('_', '/');
            var padding = base64.Length % 4;
            if (padding != 0)
            {
                base64 += new string('=', 4 - padding);
            }

            return JsonSerializer.Deserialize<JottacloudLoginToken>(Convert.FromBase64String(base64));
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The OIDC token endpoint from the well-known link; the default when it can't be read.</summary>
    private async Task<string> DiscoverTokenEndpointAsync(string? wellKnownLink, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(wellKnownLink, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps)
        {
            return DefaultTokenUrl;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, link);
            using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var wellKnown = await response.ReadJsonAsync<JottacloudWellKnown>(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(wellKnown.TokenEndpoint))
                {
                    return wellKnown.TokenEndpoint;
                }
            }
        }
        catch (CloudProviderException)
        {
            // Fall back to the default endpoint, as macOS does.
        }

        return DefaultTokenUrl;
    }

    private async Task<(HttpStatusCode Status, JottacloudTokenResponse? Tokens)> RequestTokenAsync(
        string endpoint, (string Key, string Value)[] form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = CloudHttp.FormContent(form) };
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, null);
        }

        var tokens = await response.ReadJsonAsync<JottacloudTokenResponse>(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, tokens);
    }

    // MARK: - Tokens

    /// <summary>A non-expired access token, refreshing (single-flight) when needed.</summary>
    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        if (CachedAccessToken() is { } cached)
        {
            return cached;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while this one waited.
            if (CachedAccessToken() is { } refreshed)
            {
                return refreshed;
            }

            JottacloudCredentials credentials;
            lock (_gate)
            {
                credentials = _credentials ?? throw CloudProviderException.NotAuthenticated();
            }

            var (status, tokens) = await RequestTokenAsync(
                credentials.TokenEndpoint,
                [("grant_type", "refresh_token"), ("client_id", ClientId), ("refresh_token", credentials.RefreshToken)],
                cancellationToken).ConfigureAwait(false);

            if (tokens is null)
            {
                SupportLogger.Shared.Log($"Token refresh failed: HTTP {(int)status}", LogCategory, LogLevel.Error);

                // A revoked or rotated-away refresh token comes back as 400 invalid_grant. Other
                // failures (server trouble) are not a reason to ask the user to sign in again.
                throw (int)status is 400 or 401 or 403
                    ? CloudProviderException.NotAuthenticated()
                    : CloudProviderException.ServerError((int)status);
            }

            if (string.IsNullOrEmpty(tokens.AccessToken))
            {
                throw CloudProviderException.InvalidResponse();
            }

            lock (_gate)
            {
                _accessToken = tokens.AccessToken;
                _accessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn ?? 3600);

                // Persist the rotated refresh token at once so the next refresh — or the next
                // launch — doesn't present a spent one.
                if (!string.IsNullOrEmpty(tokens.RefreshToken) &&
                    tokens.RefreshToken != credentials.RefreshToken &&
                    ReferenceEquals(_credentials, credentials))
                {
                    _credentials = credentials with { RefreshToken = tokens.RefreshToken };
                    Credentials.Save(CredentialKey, _credentials);
                }
            }

            return tokens.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private string? CachedAccessToken()
    {
        lock (_gate)
        {
            if (_credentials is null)
            {
                throw CloudProviderException.NotAuthenticated();
            }

            return _accessToken is not null && _accessTokenExpiry > DateTimeOffset.UtcNow.AddSeconds(60) ? _accessToken : null;
        }
    }

    private void ForgetAccessToken(string token)
    {
        lock (_gate)
        {
            if (_accessToken == token)
            {
                _accessToken = null;
            }
        }
    }

    // MARK: - Request plumbing

    /// <summary>
    /// Sends an authenticated request. A 401 on a cached access token (revoked early, clock
    /// skew) refreshes once and retries — unless the body can't be replayed.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, bool transfer, CancellationToken cancellationToken, bool allowRetry = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await AccessTokenAsync(cancellationToken).ConfigureAwait(false);
            using var request = build();
            request.Headers.Authorization = CloudHttp.Bearer(token);
            var response = transfer
                ? await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false)
                : await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && allowRetry)
            {
                response.Dispose();
                ForgetAccessToken(token);
                continue;
            }

            return response;
        }
    }

    /// <summary>A JFS request expecting an XML (or empty) body; throws the mapped error on non-2xx.</summary>
    private async Task<string> JfsAsync(HttpMethod method, string url, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(method, url), transfer: false, cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapStatus(response.StatusCode, path);
        }

        return body;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw CloudProviderException.Network(e);
        }
    }

    private static CloudProviderException MapStatus(HttpStatusCode status, string path) => (int)status switch
    {
        401 or 403 => CloudProviderException.Unauthorized(),
        _ => CloudHttp.StatusError(status, path),
    };

    // MARK: - Paths

    /// <summary>"/a/b/" → "a/b"; "/" → "".</summary>
    private static string Relative(string path) => CloudPath.Normalize(path).TrimStart('/');

    /// <summary>JFS URL for a FileFluss path: jfs/{user}/{device}/{mountpoint}/{path}.</summary>
    internal static string JfsUrl(string username, string path, params (string Name, string Value)[] query)
    {
        var relative = Relative(path);
        var full = $"{username}/{Device}/{Mountpoint}" + (relative.Length > 0 ? "/" + relative : string.Empty);
        var url = JfsBase + CloudHttp.EscapePath(full);
        return query.Length == 0
            ? url
            : url + "?" + string.Join('&', query.Select(q => q.Name + "=" + EscapeQueryValue(q.Value)));
    }

    /// <summary>Absolute JFS path used as the value of mv/cp: /{user}/{device}/{mountpoint}/{dest}.</summary>
    internal static string AbsoluteJfsPath(string username, string path)
    {
        var relative = Relative(path);
        return $"/{username}/{Device}/{Mountpoint}" + (relative.Length > 0 ? "/" + relative : string.Empty);
    }

    /// <summary>
    /// Path for the files-v1 allocate API: /jfs/{device}/{mountpoint}/{path} (no username — the
    /// token implies it). Sent as a JSON string, so not percent-encoded.
    /// </summary>
    internal static string AllocatePath(string path)
    {
        var relative = Relative(path);
        return $"/jfs/{Device}/{Mountpoint}" + (relative.Length > 0 ? "/" + relative : string.Empty);
    }

    /// <summary>Strict query encoding (a name with "&amp;" or "+" must not split the query), keeping "/" readable.</summary>
    private static string EscapeQueryValue(string value) => Uri.EscapeDataString(value).Replace("%2F", "/", StringComparison.Ordinal);

    /// <summary>Jottacloud timestamp format, e.g. "2024-01-15-T10:30:00Z".</summary>
    internal static string JottaTime(DateTime utc)
        => utc.ToUniversalTime().ToString("yyyy-MM-dd'-T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // MARK: - Listing

    public override async Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var items = await ListRawAsync(path, cancellationToken).ConfigureAwait(false);

        // A JFS listing carries dates for files but not for sub-folders. Fetch each sub-folder's
        // own modified time so the panel shows real dates.
        return await EnrichFolderDatesAsync(items, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Listing without the per-sub-folder date fetch — for recursive walks and metadata lookups.</summary>
    private async Task<IReadOnlyList<CloudFileItem>> ListRawAsync(string path, CancellationToken cancellationToken)
    {
        var xml = await JfsAsync(HttpMethod.Get, JfsUrl(Username, path), path, cancellationToken).ConfigureAwait(false);
        return JottacloudXmlParser.ParseListing(xml, CloudPath.Normalize(path));
    }

    private async Task<IReadOnlyList<CloudFileItem>> EnrichFolderDatesAsync(IReadOnlyList<CloudFileItem> items, CancellationToken cancellationToken)
    {
        if (!items.Any(i => i.IsDirectory))
        {
            return items;
        }

        using var throttle = new SemaphoreSlim(FolderDateConcurrency);
        var tasks = items.Select(async item =>
        {
            if (!item.IsDirectory)
            {
                return item;
            }

            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await FolderModifiedDateAsync(item.Path, cancellationToken).ConfigureAwait(false) is { } date
                    ? item with { ModificationDate = date }
                    : item;
            }
            finally
            {
                throttle.Release();
            }
        }).ToList();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>A folder's own modified date; null on any failure so it keeps its placeholder.</summary>
    private async Task<DateTimeOffset?> FolderModifiedDateAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var xml = await JfsAsync(HttpMethod.Get, JfsUrl(Username, path), path, cancellationToken).ConfigureAwait(false);
            return JottacloudXmlParser.ParseFolderModified(xml);
        }
        catch (CloudProviderException)
        {
            return null;
        }
    }

    public override async Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        if (CloudPath.IsRoot(path))
        {
            return new CloudFileItem("d:/", string.Empty, "/", isDirectory: true, 0, CloudFileItem.UnknownDate);
        }

        var name = CloudPath.Name(path);
        var entries = await ListRawAsync(CloudPath.Parent(path), cancellationToken).ConfigureAwait(false);
        return entries.FirstOrDefault(e => e.Name == name) ?? throw CloudProviderException.NotFound(path);
    }

    public override async Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in await ListRawAsync(pending.Pop(), cancellationToken).ConfigureAwait(false))
            {
                if (entry.IsDirectory)
                {
                    pending.Push(entry.Path);
                }
                else
                {
                    total += entry.Size;
                }
            }
        }

        return total;
    }

    // MARK: - File operations

    public override async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => await JfsAsync(HttpMethod.Post, JfsUrl(Username, path, ("mkDir", "true")), path, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Both forms move the item to the Jottacloud trash (recoverable). <c>dl=true</c> only
    /// deletes files; folders need <c>dlDir=true</c>. The type isn't known here, so the file
    /// form goes first (the common case, one request) and the folder form is the fallback.
    /// </summary>
    public override async Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
    {
        var username = Username;
        try
        {
            await JfsAsync(HttpMethod.Post, JfsUrl(username, path, ("dl", "true")), path, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException original)
        {
            try
            {
                await JfsAsync(HttpMethod.Post, JfsUrl(username, path, ("dlDir", "true")), path, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException)
            {
                // Surface the original (file-delete) error.
                throw original;
            }
        }
    }

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => MoveItemAsync(path, CloudPath.Combine(CloudPath.Parent(path), newName), cancellationToken);

    public override async Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        var username = Username;
        await JfsAsync(HttpMethod.Post, JfsUrl(username, path, ("mv", AbsoluteJfsPath(username, newPath))), path, cancellationToken).ConfigureAwait(false);
    }

    public override async Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        var username = Username;
        await JfsAsync(HttpMethod.Post, JfsUrl(username, path, ("cp", AbsoluteJfsPath(username, newPath))), path, cancellationToken).ConfigureAwait(false);
    }

    public override async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var url = JfsUrl(Username, remotePath, ("mode", "bin"));
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), transfer: true, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapStatus(response.StatusCode, remotePath);
        }

        await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    public override async Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(localPath);
        if (!file.Exists)
        {
            throw CloudProviderException.CommandFailed(Localization.L("Item not found: {0}", localPath));
        }

        var size = file.Length;
        var modified = file.LastWriteTimeUtc;
        var created = file.CreationTimeUtc;
        var md5 = await StreamingMd5Async(localPath, cancellationToken).ConfigureAwait(false);

        // 1) Allocate. The server may answer COMPLETED for content it already has (MD5
        //    dedupe) — an instant, zero-byte upload.
        var allocation = new JottacloudAllocateRequest(AllocatePath(remotePath), size, md5, JottaTime(created), JottaTime(modified));
        JottacloudAllocateResponse allocated;
        using (var response = await SendAsync(
                   () => new HttpRequestMessage(HttpMethod.Post, ApiBase + "files/v1/allocate") { Content = CloudHttp.JsonContent(allocation) },
                   transfer: false,
                   cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw CloudProviderException.SizeLimitError((int)response.StatusCode, localPath) ?? MapStatus(response.StatusCode, remotePath);
            }

            allocated = await response.ReadJsonAsync<JottacloudAllocateResponse>(cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(allocated.State, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            // Nothing travels, but the file is complete: let the transfer's byte count say so.
            onBytes?.Invoke(size);
            return;
        }

        if (!Uri.TryCreate(allocated.UploadUrl, UriKind.Absolute, out var uploadUrl))
        {
            throw CloudProviderException.InvalidResponse();
        }

        // 2) The bytes, streamed from disk. No 401 retry: the body may already be on the wire.
        using var upload = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = ProgressStreamContent.FromFile(localPath, onBytes) },
            transfer: true,
            cancellationToken,
            allowRetry: false).ConfigureAwait(false);
        if (!upload.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"Upload failed: HTTP {(int)upload.StatusCode}", LogCategory, LogLevel.Error);
            throw CloudProviderException.SizeLimitError((int)upload.StatusCode, localPath) ?? MapStatus(upload.StatusCode, remotePath);
        }
    }

    /// <summary>MD5 of a file as lowercase hex, streamed so large files never load into memory.</summary>
    internal static async Task<string> StreamingMd5Async(string localPath, CancellationToken cancellationToken)
    {
        await using var stream = HttpTransfer.OpenForUpload(localPath);
        var hash = await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    // MARK: - Quota

    /// <summary>The account root (jfs/{user}) reports capacity and usage; capacity -1 is an unlimited plan.</summary>
    public override async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
    {
        var username = Username;
        var xml = await JfsAsync(HttpMethod.Get, JfsBase + CloudHttp.Escape(username), "/", cancellationToken).ConfigureAwait(false);
        return JottacloudXmlParser.ParseAccountUsage(xml) is { } usage
            ? new CloudStorageQuota(usage.Usage, usage.Capacity < 0 ? null : usage.Capacity)
            : null;
    }

    // MARK: - Share links
    //
    // Part of the same undocumented JFS API, the endpoints rclone's backend has used for years:
    //   GET {jfsURL}?mode=enableShare    turn the public link on
    //   GET {jfsURL}?mode=disableShare   turn it off again
    //   GET {jfsURL}                     read it back
    // All three answer with the file's JFS XML; a shared file additionally carries <publicURI>
    // and <publicSharePath>.

    /// <summary>Turns the public link on. <paramref name="options"/> is ignored: enableShare takes none.</summary>
    public override async Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
    {
        var username = Username;
        var xml = await ShareRequestAsync(JfsUrl(username, path, ("mode", "enableShare")), path, cancellationToken).ConfigureAwait(false);

        // A 2xx without <publicURI> means the toggle didn't take — most likely an account or
        // plan that can't share.
        var share = JottacloudXmlParser.ParsePublicShare(xml)
            ?? throw CloudProviderException.CommandFailed(Localization.L("Jottacloud accepted the request but returned no public link for this file."));
        return ShareLinkFrom(username, share);
    }

    /// <summary>
    /// The link the file already has, or null. A plain GET carries &lt;publicURI&gt; only while
    /// sharing is on, so its absence is the "not shared" answer; a missing file still throws
    /// NotFound, so the two can't be confused.
    /// </summary>
    public override async Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken = default)
    {
        var username = Username;
        var xml = await ShareRequestAsync(JfsUrl(username, path), path, cancellationToken).ConfigureAwait(false);
        return JottacloudXmlParser.ParsePublicShare(xml) is { } share ? ShareLinkFrom(username, share) : null;
    }

    /// <summary>
    /// Withdraws the link. disableShare on an unshared file succeeds, so this doesn't look first:
    /// "this file is not shared" ends up true either way.
    /// </summary>
    public override async Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => await ShareRequestAsync(JfsUrl(Username, path, ("mode", "disableShare")), path, cancellationToken).ConfigureAwait(false);

    internal static CloudShareLink ShareLinkFrom(string username, JottacloudXmlParser.PublicShare share)
    {
        var direct = new Uri(WwwBase + "opin/io/downloadPublic/" + CloudHttp.Escape(username) + "/" + CloudHttp.Escape(share.PublicUri));

        // The landing page is what the web UI hands out; the direct link stands in when a
        // document lacks publicSharePath.
        var landing = share.PublicSharePath.Length == 0
            ? direct
            : new Uri(WwwBase + CloudHttp.EscapePath(share.PublicSharePath.TrimStart('/')));
        return new CloudShareLink(landing) { DirectDownloadUrl = direct };
    }

    /// <summary>
    /// A share-mode request: like the other JFS calls, but a failure body is scanned for JFS's
    /// own &lt;message&gt;, so a refusal reaches the user in Jottacloud's words. 404 keeps
    /// NotFound so a missing file stays distinguishable from a refused share.
    /// </summary>
    private async Task<string> ShareRequestAsync(string url, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), transfer: false, cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return body;
        }

        SupportLogger.Shared.Log($"Share request failed: HTTP {(int)response.StatusCode}", LogCategory, LogLevel.Error);
        if (response.StatusCode != HttpStatusCode.NotFound && JottacloudXmlParser.ParseError(body) is { } message)
        {
            throw CloudProviderException.CommandFailed(message);
        }

        throw MapStatus(response.StatusCode, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
            _refreshGate.Dispose();
        }

        base.Dispose(disposing);
    }
}
