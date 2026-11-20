// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Models;

/// <summary>
/// Which share entries a file's context menu offers — the macOS <c>NativeCloudFileList</c>
/// rules. "Copy Share Link" is always there; a file known to be shared adds a checked,
/// disabled "Shared" marker and actions for the link it has, an unshared one the options
/// dialog when there is anything to set.
/// </summary>
public sealed record ShareLinkMenu(bool SharedMarker, bool ChangeSharing, bool StopSharing, bool ShareWithOptions)
{
    /// <param name="capabilities">The provider's sharing capabilities.</param>
    /// <param name="isShared">
    /// True or false once looked up; null while unknown. Only providers that can read their
    /// links back (<see cref="ShareLinkCapabilities.CanQueryExisting"/>) ever report "shared" —
    /// S3-style accounts mint a fresh signed URL every time and have nothing to withdraw.
    /// </param>
    public static ShareLinkMenu For(ShareLinkCapabilities capabilities, bool? isShared)
    {
        if (isShared == true && capabilities.CanQueryExisting)
        {
            return new ShareLinkMenu(
                SharedMarker: true,
                ChangeSharing: capabilities.CanUpdate && capabilities.HasOptions,
                StopSharing: capabilities.CanRemove,
                ShareWithOptions: false);
        }

        return new ShareLinkMenu(SharedMarker: false, ChangeSharing: false, StopSharing: false, ShareWithOptions: capabilities.HasOptions);
    }
}

/// <summary>Reading a provider's refusal of a share request (macOS <c>noteShareOptionsRejected</c>).</summary>
public static class ShareLinkRefusal
{
    private static readonly string[] PlanRefusalWords =
        ["forbidden", "not_authorized", "not authorized", "settings_error", "403", "payment", "upgrade", "premium", "plan", "subscription"];

    /// <summary>
    /// True for refusals that look like a plan or policy limit. A typo'd password or a network
    /// blip must not permanently hide the password or expiry option.
    /// </summary>
    public static bool IsPlanRefusal(string message)
    {
        var text = message.ToLowerInvariant();
        return PlanRefusalWords.Any(text.Contains);
    }

    /// <summary>
    /// The options a refusal applies to, or null when it wasn't about options at all — which
    /// of "a password and an expiry date", "a password", "an expiry date" to name.
    /// </summary>
    public static string? RefusedOptionsKey(ShareLinkOptions options, string message)
    {
        var usedPassword = !string.IsNullOrEmpty(options.Password);
        var usedExpiry = options.Expiry is not null;
        if (!(usedPassword || usedExpiry) || !IsPlanRefusal(message))
        {
            return null;
        }

        return (usedPassword, usedExpiry) switch
        {
            (true, true) => "a password and an expiry date",
            (true, false) => "a password",
            _ => "an expiry date",
        };
    }
}
