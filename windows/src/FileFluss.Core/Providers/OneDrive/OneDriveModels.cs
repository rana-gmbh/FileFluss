// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileFluss.Core.IO;
using FileFluss.Core.Models;

namespace FileFluss.Core.Providers.OneDrive;

/// <summary>What the credential store holds for one OneDrive account (macOS <c>OneDriveCredentials</c>).</summary>
internal sealed record OneDriveCredentials(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string RefreshToken,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("userEmail")] string UserEmail);

internal sealed record OneDriveTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] long ExpiresIn,
    [property: JsonPropertyName("token_type")] string? TokenType);

internal sealed record GraphUser(
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("mail")] string? Mail,
    [property: JsonPropertyName("userPrincipalName")] string? UserPrincipalName);

internal sealed record GraphListResponse(
    [property: JsonPropertyName("value")] IReadOnlyList<GraphDriveItem>? Value,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink);

internal sealed record GraphFolderFacet([property: JsonPropertyName("childCount")] int? ChildCount);

internal sealed record GraphHashes(
    [property: JsonPropertyName("sha1Hash")] string? Sha1Hash,
    [property: JsonPropertyName("quickXorHash")] string? QuickXorHash);

internal sealed record GraphFileFacet(
    [property: JsonPropertyName("mimeType")] string? MimeType,
    [property: JsonPropertyName("hashes")] GraphHashes? Hashes);

internal sealed record GraphItemReference(
    [property: JsonPropertyName("path")] string? Path);

internal sealed record GraphDriveItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("lastModifiedDateTime")] string? LastModifiedDateTime,
    [property: JsonPropertyName("folder")] GraphFolderFacet? Folder,
    [property: JsonPropertyName("file")] GraphFileFacet? File,
    [property: JsonPropertyName("parentReference")] GraphItemReference? ParentReference)
{
    public bool IsDirectory => Folder is not null;

    public CloudFileItem ToCloudFileItem(string parentPath)
    {
        var path = CloudPath.Combine(parentPath, Name);
        return new CloudFileItem(
            IsDirectory ? "d" + Id : "f" + Id,
            Name,
            path,
            IsDirectory,
            Size ?? 0,
            GraphDates.Parse(LastModifiedDateTime) ?? CloudFileItem.UnknownDate,
            File?.Hashes?.Sha1Hash ?? File?.Hashes?.QuickXorHash)
        {
            IsHidden = CloudFileItem.IsDotHidden(Name),
        };
    }

    /// <summary>
    /// The folder a search hit lives in, from <c>parentReference.path</c>
    /// ("/drive/root:/Folder/Sub", percent-encoded). Null when Graph left it out, which it
    /// does for some search results.
    /// </summary>
    public string? ParentPathFromReference()
    {
        var raw = ParentReference?.Path;
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var marker = raw.IndexOf("root:", StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var remainder = raw[(marker + "root:".Length)..];
        try
        {
            remainder = Uri.UnescapeDataString(remainder);
        }
        catch (UriFormatException)
        {
            // Keep the raw text; a wrong-looking path beats dropping the hit.
        }

        return CloudPath.Normalize(remainder);
    }
}

internal sealed record GraphQuota(
    [property: JsonPropertyName("total")] long? Total,
    [property: JsonPropertyName("used")] long? Used);

internal sealed record GraphDriveQuota([property: JsonPropertyName("quota")] GraphQuota? Quota);

internal sealed record GraphUploadSession([property: JsonPropertyName("uploadUrl")] string? UploadUrl);

internal sealed record GraphSharingLink(
    [property: JsonPropertyName("webUrl")] string? WebUrl,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("scope")] string? Scope);

/// <summary>
/// The Permission object Graph answers <c>createLink</c> and <c>GET …/permissions</c> with.
/// Only the nested sharing link is needed, plus the fields that say what was applied.
/// </summary>
internal sealed record GraphPermission(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("link")] GraphSharingLink? Link,
    [property: JsonPropertyName("hasPassword")] bool? HasPassword,
    [property: JsonPropertyName("expirationDateTime")] string? ExpirationDateTime);

/// <summary><c>GET …/permissions</c>: an item nobody shared comes back 200 with an empty <c>value</c>.</summary>
internal sealed record GraphPermissionList([property: JsonPropertyName("value")] IReadOnlyList<GraphPermission>? Value);

internal static class GraphDates
{
    /// <summary>ISO 8601 with or without fractional seconds, as Graph writes them.</summary>
    public static DateTimeOffset? Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;
    }

    /// <summary>"yyyy-MM-ddTHH:mm:ssZ" — what <c>createLink</c> and the upload-time fileSystemInfo patch send.</summary>
    public static string Seconds(DateTimeOffset date)
        => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>"yyyy-MM-ddTHH:mm:ss.fffZ" — what setModificationDate sends.</summary>
    public static string Milliseconds(DateTimeOffset date)
        => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Graph's error envelope: <c>{ "error": { "code", "message", "innerError": { … } } }</c>.</summary>
internal static class GraphErrors
{
    /// <summary>
    /// The message is the only place the real cause shows up (tenant sharing policy, plan
    /// requirements), and "Access denied" alone is unactionable — Graph's <c>code</c> and
    /// <c>innerError</c> say which rule was hit, so they are appended.
    /// </summary>
    public static string? ParseMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var message = StringProperty(error, "message");
            var code = StringProperty(error, "code");
            string? innerCode = null;
            string? innerMessage = null;
            if ((error.TryGetProperty("innerError", out var inner) || error.TryGetProperty("innererror", out inner)) &&
                inner.ValueKind == JsonValueKind.Object)
            {
                innerCode = StringProperty(inner, "code");
                innerMessage = StringProperty(inner, "message");
            }

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(message))
            {
                parts.Add(message);
            }

            var codes = new[] { code, innerCode }.Where(c => !string.IsNullOrEmpty(c)).ToList();
            if (codes.Count > 0)
            {
                parts.Add($"({string.Join(" / ", codes)})");
            }

            if (!string.IsNullOrEmpty(innerMessage) && innerMessage != message)
            {
                parts.Add(innerMessage);
            }

            return parts.Count > 0 ? string.Join(' ', parts) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
