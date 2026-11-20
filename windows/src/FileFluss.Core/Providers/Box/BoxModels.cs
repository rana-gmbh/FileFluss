// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Text.Json.Serialization;
using FileFluss.Core.IO;
using FileFluss.Core.Models;

namespace FileFluss.Core.Providers.Box;

/// <summary>
/// What is stored under <c>box.&lt;account-uuid&gt;</c>. Box rotates the refresh token on
/// every refresh and invalidates the previous one, so this record must be re-saved after each
/// refresh — a rotated token that only lives in memory leaves a dead one in the store, and the
/// next launch asks for a new sign-in (issue #46).
/// </summary>
internal sealed record BoxCredentials(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string UserLogin,
    string DisplayName);

internal sealed class BoxTokenResponse
{
    [JsonPropertyName("access_token")]
    [JsonRequired]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    [JsonRequired]
    public long ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }
}

internal sealed class BoxUser
{
    [JsonPropertyName("id")]
    [JsonRequired]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    [JsonRequired]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("login")]
    [JsonRequired]
    public string Login { get; set; } = string.Empty;
}

/// <summary>A file, folder or web link as Box lists it.</summary>
internal sealed class BoxItem
{
    [JsonPropertyName("id")]
    [JsonRequired]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    [JsonRequired]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    [JsonRequired]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("modified_at")]
    public string? ModifiedAt { get; set; }

    [JsonPropertyName("content_modified_at")]
    public string? ContentModifiedAt { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    /// <summary>Only requested by search, whose results come from anywhere in the tree.</summary>
    [JsonPropertyName("path_collection")]
    public BoxPathCollection? PathCollection { get; set; }

    public bool IsFolder => Type == "folder";

    /// <remarks>
    /// The item's id in the listing is its path, as on macOS: the Box id lives in the
    /// provider's path cache, and a path is what every other operation is addressed by.
    /// </remarks>
    public CloudFileItem ToCloudFileItem(string parentPath)
    {
        var path = CloudPath.Combine(parentPath, Name);

        // Prefer the user-set content_modified_at (what other providers expose), then the
        // server's modified_at.
        var modified = ParseDate(ContentModifiedAt) ?? ParseDate(ModifiedAt) ?? CloudFileItem.UnknownDate;
        return new CloudFileItem(path, Name, path, IsFolder, Size ?? 0, modified, Sha1)
        {
            IsHidden = CloudFileItem.IsDotHidden(Name),
        };
    }

    /// <summary>
    /// The folder a search hit lives in, from its <c>path_collection</c> ("All Files" = id 0
    /// first). "/" when the chain doesn't start at the user's root (an item shared from
    /// elsewhere), which is where macOS puts every hit.
    /// </summary>
    public string SearchParentPath()
    {
        var entries = PathCollection?.Entries;
        if (entries is not { Count: > 0 } || entries[0].Id != "0")
        {
            return "/";
        }

        return "/" + string.Join('/', entries.Skip(1).Select(e => e.Name));
    }

    /// <summary>RFC 3339, with or without fractional seconds depending on the endpoint.</summary>
    internal static DateTimeOffset? ParseDate(string? raw)
        => !string.IsNullOrEmpty(raw) &&
           DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
}

internal sealed class BoxPathCollection
{
    [JsonPropertyName("entries")]
    public List<BoxPathEntry> Entries { get; set; } = [];
}

internal sealed class BoxPathEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>Folder listings, search results, and the upload/commit responses (one entry).</summary>
internal sealed class BoxFolderItems
{
    [JsonPropertyName("entries")]
    [JsonRequired]
    public List<BoxItem> Entries { get; set; } = [];

    [JsonPropertyName("total_count")]
    public int? TotalCount { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }

    [JsonPropertyName("offset")]
    public int? Offset { get; set; }
}

/// <summary>
/// The <c>shared_link</c> sub-object Box returns for an item. Only this one field is ever
/// asked for (<c>?fields=shared_link</c>), so the envelope stays this small.
/// </summary>
internal sealed class BoxSharedLinkEnvelope
{
    [JsonPropertyName("shared_link")]
    public BoxSharedLink? SharedLink { get; set; }
}

internal sealed class BoxSharedLink
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Direct-download URL. Always null for folders, and for files whose effective permission is preview-only.</summary>
    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("is_password_enabled")]
    public bool? IsPasswordEnabled { get; set; }

    /// <summary>RFC 3339, or absent when the link never expires.</summary>
    [JsonPropertyName("unshared_at")]
    public string? UnsharedAt { get; set; }

    /// <summary>What the link actually grants after enterprise policy: "open", "company" or "collaborators".</summary>
    [JsonPropertyName("effective_access")]
    public string? EffectiveAccess { get; set; }

    /// <summary>"can_download" or "can_preview" after policy is applied.</summary>
    [JsonPropertyName("effective_permission")]
    public string? EffectivePermission { get; set; }
}

internal sealed class BoxUploadSession
{
    [JsonPropertyName("id")]
    [JsonRequired]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("part_size")]
    [JsonRequired]
    public long PartSize { get; set; }

    [JsonPropertyName("session_endpoints")]
    [JsonRequired]
    public Endpoints SessionEndpoints { get; set; } = new();

    internal sealed class Endpoints
    {
        [JsonPropertyName("upload_part")]
        [JsonRequired]
        public string UploadPart { get; set; } = string.Empty;

        [JsonPropertyName("commit")]
        [JsonRequired]
        public string Commit { get; set; } = string.Empty;

        [JsonPropertyName("abort")]
        [JsonRequired]
        public string Abort { get; set; } = string.Empty;
    }
}

internal sealed class BoxUploadedPart
{
    [JsonPropertyName("part")]
    [JsonRequired]
    public BoxPart Part { get; set; } = new();

    internal sealed class BoxPart
    {
        [JsonPropertyName("part_id")]
        [JsonRequired]
        public string PartId { get; set; } = string.Empty;

        [JsonPropertyName("offset")]
        [JsonRequired]
        public long Offset { get; set; }

        [JsonPropertyName("size")]
        [JsonRequired]
        public long Size { get; set; }
    }
}
