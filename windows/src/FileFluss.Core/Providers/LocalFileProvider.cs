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

    /// <summary>
    /// <see cref="EnumerationOptions.IgnoreInaccessible"/> is off on purpose: on, a folder that
    /// can't be opened lists as empty, and an empty-looking folder tells Mirror to delete its
    /// contents on the other side (macOS 1.6: "Mirror stops on a folder it can't read"). A
    /// one-level listing never opens its entries, so nothing else is affected.
    /// </summary>
    private static readonly EnumerationOptions ListingOptions = new()
    {
        IgnoreInaccessible = false,
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

        var label = DriveLabelReader(rootPath);
        DriveLabels[rootPath] = (label, now);
        return label;
    }

    /// <summary>
    /// Raised (on a thread-pool thread) with a drive's root once a label refreshed in the
    /// background differs from what <see cref="CachedDriveLabel"/> handed out before.
    /// </summary>
    public static event EventHandler<string>? DriveLabelChanged;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> RefreshingDriveLabels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test seam: reads a drive's label from the volume (normally <see cref="ReadDriveLabel"/>).</summary>
    internal static Func<string, string> DriveLabelReader { get; set; } = ReadDriveLabel;

    /// <summary>
    /// A drive's name for the UI thread, which must never wait for the volume: a mapped drive
    /// whose server is gone takes 20–60 s to say so, and the path bar and window title asked on
    /// every navigation. Answers with the remembered label — even an old one — or, before the
    /// drive was ever asked, its letter ("Z:"), and refreshes on the thread pool;
    /// <see cref="DriveLabelChanged"/> tells when a better name has arrived.
    /// </summary>
    public static string CachedDriveLabel(string rootPath)
    {
        if (DriveLabels.TryGetValue(rootPath, out var known))
        {
            if (Environment.TickCount64 - known.At >= DriveLabelLifetimeMs)
            {
                RefreshDriveLabel(rootPath, known.Label);
            }

            return known.Label;
        }

        var letter = rootPath.TrimEnd('\\', '/');
        RefreshDriveLabel(rootPath, letter);
        return letter;
    }

    /// <summary>A folder's name for display without touching the disk: a drive root's is <see cref="CachedDriveLabel"/>.</summary>
    public static string DisplayName(string path)
    {
        if (path.Length == 0)
        {
            return Localization.L("This PC");
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : CachedDriveLabel(path);
    }

    private static void RefreshDriveLabel(string rootPath, string shown)
    {
        if (!RefreshingDriveLabels.TryAdd(rootPath, 0))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var label = DriveLabelReader(rootPath);
                DriveLabels[rootPath] = (label, Environment.TickCount64);
                if (!string.Equals(label, shown, StringComparison.Ordinal))
                {
                    DriveLabelChanged?.Invoke(null, rootPath);
                }
            }
            finally
            {
                RefreshingDriveLabels.TryRemove(rootPath, out _);
            }
        });
    }

    /// <summary>Forgets every remembered drive name (tests).</summary>
    internal static void ForgetDriveLabels() => DriveLabels.Clear();

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

            // Skipping what can't be read hides the folder itself being shut too: a folder of
            // another user's, or System Volume Information, listed as "Empty Folder". Asked
            // only when nothing came back, so an ordinary listing costs nothing extra.
            if (items.Count == 0)
            {
                ThrowIfUnreadable(path);
            }

            return items;
        }, cancellationToken);

    /// <summary>Opens the folder for listing without skipping a refusal, and turns one into a message the panel shows.</summary>
    private void ThrowIfUnreadable(string path)
    {
        try
        {
            using var probe = Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 }).GetEnumerator();
            _ = probe.MoveNext();
        }
        catch (UnauthorizedAccessException)
        {
            throw CloudProviderException.CommandFailed(Localization.L("You don't have permission to open \"{0}\".", GetName(path)));
        }
        catch (DirectoryNotFoundException)
        {
            throw CloudProviderException.NotFound(path);
        }
        catch (IOException e)
        {
            // A drive or share gone mid-listing, a device not ready.
            throw CloudProviderException.CommandFailed(e.Message, e);
        }
    }

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

    /// <summary>
    /// Puts a finished staged copy in place of <paramref name="destination"/>. Only called once
    /// the user chose to replace what's there (or nothing is), so a read-only file in the way
    /// is cleared and replaced, as Explorer does — the move itself is refused on one ("access
    /// denied"), which failed every Replace onto a read-only file. The copy keeps its own
    /// attributes, so a read-only source still lands read-only.
    /// </summary>
    public static void MoveIntoPlace(string partial, string destination)
    {
        try
        {
            File.Move(partial, destination, overwrite: true);
        }
        catch (UnauthorizedAccessException) when (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
            File.Move(partial, destination, overwrite: true);
        }
    }

    /// <summary>
    /// Removes a staged copy after a failure, best-effort. CopyFileEx carries a read-only
    /// source's attribute over to the partial file, and a read-only file can't be deleted.
    /// </summary>
    public static void DeletePartial(string partial)
    {
        try
        {
            if (File.Exists(partial))
            {
                var attributes = File.GetAttributes(partial);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(partial, attributes & ~FileAttributes.ReadOnly);
                }

                File.Delete(partial);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

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

            MoveIntoPlace(partial, destination);
        }
        catch
        {
            DeletePartial(partial);
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
        if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is not null)
        {
            // A junction or symbolic link to a folder: only the link goes. Walking it first to
            // clear read-only flags reached into the folder it points to and changed files
            // there (Directory.Delete itself already removed just the link).
            Directory.Delete(path, recursive: false);
        }
        else if (Directory.Exists(path))
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
