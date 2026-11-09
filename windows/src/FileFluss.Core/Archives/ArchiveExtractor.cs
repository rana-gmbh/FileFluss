// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Buffers;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Archives;

/// <summary>What <see cref="ArchiveExtractor.UnpackAsync"/> produced.</summary>
/// <param name="Destination">The item that landed: the archive's one top-level item, or the folder named after it.</param>
/// <param name="FirstUnsafeEntry">An entry left out for being unsafe, for the message saying so; null when there was none.</param>
public sealed record UnpackResult(string Destination, string? FirstUnsafeEntry);

/// <summary>
/// Writes archive entries to disk (the extracting half of macOS <c>ArchiveService</c>).
/// Every path passes two independent checks: <see cref="ArchiveEntry.Normalise"/> when the
/// archive is read, and <see cref="TargetPath"/> here, which refuses anything that would land
/// outside the destination.
/// </summary>
public static class ArchiveExtractor
{
    /// <summary>
    /// Writes the named entries into <paramref name="destination"/>, dropping
    /// <paramref name="prefix"/> from the front of each path — so a folder extracted out of
    /// "docs/reports/2026" arrives as "2026", not as three nested folders. Returns the
    /// top-level items written. One pass over the archive however many entries are asked for;
    /// with the archive's <paramref name="tree"/>, folders are made from it up front and the
    /// pass stops as soon as the last requested file is out — in a solid archive, everything
    /// stored after it is never decompressed.
    /// </summary>
    public static IReadOnlyList<string> Extract(
        string archivePath,
        ArchiveFormat format,
        IReadOnlyCollection<string> entryPaths,
        string prefix,
        string destination,
        ByteProgress? onBytes = null,
        CancellationToken cancellationToken = default,
        string? archiveName = null,
        Action<string, string>? onFileWritten = null,
        ArchiveTree? tree = null)
    {
        Directory.CreateDirectory(destination);
        var remaining = new HashSet<string>(entryPaths, StringComparer.Ordinal);
        var written = new List<string>();
        var filesWritten = new Dictionary<string, string>(StringComparer.Ordinal);

        void NoteTopLevel(string entryPath)
        {
            var relative = prefix.Length == 0 ? entryPath
                : entryPath.StartsWith(prefix + "/", StringComparison.Ordinal) ? entryPath[(prefix.Length + 1)..]
                : entryPath[(entryPath.LastIndexOf('/') + 1)..];
            var slash = relative.IndexOf('/');
            var top = TargetPath(slash < 0 ? relative : relative[..slash], string.Empty, destination);
            if (!written.Contains(top, StringComparer.OrdinalIgnoreCase))
            {
                written.Add(top);
            }
        }

        // Whatever came from the internet still did after unpacking: SmartScreen and Protected
        // View check the extracted files too (macOS carries the quarantine flag the same way).
        var marked = MarkOfTheWeb.Zone(archivePath) is >= 3;

        // Directory dates are applied at the end: writing into a directory bumps its date, so
        // one stamped at creation would be wrong again by the time its contents land.
        var directoryDates = new List<(string Path, DateTimeOffset Date)>();
        if (tree is not null)
        {
            foreach (var path in remaining.ToList())
            {
                if (tree.Entry(path) is { IsDirectory: true } folder)
                {
                    var target = TargetPath(path, prefix, destination);
                    Directory.CreateDirectory(target);
                    directoryDates.Add((target, folder.ModificationDate));
                    NoteTopLevel(path);
                    remaining.Remove(path);
                }
            }
        }

        if (remaining.Count == 0)
        {
            SetDirectoryDates(directoryDates);
            return written;
        }

        ArchiveReader.Walk(archivePath, format, archiveName ?? Path.GetFileName(archivePath), withData: true, record =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Entry is not { } entry || !remaining.Remove(entry.Path))
            {
                return true;
            }

            var target = TargetPath(entry.Path, prefix, destination);
            NoteTopLevel(entry.Path);

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                directoryDates.Add((target, entry.ModificationDate));
            }
            else if (record.IsHardLink)
            {
                // The same file as an entry already written in this pass: a copy is the
                // nearest thing Windows offers without asking for a privilege.
                if (record.LinkTarget is { } linked && filesWritten.TryGetValue(linked, out var original))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(original, target, overwrite: true);
                    filesWritten[entry.Path] = target;
                }
            }
            else if (entry.IsSymlink)
            {
                // Symbolic links need Developer Mode or an administrator on Windows, and one
                // that points out of the archive is an escape in two steps. Left out.
                SupportLogger.Shared.Log($"skipped symbolic link \"{entry.Path}\" in {Path.GetFileName(archivePath)}", "archive");
            }
            else if (entry.IsEncrypted)
            {
                throw ArchiveException.Encrypted();
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                WriteFile(record.OpenData!, target, entry, onBytes, cancellationToken);
                if (marked)
                {
                    MarkOfTheWeb.Apply(target);
                }

                filesWritten[entry.Path] = target;
                onFileWritten?.Invoke(entry.Path, target);
            }

            return remaining.Count > 0;
        }, cancellationToken);

        SetDirectoryDates(directoryDates);
        return written;
    }

    private static void SetDirectoryDates(List<(string Path, DateTimeOffset Date)> directoryDates)
    {
        foreach (var (path, date) in directoryDates)
        {
            if (date > Models.CloudFileItem.UnknownDate)
            {
                try
                {
                    Directory.SetLastWriteTimeUtc(path, date.UtcDateTime);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Writes one file entry to <paramref name="targetFile"/>, replacing what is there, through
    /// a temporary name so a failure or a cancel leaves nothing half-written behind.
    /// </summary>
    public static void ExtractFile(string archivePath, ArchiveFormat format, string entryPath, string targetFile, ByteProgress? onBytes = null, CancellationToken cancellationToken = default, string? archiveName = null)
    {
        var found = false;
        var partial = targetFile + ".ffpart";
        try
        {
            ArchiveReader.Walk(archivePath, format, archiveName ?? Path.GetFileName(archivePath), withData: true, record =>
            {
                if (record.Entry is not { } entry || entry.Path != entryPath)
                {
                    return true;
                }

                if (entry.IsDirectory || entry.IsSymlink)
                {
                    throw ArchiveException.NotInArchive(entry.Name);
                }

                if (entry.IsEncrypted)
                {
                    throw ArchiveException.Encrypted();
                }

                found = true;
                WriteFile(record.OpenData!, partial, entry, onBytes, cancellationToken);
                return false;
            }, cancellationToken);

            if (!found)
            {
                throw ArchiveException.NotInArchive(entryPath[(entryPath.LastIndexOf('/') + 1)..]);
            }

            File.Move(partial, targetFile, overwrite: true);
            if (MarkOfTheWeb.Zone(archivePath) is >= 3)
            {
                MarkOfTheWeb.Apply(targetFile);
            }
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>Unpacks the whole archive into <paramref name="destination"/>, which is created if needed.</summary>
    public static ArchiveTree ExtractAll(string archivePath, ArchiveFormat format, string destination, ByteProgress? onBytes = null, CancellationToken cancellationToken = default, string? archiveName = null, Action<string, string>? onFileWritten = null)
    {
        // Listed first: an encrypted archive has to be reported as one before anything is written.
        var tree = ArchiveReader.List(archivePath, format, archiveName, cancellationToken);
        if (tree.ContainsEncryptedEntries)
        {
            throw ArchiveException.Encrypted();
        }

        Extract(archivePath, format, tree.Entries.Select(e => e.Path).ToList(), string.Empty, destination, onBytes, cancellationToken, archiveName, onFileWritten, tree);
        return tree;
    }

    /// <summary>
    /// Unpacks an archive into <paramref name="directory"/> by Finder's rule — one top-level
    /// item lands as itself, several go into a folder named after the archive — and returns
    /// what landed. Everything is written to a working folder first and moved into place
    /// afterwards, so a failure or a cancel leaves nothing behind, and the move is a
    /// same-volume rename. <paramref name="isTaken"/> decides whether a name in the directory
    /// is in use, which lets a caller that will upload the result check a cloud folder.
    /// </summary>
    public static Task<UnpackResult> UnpackAsync(
        string archivePath,
        string archiveName,
        ArchiveFormat format,
        string directory,
        Func<string, bool> isTaken,
        ByteProgress? onBytes = null,
        CancellationToken cancellationToken = default,
        Action<string, string>? onFileWritten = null)
        => Task.Run(() =>
        {
            var work = Path.Combine(directory, $".FileFluss-extract-{Guid.NewGuid():N}");
            try
            {
                var info = Directory.CreateDirectory(work);

                // Hidden while it fills, so the panel showing this folder doesn't flash it.
                info.Attributes |= FileAttributes.Hidden;
                var tree = ExtractAll(archivePath, format, work, onBytes, cancellationToken, archiveName, onFileWritten);
                var roots = Directory.GetFileSystemEntries(work);
                var preferred = roots.Length == 1 ? Path.GetFileName(roots[0]) : ArchiveFormats.BaseName(archiveName);
                var destination = Path.Combine(directory, AvailableName(preferred, isTaken));
                if (roots.Length == 1)
                {
                    if (Directory.Exists(roots[0]))
                    {
                        Directory.Move(roots[0], destination);
                    }
                    else
                    {
                        File.Move(roots[0], destination);
                    }

                    TryDeleteDirectory(work);
                }
                else
                {
                    Directory.Move(work, destination);
                    new DirectoryInfo(destination).Attributes &= ~FileAttributes.Hidden;
                }

                return new UnpackResult(destination, tree.FirstUnsafeEntry);
            }
            catch
            {
                TryDeleteDirectory(work);
                throw;
            }
        }, cancellationToken);

    /// <summary>
    /// <paramref name="name"/> itself when free, otherwise the first free " 2", " 3" … variant,
    /// numbered before the extension (macOS <c>availableName</c>).
    /// </summary>
    public static string AvailableName(string name, Func<string, bool> isTaken)
    {
        if (!isTaken(name))
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        var stem = extension.Length > 0 && extension.Length < name.Length ? name[..^extension.Length] : name;
        if (stem == name)
        {
            extension = string.Empty;
        }

        for (var counter = 2; ; counter++)
        {
            var candidate = $"{stem} {counter}{extension}";
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Where <paramref name="entryPath"/> lands under <paramref name="root"/>. The entry was
    /// already normalised when read; this is the second, independent check.
    /// </summary>
    public static string TargetPath(string entryPath, string prefix, string root)
    {
        string relative;
        if (prefix.Length == 0)
        {
            relative = entryPath;
        }
        else if (entryPath.StartsWith(prefix + "/", StringComparison.Ordinal))
        {
            relative = entryPath[(prefix.Length + 1)..];
        }
        else
        {
            // Not under the prefix at all: keep its own name rather than write it somewhere it
            // was never meant to go.
            relative = entryPath[(entryPath.LastIndexOf('/') + 1)..];
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine([fullRoot, .. relative.Split('/')]));
        if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.Split('/').Any(c => ArchiveEntry.Normalise(c) != c))
        {
            throw ArchiveException.UnsafeEntry(entryPath);
        }

        return candidate;
    }

    private static void WriteFile(Func<Stream> open, string target, ArchiveEntry entry, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        // Replace rather than append: extracting twice into the same staging folder must not
        // double a file, nor follow a file there that was swapped for something else.
        if (File.Exists(target))
        {
            File.Delete(target);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(ArchiveReader.BlockSize);
        try
        {
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0))
            {
                var data = open();
                int read;
                while ((read = data.Read(buffer, 0, ArchiveReader.BlockSize)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    onBytes?.Invoke(read);
                }
            }

            if (entry.ModificationDate > Models.CloudFileItem.UnknownDate)
            {
                try
                {
                    File.SetLastWriteTimeUtc(target, entry.ModificationDate.UtcDateTime);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
                {
                }
            }
        }
        catch
        {
            TryDelete(target);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
