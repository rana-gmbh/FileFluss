// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json.Serialization;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.WebDav;

/// <summary>Stored for WebDAV and GMX accounts (the macOS <c>WebDAVCredentials</c>, same JSON keys).</summary>
internal sealed record WebDavCredentials(
    [property: JsonPropertyName("serverURL")] string ServerUrl,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("displayName")] string DisplayName);

/// <summary>What differs between the servers that share <see cref="WebDavClient"/>.</summary>
/// <param name="PropfindBody">Properties asked for in listings; NextCloud adds <c>oc:checksums</c> and <c>oc:size</c>.</param>
/// <param name="MkcolTriesBothForms">
/// Generic WebDAV tries MKCOL with and without a trailing slash (servers insist on one or the
/// other) and treats 405/301 on both as "already exists". NextCloud sends one MKCOL and
/// treats 405 — which RFC 4918 specifies for an existing collection — as success.
/// </param>
internal sealed record WebDavDialect(string LogCategory, string PropfindBody, bool MkcolTriesBothForms)
{
    public const string PlainPropfindBody = """
        <?xml version="1.0" encoding="UTF-8"?>
        <d:propfind xmlns:d="DAV:">
            <d:prop>
                <d:getlastmodified/>
                <d:getcontentlength/>
                <d:getcontenttype/>
                <d:resourcetype/>
                <d:displayname/>
            </d:prop>
        </d:propfind>
        """;

    public static readonly WebDavDialect Generic = new("WebDAV", PlainPropfindBody, MkcolTriesBothForms: true);

    public static readonly WebDavDialect Gmx = new("GMXCloud", PlainPropfindBody, MkcolTriesBothForms: true);
}

