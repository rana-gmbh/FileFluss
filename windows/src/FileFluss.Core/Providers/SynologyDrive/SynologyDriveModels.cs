// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json.Serialization;

namespace FileFluss.Core.Providers.SynologyDrive;

/// <summary>
/// What a Synology Drive account stores — the macOS <c>SynologyDriveCredentials</c>, same JSON
/// keys. DSM's web API has no long-lived token: each session logs in with the password.
/// </summary>
internal sealed record SynologyDriveCredentials(
    [property: JsonPropertyName("serverURL")] string ServerUrl,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("allowSelfSignedCertificate")] bool AllowSelfSignedCertificate,
    [property: JsonPropertyName("displayName")] string DisplayName);

/// <summary>Every DSM web API answer: <c>{"success":…,"data":…,"error":{"code":…}}</c>.</summary>
internal sealed record SynologyEnvelope<T>(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("data")] T? Data,
    [property: JsonPropertyName("error")] SynologyErrorInfo? Error);

internal sealed record SynologyErrorInfo([property: JsonPropertyName("code")] int Code);

/// <summary>Stands in for endpoints that succeed without a payload (delete, rename, sharing edit).</summary>
internal sealed record SynologyEmpty;

internal sealed record SynologyAuthData([property: JsonPropertyName("sid")] string? Sid);

/// <summary><c>additional.time</c>: POSIX timestamps.</summary>
internal sealed record SynologyTime([property: JsonPropertyName("mtime")] double? Mtime)
{
    public DateTimeOffset? ModifiedDate => Mtime is { } seconds ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)) : null;
}

internal sealed record SynologyAdditional(
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("time")] SynologyTime? Time);

internal sealed record SynologyShare(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("additional")] SynologyAdditional? Additional);

internal sealed record SynologyListSharesData([property: JsonPropertyName("shares")] IReadOnlyList<SynologyShare>? Shares);

/// <summary>A file or folder. <see cref="Code"/> is set on a getinfo entry for a path that failed (408: no such file).</summary>
internal sealed record SynologyFileEntry(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("isdir")] bool IsDir,
    [property: JsonPropertyName("additional")] SynologyAdditional? Additional,
    [property: JsonPropertyName("code")] int? Code);

internal sealed record SynologyListData([property: JsonPropertyName("files")] IReadOnlyList<SynologyFileEntry>? Files);

internal sealed record SynologyTaskStartData([property: JsonPropertyName("taskid")] string? TaskId);

internal sealed record SynologyTaskStatusData(
    [property: JsonPropertyName("finished")] bool? Finished,
    [property: JsonPropertyName("error")] System.Text.Json.JsonElement? Error = null)
{
    /// <summary>The task's own failure code: DSM reports it as <c>{"code":…}</c> (or a bare number) beside <c>finished</c>.</summary>
    public int? ErrorCode => Error switch
    {
        { ValueKind: System.Text.Json.JsonValueKind.Number } number when number.TryGetInt32(out var code) => code,
        { ValueKind: System.Text.Json.JsonValueKind.Object } error
            when error.TryGetProperty("code", out var value) && value.TryGetInt32(out var code) => code,
        _ => null,
    };
}

/// <summary>
/// <c>SYNO.FileStation.Sharing</c> <c>create</c>: one entry per requested path, each with its
/// own <c>error</c> even when the envelope reports success.
/// </summary>
internal sealed record SynologySharingLinksData([property: JsonPropertyName("links")] IReadOnlyList<SynologyCreatedLink>? Links);

internal sealed record SynologyCreatedLink(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("error")] int? Error);

/// <summary>
/// <c>list</c> and <c>getinfo</c>: the links the signed-in user owns, with the settings DSM
/// holds for each. Field coverage varies across DSM 6/7, so everything is optional.
/// </summary>
internal sealed record SynologySharingListData(
    [property: JsonPropertyName("links")] IReadOnlyList<SynologySharingEntry>? Links,
    [property: JsonPropertyName("total")] int? Total);

/// <param name="Path">The shared path in File Station's own form.</param>
/// <param name="DateExpired">"0" or absent when the link never expires, otherwise "yyyy-MM-dd" (DSM 7 may add a time).</param>
/// <param name="ExpireTimes">Remaining download allowance; 0 is unlimited.</param>
internal sealed record SynologySharingEntry(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("date_expired")] string? DateExpired,
    [property: JsonPropertyName("has_password")] bool? HasPassword,
    [property: JsonPropertyName("expire_times")] int? ExpireTimes);
