// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Storage;

/// <summary>What a storage analysis runs against: a whole cloud account, a folder inside one, or a local folder.</summary>
/// <param name="DisplayName">The account's name (cloud); ignored for local folders.</param>
public sealed record StorageScanSource(FileProvider Provider, string RootPath, string DisplayName)
{
    public static StorageScanSource ForLocal(string path)
        => new(LocalFileProvider.Shared, path, LocalFileProvider.Shared.GetName(path));

    /// <summary>The whole account (its root), or <paramref name="path"/> inside it.</summary>
    public static StorageScanSource ForAccount(CloudProvider provider, string displayName, string? path = null)
        => new(provider, path ?? provider.RootPath, displayName);

    /// <summary>Distinguishes sources in a picker; the same folder from both panels is one entry.</summary>
    public string Id => Provider is CloudProvider cloud ? $"cloud:{cloud.AccountId}:{RootPath}" : $"local:{RootPath}";

    private bool IsAccountRoot => !Provider.IsLocal && Provider.IsRoot(RootPath);

    /// <summary>The picker title: "Dropbox (Robert)", "Dropbox (Robert) — /Photos", or a local path.</summary>
    public string Title => Provider.IsLocal ? RootPath : IsAccountRoot ? DisplayName : $"{DisplayName} — {RootPath}";

    /// <summary>The tree's root row.</summary>
    public string RootName => IsAccountRoot ? DisplayName : Provider.GetName(RootPath);
}

/// <summary>Live counters while a scan runs, plus a partial report from time to time.</summary>
/// <param name="Partial">A tree of everything seen so far; null on updates that only move the counters.</param>
public sealed record StorageScanUpdate(int FilesSeen, long BytesSeen, string CurrentPath, StorageReport? Partial);

/// <summary>
/// Walks a source folder by folder and reports which folders and files take up the space,
/// streaming partial results as it goes. One walk for local folders and cloud accounts
/// alike, through <see cref="FileProvider.ListDirectoryAsync"/>.
/// </summary>
public static class StorageScanner
{
    /// <summary>Rebuilding the tree is O(n log n) over everything seen; throttled so the whole scan doesn't turn quadratic.</summary>
    private static readonly TimeSpan PartialInterval = TimeSpan.FromSeconds(0.75);

    private const int PartialMinimumFiles = 100;

    /// <summary>How often the counters alone are reported.</summary>
    private static readonly TimeSpan CounterInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Scans <paramref name="source"/>. Cancelling returns what was found so far with
    /// <see cref="StorageReport.WasCancelled"/> set rather than throwing. A folder that can't
    /// be read is skipped — one unreadable folder must not abandon the analysis — except the
    /// root itself, whose failure is thrown (a wrong password is not an empty account).
    /// </summary>
    /// <param name="onUpdate">Called from the scanning thread.</param>
    public static Task<StorageReport> ScanAsync(
        StorageScanSource source,
        Action<StorageScanUpdate>? onUpdate = null,
        CancellationToken cancellationToken = default)
        => ScanAsync(source, onUpdate, PartialInterval, PartialMinimumFiles, cancellationToken);

    /// <summary>Test seam: the partial-report throttle.</summary>
    internal static async Task<StorageReport> ScanAsync(
        StorageScanSource source,
        Action<StorageScanUpdate>? onUpdate,
        TimeSpan partialInterval,
        int partialMinimumFiles,
        CancellationToken cancellationToken)
    {
        var provider = source.Provider;
        var local = provider.IsLocal;
        var entries = new List<StorageEntry>();

        // Index cursor rather than dequeuing from the front of a big list.
        var pending = new List<string> { source.RootPath };
        var next = 0;
        var files = 0;
        long bytes = 0;
        var filesAtLastPartial = 0;
        var clock = Stopwatch.StartNew();
        var lastPartial = -partialInterval;
        var lastCounters = TimeSpan.Zero;
        var cancelled = false;

        while (next < pending.Count)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var path = pending[next++];
            IReadOnlyList<Models.CloudFileItem> items;
            try
            {
                items = await provider.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            catch (Exception e) when (next > 1)
            {
                SupportLogger.Shared.Log($"Storage scan skipped {path}: {e.Message}", "Storage");
                continue;
            }

            foreach (var item in items)
            {
                if (local && item.IsHidden)
                {
                    // Hidden and system items are skipped, as macOS skipped hidden files.
                    continue;
                }

                entries.Add(new StorageEntry(
                    item.Path,
                    item.Name,
                    item.IsDirectory,
                    item.IsDirectory ? 0 : item.Size,
                    item.HasKnownModificationDate ? item.ModificationDate : null));

                if (item.IsDirectory)
                {
                    // A junction or symlinked folder would be counted twice, or forever.
                    if (!(local && item.IsSymlink))
                    {
                        pending.Add(item.Path);
                    }
                }
                else
                {
                    files++;
                    bytes += item.Size;
                }
            }

            if (onUpdate is null)
            {
                continue;
            }

            var now = clock.Elapsed;
            if (files - filesAtLastPartial >= partialMinimumFiles && now - lastPartial >= partialInterval)
            {
                filesAtLastPartial = files;
                lastPartial = now;
                lastCounters = now;
                var partial = StorageTreeBuilder.Build(entries, source.RootPath, source.RootName, provider);
                onUpdate(new StorageScanUpdate(files, bytes, path, partial));
            }
            else if (now - lastCounters >= CounterInterval)
            {
                lastCounters = now;
                onUpdate(new StorageScanUpdate(files, bytes, path, null));
            }
        }

        var report = StorageTreeBuilder.Build(entries, source.RootPath, source.RootName, provider, cancelled);
        onUpdate?.Invoke(new StorageScanUpdate(files, bytes, string.Empty, report));
        return report;
    }
}
