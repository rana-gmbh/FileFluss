// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.WordPress;

/// <summary>What is stored per account — the macOS <c>WordPressCredentials</c>. The site URL is kept as typed.</summary>
internal sealed record WordPressCredentials(string SiteUrl, string Username, string Password, string DisplayName);

/// <summary>
/// One attachment from <c>/wp/v2/media</c>, decoded leniently: <c>media_details.filesize</c>
/// is a number on some WordPress versions and a string on others, and <c>media_details</c>
/// itself is an empty array (not an object) for attachments WordPress has no details for.
/// </summary>
internal sealed record WordPressMedia(int Id, string Date, string? Title, string? SourceUrl, long FileSize, string? File)
{
    public static WordPressMedia Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("id", out var idElement) ||
            !idElement.TryGetInt32(out var id))
        {
            throw CloudProviderException.InvalidResponse();
        }

        string? title = null;
        if (element.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.Object)
        {
            title = String(titleElement, "rendered");
        }

        long size = 0;
        string? file = null;
        if (element.TryGetProperty("media_details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            if (details.TryGetProperty("filesize", out var filesize))
            {
                size = filesize.ValueKind switch
                {
                    JsonValueKind.Number when filesize.TryGetInt64(out var number) => number,
                    JsonValueKind.String when long.TryParse(filesize.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => 0,
                };
            }

            file = String(details, "file");
        }

        return new WordPressMedia(id, String(element, "date") ?? string.Empty, title, String(element, "source_url"), size, file);
    }

    public static IReadOnlyList<WordPressMedia> ParseArray(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw CloudProviderException.InvalidResponse();
            }

            return document.RootElement.EnumerateArray().Select(Parse).ToList();
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    public static WordPressMedia ParseSingle(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// The WordPress REST API (<c>/wp-json/wp/v2</c>) authenticated with an Application
/// Password over Basic auth — port of the macOS <c>WordPressAPIClient</c>.
///
/// <para>The media library has no folders. FileFluss presents it as virtual
/// <c>/YYYY/MM</c> folders built from each attachment's date — the same year/month layout
/// WordPress uses under <c>wp-content/uploads</c> — so a library of thousands of items stays
/// browsable. Files are addressed by those virtual paths; the media id behind each path is
/// cached for delete/rename.</para>
/// </summary>
internal sealed partial class WordPressApiClient
{
    private const int MaxRedirects = 10;

    private static readonly string[] MonthNames =
    [
        string.Empty, "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    private readonly HttpClient _http;
    private readonly string _baseApiUrl;
    private readonly Uri? _siteUri;
    private readonly AuthenticationHeaderValue _authorization;

    /// <summary>Virtual path → media id, for delete/rename/download. Shared by concurrent callers.</summary>
    private readonly ConcurrentDictionary<string, int> _mediaIdCache = new(StringComparer.Ordinal);

    public WordPressApiClient(HttpClient http, WordPressCredentials credentials)
    {
        _http = http;
        Credentials = credentials;
        var site = NormalizeSiteUrl(credentials.SiteUrl);
        _siteUri = Uri.TryCreate(site, UriKind.Absolute, out var parsed) ? parsed : null;
        _baseApiUrl = site + "/wp-json/wp/v2";
        _authorization = CloudHttp.Basic(credentials.Username, credentials.Password);
    }

    public WordPressCredentials Credentials { get; }

    /// <summary>"example.com/" → "https://example.com".</summary>
    public static string NormalizeSiteUrl(string siteUrl)
    {
        var url = siteUrl.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        return url.TrimEnd('/');
    }

    // MARK: - Authentication

    /// <summary>
    /// Verifies the site and the Application Password with a one-item media listing, then
    /// reads the user's display name (non-critical). Returns the credentials to store.
    /// </summary>
    public static async Task<WordPressCredentials> VerifyAsync(HttpClient http, string siteUrl, string username, string password, CancellationToken cancellationToken)
    {
        var client = new WordPressApiClient(http, new WordPressCredentials(siteUrl, username, password, string.Empty));
        using (var response = await client.SendAsync(HttpMethod.Get, client.ApiUri("/media", ("per_page", "1")), null, cancellationToken).ConfigureAwait(false))
        {
            var status = (int)response.StatusCode;
            if (status is 401 or 403)
            {
                throw CloudProviderException.InvalidCredentials();
            }

            if (status == 404)
            {
                throw CloudProviderException.NotFound("WordPress REST API not found. Make sure the site has the REST API enabled at /wp-json/wp/v2/media");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw CloudProviderException.ServerError(status);
            }

            // A site without the REST API often answers 200 with its HTML front page.
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!IsJson(body))
            {
                throw CloudProviderException.InvalidResponse();
            }
        }

        var displayName = username;
        try
        {
            using var userResponse = await client.SendAsync(HttpMethod.Get, client.ApiUri("/users/me"), null, cancellationToken).ConfigureAwait(false);
            if (userResponse.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("name", out var name) &&
                    name.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(name.GetString()))
                {
                    displayName = name.GetString()!;
                }
            }
        }
        catch (Exception e) when (e is CloudProviderException or JsonException or HttpRequestException)
        {
            // Non-critical: the user name stands in for the display name.
        }

        SupportLogger.Shared.Log($"[WordPress] Authenticated as {username} at {siteUrl}", "WordPress");
        return new WordPressCredentials(siteUrl, username, password, displayName);
    }

    /// <summary>The name the account is labelled with: the WordPress display name, else the site's host, else the user name.</summary>
    public string UserDisplayName()
    {
        if (!string.IsNullOrEmpty(Credentials.DisplayName))
        {
            return Credentials.DisplayName;
        }

        return Uri.TryCreate(Credentials.SiteUrl, UriKind.Absolute, out var url) && !string.IsNullOrEmpty(url.Host)
            ? url.Host
            : Credentials.Username;
    }

    // MARK: - Listing

    public Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        var components = CloudPath.Components(path);
        if (components.Length == 0)
        {
            return ListYearFoldersAsync(cancellationToken);
        }

        if (components.Length == 1 && TryYear(components[0], out var year))
        {
            return ListMonthFoldersAsync(year, cancellationToken);
        }

        if (components.Length == 2 && TryYear(components[0], out year) && TryMonth(components[1], out var month))
        {
            return ListMediaFilesAsync(year, month, cancellationToken);
        }

        return Task.FromResult<IReadOnlyList<CloudFileItem>>([]);
    }

    /// <summary>
    /// The root: one folder per year that has media. The API has no "distinct years" query,
    /// so this samples the date-ordered list — first page, last page, and the pages in between
    /// up to ten in total — which is exact for libraries up to 1,000 items and covers the
    /// oldest and newest year beyond that.
    /// </summary>
    private async Task<IReadOnlyList<CloudFileItem>> ListYearFoldersAsync(CancellationToken cancellationToken)
    {
        var years = new HashSet<int>();
        int totalPages;
        using (var first = await SendAsync(HttpMethod.Get, MediaPageUri(1), null, cancellationToken).ConfigureAwait(false))
        {
            ThrowIfUnauthorized(first);
            if (!first.IsSuccessStatusCode)
            {
                throw CloudProviderException.InvalidResponse();
            }

            totalPages = TotalPages(first);
            AddYears(years, await ReadMediaAsync(first, cancellationToken).ConfigureAwait(false));
        }

        if (totalPages > 1)
        {
            // The last page holds the newest media.
            using (var last = await SendAsync(HttpMethod.Get, MediaPageUri(totalPages), null, cancellationToken).ConfigureAwait(false))
            {
                ThrowIfUnauthorized(last);
                if (last.IsSuccessStatusCode)
                {
                    AddYears(years, await ReadMediaAsync(last, cancellationToken).ConfigureAwait(false));
                }
            }

            var pagesToFetch = Math.Min(totalPages, 10);
            for (var page = 2; page < pagesToFetch; page++)
            {
                using var response = await SendAsync(HttpMethod.Get, MediaPageUri(page), null, cancellationToken).ConfigureAwait(false);
                ThrowIfUnauthorized(response);
                if (response.IsSuccessStatusCode)
                {
                    AddYears(years, await ReadMediaAsync(response, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        return years.OrderDescending()
            .Select(year => new CloudFileItem($"wp-year-{year}", year.ToString(CultureInfo.InvariantCulture), $"/{year}", true, 0, CloudFileItem.UnknownDate))
            .ToList();

        Uri MediaPageUri(int page) => ApiUri("/media", ("per_page", "100"), ("page", Num(page)), ("orderby", "date"), ("order", "asc"));
    }

    private async Task<IReadOnlyList<CloudFileItem>> ListMonthFoldersAsync(int year, CancellationToken cancellationToken)
    {
        var months = new HashSet<int>();
        var after = $"{year}-01-01T00:00:00";
        var before = $"{year + 1}-01-01T00:00:00";
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                ApiUri("/media", ("after", after), ("before", before), ("per_page", "100"), ("page", Num(page))),
                null,
                cancellationToken).ConfigureAwait(false);
            ThrowIfUnauthorized(response);
            if (!response.IsSuccessStatusCode)
            {
                break;
            }

            var items = await ReadMediaAsync(response, cancellationToken).ConfigureAwait(false);
            if (items.Count == 0)
            {
                break;
            }

            foreach (var item in items)
            {
                if (ExtractMonth(item.Date) is { } month)
                {
                    months.Add(month);
                }
            }

            if (page >= TotalPages(response))
            {
                break;
            }
        }

        return months.Where(m => m is >= 1 and <= 12).OrderDescending().Select(month =>
        {
            var padded = month.ToString("00", CultureInfo.InvariantCulture);
            return new CloudFileItem($"wp-month-{year}-{padded}", $"{padded} - {MonthNames[month]}", $"/{year}/{padded}", true, 0, CloudFileItem.UnknownDate);
        }).ToList();
    }

    private async Task<IReadOnlyList<CloudFileItem>> ListMediaFilesAsync(int year, int month, CancellationToken cancellationToken)
    {
        var all = new List<CloudFileItem>();
        var padded = month.ToString("00", CultureInfo.InvariantCulture);
        var nextYear = month == 12 ? year + 1 : year;
        var nextMonth = month == 12 ? 1 : month + 1;
        var after = $"{year}-{padded}-01T00:00:00";
        var before = $"{nextYear}-{nextMonth.ToString("00", CultureInfo.InvariantCulture)}-01T00:00:00";
        var basePath = $"/{year}/{padded}";
        var complete = false;

        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                ApiUri("/media", ("after", after), ("before", before), ("per_page", "100"), ("page", Num(page))),
                null,
                cancellationToken).ConfigureAwait(false);
            ThrowIfUnauthorized(response);
            if (!response.IsSuccessStatusCode)
            {
                SupportLogger.Shared.Log($"[WordPress] Listing {basePath} page {page} failed: HTTP {(int)response.StatusCode}", "WordPress", LogLevel.Error);
                break;
            }

            var items = await ReadMediaAsync(response, cancellationToken).ConfigureAwait(false);
            if (items.Count == 0)
            {
                complete = true;
                break;
            }

            foreach (var item in items)
            {
                var file = ToCloudFile(item, basePath);
                all.Add(file);
                _mediaIdCache[file.Path] = item.Id;
            }

            if (page >= TotalPages(response))
            {
                complete = true;
                break;
            }
        }

        if (complete)
        {
            // Only a listing that ran to the end proves something is gone.
            _mediaIdCache.RetainPathChildren(basePath, all.Select(i => i.Name).ToHashSet(StringComparer.Ordinal));
        }

        return all;
    }

    /// <summary>The library-wide media search; results carry their virtual <c>/YYYY/MM/name</c> paths.</summary>
    public async Task<IReadOnlyList<CloudFileItem>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var results = new List<CloudFileItem>();
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                ApiUri("/media", ("search", query), ("per_page", "100"), ("page", Num(page))),
                null,
                cancellationToken).ConfigureAwait(false);
            ThrowIfUnauthorized(response);
            if (!response.IsSuccessStatusCode)
            {
                break;
            }

            var items = await ReadMediaAsync(response, cancellationToken).ConfigureAwait(false);
            if (items.Count == 0)
            {
                break;
            }

            foreach (var item in items)
            {
                var file = ToCloudFile(item, VirtualFolder(item));
                results.Add(file);
                _mediaIdCache[file.Path] = item.Id;
            }

            if (page >= TotalPages(response))
            {
                break;
            }
        }

        return results;
    }

    // MARK: - Items

    public async Task<CloudFileItem> GetFileInfoAsync(string path, CancellationToken cancellationToken)
    {
        var item = await GetMediaAsync(path, cancellationToken).ConfigureAwait(false);
        return ToCloudFile(item, CloudPath.Parent(path));
    }

    /// <summary>
    /// WordPress media is already served publicly from the uploads folder, so "sharing" hands
    /// back <c>source_url</c>, which is itself the direct-download URL; nothing changes on the
    /// server. Membership plugins, private sites and maintenance mode can make it 403/404 —
    /// deliberately not promised otherwise.
    /// </summary>
    public async Task<Uri> PublicMediaUrlAsync(string path, CancellationToken cancellationToken)
    {
        var item = await GetMediaAsync(path, cancellationToken).ConfigureAwait(false);
        if (item.SourceUrl is null || !Uri.TryCreate(item.SourceUrl, UriKind.Absolute, out var url))
        {
            throw CloudProviderException.InvalidResponse();
        }

        return url;
    }

    public async Task DownloadAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var item = await GetMediaAsync(remotePath, cancellationToken).ConfigureAwait(false);
        if (item.SourceUrl is null)
        {
            throw CloudProviderException.NotFound(remotePath);
        }

        if (!Uri.TryCreate(item.SourceUrl, UriKind.Absolute, out var source))
        {
            throw CloudProviderException.InvalidResponse();
        }

        // The Application Password authenticates REST calls only; it goes to the site itself and
        // never to a CDN host that serves the uploads (Jetpack, a separate media domain).
        var authorize = _siteUri is not null && string.Equals(source.Host, _siteUri.Host, StringComparison.OrdinalIgnoreCase);
        using var response = await SendAsync(HttpMethod.Get, source, null, cancellationToken, transfer: true, authorize: authorize).ConfigureAwait(false);
        ThrowIfUnauthorized(response);
        if ((int)response.StatusCode == 404)
        {
            throw CloudProviderException.NotFound(remotePath);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw CloudProviderException.ServerError((int)response.StatusCode);
        }

        await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads into the media library. WordPress files it under the upload month, so the
    /// virtual folder in <paramref name="remotePath"/> is not where it lands — only the name is used.
    /// </summary>
    public async Task UploadAsync(string localPath, string remotePath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var fileName = CloudPath.Name(remotePath);
        if (fileName.Length == 0)
        {
            fileName = Path.GetFileName(localPath);
        }

        var mimeType = MimeTypeForExtension(Path.GetExtension(fileName));
        var uri = ApiUri("/media");
        using var response = await SendAsync(
            HttpMethod.Post,
            uri,
            () => new MultipartFileContent(localPath, "file", fileName, mimeType, onBytes),
            cancellationToken,
            transfer: true).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        SupportLogger.Shared.Log($"[WordPress] Upload {fileName} → HTTP {status}", "WordPress", response.IsSuccessStatusCode ? LogLevel.Info : LogLevel.Error);
        ThrowIfUnauthorized(response);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (CloudProviderException.SizeLimitError(status, localPath) is { } tooLarge)
        {
            throw tooLarge;
        }

        // WordPress explains rejected uploads ("Sorry, you are not allowed to upload this file type.").
        var message = ErrorMessage(await response.ReadBodySnippetAsync(cancellationToken, 4000).ConfigureAwait(false));
        throw message is null ? CloudProviderException.ServerError(status) : CloudProviderException.CommandFailed(message);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var mediaId = await ResolveMediaIdAsync(path, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(HttpMethod.Delete, ApiUri($"/media/{mediaId}", ("force", "true")), null, cancellationToken).ConfigureAwait(false);
        ThrowIfUnauthorized(response);
        if (!response.IsSuccessStatusCode)
        {
            throw CloudProviderException.ServerError((int)response.StatusCode);
        }

        _mediaIdCache.RemovePathSubtree(CloudPath.Normalize(path));
    }

    /// <summary>
    /// WordPress "rename" sets the attachment title; the file and its URL keep their name, so
    /// the listing (which shows the file name) does not change.
    /// </summary>
    public async Task RenameAsync(string path, string newName, CancellationToken cancellationToken)
    {
        var mediaId = await ResolveMediaIdAsync(path, cancellationToken).ConfigureAwait(false);
        var title = Path.GetFileNameWithoutExtension(newName);
        using var response = await SendAsync(
            HttpMethod.Post,
            ApiUri($"/media/{mediaId}"),
            () => CloudHttp.JsonContent(new Dictionary<string, string> { ["title"] = title }),
            cancellationToken).ConfigureAwait(false);
        ThrowIfUnauthorized(response);
        if (!response.IsSuccessStatusCode)
        {
            throw CloudProviderException.ServerError((int)response.StatusCode);
        }
    }

    // MARK: - Media id resolution

    private async Task<WordPressMedia> GetMediaAsync(string path, CancellationToken cancellationToken)
    {
        var mediaId = await ResolveMediaIdAsync(path, cancellationToken).ConfigureAwait(false);
        using var response = await SendAsync(HttpMethod.Get, ApiUri($"/media/{mediaId}"), null, cancellationToken).ConfigureAwait(false);
        ThrowIfUnauthorized(response);
        if (!response.IsSuccessStatusCode)
        {
            throw CloudProviderException.NotFound(path);
        }

        return WordPressMedia.ParseSingle(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Cache, then a listing of the month folder, then a title search.</summary>
    private async Task<int> ResolveMediaIdAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = CloudPath.Normalize(path);
        if (_mediaIdCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        var components = CloudPath.Components(normalized);
        if (components.Length < 3 ||
            !int.TryParse(components[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(components[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month))
        {
            throw CloudProviderException.NotFound(path);
        }

        var fileName = string.Join('/', components.Skip(2));
        await ListMediaFilesAsync(year, month, cancellationToken).ConfigureAwait(false);
        if (_mediaIdCache.TryGetValue(normalized, out cached))
        {
            return cached;
        }

        using var response = await SendAsync(
            HttpMethod.Get,
            ApiUri("/media", ("search", Path.GetFileNameWithoutExtension(fileName))),
            null,
            cancellationToken).ConfigureAwait(false);
        ThrowIfUnauthorized(response);
        if (!response.IsSuccessStatusCode)
        {
            throw CloudProviderException.NotFound(path);
        }

        foreach (var item in await ReadMediaAsync(response, cancellationToken).ConfigureAwait(false))
        {
            if (FileNameOf(item) == fileName)
            {
                _mediaIdCache[normalized] = item.Id;
                return item.Id;
            }
        }

        throw CloudProviderException.NotFound(path);
    }

    // MARK: - Mapping

    internal static CloudFileItem ToCloudFile(WordPressMedia item, string basePath)
    {
        var name = FileNameOf(item);
        var path = basePath == "/" ? "/" + name : basePath + "/" + name;
        return new CloudFileItem($"wp-{item.Id}", name, path, false, item.FileSize, ParseDate(item.Date))
        {
            IsHidden = CloudFileItem.IsDotHidden(name),
        };
    }

    /// <summary>The real file name from <c>media_details.file</c> ("2026/04/photo.jpg"), else the title plus the URL's extension.</summary>
    internal static string FileNameOf(WordPressMedia item)
    {
        if (!string.IsNullOrEmpty(item.File))
        {
            var slash = item.File.LastIndexOf('/');
            return slash < 0 ? item.File : item.File[(slash + 1)..];
        }

        var title = WebUtility.HtmlDecode(TagPattern().Replace(item.Title ?? string.Empty, string.Empty)).Trim();
        var extension = string.Empty;
        if (item.SourceUrl is not null)
        {
            var sourcePath = Uri.TryCreate(item.SourceUrl, UriKind.Absolute, out var source) ? source.AbsolutePath : item.SourceUrl;
            extension = Path.GetExtension(sourcePath).TrimStart('.');
        }

        if (extension.Length > 0 && title.Length > 0)
        {
            return $"{title}.{extension}";
        }

        return title.Length == 0 ? $"media-{item.Id}" : title;
    }

    /// <summary>
    /// WordPress "date" is the site's local time without an offset ("2026-04-08T12:00:00");
    /// it is read as local time, as macOS does. A value with a zone is honoured.
    /// </summary>
    internal static DateTimeOffset ParseDate(string value)
    {
        if ((value.EndsWith('Z') || ZonePattern().IsMatch(value)) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var zoned))
        {
            return zoned;
        }

        return DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            ? local
            : CloudFileItem.UnknownDate;
    }

    internal static int? ExtractYear(string date)
        => date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;

    internal static int? ExtractMonth(string date)
        => date.Length >= 7 && int.TryParse(date.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month) ? month : null;

    private static string VirtualFolder(WordPressMedia item)
        => ExtractYear(item.Date) is { } year && ExtractMonth(item.Date) is { } month
            ? $"/{year}/{month.ToString("00", CultureInfo.InvariantCulture)}"
            : "/";

    private static bool TryYear(string component, out int year)
        => int.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out year) && year is >= 1900 and <= 2100;

    private static bool TryMonth(string component, out int month)
        => int.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out month) && month is >= 1 and <= 12;

    private static void AddYears(HashSet<int> years, IEnumerable<WordPressMedia> items)
    {
        foreach (var item in items)
        {
            if (ExtractYear(item.Date) is { } year)
            {
                years.Add(year);
            }
        }
    }

    internal static string MimeTypeForExtension(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => "image/jpeg",
        "png" => "image/png",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "svg" => "image/svg+xml",
        "mp4" => "video/mp4",
        "mov" => "video/quicktime",
        "avi" => "video/x-msvideo",
        "mp3" => "audio/mpeg",
        "wav" => "audio/wav",
        "pdf" => "application/pdf",
        "doc" => "application/msword",
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "zip" => "application/zip",
        _ => "application/octet-stream",
    };

    // MARK: - HTTP

    private Uri ApiUri(string path, params (string Key, string Value)[] query)
    {
        var url = _baseApiUrl + path;
        if (query.Length > 0)
        {
            url += "?" + string.Join('&', query.Select(q => $"{q.Key}={CloudHttp.Escape(q.Value)}"));
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : throw CloudProviderException.InvalidResponse();
    }

    /// <summary>
    /// Sends with redirects followed by hand. .NET drops the Authorization header on every
    /// redirect and turns a redirected POST into a GET, so an "http://" site URL or a
    /// trailing-slash redirect would lose the credentials or the upload. macOS keeps both for
    /// same-host redirects (<c>WPSessionDelegate</c>); a redirect to another host — or a
    /// downgrade to plain HTTP — is followed without credentials, as URLSession does.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri uri,
        Func<HttpContent>? content,
        CancellationToken cancellationToken,
        bool transfer = false,
        bool authorize = true)
    {
        var originalHost = uri.Host;
        for (var hop = 0; ; hop++)
        {
            HttpResponseMessage response;
            using (var request = new HttpRequestMessage(method, uri) { Content = content?.Invoke() })
            {
                if (authorize)
                {
                    request.Headers.Authorization = _authorization;
                }

                response = transfer
                    ? await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false)
                    : await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is null || hop >= MaxRedirects)
            {
                return response;
            }

            var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
            if (!WebDav.WebDavHttp.IsFollowable(uri, next))
            {
                // Never down to plain http (an upload would cross the network in the clear),
                // never to anything but http(s): the redirect answer is the result.
                return response;
            }

            response.Dispose();

            var sameHost = string.Equals(next.Host, originalHost, StringComparison.OrdinalIgnoreCase) &&
                           !(uri.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp);
            if (!sameHost)
            {
                authorize = false;
                if (status is 301 or 302 or 303 && method != HttpMethod.Get && method != HttpMethod.Head)
                {
                    method = HttpMethod.Get;
                    content = null;
                }
            }

            uri = next;
        }
    }

    private static async Task<IReadOnlyList<WordPressMedia>> ReadMediaAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => WordPressMedia.ParseArray(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

    private static int TotalPages(HttpResponseMessage response)
        => response.Headers.TryGetValues("X-WP-TotalPages", out var values) &&
           int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages)
            ? pages
            : 1;

    private static void ThrowIfUnauthorized(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw CloudProviderException.Unauthorized();
        }
    }

    private static bool IsJson(string body)
    {
        try
        {
            using var _ = JsonDocument.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The "message" of a WordPress REST error body ({"code": …, "message": …}).</summary>
    internal static string? ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("message", out var message) &&
                   message.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(message.GetString())
                ? WebUtility.HtmlDecode(message.GetString()!)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"[+-]\d{2}:?\d{2}$")]
    private static partial Regex ZonePattern();
}
