// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FileFluss.Core.IO;
using FileFluss.Core.Models;

namespace FileFluss.Core.Providers.PCloud;

/// <summary>
/// What the credential store holds for one pCloud account (macOS <c>PCloudCredentials</c>).
/// <see cref="Hostname"/> records which regional API host accepted the sign-in:
/// <c>api.pcloud.com</c> for US accounts, <c>eapi.pcloud.com</c> for EU ones. Every later
/// call goes there, because a token only works on its own region's host.
/// </summary>
internal sealed record PCloudCredentials(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("hostname")] string Hostname,
    [property: JsonPropertyName("userId")] ulong UserId);

internal sealed record PCloudLoginResponse(
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("auth")] string? Auth,
    [property: JsonPropertyName("userid")] ulong? UserId,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("error")] string? Error);

internal sealed record PCloudDigestResponse(
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("digest")] string? Digest);

internal sealed record PCloudUserInfoResponse(
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("userid")] ulong? UserId,
    [property: JsonPropertyName("quota")] long? Quota,
    [property: JsonPropertyName("usedquota")] long? UsedQuota);

internal sealed record PCloudUserInfo(string Email, ulong UserId, long Quota, long UsedQuota);

internal sealed record PCloudMetadataResponse(
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("metadata")] PCloudItemMetadata? Metadata);

internal sealed record PCloudFileLinkResponse(
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("hosts")] IReadOnlyList<string>? Hosts)
{
    /// <summary>"https://{first host}{path}" — the CDN URL serving the bytes; null when the reply is incomplete.</summary>
    public Uri? DownloadUrl()
    {
        var host = Hosts?.FirstOrDefault();
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(Path))
        {
            return null;
        }

        return Uri.TryCreate($"https://{host}{Path}", UriKind.Absolute, out var url) ? url : null;
    }
}

/// <summary>
/// A file or folder. Dates are Unix seconds because every call passes
/// <c>timeformat=timestamp</c> — without it pCloud sends RFC 2822 strings and the decode fails.
/// </summary>
internal sealed record PCloudItemMetadata(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("isfolder")] bool IsFolder,
    [property: JsonPropertyName("fileid")] ulong? FileId,
    [property: JsonPropertyName("folderid")] ulong? FolderId,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("modified")] double? Modified,
    [property: JsonPropertyName("created")] double? Created,
    [property: JsonPropertyName("contenttype")] string? ContentType,
    [property: JsonPropertyName("hash")] ulong? Hash,
    [property: JsonPropertyName("contents")] IReadOnlyList<PCloudItemMetadata>? Contents)
{
    public CloudFileItem ToCloudFileItem(string parentPath)
    {
        var name = Name ?? string.Empty;
        return new CloudFileItem(
            IsFolder ? $"d{FolderId ?? 0}" : $"f{FileId ?? 0}",
            name,
            CloudPath.Combine(parentPath, name),
            IsFolder,
            Size ?? 0,
            Modified is { } seconds ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)) : CloudFileItem.UnknownDate,
            // pCloud's own 64-bit content hash, as macOS reports it.
            Hash?.ToString(CultureInfo.InvariantCulture))
        {
            IsHidden = CloudFileItem.IsDotHidden(name),
        };
    }

    /// <summary>Bytes of every file beneath a recursive listing.</summary>
    public long TotalFileSize()
    {
        long total = 0;
        foreach (var item in Contents ?? [])
        {
            total += item.IsFolder ? item.TotalFileSize() : item.Size ?? 0;
        }

        return total;
    }
}

/// <summary><c>getfilepublink</c> reply. <c>code</c> is what <c>getpublinkdownload</c> needs to resolve the CDN URL.</summary>
internal sealed record PCloudPubLinkResponse(
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("link")] string? Link,
    [property: JsonPropertyName("linkid")] ulong? LinkId,
    [property: JsonPropertyName("code")] string? Code);

internal sealed record PCloudPublinkMetadata(
    [property: JsonPropertyName("fileid")] ulong? FileId,
    [property: JsonPropertyName("folderid")] ulong? FolderId,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("isfolder")] bool? IsFolder);

/// <summary>
/// One entry of <c>listpublinks</c>. pCloud has moved these fields around between API
/// revisions and returns different sets for file and folder links, so every identifying
/// value is optional and read from wherever it turns up — the link or its metadata.
/// </summary>
internal sealed record PCloudPublink(
    [property: JsonPropertyName("linkid")] ulong? LinkId,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("link")] string? Link,
    [property: JsonPropertyName("fileid")] ulong? FileId,
    [property: JsonPropertyName("folderid")] ulong? FolderId,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("haspassword")] bool? HasPassword,
    [property: JsonPropertyName("expire")] JsonElement? Expire,
    [property: JsonPropertyName("expires")] JsonElement? Expires,
    [property: JsonPropertyName("metadata")] PCloudPublinkMetadata? Metadata)
{
    public ulong? ResolvedFileId => FileId ?? Metadata?.FileId;

    public string? ResolvedPath => Path ?? Metadata?.Path;

    public DateTimeOffset? ExpiryDate => PCloudDates.ParseLinkDate(Expire) ?? PCloudDates.ParseLinkDate(Expires);
}

internal sealed record PCloudPublinksResponse(
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("publinks")] IReadOnlyList<PCloudPublink>? Publinks);

internal static partial class PCloudDates
{
    private static readonly string[] Rfc2822Formats =
    [
        "ddd, dd MMM yyyy HH:mm:ss zzz",
        "ddd, d MMM yyyy HH:mm:ss zzz",
    ];

    /// <summary>
    /// A link's expiry. pCloud writes RFC 2822 text by default and Unix seconds when the call
    /// passes <c>timeformat=timestamp</c>; both are accepted, so an endpoint that ignores the
    /// parameter doesn't fail the decode. 0 (or "0") is pCloud's "never expires".
    /// </summary>
    public static DateTimeOffset? ParseLinkDate(JsonElement? value)
    {
        if (value is not { } element)
        {
            return null;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetDouble(out var seconds):
                return seconds > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)) : null;
            case JsonValueKind.String:
                return ParseText(element.GetString());
            default:
                return null;
        }
    }

    public static DateTimeOffset? ParseText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "0")
        {
            return null;
        }

        // .NET's "zzz" wants "+00:00"; RFC 2822 writes "+0000".
        var normalized = OffsetWithoutColon().Replace(raw.Trim(), "$1:$2");
        if (DateTimeOffset.TryParseExact(normalized, Rfc2822Formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
        {
            return date;
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date) ? date : null;
    }

    /// <summary>pCloud's <c>expire</c> parameter: UTC, seconds precision.</summary>
    public static string ExpiryParameter(DateTimeOffset date)
        => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex OffsetWithoutColon();
}
