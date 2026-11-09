// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers;

/// <summary>
/// The local file system behind the same contract as a cloud account. Paths are native
/// Windows paths; the empty path is "This PC", whose children are the ready drives.
///
/// <para>Two operations are pluggable because the best implementation is Win32-only and Core
/// stays platform-neutral: <see cref="CopyFileHandler"/> (the app installs CopyFileEx, which
/// keeps attributes and alternate streams and lets SMB servers copy server-side) and
/// <see cref="RecycleHandler"/> (the Recycle Bin).</para>
/// </summary>
public sealed class LocalFileProvider : FileProvider
{
    public static readonly LocalFileProvider Shared = new();

    /// <summary>Copies one file with progress; replaces the destination. Default: a buffered stream copy.</summary>
    public delegate Task CopyFile(string source, string destination, ByteProgress? onBytes, CancellationToken cancellationToken);

    public static CopyFile CopyFileHandler { get; set; } = StreamCopyAsync;

    /// <summary>
    /// Moves a path to the Recycle Bin; returns null once it is there, or why it could not be
    /// moved (permanent delete is then refused — the panel explains and offers it instead).
    /// </summary>
    public static Func<string, IO.RecycleRefusal?>? RecycleHandler { get; set; }

    private static readonly EnumerationOptions ListingOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private static readonly EnumerationOptions SizeOptions = new()
    {
        IgnoreInaccessible = true,
        // A junction or symlink to a parent folder would otherwise be counted forever.
        AttributesToSkip = FileAttributes.ReparsePoint,
        RecurseSubdirectories = true,
    };

    public override bool IsLocal => true;

    /// <summary>NTFS folds case.</summary>
    public override bool IsCaseSensitive => false;

    /// <summary>"This PC".</summary>
    public override string RootPath => string.Empty;

    // MARK: - Paths

    public override string CombinePath(string directory, string name)
        => directory.Length == 0 ? name : Path.Combine(directory, name);

    public override string GetParentPath(string path)
    {
        if (path.Length == 0)
        {
            return string.Empty;
        }

        var full = Path.TrimEndingDirectorySeparator(path);
        return Path.GetDirectoryName(full) ?? string.Empty;
    }

    public override string GetName(string path)
    {
        if (path.Length == 0)
        {
            return Localization.L("This PC");
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : DriveLabel(path);
    }

    public override bool IsRoot(string path) => path.Length == 0;

    /// <summary>How long a drive's name is reused before the volume is asked again.</summary>
    private const long DriveLabelLifetimeMs = 10_000;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Label, long At)> DriveLabels = new(StringComparer.OrdinalIgnoreCase);

    static LocalFileProvider()
    {
        // "Local Disk" and friends are translated.
        Localization.LanguageChanged += (_, _) => DriveLabels.Clear();
    }

    /// <summary>
    /// "Local Disk (C:)", the way Explorer names a drive. Remembered for a few seconds: the
    /// window title and path bar ask for it on every change of a panel showing a drive's root,
    /// and each answer waits for the volume (a sleeping disk or network share: seconds).
    /// </summary>
    public static string DriveLabel(string rootPath)
    {
        var now = Environment.TickCount64;
        if (DriveLabels.TryGetValue(rootPath, out var known) && now - known.At < DriveLabelLifetimeMs)
        {
            return known.Label;
        }

        var label = ReadDriveLabel(rootPath);
        DriveLabels[rootPath] = (label, now);
        return label;
    }

