// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers.WebDav;

namespace FileFluss.Core.Providers.Koofr;

/// <summary>Stored for a Koofr account (the macOS <c>KoofrCredentials</c>, same JSON keys).</summary>
internal sealed record KoofrCredentials(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("appPassword")] string AppPassword,
    [property: JsonPropertyName("primaryMountId")] string PrimaryMountId,
    [property: JsonPropertyName("displayName")] string DisplayName);

/// <summary>
/// Koofr's REST API (<c>app.koofr.net/api/v2</c>, content under <c>/content/api/v2</c>) with
/// an app password over HTTP Basic. Everything is addressed by path within the primary
/// mount, so there is no id cache to maintain.
/// </summary>
internal sealed class KoofrApiClient
{
    internal const string ApiBase = "https://app.koofr.net/api/v2";
    internal const string ContentBase = "https://app.koofr.net/content/api/v2";
    internal const string LogCategory = "Koofr";

    private readonly HttpClient _http;
    private readonly AuthenticationHeaderValue _auth;

    public KoofrApiClient(HttpClient http, KoofrCredentials credentials)
    {
        _http = http;
        Credentials = credentials;
        _auth = CloudHttp.Basic(credentials.Email, credentials.AppPassword);
    }

    public KoofrCredentials Credentials { get; }

    private string MountId => CloudHttp.Escape(Credentials.PrimaryMountId);

    // MARK: - Authentication

    /// <summary>Verifies the app password via <c>/user</c> and picks the primary mount (or the first).</summary>
    public static async Task<KoofrCredentials> AuthenticateAsync(HttpClient http, string email, string appPassword, CancellationToken cancellationToken)
    {
        var auth = CloudHttp.Basic(email, appPassword);

        KoofrUser user;
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/user") { Headers = { Authorization = auth } })
        using (var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false))
        {
            var status = (int)response.StatusCode;
            if (status == 401)
            {
                throw CloudProviderException.InvalidCredentials();
            }

            if (!WebDavHttp.IsSuccess(response))
            {
                SupportLogger.Shared.Log($"User info failed: HTTP {status}", LogCategory, LogLevel.Error);
                throw CloudProviderException.ServerError(status);
            }

            user = await response.ReadJsonAsync<KoofrUser>(cancellationToken).ConfigureAwait(false);
        }

        var fullName = $"{user.FirstName} {user.LastName}".Trim();
        var displayName = fullName.Length > 0 ? fullName : user.Email ?? email;

        KoofrMounts mounts;
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/mounts") { Headers = { Authorization = auth } })
        using (var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (!WebDavHttp.IsSuccess(response))
            {
                throw CloudProviderException.InvalidResponse();
            }

            mounts = await response.ReadJsonAsync<KoofrMounts>(cancellationToken).ConfigureAwait(false);
        }

        var primary = mounts.Mounts?.FirstOrDefault(m => m.IsPrimary) ?? mounts.Mounts?.FirstOrDefault()
            ?? throw CloudProviderException.NotFound("No mount found");

