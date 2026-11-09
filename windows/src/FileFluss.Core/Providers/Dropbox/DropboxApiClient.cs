// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.Dropbox;

/// <summary>
/// Dropbox API v2 (port of the macOS <c>DropboxAPIClient</c>). Path-based: the root is ""
/// on the wire. RPC calls go to api.dropboxapi.com with a JSON body; content calls go to
/// content.dropboxapi.com with their argument JSON in the <c>Dropbox-API-Arg</c> header.
/// Thread-safe: one instance serves panels, transfers and the indexer concurrently.
/// </summary>
internal sealed class DropboxApiClient
{
    // The developer's registered Dropbox app (PKCE flow; the secret is sent as macOS does).
    internal const string AppKey = "b5v4zgbnycbuimj";
    internal const string AppSecret = "xcvb8frfc2jyzvj";

    /// <summary>
    /// Space-separated scopes requested at authorize time. All must be enabled in the Dropbox
    /// app console — requesting one the app can't grant fails the whole authorization.
    /// <c>account_info.read</c> covers quota and the account name; <c>files.metadata.read</c>
    /// listing/search/metadata; <c>files.content.read</c> downloads;
    /// <c>files.content.write</c> upload/delete/move/copy/create_folder;
    /// <c>sharing.write</c> creating public links and <c>sharing.read</c> looking up one that
    /// exists (the console grants the two together). Deliberately not
    /// <c>files.metadata.write</c> or <c>account_info.write</c>.
    ///
    /// Accounts linked before <c>sharing.write</c> joined the list hold a token without it:
    /// sharing then fails with 401 + <c>missing_scope</c>, mapped to NotAuthenticated so the
    /// panel's re-auth banner asks for a new sign-in that picks the scope up.
    /// </summary>
    internal const string OAuthScopes = "account_info.read files.metadata.read files.content.read files.content.write sharing.write sharing.read";

    internal const string AuthorizeUrl = "https://www.dropbox.com/oauth2/authorize";
    internal const string TokenUrl = "https://api.dropboxapi.com/oauth2/token";
    internal const string RpcUrl = "https://api.dropboxapi.com/2";
    internal const string ContentUrl = "https://content.dropboxapi.com/2";

    private const string LogCategory = "Dropbox";

    /// <summary>Request bodies: unescaped UTF-8 for readability; nulls are written only when put in explicitly.</summary>
    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http;
    private readonly Action<DropboxCredentials> _credentialsChanged;
    private readonly Lock _gate = new();
    private DropboxCredentials _credentials;

    /// <summary>
    /// The refresh in flight, if any. Concurrent callers await this one task rather than
    /// firing parallel POSTs to /oauth2/token: Dropbox rotates refresh tokens, so a parallel
    /// refresh races and one caller comes back with invalid_grant → signed out.
    /// </summary>
    private Task<DropboxCredentials>? _inflightRefresh;

    /// <param name="credentialsChanged">Called with every refreshed or renamed credential set, so it is persisted at once.</param>
    public DropboxApiClient(HttpClient http, DropboxCredentials credentials, Action<DropboxCredentials> credentialsChanged)
    {
        _http = http;
        _credentials = credentials;
        _credentialsChanged = credentialsChanged;
    }

    /// <summary>Files up to this size go up in one /files/upload call (Dropbox's limit is 150 MB); larger ones use an upload session.</summary>
    internal long SimpleUploadLimit { get; set; } = 150_000_000;

    /// <summary>Bytes per upload-session append (macOS: 150 MB).</summary>
    internal long SessionChunkSize { get; set; } = 150_000_000;

    public DropboxCredentials Credentials
    {
        get
        {
            lock (_gate)
            {
                return _credentials;
            }
        }
    }

    // MARK: - OAuth2 (PKCE)

    public static Uri BuildAuthorizeUri(string redirectUri, string codeChallenge, string state)
    {
        var query = new (string, string)[]
        {
            ("client_id", AppKey),
            ("redirect_uri", redirectUri),
            ("response_type", "code"),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"),
            ("token_access_type", "offline"),

            // Explicit scopes make the grant deterministic rather than depending on the
            // app-console default set. account_info.read is what powers the quota and the
            // account name — without it the quota silently fails.
            ("scope", OAuthScopes),
            ("state", state),
        };
        return new Uri(AuthorizeUrl + "?" + string.Join('&', query.Select(q => $"{q.Item1}={CloudHttp.Escape(q.Item2)}")));
    }

