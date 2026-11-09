// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Transfers;

namespace FileFluss.Core.Archives;

/// <summary>
/// "Compress" and "Extract Here / Extract to … Panel" as jobs in the transfer list, for local
/// folders and cloud accounts alike (macOS <c>FileManagerViewModel.compress / extractArchive</c>
/// and <c>CloudFileManagerViewModel+Archive</c>). No provider can pack or unpack on its own
/// servers, so a cloud side takes the steps a person would: download to a staging folder, do
/// the work there, upload the result beside the original. One <see cref="TransferProgress"/>
/// carries all of it; every job records its outcome there and never throws.
/// </summary>
public static class ArchiveJobs
{
    /// <summary>A progress row for a job about <paramref name="name"/>.</summary>
    public static TransferProgress CreateProgress(TransferOperation operation, string name, PanelSide panel)
    {
        var progress = new TransferProgress(operation, 1, panel);
        progress.SetItems([new CloudFileItem(name, name, name, false, 0, CloudFileItem.UnknownDate)]);
        return progress;
    }

    // MARK: - Compress

    /// <summary>
    /// Packs <paramref name="items"/> from <paramref name="folder"/> on <paramref name="provider"/>
    /// into <paramref name="archiveName"/> beside them. Returns the archive's path on the
    /// provider, or null when it failed or was cancelled (the progress says which).
    /// </summary>
    public static async Task<string?> CompressAsync(
        FileProvider provider,
        string folder,
        IReadOnlyList<CloudFileItem> items,
        string archiveName,
        ArchiveCreationFormat format,
        TransferProgress progress)
    {
        var ct = progress.CancellationToken;
        string? staging = null;
        try
        {
            progress.SetPhase(TransferPhase.Copying);
            progress.BeginFile(archiveName, 0);
            if (provider.IsLocal)
            {
                // The count only drives the bar; walking a 50,000-file folder before packing
                // starts would make the wait longer than the job, so it arrives when it arrives.
                _ = Task.Run(() =>
                {
                    var (entries, bytes) = CountLocal(folder, items.Select(i => i.Name).ToList(), ct);
                    progress.SetPlan(entries, bytes);
                }, ct);
                var created = await ArchiveWriter.CreateAsync(folder, items.Select(i => i.Name).ToList(), archiveName, format,
                    _ => progress.FileFinished(), progress.AddDownloadBytes, ct).ConfigureAwait(false);
                progress.RecordSuccess(archiveName);
                return created;
            }

            // Phase 1: the selection, downloaded. What arrived is read back rather than
            // predicted: a provider may rename on the way down, or export a document.
            staging = StagingLocation.StagingDirectory("compress");
            var source = Path.Combine(staging, "source");
            Directory.CreateDirectory(source);
            progress.SetPhase(TransferPhase.Downloading);
            long selectionBytes = 0;
            foreach (var item in items)
            {
                selectionBytes += item.IsDirectory ? await SafeFolderSizeAsync(provider, item.Path, ct).ConfigureAwait(false) : item.Size;
            }

            progress.SetPlan(0, selectionBytes * 2);
            await DownloadTreeAsync(provider, items, source, progress, ct).ConfigureAwait(false);
            var downloaded = Directory.GetFileSystemEntries(source).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (downloaded.Count != items.Count)
            {
                throw ArchiveException.Failed(Localization.L("Not every selected item could be downloaded, so no archive was made."));
            }

            // Phase 2: pack.
            progress.SetPhase(TransferPhase.Copying);
            progress.BeginFile(archiveName, 0);
            var archive = await ArchiveWriter.CreateAsync(source, downloaded, archiveName, format, null, progress.AddDownloadBytes, ct).ConfigureAwait(false);

            // Phase 3: the archive alone, uploaded next to the originals.
            var size = new FileInfo(archive).Length;
            progress.SetPlan(0, selectionBytes * 2 + size);
            progress.SetPhase(TransferPhase.Uploading);
            progress.BeginFile(archiveName, size);
            var remote = provider.CombinePath(folder, archiveName);
            if (provider is CloudProvider cloud)
            {
                CloudProviderException.EnforceUploadSizeLimit(archive, cloud.MaxUploadFileSize);
            }

            long uploaded = 0;
            await provider.UploadFileAsync(archive, remote, d => { Interlocked.Add(ref uploaded, d); progress.AddUploadBytes(d); }, ct).ConfigureAwait(false);
            progress.TopUp(true, uploaded, size);
            progress.RecordSuccess(archiveName);
            return remote;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e)
        {
            progress.RecordFailure(archiveName, e.Message);
            SupportLogger.Shared.Log($"compress failed: {e.Message}", "archive", LogLevel.Error);
            return null;
        }
        finally
        {
            TryDeleteDirectory(staging);
            progress.Complete();
        }
    }