        SupportLogger.Shared.Log($"Authenticated as {displayName}, mount: {primary.Id}", LogCategory);
        return new KoofrCredentials(email, appPassword, primary.Id, displayName);
    }

    /// <summary>
    /// <c>/mounts/{id}</c> carries <c>spaceTotal</c>/<c>spaceUsed</c> for the primary mount.
    /// A total of 0 (disabled mounts, rarely) reports used only.
    /// </summary>
    public async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken)
    {
        var mount = await GetJsonAsync<KoofrMountDetail>($"/mounts/{MountId}", null, null, cancellationToken).ConfigureAwait(false);
        return new CloudStorageQuota(mount.SpaceUsed ?? 0, mount.SpaceTotal is > 0 ? mount.SpaceTotal : null);
    }

    // MARK: - Files

    public async Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        var folder = CloudPath.Normalize(path);
        var response = await GetJsonAsync<KoofrFiles>($"/mounts/{MountId}/files/list", $"path={CloudHttp.Escape(folder)}", folder, cancellationToken).ConfigureAwait(false);
        return (response.Files ?? []).Select(f => f.ToItem(folder)).ToList();
    }

    public async Task<CloudFileItem> GetFileInfoAsync(string path, CancellationToken cancellationToken)
    {
        var target = CloudPath.Normalize(path);
        var info = await GetJsonAsync<KoofrFileInfo>($"/mounts/{MountId}/files/info", $"path={CloudHttp.Escape(target)}", target, cancellationToken).ConfigureAwait(false);
        return info.ToItem(CloudPath.Parent(target), fallbackPath: target);
    }

    public async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var path = CloudPath.Normalize(remotePath);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ContentBase}/mounts/{MountId}/files/get?path={CloudHttp.Escape(path)}")
        {
            Headers = { Authorization = _auth },
        };
        using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        if (!WebDavHttp.IsSuccess(response))
        {
            throw WebDavHttp.MapStatus((int)response.StatusCode, path);
        }

        await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Multipart POST to <c>files/put</c>, overwriting an existing file. <c>modified</c> (ms
    /// since the epoch) keeps the local modification time.
    /// </summary>
    public async Task UploadFileAsync(string localPath, string folderPath, string fileName, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var folder = CloudPath.Normalize(folderPath);
        var url = $"{ContentBase}/mounts/{MountId}/files/put?path={CloudHttp.Escape(folder)}&filename={CloudHttp.Escape(fileName)}&autorename=false&overwrite=true";
        if (WebDavHttp.LocalModificationTime(localPath) is { } modified)
        {
            url += $"&modified={modified.ToUnixTimeMilliseconds()}";
        }

        using var content = new MultipartFormDataContent(Guid.NewGuid().ToString());
        content.Add(ProgressStreamContent.FromFile(localPath, onBytes), "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Headers = { Authorization = _auth }, Content = content };
        using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        if (!WebDavHttp.IsSuccess(response))
        {
            var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
            SupportLogger.Shared.Log($"Upload failed: HTTP {(int)response.StatusCode}: {body}", LogCategory, LogLevel.Error);
            throw WebDavHttp.MapStatus((int)response.StatusCode, CloudPath.Combine(folder, fileName));
        }
    }

    public async Task DeleteItemAsync(string path, CancellationToken cancellationToken)
    {
        var target = CloudPath.Normalize(path);
        using var response = await SendAsync(HttpMethod.Delete, $"/mounts/{MountId}/files/remove", $"path={CloudHttp.Escape(target)}", null, target, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Idempotent: an existing folder (by <c>files/info</c>) is success without a request.</summary>
    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        var target = CloudPath.Normalize(path);
        try
        {
            await GetFileInfoAsync(target, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (CloudProviderException)
        {
            // Not there (or not answerable): create it.
        }

        var parent = CloudPath.Parent(target);
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/mounts/{MountId}/files/folder",
            $"path={CloudHttp.Escape(parent)}",
            new { name = CloudPath.Name(target) },
            target,
            false,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken)
    {
        var target = CloudPath.Normalize(path);
        using var response = await SendAsync(
            HttpMethod.Put,
            $"/mounts/{MountId}/files/rename",
            $"path={CloudHttp.Escape(target)}",
            new { name = newName },
            target,
            false,
            cancellationToken).ConfigureAwait(false);
    }

    // MARK: - Share links
    //
    // Koofr's published API reference doesn't document link sharing. These are the endpoints
    // rclone's "koofr" backend has shipped for years, so they're observed behaviour rather
    // than a contract:
    //
    //   POST   /mounts/{mountId}/links        {"path":"/full/path"}  → link
    //   GET    /mounts/{mountId}/links                               → all links
    //   DELETE /mounts/{mountId}/links/{id}                          → unshare
    //
    // No create-time password, expiry or download toggle: the body carries the path and
    // nothing else. `PUT …/links/{id}` appears to exist but its fields are unverified, so
    // updating a link is deliberately not offered.

    /// <summary>Creates a public link. Options are ignored — the API takes none, and the capabilities say so.</summary>
    public async Task<CloudShareLink> CreateShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/mounts/{MountId}/links", null, new { path }, path, true, cancellationToken).ConfigureAwait(false);
        var link = await response.ReadJsonAsync<KoofrLink>(cancellationToken).ConfigureAwait(false);
        return await ShareLinkFromAsync(link, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Koofr has no per-path lookup: the whole link list is fetched and matched on path. No
    /// match is the "not shared" answer; an HTTP failure still throws, so a refused request
    /// never reads as "this file isn't shared".
    /// </summary>
    public async Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken)
        => await FindLinkAsync(path, cancellationToken).ConfigureAwait(false) is { } link
            ? await ShareLinkFromAsync(link, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>Withdraws the link; no link is not an error — "not shared" already holds.</summary>
    public async Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        if (await FindLinkAsync(path, cancellationToken).ConfigureAwait(false) is not { } link)
        {
            SupportLogger.Shared.Log("removeShareLink: no link on that path, nothing to withdraw", LogCategory);
            return;
        }

        using var response = await SendAsync(HttpMethod.Delete, $"/mounts/{MountId}/links/{CloudHttp.Escape(link.Id ?? string.Empty)}", null, null, path, true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<KoofrLink?> FindLinkAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/mounts/{MountId}/links", null, null, path, true, cancellationToken).ConfigureAwait(false);
        var links = ParseLinks(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var wanted = NormalizedLinkPath(path);
        return links.FirstOrDefault(l => l.Path is not null && NormalizedLinkPath(l.Path) == wanted);
    }

    /// <summary>
    /// <c>{"links":[…]}</c>, or a bare array since the endpoint is undocumented. Anything else
    /// throws rather than reading as an empty list, which would masquerade as "not shared".
    /// </summary>
    internal static IReadOnlyList<KoofrLink> ParseLinks(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var array = root.ValueKind switch
            {
                JsonValueKind.Object when root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array => links,
                JsonValueKind.Array => root,
                _ => throw CloudProviderException.InvalidResponse(),
            };
            return array.Deserialize<List<KoofrLink>>(CloudHttp.Json) ?? [];
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    private async Task<CloudShareLink> ShareLinkFromAsync(KoofrLink link, CancellationToken cancellationToken)
    {
        // `shortUrl` is the same landing page behind a shorter host; prefer the canonical
        // `url` and fall back only when it's unusable.
        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var landing) && !Uri.TryCreate(link.ShortUrl, UriKind.Absolute, out landing))
        {
            throw CloudProviderException.InvalidResponse();
        }

        return new CloudShareLink(landing)
        {
            DirectDownloadUrl = await DirectContentUrlAsync(link, cancellationToken).ConfigureAwait(false),
            ExpiresAt = link.ValidTo is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(link.ValidTo.Value) : null,

            // Nothing here sets a password, but a link made in Koofr's web UI can carry one —
            // report what the server says, not what we asked.
            HasPassword = link.HasPassword ?? false,
        };
    }

    /// <summary>
    /// The direct-content URL of a link: the landing URL with <c>/links</c> swapped for
    /// <c>/content/links</c> plus the file-fetch suffix, whose <c>path</c> must be exactly
    /// <c>%2F</c>. Per rclone this serves the file itself only for an item in the mount root;
    /// deeper in the tree Koofr answers with a ZIP holding the single file, and a folder link
    /// is a ZIP by definition — both get no direct URL, since handing someone a ZIP when they
    /// expect the file is worse than the landing page.
    /// </summary>
    private async Task<Uri?> DirectContentUrlAsync(KoofrLink link, CancellationToken cancellationToken)
    {
        if (link.Path is null || link.Url is null)
        {
            return null;
        }

        var path = NormalizedLinkPath(link.Path);
        if (CloudPath.Parent(path) != "/" || path == "/")
        {
            return null;
        }

        // One extra request, and only for root-level items. Anything we can't confirm to be
        // a file stays null rather than risking the ZIP.
        try
        {
            if ((await GetFileInfoAsync(path, cancellationToken).ConfigureAwait(false)).IsDirectory)
            {
                return null;
            }
        }
        catch (CloudProviderException)
        {
            return null;
        }

        var index = link.Url.IndexOf("/links", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var content = string.Concat(link.Url.AsSpan(0, index), "/content/links", link.Url.AsSpan(index + "/links".Length));
        return Uri.TryCreate(content + "/files/get?path=%2F", UriKind.Absolute, out var direct) ? direct : null;
    }

    /// <summary>Link paths come back as submitted; compared normalised so a trailing or missing slash can't hide a link.</summary>
    internal static string NormalizedLinkPath(string path)
    {
        var p = path.StartsWith('/') ? path : "/" + path;
        while (p.Length > 1 && p.EndsWith('/'))
        {
            p = p[..^1];
        }

        return p;
    }

    // MARK: - HTTP

    private async Task<T> GetJsonAsync<T>(string path, string? query, string? itemPath, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, query, null, itemPath, false, cancellationToken).ConfigureAwait(false);
        return await response.ReadJsonAsync<T>(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One API call; throws on a non-2xx status. With <paramref name="surfaceServerMessage"/>
    /// (the sharing path, where the only useful explanation — "link sharing isn't available on
    /// this plan" — is server-side) a failure body's message becomes the error text; other
    /// callers keep the coarse status mapping.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? query, object? jsonBody, string? itemPath, bool surfaceServerMessage, CancellationToken cancellationToken)
    {
        var url = query is null ? $"{ApiBase}{path}" : $"{ApiBase}{path}?{query}";
        using var request = new HttpRequestMessage(method, url) { Headers = { Authorization = _auth } };
        if (jsonBody is not null)
        {
            request.Content = CloudHttp.JsonContent(jsonBody);
        }

        var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (WebDavHttp.IsSuccess(response))
        {
            return response;
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            SupportLogger.Shared.Log($"{method.Method} {path} → HTTP {status}: {(body.Length > 500 ? body[..500] : body)}", LogCategory, LogLevel.Error);
            if (surfaceServerMessage && ServerMessage(body) is { } message)
            {
                throw CloudProviderException.CommandFailed(message);
            }

            throw WebDavHttp.MapStatus(status, itemPath);
        }
    }

    /// <summary>
    /// Koofr's failure bodies are JSON of undocumented shape: the text has been seen at the
    /// top level (<c>{"error":"…"}</c>, <c>{"message":"…"}</c>) and nested
    /// (<c>{"error":{"message":"…"}}</c>). All are tried.
    /// </summary>
    internal static string? ServerMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (NonEmptyString(root, "message") is { } message)
            {
                return message;
            }

            if (NonEmptyString(root, "error") is { } error)
            {
                return error;
            }

            if (root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                return NonEmptyString(nested, "message") ?? NonEmptyString(nested, "code");
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? NonEmptyString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}

internal sealed record KoofrUser(string? Id, string? FirstName, string? LastName, string? Email);

internal sealed record KoofrMounts(List<KoofrMount>? Mounts);

internal sealed record KoofrMount(string Id, string? Name, bool IsPrimary);

internal sealed record KoofrMountDetail(long? SpaceTotal, long? SpaceUsed);

internal sealed record KoofrFiles(List<KoofrFileInfo>? Files);

/// <summary>A listing entry. <c>modified</c> is milliseconds since the epoch; <c>type</c> is "file" or "dir".</summary>
internal sealed record KoofrFileInfo(string? Name, string? Type, long Modified, long Size, string? ContentType, string? Hash)
{
    public bool IsDirectory => Type == "dir";

    public CloudFileItem ToItem(string parentPath, string? fallbackPath = null)
    {
        var name = Name ?? string.Empty;
        var path = name.Length > 0 ? CloudPath.Combine(parentPath, name) : fallbackPath ?? parentPath;
        var displayName = name.Length > 0 ? name : CloudPath.Name(path);
        return new CloudFileItem(path, displayName, path, IsDirectory, Size, DateTimeOffset.FromUnixTimeMilliseconds(Modified), Hash)
        {
            IsHidden = CloudFileItem.IsDotHidden(displayName),
        };
    }
}

/// <summary>
/// One entry of the link list, and what <c>POST …/links</c> answers. Everything is optional:
/// the shape is undocumented and a missing field must not sink the lookup.
/// <c>validTo</c> is milliseconds since the epoch; absent or 0 never expires.
/// </summary>
internal sealed record KoofrLink(string? Id, string? Path, string? Url, string? ShortUrl, bool? HasPassword, long? ValidTo);
