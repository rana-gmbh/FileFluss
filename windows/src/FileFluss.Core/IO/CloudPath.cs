// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.IO;

/// <summary>POSIX path helpers for cloud providers (root "/", no trailing slash except the root).</summary>
public static class CloudPath
{
    /// <summary>"/a/b/" → "/a/b"; "" → "/"; "a/b" → "/a/b"; collapses duplicate slashes.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', parts);
    }

    public static string Combine(string directory, string name)
    {
        var trimmed = Normalize(directory);
        return trimmed == "/" ? "/" + name.Trim('/') : trimmed + "/" + name.Trim('/');
    }

    public static string Parent(string path)
    {
        var normalized = Normalize(path);
        var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "/" : normalized[..slash];
    }

    public static string Name(string path)
    {
        var normalized = Normalize(path);
        return normalized == "/" ? string.Empty : normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    public static bool IsRoot(string path) => Normalize(path) == "/";

    /// <summary>
    /// True for a name that would climb out of its folder once used as a path component:
    /// "." or "..", or one with such a part between slashes or backslashes
    /// (<see cref="Normalize"/> treats a backslash as a separator). No real file is called
    /// that; a hostile server can still list one.
    /// </summary>
    public static bool IsTraversalName(string name)
    {
        foreach (var part in name.Split('/', '\\'))
        {
            if (part is "." or "..")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Path components without the root: "/a/b" → ["a", "b"].</summary>
    public static string[] Components(string path)
        => Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True when <paramref name="path"/> is <paramref name="ancestor"/> or inside it (component boundaries).</summary>
    public static bool IsWithin(string path, string ancestor)
    {
        var p = Normalize(path);
        var a = Normalize(ancestor);
        return a == "/" || p == a || p.StartsWith(a + "/", StringComparison.Ordinal);
    }
}

/// <summary>
/// Maintenance for the path → id caches that providers addressing items by server-assigned
/// ids keep, so navigating doesn't re-walk the tree on every step (port of the macOS
/// <c>PathCacheEviction</c>).
/// </summary>
public static class PathCacheExtensions
{
    /// <summary>
    /// Removes <paramref name="path"/> and everything cached beneath it. Deleting a folder takes
    /// its subtree with it, and a rename/move takes every descendant's path — evicting only the
    /// exact key leaves children mapped to ids that name nothing, and an idempotent
    /// create-folder then "finds" a folder that no longer exists. The root key is kept.
    /// </summary>
    public static void RemovePathSubtree<T>(this IDictionary<string, T> cache, string path)
    {
        var trimmed = Trimmed(path);
        if (trimmed != "/")
        {
            cache.Remove(trimmed);
        }

        var prefix = trimmed == "/" ? "/" : trimmed + "/";
        foreach (var key in cache.Keys.Where(k => k != "/" && k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            cache.Remove(key);
        }
    }

    /// <summary>
    /// Makes a completed listing authoritative for its own level: anything cached directly
    /// under <paramref name="path"/> that the listing didn't contain is dropped with its
    /// subtree. Only call after a listing completed — a listing that failed part-way is not
    /// evidence that anything is gone.
    /// </summary>
    public static void RetainPathChildren<T>(this IDictionary<string, T> cache, string path, ISet<string> names)
    {
        var parent = Trimmed(path);
        var prefix = parent == "/" ? "/" : parent + "/";
        var stale = cache.Keys.Where(key =>
        {
            if (key == "/" || !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            var remainder = key[prefix.Length..];
            return remainder.Length > 0 && !remainder.Contains('/') && !names.Contains(remainder);
        }).ToList();

        foreach (var key in stale)
        {
            cache.RemovePathSubtree(key);
        }
    }

    private static string Trimmed(string path) => path.Length > 1 && path.EndsWith('/') ? path[..^1] : path;
}
