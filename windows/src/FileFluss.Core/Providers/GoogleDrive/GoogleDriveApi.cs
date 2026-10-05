// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Text.Json;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.GoogleDrive;

/// <summary>Drive v3 endpoints and the small pieces both Google providers share.</summary>
internal static class GoogleDriveApi
{
    public const string ApiBase = "https://www.googleapis.com/drive/v3";
    public const string UploadBase = "https://www.googleapis.com/upload/drive/v3";
    public const string FolderMimeType = "application/vnd.google-apps.folder";

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
