// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers.S3;

namespace FileFluss.Core.Providers.Drime;

/// <summary>
/// Drime Cloud (https://docs.drime.cloud), port of the macOS <c>DrimeAPIClient</c>.
///
/// <para>Drime addresses everything by numeric entry id; FileFluss addresses by path. The
/// client keeps a path → id cache filled from listings, the way Box does. The root of the
/// personal Drive has no entry of its own — the API spells it as an omitted
/// <c>folderId</c> / a null parent — and the cache stores it as id 0. Only the personal
/// workspace (<c>workspaceId=0</c>) is exposed.</para>
///
/// <para>Thread-safe: one instance serves panels, transfers and the indexer concurrently.</para>
/// </summary>
internal sealed class DrimeApiClient
{
    internal const string LogCategory = "Drime";

    /// <summary>
    /// Client ID of the FileFluss application in Drime's developer console, registered as a
    /// desktop app with the redirect URL <c>http://127.0.0.1/callback</c>. A desktop app is a
    /// public client: PKCE stands in for a secret. Empty, as on macOS: until it is filled in,
    /// browser sign-in is not offered and accounts connect with a personal access token.
    /// </summary>
    internal const string ClientId = "";

    public static bool IsOAuthAvailable => ClientId.Length > 0;

    internal const string TokenUrl = "https://app.drime.cloud/oauth/token";
    internal const string RevokeUrl = "https://app.drime.cloud/oauth/revoke";
    internal const string ApiUrl = "https://app.drime.cloud/api/v1";
    internal const string ShareBaseUrl = "https://app.drime.cloud/drive/shares/";

    /// <summary>Where the user creates a personal access token.</summary>
    internal const string TokenSettingsUrl = "https://app.drime.cloud/account-settings#developers";

    // MARK: - Upload sizing

    /// <summary>Below this a file goes up in one <c>POST /uploads</c>; at or above it, through an S3 multipart upload. Drime's own threshold.</summary>
    internal const long MultipartThreshold = 5L * 1024 * 1024;

    /// <summary>Drime's minimum part is 5 MiB; larger parts mean fewer signing round trips, which is what dominates on a fast line.</summary>
    internal const long MinimumPartSize = 16L * 1024 * 1024;

    internal const long MaximumParts = 10_000;

    /// <summary>Parts in flight at once. Drime suggests up to 8.</summary>
    internal const int PartConcurrency = 4;

    /// <summary>Drime serves at most this many entries per listing page.</summary>
    private const int PageSize = 1000;

    private const int MaxAttempts = 4;

    /// <summary>Server errors a GET may repeat — Cloudflare's 52x included, since Drime sits behind it.</summary>
    private static readonly HashSet<int> RetryableGetStatuses = [500, 502, 503, 504, 520, 521, 522, 523, 524];

    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http;
    private readonly Action<DrimeCredentials> _credentialsChanged;
    private readonly Lock _gate = new();
    private DrimeCredentials _credentials;

    /// <summary>
    /// The refresh in flight. Drime rotates refresh tokens and treats a reused one as stolen —
    /// revoking every token FileFluss holds — so concurrent callers await this one refresh.
    /// </summary>
    private Task<DrimeCredentials>? _inflightRefresh;

    /// <summary>Path → entry id. "/" is the root, id 0.</summary>
    private readonly ConcurrentDictionary<string, long> _pathIdCache = new(StringComparer.Ordinal) { ["/"] = 0 };

    /// <param name="credentialsChanged">
    /// Called with every refreshed credential set, before the new token is used: the old
    /// refresh token is dead the moment a new one exists.
    /// </param>
    public DrimeApiClient(HttpClient http, DrimeCredentials credentials, Action<DrimeCredentials> credentialsChanged)
    {
        _http = http;
        _credentials = credentials;
        _credentialsChanged = credentialsChanged;
    }

    /// <summary>Test seam: the single/multipart threshold.</summary>
    internal long MultipartThresholdBytes { get; set; } = MultipartThreshold;

    /// <summary>Test seam: a fixed part size instead of <see cref="PartSize"/>.</summary>
    internal long? PartSizeOverride { get; set; }

    /// <summary>Waits between retries. Tests make it instant.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    internal IDictionary<string, long> PathIdCache => _pathIdCache;

    public DrimeCredentials Credentials
    {
        get
        {
            lock (_gate)
            {
                return _credentials;
            }
        }
    }

    // MARK: - Sign-in

