// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers.WebDav;

namespace FileFluss.Core.Providers.KDrive;

/// <summary>Stored for a kDrive account (the macOS <c>KDriveCredentials</c>, same JSON keys).</summary>
/// <param name="UserEmail">Despite the name, the display name when the profile has one (as on macOS).</param>
internal sealed record KDriveCredentials(
    [property: JsonPropertyName("apiToken")] string ApiToken,
    [property: JsonPropertyName("driveId")] long DriveId,
    [property: JsonPropertyName("userEmail")] string UserEmail);

/// <summary>
/// Infomaniak's kDrive API (<c>api.infomaniak.com</c>, v2 for metadata, v3 for uploads) with
/// a personal API token. Items are addressed by numeric file id, so paths are resolved
/// component by component and cached; the cache is pruned on delete/rename (whole subtree)
/// and after every completed listing.
/// </summary>
internal sealed class KDriveApiClient
{
    internal const string ApiBase = "https://api.infomaniak.com";
    internal const string LogCategory = "kDrive";

    private readonly HttpClient _http;
    private readonly AuthenticationHeaderValue _auth;
    private readonly ConcurrentDictionary<string, long> _pathToId = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _rootGate = new(1, 1);

    public KDriveApiClient(HttpClient http, KDriveCredentials credentials)
    {
        _http = http;
        Credentials = credentials;
        _auth = CloudHttp.Bearer(credentials.ApiToken);
    }

    public KDriveCredentials Credentials { get; }

    private long DriveId => Credentials.DriveId;

    /// <summary>
    /// kDrive's default <c>per_page</c> on the listing endpoint is small (10), so a single
    /// unpaginated call silently truncates larger folders. Pages are fetched until one comes
    /// back short. Settable for tests.
    /// </summary>
    internal int PageSize { get; set; } = 500;

    /// <summary>Waits between upload retries; settable so tests don't sleep.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    // MARK: - Drive info

