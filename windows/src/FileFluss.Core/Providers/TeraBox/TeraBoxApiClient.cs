// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.TeraBox;

/// <summary>
/// The TeraBox Open Platform application credentials. They are issued by TeraBox when an
/// integration is approved — not self-serve — and the macOS app ships placeholders until
/// they are provisioned. With placeholders sign-in cannot succeed, so connecting reports
/// "not yet implemented", exactly as on macOS.
/// </summary>
internal sealed record TeraBoxAppCredentials(string ClientId, string ClientSecret, string PrivateSecret)
{
    /// <summary>Fixed app id the shard-upload endpoint requires, per the TeraBox docs.</summary>
    public const string UploadAppId = "250528";

    public static readonly TeraBoxAppCredentials Default = new(
        "YOUR_TERABOX_CLIENT_ID",          // AppKey
        "YOUR_TERABOX_CLIENT_SECRET",      // SecretKey
        "YOUR_TERABOX_PRIVATE_SECRET");    // used only for the sign

    public bool IsConfigured =>
        !ClientId.StartsWith("YOUR_", StringComparison.Ordinal) &&
        !ClientSecret.StartsWith("YOUR_", StringComparison.Ordinal) &&
        !PrivateSecret.StartsWith("YOUR_", StringComparison.Ordinal);
}

/// <summary>
/// Persisted post-login state. TeraBox tokens are short-lived (access 2 days, refresh 30
/// days, single-use refresh), so the refresh token and the per-user domains are kept and the
/// access token is refreshed on demand.
/// </summary>
/// <param name="ApiDomain">Host for every basic API except the shard upload (from /oauth/tokeninfo).</param>
/// <param name="UploadDomain">Host for the /rest/2.0/pcs/superfile2 shard upload.</param>
internal sealed record TeraBoxCredentials(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string ApiDomain,
    string UploadDomain,
    long UserId);

/// <summary>The device-code handshake: show the QR, poll for the token.</summary>
/// <param name="QrPng">Decoded PNG of the QR the user scans in the TeraBox app; null when the data URI was unreadable.</param>
internal sealed record TeraBoxDeviceCode(string DeviceCode, byte[]? QrPng, int Interval, int ExpiresIn);

/// <summary>
/// The TeraBox Open Platform API — port of the macOS <c>TeraBoxAPIClient</c>. Auth is Device
/// Code mode; the token endpoints are signed with
/// <c>md5(client_id_timestamp_client_secret_private_secret)</c>; the <c>openapi</c> endpoints
/// take the token as an <c>access_tokens</c> query parameter and report failures as
/// <c>errno</c> in a 200 body. Uploads are precreate → superfile2 shards → create.
/// </summary>
internal sealed class TeraBoxApiClient
{
    internal const string AuthHost = "https://www.terabox.com";

    /// <summary>4 MB: the API requires at least 4 MB per non-final shard.</summary>
    internal const int SliceSize = 4 * 1024 * 1024;

    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly TeraBoxAppCredentials _app;
    private readonly Action<TeraBoxCredentials> _persist;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Lock _gate = new();
    private TeraBoxCredentials _credentials;

    /// <param name="persist">Called with every refreshed token pair — each refresh token is single-use, so it must be stored at once.</param>
    public TeraBoxApiClient(HttpClient http, TeraBoxAppCredentials app, TeraBoxCredentials credentials, Action<TeraBoxCredentials> persist)
    {
        _http = http;
        _app = app;
        _credentials = credentials;
        _persist = persist;
    }

    public TeraBoxCredentials CurrentCredentials
    {
        get
        {
            lock (_gate)
            {
                return _credentials;
            }
        }
    }

    public string UserDisplayName() => $"TeraBox ({CurrentCredentials.UserId.ToString(CultureInfo.InvariantCulture)})";

    // MARK: - Device-code authentication

