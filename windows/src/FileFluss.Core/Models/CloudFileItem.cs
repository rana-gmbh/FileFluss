// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Models;

/// <summary>
/// Whether the file is fully present locally or held remotely. Only meaningful for
/// providers with a partial-local concept (an iCloud Drive placeholder, a OneDrive
/// Files-On-Demand file).
/// </summary>
public enum CloudDownloadStatus
{
    Local,
    Evicted,
    Downloading,
}

/// <summary>
/// What kind of navigational node an item is. Most are <see cref="Normal"/>; the others are
/// synthetic entries (Google Drive's "Shared drives" tree) the UI must treat differently.
/// </summary>
public enum CloudItemRole
{
    Normal,

    /// <summary>A synthetic grouping node: navigable, but no copy/move/rename/delete and nothing can be created inside.</summary>
    VirtualContainer,

    /// <summary>A shared drive's root: browsable and copyable-from, but not renamable, deletable or movable.</summary>
    DriveRoot,
}

/// <summary>
/// One entry in a folder listing — of a cloud account, and (with <see cref="CreationDate"/>
/// and <see cref="IsHidden"/> filled in) of a local folder too, so a panel deals with one
/// type whatever it shows.
///
/// <para>Paths are provider paths: POSIX-style (<c>/Photos/2026</c>) for every cloud
/// provider, native Windows paths for the local file system.</para>
/// </summary>
public sealed record CloudFileItem
{
    /// <summary>The date providers report when they know none. Sorts first, renders as "--".</summary>
    public static readonly DateTimeOffset UnknownDate = DateTimeOffset.MinValue;

    public CloudFileItem(string id, string name, string path, bool isDirectory, long size, DateTimeOffset modificationDate, string? checksum = null)
    {
        Id = id;
        Name = name;
        Path = path;
        IsDirectory = isDirectory;
        Size = size;
        ModificationDate = modificationDate;
        Checksum = checksum;
    }

    /// <summary>Stable identity within a listing: the provider's own id where it has one, otherwise the path.</summary>
    public string Id { get; init; }

    public string Name { get; init; }

    public string Path { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>Bytes; 0 for folders unless the provider reports a folder size.</summary>
    public long Size { get; init; }

    public DateTimeOffset ModificationDate { get; init; }

    public string? Checksum { get; init; }

    public CloudDownloadStatus DownloadStatus { get; init; } = CloudDownloadStatus.Local;

    /// <summary>Overrides the default folder/file glyph for synthetic entries (e.g. "drive").</summary>
    public string? IconOverride { get; init; }

    public CloudItemRole Role { get; init; } = CloudItemRole.Normal;

    /// <summary>
    /// Replaces the Kind column's text derived from the extension. Providers whose items
    /// aren't ordinary files say what a row actually is — a GitHub account labels a
    /// repository private or public there, the one thing its overview has to make obvious.
    /// </summary>
    public string? KindOverride { get; init; }

    /// <summary>Known for local files and a few providers; <see cref="UnknownDate"/> otherwise.</summary>
    public DateTimeOffset CreationDate { get; init; } = UnknownDate;

    /// <summary>
    /// Hidden as the source defines it: the Hidden attribute locally, a leading dot in a
    /// cloud listing (see <see cref="IsDotHidden"/>).
    /// </summary>
    public bool IsHidden { get; init; }

    public bool IsSymlink { get; init; }

    public bool HasKnownModificationDate => ModificationDate > UnknownDate;

    /// <summary>Lower-case extension without the dot; empty for folders and extensionless names.</summary>
    public string Extension
    {
        get
        {
            if (IsDirectory)
            {
                return string.Empty;
            }

            var dot = Name.LastIndexOf('.');
            return dot <= 0 || dot == Name.Length - 1 ? string.Empty : Name[(dot + 1)..].ToLowerInvariant();
        }
    }

    /// <summary>Unix convention used for cloud listings: names starting with a dot.</summary>
    public static bool IsDotHidden(string name) => name.StartsWith('.');

    /// <summary>Shared drives and their containers can be opened but not edited.</summary>
    public bool IsModifiable => Role == CloudItemRole.Normal;
}