    private static string ReadDriveLabel(string rootPath)
    {
        var letter = rootPath.TrimEnd('\\', '/');
        try
        {
            var drive = new DriveInfo(rootPath);
            if (drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel))
            {
                return $"{drive.VolumeLabel} ({letter})";
            }

            var fallback = drive.DriveType switch
            {
                DriveType.Removable => Localization.L("USB Drive"),
                DriveType.Network => Localization.L("Network Drive"),
                DriveType.CDRom => Localization.L("DVD Drive"),
                _ => Localization.L("Local Disk"),
            };
            return $"{fallback} ({letter})";
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return letter;
        }
    }

    // MARK: - Listing

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<CloudFileItem>>(() =>
        {
            if (path.Length == 0)
            {
                return ListDrives();
            }

            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                throw CloudProviderException.NotFound(path);
            }

            var items = new List<CloudFileItem>();
            try
            {
                foreach (var info in directory.EnumerateFileSystemInfos("*", ListingOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    items.Add(ToItem(info));
                }
            }
            catch (UnauthorizedAccessException)
            {
                throw CloudProviderException.CommandFailed(Localization.L("You don't have permission to open \"{0}\".", GetName(path)));
            }

            return items;
        }, cancellationToken);

    private static List<CloudFileItem> ListDrives()
    {
        var items = new List<CloudFileItem>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try
            {
                ready = drive.IsReady;
            }
            catch (IOException)
            {
                ready = false;
            }

            if (!ready)
            {
                continue;
            }

            long used = 0;
            try
            {
                used = drive.TotalSize - drive.TotalFreeSpace;
            }
            catch (IOException)
            {
            }

            items.Add(new CloudFileItem(drive.RootDirectory.FullName, DriveLabel(drive.RootDirectory.FullName), drive.RootDirectory.FullName, true, used, CloudFileItem.UnknownDate)
            {
                IconOverride = drive.DriveType switch
                {
                    DriveType.Removable => "drive.removable",
                    DriveType.Network => "drive.network",
                    DriveType.CDRom => "drive.optical",
                    _ => "drive",
                },
                Role = CloudItemRole.DriveRoot,
            });
        }

        return items;
    }

    public static CloudFileItem ToItem(FileSystemInfo info)
    {
        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
        return new CloudFileItem(
            info.FullName,
            info.Name,
            info.FullName,
            isDirectory,
            isDirectory ? 0 : ((FileInfo)info).Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime())
        {
            CreationDate = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero).ToLocalTime(),
            IsHidden = (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0,
            IsSymlink = (info.Attributes & FileAttributes.ReparsePoint) != 0,
            DownloadStatus = (info.Attributes & (FileAttributes)0x00400000) != 0 // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
                ? CloudDownloadStatus.Evicted
                : CloudDownloadStatus.Local,
        };
    }

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists)
            {
                throw CloudProviderException.NotFound(path);
            }

            return ToItem(info);
        }, cancellationToken);

    // MARK: - Transfers ("download" and "upload" are both local copies here)

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => CopyFileHandler(remotePath, localPath, onBytes, cancellationToken);

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => CopyFileHandler(localPath, remotePath, onBytes, cancellationToken);

    /// <summary>Buffered copy with progress, staged through a partial file and timestamp-preserving.</summary>
    public static async Task StreamCopyAsync(string source, string destination, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var partial = destination + ".fileflusspart";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                output.SetLength(input.Length);
                await HttpTransfer.CopyAsync(input, output, onBytes, cancellationToken).ConfigureAwait(false);
            }

            File.Move(partial, destination, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(partial);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }

        // The copy is complete; dates are best-effort (a read-only target, a FAT stick).
        try
        {
            var info = new FileInfo(source);
            File.SetLastWriteTimeUtc(destination, info.LastWriteTimeUtc);
            File.SetCreationTimeUtc(destination, info.CreationTimeUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
        }
    }

    // MARK: - Mutations

    /// <summary>Moves to the Recycle Bin when the app installed a handler; otherwise deletes permanently.</summary>
    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            if (RecycleHandler is { } recycle)
            {
                if (recycle(path) is { } refusal)
                {
                    var where = refusal.Location is null ? string.Empty : $", at {refusal.Location}";
                    Diagnostics.SupportLogger.Shared.Log($"Recycle Bin refused an item: {refusal.Kind}, code {refusal.CodeText}{where}", "fs", Diagnostics.LogLevel.Error);
                    throw new IO.RecycleRefusedException(path, refusal);
                }

                return;
            }

            DeletePermanently(path);
        }, cancellationToken);

    public static void DeletePermanently(string path)
    {
        if (Directory.Exists(path))
        {
            // Clear read-only flags first — Directory.Delete refuses read-only files.
            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            File.Delete(path);
        }
        else
        {
            throw CloudProviderException.NotFound(path);
        }
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => Directory.CreateDirectory(path), cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var target = Path.Combine(GetParentPath(path), newName);
            MoveLocal(path, target);
        }, cancellationToken);

    public override async Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        // File.Move crosses volumes by itself; Directory.Move cannot, so a folder moving to
        // another drive is copied and then deleted, as Explorer does.
        if (File.Exists(path) || SameVolume(path, newPath))
        {
            await Task.Run(() => MoveLocal(path, newPath), cancellationToken).ConfigureAwait(false);
            return;
        }

        await CopyDirectoryAsync(path, newPath, null, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => DeletePermanently(path), cancellationToken).ConfigureAwait(false);
    }

    public override async Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(path))
        {
            await CopyDirectoryAsync(path, newPath, null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CopyFileHandler(path, newPath, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyDirectoryAsync(string source, string destination, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos("*", ListingOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, entry.Name);
            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                await CopyDirectoryAsync(entry.FullName, target, onBytes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CopyFileHandler(entry.FullName, target, onBytes, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void MoveLocal(string source, string target)
    {
        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            return;
        }

        var caseOnly = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(source))
        {
            if (caseOnly)
            {
                // A case-only directory rename needs a hop through a temporary name.
                var hop = source + ".ff-" + Guid.NewGuid().ToString("N")[..8];
                Directory.Move(source, hop);
                Directory.Move(hop, target);
            }
            else
            {
                Directory.Move(source, target);
            }
        }
        else if (File.Exists(source))
        {
            File.Move(source, target, overwrite: false);
        }
        else
        {
            throw CloudProviderException.NotFound(source);
        }
    }

    private static bool SameVolume(string a, string b)
        => string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    public override Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            long total = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SizeOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += file.Length;
            }

            return total;
        }, cancellationToken);

    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            if (Directory.Exists(path))
            {
                Directory.SetLastWriteTimeUtc(path, date.UtcDateTime);
            }
            else
            {
                File.SetLastWriteTimeUtc(path, date.UtcDateTime);
            }
        }, cancellationToken);

    public override Task<long?> AvailableSpaceAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run<long?>(() =>
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }, cancellationToken);
}