    /// <summary>Step 1: a device code and the QR to scan. Needs only the client id.</summary>
    public static async Task<TeraBoxDeviceCode> RequestDeviceCodeAsync(HttpClient http, TeraBoxAppCredentials app, CancellationToken cancellationToken)
    {
        if (!app.IsConfigured)
        {
            throw CloudProviderException.NotImplemented();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{AuthHost}/oauth/devicecode?client_id={CloudHttp.Escape(app.ClientId)}");
        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await CheckHttpAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = ParseJson(body);
        var data = Object(document.RootElement, "data") ?? throw CloudProviderException.InvalidResponse();
        return new TeraBoxDeviceCode(
            String(data, "device_code") ?? throw CloudProviderException.InvalidResponse(),
            DecodeDataUriPng(String(data, "qrcode_url") ?? string.Empty),
            (int)(Int64(data, "interval") ?? 5),
            (int)(Int64(data, "expires_in") ?? 300));
    }

    /// <summary>
    /// Step 2: polls /oauth/gettoken until the user authorizes in the TeraBox app or the code
    /// expires. Every failed poll is retried — "not authorized yet" (errno 400001) is the
    /// expected answer until the QR is scanned.
    /// </summary>
    public static async Task<TeraBoxCredentials> PollForTokenAsync(
        HttpClient http,
        TeraBoxAppCredentials app,
        TeraBoxDeviceCode device,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);
        var interval = TimeSpan.FromSeconds(Math.Max(2, device.Interval));
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TokenResponse? tokens = null;
            try
            {
                tokens = await ExchangeAsync(http, app, "device_code", device.DeviceCode, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException)
            {
                // Not authorized yet, or a transient failure: poll again.
            }

            if (tokens is not null)
            {
                return await CompleteWithTokenInfoAsync(http, tokens, cancellationToken).ConfigureAwait(false);
            }

            await delay(interval, cancellationToken).ConfigureAwait(false);
        }

        throw CloudProviderException.CommandFailed(Localization.L("Authorization timed out — the QR code expired before it was scanned."));
    }

