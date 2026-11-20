// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Text.Json.Serialization;
using FileFluss.Core.Models;

namespace FileFluss.Core.Providers.Dropbox;

/// <summary>
/// What is stored under <c>dropbox.&lt;account-uuid&gt;</c>. The refresh token is the one that
/// matters: access tokens live four hours and are re-minted from it.
/// </summary>
internal sealed record DropboxCredentials(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string AccountId,
    string DisplayName);

internal sealed class DropboxTokenResponse
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

    [JsonPropertyName("account_id")]
    public string? AccountId { get; set; }
}

internal sealed class DropboxAccountInfo
{
    [JsonPropertyName("account_id")]
    [JsonRequired]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    [JsonRequired]
    public DropboxName Name { get; set; } = new();

    internal sealed class DropboxName
    {
        [JsonPropertyName("display_name")]
        [JsonRequired]
        public string DisplayName { get; set; } = string.Empty;
    }
}

/// <summary>/users/get_space_usage. <c>allocation</c> is "individual" or "team"; both carry <c>allocated</c>.</summary>
internal sealed class DropboxSpaceUsage
{
    [JsonPropertyName("used")]
    [JsonRequired]
    public long Used { get; set; }

    [JsonPropertyName("allocation")]
    [JsonRequired]
    public DropboxAllocation Allocation { get; set; } = new();

    internal sealed class DropboxAllocation
    {
        [JsonPropertyName("allocated")]
        public long? Allocated { get; set; }
    }
}

internal sealed class DropboxListFolderResponse
{
    [JsonPropertyName("entries")]
    [JsonRequired]
    public List<DropboxEntry> Entries { get; set; } = [];

    [JsonPropertyName("cursor")]
    [JsonRequired]
    public string Cursor { get; set; } = string.Empty;

    [JsonPropertyName("has_more")]
    [JsonRequired]
    public bool HasMore { get; set; }
}

internal sealed class DropboxSearchResponse
{
    [JsonPropertyName("matches")]
    [JsonRequired]
    public List<Match> Matches { get; set; } = [];

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    internal sealed class Match
    {
        [JsonPropertyName("metadata")]
        [JsonRequired]
        public MatchMetadata Metadata { get; set; } = new();
    }

    /// <summary>search_v2 wraps the entry once more: <c>{".tag": "metadata", "metadata": {…}}</c>.</summary>
    internal sealed class MatchMetadata
    {
        [JsonPropertyName("metadata")]
        [JsonRequired]
        public DropboxEntry Metadata { get; set; } = new();
    }
}

internal sealed class DropboxUploadSessionStart
{
    [JsonPropertyName("session_id")]
    [JsonRequired]
    public string SessionId { get; set; } = string.Empty;
}

/// <summary>
/// The part of Dropbox's <c>SharedLinkMetadata</c> we use. Returned both by
/// /sharing/create_shared_link_with_settings and, for a link that already existed, nested
/// inside that call's error body.
/// </summary>
internal sealed class DropboxSharedLinkMetadata
{
    /// <summary>Preview link, ending in <c>?dl=0</c>.</summary>
    [JsonPropertyName("url")]
    [JsonRequired]
    public string Url { get; set; } = string.Empty;

    /// <summary>ISO 8601 UTC; present only on a link that is set to expire.</summary>
    [JsonPropertyName("expires")]
    public string? Expires { get; set; }

    [JsonPropertyName("link_permissions")]
    public LinkPermissions? Permissions { get; set; }

    internal sealed class LinkPermissions
    {
        [JsonPropertyName("require_password")]
        public bool? RequirePassword { get; set; }

        [JsonPropertyName("allow_download")]
        public bool? AllowDownload { get; set; }
    }
}

internal sealed class DropboxListSharedLinksResponse
{
    [JsonPropertyName("links")]
    [JsonRequired]
    public List<DropboxSharedLinkMetadata> Links { get; set; } = [];
}

/// <summary>
/// Error envelope of the /sharing/* calls. The <c>shared_link_already_exists</c> member isn't
/// really a failure: its payload carries the metadata of the link the file already has.
/// </summary>
internal sealed class DropboxShareErrorEnvelope
{
    /// <summary>Dropbox's own summary, e.g. <c>settings_error/not_authorized/</c>. Surfaced verbatim.</summary>
    [JsonPropertyName("error_summary")]
    public string? ErrorSummary { get; set; }

    [JsonPropertyName("error")]
    public ErrorDetail? Error { get; set; }

    internal sealed class ErrorDetail
    {
        [JsonPropertyName(".tag")]
        public string? Tag { get; set; }

        [JsonPropertyName("shared_link_already_exists")]
        public AlreadyExists? SharedLinkAlreadyExists { get; set; }
    }

    /// <summary><c>.tag</c> is "metadata" (with the link) or "unverified" (without).</summary>
    internal sealed class AlreadyExists
    {
        [JsonPropertyName("metadata")]
        public DropboxSharedLinkMetadata? Metadata { get; set; }
    }
}

/// <summary>One <c>Metadata</c> union member: file, folder or deleted.</summary>
internal sealed class DropboxEntry
{
    [JsonPropertyName(".tag")]
    [JsonRequired]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    [JsonRequired]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("path_lower")]
    public string? PathLower { get; set; }

    [JsonPropertyName("path_display")]
    public string? PathDisplay { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("server_modified")]
    public string? ServerModified { get; set; }

    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; set; }

    public bool IsFolder => Tag == "folder";

    /// <summary>Null for "deleted" entries (only listed with include_deleted, but never trusted).</summary>
    public CloudFileItem? ToCloudFileItem()
    {
        if (Tag is not ("file" or "folder"))
        {
            return null;
        }

        var path = PathDisplay ?? PathLower ?? "/" + Name;
        return new CloudFileItem(
            Id ?? (IsFolder ? "d_" + Name : "f_" + Name),
            Name,
            path,
            IsFolder,
            Size ?? 0,
            ParseDate(ServerModified) ?? CloudFileItem.UnknownDate,
            ContentHash)
        {
            IsHidden = CloudFileItem.IsDotHidden(Name),
        };
    }

    /// <summary>Dropbox timestamps are whole-second UTC: "2015-05-12T15:50:38Z".</summary>
    internal static DateTimeOffset? ParseDate(string? raw)
        => !string.IsNullOrEmpty(raw) &&
           DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
}