/// <summary>
/// WebDAV over HTTP Basic auth — the macOS <c>WebDAVAPIClient</c>, and the file half of the
/// NextCloud client, which spoke the same protocol with a few dialect switches. Immutable;
/// a new sign-in builds a new instance on the provider's <see cref="HttpClient"/>.
/// </summary>
internal sealed class WebDavClient
{
    private const string QuotaPropfindBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <D:propfind xmlns:D="DAV:">
          <D:prop>
            <D:quota-available-bytes/>
            <D:quota-used-bytes/>
          </D:prop>
        </D:propfind>
        """;

    private readonly HttpClient _http;
    private readonly AuthenticationHeaderValue _auth;
    private readonly WebDavDialect _dialect;

    public WebDavClient(HttpClient http, string davBaseUrl, string username, string password, WebDavDialect dialect)
    {
        _http = http;
        _auth = CloudHttp.Basic(username, password);
        _dialect = dialect;
        DavBaseUrl = davBaseUrl.TrimEnd('/');
    }

    /// <summary>The DAV root without a trailing slash; every path is appended to it.</summary>
    public string DavBaseUrl { get; }

    // MARK: - Authentication

    /// <summary>
    /// Finds the DAV root behind what the user typed. The URL itself is tried first, then the
    /// usual sub-paths (<c>/webdav</c>, Nextcloud's <c>/remote.php/dav/files/&lt;user&gt;</c>,
    /// <c>/dav</c>). A 207 to PROPFIND marks a candidate; one whose OPTIONS lists MKCOL (i.e.
    /// writable) wins outright, otherwise the first 207 is the fallback. 401 means the
    /// password is wrong.
    /// </summary>
    public static async Task<WebDavCredentials> AuthenticateAsync(
        HttpClient http, string serverUrl, string username, string password, string logCategory, CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        var auth = CloudHttp.Basic(username, password);
        string[] candidates =
        [
            baseUrl,
            $"{baseUrl}/webdav",
            $"{baseUrl}/remote.php/dav/files/{CloudHttp.Escape(username)}",
            $"{baseUrl}/dav",
        ];

        string? working = null;
        var lastStatus = 0;
        CloudProviderException? lastNetworkError = null;

        foreach (var candidate in candidates)
        {
            if (!Uri.TryCreate(candidate + "/", UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            {
                continue;
            }

            HttpResponseMessage response;
            try
            {
                response = await WebDavHttp.SendAsync(
                    http,
                    url,
                    u => PropfindRequest(u, auth, "0", WebDavDialect.PlainPropfindBody),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NetworkError)
            {
                // Unreachable sub-path or host: try the next candidate.
                lastNetworkError = e;
                continue;
            }

            using (response)
            {
                lastStatus = (int)response.StatusCode;
                if (lastStatus == 401)
                {
                    // macOS throws here unconditionally. A sub-path probed after a working
                    // candidate was already found must not turn a good sign-in into
                    // "invalid credentials", so keep what we have.
                    if (working is not null)
                    {
                        break;
                    }

                    throw CloudProviderException.InvalidCredentials();
                }

                if (lastStatus == 207)
                {
                    if (await AllowsMkcolAsync(http, url, auth, cancellationToken).ConfigureAwait(false))
                    {
                        working = candidate;
                        break;
                    }

                    working ??= candidate;
                }
            }
        }

        if (working is null)
        {
            if (lastStatus == 401)
            {
                throw CloudProviderException.InvalidCredentials();
            }

            if (lastStatus == 0 && lastNetworkError is not null)
            {
                throw lastNetworkError;
            }

            throw CloudProviderException.ServerError(lastStatus);
        }

        SupportLogger.Shared.Log($"Authenticated against {working}", logCategory);
        return new WebDavCredentials(working, username, password, username);
    }

    /// <summary>Trims, drops a trailing slash and assumes https:// when no scheme was typed.</summary>
    public static string NormalizeServerUrl(string raw)
    {
        var url = raw.Trim().TrimEnd('/');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        return url;
    }

    private static async Task<bool> AllowsMkcolAsync(HttpClient http, Uri url, AuthenticationHeaderValue auth, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await WebDavHttp.SendAsync(
                http,
                url,
                u => new HttpRequestMessage(HttpMethod.Options, u) { Headers = { Authorization = auth } },
                cancellationToken).ConfigureAwait(false);
            var allow = response.Content.Headers.Allow.Concat(
                response.Headers.TryGetValues("Allow", out var raw) ? raw : []);
            return allow.Any(value => value.Contains("MKCOL", StringComparison.OrdinalIgnoreCase));
        }
        catch (CloudProviderException)
        {
            return false;
        }
    }

    // MARK: - Listing

    public async Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        var folder = CloudPath.Normalize(path);
        using var response = await SendAsync(
            BuildUri(folder),
            u => PropfindRequest(u, _auth, "1", _dialect.PropfindBody),
            cancellationToken,
            timeout: WebDavHttp.LongOperationTimeout).ConfigureAwait(false);

        if ((int)response.StatusCode != 207)
        {
            SupportLogger.Shared.Log($"PROPFIND {folder} → HTTP {(int)response.StatusCode}", _dialect.LogCategory, LogLevel.Error);
            throw WebDavHttp.MapStatus((int)response.StatusCode, folder);
        }

        var entries = await ParseAsync(response, cancellationToken).ConfigureAwait(false);
        return ListingItems(entries, folder);
    }

    /// <summary>
    /// A Depth-1 PROPFIND answers with the folder itself as well as its children. The folder's
    /// own entry is found by path, not by position (macOS #64: a Hetzner Storage Box with two
    /// folders showed one — the root's nameless entry was already gone, and dropping "the
    /// first" took a real folder instead). Nothing in the spec fixes the ordering either.
    ///
    /// <para>When no entry has the folder's path, nothing is removed: a server that doesn't
    /// list the folder itself must lose nothing. The one exception is a server that echoes
    /// the href in another form (a different base prefix): a first entry that is a folder
    /// whose path <i>ends</i> with the listed folder's is taken as its own entry. The root
    /// has no such fallback, so the #64 shape can never cost a child its place.</para>
    ///
    /// Children are placed under <paramref name="folder"/> by their href's last component,
    /// which stays right even when the server's hrefs don't start with the base path.
    /// </summary>
    internal static IReadOnlyList<CloudFileItem> ListingItems(List<WebDavEntry> entries, string folder)
    {
        var self = entries.FindIndex(e => SamePath(e.Path, folder));
        if (self < 0 && entries.Count > 0 && IsSameFolderUnderAnotherPrefix(entries[0], folder))
        {
            self = 0;
        }

        var items = new List<CloudFileItem>(Math.Max(0, entries.Count - 1));
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (i == self || entry.Name.Length == 0)
            {
                continue;
            }

            var component = CloudPath.Name(entry.Path);
            items.Add(entry.ToItem(CloudPath.Combine(folder, component.Length > 0 ? component : entry.Name), entry.Name));
        }

        return items;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(CloudPath.Normalize(a), CloudPath.Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameFolderUnderAnotherPrefix(WebDavEntry entry, string folder)
    {
        var target = CloudPath.Normalize(folder);
        if (!entry.IsDirectory || CloudPath.IsRoot(target))
        {
            return false;
        }

        // The target starts with "/", so a suffix match is always at a component boundary.
        var path = CloudPath.Normalize(entry.Path);
        return path.Length > target.Length && path.EndsWith(target, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<CloudFileItem> GetFileInfoAsync(string path, CancellationToken cancellationToken)
    {
        var target = CloudPath.Normalize(path);
        using var response = await SendAsync(
            BuildUri(target),
            u => PropfindRequest(u, _auth, "0", _dialect.PropfindBody),
            cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode != 207)
        {
            throw WebDavHttp.MapStatus((int)response.StatusCode, target);
        }

        var entries = await ParseAsync(response, cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            throw CloudProviderException.NotFound(target);
        }

        var entry = entries[0];
        var name = entry.Name.Length > 0 ? entry.Name : (CloudPath.IsRoot(target) ? "/" : CloudPath.Name(target));
        return entry.ToItem(target, name);
    }

    // MARK: - Transfers

    public async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var path = CloudPath.Normalize(remotePath);
        using var response = await SendAsync(
            BuildUri(path),
            u => new HttpRequestMessage(HttpMethod.Get, u) { Headers = { Authorization = _auth } },
            cancellationToken,
            transfer: true).ConfigureAwait(false);

        if (!WebDavHttp.IsSuccess(response))
        {
            throw WebDavHttp.MapStatus((int)response.StatusCode, path);
        }

        await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One streamed PUT. Many WebDAV servers (Nextcloud, ownCloud, Seafile, …) honour
    /// <c>X-OC-Mtime</c> to keep the client's modification time; the rest ignore it.
    /// </summary>
    public async Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var path = CloudPath.Normalize(remotePath);
        var mtime = WebDavHttp.UnixMtime(localPath);
        var progress = new HighWaterProgress(onBytes);

        using var response = await SendAsync(
            BuildUri(path),
            u =>
            {
                var request = new HttpRequestMessage(HttpMethod.Put, u)
                {
                    Headers = { Authorization = _auth },
                    Content = ProgressStreamContent.FromFile(localPath, progress.NextAttempt()),
                };
                if (mtime is { } seconds)
                {
                    request.Headers.TryAddWithoutValidation("X-OC-Mtime", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                return request;
            },
            cancellationToken,
            transfer: true).ConfigureAwait(false);

        if (!WebDavHttp.IsSuccess(response))
        {
            var status = (int)response.StatusCode;
            var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
            SupportLogger.Shared.Log($"Upload {path} failed: HTTP {status}: {body}", _dialect.LogCategory, LogLevel.Error);
            throw CloudProviderException.SizeLimitError(status, localPath) ?? WebDavHttp.MapStatus(status, path);
        }
    }

    // MARK: - Changes

    /// <summary>
    /// PROPPATCH of <c>getlastmodified</c>. RFC 4918 makes the property read-only, but
    /// Nextcloud/ownCloud and a few others honour it as an extension. Best-effort: an HTTP
    /// refusal is not reported; only a transport failure throws.
    /// </summary>
    public async Task SetModificationDateAsync(string remotePath, DateTimeOffset date, CancellationToken cancellationToken)
    {
        var path = CloudPath.Normalize(remotePath);
        var body = $"""
            <?xml version="1.0" encoding="utf-8" ?>
            <d:propertyupdate xmlns:d="DAV:">
              <d:set>
                <d:prop>
                  <d:getlastmodified>{WebDavResponseParser.FormatHttpDate(date)}</d:getlastmodified>
                </d:prop>
              </d:set>
            </d:propertyupdate>
            """;
        using var response = await SendAsync(
            BuildUri(path),
            u => new HttpRequestMessage(WebDavHttp.Proppatch, u)
            {
                Headers = { Authorization = _auth },
                Content = XmlContent(body),
            },
            cancellationToken).ConfigureAwait(false);
        if (!WebDavHttp.IsSuccess(response))
        {
            SupportLogger.Shared.Log($"PROPPATCH {path} → HTTP {(int)response.StatusCode} (ignored)", _dialect.LogCategory);
        }
    }

    public async Task DeleteItemAsync(string path, CancellationToken cancellationToken)
    {
        var target = CloudPath.Normalize(path);
        using var response = await SendAsync(
            BuildUri(target),
            u => new HttpRequestMessage(HttpMethod.Delete, u) { Headers = { Authorization = _auth } },
            cancellationToken,
            timeout: WebDavHttp.LongOperationTimeout).ConfigureAwait(false);

        if (!WebDavHttp.IsSuccess(response))
        {
            await LogFailureAsync("DELETE", target, response, cancellationToken).ConfigureAwait(false);
            throw WebDavHttp.MapStatus((int)response.StatusCode, target);
        }
    }

    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        var target = CloudPath.Normalize(path);
        var uri = BuildUri(target);
        Uri[] candidates = _dialect.MkcolTriesBothForms ? [uri, new Uri(uri.AbsoluteUri + "/")] : [uri];

        foreach (var candidate in candidates)
        {
            // Not following redirects: a 301 here is the server asking for the other form,
            // which is the next candidate anyway.
            using var response = await SendAsync(
                candidate,
                u => new HttpRequestMessage(WebDavHttp.Mkcol, u)
                {
                    Headers = { Authorization = _auth },
                    Content = new ByteArrayContent([]),
                },
                cancellationToken,
                followRedirects: false).ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (WebDavHttp.IsSuccess(response))
            {
                return;
            }

            if (_dialect.MkcolTriesBothForms && status is 405 or 301)
            {
                continue;
            }

            if (!_dialect.MkcolTriesBothForms && status == 405)
            {
                // RFC 4918: 405 to MKCOL means the collection already exists — idempotent success.
                SupportLogger.Shared.Log($"MKCOL {target} → HTTP 405 (folder already exists, treating as success)", _dialect.LogCategory);
                return;
            }

            await LogFailureAsync("MKCOL", target, response, cancellationToken).ConfigureAwait(false);
            throw WebDavHttp.MapStatus(status, target);
        }

        // Both forms answered 405/301: the folder is almost certainly there already.
    }

    public Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken)
    {
        var source = CloudPath.Normalize(path);
        return MoveOrCopyAsync(WebDavHttp.Move, source, CloudPath.Combine(CloudPath.Parent(source), newName), cancellationToken);
    }

    public Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken)
        => MoveOrCopyAsync(WebDavHttp.Move, CloudPath.Normalize(path), CloudPath.Normalize(newPath), cancellationToken);

    public Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken)
        => MoveOrCopyAsync(WebDavHttp.Copy, CloudPath.Normalize(path), CloudPath.Normalize(newPath), cancellationToken);

    /// <summary>Server-side MOVE/COPY. <c>Overwrite: F</c>: an existing target fails (412) rather than being replaced.</summary>
    private async Task MoveOrCopyAsync(HttpMethod method, string source, string destination, CancellationToken cancellationToken)
    {
        var destinationUrl = BuildUri(destination).AbsoluteUri;
        using var response = await SendAsync(
            BuildUri(source),
            u =>
            {
                var request = new HttpRequestMessage(method, u) { Headers = { Authorization = _auth } };
                request.Headers.TryAddWithoutValidation("Destination", destinationUrl);
                request.Headers.TryAddWithoutValidation("Overwrite", "F");
                return request;
            },
            cancellationToken,
            timeout: WebDavHttp.LongOperationTimeout).ConfigureAwait(false);

        if (!WebDavHttp.IsSuccess(response))
        {
            await LogFailureAsync($"{method.Method} → {destination}", source, response, cancellationToken).ConfigureAwait(false);
            throw WebDavHttp.MapStatus((int)response.StatusCode, source);
        }
    }

    // MARK: - Search and quota

    /// <summary>Client-side search: a recursive PROPFIND walk filtered by name (plain WebDAV has no search).</summary>
    public async Task<IReadOnlyList<CloudFileItem>> SearchByWalkingAsync(string query, string? path, CancellationToken cancellationToken)
    {
        var all = await ListAllRecursivelyAsync(CloudPath.Normalize(path ?? "/"), cancellationToken).ConfigureAwait(false);
        return all.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task<List<CloudFileItem>> ListAllRecursivelyAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = await ListFolderAsync(path, cancellationToken).ConfigureAwait(false);
        var result = new List<CloudFileItem>(items);
        foreach (var item in items.Where(i => i.IsDirectory))
        {
            result.AddRange(await ListAllRecursivelyAsync(item.Path, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>
    /// Nextcloud's DAV <c>SEARCH</c> (basicsearch on displayname, depth infinity, 100 hits).
    /// The query and scope are XML-escaped (macOS interpolated them raw, so a "&amp;" in the
    /// query broke the request).
    /// </summary>
    public async Task<IReadOnlyList<CloudFileItem>> SearchAsync(string query, string? path, CancellationToken cancellationToken)
    {
        var searchPath = CloudPath.Normalize(path ?? "/");
        var uri = BuildUri(searchPath);
        var body = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <d:searchrequest xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns" xmlns:nc="http://nextcloud.org/ns">
                <d:basicsearch>
                    <d:select>
                        <d:prop>
                            <d:getlastmodified/>
                            <d:getcontentlength/>
                            <d:getcontenttype/>
                            <d:resourcetype/>
                            <d:displayname/>
                            <oc:checksums/>
                            <oc:size/>
                        </d:prop>
                    </d:select>
                    <d:from>
                        <d:scope>
                            <d:href>{SecurityElement.Escape(uri.AbsoluteUri)}</d:href>
                            <d:depth>infinity</d:depth>
                        </d:scope>
                    </d:from>
                    <d:where>
                        <d:like>
                            <d:prop><d:displayname/></d:prop>
                            <d:literal>%{SecurityElement.Escape(query)}%</d:literal>
                        </d:like>
                    </d:where>
                    <d:limit>
                        <d:nresults>100</d:nresults>
                    </d:limit>
                </d:basicsearch>
            </d:searchrequest>
            """;