    /// <summary>Files and folders at or below the selection, and their bytes: the progress bar's denominator. Metadata only.</summary>
    internal static (int Entries, long Bytes) CountLocal(string folder, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        int entries = 0;
        long bytes = 0;
        try
        {
            foreach (var item in ArchiveWriter.Expand(folder, names, cancellationToken))
            {
                entries++;
                if (!item.IsDirectory)
                {
                    bytes += new FileInfo(item.SourcePath).Length;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CloudProviderException or OperationCanceledException)
        {
        }

        return (entries, bytes);
    }

    // MARK: - Extract

    /// <summary>
    /// Unpacks the whole archive <paramref name="archive"/> (on <paramref name="source"/>) into
    /// <paramref name="destinationFolder"/> on <paramref name="destination"/>, by Finder's rule:
    /// one top-level item lands as itself, several go into a folder named after the archive,
    /// either way under a name that is free there. Returns where it landed, or null.
    /// </summary>
    public static async Task<string?> ExtractAsync(
        FileProvider source,
        CloudFileItem archive,
        FileProvider destination,
        string destinationFolder,
        TransferProgress progress)
    {
        var ct = progress.CancellationToken;
        string? staging = null;
        try
        {
            var format = ArchiveFormats.Detect(archive.Name) ?? throw ArchiveException.Unreadable(archive.Name);
            string localArchive;
            if (source.IsLocal)
            {
                localArchive = archive.Path;
            }
            else
            {
                // Phase 1: the archive itself.
                staging = StagingLocation.StagingDirectory("extract");
                progress.SetPhase(TransferPhase.Downloading);
                progress.BeginFile(archive.Name, archive.Size);
                progress.SetPlan(0, archive.Size);
                var requested = SafeLocalPath.Destination(archive.Name, staging);
                long reported = 0;
                await source.DownloadFileAsync(archive.Path, requested, d => { Interlocked.Add(ref reported, d); progress.AddDownloadBytes(d); }, ct).ConfigureAwait(false);
                progress.TopUp(false, reported, archive.Size);
                localArchive = DownloadedFile.Locate(requested);
            }

            // Phase 2: unpack — straight into a local destination, into staging for a cloud one.
            var tree = await Task.Run(() => ArchiveReader.List(localArchive, format, archive.Name, ct), ct).ConfigureAwait(false);
            if (tree.ContainsEncryptedEntries)
            {
                throw ArchiveException.Encrypted();
            }

            var unpackBytes = tree.TotalSize;
            var downloaded = progress.MovedBytes;
            progress.SetPlan(tree.FileCount, downloaded + unpackBytes * (destination.IsLocal ? 1 : 2));
            progress.SetPhase(TransferPhase.Copying);
            progress.BeginFile(archive.Name, unpackBytes);

            Func<string, bool> isTaken;
            string unpackInto;
            if (destination.IsLocal)
            {
                unpackInto = destinationFolder;
                isTaken = name => File.Exists(Path.Combine(destinationFolder, name)) || Directory.Exists(Path.Combine(destinationFolder, name));
            }
            else
            {
                staging ??= StagingLocation.StagingDirectory("extract");
                unpackInto = Path.Combine(staging, "out");
                Directory.CreateDirectory(unpackInto);
                var taken = new HashSet<string>(destination.NameComparer);
                foreach (var existing in await destination.ListDirectoryAsync(destinationFolder, ct).ConfigureAwait(false))
                {
                    taken.Add(existing.Name);
                }

                isTaken = taken.Contains;
            }

            var result = await ArchiveExtractor.UnpackAsync(localArchive, archive.Name, format, unpackInto, isTaken,
                progress.AddDownloadBytes, ct, (_, _) => progress.FileFinished()).ConfigureAwait(false);
            var landedName = Path.GetFileName(result.Destination);

            // Phase 3: upload, unless it already landed where it belongs.
            string landed;
            if (destination.IsLocal)
            {
                landed = result.Destination;
            }
            else
            {
                progress.SetPhase(TransferPhase.Uploading);
                landed = await UploadTreeAsync(result.Destination, destination, destinationFolder, progress, ct).ConfigureAwait(false);
            }

            progress.RecordSuccess(landedName);
            if (result.FirstUnsafeEntry is { } refused)
            {
                // The rest was unpacked; the entry that would have escaped is named, not hidden.
                progress.RecordFailure(refused, ArchiveException.UnsafeEntry(refused).Message);
            }

            SupportLogger.Shared.Log($"extracted {archive.Name} to {landedName}", "archive");
            return landed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e)
        {
            progress.RecordFailure(archive.Name, e.Message);
            SupportLogger.Shared.Log($"extract failed: {e.Message}", "archive", LogLevel.Error);
            return null;
        }
        finally
        {
            TryDeleteDirectory(staging);
            progress.Complete();
        }
    }

    // MARK: - Moving trees

    private static async Task<long> SafeFolderSizeAsync(FileProvider provider, string path, CancellationToken ct)
    {
        try
        {
            return await provider.FolderSizeAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A folder whose size can't be read contributes nothing; the bar falls back to counts.
            return 0;
        }
    }

    /// <summary>Downloads files and folders (recursively) into <paramref name="localFolder"/>, names made safe for Windows.</summary>
    internal static async Task DownloadTreeAsync(FileProvider provider, IReadOnlyList<CloudFileItem> items, string localFolder, TransferProgress progress, CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var target = SafeLocalPath.Destination(item.Name, localFolder);
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(target);
                var children = await provider.ListDirectoryAsync(item.Path, ct).ConfigureAwait(false);
                await DownloadTreeAsync(provider, children, target, progress, ct).ConfigureAwait(false);
                continue;
            }

            progress.BeginFile(item.Name, item.Size);
            long reported = 0;
            await provider.DownloadFileAsync(item.Path, target, d => { Interlocked.Add(ref reported, d); progress.AddDownloadBytes(d); }, ct).ConfigureAwait(false);
            progress.TopUp(false, reported, item.Size);
            progress.FileFinished();
        }
    }

