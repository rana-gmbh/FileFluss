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
        => Task.Run(() => Maintain(DateTime.UtcNow.AddDays(-maxAgeDays), maxSizeMB));

    /// <summary>
    /// Trims the cache to <paramref name="maxSizeMB"/>, oldest first — run when the limit is
    /// changed in Settings, so a lower limit takes effect at once rather than at the next launch
    /// (macOS <c>enforceMaxSize</c>).
    /// </summary>
    public static Task EnforceMaxSizeAsync(int maxSizeMB)
        => Task.Run(() => Maintain(null, maxSizeMB));

    /// <summary>The slider's value as stored: whole 50 MB steps inside the allowed range (macOS <c>commitSliderValue</c>).</summary>
    public static int SnapSliderMB(double value)
        => Math.Clamp((int)Math.Round(value / 50, MidpointRounding.AwayFromZero) * 50, MinSizeMB, MaxSizeMB);

    /// <summary>
    /// The MB field's text as stored: a number clamped to the allowed range, or
    /// <paramref name="current"/> when it isn't one (macOS <c>commitFieldValue</c>).
    /// </summary>
    public static int ParseFieldMB(string text, int current)
    {
        var parsed = int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer | System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.CurrentCulture, out var value)
            ? value
            : current;
        return Math.Clamp(parsed, MinSizeMB, MaxSizeMB);
    }

    /// <summary>
    /// Deletes cached files, oldest first, until <paramref name="entries"/> fit in
    /// <paramref name="limitBytes"/>. A file <paramref name="tryDelete"/> can't remove (an open
    /// document) still counts, so the next-oldest goes instead. Returns what was deleted.
    /// </summary>
    public static IReadOnlyList<string> TrimToLimit(IEnumerable<(string Path, long Size, DateTime Cached)> entries, long limitBytes, Func<string, bool> tryDelete)
    {
        var list = entries.ToList();
        var total = list.Sum(e => e.Size);
        var deleted = new List<string>();
        foreach (var entry in list.OrderBy(e => e.Cached))
        {
            if (total <= limitBytes)
            {
                break;
            }

            if (tryDelete(entry.Path))
            {
                deleted.Add(entry.Path);
                total -= entry.Size;
            }
        }

        return deleted;
    }

    private static void Maintain(DateTime? cutoff, int maxSizeMB)
    {
        var entries = new List<(string Path, long Size, DateTime Cached)>();
        foreach (var file in CacheFiles())
        {
            try
            {
                var info = new FileInfo(file);
                var cached = info.CreationTimeUtc;
                if (cutoff is { } oldest && cached < oldest)
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

        TrimToLimit(entries, (long)maxSizeMB * 1024 * 1024, TryDelete);
        foreach (var root in StagingLocation.CacheScanRoots())
        {
            RemoveEmptyDirectories(root);
        }
    }

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
