// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.Box;

/// <summary>
/// Box Content API 2.0 (port of the macOS <c>BoxAPIClient</c>). Box addresses everything by
/// id, so paths are resolved through a path → id cache (root "/" is folder "0").
/// Thread-safe: one instance serves panels, transfers and the indexer concurrently.
///
/// <para>Every request carries the FileFluss User-Agent (<see cref="CloudHttp.UserAgent"/>,
/// set on the client): Box's edge blocks a generic one with an HTML 403 before the request
/// reaches the API, which is why macOS sets its own as well.</para>
/// </summary>
internal sealed class BoxApiClient
{
    // The developer's registered Box app. Box uses the confidential-client model — id and
    // secret are both required for the token exchange. Treat the secret as sensitive even
    // though it ships in the binary (standard practice for desktop apps).
    internal const string ClientId = "kv5fvna9auttv0d2su7ssies972hri96";
    internal const string ClientSecret = "meEDcWLwAphA0UfMaoOfK6MoCztrT2lB";

    internal const string AuthBaseUrl = "https://account.box.com/api/oauth2/authorize";
    internal const string TokenUrl = "https://api.box.com/oauth2/token";
    internal const string ApiUrl = "https://api.box.com/2.0";
    internal const string UploadUrl = "https://upload.box.com/api/2.0";

    /// <summary>
    /// Box's documented ceiling for a chunked upload. The account's own per-file limit
    /// (250 MB on free plans) is enforced when the upload session is opened and surfaces as
    /// FileTooLarge.
    /// </summary>
    internal const long MaxChunkedUploadBytes = 50L * 1024 * 1024 * 1024;

    internal const string ItemFields = "id,type,name,size,modified_at,content_modified_at,sha1";

    private const int PageLimit = 1000;
    private const string LogCategory = "Box";

    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http;
    private readonly Action<BoxCredentials> _credentialsChanged;
    private readonly Lock _gate = new();
    private BoxCredentials _credentials;

    /// <summary>
    /// The refresh in flight, if any. Box refresh tokens are single-use, so a parallel refresh
    /// always fails with invalid_grant and signs the account out; concurrent callers await
    /// this one task instead.
    /// </summary>
    private Task<BoxCredentials>? _inflightRefresh;

    /// <summary>
    /// Path → Box id. Walking a path resolves and caches intermediate folder ids, so repeated
    /// operations on the same subtree are O(1) lookups.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _pathIdCache = new(StringComparer.Ordinal) { ["/"] = "0" };

    /// <param name="credentialsChanged">
    /// Called with every refreshed credential set. Not optional housekeeping for Box: each
    /// refresh returns a NEW refresh token and invalidates the previous one, so keeping the new
    /// one only in memory leaves a dead token in the store and the next launch asks the user to
    /// sign in again, every time (issue #46).
    /// </param>
    public BoxApiClient(HttpClient http, BoxCredentials credentials, Action<BoxCredentials> credentialsChanged)
    {
        _http = http;
        _credentials = credentials;
        _credentialsChanged = credentialsChanged;
    }

    /// <summary>Files up to this size go up in one multipart request; larger ones through an upload session.</summary>
    internal long SinglePutMaxBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>Waits between upload retries and commit polls. Tests make it instant.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    internal IDictionary<string, string> PathIdCache => _pathIdCache;

    public BoxCredentials Credentials
    {
        get
        {
            lock (_gate)
            {
                return _credentials;
            }
        }
    }

    // MARK: - OAuth2

    public static Uri BuildAuthorizeUri(string redirectUri, string state)
    {
        var query = new (string, string)[]
        {
            ("response_type", "code"),
            ("client_id", ClientId),
            ("redirect_uri", redirectUri),
            ("state", state),
        };
        return new Uri(AuthBaseUrl + "?" + string.Join('&', query.Select(q => $"{q.Item1}={CloudHttp.Escape(q.Item2)}")));
    }

    public static async Task<BoxCredentials> ExchangeCodeAsync(HttpClient http, string code, string redirectUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = CloudHttp.FormContent(
                ("grant_type", "authorization_code"),
                ("code", code),
                ("client_id", ClientId),
                ("client_secret", ClientSecret),
                ("redirect_uri", redirectUri)),
        };
        request.Headers.Accept.ParseAdd("application/json");

        SupportLogger.Shared.Log($"[Box] Token POST → {TokenUrl}", LogCategory);
        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // A failed exchange carries no token, so its body is safe to log.
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            SupportLogger.Shared.Log($"[Box] Token exchange failed: HTTP {(int)response.StatusCode}: {Prefix(body, 500)}", LogCategory, LogLevel.Error);

