// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Search;

/// <summary>What kind of drive the Drives section shows (macOS <c>Drive.Kind</c>).</summary>
public enum DriveKind
{
    /// <summary>USB, Thunderbolt, SD card, optical disc — a volume that leaves with its hardware.</summary>
    External,

    /// <summary>A mapped network share (SMB, WebDAV redirector).</summary>
    Network,

    /// <summary>
    /// A second partition or volume on a built-in disk. Reported on macOS by a user who had one
    /// and no way to reach it: the section was written as "external and network", so every
    /// internal volume that wasn't the startup disk was shown nowhere.
    /// </summary>
    InternalVolume,
}

public static class DriveKinds
{
    /// <summary>The macOS raw value, used in the index (<c>drive-external</c>, …).</summary>
    public static string RawValue(this DriveKind kind) => kind switch
    {
        DriveKind.Network => "network",
        DriveKind.InternalVolume => "internalVolume",
        _ => "external",
    };

    public static DriveKind FromRawValue(string raw) => raw switch
    {
        "network" => DriveKind.Network,
        "internalVolume" => DriveKind.InternalVolume,
        _ => DriveKind.External,
    };

    /// <summary><c>indexed_sources.kind</c> for a drive of this kind.</summary>
    public static string IndexKind(this DriveKind kind) => "drive-" + kind.RawValue();

    /// <summary>The drive kind of an <c>indexed_sources.kind</c>; null for cloud accounts.</summary>
    public static DriveKind? FromIndexKind(string indexKind)
        => indexKind.StartsWith("drive-", StringComparison.Ordinal) ? FromRawValue(indexKind["drive-".Length..]) : null;

    public static string DisplayName(this DriveKind kind) => kind switch
    {
        DriveKind.Network => Localization.L("Network Drive"),
        DriveKind.InternalVolume => Localization.L("Internal Volume"),
        _ => Localization.L("External Drive"),
    };

    /// <summary>Glyph name, matching <c>LocalFileProvider</c>'s drive icons.</summary>
    public static string Icon(this DriveKind kind) => kind switch
    {
        DriveKind.Network => "drive.network",
        DriveKind.InternalVolume => "drive",
        _ => "drive.removable",
    };
}

/// <summary>
/// A mounted volume as the app sees it. macOS identifies drives by volume UUID; on Windows the
/// app supplies the volume serial number (<c>GetVolumeInformation</c>) as <see cref="Id"/>, so
/// a drive keeps its identity — and its index — whichever letter it comes back under.
/// </summary>
/// <param name="Id">Volume serial number, e.g. "1A2B-3C4D".</param>
/// <param name="Label">Volume label; may be empty.</param>
/// <param name="RootPath">Where it is mounted now, e.g. <c>E:\</c>.</param>
public sealed record DriveIdentity(string Id, string Label, string RootPath, DriveType DriveType)
{
    /// <summary><see cref="DriveInfo.IsReady"/>: a card reader with no card, a drive mid-eject.</summary>
    public bool IsReady { get; init; } = true;

    /// <summary>
    /// True for a fixed disk on an external bus (USB, Thunderbolt). Windows reports many USB
    /// SSDs and enclosures as <see cref="DriveType.Fixed"/>, as macOS reports many as
    /// non-removable; the app can tell from the storage bus type.
    /// </summary>
    public bool IsExternalBus { get; init; }

    /// <summary>The source id its index is stored under.</summary>
    public string SourceId => KnownDrive.SourceIdFor(Id);
}

/// <summary>
/// A drive the app knows about: mounted now, or indexed before and unplugged since (macOS
/// <c>Drive</c>). Persisted in the index database so an indexed drive stays listed — offline,
/// with its indexed tree browsable — after it is unplugged.
/// </summary>
public sealed record KnownDrive
{
    /// <summary>"vol:&lt;serial&gt;" — the drive's id everywhere, and its index source id.</summary>
    public required string Id { get; init; }

    public required string VolumeId { get; init; }

    /// <summary>The volume label as last seen; may be empty.</summary>
    public string Label { get; init; } = string.Empty;

    public required DriveKind Kind { get; init; }

    /// <summary>Where it was mounted last, kept while it is offline ("was at E:\").</summary>
    public string? LastRootPath { get; init; }

    /// <summary>The last successful index run; null = never indexed.</summary>
    public DateTimeOffset? LastIndexed { get; init; }

    /// <summary>Entries the last index run stored.</summary>
    public int TotalFiles { get; init; }

    public long TotalBytes { get; init; }

    /// <summary>Mounted right now. Filled in by <see cref="DriveRegistry"/>; not persisted.</summary>
    public bool IsOnline { get; init; }

    /// <summary>The current root when online. Filled in by <see cref="DriveRegistry"/>; not persisted.</summary>
    public string? RootPath { get; init; }

    public bool IsIndexed => LastIndexed is not null;

    /// <summary>The label, or "External Drive" and the like for an unlabelled volume.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Kind.DisplayName() : Label;

    public string Icon => Kind.Icon();

    public static string SourceIdFor(string volumeId) => "vol:" + volumeId;
}

/// <summary>Which mounted volumes belong in the Drives section (macOS <c>DriveMonitor.kind</c>).</summary>
public static class DriveClassifier
{
    /// <summary>
    /// What a mounted volume is, from the facts Windows reports about it; null means "don't
    /// show this". Pure, so it can be tested without plugging disks in.
    /// </summary>
    /// <param name="systemRoot">The Windows volume (<c>C:\</c>); defaults to the running system's.</param>
    public static DriveKind? Classify(DriveIdentity drive, string? systemRoot = null)
    {
        // A volume Windows says isn't ready has nothing to browse (macOS: not browsable).
        if (!drive.IsReady)
        {
            return null;
        }

        // The startup volume: it is "This PC" and Home, not a drive to index for offline use.
        systemRoot ??= Path.GetPathRoot(Environment.SystemDirectory);
        if (!string.IsNullOrEmpty(systemRoot) && SameRoot(drive.RootPath, systemRoot))
        {
            return null;
        }

        return drive.DriveType switch
        {
            DriveType.Network => DriveKind.Network,

            // An optical disc is a removable local volume, as on macOS — and an archive DVD is
            // exactly what one indexes to search while it sits on the shelf.
            DriveType.Removable or DriveType.CDRom => DriveKind.External,

            // Don't gate on Removable alone: many USB disks report Fixed.
            DriveType.Fixed => drive.IsExternalBus ? DriveKind.External : DriveKind.InternalVolume,

            // RAM disks, and volumes Windows can't place, aren't somewhere to keep files.
            _ => null,
        };
    }

    /// <summary>"C:\" and "C:" are the same volume.</summary>
    internal static bool SameRoot(string a, string b)
        => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
