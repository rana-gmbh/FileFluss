// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.App.Services;

/// <summary>
/// Segoe Fluent Icons code points (Segoe MDL2 Assets on Windows 10 has the same ones) —
/// the Windows counterparts of the SF Symbols the macOS app uses.
/// </summary>
public static class Glyphs
{
    public const string FontFamily = "Segoe Fluent Icons, Segoe MDL2 Assets";

    public const string Back = "";
    public const string Forward = "";
    public const string Up = "";
    public const string Refresh = "";
    public const string Search = "";
    public const string Sync = "";
    public const string Compare = "";
    public const string Settings = "";
    public const string Add = "";
    public const string Delete = "";
    public const string Copy = "";
    public const string Cut = "";
    public const string Paste = "";
    public const string Rename = "";
    public const string NewFolder = "";
    public const string Folder = "";
    public const string OpenFolder = "";
    public const string Home = "";
    public const string Document = "";
    public const string Download = "";
    public const string Upload = "";
    public const string Pictures = "";
    public const string Music = "";
    public const string Video = "";
    public const string Desktop = "";
    public const string HardDrive = "";
    public const string Usb = "";
    public const string NetworkDrive = "";
    public const string Cloud = "";
    public const string Eye = "";
    public const string Hide = "";
    public const string Link = "";
    public const string Share = "";
    public const string Info = "";
    public const string Terminal = "";
    public const string Filter = "";
    public const string More = "";
    public const string Cancel = "";
    public const string CheckMark = "";
    public const string Warning = "";
    public const string Error = "";
    public const string Lock = "";
    public const string TwoPanes = "";
    public const string SinglePane = "";
    public const string Pin = "";
    public const string MoveTo = "";
    public const string Transfers = "";
    public const string Pie = "";
    public const string Offline = "";
    public const string Star = "";
    public const string Help = "";
    public const string OpenInNew = "";
    public const string Calculator = "";
    public const string Mount = "";
    public const string Camera = "";
    public const string ChevronDown = "";
    public const string ChevronRight = "";
    public const string SidebarCollapse = "";

    public const string TextDocument = "";
    public const string Heart = "";
    public const string Flag = "";
    public const string Bookmark = "";
    public const string Tag = "";
    public const string Work = "";
    public const string Archive = "";
    public const string Inbox = "";
    public const string Send = "";
    public const string Person = "";

    /// <summary>The prefix of a favourite icon that is a provider logo: "@logo:Dropbox".</summary>
    public const string LogoPrefix = "@logo:";

    /// <summary>
    /// The "Change Icon" choices (macOS <c>FavoriteIconLibrary.allSymbols</c>): stored under the
    /// macOS SF Symbol names so the two apps mean the same thing by them, shown as the nearest
    /// Segoe Fluent glyph. The names are the macOS menu's English labels, but for Videos.
    /// </summary>
    public static readonly IReadOnlyList<(string Icon, string Name)> FavoriteIconLibrary =
    [
        ("house", "Home"),
        ("menubar.dock.rectangle", "Desktop"),
        ("doc", "Document"),
        ("doc.text", "Text Document"),
        ("arrow.down.circle", "Downloads"),
        ("photo", "Pictures"),
        ("music.note", "Music"),
        ("film", "Videos"), // macOS "Movies"; Windows calls the folder Videos
        ("folder.fill", "Folder"),
        ("cloud.fill", "Cloud"),
        ("star.fill", "Star"),
        ("heart.fill", "Heart"),
        ("flag.fill", "Flag"),
        ("bookmark.fill", "Bookmark"),
        ("tag.fill", "Tag"),
        ("briefcase.fill", "Work"),
        ("archivebox.fill", "Archive"),
        ("tray.fill", "Inbox"),
        ("paperplane.fill", "Sent"),
        ("person.crop.circle.fill", "Personal"),
    ];

    /// <summary>The glyph for a favourite's stored icon name (Windows names and the macOS SF Symbol names).</summary>
    public static string ForFavorite(string icon) => icon switch
    {
        "home" or "house" or "house.fill" => Home,
        "desktop" or "menubar.dock.rectangle" or "desktopcomputer" => Desktop,
        "documents" or "doc" or "doc.fill" => Document,
        "doc.text" or "doc.text.fill" => TextDocument,
        "downloads" or "arrow.down.circle" or "arrow.down.circle.fill" => Download,
        "pictures" or "photo" or "photo.fill" => Pictures,
        "music" or "music.note" => Music,
        "videos" or "film" or "film.fill" => Video,
        "cloud" or "cloud.fill" => Cloud,
        "star" or "star.fill" => Star,
        "heart" or "heart.fill" => Heart,
        "flag" or "flag.fill" => Flag,
        "bookmark" or "bookmark.fill" => Bookmark,
        "tag" or "tag.fill" => Tag,
        "briefcase" or "briefcase.fill" => Work,
        "archivebox" or "archivebox.fill" => Archive,
        "tray" or "tray.fill" => Inbox,
        "paperplane" or "paperplane.fill" => Send,
        "person.crop.circle" or "person.crop.circle.fill" => Person,
        _ when icon.StartsWith(LogoPrefix, StringComparison.Ordinal) => Cloud,
        _ => Folder,
    };

    /// <summary>The logo asset a favourite icon names ("@logo:Dropbox" → "Dropbox"), or null for a glyph.</summary>
    public static string? LogoAsset(string icon)
        => icon.StartsWith(LogoPrefix, StringComparison.Ordinal) && icon.Length > LogoPrefix.Length ? icon[LogoPrefix.Length..] : null;
}