    /// <summary>
    /// The root folder's file id: <c>root_file_id</c> of the drive (1 when the field is
    /// missing). If the drive lookup fails, id 1 — the common default — is verified by
    /// listing it.
    /// </summary>
    public async Task<long> FetchRootFileIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var document = await GetDataAsync($"/2/drive/{DriveId}", null, null, cancellationToken).ConfigureAwait(false);
            var data = document.RootElement.GetProperty("data");
            return data.TryGetProperty("root_file_id", out var root) && root.TryGetInt64(out var id) ? id : 1;
        }
        catch (Exception e) when (e is CloudProviderException or KeyNotFoundException or InvalidOperationException)
        {
            using var _ = await GetDataAsync($"/2/drive/{DriveId}/files/1/files", null, null, cancellationToken).ConfigureAwait(false);
            return 1;
        }
    }

    public void SetRootId(long id) => _pathToId["/"] = id;

    /// <summary>
    /// The root id, looked up once (single-flight) on first use. macOS kicked the lookup off
    /// in the background at launch and used a placeholder 0 until it landed — or forever when
    /// it failed; here a failed lookup is retried by the next call instead.
    /// </summary>
    private async Task<long> RootIdAsync(CancellationToken cancellationToken)
    {
        if (_pathToId.TryGetValue("/", out var root))
        {
            return root;
        }

        await _rootGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pathToId.TryGetValue("/", out root))
            {
                return root;
            }

            root = await FetchRootFileIdAsync(cancellationToken).ConfigureAwait(false);
            SetRootId(root);
            return root;
        }
        finally
        {
            _rootGate.Release();
        }
    }

    /// <summary>
    /// <c>/2/drive/{id}</c>: <c>size</c> is the allotted bytes, <c>used_size</c> the consumed
    /// ones. A 0 total means the API didn't surface it; the bar then shows used only.
    /// </summary>
    public async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken)
    {
        using var document = await GetDataAsync($"/2/drive/{DriveId}", null, null, cancellationToken).ConfigureAwait(false);
        var data = document.RootElement.TryGetProperty("data", out var d) ? d : default;
        var size = LongOf(data, "size");
        return new CloudStorageQuota(LongOf(data, "used_size") ?? 0, size is > 0 ? size : null);
    }

    // MARK: - Path resolution

    /// <summary>Resolves a path to a file id, walking from the deepest cached ancestor.</summary>
    public async Task<long> ResolvePathToIdAsync(string path, CancellationToken cancellationToken)
    {
        var target = CloudPath.Normalize(path);
        if (_pathToId.TryGetValue(target, out var cached))
        {
            return cached;
        }

        var currentId = await RootIdAsync(cancellationToken).ConfigureAwait(false);
        var currentPath = string.Empty;
        foreach (var component in CloudPath.Components(target))
        {
            currentPath += "/" + component;
            if (_pathToId.TryGetValue(currentPath, out cached))
            {
                currentId = cached;
                continue;
            }

            var children = await ListFolderAsync(currentId, cancellationToken).ConfigureAwait(false);
            var match = children.FirstOrDefault(c => c.Name == component) ?? throw CloudProviderException.NotFound(currentPath);
            currentId = match.Id;
            _pathToId[currentPath] = currentId;
        }

        return currentId;
    }

    public void CachePath(string path, long fileId) => _pathToId[CloudPath.Normalize(path)] = fileId;

    /// <summary>A completed listing is authoritative for its level: vanished children leave the cache with their subtree.</summary>
    public void RetainChildren(string path, ISet<string> names) => _pathToId.RetainPathChildren(CloudPath.Normalize(path), names);

    public void EvictSubtree(string path) => _pathToId.RemovePathSubtree(CloudPath.Normalize(path));

    // MARK: - Folders

    public async Task<List<KDriveFile>> ListFolderAsync(long fileId, CancellationToken cancellationToken)
    {
        var all = new List<KDriveFile>();
        for (var page = 1; ; page++)
        {
            var query = $"order_by=name&order=asc&per_page={PageSize}&page={page}";
            using var document = await GetDataAsync($"/2/drive/{DriveId}/files/{fileId}/files", query, null, cancellationToken).ConfigureAwait(false);
            var items = document.RootElement.GetProperty("data").Deserialize<List<KDriveFile>>(CloudHttp.Json) ?? [];
            all.AddRange(items);
            if (items.Count < PageSize)
            {
                return all;
            }
        }
    }

    public async Task CreateFolderAsync(long parentId, string name, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/2/drive/{DriveId}/files/{parentId}/directory", null, CloudHttp.JsonContent(new { name }), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"POST directory in {parentId}", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Idempotent: a path that already resolves is left alone; the new folder's id is cached.</summary>
    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        var target = CloudPath.Normalize(path);
        try
        {
            await ResolvePathToIdAsync(target, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (CloudProviderException)
        {
            // Not there: create it.
        }

        var name = CloudPath.Name(target);
        var parentId = await ResolvePathToIdAsync(CloudPath.Parent(target), cancellationToken).ConfigureAwait(false);
        await CreateFolderAsync(parentId, name, cancellationToken).ConfigureAwait(false);

        // The create call doesn't hand back an id we trust; read the parent to learn it.
        var created = (await ListFolderAsync(parentId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(c => c.Name == name);
        if (created is not null)
        {
            CachePath(target, created.Id);
        }
    }

    // MARK: - Files

    public async Task DownloadFileAsync(long fileId, string localPath, ByteProgress? onBytes, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/2/drive/{DriveId}/files/{fileId}/download") { Headers = { Authorization = _auth } };
        using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        if (!WebDavHttp.IsSuccess(response))
        {
            throw WebDavHttp.MapStatus((int)response.StatusCode, path);
        }

        await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One-shot upload to the v3 upload service. <c>conflict=version</c> replaces an existing
    /// file (keeping a version); the local modification and creation times are passed along
    /// so sync diffs stay stable.
    ///
    /// <para>The upload service is a separate subsystem from the metadata API: a folder that
    /// was just created lists immediately but the upload endpoint can 404 for about a second.
    /// So a 404 is retried up to three times with a growing pause. Progress of a retried
    /// attempt isn't counted twice.</para>
    /// </summary>
    public async Task UploadFileAsync(string localPath, long folderId, string fileName, ByteProgress? onBytes, string path, CancellationToken cancellationToken)
    {
        var size = new FileInfo(localPath).Length;
        var query = new List<string>
        {
            $"directory_id={folderId}",
            $"file_name={CloudHttp.Escape(fileName)}",
            $"total_size={size.ToString(CultureInfo.InvariantCulture)}",
            "conflict=version",
        };
        if (WebDavHttp.LocalModificationTime(localPath) is { } modified)
        {
            query.Add($"last_modified_at={modified.ToUnixTimeSeconds()}");
        }

        if (WebDavHttp.LocalCreationTime(localPath) is { } created)
        {
            query.Add($"created_at={created.ToUnixTimeSeconds()}");
        }

        var url = $"{ApiBase}/3/drive/{DriveId}/upload?{string.Join('&', query)}";
        var progress = new HighWaterProgress(onBytes);
        for (var attempt = 0; ; attempt++)
        {
            SupportLogger.Shared.Log($"POST upload → folderId={folderId}, size={size}, attempt={attempt + 1}", LogCategory);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Headers = { Authorization = _auth },
                Content = ProgressStreamContent.FromFile(localPath, progress.NextAttempt()),
            };
            using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (WebDavHttp.IsSuccess(response))
            {
                return;
            }

            var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
            SupportLogger.Shared.Log($"POST upload → HTTP {status}: {body}", LogCategory, LogLevel.Error);
            if (status == 404 && attempt < 3)
            {
                await Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw WebDavHttp.MapStatus(status, path);
        }
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var target = CloudPath.Normalize(path);
        var fileId = await ResolvePathToIdAsync(target, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(HttpMethod.Delete, $"/2/drive/{DriveId}/files/{fileId}", null, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"DELETE files/{fileId}", target, cancellationToken).ConfigureAwait(false);

        // Everything below it is gone too.
        EvictSubtree(target);
    }

    public async Task RenameAsync(string path, string newName, CancellationToken cancellationToken)
    {
        var target = CloudPath.Normalize(path);
        var fileId = await ResolvePathToIdAsync(target, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(HttpMethod.Post, $"/2/drive/{DriveId}/files/{fileId}/rename", null, CloudHttp.JsonContent(new { name = newName }), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"POST rename {fileId}", target, cancellationToken).ConfigureAwait(false);

        // The old path and its subtree are gone; the new path names the same id.
        EvictSubtree(target);
        CachePath(CloudPath.Combine(CloudPath.Parent(target), newName), fileId);
    }

    public async Task<KDriveFile> StatAsync(long fileId, string path, CancellationToken cancellationToken)
    {
        using var document = await GetDataAsync($"/2/drive/{DriveId}/files/{fileId}", null, path, cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("data").Deserialize<KDriveFile>(CloudHttp.Json) ?? throw CloudProviderException.InvalidResponse();
    }

    /// <summary>Sums sizes by id, so the walk needs no path resolution.</summary>
    public async Task<long> FolderSizeAsync(long fileId, CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var item in await ListFolderAsync(fileId, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += item.IsFolder
                ? await FolderSizeAsync(item.Id, cancellationToken).ConfigureAwait(false)
                : item.Size ?? 0;
        }

        return total;
    }

    // MARK: - Server-side move / copy
    //
    // Not wired to the provider: `POST /files/{id}/move/{dir}` answered 404 against a live
    // account on macOS, and the shape Infomaniak accepts now isn't documented, so the provider
    // reports NotImplemented and callers fall back to download + upload. Kept so the fix is a
    // one-liner once the endpoint is confirmed.

    /// <summary>Moves into another folder, optionally renaming in the same call.</summary>
    internal async Task MoveAsync(long fileId, long toDirectoryId, string? newName, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/2/drive/{DriveId}/files/{fileId}/move/{toDirectoryId}",
            null,
            newName is null ? null : CloudHttp.JsonContent(new { name = newName }),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"POST move {fileId} → dir {toDirectoryId}", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Server-side copy via the <c>duplicate</c> endpoint.</summary>
    internal async Task DuplicateAsync(long fileId, long toDirectoryId, string? newName, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/2/drive/{DriveId}/files/{fileId}/duplicate/{toDirectoryId}",
            null,
            newName is null ? null : CloudHttp.JsonContent(new { name = newName }),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"POST duplicate {fileId} → dir {toDirectoryId}", null, cancellationToken).ConfigureAwait(false);
    }

    // MARK: - Share links

    /// <summary>
    /// <c>POST …/files/{id}/link</c>. Expiry is a paid kSuite feature: free workspaces reject
    /// <c>valid_until</c> with their own explanation, which is surfaced verbatim. Optional
    /// fields are left out entirely when unset, so <c>valid_until</c> never reaches a free
    /// tier that doesn't allow it.
    /// </summary>
    public async Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        var fileId = await ResolvePathToIdAsync(path, cancellationToken).ConfigureAwait(false);
        var password = string.IsNullOrEmpty(options.Password) ? null : options.Password;

        // `right` is "password" when one is set — the password alone doesn't switch the mode.
        var body = new JsonObject { ["right"] = password is null ? "public" : "password" };
        if (password is not null)
        {
            body["password"] = password;
        }

        if (options.Expiry is { } expiry)
        {
            // Infomaniak dates are Unix timestamps throughout this API.
            body["valid_until"] = expiry.ToUnixTimeSeconds();
        }

        body["can_download"] = options.AllowDownload;
        body["can_edit"] = false;

        using var response = await SendAsync(HttpMethod.Post, $"/2/drive/{DriveId}/files/{fileId}/link", null, JsonBody(body), cancellationToken).ConfigureAwait(false);
        await EnsureLinkSuccessAsync(response, $"POST files/{fileId}/link", path, cancellationToken).ConfigureAwait(false);

        var link = ParseLink(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
            ?? throw CloudProviderException.InvalidResponse();
        return ShareLinkFrom(link, requestedPassword: password is not null, requestedDownload: options.AllowDownload);
    }

    /// <summary>
    /// The link the file already has, or null. Infomaniak says "no link" as a 404 (or, on
    /// some deployments, 403/422 with a not-found code, or a 2xx whose data isn't a link) —
    /// that is the answer, not a failure. Every other failure carries the API's own message.
    /// </summary>
    public async Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        var fileId = await ResolvePathToIdAsync(path, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(HttpMethod.Get, $"/2/drive/{DriveId}/files/{fileId}/link", null, null, cancellationToken, acceptJson: true).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status == 404)
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!WebDavHttp.IsSuccess(response))
        {
            var error = ParseError(text);
            if (error?.Code?.Contains("not_found", StringComparison.Ordinal) == true)
            {
                return null;
            }

            SupportLogger.Shared.Log($"GET files/{fileId}/link → HTTP {status}: {Snippet(text)}", LogCategory, LogLevel.Error);
            throw error?.Message is { } message ? CloudProviderException.CommandFailed(message) : WebDavHttp.MapStatus(status, path);
        }

        if (ParseLink(text) is not { } link)
        {
            SupportLogger.Shared.Log($"GET files/{fileId}/link → 2xx without a link object, treating as not shared", LogCategory);
            return null;
        }

        return ShareLinkFrom(link, requestedPassword: false, requestedDownload: false);
    }

    /// <summary>
    /// <c>PUT …/link</c> changes password, expiry and download permission in place (same URL).
    /// PUT merges, so every field is sent explicitly — <c>valid_until: null</c> is how an
    /// expiry comes off and <c>password: null</c> with <c>right: "public"</c> how a password
    /// does. The mode must follow the password: <c>right: "password"</c> with a null password
    /// is rejected.
    /// </summary>
    public async Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        var fileId = await ResolvePathToIdAsync(path, cancellationToken).ConfigureAwait(false);
        var password = string.IsNullOrEmpty(options.Password) ? null : options.Password;
        var body = new JsonObject
        {
            ["right"] = password is null ? "public" : "password",
            ["password"] = password,
            ["valid_until"] = options.Expiry?.ToUnixTimeSeconds(),
            ["can_download"] = options.AllowDownload,
            ["can_edit"] = false,
        };

        using var response = await SendAsync(HttpMethod.Put, $"/2/drive/{DriveId}/files/{fileId}/link", null, JsonBody(body), cancellationToken).ConfigureAwait(false);
        await EnsureLinkSuccessAsync(response, $"PUT files/{fileId}/link", path, cancellationToken).ConfigureAwait(false);

        if (ParseLink(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) is { } link)
        {
            return ShareLinkFrom(link, requestedPassword: password is not null, requestedDownload: options.AllowDownload);
        }

        // Several Infomaniak write endpoints answer {"result":"success","data":true} instead
        // of echoing the object. Read the link back rather than assume the request applied.
        return await ExistingShareLinkAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw CloudProviderException.InvalidResponse();
    }

    /// <summary>Withdraws the link; "there was none" (404 / not-found code) is the outcome wanted, not a failure.</summary>
    public async Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        var fileId = await ResolvePathToIdAsync(path, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(HttpMethod.Delete, $"/2/drive/{DriveId}/files/{fileId}/link", null, null, cancellationToken, acceptJson: true).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status == 404 || WebDavHttp.IsSuccess(response))
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var error = ParseError(text);
        if (error?.Code?.Contains("not_found", StringComparison.Ordinal) == true)
        {
            return;
        }

        SupportLogger.Shared.Log($"DELETE files/{fileId}/link → HTTP {status}: {Snippet(text)}", LogCategory, LogLevel.Error);
        throw error?.Message is { } message ? CloudProviderException.CommandFailed(message) : WebDavHttp.MapStatus(status, path);
    }

    /// <summary>
    /// Infomaniak's link object → <see cref="CloudShareLink"/>. kDrive documents no
    /// direct-download variant: the link always lands on the viewer page.
    /// <paramref name="requestedPassword"/>/<paramref name="requestedDownload"/> describe what
    /// was asked for and only matter where the server leaves a field out.
    /// </summary>
    private static CloudShareLink ShareLinkFrom(KDriveShareLink link, bool requestedPassword, bool requestedDownload)
    {
        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var url))
        {
            throw CloudProviderException.InvalidResponse();
        }

        return new CloudShareLink(url)
        {
            ExpiresAt = link.ValidUntil is { } until ? DateTimeOffset.FromUnixTimeSeconds(until) : null,
            HasPassword = link.Right == "password" || requestedPassword,

            // `capabilities` reports what the link ended up permitting, which can be less
            // than asked for on restricted workspaces — worth telling the user.
            Note = requestedDownload && link.Capabilities?.CanDownload == false
                ? Localization.L("kDrive created the link without download permission — recipients can view the file only.")
                : null,
        };
    }

    /// <summary>The <c>data</c> link object, or null when <c>data</c> is anything else (<c>true</c>, <c>null</c>).</summary>
    internal static KDriveShareLink? ParseLink(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("data", out var data)
                   && data.ValueKind == JsonValueKind.Object
                   && data.TryGetProperty("url", out var url)
                   && url.ValueKind == JsonValueKind.String
                ? data.Deserialize<KDriveShareLink>(CloudHttp.Json)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>{"result":"error","error":{"code":…,"description":…}}</c>; some endpoints send
    /// <c>message</c> instead of <c>description</c>. The code is the last resort — terse
    /// (<c>validation_failed</c>) but more useful than a bare status.
    /// </summary>
    internal static KDriveError? ParseError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            static string? Text(JsonElement e, string name)
                => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var code = Text(error, "code");
            var message = new[] { Text(error, "description"), Text(error, "message"), code }.FirstOrDefault(s => !string.IsNullOrEmpty(s));
            return new KDriveError(code, message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // MARK: - HTTP

    private async Task<JsonDocument> GetDataAsync(string path, string? query, string? itemPath, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, query, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"GET {path}", itemPath, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("data", out _))
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

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? query, HttpContent? content, CancellationToken cancellationToken, bool acceptJson = false)
    {
        var url = query is null ? $"{ApiBase}{path}" : $"{ApiBase}{path}?{query}";
        using var request = new HttpRequestMessage(method, url) { Headers = { Authorization = _auth }, Content = content };
        if (acceptJson)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        return await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, string? itemPath, CancellationToken cancellationToken)
    {
        if (WebDavHttp.IsSuccess(response))
        {
            return;
        }

        var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
        SupportLogger.Shared.Log($"{operation} → HTTP {(int)response.StatusCode}: {body}", LogCategory, LogLevel.Error);
        throw WebDavHttp.MapStatus((int)response.StatusCode, itemPath);
    }

    /// <summary>Link writes surface the API's own message (paid-plan refusals) before the coarse status mapping.</summary>
    private static async Task EnsureLinkSuccessAsync(HttpResponseMessage response, string operation, string path, CancellationToken cancellationToken)
    {
        if (WebDavHttp.IsSuccess(response))
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        SupportLogger.Shared.Log($"{operation} → HTTP {(int)response.StatusCode}: {Snippet(text)}", LogCategory, LogLevel.Error);
        throw ParseError(text)?.Message is { } message
            ? CloudProviderException.CommandFailed(message)
            : WebDavHttp.MapStatus((int)response.StatusCode, path);
    }

    private static StringContent JsonBody(JsonObject body)
        => new(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    private static string Snippet(string text) => text.Length > 500 ? text[..500] : text;

    private static long? LongOf(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
            ? number
            : null;
}

/// <summary>A listing/stat entry. <c>last_modified_at</c> is Unix seconds; <c>type</c> is "file" or "dir".</summary>
internal sealed record KDriveFile(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("last_modified_at")] long? LastModifiedAt,
    [property: JsonPropertyName("created_at")] long? CreatedAt)
{
    public bool IsFolder => Type == "dir";

    public CloudFileItem ToItem(string parentPath)
    {
        var path = CloudPath.Combine(parentPath, Name);
        return new CloudFileItem(
            $"{(IsFolder ? "d" : "f")}{Id}",
            Name,
            path,
            IsFolder,
            Size ?? 0,
            LastModifiedAt is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : CloudFileItem.UnknownDate)
        {
            IsHidden = CloudFileItem.IsDotHidden(Name),
            CreationDate = CreatedAt is { } created ? DateTimeOffset.FromUnixTimeSeconds(created) : CloudFileItem.UnknownDate,
        };
    }
}

/// <summary>A drive (workspace) the token can reach.</summary>
internal sealed record KDriveDrive(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name);

/// <summary>
/// The link object: <c>valid_until</c> is null for a link that never expires, and
/// <c>capabilities</c> reports what the link actually permits.
/// </summary>
internal sealed record KDriveShareLink(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("right")] string? Right,
    [property: JsonPropertyName("valid_until")] long? ValidUntil,
    [property: JsonPropertyName("capabilities")] KDriveLinkCapabilities? Capabilities);

internal sealed record KDriveLinkCapabilities(
    [property: JsonPropertyName("can_edit")] bool? CanEdit,
    [property: JsonPropertyName("can_download")] bool? CanDownload);

internal sealed record KDriveError(string? Code, string? Message);
