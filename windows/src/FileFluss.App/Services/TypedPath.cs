// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;

namespace FileFluss.App.Services;

/// <summary>What a typed path turned out to be on disk.</summary>
public enum TypedPathKind
{
    None,
    Folder,
    File,
}

/// <summary>
/// A path typed into the path bar or handed over on the command line: which side of the app
/// it means (this PC or the panel's account/archive), where it is, and whether it exists.
/// </summary>
public static class TypedPath
{
    /// <summary>Quotes trimmed, %VARIABLES% expanded and a leading "~" made the user's folder.</summary>
    public static string Expand(string text)
    {
        var expanded = Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
        if (expanded.StartsWith('~'))
        {
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + expanded[1..];
        }

        return expanded;
    }

    /// <summary>
    /// Whether the text names a place on this PC whatever panel it is typed into: a drive
    /// ("C:", "C:\…", "C:/…"), a network share ("\\server\share"), or what %VARIABLES% or "~"
    /// expand to. Anything else ("/Photos", "Photos/2024") is a path in a cloud account or
    /// archive — in a cloud panel "C:\Users\…" used to be looked up as "/C:/Users/…" there.
    /// </summary>
    public static bool NamesThisPc(string text)
    {
        var expanded = Expand(text);
        return IsDrivePath(expanded) || expanded.StartsWith(@"\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// The full local path for the text: relative paths against <paramref name="currentFolder"/>
    /// (when there is one), a bare "C:" as the drive's root (not "the current folder on C:").
    /// Null when nothing usable was typed, or no path can hold it.
    /// </summary>
    public static string? ResolveLocal(string text, string currentFolder)
    {
        var expanded = Expand(text);
        if (expanded.Length == 0)
        {
            return null;
        }

        try
        {
            if (expanded.Length == 2 && IsDrivePath(expanded))
            {
                return expanded + Path.DirectorySeparatorChar;
            }

            if (!Path.IsPathRooted(expanded) && currentFolder.Length > 0)
            {
                return Path.GetFullPath(Path.Combine(currentFolder, expanded));
            }

            // Characters no path can hold throw here rather than in the existence check.
            _ = Path.GetFullPath(expanded);
            return expanded;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Folder, file or nothing. Asks the disk — and an offline network share takes the SMB
    /// timeout (20–60 s) to answer — so call it off the UI thread.
    /// </summary>
    public static TypedPathKind Probe(string path)
        => Directory.Exists(path) ? TypedPathKind.Folder
        : File.Exists(path) ? TypedPathKind.File
        : TypedPathKind.None;

    private static bool IsDrivePath(string path)
        => path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path.Length == 2 || path[2] is '\\' or '/');
}
