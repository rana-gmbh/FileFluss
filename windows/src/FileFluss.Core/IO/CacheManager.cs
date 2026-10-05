// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.IO;

/// <summary>
/// The cloud file cache under <see cref="StagingLocation"/>: its size for Settings → Storage,
/// Clear, and the launch-time auto-maintenance (prune entries older than N days, then
/// enforce a size cap oldest-first) — macOS <c>CacheManager</c>.
///
/// <para>Age is when a file was cached (its creation time), not its modification time:
/// cached copies carry the cloud file's own date, which may be years old.</para>
/// </summary>
public static class CacheManager
{
    public const int MinSizeMB = 100;
    public const int MaxSizeMB = 10_000;

    private static IEnumerable<string> CacheFiles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in StagingLocation.CacheScanRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (seen.Add(file))
                {
                    yield return file;
                }
            }
        }
    }

    public static Task<long> SizeAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            long total = 0;
            foreach (var file in CacheFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                }
            }

            return total;
        }, cancellationToken);

    /// <summary>Deletes everything cached. Files in use (an open document) are left alone.</summary>
    public static Task ClearAsync()
        => Task.Run(() =>
        {
            foreach (var file in CacheFiles())
            {
                TryDelete(file);
            }

            foreach (var root in StagingLocation.CacheScanRoots())
            {
                RemoveEmptyDirectories(root);
            }
        });

    public static Task RunAutoMaintenanceAsync(int maxAgeDays, int maxSizeMB)
        => Task.Run(() =>
        {
            var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
            var entries = new List<(string Path, long Size, DateTime Cached)>();
            foreach (var file in CacheFiles())
            {
                try
                {
                    var info = new FileInfo(file);
                    var cached = info.CreationTimeUtc;
                    if (cached < cutoff)
                    {
                        TryDelete(file);
                        continue;
                    }

                    entries.Add((file, info.Length, cached));
                }
                catch (IOException)
                {
                }
            }

            var limit = (long)maxSizeMB * 1024 * 1024;
            var total = entries.Sum(e => e.Size);
            foreach (var entry in entries.OrderBy(e => e.Cached))
            {
                if (total <= limit)
                {
                    break;
                }

                if (TryDelete(entry.Path))
                {
                    total -= entry.Size;
                }
            }

            foreach (var root in StagingLocation.CacheScanRoots())
            {
                RemoveEmptyDirectories(root);
            }
        });

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