    /// <summary>
    /// Checks a personal access token by asking who it belongs to — every token may read
    /// <c>/users/me</c>, whatever its permissions.
    /// </summary>
    public static async Task<DrimeCredentials> ValidatePersonalTokenAsync(HttpClient http, string token, CancellationToken cancellationToken)
    {
        token = token.Trim();
        if (token.Length == 0)
        {
            throw CloudProviderException.InvalidCredentials();
        }

        (string Email, string DisplayName) user;
        try
        {
            user = await FetchUserAsync(http, token, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotAuthenticated)
        {
            throw CloudProviderException.CommandFailed(Localization.L("Drime didn't accept this token. Check that it was copied completely and hasn't been deleted."));
        }

        return DrimeCredentials.PersonalToken(token, user.Email, user.DisplayName);
    }

    private static async Task<(string Email, string DisplayName)> FetchUserAsync(HttpClient http, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl + "/users/me");
        request.Headers.Authorization = CloudHttp.Bearer(accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw DrimeJson.MapHttpError((int)response.StatusCode, body);
        }

        return ParseJson(body, root =>
        {
            if (!root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
            {
                throw CloudProviderException.InvalidResponse();
            }

            return (DrimeJson.String(user, "email") ?? string.Empty, DrimeJson.String(user, "display_name") ?? string.Empty);
        });
    }

    /// <summary>
    /// Ends the authorization on Drime's side, so a removed account doesn't leave a working
    /// grant behind. Personal tokens can only be deleted on the website. Best effort: removal
    /// goes ahead either way.
    /// </summary>
    public async Task RevokeAsync()
    {
        var credentials = Credentials;
        if (credentials.Kind != DrimeCredentialKind.OAuth || credentials.RefreshToken.Length == 0)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, RevokeUrl)
            {
                Content = CloudHttp.FormContent(("token", credentials.RefreshToken), ("client_id", ClientId)),
            };
            using var response = await _http.SendApiAsync(request, CancellationToken.None, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch (CloudProviderException)
        {
        }
    }

    // MARK: - Token refresh

    /// <summary>The current credentials, refreshed first when an OAuth token expires within a minute.</summary>
    public Task<DrimeCredentials> RefreshIfNeededAsync(CancellationToken cancellationToken)
    {
        Task<DrimeCredentials> task;
        lock (_gate)
        {
            if (_inflightRefresh is null &&
                (_credentials.Kind != DrimeCredentialKind.OAuth || DateTimeOffset.UtcNow < _credentials.ExpiresAt.AddSeconds(-60)))
            {
                return Task.FromResult(_credentials);
            }

            task = _inflightRefresh ?? StartRefreshLocked();
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>Refreshes regardless of the expiry — after a 401. A personal token has nothing to refresh.</summary>
    private Task<DrimeCredentials> ForceRefreshAsync(CancellationToken cancellationToken)
    {
        Task<DrimeCredentials> task;
        lock (_gate)
        {
            if (_inflightRefresh is null && _credentials.Kind != DrimeCredentialKind.OAuth)
            {
                return Task.FromException<DrimeCredentials>(CloudProviderException.NotAuthenticated());
            }

            task = _inflightRefresh ?? StartRefreshLocked();
        }

        return task.WaitAsync(cancellationToken);
    }

    private Task<DrimeCredentials> StartRefreshLocked()
    {
        var refreshToken = _credentials.RefreshToken;
        if (refreshToken.Length == 0)
        {
            return Task.FromException<DrimeCredentials>(CloudProviderException.NotAuthenticated());
        }

        // Task.Run so the refresh never completes (and clears _inflightRefresh) before it was
        // published under the lock. Not tied to one caller's token: others await it too.
        var task = Task.Run(() => RunRefreshAsync(refreshToken));
        _inflightRefresh = task;
        return task;
    }

    private async Task<DrimeCredentials> RunRefreshAsync(string refreshToken)
    {
        try
        {
            return await PerformRefreshAsync(refreshToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _inflightRefresh = null;
            }
        }
    }

    private async Task<DrimeCredentials> PerformRefreshAsync(string refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = CloudHttp.FormContent(("grant_type", "refresh_token"), ("refresh_token", refreshToken), ("client_id", ClientId)),
        };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _http.SendApiAsync(request, CancellationToken.None).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw TokenError((int)response.StatusCode, body);
        }

        var (accessToken, newRefreshToken, expiresIn) = ParseJson(body, root => (
            DrimeJson.String(root, "access_token") ?? throw CloudProviderException.InvalidResponse(),
            DrimeJson.String(root, "refresh_token"),
            DrimeJson.Int64(root, "expires_in") ?? 3600));

        DrimeCredentials updated;
        lock (_gate)
        {
            updated = _credentials with
            {
                AccessToken = accessToken,
                RefreshToken = string.IsNullOrEmpty(newRefreshToken) ? _credentials.RefreshToken : newRefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            };
            _credentials = updated;
        }

        // Persist before anyone uses the new token: the old refresh token is already dead.
        _credentialsChanged(updated);
        return updated;
    }

    /// <summary>
    /// invalid_grant means the grant is gone (revoked, expired after 90 days, or a refresh
    /// token was reused): only a new sign-in helps. Anything else says what Drime said.
    /// </summary>
    private static CloudProviderException TokenError(int status, string body)
    {
        string? error = null;
        string? description = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            error = DrimeJson.String(document.RootElement, "error");
            description = DrimeJson.String(document.RootElement, "error_description");
        }
        catch (JsonException)
        {
        }

        SupportLogger.Shared.Log($"[Drime] Token request failed: HTTP {status} {error}", LogCategory, LogLevel.Error);
        if (error == "invalid_grant")
        {
            return CloudProviderException.NotAuthenticated();
        }

        if (status == 429)
        {
            return CloudProviderException.RateLimited();
        }

        return CloudProviderException.CommandFailed(Localization.L("Drime sign-in failed: {0}", description ?? error ?? $"HTTP {status}"));
    }

