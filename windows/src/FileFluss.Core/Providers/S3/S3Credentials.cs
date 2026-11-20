// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Providers.S3;

/// <summary>
/// What an AWS S3, S3-compatible or Synology C2 account stores — the macOS
/// <c>S3Credentials</c>, with the same JSON keys.
/// </summary>
/// <param name="EndpointHost">
/// Custom endpoint host for S3-compatible services (Synology C2, Hetzner, R2, MinIO, Wasabi…),
/// e.g. <c>eu-001.s3.synologyc2.net</c>. Null for AWS, which uses <c>s3.&lt;region&gt;.amazonaws.com</c>.
/// </param>
/// <param name="RootPath">
/// The bucket (optionally bucket/folder) the account is scoped to, as "/bucket/folder". Kept
/// with the credentials so the client knows not to ask for the account's bucket list — a key
/// scoped to one bucket has no permission to see the others (issue #53). Entries stored
/// before bucket scoping existed have none and keep listing the account's buckets.
/// </param>
internal sealed record S3Credentials(
    string AccessKeyId,
    string SecretAccessKey,
    string Region,
    string DisplayName,
    string? EndpointHost = null,
    string? RootPath = null);
