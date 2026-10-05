// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json.Serialization;

namespace FileFluss.Core.Providers.Seafile;

/// <summary>
/// Persisted credentials. The API token from <c>/api2/auth-token/</c> doesn't expire on a clock —
/// only an admin or a password change revokes it. The username is kept for the display name;
/// only the token matters for API calls. The password is never stored.
/// </summary>
internal sealed record SeafileCredentials(
    [property: JsonPropertyName("serverURL")] string ServerUrl,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("token")] string Token)
{
    /// <summary>
    /// Accept the certificate the configured host presents even when it doesn't chain to a
    /// trusted CA (self-hosted boxes with a self-signed or LAN-only certificate). Scoped to that
    /// host. Defaults to false so older entries keep strict behaviour.
    /// </summary>
    [JsonPropertyName("allowSelfSignedCertificate")]
    public bool AllowSelfSignedCertificate { get; init; }
}

internal sealed record SeafileTokenResponse([property: JsonPropertyName("token")] string? Token);

/// <summary>An entry of <c>/api2/repos/</c>.</summary>
internal sealed record SeafileLibrary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("owner")] string? Owner,
    [property: JsonPropertyName("encrypted")] bool? Encrypted,
    [property: JsonPropertyName("mtime")] long? Mtime,
    [property: JsonPropertyName("size")] long? Size);

/// <summary>An entry of <c>/api2/repos/{id}/dir/</c>.</summary>
internal sealed record SeafileDirEntry(
    // "file" or "dir"
    [property: JsonPropertyName("type")] string? Type,
    // SHA1 of the file content / dir tree hash
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("mtime")] long? Mtime);

/// <summary><c>/api2/repos/{id}/file/detail/</c>.</summary>
internal sealed record SeafileFileDetail(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("mtime")] long? Mtime,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("size")] long? Size);

internal sealed record SeafileAccountInfo(
    [property: JsonPropertyName("usage")] long? Usage,
    [property: JsonPropertyName("total")] long? Total);

/// <summary>
/// Body of <c>POST /api/v2.1/share-links/</c>. Null members are left out: servers reject
/// <c>expire_days: null</c> but accept the key being absent.
/// </summary>
internal sealed record SeafileShareLinkRequest(
    [property: JsonPropertyName("repo_id")] string RepoId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("password"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Password,
    [property: JsonPropertyName("expire_days"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ExpireDays,
    [property: JsonPropertyName("permissions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SeafileSharePermissions? Permissions);

internal sealed record SeafileSharePermissions(
    [property: JsonPropertyName("can_edit")] bool? CanEdit,
    [property: JsonPropertyName("can_download")] bool? CanDownload,
    [property: JsonPropertyName("can_upload")] bool? CanUpload);

/// <summary>
/// A share link as the server reports it. <see cref="Link"/> is the landing page
/// (<c>https://server/f/&lt;token&gt;/</c>); the rest is echoed-back state.
/// </summary>
internal sealed record SeafileShareLink(
    [property: JsonPropertyName("link")] string? Link,
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("expire_date")] string? ExpireDate,
    [property: JsonPropertyName("is_expired")] bool? IsExpired,
    [property: JsonPropertyName("permissions")] SeafileSharePermissions? Permissions,
    // Only some releases report this on a listed link (as a hash, or null). Absent means
    // "unknown", not "no password".
    [property: JsonPropertyName("password")] string? Password,
    // Seafile 11+ answers the list endpoint with this flag; older ones leave it out.
    [property: JsonPropertyName("is_password_set")] bool? IsPasswordSet);

/// <summary>
/// Error bodies differ across versions and API generations: v2.1 endpoints use
/// <c>error_msg</c>, DRF's validation layer <c>detail</c>, a few older paths <c>error</c>.
/// </summary>
internal sealed record SeafileErrorBody(
    [property: JsonPropertyName("error_msg")] string? ErrorMsg,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("error")] string? Error)
{
    public string? Message => new[] { ErrorMsg, Detail, Error }.FirstOrDefault(m => !string.IsNullOrEmpty(m));
}
