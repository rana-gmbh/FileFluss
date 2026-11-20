// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Models;

/// <summary>
/// Storage usage for one account. Some providers report no upper bound (enterprise plans
/// without an enforced quota) — <see cref="TotalBytes"/> is null then and the UI shows only
/// the used figure.
/// </summary>
public sealed record CloudStorageQuota(long UsedBytes, long? TotalBytes)
{
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Null when there is no positive total — a zero total means "unlimited" (macOS).</summary>
    public long? FreeBytes => TotalBytes is { } total && total > 0 ? Math.Max(0, total - UsedBytes) : null;
}

/// <summary>
/// What the user asked for when creating a public link. Providers ignore fields their API
/// doesn't support; <see cref="ShareLinkCapabilities"/> keeps the UI from offering them.
/// </summary>
public sealed record ShareLinkOptions
{
    public static readonly ShareLinkOptions Default = new();

    /// <summary>Password the recipient must enter. Most providers gate this behind a paid plan.</summary>
    public string? Password { get; init; }

    /// <summary>When the link stops working. S3 presigned links require one (max 7 days).</summary>
    public DateTimeOffset? Expiry { get; init; }

    /// <summary>False asks for view-only. Only honoured where <see cref="ShareLinkCapabilities.SupportsDownloadToggle"/>.</summary>
    public bool AllowDownload { get; init; } = true;
}

/// <summary>A public link created for a remote file.</summary>
public sealed record CloudShareLink(Uri Url)
{
    /// <summary>Serves the bytes directly, where the provider offers it. Null for landing-page-only providers.</summary>
    public Uri? DirectDownloadUrl { get; init; }

    /// <summary>Effective expiry as reported back by the server; null when the link never expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public bool HasPassword { get; init; }

    /// <summary>
    /// False when the link does not work for just anyone — an organisation-only OneDrive link,
    /// a Box link an admin downgraded. The user has to be told.
    /// </summary>
    public bool IsPublic { get; init; } = true;

    /// <summary>Something worth surfacing, e.g. the server granted less than asked for.</summary>
    public string? Note { get; init; }

    /// <summary>The URL copied to the clipboard: direct download when available, else the landing page.</summary>
    public Uri PreferredUrl => DirectDownloadUrl ?? Url;
}

/// <summary>
/// What a provider's sharing API can do — a ceiling, not a guarantee: an admin can switch
/// sharing off and password/expiry are often paid features, so creation can still fail and
/// callers must surface the server's own message.
/// </summary>
public sealed record ShareLinkCapabilities
{
    /// <summary>SFTP, FTP, plain WebDAV, GMX, TeraBox, iCloud, GoPro.</summary>
    public static readonly ShareLinkCapabilities Unsupported = new() { CanCreate = false };

    public bool CanCreate { get; init; }

    public bool CanQueryExisting { get; init; }

    public bool CanUpdate { get; init; }

    public bool CanRemove { get; init; }

    public bool SupportsPassword { get; init; }

    public bool SupportsExpiry { get; init; }

    public bool SupportsDownloadToggle { get; init; }

    public bool PasswordRequiresPaidPlan { get; init; }

    public bool ExpiryRequiresPaidPlan { get; init; }

    /// <summary>S3 presigned URLs are signed for a fixed window.</summary>
    public bool RequiresExpiry { get; init; }

    /// <summary>Longest accepted expiry; S3 SigV4 tops out at 7 days.</summary>
    public TimeSpan? MaximumExpiry { get; init; }

    /// <summary>True when the options sheet has anything to offer; otherwise only "Copy Share Link".</summary>
    public bool HasOptions => SupportsPassword || SupportsExpiry || SupportsDownloadToggle;
}
