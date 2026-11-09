// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json.Serialization;

namespace FileFluss.Core.Models;

public enum PanelSide
{
    Left,
    Right,
}

public static class PanelSideExtensions
{
    public static PanelSide Other(this PanelSide side) => side == PanelSide.Left ? PanelSide.Right : PanelSide.Left;
}

/// <summary>
/// Forces drag & drop to one outcome, bypassing the "Move or Copy?" prompt. Session-only,
/// so a stuck Move Mode can't quietly destroy files across launches.
/// </summary>
public enum DragDropMode
{
    Ask,
    Copy,
    Move,
}

/// <summary>
/// Where a panel is: a local folder (Windows path; "" = This PC), a folder of a cloud account,
/// or a folder of an unplugged drive browsed from its offline index (POSIX path relative to the drive).
/// </summary>
public sealed record PanelLocation
{
    private const string OfflineDriveKind = "offline-drive";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "local";

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("accountId")]
    public Guid? AccountId { get; init; }

    /// <summary>The drive's "vol:…" id, for an offline drive.</summary>
    [JsonPropertyName("driveId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DriveId { get; init; }

    /// <summary>
    /// For a folder inside an archive: the archive's own path where it is stored — a Windows
    /// path, or a POSIX path on the account in <see cref="ArchiveAccountId"/>. <see cref="Path"/>
    /// is then the POSIX path inside the archive ("/" for its root).
    /// </summary>
    [JsonPropertyName("archive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArchivePath { get; init; }

    /// <summary>The account an archive is stored on; null for an archive on this PC. Kept apart from <see cref="AccountId"/> so nothing mistakes the archive for the account.</summary>
    [JsonPropertyName("archiveAccountId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ArchiveAccountId { get; init; }

    [JsonIgnore]
    public bool IsCloud => Kind == "cloud" && AccountId is not null;

    [JsonIgnore]
    public bool IsOfflineDrive => Kind == OfflineDriveKind && DriveId is not null;

    /// <summary>A folder inside an archive (ZIP, RAR, 7z, a tarball), browsed read-only.</summary>
    [JsonIgnore]
    public bool IsArchive => Kind == ArchiveKind && ArchivePath is not null;

    /// <summary>A folder on disk (not a cloud account, not an unplugged drive, not inside an archive).</summary>
    [JsonIgnore]
    public bool IsLocal => !IsCloud && !IsOfflineDrive && !IsArchive;

    /// <summary>The folder an archive sits in, for "up" out of its root and for what a session remembers.</summary>
    [JsonIgnore]
    public PanelLocation? ArchiveContainer => !IsArchive ? null
        : ArchiveAccountId is { } account ? Cloud(account, IO.CloudPath.Parent(ArchivePath!))
        : Local(System.IO.Path.GetDirectoryName(ArchivePath!) ?? string.Empty);

    /// <summary>The archive's own file name.</summary>
    [JsonIgnore]
    public string ArchiveName => ArchivePath is null ? string.Empty
        : ArchiveAccountId is null ? System.IO.Path.GetFileName(ArchivePath) : IO.CloudPath.Name(ArchivePath);

    public static PanelLocation Local(string path) => new() { Kind = "local", Path = path };

    public static PanelLocation Cloud(Guid accountId, string path) => new() { Kind = "cloud", AccountId = accountId, Path = path };

    public static PanelLocation OfflineDrive(string driveId, string path) => new() { Kind = OfflineDriveKind, DriveId = driveId, Path = path };

    /// <summary>A folder inside the archive at <paramref name="archivePath"/> (on <paramref name="accountId"/>, or on this PC when null).</summary>
    public static PanelLocation Archive(string archivePath, Guid? accountId, string innerPath = "/")
        => new() { Kind = ArchiveKind, ArchivePath = archivePath, ArchiveAccountId = accountId, Path = innerPath };

    private const string ArchiveKind = "archive";
}

/// <summary>How each panel starts (issue #49).</summary>
public enum PanelStartupMode
{
    Home,
    LastSession,
    Specific,
}

/// <summary>
/// A sidebar favourite: a local folder or a cloud folder. Both kinds share one ordered list
/// per panel so they can be reordered, renamed and removed uniformly. The system defaults
/// (Home, Desktop, …) are seeded once as ordinary entries.
/// </summary>
public sealed record SidebarFavorite
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>A glyph name (see the app's icon map) or "@logo:&lt;provider&gt;".</summary>
    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "folder";

    [JsonPropertyName("location")]
    public PanelLocation Location { get; init; } = PanelLocation.Local(string.Empty);

    [JsonIgnore]
    public bool IsCloud => Location.IsCloud;

    public static SidebarFavorite ForLocal(string name, string icon, string path)
        => new() { DisplayName = name, Icon = icon, Location = PanelLocation.Local(path) };

    public static SidebarFavorite ForCloud(string name, CloudProviderType provider, Guid accountId, string path)
        => new() { DisplayName = name, Icon = "@logo:" + provider.LogoAssetName(), Location = PanelLocation.Cloud(accountId, path) };

    /// <summary>Home, Desktop, Documents, Downloads, Pictures, Music, Videos — the Windows set of known folders.</summary>
    public static List<SidebarFavorite> DefaultLocalFavorites()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<SidebarFavorite>
        {
            ForLocal("Home", "home", home),
            ForLocal("Desktop", "desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ForLocal("Documents", "documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ForLocal("Downloads", "downloads", System.IO.Path.Combine(home, "Downloads")),
            ForLocal("Pictures", "pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ForLocal("Music", "music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            ForLocal("Videos", "videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        };
        return list.Where(f => !string.IsNullOrEmpty(f.Location.Path)).ToList();
    }
}

/// <summary>Explorer-style row heights (macOS: Finder's row size toggle).</summary>
public enum FileListRowSize
{
    Small,
    Medium,
    Large,
}

public enum FileListColumn
{
    Name,
    DateModified,
    DateCreated,
    Size,
    Kind,
}

public enum SortField
{
    Name,
    Date,
    Size,
    Kind,
    DateCreated,
}