    // MARK: - Account

    /// <summary><c>available</c> is the plan's total, not what is left of it.</summary>
    public async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken)
    {
        var body = await SendAsync(HttpMethod.Get, "/user/space-usage", [("workspaceId", "0")], null, cancellationToken).ConfigureAwait(false);
        return ParseJson(body, root =>
        {
            var available = DrimeJson.Int64(root, "available") ?? 0;
            return new CloudStorageQuota(DrimeJson.Int64(root, "used") ?? 0, available > 0 ? available : null);
        });
    }

    // MARK: - Listing

    public async Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = Normalize(path);
        var folderId = await ResolveFolderIdAsync(normalized, cancellationToken).ConfigureAwait(false);
        var entries = await ListEntriesAsync(folderId, cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            _pathIdCache[Join(normalized, entry.Name)] = entry.Id;
        }

        _pathIdCache.RetainPathChildren(normalized, entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal));
        return entries.Select(e => e.ToItem(normalized)).ToList();
    }

    /// <summary>One page of <c>GET /drive/file-entries</c> as panel rows — the wire format the tests pin.</summary>
    internal static IReadOnlyList<CloudFileItem> ItemsFromListingPage(string json, string parentPath)
        => ParseListing(json).Entries.Select(e => e.ToItem(Normalize(parentPath))).ToList();

    private static (IReadOnlyList<DrimeEntry> Entries, long? CurrentPage, long? LastPage) ParseListing(string json)
        => ParseJson(json, root =>
        {
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw CloudProviderException.InvalidResponse();
            }

            IReadOnlyList<DrimeEntry> entries = data.EnumerateArray().Select(DrimeEntry.Parse).ToList();
            return (entries, DrimeJson.Int64(root, "current_page"), DrimeJson.Int64(root, "last_page"));
        });

    /// <summary>Every entry directly inside <paramref name="folderId"/> (0 = root), all pages.</summary>
    private async Task<IReadOnlyList<DrimeEntry>> ListEntriesAsync(long folderId, CancellationToken cancellationToken)
    {
        var all = new List<DrimeEntry>();
        for (var page = 1; ; page++)
        {
            var query = new List<(string, string)>
            {
                ("workspaceId", "0"),
                ("perPage", PageSize.ToString(CultureInfo.InvariantCulture)),
                ("page", page.ToString(CultureInfo.InvariantCulture)),
                ("orderBy", "name"),
                ("orderDir", "asc"),
            };
            if (folderId != 0)
            {
                query.Add(("folderId", folderId.ToString(CultureInfo.InvariantCulture)));
            }

            var body = await SendAsync(HttpMethod.Get, "/drive/file-entries", [.. query], null, cancellationToken).ConfigureAwait(false);
            var (entries, current, last) = ParseListing(body);

            // Past the last page it allows, Drime serves that page again instead of an empty
            // one. Returning what we have would make the rest of the folder look deleted — to
            // a mirror sync above all — so refuse instead.
            if (current is { } served && served != page)
            {
                throw CloudProviderException.CommandFailed(Localization.L("This Drime folder holds more items than Drime will list. Split it into smaller folders to browse it here."));
            }

            all.AddRange(entries);
            if (last is not { } lastPage || page >= lastPage || entries.Count == 0)
            {
                return all;
            }
        }
    }

    public async Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = Normalize(path);
        var entry = await ResolveEntryAsync(normalized, cancellationToken).ConfigureAwait(false);
        return entry.ToItem(Parent(normalized));
    }

    // MARK: - Download

    /// <summary>
    /// <c>GET /file-entries/{id}</c> serves the content. Drime may redirect to its storage;
    /// HttpClient drops the Authorization header when the host changes, which the signed URL
    /// doesn't need anyway.
    /// </summary>
    public async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var entry = await ResolveEntryAsync(Normalize(remotePath), cancellationToken).ConfigureAwait(false);
        if (entry.IsFolder)
        {
            throw CloudProviderException.NotFound(remotePath);
        }

        var url = $"{ApiUrl}/file-entries/{entry.Id}";
        async Task<HttpResponseMessage> AttemptAsync(bool forceRefresh)
        {
            var credentials = forceRefresh
                ? await ForceRefreshAsync(cancellationToken).ConfigureAwait(false)
                : await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = CloudHttp.Bearer(credentials.AccessToken);
            return await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var response = await AttemptAsync(false).ConfigureAwait(false);
        if ((int)response.StatusCode == 401 && Credentials.Kind == DrimeCredentialKind.OAuth)
        {
            response.Dispose();
            response = await AttemptAsync(true).ConfigureAwait(false);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
                SupportLogger.Shared.Log($"[Drime] Download failed: HTTP {(int)response.StatusCode}", LogCategory, LogLevel.Error);
                throw DrimeJson.MapHttpError((int)response.StatusCode, body);
            }

            await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    // MARK: - Upload

    /// <summary>
    /// Drime never overwrites: a file uploaded next to one of the same name is stored as
    /// "name (1)". So a replacement uploads first, then trashes the old file and gives the
    /// new one its name — the old content stays until the new content is safely stored.
    /// </summary>
    public async Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var path = Normalize(remotePath);
        var name = Name(path);
        var parentId = await ResolveFolderIdAsync(Parent(path), cancellationToken).ConfigureAwait(false);
        var size = new FileInfo(localPath).Length;

        var existing = (await ListEntriesAsync(parentId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(e => e.Name == name);
        if (existing is { IsFolder: true })
        {
            throw CloudProviderException.CommandFailed(Localization.L("A folder named “{0}” is already there.", name));
        }

        var uploaded = size < MultipartThresholdBytes
            ? await SimpleUploadAsync(localPath, name, parentId, onBytes, cancellationToken).ConfigureAwait(false)
            : await MultipartUploadAsync(localPath, size, name, parentId, onBytes, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            await TrashAsync([existing.Id], cancellationToken).ConfigureAwait(false);
        }

        if (uploaded.Name != name)
        {
            uploaded = await RenameAsync(uploaded.Id, name, cancellationToken).ConfigureAwait(false);
        }

        _pathIdCache[path] = uploaded.Id;
    }

    private async Task<DrimeEntry> SimpleUploadAsync(string localPath, string name, long parentId, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        HttpContent Body()
        {
            // Plain multipart rather than MultipartFormDataContent, which would rewrite the
            // file name as RFC 2047/5987 for anything non-ASCII — Drime (PHP) reads only the
            // raw filename="…", so it goes out as UTF-8, quotes escaped as on macOS.
            var form = new MultipartContent("form-data", "FileFluss-" + Guid.NewGuid().ToString("N"))
            {
                HeaderEncodingSelector = (_, _) => Encoding.UTF8,
            };

            void Field(string key, string value)
            {
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
                content.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"{key}\"");
                form.Add(content);
            }

            if (parentId != 0)
            {
                Field("parentId", parentId.ToString(CultureInfo.InvariantCulture));
            }

            Field("workspaceId", "0");

            // Just the leaf: a longer relativePath makes Drime build folders from it.
            Field("relativePath", name);
            var file = ProgressStreamContent.FromFile(localPath, onBytes, MimeType(name));
            file.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"file\"; filename=\"{name.Replace("\"", "%22", StringComparison.Ordinal)}\"");
            form.Add(file);
            return form;
        }

        async Task<(int Status, string Body)> AttemptAsync(bool forceRefresh)
        {
            var credentials = forceRefresh
                ? await ForceRefreshAsync(cancellationToken).ConfigureAwait(false)
                : await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl + "/uploads") { Content = Body() };
            request.Headers.Authorization = CloudHttp.Bearer(credentials.AccessToken);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        var (status, body) = await AttemptAsync(false).ConfigureAwait(false);
        if (status == 401 && Credentials.Kind == DrimeCredentialKind.OAuth)
        {
            (status, body) = await AttemptAsync(true).ConfigureAwait(false);
        }

        if (status is < 200 or >= 300)
        {
            SupportLogger.Shared.Log($"[Drime] Upload failed: HTTP {status}: {Prefix(body, 300)}", LogCategory, LogLevel.Error);
            throw CloudProviderException.SizeLimitError(status, localPath) ?? DrimeJson.MapHttpError(status, body);
        }

        return FileEntry(body);
    }

    /// <summary>S3 multipart: open, sign and PUT each part straight to storage, complete, then register the object as a Drime entry.</summary>
    private async Task<DrimeEntry> MultipartUploadAsync(string localPath, long size, string name, long parentId, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var partSize = PartSizeOverride ?? PartSize(size);
        var partCount = (int)((size + partSize - 1) / partSize);
        var mime = MimeType(name);

        // Drime's multipart endpoints insist on an extension.
        var extension = Path.GetExtension(name).TrimStart('.');
        if (extension.Length == 0)
        {
            extension = "bin";
        }

        var create = new JsonObject
        {
            ["filename"] = name,
            ["mime"] = mime,
            ["size"] = size,
            ["extension"] = extension,
            ["relativePath"] = name,
            ["workspaceId"] = 0,
        };
        AddParent(create, "parentId", parentId);
        var created = await SendAsync(HttpMethod.Post, "/s3/multipart/create", [], create, cancellationToken).ConfigureAwait(false);
        var (key, uploadId) = ParseJson(created, root => (
            DrimeJson.String(root, "key") ?? throw CloudProviderException.InvalidResponse(),
            DrimeJson.String(root, "uploadId") ?? throw CloudProviderException.InvalidResponse()));

        try
        {
            var parts = await UploadPartsAsync(localPath, partSize, partCount, size, key, uploadId, onBytes, cancellationToken).ConfigureAwait(false);

            var partList = new JsonArray();
            foreach (var (number, etag) in parts)
            {
                partList.Add(new JsonObject { ["PartNumber"] = number, ["ETag"] = etag });
            }

            await SendAsync(HttpMethod.Post, "/s3/multipart/complete", [], new JsonObject
            {
                ["key"] = key,
                ["uploadId"] = uploadId,
                ["parts"] = partList,
            }, cancellationToken).ConfigureAwait(false);

            var entry = new JsonObject
            {
                ["filename"] = key[(key.LastIndexOf('/') + 1)..],
                ["size"] = size,
                ["clientName"] = name,
                ["clientMime"] = mime,
                ["clientExtension"] = extension,
            };
            AddParent(entry, "parentId", parentId);
            entry["relativePath"] = name;
            entry["workspaceId"] = 0;
            return FileEntry(await SendAsync(HttpMethod.Post, "/s3/entries", [], entry, cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            await AbortMultipartAsync(key, uploadId).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>At least <see cref="MinimumPartSize"/>, large enough to stay under the 10,000-part limit; whole MiB so every part but the last is equal.</summary>
    internal static long PartSize(long fileSize)
    {
        const long mib = 1024 * 1024;
        var needed = (fileSize + MaximumParts - 1) / MaximumParts;
        var rounded = (needed + mib - 1) / mib * mib;
        return Math.Max(MinimumPartSize, rounded);
    }

    private async Task<IReadOnlyList<(int Number, string ETag)>> UploadPartsAsync(
        string localPath, long partSize, int partCount, long fileSize, string key, string uploadId, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<(int Number, string ETag)>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = PartConcurrency, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(1, partCount), options, async (number, token) =>
        {
            var offset = (number - 1) * partSize;
            var length = Math.Min(partSize, fileSize - offset);
            var url = await SignPartAsync(number, key, uploadId, token).ConfigureAwait(false);
            var etag = await PutPartAsync(localPath, offset, length, url, onBytes, token).ConfigureAwait(false);
            results.Add((number, etag));
        }).ConfigureAwait(false);
        return results.OrderBy(r => r.Number).ToList();
    }

    private async Task<Uri> SignPartAsync(int number, string key, string uploadId, CancellationToken cancellationToken)
    {
        var body = await SendAsync(HttpMethod.Post, "/s3/multipart/batch-sign-part-urls", [], new JsonObject
        {
            ["key"] = key,
            ["uploadId"] = uploadId,
            ["partNumbers"] = new JsonArray(number),
        }, cancellationToken).ConfigureAwait(false);

        var raw = ParseJson(body, root =>
        {
            if (!root.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var all = urls.EnumerateArray().ToList();
            var match = all.FirstOrDefault(u => DrimeJson.Int64(u, "partNumber") == number);
            return DrimeJson.String(match.ValueKind == JsonValueKind.Object ? match : all.FirstOrDefault(), "url");
        });
        return raw is not null && Uri.TryCreate(raw, UriKind.Absolute, out var url) ? url : throw CloudProviderException.InvalidResponse();
    }

    /// <summary>
    /// PUTs one part to its presigned storage URL — no Authorization header, the signature is
    /// in the URL. Transient failures are retried; progress is reported only once per byte
    /// however often it is resent.
    /// </summary>
    private async Task<string> PutPartAsync(string localPath, long offset, long length, Uri url, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var progress = new PartProgress(onBytes);
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.BeginAttempt();
            var slice = new FileSliceStream(localPath, offset, length);
            if (slice.Length != length)
            {
                await slice.DisposeAsync().ConfigureAwait(false);
                throw CloudProviderException.CommandFailed(Localization.L("The file changed while it was being uploaded."));
            }

            using (var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ProgressStreamContent(slice, progress.Handler, length) })
            {
                try
                {
                    using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    if (response.IsSuccessStatusCode)
                    {
                        // S3 wants the ETag back exactly as sent, quotes and all.
                        return response.Headers.TryGetValues("ETag", out var values) && values.FirstOrDefault() is { Length: > 0 } etag
                            ? etag
                            : throw CloudProviderException.InvalidResponse();
                    }

                    if (attempt == MaxAttempts || !(status == 429 || status is >= 500 and <= 599))
                    {
                        var body = await response.ReadBodySnippetAsync(cancellationToken, 300).ConfigureAwait(false);
                        SupportLogger.Shared.Log($"[Drime] Part upload failed: HTTP {status}: {body}", LogCategory, LogLevel.Error);
                        throw DrimeJson.MapHttpError(status, body);
                    }
                }
                catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NetworkError && attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
                {
                    SupportLogger.Shared.Log($"[Drime] Part upload error ({e.InnerException?.GetType().Name}), retrying", LogCategory);
                }
            }

            await Delay(delay, cancellationToken).ConfigureAwait(false);
            delay *= 2;
        }

        throw CloudProviderException.InvalidResponse();
    }

    /// <summary>Best effort, and not tied to the upload's token, so it still runs when the upload was cancelled.</summary>
    private async Task AbortMultipartAsync(string key, string uploadId)
    {
        try
        {
            var credentials = await RefreshIfNeededAsync(CancellationToken.None).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl + "/s3/multipart/abort")
            {
                Content = JsonBody(new JsonObject { ["key"] = key, ["uploadId"] = uploadId }),
            };
            request.Headers.Authorization = CloudHttp.Bearer(credentials.AccessToken);
            using var response = await _http.SendApiAsync(request, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (CloudProviderException)
        {
        }
    }

    // MARK: - Changes

    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path);
        try
        {
            if ((await ResolveEntryAsync(path, cancellationToken).ConfigureAwait(false)).IsFolder)
            {
                return;
            }
        }
        catch (CloudProviderException)
        {
            // Not there (or not resolvable right now): try to create it.
        }

        var parentId = await ResolveFolderIdAsync(Parent(path), cancellationToken).ConfigureAwait(false);
        var body = new JsonObject { ["name"] = Name(path) };
        AddParent(body, "parentId", parentId);
        var created = await SendAsync(HttpMethod.Post, "/folders", [("workspaceId", "0")], body, cancellationToken).ConfigureAwait(false);
        var folder = ParseJson(created, root => root.TryGetProperty("folder", out var f) ? DrimeEntry.Parse(f) : throw CloudProviderException.InvalidResponse());
        _pathIdCache[path] = folder.Id;
    }

    /// <summary>Moves to Drime's trash, like the web app's delete — recoverable for the user there.</summary>
    public async Task DeleteItemAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path);
        var entry = await ResolveEntryAsync(path, cancellationToken).ConfigureAwait(false);
        await TrashAsync([entry.Id], cancellationToken).ConfigureAwait(false);
        _pathIdCache.RemovePathSubtree(path);
    }

    private Task TrashAsync(long[] ids, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, "/file-entries/delete", [], new JsonObject
        {
            ["entryIds"] = new JsonArray([.. ids.Select(id => (JsonNode?)id)]),
            ["deleteForever"] = false,
        }, cancellationToken);

    public async Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken)
    {
        path = Normalize(path);
        var entry = await ResolveEntryAsync(path, cancellationToken).ConfigureAwait(false);
        var renamed = await RenameAsync(entry.Id, newName, cancellationToken).ConfigureAwait(false);
        _pathIdCache.RemovePathSubtree(path);
        _pathIdCache[Join(Parent(path), newName)] = renamed.Id;
    }

    /// <summary>
    /// A plain PUT to this route comes back from Drime's origin malformed (Cloudflare reports a
    /// 520), so it goes as a POST with Laravel's method override, which reaches the same handler.
    /// </summary>
    private async Task<DrimeEntry> RenameAsync(long entryId, string newName, CancellationToken cancellationToken)
    {
        var body = await SendAsync(
            HttpMethod.Post,
            $"/file-entries/{entryId}",
            [("workspaceId", "0")],
            new JsonObject { ["name"] = newName },
            cancellationToken,
            headers: [("X-HTTP-Method-Override", "PUT")]).ConfigureAwait(false);
        return FileEntry(body);
    }

    public async Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken)
    {
        var source = Normalize(path);
        var destination = Normalize(newPath);
        var entry = await ResolveEntryAsync(source, cancellationToken).ConfigureAwait(false);
        var newName = Name(destination);
        var targetParent = Parent(destination);

        if (Parent(source) != targetParent)
        {
            var parentId = await ResolveFolderIdAsync(targetParent, cancellationToken).ConfigureAwait(false);
            var body = new JsonObject { ["entryIds"] = new JsonArray(entry.Id) };
            AddParent(body, "destinationId", parentId);
            body["workspaceId"] = 0;
            await SendAsync(HttpMethod.Post, "/file-entries/move", [("workspaceId", "0")], body, cancellationToken).ConfigureAwait(false);
        }

        if (entry.Name != newName)
        {
            await RenameAsync(entry.Id, newName, cancellationToken).ConfigureAwait(false);
        }

        _pathIdCache.RemovePathSubtree(source);
        _pathIdCache[destination] = entry.Id;
    }

    /// <summary><c>duplicate</c> copies whole folders server-side. In a folder that already has the name it produces "name (1)", hence the rename.</summary>
    public async Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken)
    {
        var source = Normalize(path);
        var destination = Normalize(newPath);
        var entry = await ResolveEntryAsync(source, cancellationToken).ConfigureAwait(false);
        var parentId = await ResolveFolderIdAsync(Parent(destination), cancellationToken).ConfigureAwait(false);
        var body = new JsonObject { ["entryIds"] = new JsonArray(entry.Id) };
        AddParent(body, "destinationId", parentId);
        body["workspaceId"] = 0;
        var result = await SendAsync(HttpMethod.Post, "/file-entries/duplicate", [("workspaceId", "0")], body, cancellationToken).ConfigureAwait(false);
        var copy = ParseJson(result, root =>
            root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() > 0
                ? DrimeEntry.Parse(entries[0])
                : throw CloudProviderException.InvalidResponse());

        var newName = Name(destination);
        if (copy.Name != newName)
        {
            copy = await RenameAsync(copy.Id, newName, cancellationToken).ConfigureAwait(false);
        }

        _pathIdCache[destination] = copy.Id;
    }

    /// <summary>Stores the original modification time (Unix milliseconds), which listings then report as <c>client_last_modified</c>.</summary>
    public async Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken)
    {
        var entry = await ResolveEntryAsync(Normalize(path), cancellationToken).ConfigureAwait(false);
        if (entry.IsFolder)
        {
            throw CloudProviderException.NotImplemented();
        }

        await SendAsync(HttpMethod.Post, $"/file-entries/{entry.Id}/metadata", [], new JsonObject { ["lastModified"] = date.ToUnixTimeMilliseconds() }, cancellationToken)
            .ConfigureAwait(false);
    }

    // MARK: - Share links

    public async Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        var entry = await ResolveEntryAsync(Normalize(path), cancellationToken).ConfigureAwait(false);
        string body;
        try
        {
            body = await SendAsync(HttpMethod.Get, $"/file-entries/{entry.Id}/shareable-link", [], null, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            return null;
        }

        return ParseJson(body, ShareLink);
    }

    public async Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        var entry = await ResolveEntryAsync(Normalize(path), cancellationToken).ConfigureAwait(false);
        var request = new JsonObject();
        AddIfPresent(request, "password", string.IsNullOrEmpty(options.Password) ? null : options.Password);
        AddIfPresent(request, "expires_at", options.Expiry is { } expiry ? DrimeJson.FormatTimestamp(expiry) : null);
        request["allow_edit"] = false;
        request["allow_download"] = options.AllowDownload;
        var body = await SendAsync(HttpMethod.Post, $"/file-entries/{entry.Id}/shareable-link", [], request, cancellationToken, surfaceServerMessage: true)
            .ConfigureAwait(false);
        return ParseJson(body, ShareLink) ?? throw CloudProviderException.InvalidResponse();
    }

    public async Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        var entry = await ResolveEntryAsync(Normalize(path), cancellationToken).ConfigureAwait(false);

        // The update route takes camelCase where the create route takes snake_case.
        var request = new JsonObject();
        AddIfPresent(request, "password", string.IsNullOrEmpty(options.Password) ? null : options.Password);
        AddIfPresent(request, "expiresAt", options.Expiry is { } expiry ? DrimeJson.FormatTimestamp(expiry) : null);
        request["allowEdit"] = false;
        request["allowDownload"] = options.AllowDownload;
        var body = await SendAsync(HttpMethod.Put, $"/file-entries/{entry.Id}/shareable-link", [], request, cancellationToken, surfaceServerMessage: true)
            .ConfigureAwait(false);
        return ParseJson(body, ShareLink) ?? throw CloudProviderException.InvalidResponse();
    }

    public async Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        var entry = await ResolveEntryAsync(Normalize(path), cancellationToken).ConfigureAwait(false);
        await SendAsync(HttpMethod.Delete, $"/file-entries/{entry.Id}/shareable-link", [], null, cancellationToken, surfaceServerMessage: true).ConfigureAwait(false);
    }

    /// <summary>Recipients open <c>/drive/shares/{hash}</c>. The password comes back hashed, so it only says whether there is one.</summary>
    private static CloudShareLink? ShareLink(JsonElement root)
    {
        if (!root.TryGetProperty("link", out var link) || link.ValueKind != JsonValueKind.Object ||
            DrimeJson.String(link, "hash") is not { Length: > 0 } hash ||
            !Uri.TryCreate(ShareBaseUrl + hash, UriKind.Absolute, out var url))
        {
            return null;
        }

        return new CloudShareLink(url)
        {
            ExpiresAt = DrimeJson.ParseTimestamp(DrimeJson.String(link, "expires_at")),
            HasPassword = DrimeJson.String(link, "password") is { Length: > 0 },
        };
    }

    // MARK: - Path resolution

    private async Task<long> ResolveFolderIdAsync(string path, CancellationToken cancellationToken)
    {
        path = Normalize(path);
        if (_pathIdCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var entry = await ResolveEntryAsync(path, cancellationToken).ConfigureAwait(false);
        return entry.IsFolder ? entry.Id : throw CloudProviderException.NotFound(path);
    }

    /// <summary>Walks the path from the root, listing each parent once and caching every child seen on the way.</summary>
    private async Task<DrimeEntry> ResolveEntryAsync(string path, CancellationToken cancellationToken)
    {
        if (path == "/")
        {
            return DrimeEntry.Root;
        }

        var parentPath = Parent(path);
        var parentId = await ResolveFolderIdAsync(parentPath, cancellationToken).ConfigureAwait(false);
        var name = Name(path);
        var children = await ListEntriesAsync(parentId, cancellationToken).ConfigureAwait(false);
        foreach (var child in children)
        {
            _pathIdCache[Join(parentPath, child.Name)] = child.Id;
        }

        var match = children.FirstOrDefault(c => c.Name == name);
        if (match is null)
        {
            _pathIdCache.RemovePathSubtree(path);
            throw CloudProviderException.NotFound(path);
        }

        return match;
    }

    /// <summary>"/a//b/" → "/a/b". Only "/" separates: a backslash is an ordinary character in a Drime name.</summary>
    internal static string Normalize(string path) => "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));

    internal static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    internal static string Join(string parent, string name) => parent == "/" ? "/" + name : parent + "/" + name;

    // MARK: - HTTP

    /// <summary>
    /// One authorized JSON request; returns the body. Refreshes ahead of expiry, and once more
    /// on a 401 (OAuth only — a personal token that gets a 401 was deleted). A 429 is retried
    /// after a pause for any method, since Drime didn't act on it; 5xx only for GET, where
    /// repeating can't duplicate anything.
    /// </summary>
    private async Task<string> SendAsync(
        HttpMethod method,
        string path,
        (string Name, string Value)[] query,
        JsonObject? body,
        CancellationToken cancellationToken,
        (string Name, string Value)[]? headers = null,
        bool surfaceServerMessage = false)
    {
        var url = ApiUrl + path;
        if (query.Length > 0)
        {
            url += "?" + string.Join('&', query.Select(q => $"{CloudHttp.Escape(q.Name)}={CloudHttp.Escape(q.Value)}"));
        }

        async Task<(int Status, string Body)> AttemptAsync(bool forceRefresh)
        {
            var credentials = forceRefresh
                ? await ForceRefreshAsync(cancellationToken).ConfigureAwait(false)
                : await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = CloudHttp.Bearer(credentials.AccessToken);
            request.Headers.Accept.ParseAdd("application/json");
            foreach (var (name, value) in headers ?? [])
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            if (body is not null)
            {
                request.Content = JsonBody(body);
            }

            using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        var delay = TimeSpan.FromSeconds(1);
        var refreshed = false;
        for (var tries = 1; ; tries++)
        {
            var (status, text) = await AttemptAsync(false).ConfigureAwait(false);
            if (status == 401 && Credentials.Kind == DrimeCredentialKind.OAuth && !refreshed)
            {
                refreshed = true;
                SupportLogger.Shared.Log($"[Drime] {method} {path} → 401, refreshing and retrying once", LogCategory);
                (status, text) = await AttemptAsync(true).ConfigureAwait(false);
            }

            if (status is >= 200 and < 300)
            {
                return text;
            }

            var retryable = status == 429 || (method == HttpMethod.Get && RetryableGetStatuses.Contains(status));
            if (retryable && tries < MaxAttempts)
            {
                await Delay(delay, cancellationToken).ConfigureAwait(false);
                delay *= 2;
                continue;
            }

            SupportLogger.Shared.Log($"[Drime] {method} {path} → HTTP {status}: {Prefix(text, 300)}", LogCategory, LogLevel.Error);
            if (surfaceServerMessage && status != 401 && DrimeJson.ServerMessage(text) is { } message)
            {
                throw CloudProviderException.CommandFailed(message);
            }

            throw DrimeJson.MapHttpError(status, text);
        }
    }

    private static StringContent JsonBody(JsonObject body) => new(body.ToJsonString(WriteOptions), Encoding.UTF8, "application/json");

    /// <summary>The root is spelled by leaving the parent out.</summary>
    private static void AddParent(JsonObject body, string key, long parentId)
    {
        if (parentId != 0)
        {
            body[key] = parentId;
        }
    }

    private static void AddIfPresent(JsonObject body, string key, string? value)
    {
        if (value is not null)
        {
            body[key] = value;
        }
    }

    private static DrimeEntry FileEntry(string body)
        => ParseJson(body, root => root.TryGetProperty("fileEntry", out var entry) ? DrimeEntry.Parse(entry) : throw CloudProviderException.InvalidResponse());

    private static T ParseJson<T>(string body, Func<JsonElement, T> read)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return read(document.RootElement);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    private static string MimeType(string name) => S3MimeTypes.For(name) ?? "application/octet-stream";

    private static string Prefix(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>Forwards byte progress for one part without counting resent bytes twice.</summary>
    private sealed class PartProgress(ByteProgress? onBytes)
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
                onBytes!(forward);
            }
        }
    }
}
