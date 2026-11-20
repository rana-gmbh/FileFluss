// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.OneDrive;

/// <summary>
/// Microsoft Graph for one OneDrive account — the macOS <c>OneDriveAPIClient</c>. Items are
/// addressed by path (<c>/me/drive/root:{path}:</c>) everywhere except the sharing calls,
/// which Graph only accepts by id. Thread-safe: one instance serves panels, transfers and
/// the indexer concurrently.
/// </summary>
internal sealed class OneDriveApiClient
{
    // FileFluss app registration in Microsoft Entra ID. Configured as a public/native client
    // with http://localhost registered for "Mobile and desktop" — Microsoft accepts any port
    // on that URI, which the loopback PKCE flow relies on.
    internal const string ClientId = "23eeca59-6b12-4ea7-8999-8d07e2af558e";
    internal const string Scopes = "https://graph.microsoft.com/Files.ReadWrite.All https://graph.microsoft.com/User.Read offline_access";
    internal const string GraphBase = "https://graph.microsoft.com/v1.0";
    internal const string AuthBase = "https://login.microsoftonline.com/common/oauth2/v2.0";

    /// <summary>Files up to this size use a single PUT; larger ones an upload session.</summary>
    internal const long SimpleUploadLimit = 4_000_000;

    /// <summary>10 MiB — Graph requires upload-session chunks in multiples of 320 KiB (this is 32 of them).</summary>
    internal const int UploadChunkSize = 10 * 1024 * 1024;

    private const string Category = "OneDrive";
    private static readonly Uri GraphRoot = new(GraphBase);

    private readonly HttpClient _http;
    private readonly Action<OneDriveApiClient, OneDriveCredentials> _persist;
    private readonly Lock _gate = new();
    private OneDriveCredentials _credentials;

    /// <summary>
    /// The refresh in flight. Concurrent callers await this one task instead of firing
    /// parallel token POSTs — Microsoft rotates refresh tokens, so a second refresh racing the
    /// first can invalidate it.
    /// </summary>
    private Task<OneDriveCredentials>? _refresh;

    public OneDriveApiClient(HttpClient http, OneDriveCredentials credentials, Action<OneDriveApiClient, OneDriveCredentials> persist)
    {
        _http = http;
        _credentials = credentials;
        _persist = persist;
    }

    public OneDriveCredentials Credentials
    {
        get
        {
            lock (_gate)
            {
                return _credentials;
            }
        }
    }

    // MARK: - OAuth (loopback redirect with PKCE)

    public static Uri AuthorizeUrl(string redirectUri, string codeChallenge, string state)
    {
        var query = string.Join('&', new (string Key, string Value)[]
        {
            ("client_id", ClientId),
            ("redirect_uri", redirectUri),
            ("response_type", "code"),
            ("response_mode", "query"),
            ("scope", Scopes),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"),
            ("prompt", "select_account"),
            ("state", state),
        }.Select(p => $"{p.Key}={CloudHttp.Escape(p.Value)}"));
        return new Uri($"{AuthBase}/authorize?{query}");
    }

