// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.IO;

/// <summary>
/// Where FileFluss stages downloads and keeps its caches. Defaults to the system temporary
/// folder; Settings → Storage can point it anywhere (an external drive, so copying a large
/// cloud file to that drive doesn't fill the system disk first, and the final move is a
/// same-volume rename). Falls back to the temp folder whenever the chosen one is unusable
/// (drive unplugged), so transfers never hard-fail on a missing cache folder.
/// </summary>
public static class StagingLocation
{
    /// <summary>The user-chosen folder; set by the app from settings. Null or empty = system temp.</summary>
    public static string? CustomFolder { get; set; }

    public static string? UsableCustomFolder
    {
        get
        {
            var folder = CustomFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return null;
            }

            return folder;
        }
    }

    /// <summary>
    /// Makes a staging root private to the user; set by the app (Windows ACLs). Downloads wait
    /// here before they are opened or moved on, so on a shared drive another account must not
    /// be able to swap them.
    /// </summary>
    public static Action<string>? SecureFolder { get; set; }

    private static string? _securedRoot;

    /// <summary>The staging root (always exists), inside a "FileFluss-Cache" subfolder to keep a shared folder tidy.</summary>
    public static string Base()
    {
        var root = Path.Combine(UsableCustomFolder ?? Path.GetTempPath(), "FileFluss-Cache");
        Directory.CreateDirectory(root);
        if (SecureFolder is { } secure && !string.Equals(Volatile.Read(ref _securedRoot), root, StringComparison.OrdinalIgnoreCase))
        {
            Volatile.Write(ref _securedRoot, root);
            secure(root);
        }

        return root;
    }

    /// <summary>A unique path for one download's staging file.</summary>
    public static string DownloadStagingFile() => Path.Combine(Base(), "dl-" + Guid.NewGuid().ToString("N"));

    /// <summary>A unique staging subdirectory (cloud↔cloud transfers), created.</summary>
    public static string StagingDirectory(string prefix)
    {
        var directory = Path.Combine(Base(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>The per-account cache of files opened from a cloud panel ("open like local").</summary>
    public static string OpenCacheDirectory(Guid accountId)
    {
        var directory = Path.Combine(Base(), "open", accountId.ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Roots to scan when measuring or clearing the cache: the current base plus the default temp root.</summary>
    public static IReadOnlyList<string> CacheScanRoots()
    {
        var roots = new List<string> { Base() };
        var temp = Path.Combine(Path.GetTempPath(), "FileFluss-Cache");
        if (!roots.Contains(temp, StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(temp);
        }

        return roots;
    }
}
