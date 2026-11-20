// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json.Serialization;

namespace FileFluss.Core.Providers.Jottacloud;

/// <summary>
/// What FileFluss keeps for a Jottacloud account across launches. The pasted Personal Login
/// Token is exchanged once for an OAuth refresh token; the access token (≈1 h) lives only in
/// memory.
///
/// <para><b>Rotation.</b> Jottacloud rotates the refresh token on every refresh and detects
/// reuse: presenting an old one invalidates the whole token family and forces the user to
/// paste a new personal token. Every rotated value is therefore persisted immediately.</para>
/// </summary>
internal sealed record JottacloudCredentials(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("refreshToken")] string RefreshToken,
    // Discovered from the personal token's well-known link and persisted, so refreshes after a
    // relaunch don't need the (discarded) personal token.
    [property: JsonPropertyName("tokenEndpoint")] string TokenEndpoint);

/// <summary>The base64url-encoded JSON inside a Personal Login Token.</summary>
internal sealed record JottacloudLoginToken(
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("well_known_link")] string? WellKnownLink,
    [property: JsonPropertyName("auth_token")] string? AuthToken);

internal sealed record JottacloudWellKnown(
    [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint);

internal sealed record JottacloudTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn);

/// <summary>Body of <c>POST files/v1/allocate</c>.</summary>
internal sealed record JottacloudAllocateRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("md5")] string Md5,
    [property: JsonPropertyName("created")] string Created,
    [property: JsonPropertyName("modified")] string Modified);

internal sealed record JottacloudAllocateResponse(
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("upload_url")] string? UploadUrl,
    [property: JsonPropertyName("resume_pos")] long? ResumePos);
