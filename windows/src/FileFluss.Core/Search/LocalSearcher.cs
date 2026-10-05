// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Search;

/// <summary>
/// Name search on disk (macOS <c>LocalSearcher</c>). macOS used Spotlight for the home tree
/// and a direct walk for mounted volumes, because Spotlight was unreliable there; Windows
/// Search is no more dependable on removable and network drives, and Core stays
/// platform-neutral, so every local search is a direct walk.
///
/// <para>Matches stream out as they are found — a big drive can take minutes to walk, and
/// the user reads the list while it fills. Only matches are turned into items; the walk's
/// hot path tests the name and nothing else.</para>
/// </summary>
public sealed class LocalSearcher
{
    /// <summary>A batch is handed on once it has this many matches…</summary>
    private const int BatchSize = 64;

    /// <summary>…or once it has waited this long, so a slow walk still shows its first hits at once.</summary>
    private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(100);

    private static readonly EnumerationOptions RecursiveOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,

        // macOS skips hidden files in a recursive walk.
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private static readonly EnumerationOptions FolderOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,

        // The current folder's own listing shows hidden items too (macOS: no skipsHiddenFiles there).
        AttributesToSkip = 0,
    };

    /// <summary>
    /// Items under <paramref name="rootPath"/> whose name contains <paramref name="query"/>
    /// (case-insensitive), in batches. Throws <see cref="CloudProviderException"/> NotFound
    /// when the folder isn't there.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<CloudFileItem>> SearchAsync(
        string query,
        string rootPath,
        bool recursive,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<IReadOnlyList<CloudFileItem>>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _ = Task.Factory.StartNew(
            () => Walk(query, rootPath, recursive, channel.Writer, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static void Walk(string query, string rootPath, bool recursive, ChannelWriter<IReadOnlyList<CloudFileItem>> writer, CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(rootPath))
            {
                writer.TryComplete(CloudProviderException.NotFound(rootPath));
                return;
            }

            // Every entry comes through (null for a non-match) so the loop can watch the clock
            // and the cancellation token between matches, not only at them.
            var walk = new FileSystemEnumerable<CloudFileItem?>(
                rootPath,
                (ref FileSystemEntry entry) => entry.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    ? LocalFileProvider.ToItem(entry.ToFileSystemInfo())
                    : null,
                recursive ? RecursiveOptions : FolderOptions)
            {
                // Don't follow a junction or symlink: one pointing at a parent never ends.
                ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
            };

            var batch = new List<CloudFileItem>();
            var clock = Stopwatch.StartNew();
            foreach (var item in walk)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (item is not null)
                {
                    batch.Add(item);
                }

                if (batch.Count >= BatchSize || (batch.Count > 0 && clock.Elapsed >= BatchDelay))
                {
                    writer.TryWrite(batch);
                    batch = [];
                    clock.Restart();
                }
            }

            if (batch.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                writer.TryWrite(batch);
            }

            writer.TryComplete();
        }
        catch (Exception e)
        {
            // Whatever ended the walk (a drive pulled mid-search, a path Windows rejects) ends
            // this source with that message, never with a reader waiting for ever.
            writer.TryComplete(e);
        }
    }
}