            // Box's own error_description names the actual cause (typically "redirect_uri
            // does not match" or "invalid_client") instead of a misleading generic message.
            var detail = ParseOAuthError(body) ?? Prefix(body, 200);
            throw CloudProviderException.CommandFailed(Localization.L("Box authentication failed: {0}", detail));
        }

        var token = await response.ReadJsonAsync<BoxTokenResponse>(cancellationToken).ConfigureAwait(false);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
        var user = await FetchUserInfoAsync(http, token.AccessToken, cancellationToken).ConfigureAwait(false);
        SupportLogger.Shared.Log($"[Box] Authenticated as {user.Login}", LogCategory);
        return new BoxCredentials(token.AccessToken, token.RefreshToken ?? string.Empty, expiresAt, user.Login, user.Name);
    }

    /// <summary>/users/me; a placeholder user when the lookup fails, so the account is still added.</summary>
    private static async Task<BoxUser> FetchUserInfoAsync(HttpClient http, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl + "/users/me");
        request.Headers.Authorization = CloudHttp.Bearer(accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new BoxUser { Id = string.Empty, Name = "Box user", Login = string.Empty };
        }

        return await response.ReadJsonAsync<BoxUser>(cancellationToken).ConfigureAwait(false);
    }

    // MARK: - Token refresh

    /// <summary>The current credentials, refreshed first when they expire within a minute.</summary>
    public Task<BoxCredentials> RefreshIfNeededAsync(CancellationToken cancellationToken)
    {
        Task<BoxCredentials> task;
        lock (_gate)
        {
            // Another caller already refreshing: await its result rather than firing a second
            // /oauth2/token POST that Box would reject (the refresh token is single-use).
            if (_inflightRefresh is null && DateTimeOffset.UtcNow < _credentials.ExpiresAt.AddSeconds(-60))
            {
                return Task.FromResult(_credentials);
            }

            task = _inflightRefresh ?? StartRefreshLocked();
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Refreshes regardless of the cached expiry. Used after a 401: Box invalidates access
    /// tokens server-side (rotation, forced logout, suspicious activity) ahead of our expiry.
    /// </summary>
    public Task<BoxCredentials> ForceRefreshAsync(CancellationToken cancellationToken)
    {
        Task<BoxCredentials> task;
        lock (_gate)
        {
            task = _inflightRefresh ?? StartRefreshLocked();
        }

        return task.WaitAsync(cancellationToken);
    }

    private Task<BoxCredentials> StartRefreshLocked()
    {
        var refreshToken = _credentials.RefreshToken;
        if (string.IsNullOrEmpty(refreshToken))
        {
            return Task.FromException<BoxCredentials>(CloudProviderException.NotAuthenticated());
        }

        // Task.Run so the refresh never completes (and clears _inflightRefresh) before it was
        // published under the lock. Not tied to any one caller's token: others await it too.
        var task = Task.Run(() => RunRefreshAsync(refreshToken));
        _inflightRefresh = task;
        return task;
    }

    private async Task<BoxCredentials> RunRefreshAsync(string refreshToken)
    {
        try
        {
            return await PerformTokenRefreshAsync(refreshToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _inflightRefresh = null;
            }
        }
    }

    private async Task<BoxCredentials> PerformTokenRefreshAsync(string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = CloudHttp.FormContent(
                ("grant_type", "refresh_token"),
                ("refresh_token", refreshToken),
                ("client_id", ClientId),
                ("client_secret", ClientSecret)),
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _http.SendApiAsync(request, CancellationToken.None).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"[Box] Token refresh failed: HTTP {(int)response.StatusCode}", LogCategory, LogLevel.Error);
            throw CloudProviderException.TokenRefreshFailed((int)response.StatusCode);
        }

        var token = await response.ReadJsonAsync<BoxTokenResponse>(CancellationToken.None).ConfigureAwait(false);
        BoxCredentials updated;
        lock (_gate)
        {
            updated = _credentials with
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken ?? _credentials.RefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
            };
            _credentials = updated;
        }

        // Persist before anyone uses the new token: the old refresh token is already dead.
        _credentialsChanged(updated);
        return updated;
    }

    // MARK: - Quota

    /// <summary>
    /// /users/me?fields=space_amount,space_used. <c>space_amount</c> is the total in bytes;
    /// -1 means "no quota assigned" (typical for enterprise accounts) and becomes "no total".
    /// </summary>
    public async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken)
    {
        var credentials = await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl + "/users/me?fields=space_amount,space_used");
        request.Headers.Authorization = CloudHttp.Bearer(credentials.AccessToken);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            var amount = ReadNumber(document.RootElement, "space_amount") ?? -1;
            var used = ReadNumber(document.RootElement, "space_used") ?? 0;
            return new CloudStorageQuota(used, amount > 0 ? amount : null);
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    /// <summary>Box writes very large quotas in exponent notation now and then; read either form.</summary>
    private static long? ReadNumber(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetInt64(out var whole))
        {
            return whole;
        }

        return value.TryGetDouble(out var real) && real < long.MaxValue ? (long)real : long.MaxValue;
    }

    // MARK: - Share links

    /// <summary>
    /// Creates the public link on an existing item. <c>?fields=shared_link</c> is mandatory:
    /// without it Box answers 200 with the plain item and no link at all. A password is only
    /// accepted with <c>access: "open"</c> and must be at least 8 characters with a digit, an
    /// uppercase letter or a non-alphanumeric; <c>unshared_at</c> needs a paid plan. Box
    /// rejects both with its own explanation, passed through verbatim.
    /// </summary>
    public Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
        => PutSharedLinkAsync(path, options, cancellationToken);

    /// <summary>
    /// Box's sharing PUT is an upsert: the call that creates a link rewrites the settings of
    /// one that exists, and the URL is preserved — so updating is creating with new settings.
    /// </summary>
    public Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
        => PutSharedLinkAsync(path, options, cancellationToken);

    /// <summary>
    /// The item's current link. Box answers 200 with <c>shared_link: null</c> when the item
    /// isn't shared — the only "not shared" signal; a 404 means the item is gone and stays an
    /// error, as does a 403 from an admin-restricted account.
    /// </summary>
    public async Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        var (id, isFolder) = await ResolvePathToEntryAsync(path, cancellationToken).ConfigureAwait(false);
        var envelope = await ApiRequestAsync<BoxSharedLinkEnvelope>(
            HttpMethod.Get,
            $"/{Collection(isFolder)}/{id}",
            [("fields", "shared_link")],
            itemPath: path,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (envelope.SharedLink is not { } link)
        {
            return null;
        }

        // Nothing was requested, so the downgrade note is computed against the defaults: it
        // still reports an admin-narrowed scope or a preview-only link.
        return ShareLink(link, ShareLinkOptions.Default);
    }

    /// <summary>
    /// Withdraws the link by nulling <c>shared_link</c> — Box needs an explicit JSON null (an
    /// omitted key is a no-op). The field is read back to confirm the link really is gone
    /// rather than trusting a bare 200.
    /// </summary>
    public async Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        var (id, isFolder) = await ResolvePathToEntryAsync(path, cancellationToken).ConfigureAwait(false);
        var collection = Collection(isFolder);
        var envelope = await ApiRequestAsync<BoxSharedLinkEnvelope>(
            HttpMethod.Put,
            $"/{collection}/{id}",
            [("fields", "shared_link")],
            new JsonObject { ["shared_link"] = null },
            surfaceServerMessage: true,
            itemPath: path,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (envelope.SharedLink is not null)
        {
            SupportLogger.Shared.Log($"[Box] Unshare of /{collection}/{id} left a shared_link in place", LogCategory, LogLevel.Error);
            throw CloudProviderException.CommandFailed(
                Localization.L("Box kept the public link in place — check whether your administrator manages sharing for this item."));
        }
    }

    private async Task<CloudShareLink> PutSharedLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        var (id, isFolder) = await ResolvePathToEntryAsync(path, cancellationToken).ConfigureAwait(false);
        var link = new JsonObject { ["access"] = "open" };
        if (!string.IsNullOrEmpty(options.Password))
        {
            link["password"] = options.Password;
        }

        if (options.Expiry is { } expiry)
        {
            link["unshared_at"] = Timestamp(expiry);
        }

        // Preview stays on even when downloads are off: that's the point of a view-only link.
        link["permissions"] = new JsonObject { ["can_download"] = options.AllowDownload, ["can_preview"] = true };

        // Box explains a refusal (password policy, plan limits, admin restrictions) in its own
        // message — surfaced as-is.
        var envelope = await ApiRequestAsync<BoxSharedLinkEnvelope>(
            HttpMethod.Put,
            $"/{Collection(isFolder)}/{id}",
            [("fields", "shared_link")],
            new JsonObject { ["shared_link"] = link },
            surfaceServerMessage: true,
            itemPath: path,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (envelope.SharedLink is { } shared ? ShareLink(shared, options) : null)
            ?? throw CloudProviderException.InvalidResponse();
    }

    /// <summary>Box's <c>shared_link</c> as our type; null when it carries no usable URL.</summary>
    internal static CloudShareLink? ShareLink(BoxSharedLink link, ShareLinkOptions options)
    {
        if (link.Url is null || !Uri.TryCreate(link.Url, UriKind.Absolute, out var url))
        {
            return null;
        }

        Uri? download = null;
        if (link.DownloadUrl is not null)
        {
            Uri.TryCreate(link.DownloadUrl, UriKind.Absolute, out download);
        }

        return new CloudShareLink(url)
        {
            DirectDownloadUrl = download,
            ExpiresAt = BoxItem.ParseDate(link.UnsharedAt),
            HasPassword = link.IsPasswordEnabled ?? !string.IsNullOrEmpty(options.Password),
            IsPublic = string.Equals(link.EffectiveAccess ?? "open", "open", StringComparison.OrdinalIgnoreCase),
            Note = ShareDowngradeNote(link, options),
        };
    }

    /// <summary>
    /// Enterprise policy can silently downgrade what was asked for: an "open" link comes back
    /// scoped to "company" or "collaborators", downloads forced off. The request still
    /// succeeds, so reading the effective values back is the only way the user learns.
    /// </summary>
    private static string? ShareDowngradeNote(BoxSharedLink link, ShareLinkOptions options)
    {
        var notes = new List<string>();
        var access = link.EffectiveAccess?.ToLowerInvariant();
        if (access is not null && access != "open")
        {
            notes.Add(access == "company"
                ? Localization.L("Your Box administrator restricted this link to people in your company — it is not publicly accessible.")
                : Localization.L("Your Box administrator restricted this link to the file's collaborators — it is not publicly accessible."));
        }

        if (options.AllowDownload && string.Equals(link.EffectivePermission, "can_preview", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(Localization.L("Your Box administrator disabled downloads for shared links — recipients can only preview the file."));
        }

        return notes.Count == 0 ? null : string.Join(' ', notes);
    }

    // MARK: - File operations

    public async Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        var folderId = await ResolvePathToIdAsync(path, cancellationToken).ConfigureAwait(false);
        var entries = new List<BoxItem>();
        var offset = 0;
        while (true)
        {
            var page = await ApiRequestAsync<BoxFolderItems>(
                HttpMethod.Get,
                $"/folders/{folderId}/items",
                [
                    ("fields", ItemFields),
                    ("limit", PageLimit.ToString(CultureInfo.InvariantCulture)),
                    ("offset", offset.ToString(CultureInfo.InvariantCulture)),
                    ("sort", "name"),
                    ("direction", "ASC"),
                ],
                itemPath: path,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            entries.AddRange(page.Entries);
            if (page.Entries.Count < PageLimit)
            {
                break;
            }

            offset += page.Entries.Count;
            if (page.TotalCount is { } total && offset >= total)
            {
                break;
            }
        }

        foreach (var entry in entries)
        {
            _pathIdCache[CloudPath.Combine(path, entry.Name)] = entry.Id;
        }

        // The listing is the truth for this level: anything cached here that Box no longer
        // lists is gone, whoever deleted it.
        _pathIdCache.RetainPathChildren(path, entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal));
        return entries.Select(e => e.ToCloudFileItem(path)).ToList();
    }

    public async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var fileId = await ResolvePathToIdAsync(CloudPath.Normalize(remotePath), cancellationToken).ConfigureAwait(false);

        // Box answers with a 302 to dl.boxcloud.com, which HttpClient follows — and drops the
        // Authorization header on the way, which is right: the signed URL carries its own.
        using var response = await SendAuthorizedAsync(
            token =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiUrl}/files/{fileId}/content");
                request.Headers.Authorization = CloudHttp.Bearer(token);
                return request;
            },
            transfer: true,
            "GET /files/content",
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
            SupportLogger.Shared.Log($"[Box] Download failed: HTTP {(int)response.StatusCode}", LogCategory, LogLevel.Error);
            throw MapHttpError((int)response.StatusCode, body, remotePath);
        }

        await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        remotePath = CloudPath.Normalize(remotePath);
        var info = new FileInfo(localPath);
        var fileSize = info.Length;
        if (fileSize > MaxChunkedUploadBytes)
        {
            throw CloudProviderException.FileTooLarge(fileSize, MaxChunkedUploadBytes);
        }

        var fileName = CloudPath.Name(remotePath);
        var parentId = await ResolvePathToIdAsync(CloudPath.Parent(remotePath), cancellationToken).ConfigureAwait(false);
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var created = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);

        // A 409 retry sends the whole small file again; report each byte once.
        var progress = new BoxPartProgress(onBytes);

        // Small files go up in one request; big ones are streamed part by part.
        Task<BoxItem> Attempt(string? existingId) => fileSize <= SinglePutMaxBytes
            ? MultipartUploadAsync(localPath, fileName, parentId, existingId, modified, created, progress, cancellationToken)
            : ChunkedUploadAsync(localPath, fileSize, fileName, parentId, existingId, modified, created, onBytes, cancellationToken);

        // Box separates "new file" (POST /files/content) from "new version"
        // (POST /files/{id}/content). The cache says whether the file exists; when it doesn't
        // know, try a new upload and treat a 409 as "replace that file".
        _pathIdCache.TryGetValue(remotePath, out var cachedId);
        BoxItem uploaded;
        try
        {
            uploaded = await Attempt(cachedId).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.ServerError && e.StatusCode == 409)
        {
            string? foundId = null;
            try
            {
                foundId = await FindFileIdAsync(parentId, fileName, remotePath, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException)
            {
            }

            if (foundId is null)
            {
                throw;
            }

            uploaded = await Attempt(foundId).ConfigureAwait(false);
        }

        _pathIdCache[remotePath] = uploaded.Id;
    }

    private async Task<BoxItem> MultipartUploadAsync(
        string localPath,
        string fileName,
        string parentId,
        string? existingId,
        DateTimeOffset modified,
        DateTimeOffset created,
        BoxPartProgress progress,
        CancellationToken cancellationToken)
    {
        var url = existingId is null ? UploadUrl + "/files/content" : $"{UploadUrl}/files/{existingId}/content";
        var attributes = UploadAttributes(fileName, parentId, isNewVersion: existingId is not null, modified, created);
        using var response = await SendAuthorizedAsync(
            token =>
            {
                progress.BeginAttempt();
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new BoxMultipartContent(attributes, fileName, localPath, progress.Handler),
                };
                request.Headers.Authorization = CloudHttp.Bearer(token);
                request.Headers.Accept.ParseAdd("application/json");
                return request;
            },
            transfer: true,
            "POST /files/content",
            cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return Deserialize<BoxFolderItems>(body).Entries.FirstOrDefault() ?? throw CloudProviderException.InvalidResponse();
        }

        SupportLogger.Shared.Log($"[Box] Upload failed: HTTP {(int)response.StatusCode}: {Prefix(body, 500)}", LogCategory, LogLevel.Error);
        throw MapHttpError((int)response.StatusCode, body, null);
    }

    // MARK: - Chunked upload sessions

    /// <summary>
    /// Box's upload-session API: open a session, PUT the file part by part (each with its
    /// SHA-1 digest), then commit with the whole-file SHA-1. Each part streams from disk, so
    /// memory stays flat whatever the file size; it is read twice (hash, then send) because
    /// the digest header has to precede the bytes.
    /// </summary>
    private async Task<BoxItem> ChunkedUploadAsync(
        string localPath,
        long fileSize,
        string fileName,
        string parentId,
        string? existingId,
        DateTimeOffset modified,
        DateTimeOffset created,
        ByteProgress? onBytes,
        CancellationToken cancellationToken)
    {
        // 1. Open the session.
        var sessionBody = new JsonObject { ["file_size"] = fileSize, ["file_name"] = fileName };
        string sessionPath;
        if (existingId is not null)
        {
            sessionPath = $"/files/{existingId}/upload_sessions";
        }
        else
        {
            sessionPath = "/files/upload_sessions";
            sessionBody["folder_id"] = parentId;
        }

        var sessionJson = Serialize(sessionBody);
        var opened = await SendToUploadHostAsync(HttpMethod.Post, UploadUrl + sessionPath, () => JsonBody(sessionJson), null, transfer: false, cancellationToken).ConfigureAwait(false);
        if (!IsSuccess(opened.Status))
        {
            SupportLogger.Shared.Log($"[Box] Create upload session failed: HTTP {opened.Status}: {Prefix(opened.Body, 500)}", LogCategory, LogLevel.Error);
            if (opened.Status == 413 || opened.Body.Contains("file_size_limit_exceeded", StringComparison.Ordinal))
            {
                // The account's plan caps single-file size (e.g. 250 MB on free).
                throw CloudProviderException.FileTooLarge(fileSize, null);
            }

            throw MapHttpError(opened.Status, opened.Body, null);
        }

        var session = Deserialize<BoxUploadSession>(opened.Body);
        if (session.PartSize <= 0 ||
            !Uri.TryCreate(session.SessionEndpoints.UploadPart, UriKind.Absolute, out var partUrl) ||
            !Uri.TryCreate(session.SessionEndpoints.Commit, UriKind.Absolute, out var commitUrl))
        {
            throw CloudProviderException.InvalidResponse();
        }

        try
        {
            // 2. Stream the file up part by part, hashing as we go.
            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var parts = new JsonArray();
            long offset = 0;
            while (offset < fileSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var want = Math.Min(session.PartSize, fileSize - offset);
                var partDigest = await HashSliceAsync(localPath, offset, want, fileHash, cancellationToken).ConfigureAwait(false);
                var partOffset = offset;
                var progress = new BoxPartProgress(onBytes);
                var uploaded = await SendWithRetryAsync(
                    () => SendToUploadHostAsync(
                        HttpMethod.Put,
                        partUrl.AbsoluteUri,
                        () =>
                        {
                            progress.BeginAttempt();
                            var content = new ProgressStreamContent(new FileSliceStream(localPath, partOffset, want), progress.Handler);
                            content.Headers.ContentRange = new ContentRangeHeaderValue(partOffset, partOffset + want - 1, fileSize);
                            return content;
                        },
                        request => request.Headers.TryAddWithoutValidation("Digest", DigestHeader(partDigest)),
                        transfer: true,
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                if (!IsSuccess(uploaded.Status))
                {
                    SupportLogger.Shared.Log($"[Box] Upload part @{offset} failed: HTTP {uploaded.Status}: {Prefix(uploaded.Body, 500)}", LogCategory, LogLevel.Error);
                    throw MapHttpError(uploaded.Status, uploaded.Body, null);
                }

                var part = Deserialize<BoxUploadedPart>(uploaded.Body).Part;
                parts.Add(new JsonObject { ["part_id"] = part.PartId, ["offset"] = part.Offset, ["size"] = part.Size });
                offset += want;
            }

            // 3. Commit. Box may answer 202 + Retry-After while it assembles the parts; poll
            // the same endpoint until it returns the file.
            var commitBody = new JsonObject { ["parts"] = parts };
            var attributes = CommitAttributes(modified, created);
            if (attributes.Count > 0)
            {
                commitBody["attributes"] = attributes;
            }

            var commitJson = Serialize(commitBody);
            var fileDigest = fileHash.GetHashAndReset();
            for (var poll = 0; poll < 30; poll++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var committed = await SendWithRetryAsync(
                    () => SendToUploadHostAsync(
                        HttpMethod.Post,
                        commitUrl.AbsoluteUri,
                        () => JsonBody(commitJson),
                        request => request.Headers.TryAddWithoutValidation("Digest", DigestHeader(fileDigest)),
                        transfer: false,
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                if (committed.Status == 202)
                {
                    var wait = Math.Clamp(committed.RetryAfterSeconds ?? 1, 1, 10);
                    await Delay(TimeSpan.FromSeconds(wait), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!IsSuccess(committed.Status))
                {
                    SupportLogger.Shared.Log($"[Box] Commit upload session failed: HTTP {committed.Status}: {Prefix(committed.Body, 500)}", LogCategory, LogLevel.Error);
                    throw MapHttpError(committed.Status, committed.Body, null);
                }

                return Deserialize<BoxFolderItems>(committed.Body).Entries.FirstOrDefault() ?? throw CloudProviderException.InvalidResponse();
            }

            throw CloudProviderException.InvalidResponse();
        }
        catch
        {
            await AbortUploadSessionAsync(session.SessionEndpoints.Abort).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>SHA-1 of one part, also fed into the whole-file hash. Throws when the file shrank mid-upload.</summary>
    private static async Task<byte[]> HashSliceAsync(string localPath, long offset, long length, IncrementalHash fileHash, CancellationToken cancellationToken)
    {
        using var partHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        await using var slice = new FileSliceStream(localPath, offset, length);
        var buffer = new byte[HttpTransfer.BufferSize];
        long total = 0;
        int read;
        while ((read = await slice.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            partHash.AppendData(buffer, 0, read);
            fileHash.AppendData(buffer, 0, read);
            total += read;
        }

        if (total != length)
        {
            // File shrank or became unreadable mid-upload.
            throw CloudProviderException.InvalidResponse();
        }

        return partHash.GetHashAndReset();
    }

    private static string DigestHeader(byte[] sha1) => "sha=" + Convert.ToBase64String(sha1);

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying transient failures (network errors, 429,
    /// 5xx) up to four attempts with a doubling backoff from one second. Anything else is
    /// returned for the caller to map.
    /// </summary>
    private async Task<UploadHostResponse> SendWithRetryAsync(Func<Task<UploadHostResponse>> operation, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await operation().ConfigureAwait(false);
                if (attempt < 4 && (result.Status == 429 || result.Status is >= 500 and <= 599))
                {
                    SupportLogger.Shared.Log($"[Box] Upload request → HTTP {result.Status}, retrying (attempt {attempt})", LogCategory);
                }
                else
                {
                    return result;
                }
            }
            catch (CloudProviderException e) when (attempt < 4 && e.Kind == CloudErrorKind.NetworkError && !cancellationToken.IsCancellationRequested)
            {
                SupportLogger.Shared.Log($"[Box] Upload request failed ({e.InnerException?.Message}), retrying (attempt {attempt})", LogCategory);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Delay(delay, cancellationToken).ConfigureAwait(false);
            delay *= 2;
        }
    }

    private readonly record struct UploadHostResponse(int Status, string Body, double? RetryAfterSeconds);

    /// <summary>
    /// Authorized request to upload.box.com: refresh first, and once more on a 401 — a
    /// multi-gigabyte upload can outlive an access token.
    /// </summary>
    private async Task<UploadHostResponse> SendToUploadHostAsync(
        HttpMethod method,
        string url,
        Func<HttpContent> content,
        Action<HttpRequestMessage>? configure,
        bool transfer,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(
            token =>
            {
                var request = new HttpRequestMessage(method, url) { Content = content() };
                request.Headers.Authorization = CloudHttp.Bearer(token);
                request.Headers.Accept.ParseAdd("application/json");
                configure?.Invoke(request);
                return request;
            },
            transfer,
            $"{method} upload request",
            cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new UploadHostResponse((int)response.StatusCode, body, response.Headers.RetryAfter?.Delta?.TotalSeconds);
    }

    /// <summary>
    /// Best-effort cleanup so a failed or cancelled upload doesn't leave a half-assembled
    /// session behind. Deliberately not tied to the caller's token, which may be the reason
    /// we are here.
    /// </summary>
    private async Task AbortUploadSessionAsync(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var url))
        {
            return;
        }

        try
        {
            var credentials = await RefreshIfNeededAsync(CancellationToken.None).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            request.Headers.Authorization = CloudHttp.Bearer(credentials.AccessToken);
            using var response = await _http.SendApiAsync(request, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is CloudProviderException or HttpRequestException or OperationCanceledException)
        {
            SupportLogger.Shared.Log($"[Box] Aborting the upload session failed: {e.Message}", LogCategory, LogLevel.Notice);
        }
    }

    private static JsonObject CommitAttributes(DateTimeOffset modified, DateTimeOffset created) => new()
    {
        ["content_modified_at"] = Timestamp(modified),
        ["content_created_at"] = Timestamp(created),
    };

    /// <summary>
    /// The multipart <c>attributes</c> part. A new version carries no parent. The timestamps
    /// are how a copy keeps its dates: Box has no API to set them on an existing file.
    /// </summary>
    internal static string UploadAttributes(string name, string parentId, bool isNewVersion, DateTimeOffset modified, DateTimeOffset created)
    {
        var attributes = new JsonObject { ["name"] = name };
        if (!isNewVersion)
        {
            attributes["parent"] = new JsonObject { ["id"] = parentId };
        }

        attributes["content_modified_at"] = Timestamp(modified);
        attributes["content_created_at"] = Timestamp(created);
        return Serialize(attributes);
    }

    public async Task DeleteItemAsync(string path, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        var (id, isFolder) = await ResolvePathToEntryAsync(path, cancellationToken).ConfigureAwait(false);
        if (isFolder)
        {
            await ApiRequestVoidAsync(HttpMethod.Delete, $"/folders/{id}", [("recursive", "true")], path, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ApiRequestVoidAsync(HttpMethod.Delete, $"/files/{id}", null, path, cancellationToken).ConfigureAwait(false);
        }

        // The whole subtree went with it.
        _pathIdCache.RemovePathSubtree(path);
    }

    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);

        // Box rejects a duplicate folder name under the same parent with 409, so stop early
        // when the folder is already there.
        try
        {
            await ResolvePathToIdAsync(path, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (CloudProviderException)
        {
        }

        var parentId = await ResolvePathToIdAsync(CloudPath.Parent(path), cancellationToken).ConfigureAwait(false);
        var created = await ApiRequestAsync<BoxItem>(
            HttpMethod.Post,
            "/folders",
            body: new JsonObject { ["name"] = CloudPath.Name(path), ["parent"] = new JsonObject { ["id"] = parentId } },
            itemPath: path,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _pathIdCache[path] = created.Id;
    }

    public async Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        var (id, isFolder) = await ResolvePathToEntryAsync(path, cancellationToken).ConfigureAwait(false);
        await ApiRequestAsync<BoxItem>(
            HttpMethod.Put,
            $"/{Collection(isFolder)}/{id}",
            body: new JsonObject { ["name"] = newName },
            itemPath: path,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Every descendant's path changed with it.
        _pathIdCache.RemovePathSubtree(path);
        _pathIdCache[CloudPath.Combine(CloudPath.Parent(path), newName)] = id;
    }

    /// <summary>
    /// Server-side move: Box accepts a parent change on PUT /files/{id} and /folders/{id}. The
    /// name rides along, so a move-with-rename (the conflict resolver picked a new name) is
    /// one atomic update.
    /// </summary>
    public async Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        newPath = CloudPath.Normalize(newPath);
        var (id, isFolder) = await ResolvePathToEntryAsync(path, cancellationToken).ConfigureAwait(false);
        var newParentId = await ResolvePathToIdAsync(CloudPath.Parent(newPath), cancellationToken).ConfigureAwait(false);
        await ApiRequestAsync<BoxItem>(
            HttpMethod.Put,
            $"/{Collection(isFolder)}/{id}",
            body: ParentAndName(newParentId, CloudPath.Name(newPath)),
            itemPath: path,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _pathIdCache.RemovePathSubtree(path);
        _pathIdCache[newPath] = id;
    }

    /// <summary>Server-side copy; POST /folders/{id}/copy duplicates a whole tree on Box's side.</summary>
    public async Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        newPath = CloudPath.Normalize(newPath);
        var (id, isFolder) = await ResolvePathToEntryAsync(path, cancellationToken).ConfigureAwait(false);
        var newParentId = await ResolvePathToIdAsync(CloudPath.Parent(newPath), cancellationToken).ConfigureAwait(false);
        var copied = await ApiRequestAsync<BoxItem>(
            HttpMethod.Post,
            $"/{Collection(isFolder)}/{id}/copy",
            body: ParentAndName(newParentId, CloudPath.Name(newPath)),
            itemPath: path,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _pathIdCache[newPath] = copied.Id;
    }

    private static JsonObject ParentAndName(string parentId, string name)
    {
        var body = new JsonObject { ["parent"] = new JsonObject { ["id"] = parentId } };
        if (name.Length > 0)
        {
            body["name"] = name;
        }

        return body;
    }

    public async Task<CloudFileItem> GetMetadataAsync(string path, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        var (id, isFolder) = await ResolvePathToEntryAsync(path, cancellationToken).ConfigureAwait(false);
        var item = await ApiRequestAsync<BoxItem>(
            HttpMethod.Get,
            $"/{Collection(isFolder)}/{id}",
            [("fields", ItemFields)],
            itemPath: path,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return item.ToCloudFileItem(CloudPath.Parent(path));
    }

    /// <summary>
    /// GET /search, scoped with <c>ancestor_folder_ids</c> below the root. Hits come from
    /// anywhere under that folder, so their real location is read from <c>path_collection</c>.
    /// </summary>
    public async Task<IReadOnlyList<CloudFileItem>> SearchAsync(string query, string? path, CancellationToken cancellationToken)
    {
        var parameters = new List<(string, string)>
        {
            ("query", query),
            ("limit", "200"),
            ("fields", ItemFields + ",path_collection"),
        };
        if (path is not null && !CloudPath.IsRoot(path))
        {
            var folderId = await ResolvePathToIdAsync(CloudPath.Normalize(path), cancellationToken).ConfigureAwait(false);
            parameters.Add(("ancestor_folder_ids", folderId));
        }

        var response = await ApiRequestAsync<BoxFolderItems>(HttpMethod.Get, "/search", parameters, cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Entries.Select(e => e.ToCloudFileItem(e.SearchParentPath())).ToList();
    }

    // MARK: - Path resolution

    private async Task<string> ResolvePathToIdAsync(string path, CancellationToken cancellationToken)
    {
        if (_pathIdCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        return (await ResolvePathToEntryAsync(path, cancellationToken).ConfigureAwait(false)).Id;
    }

    /// <summary>
    /// Walks <paramref name="path"/> component by component, looking each child up by name in
    /// its parent. The cache can't say file or folder, so the last component is always
    /// re-fetched to learn its type; intermediate components must be folders.
    /// </summary>
    private async Task<(string Id, bool IsFolder)> ResolvePathToEntryAsync(string path, CancellationToken cancellationToken)
    {
        var components = CloudPath.Components(path);
        if (components.Length == 0)
        {
            return ("0", true);
        }

        var currentId = "0";
        var currentPath = string.Empty;
        for (var index = 0; index < components.Length; index++)
        {
            var name = components[index];
            currentPath += "/" + name;
            var isLast = index == components.Length - 1;
            if (!isLast && _pathIdCache.TryGetValue(currentPath, out var cached))
            {
                currentId = cached;
                continue;
            }

            var match = await FindEntryAsync(currentId, name, currentPath, cancellationToken).ConfigureAwait(false)
                ?? throw CloudProviderException.NotFound(currentPath);
            _pathIdCache[currentPath] = match.Id;
            if (isLast)
            {
                return match;
            }

            if (!match.IsFolder)
            {
                throw CloudProviderException.NotFound(currentPath);
            }

            currentId = match.Id;
        }

        return (currentId, true);
    }

    /// <summary>
    /// One child of <paramref name="parentId"/> by exact name. /folders/{id}/items has no name
    /// filter, so pages of up to 1000 are searched locally; one page is usually enough.
    /// </summary>
    private async Task<(string Id, bool IsFolder)?> FindEntryAsync(string parentId, string name, string path, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (true)
        {
            var page = await ApiRequestAsync<BoxFolderItems>(
                HttpMethod.Get,
                $"/folders/{parentId}/items",
                [
                    ("fields", "id,type,name"),
                    ("limit", PageLimit.ToString(CultureInfo.InvariantCulture)),
                    ("offset", offset.ToString(CultureInfo.InvariantCulture)),
                ],
                itemPath: path,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (page.Entries.FirstOrDefault(e => e.Name == name) is { } hit)
            {
                return (hit.Id, hit.IsFolder);
            }

            if (page.Entries.Count < PageLimit)
            {
                return null;
            }

            offset += page.Entries.Count;
            if (page.TotalCount is { } total && offset >= total)
            {
                return null;
            }
        }
    }

    /// <summary>A file (not a folder) by exact name — recovers a 409 on upload as a new version.</summary>
    private async Task<string?> FindFileIdAsync(string parentId, string name, string path, CancellationToken cancellationToken)
        => await FindEntryAsync(parentId, name, path, cancellationToken).ConfigureAwait(false) is { IsFolder: false } entry ? entry.Id : null;

    private static string Collection(bool isFolder) => isFolder ? "folders" : "files";

    // MARK: - HTTP helpers

    /// <summary>
    /// Sends an authorized request: refresh-if-stale first, and one forced refresh + retry on
    /// a 401 — which usually means Box invalidated the token server-side ahead of our expiry.
    /// <paramref name="build"/> runs again for the retry so upload content is re-created.
    /// </summary>
    private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<string, HttpRequestMessage> build, bool transfer, string logPath, CancellationToken cancellationToken)
    {
        var credentials = await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendOnceAsync(build(credentials.AccessToken), transfer, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        SupportLogger.Shared.Log($"[Box] {logPath} → 401, force-refreshing and retrying once", LogCategory);
        var fresh = await ForceRefreshAsync(cancellationToken).ConfigureAwait(false);
        return await SendOnceAsync(build(fresh.AccessToken), transfer, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, bool transfer, CancellationToken cancellationToken)
    {
        using (request)
        {
            return transfer
                ? await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false)
                : await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <param name="surfaceServerMessage">
    /// A failure carrying Box's <c>{"message": …}</c> becomes CommandFailed with Box's own
    /// wording instead of the coarse status mapping. Used by the sharing calls, where the real
    /// cause (admin policy, plan limits) is only explicable server-side.
    /// </param>
    private async Task<T> ApiRequestAsync<T>(
        HttpMethod method,
        string path,
        IReadOnlyList<(string Name, string Value)>? query = null,
        JsonNode? body = null,
        bool surfaceServerMessage = false,
        string? itemPath = null,
        CancellationToken cancellationToken = default)
    {
        var (status, text) = await SendApiRequestAsync(method, path, query, body, cancellationToken).ConfigureAwait(false);
        if (!IsSuccess(status))
        {
            SupportLogger.Shared.Log($"[Box] {method} {path} → HTTP {status}: {Prefix(text, 500)}", LogCategory, LogLevel.Error);
            if (surfaceServerMessage && ParseErrorMessage(text) is { Length: > 0 } message)
            {
                throw CloudProviderException.CommandFailed(message);
            }

            throw MapHttpError(status, text, itemPath);
        }

        return Deserialize<T>(text);
    }

    private async Task ApiRequestVoidAsync(HttpMethod method, string path, IReadOnlyList<(string Name, string Value)>? query, string? itemPath, CancellationToken cancellationToken)
    {
        var (status, text) = await SendApiRequestAsync(method, path, query, null, cancellationToken).ConfigureAwait(false);
        if (!IsSuccess(status))
        {
            SupportLogger.Shared.Log($"[Box] {method} {path} → HTTP {status}: {Prefix(text, 500)}", LogCategory, LogLevel.Error);
            throw MapHttpError(status, text, itemPath);
        }
    }

    private async Task<(int Status, string Body)> SendApiRequestAsync(
        HttpMethod method,
        string path,
        IReadOnlyList<(string Name, string Value)>? query,
        JsonNode? body,
        CancellationToken cancellationToken)
    {
        var url = ApiUrl + path;
        if (query is { Count: > 0 })
        {
            url += "?" + string.Join('&', query.Select(q => $"{CloudHttp.Escape(q.Name)}={CloudHttp.Escape(q.Value)}"));
        }

        var json = body is null ? null : Serialize(body);
        using var response = await SendAuthorizedAsync(
            token =>
            {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = CloudHttp.Bearer(token);
                request.Headers.Accept.ParseAdd("application/json");
                if (json is not null)
                {
                    request.Content = JsonBody(json);
                }

                return request;
            },
            transfer: false,
            $"{method} {path}",
            cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ((int)response.StatusCode, text);
    }

    private static bool IsSuccess(int status) => status is >= 200 and <= 299;

    private static StringContent JsonBody(string json)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static string Serialize(JsonNode node) => node.ToJsonString(WriteOptions);

    private static T Deserialize<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, CloudHttp.Json) ?? throw CloudProviderException.InvalidResponse();
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    private static string Prefix(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>Whole-second UTC RFC 3339, as Box's date fields expect.</summary>
    internal static string Timestamp(DateTimeOffset date)
        => date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // MARK: - Errors

    /// <summary>
    /// Box's status mapping. macOS maps 401 to notAuthenticated and 403 to unauthorized; both
    /// drive the re-auth banner there, and Unauthorized does here. A 404 names the item.
    /// </summary>
    internal static CloudProviderException MapHttpError(int status, string? body, string? itemPath) => status switch
    {
        401 or 403 => CloudProviderException.Unauthorized(),
        404 => CloudProviderException.NotFound(itemPath ?? ParseErrorMessage(body) ?? "Resource not found"),
        429 => CloudProviderException.RateLimited(),
        507 => CloudProviderException.QuotaExceeded(),
        _ => CloudProviderException.ServerError(status),
    };

    /// <summary>Box's OAuth error envelope: the human-readable <c>error_description</c>, else the <c>error</c> code.</summary>
    internal static string? ParseOAuthError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var description = StringProperty(document.RootElement, "error_description");
            return !string.IsNullOrEmpty(description) ? description : StringProperty(document.RootElement, "error");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Box's error message. For a rejected share setting the top-level message is just
    /// "Forbidden"; the useful part — which field it objected to — is in
    /// <c>context_info.errors</c>, appended as "message — name: reason; …".
    /// </summary>
    internal static string? ParseErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var message = StringProperty(root, "message");
            var code = StringProperty(root, "code");
            if (root.TryGetProperty("context_info", out var context) &&
                context.ValueKind == JsonValueKind.Object &&
                context.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array &&
                errors.GetArrayLength() > 0)
            {
                var details = string.Join("; ", errors.EnumerateArray()
                    .Select(entry => string.Join(": ", new[]
                    {
                        StringProperty(entry, "name"),
                        StringProperty(entry, "message") ?? StringProperty(entry, "reason"),
                    }.OfType<string>()))
                    .Where(detail => detail.Length > 0));
                var head = message ?? code ?? string.Empty;
                if (details.Length > 0)
                {
                    return head.Length == 0 ? details : $"{head} — {details}";
                }
            }

            return message ?? code;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringProperty(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Forwards byte progress for one part (or one small upload), but never reports the same bytes
/// twice when it is sent again after a retry.
/// </summary>
internal sealed class BoxPartProgress(ByteProgress? onBytes)
{
    private readonly Lock _gate = new();
    private long _high;
    private long _current;

    public ByteProgress? Handler => onBytes is null ? null : Report;

    public void BeginAttempt()
    {
        lock (_gate)
        {
            _current = 0;
        }
    }

    private void Report(long delta)
    {
        long forward;
        lock (_gate)
        {
            _current += delta;
            forward = Math.Max(0, _current - _high);
            _high = Math.Max(_high, _current);
        }

        if (forward > 0)
        {
            onBytes?.Invoke(forward);
        }
    }
}

/// <summary>
/// The multipart body for POST /files/content, streamed from disk. The <c>attributes</c> JSON
/// MUST come before the file part — Box answers 400 metadata_after_file_contents otherwise.
/// Progress counts the file's bytes only.
/// </summary>
internal sealed class BoxMultipartContent : HttpContent
{
    private readonly byte[] _head;
    private readonly byte[] _tail;
    private readonly string _localPath;
    private readonly long _fileLength;
    private readonly ByteProgress? _onBytes;

    public BoxMultipartContent(string attributesJson, string fileName, string localPath, ByteProgress? onBytes)
    {
        var boundary = "----FileFlussBoundary" + Guid.NewGuid().ToString("D").ToUpperInvariant();

        // Box takes the name from the attributes; the filename parameter only has to be a
        // well-formed header, so quotes and line breaks are neutralised.
        var safeName = fileName.Replace("\"", "%22", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
        _head = Encoding.UTF8.GetBytes(
            $"--{boundary}\r\n" +
            "Content-Disposition: form-data; name=\"attributes\"\r\n" +
            "Content-Type: application/json\r\n\r\n" +
            attributesJson +
            $"\r\n--{boundary}\r\n" +
            $"Content-Disposition: form-data; name=\"file\"; filename=\"{safeName}\"\r\n" +
            "Content-Type: application/octet-stream\r\n\r\n");
        _tail = Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n");
        _localPath = localPath;
        _fileLength = new FileInfo(localPath).Length;
        _onBytes = onBytes;
        Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(_head, cancellationToken).ConfigureAwait(false);
        await using (var file = HttpTransfer.OpenForUpload(_localPath))
        {
            var buffer = new byte[HttpTransfer.BufferSize];
            var remaining = _fileLength;
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
                _onBytes?.Invoke(read);
            }
        }

        await stream.WriteAsync(_tail, cancellationToken).ConfigureAwait(false);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        length = _head.Length + _fileLength + _tail.Length;
        return true;
    }
}