    public static async Task<DropboxCredentials> ExchangeCodeAsync(HttpClient http, string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = CloudHttp.FormContent(
                ("code", code),
                ("grant_type", "authorization_code"),
                ("client_id", AppKey),
                ("client_secret", AppSecret),
                ("redirect_uri", redirectUri),
                ("code_verifier", codeVerifier)),
        };

        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // A failed exchange carries no token, so its body is safe to log.
            var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
            SupportLogger.Shared.Log($"[Dropbox] Token exchange failed: HTTP {(int)response.StatusCode}: {body}", LogCategory, LogLevel.Error);
            throw CloudProviderException.InvalidCredentials();
        }

        // The body of a successful exchange IS the access and refresh token — never log it.
        SupportLogger.Shared.Log($"[Dropbox] Token exchange returned HTTP {(int)response.StatusCode}", LogCategory);
        var token = await response.ReadJsonAsync<DropboxTokenResponse>(cancellationToken).ConfigureAwait(false);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);

        // Soft lookup: a hiccup in the name lookup must not stop the account being added.
        (string AccountId, string DisplayName) user;
        try
        {
            user = await FetchCurrentAccountAsync(http, token.AccessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException)
        {
            user = ("unknown", "Unknown");
        }

        return new DropboxCredentials(token.AccessToken, token.RefreshToken ?? string.Empty, expiresAt, user.AccountId, user.DisplayName);
    }

    /// <summary>
    /// /users/get_current_account. Throws on any failure, so the launch-time name refresh can
    /// tell "lookup failed" (keep the old name) from a fresh value.
    /// </summary>
    public static async Task<(string AccountId, string DisplayName)> FetchCurrentAccountAsync(HttpClient http, string accessToken, CancellationToken cancellationToken)
    {
        // RPC endpoints want a null body with Content-Type application/json. Without the header
        // Dropbox answers 400 "Bad HTTP Content-Type header", which left the account name stuck
        // at "Unknown" on macOS.
        using var request = new HttpRequestMessage(HttpMethod.Post, RpcUrl + "/users/get_current_account") { Content = JsonBody("null") };
        request.Headers.Authorization = CloudHttp.Bearer(accessToken);
        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CloudProviderException.ServerError((int)response.StatusCode);
        }

        var account = await response.ReadJsonAsync<DropboxAccountInfo>(cancellationToken).ConfigureAwait(false);
        return (account.AccountId, account.Name.DisplayName);
    }

    /// <summary>
    /// Re-fetches the live account name. Returns the updated credentials (already persisted
    /// through the change callback), or null when nothing changed. Lets accounts linked
    /// before the Content-Type fix heal a stuck "Unknown" without a manual re-link.
    /// </summary>
    public async Task<DropboxCredentials?> RefreshDisplayNameAsync(CancellationToken cancellationToken)
    {
        var current = await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
        var (accountId, displayName) = await FetchCurrentAccountAsync(_http, current.AccessToken, cancellationToken).ConfigureAwait(false);
        var trimmed = displayName.Trim();
        DropboxCredentials updated;
        lock (_gate)
        {
            if (trimmed.Length == 0 || trimmed == "Unknown" || trimmed == _credentials.DisplayName)
            {
                return null;
            }

            updated = _credentials with
            {
                AccountId = accountId.Length == 0 ? _credentials.AccountId : accountId,
                DisplayName = trimmed,
            };
            _credentials = updated;
        }

        _credentialsChanged(updated);
        return updated;
    }

    // MARK: - Token refresh

    /// <summary>The current credentials, refreshed first when they expire within a minute.</summary>
    public Task<DropboxCredentials> RefreshIfNeededAsync(CancellationToken cancellationToken)
    {
        Task<DropboxCredentials> task;
        lock (_gate)
        {
            if (_inflightRefresh is null && DateTimeOffset.UtcNow < _credentials.ExpiresAt.AddSeconds(-60))
            {
                return Task.FromResult(_credentials);
            }

            task = _inflightRefresh ?? StartRefreshLocked();
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Refreshes regardless of the cached expiry. Used after a 401: servers can invalidate an
    /// access token before our local expiry says so.
    /// </summary>
    public Task<DropboxCredentials> ForceRefreshAsync(CancellationToken cancellationToken)
    {
        Task<DropboxCredentials> task;
        lock (_gate)
        {
            task = _inflightRefresh ?? StartRefreshLocked();
        }

        return task.WaitAsync(cancellationToken);
    }

    private Task<DropboxCredentials> StartRefreshLocked()
    {
        var refreshToken = _credentials.RefreshToken;
        if (string.IsNullOrEmpty(refreshToken))
        {
            return Task.FromException<DropboxCredentials>(CloudProviderException.NotAuthenticated());
        }

        // Task.Run so the refresh never completes (and clears _inflightRefresh) before it was
        // published under the lock. Not tied to any one caller's token: others await it too.
        var task = Task.Run(() => RunRefreshAsync(refreshToken));
        _inflightRefresh = task;
        return task;
    }

    private async Task<DropboxCredentials> RunRefreshAsync(string refreshToken)
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

    private async Task<DropboxCredentials> PerformTokenRefreshAsync(string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = CloudHttp.FormContent(
                ("grant_type", "refresh_token"),
                ("refresh_token", refreshToken),
                ("client_id", AppKey),
                ("client_secret", AppSecret)),
        };

        using var response = await _http.SendApiAsync(request, CancellationToken.None).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"[Dropbox] Token refresh failed: HTTP {(int)response.StatusCode}", LogCategory, LogLevel.Error);
            throw CloudProviderException.TokenRefreshFailed((int)response.StatusCode);
        }

        var token = await response.ReadJsonAsync<DropboxTokenResponse>(CancellationToken.None).ConfigureAwait(false);
        DropboxCredentials updated;
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

        _credentialsChanged(updated);
        return updated;
    }

    // MARK: - Quota

    /// <summary>
    /// /users/get_space_usage. <c>allocated == 0</c> happens on Business plans without an
    /// enforced quota — "no total" rather than "0 bytes total", so the status bar shows just
    /// the used figure. A failure is logged with Dropbox's reason (usually a token without
    /// <c>account_info.read</c>: 401 + missing_scope) and reported as "unknown".
    /// </summary>
    public async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken)
    {
        var credentials = await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);

        // get_space_usage requires a null body and the JSON Content-Type; without the header
        // Dropbox 400s, which is why the quota never appeared on macOS.
        using var request = new HttpRequestMessage(HttpMethod.Post, RpcUrl + "/users/get_space_usage") { Content = JsonBody("null") };
        request.Headers.Authorization = CloudHttp.Bearer(credentials.AccessToken);
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.ReadBodySnippetAsync(cancellationToken, 300).ConfigureAwait(false);
            SupportLogger.Shared.Log($"[Dropbox] get_space_usage failed (HTTP {(int)response.StatusCode}): {body}", LogCategory, LogLevel.Error);
            return null;
        }

        var usage = await response.ReadJsonAsync<DropboxSpaceUsage>(cancellationToken).ConfigureAwait(false);
        var total = usage.Allocation.Allocated is > 0 ? usage.Allocation.Allocated : null;
        return new CloudStorageQuota(usage.Used, total);
    }

    // MARK: - File operations

    /// <summary>Dropbox addresses the root as "" (empty), everything else by its POSIX path.</summary>
    internal static string DropboxPath(string path)
    {
        var normalized = CloudPath.Normalize(path);
        return normalized == "/" ? string.Empty : normalized;
    }

    public async Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        var entries = new List<DropboxEntry>();
        var page = await RpcAsync<DropboxListFolderResponse>(
            "/files/list_folder",
            new JsonObject
            {
                ["path"] = DropboxPath(path),
                ["recursive"] = false,
                ["include_deleted"] = false,
                ["limit"] = 2000,
            },
            path,
            cancellationToken).ConfigureAwait(false);
        entries.AddRange(page.Entries);

        var guard = new PageGuard("Dropbox list_folder");
        while (page.HasMore)
        {
            guard.Follow(page.Cursor);
            page = await RpcAsync<DropboxListFolderResponse>(
                "/files/list_folder/continue",
                new JsonObject { ["cursor"] = page.Cursor },
                path,
                cancellationToken).ConfigureAwait(false);
            entries.AddRange(page.Entries);
        }

        return entries.Select(e => e.ToCloudFileItem()).OfType<CloudFileItem>().ToList();
    }

    public async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var arg = ApiArg(new JsonObject { ["path"] = DropboxPath(remotePath) });
        using var response = await SendAuthorizedAsync(
            token =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, ContentUrl + "/files/download");
                request.Headers.Authorization = CloudHttp.Bearer(token);
                request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", arg);
                return request;
            },
            transfer: true,
            "/files/download",
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
            SupportLogger.Shared.Log($"[Dropbox] Download failed: HTTP {(int)response.StatusCode}: {body}", LogCategory, LogLevel.Error);
            throw MapHttpError((int)response.StatusCode, body, remotePath);
        }

        await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var info = new FileInfo(localPath);
        var size = info.Length;

        // client_modified keeps the source's date on Dropbox, as macOS does.
        var clientModified = DateString(new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        if (size <= SimpleUploadLimit)
        {
            await SimpleUploadAsync(localPath, DropboxPath(remotePath), clientModified, onBytes, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SessionUploadAsync(localPath, size, DropboxPath(remotePath), clientModified, onBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static JsonObject CommitInfo(string path, string clientModified) => new()
    {
        ["path"] = path,

        // "add" without autorename: an existing file is a conflict, never a silent duplicate.
        ["mode"] = "add",
        ["autorename"] = false,
        ["mute"] = false,
        ["client_modified"] = clientModified,
    };

    private async Task SimpleUploadAsync(string localPath, string path, string clientModified, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var arg = ApiArg(CommitInfo(path, clientModified));

        // A 401 retry sends the body again; report each byte once.
        var progress = new HighWaterProgress(onBytes);
        using var response = await SendAuthorizedAsync(
            token =>
            {
                progress.BeginAttempt();
                var request = new HttpRequestMessage(HttpMethod.Post, ContentUrl + "/files/upload")
                {
                    Content = ProgressStreamContent.FromFile(localPath, progress.Handler),
                };
                request.Headers.Authorization = CloudHttp.Bearer(token);
                request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", arg);
                return request;
            },
            transfer: true,
            "/files/upload",
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
            SupportLogger.Shared.Log($"[Dropbox] Upload failed: HTTP {(int)response.StatusCode}: {body}", LogCategory, LogLevel.Error);
            throw MapHttpError((int)response.StatusCode, body, path);
        }
    }

    /// <summary>
    /// upload_session/start (empty), append_v2 for every chunk but the last, and finish with
    /// the last chunk and the commit info. Each chunk streams from disk.
    /// </summary>
    private async Task SessionUploadAsync(string localPath, long fileSize, string path, string clientModified, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        string sessionId;
        {
            var startArg = ApiArg(new JsonObject { ["close"] = false });
            var credentials = await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, ContentUrl + "/files/upload_session/start")
            {
                Content = new ByteArrayContent([]) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
            };
            request.Headers.Authorization = CloudHttp.Bearer(credentials.AccessToken);
            request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", startArg);
            using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw MapHttpError((int)response.StatusCode, body, path);
            }

            sessionId = Deserialize<DropboxUploadSessionStart>(body).SessionId;
        }

        long offset = 0;
        while (offset < fileSize)
        {
            var end = Math.Min(offset + SessionChunkSize, fileSize);
            var isLast = end >= fileSize;
            var cursor = new JsonObject { ["session_id"] = sessionId, ["offset"] = offset };
            var (endpoint, arg) = isLast
                ? ("/files/upload_session/finish", ApiArg(new JsonObject { ["cursor"] = cursor, ["commit"] = CommitInfo(path, clientModified) }))
                : ("/files/upload_session/append_v2", ApiArg(new JsonObject { ["cursor"] = cursor, ["close"] = false }));

            var credentials = await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, ContentUrl + endpoint)
            {
                Content = new ProgressStreamContent(new FileSliceStream(localPath, offset, end - offset), onBytes),
            };
            request.Headers.Authorization = CloudHttp.Bearer(credentials.AccessToken);
            request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", arg);
            using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
                SupportLogger.Shared.Log($"[Dropbox] {endpoint} @{offset} failed: HTTP {(int)response.StatusCode}: {body}", LogCategory, LogLevel.Error);
                throw MapHttpError((int)response.StatusCode, body, path);
            }

            offset = end;
        }
    }

    public Task DeleteItemAsync(string path, CancellationToken cancellationToken)
        => RpcVoidAsync("/files/delete_v2", new JsonObject { ["path"] = DropboxPath(path) }, path, cancellationToken);

    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await GetMetadataAsync(path, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (CloudProviderException)
        {
            // Not there (or the lookup failed) — create it; a real problem resurfaces below.
        }

        await RpcVoidAsync(
            "/files/create_folder_v2",
            new JsonObject { ["path"] = DropboxPath(path), ["autorename"] = false },
            path,
            cancellationToken).ConfigureAwait(false);
    }

    public Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken)
        => MoveItemAsync(path, CloudPath.Combine(CloudPath.Parent(path), newName), cancellationToken);

    public Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken)
        => RpcVoidAsync(
            "/files/move_v2",
            new JsonObject { ["from_path"] = DropboxPath(path), ["to_path"] = DropboxPath(newPath), ["autorename"] = false },
            path,
            cancellationToken);

    public Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken)
        => RpcVoidAsync(
            "/files/copy_v2",
            new JsonObject { ["from_path"] = DropboxPath(path), ["to_path"] = DropboxPath(newPath), ["autorename"] = false },
            path,
            cancellationToken);

    public async Task<CloudFileItem> GetMetadataAsync(string path, CancellationToken cancellationToken)
    {
        var entry = await RpcAsync<DropboxEntry>("/files/get_metadata", new JsonObject { ["path"] = DropboxPath(path) }, path, cancellationToken).ConfigureAwait(false);
        return entry.ToCloudFileItem() ?? throw CloudProviderException.InvalidResponse();
    }

    public async Task<IReadOnlyList<CloudFileItem>> SearchAsync(string query, string? path, CancellationToken cancellationToken)
    {
        var options = new JsonObject();
        if (path is not null)
        {
            options["path"] = DropboxPath(path);
        }

        options["max_results"] = 100;
        var response = await RpcAsync<DropboxSearchResponse>(
            "/files/search_v2",
            new JsonObject { ["query"] = query, ["options"] = options },
            path,
            cancellationToken).ConfigureAwait(false);
        return response.Matches.Select(m => m.Metadata.Metadata.ToCloudFileItem()).OfType<CloudFileItem>().ToList();
    }

    // MARK: - Share links

    /// <summary>
    /// Creates a public link for <paramref name="path"/> — or hands back the one that already
    /// exists. Dropbox reports "already shared" as an error (<c>shared_link_already_exists</c>)
    /// whose body normally carries the existing link; when it doesn't, /sharing/list_shared_links
    /// is asked. Password and expiry are Professional/Business features: a free account gets a
    /// <c>settings_error</c>, whose <c>error_summary</c> is surfaced verbatim — only Dropbox can
    /// say which plan the user would need.
    /// </summary>
    public async Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        var dbPath = DropboxPath(path);
        var password = string.IsNullOrEmpty(options.Password) ? null : options.Password;
        var settings = new JsonObject();
        if (password is not null)
        {
            // Omitted rather than sent as false: on a free account any password field at all
            // turns the call into a settings_error.
            settings["require_password"] = true;
            settings["link_password"] = password;
        }

        if (options.Expiry is { } expiry)
        {
            settings["expires"] = Timestamp(expiry);
        }

        settings["allow_download"] = options.AllowDownload;

        // "viewer" is view+comment. The edit levels need a shared *folder*, which a public file link isn't.
        settings["access"] = "viewer";

        var (status, body) = await RpcRawAsync(
            "/sharing/create_shared_link_with_settings",
            Serialize(new JsonObject { ["path"] = dbPath, ["settings"] = settings }),
            cancellationToken).ConfigureAwait(false);
        if (IsSuccess(status))
        {
            return ShareLink(Deserialize<DropboxSharedLinkMetadata>(body), requestedPassword: password is not null);
        }

        // A token minted before sharing.write was requested: no refresh can fix it, the user
        // has to sign in again — NotAuthenticated drives the re-auth banner.
        if (IsMissingScope(status, body))
        {
            SupportLogger.Shared.Log("[Dropbox] create_shared_link_with_settings rejected: token lacks the sharing.write scope", LogCategory, LogLevel.Error);
            throw CloudProviderException.NotAuthenticated();
        }

        var envelope = TryDeserialize<DropboxShareErrorEnvelope>(body);

        // Already shared: the error payload *is* the link we wanted.
        if (envelope?.Error?.SharedLinkAlreadyExists is { } alreadyExists)
        {
            if (alreadyExists.Metadata is { } metadata)
            {
                return ShareLink(metadata, requestedPassword: false, ExistingLinkNote(options));
            }

            // ".tag": "unverified" carries no metadata: the link exists, we just weren't told
            // which. Best-effort lookup — on a token predating sharing.read it fails, and that
            // must not replace the real situation ("already shared") with a scope error.
            DropboxSharedLinkMetadata? existing = null;
            try
            {
                existing = await FirstDirectSharedLinkAsync(dbPath, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException)
            {
            }

            if (existing is not null)
            {
                return ShareLink(existing, requestedPassword: false, ExistingLinkNote(options));
            }

            throw CloudProviderException.CommandFailed(
                Localization.L("This file already has a public link. Open it in Dropbox to copy that link."));
        }

        throw ShareFailure(status, body, envelope, "create_shared_link_with_settings", path);
    }

    /// <summary>
    /// The link this path already has, or null. <c>direct_only</c> drops links inherited from
    /// a shared parent folder; an unshared file is an empty <c>links</c> array, not an error.
    /// A token without <c>sharing.read</c> is NotAuthenticated, as in creation.
    /// </summary>
    public async Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        var (status, body) = await RpcRawAsync(
            "/sharing/list_shared_links",
            Serialize(new JsonObject { ["path"] = DropboxPath(path), ["direct_only"] = true }),
            cancellationToken).ConfigureAwait(false);
        if (!IsSuccess(status))
        {
            throw ShareFailure(status, body, TryDeserialize<DropboxShareErrorEnvelope>(body), "list_shared_links", path);
        }

        var first = Deserialize<DropboxListSharedLinksResponse>(body).Links.FirstOrDefault();
        return first is null ? null : ShareLink(first, requestedPassword: false);
    }

    /// <summary>
    /// Changes the existing link's settings in place, keeping its URL. The endpoint is
    /// addressed by URL, so the current link is looked up first.
    /// </summary>
    public async Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        var existing = await ExistingShareLinkAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw CloudProviderException.CommandFailed(Localization.L("This file has no public link to update. Create one first."));

        var password = string.IsNullOrEmpty(options.Password) ? null : options.Password;
        var settings = new JsonObject
        {
            // An explicit false here, unlike creation: false takes an existing password off again.
            ["require_password"] = password is not null,
        };
        if (password is not null)
        {
            settings["link_password"] = password;
        }

        if (options.Expiry is { } expiry)
        {
            settings["expires"] = Timestamp(expiry);
        }

        settings["allow_download"] = options.AllowDownload;
        settings["access"] = "viewer";

        var (status, body) = await RpcRawAsync(
            "/sharing/modify_shared_link_settings",
            Serialize(new JsonObject
            {
                ["url"] = existing.Url.OriginalString,
                ["settings"] = settings,

                // The only way to clear an expiry: "expires" can set or move it, never remove it.
                ["remove_expiration"] = options.Expiry is null,
            }),
            cancellationToken).ConfigureAwait(false);
        if (!IsSuccess(status))
        {
            throw ShareFailure(status, body, TryDeserialize<DropboxShareErrorEnvelope>(body), "modify_shared_link_settings", path);
        }

        return ShareLink(Deserialize<DropboxSharedLinkMetadata>(body), requestedPassword: password is not null);
    }

    /// <summary>Withdraws the link. Not an error when there is none: "this file is not shared" already holds.</summary>
    public async Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        var existing = await ExistingShareLinkAsync(path, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            SupportLogger.Shared.Log("[Dropbox] removeShareLink: no public link on that path, nothing to revoke", LogCategory);
            return;
        }

        var (status, body) = await RpcRawAsync(
            "/sharing/revoke_shared_link",
            Serialize(new JsonObject { ["url"] = existing.Url.OriginalString }),
            cancellationToken).ConfigureAwait(false);
        if (!IsSuccess(status))
        {
            throw ShareFailure(status, body, TryDeserialize<DropboxShareErrorEnvelope>(body), "revoke_shared_link", path);
        }
    }

    private async Task<DropboxSharedLinkMetadata?> FirstDirectSharedLinkAsync(string dbPath, CancellationToken cancellationToken)
    {
        var response = await RpcAsync<DropboxListSharedLinksResponse>(
            "/sharing/list_shared_links",
            new JsonObject { ["path"] = dbPath, ["direct_only"] = true },
            dbPath,
            cancellationToken).ConfigureAwait(false);
        return response.Links.FirstOrDefault();
    }

    /// <summary>
    /// The best error a failed /sharing/* response yields: a missing scope first (re-linking
    /// is the only fix), then Dropbox's own error_summary, then the plain-text explanation a
    /// malformed request comes back with instead of the JSON envelope.
    /// </summary>
    private static CloudProviderException ShareFailure(int status, string body, DropboxShareErrorEnvelope? envelope, string endpoint, string path)
    {
        if (IsMissingScope(status, body))
        {
            SupportLogger.Shared.Log($"[Dropbox] {endpoint} rejected: token lacks the required sharing scope", LogCategory, LogLevel.Error);
            return CloudProviderException.NotAuthenticated();
        }

        if (!string.IsNullOrEmpty(envelope?.ErrorSummary))
        {
            return CloudProviderException.CommandFailed(envelope.ErrorSummary);
        }

        var text = body.Trim();
        if (text.Length > 0)
        {
            return CloudProviderException.CommandFailed(text.Length <= 300 ? text : text[..300]);
        }

        return MapHttpError(status, body, path);
    }

    private static CloudShareLink ShareLink(DropboxSharedLinkMetadata metadata, bool requestedPassword, string? note = null)
    {
        if (!Uri.TryCreate(metadata.Url, UriKind.Absolute, out var url))
        {
            throw CloudProviderException.InvalidResponse();
        }

        // Honour what the server reports, falling back to what we asked for when it leaves a field out.
        var allowsDownload = metadata.Permissions?.AllowDownload ?? true;
        return new CloudShareLink(url)
        {
            DirectDownloadUrl = allowsDownload ? DirectDownloadUrl(metadata.Url) : null,
            ExpiresAt = DropboxEntry.ParseDate(metadata.Expires),
            HasPassword = metadata.Permissions?.RequirePassword ?? requestedPassword,
            Note = note,
        };
    }

    /// <summary>
    /// Dropbox hands back a preview link ending in <c>?dl=0</c>; <c>dl=1</c> streams the bytes.
    /// Rebuilding the query also covers a link that already has <c>dl=1</c> or none at all.
    /// </summary>
    internal static Uri? DirectDownloadUrl(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var builder = new UriBuilder(uri);
        var parts = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();
        var index = parts.FindIndex(p => p == "dl" || p.StartsWith("dl=", StringComparison.Ordinal));
        if (index >= 0)
        {
            parts[index] = "dl=1";
        }
        else
        {
            parts.Add("dl=1");
        }

        builder.Query = string.Join('&', parts);
        return builder.Uri;
    }

    /// <summary>
    /// The already-exists path returns the link with its own settings — nothing asked for was
    /// applied, so say so when the user actually asked for something.
    /// </summary>
    private static string? ExistingLinkNote(ShareLinkOptions options)
    {
        var askedForSettings = !string.IsNullOrEmpty(options.Password) || options.Expiry is not null || !options.AllowDownload;
        return askedForSettings
            ? Localization.L("This file already had a public link, so the requested settings were not applied.")
            : null;
    }

    /// <summary>
    /// Dropbox answers a scope-less token with 401 and a <c>missing_scope</c> tag. Matched on
    /// the raw body — the 401 envelope is plain text as often as JSON.
    /// </summary>
    private static bool IsMissingScope(int status, string body)
        => status == 401 && body.Contains("missing_scope", StringComparison.Ordinal);

    // MARK: - RPC helpers

    /// <summary>
    /// Sends an authorized request: refresh-if-stale first, and one forced refresh + retry on
    /// a 401. <paramref name="build"/> is called again for the retry, so upload content is
    /// re-created rather than re-sent from a consumed stream.
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
        SupportLogger.Shared.Log($"[Dropbox] Got 401 on {logPath}, refreshing token and retrying", LogCategory);
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

    /// <summary>
    /// An RPC POST whose response comes back as-is, error statuses included. The sharing calls
    /// need the raw body: Dropbox reports "link already exists" as an error carrying the link.
    /// </summary>
    private async Task<(int Status, string Body)> RpcRawAsync(string endpoint, string json, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(
            token =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, RpcUrl + endpoint) { Content = JsonBody(json) };
                request.Headers.Authorization = CloudHttp.Bearer(token);
                return request;
            },
            transfer: false,
            endpoint,
            cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (!IsSuccess(status))
        {
            SupportLogger.Shared.Log($"[Dropbox] POST {endpoint} → HTTP {status}: {(body.Length <= 1000 ? body : body[..1000])}", LogCategory, LogLevel.Error);
        }

        return (status, body);
    }

    private async Task<T> RpcAsync<T>(string endpoint, JsonObject body, string? itemPath, CancellationToken cancellationToken)
    {
        var (status, text) = await RpcRawAsync(endpoint, Serialize(body), cancellationToken).ConfigureAwait(false);
        if (!IsSuccess(status))
        {
            throw MapHttpError(status, text, itemPath);
        }

        return Deserialize<T>(text);
    }

    private async Task RpcVoidAsync(string endpoint, JsonObject body, string? itemPath, CancellationToken cancellationToken)
    {
        var (status, text) = await RpcRawAsync(endpoint, Serialize(body), cancellationToken).ConfigureAwait(false);
        if (!IsSuccess(status))
        {
            throw MapHttpError(status, text, itemPath);
        }
    }

    private static bool IsSuccess(int status) => status is >= 200 and <= 299;

    /// <summary>A JSON body with exactly "application/json" (no charset), as the macOS client sends.</summary>
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

    private static T? TryDeserialize<T>(string body)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, CloudHttp.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // MARK: - Dropbox-API-Arg encoding

    /// <summary>The argument JSON for a content endpoint's <c>Dropbox-API-Arg</c> header.</summary>
    internal static string ApiArg(JsonObject arg) => EscapeNonAscii(Serialize(arg));

    /// <summary>
    /// HTTP header values must be ASCII, so Dropbox wants every other character in
    /// <c>Dropbox-API-Arg</c> as a JSON <c>\uXXXX</c> escape. Escaping UTF-16 code units writes
    /// characters outside the BMP (emoji) as a valid surrogate pair.
    /// </summary>
    internal static string EscapeNonAscii(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c > '~')
            {
                builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    // MARK: - Dates and errors

    /// <summary>Whole-second UTC ("2026-01-01T12:00:00Z"); Dropbox rejects fractional seconds as malformed.</summary>
    internal static string Timestamp(DateTimeOffset date)
        => date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string DateString(DateTimeOffset date) => Timestamp(date);

    /// <summary>
    /// Dropbox's status mapping. 409 carries endpoint-specific errors (path not found,
    /// conflict…) in its body. macOS maps 401 to notAuthenticated and 403 to unauthorized;
    /// both drive the re-auth banner there, and Unauthorized does here.
    /// </summary>
    internal static CloudProviderException MapHttpError(int status, string? body, string? path) => status switch
    {
        401 or 403 => CloudProviderException.Unauthorized(),
        409 when body?.Contains("not_found", StringComparison.Ordinal) == true => CloudProviderException.NotFound(path ?? "Path not found"),
        429 => CloudProviderException.RateLimited(),
        _ => CloudProviderException.ServerError(status),
    };
}

/// <summary>
/// Forwards upload progress, but never reports the same bytes twice when a request is sent
/// again (the 401 retry), so a transfer's total can't run past the file size.
/// </summary>
internal sealed class HighWaterProgress(ByteProgress? onBytes)
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