    internal sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresIn);

    private static async Task<TokenResponse> ExchangeAsync(HttpClient http, TeraBoxAppCredentials app, string grantType, string code, CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AuthHost}/oauth/gettoken")
        {
            Content = Multipart(
                ("client_id", app.ClientId),
                ("client_secret", app.ClientSecret),
                ("grant_type", grantType),
                ("code", code),
                ("timestamp", timestamp),
                ("sign", Sign(app, timestamp))),
        };
        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = ParseJson(body);
        var errno = Int64(document.RootElement, "errno") ?? -1;
        var token = ParseToken(Object(document.RootElement, "data"));

        // 400001 = the user hasn't authorized yet (device mode); the caller retries.
        if ((int)response.StatusCode != 200 || errno != 0 || token is null)
        {
            throw MapAuthError(errno);
        }

        return token;
    }

    private static async Task<TeraBoxCredentials> CompleteWithTokenInfoAsync(HttpClient http, TokenResponse tokens, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AuthHost}/oauth/tokeninfo")
        {
            Content = Multipart(("access_token", tokens.AccessToken)),
        };
        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await CheckHttpAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = ParseJson(body);
        var data = Object(document.RootElement, "data") ?? throw CloudProviderException.InvalidResponse();
        return new TeraBoxCredentials(
            tokens.AccessToken,
            tokens.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn),
            String(data, "api_domain") ?? throw CloudProviderException.InvalidResponse(),
            String(data, "upload_domain") ?? throw CloudProviderException.InvalidResponse(),
            Int64(data, "user_id") ?? throw CloudProviderException.InvalidResponse());
    }

    /// <summary>
    /// Refreshes when the access token is within five minutes of expiry. Single-flight: the
    /// refresh token is single-use, so a second concurrent refresh would invalidate the first.
    /// The new pair is persisted before anyone uses it.
    /// </summary>
    public async Task RefreshIfNeededAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < CurrentCredentials.ExpiresAt - RefreshMargin)
        {
            return;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = CurrentCredentials;
            if (DateTimeOffset.UtcNow < current.ExpiresAt - RefreshMargin)
            {
                return; // Another caller refreshed while this one waited.
            }

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{AuthHost}/oauth/refreshtoken")
            {
                Content = Multipart(
                    ("client_id", _app.ClientId),
                    ("client_secret", _app.ClientSecret),
                    ("refresh_token", current.RefreshToken),
                    ("timestamp", timestamp),
                    ("sign", Sign(_app, timestamp))),
            };
            // Not the caller's token: the refresh token is single-use, so abandoning a response
            // the server already acted on (a cancelled listing) would lose the new pair for good.
            using var response = await _http.SendApiAsync(request, CancellationToken.None).ConfigureAwait(false);
            var body = await CheckHttpAsync(response, CancellationToken.None).ConfigureAwait(false);
            using var document = ParseJson(body);
            var errno = Int64(document.RootElement, "errno") ?? -1;
            var token = ParseToken(Object(document.RootElement, "data"));
            if (errno != 0 || token is null)
            {
                throw MapAuthError(errno);
            }

            var refreshed = current with
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
            };
            lock (_gate)
            {
                _credentials = refreshed;
            }

            _persist(refreshed);
            SupportLogger.Shared.Log("[TeraBox] Access token refreshed", "TeraBox");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>The token-endpoint signature: md5("client_id_timestamp_client_secret_private_secret"), lower-case hex.</summary>
    internal static string Sign(TeraBoxAppCredentials app, string timestamp)
        => Md5Hex(Encoding.UTF8.GetBytes($"{app.ClientId}_{timestamp}_{app.ClientSecret}_{app.PrivateSecret}"));

    // MARK: - Listing

    public async Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<CloudFileItem>();
        for (var page = 1; ; page++)
        {
            var body = await ApiGetAsync(
                $"/openapi/api/list?dir={CloudHttp.Escape(path)}&order=name&desc=0&num=1000&page={page.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken).ConfigureAwait(false);
            CheckErrno(body, path);
            using var document = ParseJson(body);
            var entries = document.RootElement.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().ToList()
                : [];
            foreach (var entry in entries)
            {
                var name = String(entry, "server_filename") ?? string.Empty;
                var entryPath = String(entry, "path") is { Length: > 0 } p ? CloudPath.Normalize(p) : CloudPath.Combine(path, name);
                var modified = Int64(entry, "server_mtime") is { } mtime ? DateTimeOffset.FromUnixTimeSeconds(mtime) : CloudFileItem.UnknownDate;
                items.Add(new CloudFileItem(
                    (Int64(entry, "fs_id") ?? 0).ToString(CultureInfo.InvariantCulture),
                    name,
                    entryPath,
                    Int64(entry, "isdir") == 1,
                    Int64(entry, "size") ?? 0,
                    modified,
                    String(entry, "md5"))
                {
                    IsHidden = CloudFileItem.IsDotHidden(name),
                });
            }

            if (entries.Count < 1000)
            {
                break;
            }
        }

        return items;
    }

    // MARK: - Download

    public async Task DownloadAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);

        // Resolve the file's download link by path. The path inside the JSON array is itself
        // percent-encoded (host-allowed set), as macOS sends it.
        var target = $"[\"{EncodeHostAllowed(remotePath)}\"]";
        var body = await ApiGetAsync($"/openapi/api/filemetas?target={CloudHttp.Escape(target)}&dlink=1", cancellationToken).ConfigureAwait(false);
        string? dlink = null;
        using (var document = ParseJson(body))
        {
            foreach (var key in (string[])["info", "list"])
            {
                if (document.RootElement.TryGetProperty(key, out var info) && info.ValueKind == JsonValueKind.Array && info.GetArrayLength() > 0)
                {
                    dlink = String(info[0], "dlink");
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(dlink))
        {
            throw CloudProviderException.NotFound(remotePath);
        }

        // The dlink wants the access token appended, and a TeraBox user agent.
        var separator = dlink.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        if (!Uri.TryCreate($"{dlink}{separator}access_tokens={CloudHttp.Escape(CurrentCredentials.AccessToken)}", UriKind.Absolute, out var url))
        {
            throw CloudProviderException.InvalidResponse();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "pan.baidu.com");
        using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CloudHttp.StatusError(response.StatusCode, remotePath);
        }

        await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    // MARK: - Upload (precreate → superfile2 → create)

    public async Task UploadAsync(string localPath, string remotePath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
        var length = new FileInfo(localPath).Length;

        // One pass over the file for the per-shard MD5s; the shards are then streamed from
        // disk one at a time, never held in memory together.
        var blockMd5s = await BlockMd5sAsync(localPath, cancellationToken).ConfigureAwait(false);
        var blockList = JsonSerializer.Serialize(blockMd5s);

        // 1. precreate
        var precreate = await ApiPostAsync(
            "/openapi/api/precreate",
            cancellationToken,
            ("path", remotePath),
            ("autoinit", "1"),
            ("block_list", blockList)).ConfigureAwait(false);
        string? uploadId;
        using (var document = ParseJson(precreate))
        {
            if (Int64(document.RootElement, "return_type") == 2)
            {
                return; // Already in the cloud (rapid upload) — nothing to send.
            }

            uploadId = String(document.RootElement, "uploadid");
        }

        if (uploadId is null)
        {
            CheckErrno(precreate, remotePath);
            throw CloudProviderException.InvalidResponse();
        }

        // 2. each shard to the upload domain's superfile2 endpoint
        for (var index = 0; index < blockMd5s.Count; index++)
        {
            var offset = (long)index * SliceSize;
            var sliceLength = Math.Max(0, Math.Min(SliceSize, length - offset));
            var credentials = CurrentCredentials;
            var url = $"{Https(credentials.UploadDomain)}/rest/2.0/pcs/superfile2?method=upload&app_id={TeraBoxAppCredentials.UploadAppId}" +
                      $"&path={CloudHttp.Escape(remotePath)}&uploadid={CloudHttp.Escape(uploadId)}&partseq={index.ToString(CultureInfo.InvariantCulture)}" +
                      $"&access_tokens={CloudHttp.Escape(credentials.AccessToken)}";
            using var shard = new ProgressStreamContent(new FileSliceStream(localPath, offset, sliceLength), onBytes, sliceLength);
            shard.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"file\"", FileName = "\"blob\"" };
            using var form = new MultipartFormDataContent();
            form.Add(shard);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = await response.ReadBodySnippetAsync(cancellationToken, 300).ConfigureAwait(false);
                SupportLogger.Shared.Log($"[TeraBox] superfile2 shard {index} failed: HTTP {(int)response.StatusCode} {snippet}", "TeraBox", LogLevel.Error);
                throw CloudProviderException.SizeLimitError((int)response.StatusCode, localPath)
                      ?? CloudHttp.StatusError(response.StatusCode, remotePath);
            }
        }

        // 3. create (merge), overwriting an existing file
        var created = await ApiPostAsync(
            "/openapi/api/create",
            cancellationToken,
            ("path", remotePath),
            ("size", length.ToString(CultureInfo.InvariantCulture)),
            ("uploadid", uploadId),
            ("block_list", blockList),
            ("rtype", "3")).ConfigureAwait(false);
        CheckErrno(created, remotePath);
    }

    /// <summary>MD5 of each 4 MB shard; a zero-byte file is one empty shard.</summary>
    internal static async Task<List<string>> BlockMd5sAsync(string localPath, CancellationToken cancellationToken)
    {
        var hashes = new List<string>();
        var buffer = new byte[HttpTransfer.BufferSize];
        await using var file = HttpTransfer.OpenForUpload(localPath);
        do
        {
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var remaining = SliceSize;
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                md5.AppendData(buffer, 0, read);
                remaining -= read;
            }

            hashes.Add(Convert.ToHexStringLower(md5.GetHashAndReset()));
        }
        while (file.Position < file.Length);

        return hashes;
    }

    // MARK: - Mutations

    /// <summary>
    /// Best-effort folder creation. TeraBox documents no dedicated mkdir; this follows the Baidu
    /// xpan convention (create with isdir=1). An existing folder counts as success.
    /// </summary>
    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
        var body = await ApiPostAsync("/openapi/api/create", cancellationToken, ("path", path), ("isdir", "1"), ("rtype", "0")).ConfigureAwait(false);
        if (Errno(body) == -8)
        {
            return;
        }

        CheckErrno(body, path);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
        var body = await ApiPostAsync("/openapi/api/filemanager?opera=delete", cancellationToken, ("filelist", JsonSerializer.Serialize(new[] { path }))).ConfigureAwait(false);
        CheckErrno(body, path);
    }

    public async Task RenameAsync(string path, string newName, CancellationToken cancellationToken)
    {
        await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
        var entry = JsonSerializer.Serialize(new[] { new Dictionary<string, string> { ["path"] = path, ["newname"] = newName } });
        var body = await ApiPostAsync("/openapi/api/filemanager?opera=rename", cancellationToken, ("filelist", entry)).ConfigureAwait(false);
        CheckErrno(body, path);
    }

    public async Task MoveAsync(string path, string newPath, CancellationToken cancellationToken)
    {
        await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
        var entry = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, string> { ["path"] = path, ["dest"] = CloudPath.Parent(newPath), ["newname"] = CloudPath.Name(newPath) },
        });
        var body = await ApiPostAsync("/openapi/api/filemanager?opera=move", cancellationToken, ("filelist", entry)).ConfigureAwait(false);
        CheckErrno(body, path);
    }

    // MARK: - Quota

    public async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken)
    {
        await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var body = await ApiGetAsync("/openapi/api/quota", cancellationToken).ConfigureAwait(false);
            using var document = ParseJson(body);
            return new CloudStorageQuota(Int64(document.RootElement, "used") ?? 0, Int64(document.RootElement, "total"));
        }
        catch (CloudProviderException)
        {
            return null;
        }
    }

    // MARK: - HTTP helpers

    private Uri ApiUri(string endpoint)
    {
        var credentials = CurrentCredentials;
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = $"{Https(credentials.ApiDomain)}{endpoint}{separator}access_tokens={CloudHttp.Escape(credentials.AccessToken)}";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : throw CloudProviderException.InvalidResponse();
    }

    private async Task<string> ApiGetAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUri(endpoint));
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        return await CheckHttpAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ApiPostAsync(string endpoint, CancellationToken cancellationToken, params (string Key, string Value)[] fields)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUri(endpoint)) { Content = CloudHttp.FormContent(fields) };
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        return await CheckHttpAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The domains come from /oauth/tokeninfo as bare host names.</summary>
    private static string Https(string domain)
        => domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? domain.TrimEnd('/')
            : "https://" + domain.TrimEnd('/');

    /// <summary>
    /// The token endpoints take multipart fields shaped as macOS sends them: a quoted name
    /// (.NET would write it bare) and no per-part content type.
    /// </summary>
    private static MultipartFormDataContent Multipart(params (string Key, string Value)[] fields)
    {
        var form = new MultipartFormDataContent();
        foreach (var (key, value) in fields)
        {
            var part = new StringContent(value, Encoding.UTF8);
            part.Headers.ContentType = null;
            part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = $"\"{key}\"" };
            form.Add(part);
        }

        return form;
    }

    private static async Task<string> CheckHttpAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Bodies here can echo request parameters; log the status only.
            SupportLogger.Shared.Log($"[TeraBox] HTTP {(int)response.StatusCode}", "TeraBox", LogLevel.Error);
            throw CloudHttp.StatusError(response.StatusCode);
        }

        return body;
    }

    private static long? Errno(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object ? Int64(document.RootElement, "errno") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The openapi endpoints report failures as <c>errno</c> in the JSON body, even with HTTP 200.</summary>
    internal static void CheckErrno(string body, string path)
    {
        if (Errno(body) is not { } errno || errno == 0)
        {
            return;
        }

        SupportLogger.Shared.Log($"[TeraBox] API errno {errno}", "TeraBox", LogLevel.Error);
        throw errno switch
        {
            -8 => CloudProviderException.CommandFailed(Localization.L("A file with that name already exists.")),
            -9 => CloudProviderException.NotFound(path),
            -7 => CloudProviderException.CommandFailed(Localization.L("Invalid file name.")),

            // Baidu-xpan convention, which the Open Platform mirrors: the access token was rejected.
            -6 => CloudProviderException.Unauthorized(),
            _ => CloudProviderException.ServerError((int)errno),
        };
    }

    internal static CloudProviderException MapAuthError(long errno) => errno switch
    {
        400001 => CloudProviderException.CommandFailed(Localization.L("Waiting for authorization in the TeraBox app.")),
        100001 => CloudProviderException.InvalidCredentials(),
        100002 => CloudProviderException.CommandFailed(Localization.L("Authorization code invalid or expired.")),
        200002 or 200003 or 200004 or 200005 => CloudProviderException.NotAuthenticated(),
        _ => CloudProviderException.ServerError((int)errno),
    };

    private static TokenResponse? ParseToken(JsonElement? data)
    {
        if (data is not { } value ||
            String(value, "access_token") is not { } access ||
            String(value, "refresh_token") is not { } refresh ||
            Int64(value, "expires_in") is not { } expiresIn)
        {
            return null;
        }

        return new TokenResponse(access, refresh, (int)expiresIn);
    }

    /// <summary>"data:image/png;base64,…" (or bare base64) → PNG bytes.</summary>
    internal static byte[]? DecodeDataUriPng(string uri)
    {
        var comma = uri.IndexOf(',', StringComparison.Ordinal);
        var payload = comma < 0 ? uri : uri[(comma + 1)..];
        try
        {
            return payload.Length == 0 ? null : Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Percent-encodes everything outside Foundation's <c>urlHostAllowed</c> set.</summary>
    internal static string EncodeHostAllowed(string value)
    {
        const string allowed = "!$&'()*+,-.0123456789:;=ABCDEFGHIJKLMNOPQRSTUVWXYZ[]_abcdefghijklmnopqrstuvwxyz~";
        var builder = new StringBuilder(value.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b < 128 && allowed.Contains((char)b, StringComparison.Ordinal))
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    internal static string Md5Hex(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));

    private static JsonDocument ParseJson(string body)
    {
        try
        {
            var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw CloudProviderException.InvalidResponse();
            }

            return document;
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    private static JsonElement? Object(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A number, or a numeric string — TeraBox is not consistent about which.</summary>
    private static long? Int64(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}
