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
    public const string Compare = "";
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

    /// <summary>The glyph for a favourite's stored icon name (macOS stored SF Symbol names).</summary>
    public static string ForFavorite(string icon) => icon switch
    {
        "home" or "house" => Home,
        "desktop" or "menubar.dock.rectangle" => Desktop,
        "documents" or "doc" => Document,
        "downloads" or "arrow.down.circle" => Download,
        "pictures" or "photo" => Pictures,
        "music" or "music.note" => Music,
        "videos" or "film" => Video,
        "star" => Star,
        _ when icon.StartsWith("@logo:", StringComparison.Ordinal) => Cloud,
        _ => Folder,
    };
}
