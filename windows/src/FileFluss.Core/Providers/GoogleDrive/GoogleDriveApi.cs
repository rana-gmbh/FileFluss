// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.GoogleDrive;

/// <summary>Drive v3 endpoints and the small pieces both Google providers share.</summary>
internal static partial class GoogleDriveApi
{
    public const string ApiBase = "https://www.googleapis.com/drive/v3";
    public const string UploadBase = "https://www.googleapis.com/upload/drive/v3";
    public const string FolderMimeType = "application/vnd.google-apps.folder";

    /// <summary>The domains a thumbnail link may point at and still be sent the account's token.</summary>
    private static readonly string[] GoogleThumbnailDomains = ["googleusercontent.com", "google.com", "googleapis.com"];

    /// <summary>"k=v&amp;…", every key and value percent-encoded (spaces as %20, '+' as %2B).</summary>
    public static string QueryString(IEnumerable<(string Key, string Value)> items)
        => string.Join('&', items.Select(i => CloudHttp.Escape(i.Key) + "=" + CloudHttp.Escape(i.Value)));

    /// <summary>
    /// A literal for a Drive <c>q</c> expression: backslashes and single quotes escaped, as
    /// the query language requires. (macOS escaped only the quote; a name with a backslash
    /// then produced an invalid query.)
    /// </summary>
    public static string QueryLiteral(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>RFC 3339 with milliseconds in UTC, the form Drive accepts for modifiedTime/createdTime.</summary>
    public static string FormatTimestamp(DateTimeOffset date)
        => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTimestamp(string? value)
        => value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : CloudFileItem.UnknownDate;

    /// <summary>
    /// Drive v3 metadata JSON, preserving modified/created times when known; fields whose
    /// value is null are omitted.
    /// </summary>
    public static string BuildMetadata(string? name, string? parentId, DateTimeOffset? modified, DateTimeOffset? created)
    {
        var metadata = new Dictionary<string, object>();
        if (name is not null)
        {
            metadata["name"] = name;
        }

        if (parentId is not null)
        {
            metadata["parents"] = new[] { parentId };
        }

        if (modified is { } m)
        {
            metadata["modifiedTime"] = FormatTimestamp(m);
        }

        if (created is { } c)
        {
            metadata["createdTime"] = FormatTimestamp(c);
        }

        return JsonSerializer.Serialize(metadata);
    }

    /// <summary>The <c>{"error":{"message":…}}</c> text of a Google error body, or null.</summary>
    public static string? ParseErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.Object &&
                   error.TryGetProperty("message", out var message) &&
                   message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>about.get</c> with the <c>storageQuota</c> field. <c>limit</c> covers Drive, Gmail
    /// and Photos together; Workspace accounts with admin-granted unlimited storage omit it,
    /// so only usage is reported then. Both come back as strings to carry 64-bit values past
    /// JavaScript's safe-integer range. Null when the call fails.
    /// </summary>
    public static async Task<CloudStorageQuota?> StorageQuotaAsync(HttpClient http, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/about?fields=storageQuota");
        request.Headers.Authorization = CloudHttp.Bearer(accessToken);
        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var about = await response.ReadJsonAsync<GoogleAbout>(cancellationToken).ConfigureAwait(false);
        var used = long.TryParse(about.StorageQuota?.Usage, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : 0;
        long? total = long.TryParse(about.StorageQuota?.Limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : null;
        return new CloudStorageQuota(used, total);
    }

    // MARK: - Paths

    /// <summary>"" → "/", "/a/" → "/a". Deliberately not <c>CloudPath.Normalize</c>: a Drive name may contain a backslash.</summary>
    public static string Trim(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    public static string Child(string parent, string name) => parent == "/" ? "/" + name : parent + "/" + name;

    public static string[] Components(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    // MARK: - Thumbnails

    /// <summary>
    /// The Drive file id inside a listed item's id. Listings and searches build ids as "f" plus
    /// the file id ("d" for folders, "d:" for a picked root), so a file's own id saves walking
    /// its path. Anything else is null.
    /// </summary>
    public static string? DriveFileId(string itemId)
        => itemId.Length > 1 && itemId[0] == 'f' ? itemId[1..] : null;

    /// <summary>
    /// The longer edge to ask Drive for: the tile's own size, within bounds that keep a
    /// mistaken caller from asking for a tiny or a huge image.
    /// </summary>
    public static int ThumbnailEdge(int maxPixelSize) => Math.Clamp(maxPixelSize, 32, 1600);

    /// <summary>
    /// <c>thumbnailLink</c> carries its size in the URL: a trailing <c>=s220</c> on the path of
    /// googleusercontent links, or an <c>sz=s220</c> query item on older ones. Google doesn't
    /// document changing it, so only those exact forms are rewritten; any other link comes back
    /// as it was, at Drive's default size, which the caller scales anyway. The rest of the link
    /// is kept character for character, percent-escapes included. Null when it isn't an
    /// absolute URL at all.
    /// </summary>
    public static string? ThumbnailLink(string link, int edge)
    {
        // The "://" test as well: on Unix .NET reads a rooted path ("/a=s220") as an absolute file URI.
        var schemeEnd = link.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || !Uri.TryCreate(link, UriKind.Absolute, out _))
        {
            return null;
        }

        var authorityEnd = link.IndexOfAny(['/', '?', '#'], schemeEnd + 3);
        var pathStart = authorityEnd < 0 ? link.Length : authorityEnd;
        var fragmentStart = link.IndexOf('#', pathStart);
        var beforeFragment = fragmentStart < 0 ? link.Length : fragmentStart;
        var queryStart = link.IndexOf('?', pathStart, beforeFragment - pathStart);
        var pathEnd = queryStart < 0 ? beforeFragment : queryStart;

        var path = link[pathStart..pathEnd];
        var sizeInPath = ThumbnailPathSize().Match(path);
        if (sizeInPath.Success)
        {
            return link[..pathStart]
                + path[..sizeInPath.Index] + "=s" + edge.ToString(CultureInfo.InvariantCulture)
                + link[pathEnd..];
        }

        if (queryStart < 0)
        {
            return link;
        }

        var items = link[(queryStart + 1)..beforeFragment].Split('&');
        for (var i = 0; i < items.Length; i++)
        {
            var separator = items[i].IndexOf('=');
            if (separator > 0 && items[i][..separator] == "sz" && ThumbnailQuerySize().IsMatch(items[i][(separator + 1)..]))
            {
                items[i] = "sz=s" + edge.ToString(CultureInfo.InvariantCulture);
                return link[..(queryStart + 1)] + string.Join('&', items) + link[beforeFragment..];
            }
        }

        return link;
    }

    /// <summary>
    /// Whether a thumbnail URL may be sent the account's token. Drive's thumbnails are fetched
    /// with credentials, so the link has to be an HTTPS address of Google's own; anything else
    /// is never fetched.
    /// </summary>
    public static bool MayCarryGoogleToken(Uri url)
    {
        if (!url.IsAbsoluteUri || !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = url.Host.ToLowerInvariant();
        return host.Length > 0 && GoogleThumbnailDomains.Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"=s\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ThumbnailPathSize();

    [GeneratedRegex(@"^s\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ThumbnailQuerySize();

    /// <summary>
    /// Asks Drive for the file's <c>thumbnailLink</c> and fetches the image behind it — the
    /// macOS <c>fetchThumbnail</c>, shared by both Google providers. The link is short-lived,
    /// so it is looked up fresh each time rather than kept; the caller keeps the image instead.
    /// Both requests carry the token, because Drive serves a private file's thumbnail only to a
    /// credentialed request, and each is sent once more with a refreshed token after a 401.
    /// </summary>
    /// <param name="mapError">The calling provider's own mapping of a failed status and body, so a thumbnail error reads like that provider's other errors.</param>
    public static async Task<byte[]?> FetchThumbnailAsync(
        HttpClient http,
        GoogleTokenSession session,
        string fileId,
        int maxPixelSize,
        Func<int, string, CloudProviderException> mapError,
        string logCategory,
        CancellationToken cancellationToken)
    {
        var metadataUrl = new Uri($"{ApiBase}/files/{CloudHttp.Escape(fileId)}?fields=hasThumbnail,thumbnailLink&supportsAllDrives=true");
        GoogleThumbnailInfo info;
        using (var response = await SendWithTokenAsync(http, session, metadataUrl, logCategory, cancellationToken).ConfigureAwait(false))
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Gone since the listing: nothing to preview.
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.ReadBodySnippetAsync(cancellationToken, 1000).ConfigureAwait(false);
                SupportLogger.Shared.Log($"GET thumbnail info → HTTP {(int)response.StatusCode}: {body}", logCategory, LogLevel.Error);
                throw mapError((int)response.StatusCode, body);
            }

            info = await response.ReadJsonAsync<GoogleThumbnailInfo>(cancellationToken).ConfigureAwait(false);
        }

        // The link is only there when this app may read the file's content and Drive has made
        // a preview, so its absence is the "no preview" answer.
        if (info.HasThumbnail == false || string.IsNullOrEmpty(info.ThumbnailLink))
        {
            return null;
        }

        var link = info.ThumbnailLink;
        var sized = ThumbnailLink(link, ThumbnailEdge(maxPixelSize));
        if (sized is not null && Uri.TryCreate(sized, UriKind.Absolute, out var sizedUrl) && MayCarryGoogleToken(sizedUrl) &&
            await FetchThumbnailImageAsync(http, session, sizedUrl, mapError, logCategory, cancellationToken).ConfigureAwait(false) is { } image)
        {
            return image;
        }

        // The size rewrite isn't documented, so a refusal of the resized link gets one more
        // try with the link exactly as Drive gave it.
        if (sized == link || !Uri.TryCreate(link, UriKind.Absolute, out var original) || !MayCarryGoogleToken(original))
        {
            return null;
        }

        return await FetchThumbnailImageAsync(http, session, original, mapError, logCategory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The image behind a thumbnail link. Null when the link is refused or what comes back
    /// isn't an image, as when an expired link lands on a sign-in page.
    /// </summary>
    private static async Task<byte[]?> FetchThumbnailImageAsync(
        HttpClient http,
        GoogleTokenSession session,
        Uri url,
        Func<int, string, CloudProviderException> mapError,
        string logCategory,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithTokenAsync(http, session, url, logCategory, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length > 0 && (type.Length == 0 || type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) ? bytes : null;
        }

        if (status is 400 or 403 or 404 or 410)
        {
            return null;
        }

        var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
        SupportLogger.Shared.Log($"GET thumbnail image → HTTP {status}", logCategory, LogLevel.Error);
        throw mapError(status, body);
    }

    /// <summary>A GET with the account's token, sent once more with a force-refreshed token after a 401, as the providers' other calls do.</summary>
    private static async Task<HttpResponseMessage> SendWithTokenAsync(HttpClient http, GoogleTokenSession session, Uri url, string logCategory, CancellationToken cancellationToken)
    {
        var accessToken = await session.AccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendOnceAsync(http, url, accessToken, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            SupportLogger.Shared.Log($"GET {url.AbsolutePath} → 401, force-refreshing and retrying once", logCategory);
            response.Dispose();
            accessToken = await session.RefreshAfterRejectionAsync(accessToken, cancellationToken).ConfigureAwait(false);
            response = await SendOnceAsync(http, url, accessToken, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private static async Task<HttpResponseMessage> SendOnceAsync(HttpClient http, Uri url, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = CloudHttp.Bearer(accessToken);
        return await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The two <c>files.get</c> fields the thumbnail lookup asks for.</summary>
internal sealed class GoogleThumbnailInfo
{
    public bool? HasThumbnail { get; init; }

    public string? ThumbnailLink { get; init; }
}

/// <summary>A <c>files</c> resource with the fields FileFluss asks for.</summary>
internal sealed class GoogleDriveFile
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    /// <summary>Bytes as a decimal string; absent for folders and Google Docs.</summary>
    public string? Size { get; init; }

    public string? ModifiedTime { get; init; }

    public string? Md5Checksum { get; init; }

    public IReadOnlyList<string>? Parents { get; init; }

    public bool IsFolder => MimeType == GoogleDriveApi.FolderMimeType;

    public DateTimeOffset ModificationDate => GoogleDriveApi.ParseTimestamp(ModifiedTime);

    /// <summary>The listing entry; ids carry a "d"/"f" prefix so a folder and a file never collide.</summary>
    public CloudFileItem ToItem(string parentPath) => new(
        (IsFolder ? "d" : "f") + Id,
        Name,
        GoogleDriveApi.Child(parentPath, Name),
        IsFolder,
        long.TryParse(Size, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
        ModificationDate,
        Md5Checksum)
    {
        IsHidden = CloudFileItem.IsDotHidden(Name),
    };
}

internal sealed class GoogleFileList
{
    public IReadOnlyList<GoogleDriveFile>? Files { get; init; }

    public string? NextPageToken { get; init; }
}

internal sealed class GoogleAbout
{
    public QuotaBody? StorageQuota { get; init; }

    internal sealed class QuotaBody
    {
        public string? Limit { get; init; }

        public string? Usage { get; init; }
    }
}

/// <summary>
/// Forwards only bytes beyond the most ever reported, so re-sending a body (a retried
/// upload) does not count its bytes twice.
/// </summary>
internal sealed class HighWaterProgress(ByteProgress? inner)
{
    private long _reportedHigh;
    private long _attempt;

    public ByteProgress? Callback => inner is null ? null : OnBytes;

    /// <summary>Starts another attempt; its bytes count again only once they pass the previous high-water mark.</summary>
    public void Restart() => Interlocked.Exchange(ref _attempt, 0);

    private void OnBytes(long delta)
    {
        var position = Interlocked.Add(ref _attempt, delta);
        long high;
        do
        {
            high = Interlocked.Read(ref _reportedHigh);
            if (position <= high)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _reportedHigh, position, high) != high);

        inner!(position - high);
    }
}