        using var response = await SendAsync(
            uri,
            u => new HttpRequestMessage(WebDavHttp.Search, u)
            {
                Headers = { Authorization = _auth },
                Content = XmlContent(body),
            },
            cancellationToken,
            timeout: WebDavHttp.LongOperationTimeout).ConfigureAwait(false);

        if ((int)response.StatusCode != 207)
        {
            SupportLogger.Shared.Log($"SEARCH {searchPath} → HTTP {(int)response.StatusCode}", _dialect.LogCategory, LogLevel.Error);
            throw WebDavHttp.MapStatus((int)response.StatusCode, searchPath);
        }

        var entries = await ParseAsync(response, cancellationToken).ConfigureAwait(false);
        return entries.Where(e => e.Name.Length > 0).Select(e => e.ToItem()).ToList();
    }

    /// <summary>RFC 4331 quota on the DAV root; null when the server doesn't answer 207 or reports nothing.</summary>
    public async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(DavBaseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        using var response = await SendAsync(
            uri,
            u => PropfindRequest(u, _auth, "0", QuotaPropfindBody),
            cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode != 207)
        {
            return null;
        }

        return WebDavResponseParser.ParseQuota(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    // MARK: - Plumbing

    /// <summary>The DAV URL of a POSIX path, each segment percent-encoded; the root maps to "base/".</summary>
    public Uri BuildUri(string path)
    {
        var encoded = string.Join('/', CloudPath.Components(path).Select(CloudHttp.Escape));
        var url = encoded.Length == 0 ? DavBaseUrl + "/" : DavBaseUrl + "/" + encoded;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : throw CloudProviderException.InvalidResponse();
    }

    private Task<HttpResponseMessage> SendAsync(
        Uri uri,
        Func<Uri, HttpRequestMessage> build,
        CancellationToken cancellationToken,
        bool transfer = false,
        TimeSpan? timeout = null,
        bool followRedirects = true)
        => WebDavHttp.SendAsync(_http, uri, build, cancellationToken, transfer, timeout, followRedirects);

    private static HttpRequestMessage PropfindRequest(Uri uri, AuthenticationHeaderValue auth, string depth, string body)
    {
        var request = new HttpRequestMessage(WebDavHttp.Propfind, uri)
        {
            Headers = { Authorization = auth },
            Content = XmlContent(body),
        };
        request.Headers.TryAddWithoutValidation("Depth", depth);
        return request;
    }

    private static StringContent XmlContent(string body)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        return content;
    }

    private async Task<List<WebDavEntry>> ParseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return WebDavResponseParser.Parse(stream, DavBaseUrl);
    }

    private async Task LogFailureAsync(string operation, string path, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
        SupportLogger.Shared.Log($"{operation} {path} → HTTP {(int)response.StatusCode}: {body}", _dialect.LogCategory, LogLevel.Error);
    }
}