    /// <summary>Uploads a local file or folder (recursively) into <paramref name="remoteFolder"/>. Returns its remote path.</summary>
    internal static async Task<string> UploadTreeAsync(string localPath, FileProvider destination, string remoteFolder, TransferProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var remote = destination.CombinePath(remoteFolder, Path.GetFileName(localPath));
        if (Directory.Exists(localPath))
        {
            await destination.CreateDirectoryAsync(remote, ct).ConfigureAwait(false);
            foreach (var child in Directory.EnumerateFileSystemEntries(localPath).Order(StringComparer.OrdinalIgnoreCase))
            {
                await UploadTreeAsync(child, destination, remote, progress, ct).ConfigureAwait(false);
            }

            return remote;
        }

        var size = new FileInfo(localPath).Length;
        if (destination is CloudProvider cloud)
        {
            CloudProviderException.EnforceUploadSizeLimit(localPath, cloud.MaxUploadFileSize);
        }

        progress.BeginFile(Path.GetFileName(localPath), size);
        long uploaded = 0;
        await destination.UploadFileAsync(localPath, remote, d => { Interlocked.Add(ref uploaded, d); progress.AddUploadBytes(d); }, ct).ConfigureAwait(false);
        progress.TopUp(true, uploaded, size);
        progress.FileFinished();
        return remote;
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SupportLogger.Shared.Log($"Couldn't remove a staging folder: {e.Message}", "archive", LogLevel.Notice);
        }
    }
}