    /// <summary>Trades the authorization code for tokens and looks up who signed in.</summary>
    public static async Task<OneDriveCredentials> ExchangeCodeAsync(HttpClient http, string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AuthBase}/token")
        {
            Content = CloudHttp.FormContent(
                ("client_id", ClientId),
                ("scope", Scopes),
                ("code", code),
                ("redirect_uri", redirectUri),
                ("grant_type", "authorization_code"),
                ("code_verifier", codeVerifier)),
        };
        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Token-endpoint error bodies carry an error code and description, never a token.
            var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
            SupportLogger.Shared.Log($"[OneDrive] Token exchange failed: HTTP {(int)response.StatusCode}: {body}", Category, LogLevel.Error);
            throw CloudProviderException.InvalidCredentials();
        }

        var token = await response.ReadJsonAsync<OneDriveTokenResponse>(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token.AccessToken))
        {
            throw CloudProviderException.InvalidResponse();
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
        var email = await FetchUserEmailAsync(http, token.AccessToken, cancellationToken).ConfigureAwait(false);
        SupportLogger.Shared.Log($"[OneDrive] Authenticated as {email}", Category);
        return new OneDriveCredentials(token.AccessToken, token.RefreshToken ?? string.Empty, expiresAt, email);
    }

    /// <summary>displayName, else mail, else userPrincipalName; "Unknown" when /me refuses.</summary>
    private static async Task<string> FetchUserEmailAsync(HttpClient http, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}/me");
        request.Headers.Authorization = CloudHttp.Bearer(accessToken);
        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return "Unknown";
        }

        var user = await response.ReadJsonAsync<GraphUser>(cancellationToken).ConfigureAwait(false);
        return NonEmpty(user.DisplayName) ?? NonEmpty(user.Mail) ?? NonEmpty(user.UserPrincipalName) ?? "Unknown";
    }

    // MARK: - Token refresh

    /// <summary>A valid access token, refreshing first when it expires within a minute.</summary>
    public async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        Task<OneDriveCredentials> pending;
        lock (_gate)
        {
            if (_refresh is { IsCompleted: false } inflight)
            {
                pending = inflight;
            }
            else if (DateTimeOffset.UtcNow < _credentials.ExpiresAt.AddSeconds(-60))
            {
                return _credentials.AccessToken;
            }
            else
            {
                pending = StartRefreshLocked();
            }
        }

        return (await pending.WaitAsync(cancellationToken).ConfigureAwait(false)).AccessToken;
    }

    /// <summary>
    /// Refreshes regardless of the cached expiry — after a 401, because Microsoft can
    /// invalidate an access token server-side before its local expiry. When another caller
    /// already replaced <paramref name="rejectedToken"/>, its result is used instead.
    /// </summary>
    private async Task<string> ForceRefreshAsync(string rejectedToken, CancellationToken cancellationToken)
    {
        Task<OneDriveCredentials> pending;
        lock (_gate)
        {
            if (_refresh is { IsCompleted: false } inflight)
            {
                pending = inflight;
            }
            else if (_credentials.AccessToken != rejectedToken)
            {
                return _credentials.AccessToken;
            }
            else
            {
                pending = StartRefreshLocked();
            }
        }

        return (await pending.WaitAsync(cancellationToken).ConfigureAwait(false)).AccessToken;
    }

    private Task<OneDriveCredentials> StartRefreshLocked()
    {
        if (string.IsNullOrEmpty(_credentials.RefreshToken))
        {
            throw CloudProviderException.NotAuthenticated();
        }

        // Not tied to any one caller's token: a caller that gives up must not cancel the
        // refresh everyone else is waiting for. SendApiAsync bounds it.
        var task = Task.Run(PerformRefreshAsync, CancellationToken.None);
        _refresh = task;
        return task;
    }

    private async Task<OneDriveCredentials> PerformRefreshAsync()
    {
        var current = Credentials;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AuthBase}/token")
        {
            Content = CloudHttp.FormContent(
                ("client_id", ClientId),
                ("grant_type", "refresh_token"),
                ("refresh_token", current.RefreshToken),
                ("scope", Scopes)),
        };
        using var response = await _http.SendApiAsync(request, CancellationToken.None).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"[OneDrive] Token refresh failed: HTTP {(int)response.StatusCode}", Category, LogLevel.Error);
            throw CloudProviderException.TokenRefreshFailed((int)response.StatusCode);
        }

        var token = await response.ReadJsonAsync<OneDriveTokenResponse>(CancellationToken.None).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token.AccessToken))
        {
            throw CloudProviderException.InvalidResponse();
        }

        var fresh = new OneDriveCredentials(
            token.AccessToken,
            NonEmpty(token.RefreshToken) ?? current.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
            current.UserEmail);
        lock (_gate)
        {
            _credentials = fresh;
        }

        // Persist at once: the old refresh token may already be spent.
        _persist(this, fresh);
        return fresh;
    }

    // MARK: - Listing and metadata

    public async Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        var endpoint = CloudPath.IsRoot(path) ? "/me/drive/root/children" : $"/me/drive/root:{EncodePath(path)}:/children";
        var page = await GraphJsonAsync<GraphListResponse>(HttpMethod.Get, endpoint + "?$top=1000&$orderby=name%20asc", null, path, cancellationToken).ConfigureAwait(false);
        var all = new List<GraphDriveItem>(page.Value ?? []);
        var guard = new PageGuard("OneDrive children");
        while (!string.IsNullOrEmpty(page.NextLink))
        {
            guard.Follow(page.NextLink);
            page = await GraphJsonAsync<GraphListResponse>(HttpMethod.Get, page.NextLink, null, path, cancellationToken).ConfigureAwait(false);
            all.AddRange(page.Value ?? []);
        }

        return all.Select(item => item.ToCloudFileItem(path)).ToList();
    }

    public async Task<CloudFileItem> GetMetadataAsync(string path, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        if (CloudPath.IsRoot(path))
        {
            var root = await GraphJsonAsync<GraphDriveItem>(HttpMethod.Get, "/me/drive/root", null, path, cancellationToken).ConfigureAwait(false);
            return new CloudFileItem("d" + root.Id, string.Empty, "/", true, root.Size ?? 0, GraphDates.Parse(root.LastModifiedDateTime) ?? CloudFileItem.UnknownDate);
        }

        var item = await GraphJsonAsync<GraphDriveItem>(HttpMethod.Get, ItemEndpoint(path), null, path, cancellationToken).ConfigureAwait(false);
        return item.ToCloudFileItem(CloudPath.Parent(path));
    }

    /// <summary>The folder item carries the size of its whole subtree; walk the tree only when it says 0.</summary>
    public async Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        if (CloudPath.IsRoot(path))
        {
            var root = await GraphJsonAsync<GraphDriveItem>(HttpMethod.Get, "/me/drive/root", null, path, cancellationToken).ConfigureAwait(false);
            return root.Size ?? 0;
        }

        var item = await GraphJsonAsync<GraphDriveItem>(HttpMethod.Get, ItemEndpoint(path), null, path, cancellationToken).ConfigureAwait(false);
        if (item.Size is > 0)
        {
            return item.Size.Value;
        }

        return await FolderSizeRecursivelyAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> FolderSizeRecursivelyAsync(string path, CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var item in await ListFolderAsync(path, cancellationToken).ConfigureAwait(false))
        {
            total += item.IsDirectory
                ? await FolderSizeRecursivelyAsync(item.Path, cancellationToken).ConfigureAwait(false)
                : item.Size;
        }

        return total;
    }

    public async Task<IReadOnlyList<CloudFileItem>> SearchAsync(string query, string? path, CancellationToken cancellationToken)
    {
        // An OData string literal doubles its single quotes.
        var literal = CloudHttp.Escape(query.Replace("'", "''", StringComparison.Ordinal));
        var scope = path is null ? "/" : CloudPath.Normalize(path);
        var endpoint = CloudPath.IsRoot(scope)
            ? $"/me/drive/root/search(q='{literal}')"
            : $"/me/drive/root:{EncodePath(scope)}:/search(q='{literal}')";
        var response = await GraphJsonAsync<GraphListResponse>(HttpMethod.Get, endpoint + "?$top=100", null, scope, cancellationToken).ConfigureAwait(false);

        // macOS placed every hit at "/<name>". Graph says where a hit lives in
        // parentReference.path when it can; the root is the fallback when it doesn't.
        return (response.Value ?? []).Select(item => item.ToCloudFileItem(item.ParentPathFromReference() ?? "/")).ToList();
    }

    /// <summary>
    /// /me/drive quota. total and used are bytes; some tenants report total 0 for unlimited
    /// Education/Business plans, surfaced as no upper bound so only the used figure shows.
    /// Null when Graph refuses.
    /// </summary>
    public async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, GraphUri("/me/drive?$select=quota")), false, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var drive = await response.ReadJsonAsync<GraphDriveQuota>(cancellationToken).ConfigureAwait(false);
        var total = drive.Quota?.Total is > 0 ? drive.Quota.Total : null;
        return new CloudStorageQuota(drive.Quota?.Used ?? 0, total);
    }

    // MARK: - Transfers

    public async Task DownloadAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        remotePath = CloudPath.Normalize(remotePath);
        var uri = GraphUri($"{ItemEndpoint(remotePath)}/content");

        // Graph answers with a redirect to a pre-authenticated URL; HttpClient follows it and
        // drops the Authorization header on the way, which that URL doesn't want anyway.
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), true, cancellationToken).ConfigureAwait(false);
        // Only a 2xx carries the file. A redirect that wasn't followed (https → http) has an
        // empty body, which would otherwise be saved as the file's contents.
        if (!response.IsSuccessStatusCode)
        {
            throw await GraphErrorAsync(response, $"GET {uri.AbsolutePath}", remotePath, cancellationToken).ConfigureAwait(false);
        }

        await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadAsync(string localPath, string remotePath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        remotePath = CloudPath.Normalize(remotePath);
        var info = new FileInfo(localPath);
        var size = info.Length;
        var modified = KnownLocalDate(info.LastWriteTimeUtc);
        var created = KnownLocalDate(info.CreationTimeUtc);

        if (size <= SimpleUploadLimit)
        {
            await SimpleUploadAsync(localPath, remotePath, onBytes, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await LargeFileUploadAsync(localPath, size, remotePath, onBytes, cancellationToken).ConfigureAwait(false);
        }

        // A simple upload has no way to carry timestamps, so they are PATCHed afterwards.
        // They are deliberately not put into the upload session either: fileSystemInfo in the
        // session payload caused HTTP 400 for some file types (large MOV uploads). Best-effort.
        if (modified is not null || created is not null)
        {
            try
            {
                await PatchFileSystemInfoAsync(remotePath, modified, created, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is CloudProviderException or OperationCanceledException)
            {
                SupportLogger.Shared.Log($"[OneDrive] Could not stamp timestamps on {remotePath}: {e.Message}", Category);
            }
        }
    }

    private async Task SimpleUploadAsync(string localPath, string remotePath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var uri = GraphUri($"{ItemEndpoint(remotePath)}/content");
        var progress = new RetryingProgress(onBytes);
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, uri)
            {
                Content = ProgressStreamContent.FromFile(localPath, progress.NextAttempt()),
            },
            true,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CloudProviderException.SizeLimitError((int)response.StatusCode, localPath)
                ?? await GraphErrorAsync(response, $"PUT {uri.AbsolutePath}", remotePath, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LargeFileUploadAsync(string localPath, long size, string remotePath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["item"] = new Dictionary<string, string> { ["@microsoft.graph.conflictBehavior"] = "replace" },
        };
        var session = await GraphJsonAsync<GraphUploadSession>(HttpMethod.Post, $"{ItemEndpoint(remotePath)}/createUploadSession", body, remotePath, cancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(session.UploadUrl, UriKind.Absolute, out var uploadUrl) || uploadUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw CloudProviderException.InvalidResponse();
        }

        // The upload URL is pre-authenticated: Graph documents that it must not get an
        // Authorization header. Each chunk is sent without the API timeout — 10 MiB on a slow
        // line easily takes longer than a minute.
        long offset = 0;
        while (offset < size)
        {
            var length = Math.Min(UploadChunkSize, size - offset);
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = new ProgressStreamContent(new FileSliceStream(localPath, offset, length), onBytes, length),
            };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, size);
            using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && (int)response.StatusCode != 308)
            {
                throw CloudProviderException.SizeLimitError((int)response.StatusCode, localPath)
                    ?? await GraphErrorAsync(response, "PUT upload session chunk", remotePath, cancellationToken).ConfigureAwait(false);
            }

            offset += length;
        }
    }

    private async Task PatchFileSystemInfoAsync(string remotePath, DateTimeOffset? modified, DateTimeOffset? created, CancellationToken cancellationToken)
    {
        var info = new Dictionary<string, string>();
        if (modified is { } m)
        {
            info["lastModifiedDateTime"] = GraphDates.Seconds(m);
        }

        if (created is { } c)
        {
            info["createdDateTime"] = GraphDates.Seconds(c);
        }

        var body = new Dictionary<string, object> { ["fileSystemInfo"] = info };
        using var response = await SendAsync(() => Build(HttpMethod.Patch, GraphUri(ItemEndpoint(remotePath)), body), false, cancellationToken).ConfigureAwait(false);
    }

    // MARK: - Changes

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
        => GraphVoidAsync(HttpMethod.Delete, ItemEndpoint(CloudPath.Normalize(path)), path, cancellationToken);

    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        try
        {
            await GetMetadataAsync(path, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (CloudProviderException)
        {
            // Not there (or not readable) — create it.
        }

        var parent = CloudPath.Parent(path);
        var endpoint = CloudPath.IsRoot(parent) ? "/me/drive/root/children" : $"/me/drive/root:{EncodePath(parent)}:/children";
        var body = new Dictionary<string, object>
        {
            ["name"] = CloudPath.Name(path),
            ["folder"] = new Dictionary<string, object>(),
            ["@microsoft.graph.conflictBehavior"] = "fail",
        };
        var uri = GraphUri(endpoint);
        using var response = await SendAsync(() => Build(HttpMethod.Post, uri, body), false, cancellationToken).ConfigureAwait(false);

        // 409 nameAlreadyExists: someone created it between the check and now — "succeeds
        // quietly if it already exists".
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
        {
            throw await GraphErrorAsync(response, $"POST {uri.AbsolutePath}", path, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task RenameAsync(string path, string newName, CancellationToken cancellationToken)
        => GraphJsonAsync<GraphDriveItem>(HttpMethod.Patch, ItemEndpoint(CloudPath.Normalize(path)), new Dictionary<string, object> { ["name"] = newName }, path, cancellationToken);

    /// <summary>
    /// Graph keeps the client-visible mtime in fileSystemInfo.lastModifiedDateTime; PATCHing
    /// the item updates it without re-uploading content.
    /// </summary>
    public Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["fileSystemInfo"] = new Dictionary<string, string> { ["lastModifiedDateTime"] = GraphDates.Milliseconds(date) },
        };
        return GraphJsonAsync<GraphDriveItem>(HttpMethod.Patch, ItemEndpoint(CloudPath.Normalize(path)), body, path, cancellationToken);
    }

    // MARK: - Share links

    /// <summary>
    /// Resolves a path to its drive-item id. The sharing endpoints must be addressed by id:
    /// Microsoft documents /me/drive/items/{id}/createLink, and the path-addressed form
    /// answers accessDenied on personal accounts even though reading and writing the same
    /// item by path work.
    /// </summary>
    private async Task<string> ItemIdAsync(string path, CancellationToken cancellationToken)
    {
        var item = await GraphJsonAsync<GraphDriveItem>(HttpMethod.Get, ItemEndpoint(CloudPath.Normalize(path)), null, path, cancellationToken).ConfigureAwait(false);
        return item.Id;
    }

    /// <summary>
    /// Creates an anonymous view link. Two failures are common and only explicable
    /// server-side: an administrator can forbid anonymous links, and on consumer OneDrive a
    /// password or expiry needs a Microsoft 365 subscription. Both come back as a Graph error
    /// whose message is passed through verbatim.
    /// </summary>
    public async Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        var endpoint = $"/me/drive/items/{EscapeId(await ItemIdAsync(path, cancellationToken).ConfigureAwait(false))}/createLink";
        var password = string.IsNullOrEmpty(options.Password) ? null : options.Password;

        // "view" + "anonymous" is the public read-only link. A Graph sharing link has no
        // download toggle, so AllowDownload is not represented (the capabilities hide it).
        var body = new Dictionary<string, object> { ["type"] = "view", ["scope"] = "anonymous" };
        if (password is not null)
        {
            body["password"] = password;
        }

        if (options.Expiry is { } expiry)
        {
            body["expirationDateTime"] = GraphDates.Seconds(expiry);
        }

        var uri = GraphUri(endpoint);
        using var response = await SendAsync(() => Build(HttpMethod.Post, uri, body), false, cancellationToken).ConfigureAwait(false);

        // Deliberately no retry without "scope": letting the account pick its default turns a
        // refused public link into a sign-in-only link that looks identical in the UI — the
        // recipient then hits a Microsoft login. Failing with the account's real objection is
        // the honest outcome. 201 is a new link, 200 an equivalent one that already existed.
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
        {
            var text = await response.ReadBodySnippetAsync(cancellationToken, 1500).ConfigureAwait(false);
            SupportLogger.Shared.Log($"[OneDrive] createLink → HTTP {(int)response.StatusCode}: {Truncate(text, 500)}", Category, LogLevel.Error);
            LogShareFailure($"POST {endpoint}", (int)response.StatusCode, text);
            throw ShareHttpError((int)response.StatusCode, text);
        }

        var permission = await response.ReadJsonAsync<GraphPermission>(cancellationToken).ConfigureAwait(false);
        SupportLogger.Shared.Log($"OneDrive createLink granted type={permission.Link?.Type ?? "?"} scope={permission.Link?.Scope ?? "?"}", "sharing");
        return ShareLinkFrom(permission, password is not null) ?? throw CloudProviderException.InvalidResponse();
    }

    /// <summary>
    /// The link the item already has, or null. Graph answers 200 with an empty value for an
    /// unshared item; a 404 (item deleted) stays an error.
    /// </summary>
    public async Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var permission = await SharingPermissionAsync(path, cancellationToken).ConfigureAwait(false);
            return permission is null ? null : ShareLinkFrom(permission, false);
        }
        catch (CloudProviderException e)
        {
            // Personal accounts refuse to list an item's permissions ("accessDenied") even when
            // creating a link on it works. That is "can't tell", not an error: the caller then
            // offers to create a link, which succeeds.
            if (IsAccessDenied(e))
            {
                SupportLogger.Shared.Log("OneDrive won't list permissions for this item — treating it as not shared", "sharing");
                return null;
            }

            throw ShareError(e);
        }
    }

    /// <summary>
    /// Graph has no "edit this link": createLink with different settings hands the existing
    /// permission straight back. So the permission is revoked and a new one minted — the URL
    /// changes and the returned note says so. Revoking first means a failed create leaves the
    /// item unshared rather than half-updated.
    /// </summary>
    public async Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        GraphPermission? existing;
        try
        {
            existing = await SharingPermissionAsync(path, cancellationToken).ConfigureAwait(false);
            if (existing?.Id is { } permissionId)
            {
                await DeleteSharingPermissionAsync(path, permissionId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (CloudProviderException e)
        {
            if (IsAccessDenied(e))
            {
                throw CloudProviderException.CommandFailed(Localization.L("This Microsoft account doesn't allow apps to change a file's existing link. Open the file in OneDrive on the web to change or remove it."));
            }

            throw ShareError(e);
        }

        var fresh = await CreateShareLinkAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            // Nothing was revoked, so nothing changed under the recipient's feet.
            return fresh;
        }

        var urlChanged = Localization.L("OneDrive can't change a link's settings in place, so the previous link was revoked and a new one created — the old address no longer works.");
        return fresh with { Note = fresh.Note is null ? urlChanged : $"{fresh.Note} {urlChanged}" };
    }

    public async Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        GraphPermission? permission;
        try
        {
            permission = await SharingPermissionAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e)
        {
            // Withdrawing needs the permission's id, which only the refused listing has. Say
            // what the user can do instead of repeating "access denied".
            if (IsAccessDenied(e))
            {
                throw CloudProviderException.CommandFailed(Localization.L("This Microsoft account doesn't allow apps to manage a file's existing links. Open the file in OneDrive on the web and remove the link there."));
            }

            throw ShareError(e);
        }

        if (permission?.Id is not { } permissionId)
        {
            throw CloudProviderException.CommandFailed(Localization.L("This item doesn't have a public link."));
        }

        try
        {
            await DeleteSharingPermissionAsync(path, permissionId, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e)
        {
            if (IsAccessDenied(e))
            {
                throw CloudProviderException.CommandFailed(Localization.L("This Microsoft account doesn't allow apps to remove an existing link. Open the file in OneDrive on the web and remove the link there."));
            }

            throw ShareError(e);
        }
    }

    /// <summary>
    /// The item's anonymous permission, else any permission carrying a link — so a link a
    /// tenant policy narrowed to "organization" is still found and can be withdrawn.
    /// Permissions granted to named people have no link and are never touched.
    /// </summary>
    private async Task<GraphPermission?> SharingPermissionAsync(string path, CancellationToken cancellationToken)
    {
        var id = await ItemIdAsync(path, cancellationToken).ConfigureAwait(false);
        var list = await GraphJsonAsync<GraphPermissionList>(HttpMethod.Get, $"/me/drive/items/{EscapeId(id)}/permissions", null, path, cancellationToken).ConfigureAwait(false);
        var linkPermissions = (list.Value ?? []).Where(p => p.Link is not null).ToList();
        return linkPermissions.FirstOrDefault(p => string.Equals(p.Link?.Scope, "anonymous", StringComparison.OrdinalIgnoreCase))
            ?? linkPermissions.FirstOrDefault();
    }

    private async Task DeleteSharingPermissionAsync(string path, string permissionId, CancellationToken cancellationToken)
    {
        var id = await ItemIdAsync(path, cancellationToken).ConfigureAwait(false);
        await GraphVoidAsync(HttpMethod.Delete, $"/me/drive/items/{EscapeId(id)}/permissions/{EscapeId(permissionId)}", path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A Graph permission as a share link; null when it carries no usable URL.</summary>
    internal static CloudShareLink? ShareLinkFrom(GraphPermission permission, bool requestedPassword)
    {
        if (!Uri.TryCreate(permission.Link?.WebUrl, UriKind.Absolute, out var url))
        {
            return null;
        }

        // A tenant that only allows internal sharing normally rejects the request outright,
        // but if it hands back a narrower scope the link isn't public and the user must know.
        var scope = permission.Link?.Scope?.ToLowerInvariant();
        var isPublic = scope is null or "anonymous";
        string? note = null;
        if (!isPublic)
        {
            // Name what Microsoft granted: "users" means only named people, "organization"
            // signed-in colleagues. Both show the recipient a sign-in page.
            var granted = string.Join('/', new[] { permission.Link?.Type, permission.Link?.Scope }.Where(s => !string.IsNullOrEmpty(s)));
            note = Localization.L(
                "This link is not public: OneDrive granted it as \"{0}\", so opening it asks for a Microsoft sign-in.",
                granted.Length == 0 ? "restricted" : granted);
        }

        return new CloudShareLink(url)
        {
            // Graph exposes no public direct-download URL for an anonymous link; /content needs a token.
            DirectDownloadUrl = null,
            ExpiresAt = GraphDates.Parse(permission.ExpirationDateTime),
            HasPassword = permission.HasPassword ?? requestedPassword,
            IsPublic = isPublic,
            Note = note,
        };
    }

    /// <summary>True for Graph's "you may not do this" refusals, whatever wording they arrive in.</summary>
    internal static bool IsAccessDenied(CloudProviderException error)
    {
        if (error.Kind == CloudErrorKind.Unauthorized)
        {
            return true;
        }

        if (error.Kind == CloudErrorKind.CommandFailed)
        {
            var text = error.Message.ToLowerInvariant();
            return text.Contains("accessdenied", StringComparison.Ordinal) || text.Contains("access denied", StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// Error mapping for the sharing calls. Graph answers "you may not create this link" with
    /// 403, which the generic mapping turns into "sign in again" — wrong and useless when
    /// reading and downloading plainly still work.
    /// </summary>
    internal static CloudProviderException ShareHttpError(int statusCode, string? body)
    {
        if (GraphErrors.ParseMessage(body) is { Length: > 0 } message)
        {
            return CloudProviderException.CommandFailed(message);
        }

        return statusCode switch
        {
            401 => CloudProviderException.NotAuthenticated(),
            403 => CloudProviderException.CommandFailed(Localization.L("OneDrive refused to create a public link for this file. Sharing by link can be switched off for an account, and business accounts often allow links only for people inside the organisation.")),
            _ => MapStatus(statusCode, null),
        };
    }

    /// <summary>Re-maps an error from the generic helpers for a sharing call.</summary>
    private static CloudProviderException ShareError(CloudProviderException error)
        => error.Kind == CloudErrorKind.Unauthorized ? ShareHttpError(403, null) : error;

    /// <summary>
    /// A rejected sharing call goes to the support log with Graph's whole response: "Access
    /// denied" alone doesn't say whether the account, the item or the request is at fault.
    /// </summary>
    private static void LogShareFailure(string endpoint, int statusCode, string body)
        => SupportLogger.Shared.Log(
            $"OneDrive sharing refused — {endpoint} → HTTP {statusCode}: {(body.Length == 0 ? "<no body>" : body)}",
            "sharing",
            LogLevel.Error);

    // MARK: - HTTP

    /// <summary>Sends with a bearer token; on 401 force-refreshes and retries once.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, bool transfer, CancellationToken cancellationToken)
    {
        var token = await AccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendOnceAsync(build, token, transfer, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var target = response.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;
        response.Dispose();
        SupportLogger.Shared.Log($"[OneDrive] {target} → 401, force-refreshing and retrying once", Category);
        token = await ForceRefreshAsync(token, cancellationToken).ConfigureAwait(false);
        return await SendOnceAsync(build, token, transfer, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> build, string token, bool transfer, CancellationToken cancellationToken)
    {
        using var request = build();
        request.Headers.Authorization = CloudHttp.Bearer(token);
        return transfer
            ? await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false)
            : await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> GraphJsonAsync<T>(HttpMethod method, string endpoint, object? body, string? itemPath, CancellationToken cancellationToken)
    {
        var uri = GraphUri(endpoint);
        using var response = await SendAsync(() => Build(method, uri, body), false, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await GraphErrorAsync(response, $"{method} {uri.AbsolutePath}", itemPath, cancellationToken).ConfigureAwait(false);
        }

        return await response.ReadJsonAsync<T>(cancellationToken).ConfigureAwait(false);
    }

    private async Task GraphVoidAsync(HttpMethod method, string endpoint, string? itemPath, CancellationToken cancellationToken)
    {
        var uri = GraphUri(endpoint);
        using var response = await SendAsync(() => Build(method, uri, null), false, cancellationToken).ConfigureAwait(false);

        // 204 No Content is the usual answer to DELETE.
        if (!response.IsSuccessStatusCode)
        {
            throw await GraphErrorAsync(response, $"{method} {uri.AbsolutePath}", itemPath, cancellationToken).ConfigureAwait(false);
        }
    }

    private static HttpRequestMessage Build(HttpMethod method, Uri uri, object? body)
        => new(method, uri) { Content = body is null ? null : CloudHttp.JsonContent(body) };

    /// <summary>
    /// A Graph endpoint, or an absolute <c>@odata.nextLink</c>. A next link is followed only
    /// to Graph itself, over HTTPS: it is a server-supplied URL that gets the bearer token.
    /// </summary>
    private static Uri GraphUri(string endpoint)
    {
        if (!endpoint.StartsWith('/'))
        {
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var absolute) &&
                absolute.Scheme == Uri.UriSchemeHttps &&
                string.Equals(absolute.Host, GraphRoot.Host, StringComparison.OrdinalIgnoreCase))
            {
                return absolute;
            }

            throw CloudProviderException.InvalidResponse();
        }

        return new Uri(GraphBase + endpoint);
    }

    /// <summary>
    /// Maps an unsuccessful Graph response. 401/404/429/507 keep their meaning (re-auth
    /// banner, not-found checks, back-off, quota); otherwise Graph's own message wins, because
    /// it is the only place the real cause shows up.
    /// </summary>
    private static async Task<CloudProviderException> GraphErrorAsync(HttpResponseMessage response, string context, string? itemPath, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var body = await response.ReadBodySnippetAsync(cancellationToken, 1500).ConfigureAwait(false);
        SupportLogger.Shared.Log($"[OneDrive] {context} → HTTP {status}: {Truncate(body, 500)}", Category, LogLevel.Error);
        if (context.Contains("/permissions", StringComparison.Ordinal) || context.Contains("/createLink", StringComparison.Ordinal))
        {
            LogShareFailure(context, status, body);
        }

        if (status == 429)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(CultureInfo.InvariantCulture) ?? "?";
            SupportLogger.Shared.Log($"[OneDrive] Throttled; Retry-After {retryAfter}s", Category, LogLevel.Notice);
        }

        return status switch
        {
            401 or 404 or 429 or 507 => MapStatus(status, itemPath),
            _ when GraphErrors.ParseMessage(body) is { Length: > 0 } message => CloudProviderException.CommandFailed(message),
            _ => MapStatus(status, itemPath),
        };
    }

    private static CloudProviderException MapStatus(int status, string? itemPath) => status switch
    {
        401 => CloudProviderException.Unauthorized(),
        403 => CloudProviderException.Unauthorized(),
        404 => CloudProviderException.NotFound(itemPath ?? string.Empty),
        429 => CloudProviderException.RateLimited(),
        507 => CloudProviderException.QuotaExceeded(),
        _ => CloudProviderException.ServerError(status),
    };

    /// <summary>"/me/drive/root:{path}:" — the path-addressed drive item.</summary>
    private static string ItemEndpoint(string path) => $"/me/drive/root:{EncodePath(path)}:";

    /// <summary>
    /// Each segment percent-encoded. Stricter than macOS' urlPathAllowed on purpose: a
    /// literal "+" in a path-addressed Graph URL is reported to be read as a space.
    /// </summary>
    internal static string EncodePath(string path) => CloudHttp.EscapePath(CloudPath.Normalize(path));

    /// <summary>
    /// An item or permission id as one path segment. Ids such as "ABC!123" stay readable
    /// (RFC 3986 pchar is kept, as macOS' urlPathAllowed does); anything else is encoded.
    /// </summary>
    internal static string EscapeId(string id)
    {
        var builder = new StringBuilder(id.Length);
        foreach (var b in Encoding.UTF8.GetBytes(id))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || "-._~!$&'()*+,;=:@".Contains(c)))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>NTFS reports 1601-01-01 when it knows no date; that is no date to stamp.</summary>
    private static DateTimeOffset? KnownLocalDate(DateTime utc)
        => utc.Year > 1601 ? new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)) : null;

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>
    /// Progress across a retried upload: the retry re-sends bytes the first attempt already
    /// reported, so only bytes beyond the furthest point reached are passed on.
    /// </summary>
    private sealed class RetryingProgress(ByteProgress? onBytes)
    {
        private long _reported;
        private long _attempt;

        public ByteProgress? NextAttempt()
        {
            if (onBytes is null)
            {
                return null;
            }

            Interlocked.Exchange(ref _attempt, 0);
            return delta =>
            {
                var sent = Interlocked.Add(ref _attempt, delta);
                var reported = Interlocked.Read(ref _reported);
                if (sent > reported)
                {
                    Interlocked.Exchange(ref _reported, sent);
                    onBytes(sent - reported);
                }
            };
        }
    }
}
