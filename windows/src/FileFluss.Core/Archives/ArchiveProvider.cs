// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Archives;

/// <summary>
/// The inside of an archive as a read-only <see cref="FileProvider"/>, so the ordinary panel
/// shows it unchanged — folder by folder, with breadcrumb, history, sorting, filtering and
/// search — while every change is refused (macOS <c>ArchiveBrowseSession</c>, which had to
/// teach two panels about archives; here the panel only sees another provider).
///
/// <para>The archive is listed once, lazily — opening a 2 GB zip reads its central directory,
/// not its contents — and everything after that is served from the <see cref="ArchiveTree"/>.
/// Nothing is unpacked until an entry is actually asked for: <see cref="DownloadFileAsync"/>
/// (a copy to the other panel, a drag to Explorer) writes just that entry, and
/// <see cref="MaterializeAsync"/> (Quick Look, prefetching for a transfer) unpacks a selection
/// into a staging folder that goes away with <see cref="Release"/>.</para>
///
/// <para>Paths are POSIX under the archive root: "/docs/readme.txt".</para>
/// </summary>
public sealed class ArchiveProvider : FileProvider
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _staging = new(1, 1);
    private readonly Dictionary<string, string> _materialized = new(StringComparer.Ordinal);
    private Task<ArchiveTree>? _tree;
    private string? _stagingRoot;
    private bool? _randomAccess;
    private int _leases;
    private bool _releasePending;

    /// <param name="archiveFile">The archive on disk (for a cloud archive: its downloaded copy).</param>
    /// <param name="format">What the archive's name says it is.</param>
    /// <param name="displayName">The archive's own name, for titles and for naming a single compressed stream.</param>
    public ArchiveProvider(string archiveFile, ArchiveFormat format, string displayName)
    {
        ArchiveFile = archiveFile;
        Format = format;
        DisplayName = displayName;
    }

    public string ArchiveFile { get; }

    public ArchiveFormat Format { get; }

    public string DisplayName { get; }

    /// <summary>Always true: FileFluss reads archives but never rewrites them.</summary>
    public bool IsReadOnly => true;

    // MARK: - The entry list

    /// <summary>The entry list, read once; a failed read is retried on the next call.</summary>
    public Task<ArchiveTree> TreeAsync(CancellationToken cancellationToken = default)
    {
        Task<ArchiveTree> task;
        lock (_gate)
        {
            if (_tree is null || _tree.IsFaulted || _tree.IsCanceled)
            {
                // Not tied to the caller's token: one panel giving up must not fail the listing
                // another is waiting for.
                _tree = Task.Run(() => ArchiveReader.List(ArchiveFile, Format, DisplayName, CancellationToken.None));
            }

            task = _tree;
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>The entry list if it has been read, without waiting.</summary>
    public ArchiveTree? LoadedTree
    {
        get
        {
            lock (_gate)
            {
                return _tree is { IsCompletedSuccessfully: true } done ? done.Result : null;
            }
        }
    }

    internal static string InnerPath(string path) => CloudPath.Normalize(path).Trim('/');

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => ListCoreAsync(path, cancellationToken);

    private async Task<IReadOnlyList<CloudFileItem>> ListCoreAsync(string path, CancellationToken cancellationToken)
    {
        var tree = await TreeAsync(cancellationToken).ConfigureAwait(false);
        var inner = InnerPath(path);
        if (inner.Length > 0 && tree.Entry(inner) is not { IsDirectory: true })
        {
            throw CloudProviderException.NotFound(path);
        }

        return tree.Children(inner).Select(tree.ItemFor).ToList();
    }

    public override async Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var tree = await TreeAsync(cancellationToken).ConfigureAwait(false);
        var inner = InnerPath(path);
        if (inner.Length == 0)
        {
            return new CloudFileItem("/", DisplayName, "/", true, tree.TotalSize, CloudFileItem.UnknownDate);
        }

        return tree.Entry(inner) is { } entry ? tree.ItemFor(entry) : throw CloudProviderException.NotFound(path);
    }

    /// <summary>What unpacking a folder would cost, already summed by the listing.</summary>
    public override async Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        var tree = await TreeAsync(cancellationToken).ConfigureAwait(false);
        var inner = InnerPath(path);
        return inner.Length == 0 ? tree.TotalSize : tree.RecursiveSize(inner);
    }

    /// <summary>Name search over the entry list, limited to <paramref name="path"/> when given.</summary>
    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
    {
        var tree = await TreeAsync(cancellationToken).ConfigureAwait(false);
        var within = path is null ? string.Empty : InnerPath(path);
        return tree.Entries
            .Where(e => e.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Where(e => within.Length == 0 || e.Path.StartsWith(within + "/", StringComparison.Ordinal))
            .Select(tree.ItemFor)
            .ToList();
    }

    // MARK: - Unpacking

    /// <summary>
    /// Writes one file entry to <paramref name="localPath"/>. A copy already unpacked into the
    /// staging folder is used when there is one; otherwise just that entry is read out of the
    /// archive. Never leaves a partial file behind.
    /// </summary>
    public override async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var tree = await TreeAsync(cancellationToken).ConfigureAwait(false);
        var inner = InnerPath(remotePath);
        if (tree.Entry(inner) is not { IsDirectory: false } entry)
        {
            throw CloudProviderException.NotFound(remotePath);
        }

        if (entry.IsEncrypted)
        {
            throw ArchiveException.Encrypted();
        }

        string? staged;
        lock (_gate)
        {
            staged = _materialized.GetValueOrDefault(inner);
        }

        if (staged is not null && File.Exists(staged))
        {
            await Task.Run(() => CopyStaged(staged, localPath, onBytes, cancellationToken), cancellationToken).ConfigureAwait(false);
            return;
        }

        await Task.Run(() => ArchiveExtractor.ExtractFile(ArchiveFile, Format, inner, localPath, onBytes, cancellationToken, DisplayName), cancellationToken).ConfigureAwait(false);
    }

    private static void CopyStaged(string staged, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var partial = localPath + ".ffpart";
        try
        {
            using (var source = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, ArchiveReader.BlockSize, FileOptions.SequentialScan))
            using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, ArchiveReader.BlockSize))
            {
                var buffer = new byte[ArchiveReader.BlockSize];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, read);
                    onBytes?.Invoke(read);
                }
            }

            File.SetLastWriteTimeUtc(partial, File.GetLastWriteTimeUtc(staged));
            File.Move(partial, localPath, overwrite: true);
            if (MarkOfTheWeb.Zone(staged) is >= 3)
            {
                MarkOfTheWeb.Apply(localPath);
            }
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    /// <summary>
    /// Whether one entry can be read without reading the ones stored before it: a zip (central
    /// directory) or a RAR / 7z that isn't solid. A tarball or a solid archive is a stream —
    /// its hundredth file is reached only through the first ninety-nine.
    /// </summary>
    public bool IsRandomAccess
    {
        get
        {
            if (_randomAccess is { } known)
            {
                return known;
            }

            var result = Format == ArchiveFormat.Zip || (Format is ArchiveFormat.Rar or ArchiveFormat.SevenZip && !ArchiveReader.IsSolid(ArchiveFile, Format));
            _randomAccess = result;
            return result;
        }
    }

    /// <summary>
    /// Before a transfer copies <paramref name="items"/> one file at a time: for an archive
    /// that can only be read as a stream, unpacks them all in one pass first, so a thousand
    /// files cost one read of the archive rather than a thousand. Each file's
    /// <see cref="DownloadFileAsync"/> then copies the staged one. Nothing to do for a zip.
    /// </summary>
    public async Task PrepareForTransferAsync(IReadOnlyList<CloudFileItem> items, CancellationToken cancellationToken = default)
    {
        var tree = await TreeAsync(cancellationToken).ConfigureAwait(false);
        if (await Task.Run(() => IsRandomAccess, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (tree.ContainsEncryptedEntries)
        {
            throw ArchiveException.Encrypted();
        }

        await MaterializeAsync(items.Select(i => i.Path).ToList(), null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Unpacks the entries at <paramref name="paths"/> — folders with everything inside them —
    /// into the staging folder, in one pass, and returns their files there in the order asked.
    /// Entries already unpacked aren't unpacked again.
    /// </summary>
    public async Task<IReadOnlyList<string>> MaterializeAsync(IReadOnlyList<string> paths, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var tree = await TreeAsync(cancellationToken).ConfigureAwait(false);
        var inner = paths.Select(InnerPath).Where(p => p.Length > 0).ToList();
        if (inner.Count == 0)
        {
            return [];
        }

        await _staging.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = StagingRoot();
            var wanted = new List<string>();
            var missingFiles = 0;
            lock (_gate)
            {
                foreach (var path in inner)
                {
                    foreach (var entry in tree.Subtree(path))
                    {
                        if (entry.IsDirectory)
                        {
                            wanted.Add(entry.Path);
                        }
                        else if (!entry.IsSymlink && (!_materialized.TryGetValue(entry.Path, out var done) || !File.Exists(done)))
                        {
                            wanted.Add(entry.Path);
                            missingFiles++;
                        }
                    }
                }
            }

            if (wanted.Any(p => tree.Entry(p) is { IsEncrypted: true }))
            {
                throw ArchiveException.Encrypted();
            }

            if (missingFiles == 0)
            {
                // Everything is unpacked already; folders are cheap to stand up again.
                foreach (var folder in wanted)
                {
                    Directory.CreateDirectory(ArchiveExtractor.TargetPath(folder, string.Empty, root));
                }
            }
            else
            {
                await Task.Run(() => ArchiveExtractor.Extract(ArchiveFile, Format, wanted, string.Empty, root, onBytes, cancellationToken, DisplayName, (entryPath, target) =>
                {
                    lock (_gate)
                    {
                        _materialized[entryPath] = target;
                    }
                }, tree), cancellationToken).ConfigureAwait(false);
            }

            return inner
                .Select(p => ArchiveExtractor.TargetPath(p, string.Empty, root))
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .ToList();
        }
        finally
        {
            _staging.Release();
        }
    }

    /// <summary>
    /// Unpacks the entries at <paramref name="paths"/> into a folder of their own, for handing
    /// to another application (Open, Open With). That folder isn't removed with the staging
    /// folder — the file may still be open in the other app — and is left for the cache's
    /// age-based clean-up, as on macOS.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExtractForOpeningAsync(IReadOnlyList<string> paths, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var tree = await TreeAsync(cancellationToken).ConfigureAwait(false);
        var inner = paths.Select(InnerPath).Where(p => p.Length > 0).ToList();
        if (inner.Count == 0)
        {
            return [];
        }

        var wanted = inner.SelectMany(p => tree.Subtree(p)).ToList();
        if (wanted.Any(e => e.IsEncrypted))
        {
            throw ArchiveException.Encrypted();
        }

        var prefix = ArchiveEntry.ParentOf(inner[0]);
        var folder = StagingLocation.StagingDirectory("archive-open");
        await Task.Run(() => ArchiveExtractor.Extract(ArchiveFile, Format, wanted.Select(e => e.Path).ToList(), prefix, folder, onBytes, cancellationToken, DisplayName, null, tree), cancellationToken).ConfigureAwait(false);
        return inner
            .Select(p => ArchiveExtractor.TargetPath(p, prefix, folder))
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToList();
    }

    private string StagingRoot()
    {
        lock (_gate)
        {
            if (_stagingRoot is null || !Directory.Exists(_stagingRoot))
            {
                _stagingRoot = StagingLocation.StagingDirectory("archive");
                _materialized.Clear();
            }

            return _stagingRoot;
        }
    }

    /// <summary>
    /// Keeps the unpacked files while a transfer out of the archive runs: a panel moving on
    /// mid-copy must not pull the staged files from under it. <see cref="Release"/> waits for
    /// the last lease.
    /// </summary>
    public IDisposable Lease()
    {
        lock (_gate)
        {
            _leases++;
        }

        return new LeaseHandle(this);
    }

    private sealed class LeaseHandle(ArchiveProvider owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.EndLease();
            }
        }
    }

    private void EndLease()
    {
        bool release;
        lock (_gate)
        {
            _leases--;
            release = _leases == 0 && _releasePending;
        }

        if (release)
        {
            Release();
        }
    }

    /// <summary>
    /// Drops the unpacked files and the entry list (both come back on demand). Called when no
    /// panel shows the archive any more; deferred while a transfer holds a <see cref="Lease"/>.
    /// </summary>
    public void Release()
    {
        string? root;
        lock (_gate)
        {
            if (_leases > 0)
            {
                _releasePending = true;
                return;
            }

            _releasePending = false;
            root = _stagingRoot;
            _stagingRoot = null;
            _materialized.Clear();
            if (_tree is { IsCompleted: true })
            {
                _tree = null;
            }
        }

        if (root is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    SupportLogger.Shared.Log($"Couldn't remove an archive's staging folder: {e.Message}", "archive", LogLevel.Notice);
                }
            });
        }
    }

    // MARK: - Refused: archives are read-only

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public override Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public static CloudProviderException ReadOnly() => CloudProviderException.CommandFailed(Localization.L("This location is read-only."));
}
