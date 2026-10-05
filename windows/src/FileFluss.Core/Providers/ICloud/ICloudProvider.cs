// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.ICloud;

/// <summary>
/// iCloud Drive through iCloud for Windows — the Windows counterpart of the macOS
/// <c>ICloudProvider</c>. As on the Mac, the "remote" store is a local folder the system keeps
/// in sync with iCloud (here a Cloud Files sync root, usually <c>%USERPROFILE%\iCloudDrive</c>;
/// see <see cref="ICloudDriveLocator"/>). FileFluss only navigates it with POSIX paths
/// relative to that folder and lets iCloud for Windows handle upload, conflicts and eviction.
///
/// <para>Files that are not downloaded are placeholders flagged
/// <c>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</c> (or OFFLINE); they list as
/// <see cref="CloudDownloadStatus.Evicted"/> with their real size, and reading one — which is
/// all a download is here — makes the Cloud Files filter hydrate it transparently, the job
/// <c>startDownloadingUbiquitousItem</c> does on macOS.</para>
/// </summary>
[CloudProvider(CloudProviderType.ICloud)]
public sealed class ICloudProvider : CloudProvider
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes Unpopulated = RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline;

    /// <summary>Hidden and system entries (desktop.ini) are skipped, as macOS skips hidden files.</summary>
    private static readonly EnumerationOptions ListingOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private readonly Func<string?> _locateRoot;
    private volatile string? _root;

    public ICloudProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    /// <param name="locateRoot">Test seam for <see cref="ICloudDriveLocator.FindRoot()"/>.</param>
    internal ICloudProvider(Guid accountId, ICredentialStore credentials, Func<string?>? locateRoot)
        : base(accountId, credentials)
    {
        _locateRoot = locateRoot ?? ICloudDriveLocator.FindRoot;
    }

    /// <summary>The folder found when the account was connected, so a relocated iCloud Drive keeps working.</summary>
    internal sealed record ICloudSettings(string RootPath);

    public override CloudProviderType ProviderType => CloudProviderType.ICloud;

    /// <summary>Names are case-insensitive here ("a.txt" and "A.txt" are one file).</summary>
    public override bool IsCaseSensitive => false;

    protected override string CredentialKeyPrefix => "icloud";

    /// <summary>True while the iCloud Drive folder exists — like macOS, the system sign-in is the credential.</summary>
    public override bool IsAuthenticated => ResolveRoot() is not null;

    /// <summary>The iCloud Drive folder this account maps "/" to, or null when it is gone.</summary>
    public string? RootFolder => ResolveRoot();

    // MARK: - Connecting

    public override ConnectSpec ConnectSpec { get; } = new(
        ConnectField.Info("FileFluss uses iCloud for Windows — no password needed here. Install iCloud for Windows from the Microsoft Store, sign in with your Apple Account and turn on iCloud Drive."),
        ConnectField.Info("Files that aren't currently downloaded show a small iCloud badge in the file list. Copying or syncing them triggers a transparent download first."));

    /// <summary>
    /// Succeeds when the iCloud Drive folder exists. There is no account name to read without
    /// Apple's APIs, so the account is called "iCloud Drive", as on macOS.
    /// </summary>
    public override Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var root = _locateRoot();
        if (root is null || !Directory.Exists(root))
        {
            throw CloudProviderException.CommandFailed(Localization.L(
                "iCloud Drive wasn't found on this PC. Install iCloud for Windows from the Microsoft Store, sign in with your Apple Account and turn on iCloud Drive, then try again."));
        }

        Credentials.Save(CredentialKey, new ICloudSettings(root));
        _root = root;
        SupportLogger.Shared.Log($"[iCloud] Connected, root={root}", "iCloud");
        return Task.FromResult(new ConnectResult(null) { DisplayName = "iCloud Drive" });
    }

    public override Task DisconnectAsync()
    {
        _root = null;
        return base.DisconnectAsync();
    }

    private string? ResolveRoot()
    {
        if (_root is { } cached && Directory.Exists(cached))
        {
            return cached;
        }

        string? found = null;
        if (Credentials.Load<ICloudSettings>(CredentialKey) is { RootPath: { Length: > 0 } stored } && Directory.Exists(stored))
        {
            found = stored;
        }
        else
        {
            found = _locateRoot();
            if (found is not null && !Directory.Exists(found))
            {
                found = null;
            }
        }

        _root = found;
        return found;
    }

    private string RequireRoot() => ResolveRoot() ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - Path mapping

    /// <summary>
    /// The local path for a POSIX path beneath the iCloud Drive folder. "." and ".." components
    /// and anything that would resolve outside the folder are refused, so a crafted path can
    /// never reach the rest of the disk.
    /// </summary>
    internal string LocalPath(string remotePath)
    {
        var root = RequireRoot();
        var components = CloudPath.Components(remotePath);
        if (components.Length == 0)
        {
            return root;
        }

        if (components.Any(c => c is "." or ".." || c.Contains('\\', StringComparison.Ordinal) || c.Contains(':', StringComparison.Ordinal)))
        {
            throw CloudProviderException.NotFound(remotePath);
        }

        var full = Path.GetFullPath(Path.Combine([root, .. components]));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw CloudProviderException.NotFound(remotePath);
        }

        return full;
    }

    // MARK: - Listing

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Run(path, () =>
        {
            var directory = new DirectoryInfo(LocalPath(path));
            if (!directory.Exists)
            {
                throw CloudProviderException.NotFound(path);
            }

            var parent = CloudPath.Normalize(path);
            var items = new List<CloudFileItem>();
            foreach (var info in directory.EnumerateFileSystemInfos("*", ListingOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(ToItem(info, CloudPath.Combine(parent, info.Name)));
            }

            return (IReadOnlyList<CloudFileItem>)items;
        }, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => Run(path, () =>
        {
            var local = LocalPath(path);
            FileSystemInfo info = Directory.Exists(local) ? new DirectoryInfo(local) : new FileInfo(local);
            if (!info.Exists)
            {
                throw CloudProviderException.NotFound(path);
            }

            var normalized = CloudPath.Normalize(path);
            var item = ToItem(info, normalized);
            return normalized == "/" ? item with { Name = "iCloud Drive" } : item;
        }, cancellationToken);

    /// <summary>
    /// Sums the logical sizes of every file beneath the folder. A placeholder reports its full
    /// size without being downloaded — the Windows equivalent of macOS preferring
    /// <c>totalFileSize</c>. Junctions and symbolic links are not followed; Cloud Files
    /// placeholders (also reparse points) are.
    /// </summary>
    public override Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
        => Run(path, () =>
        {
            long total = 0;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(LocalPath(path)));
            while (pending.Count > 0)
            {
                foreach (var info in pending.Pop().EnumerateFileSystemInfos("*", ListingOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (info is DirectoryInfo directory)
                    {
                        if (!IsLink(directory))
                        {
                            pending.Push(directory);
                        }
                    }
                    else if (info is FileInfo file)
                    {
                        total += file.Length;
                    }
                }
            }

            return total;
        }, cancellationToken);

    // No StorageQuotaAsync: Apple exposes no iCloud quota on Windows either, and the local
    // disk's capacity would mislead — evicted files use iCloud storage, not this PC's.

    // MARK: - Transfers

    /// <summary>A copy out of the folder. Reading a placeholder hydrates it first, transparently.</summary>
    public override async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var source = LocalPath(remotePath);
        if (!File.Exists(source))
        {
            throw CloudProviderException.NotFound(remotePath);
        }

        await RunAsync(remotePath, () => LocalFileProvider.CopyFileHandler(source, localPath, onBytes, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>A copy into the folder; iCloud for Windows uploads it from there.</summary>
    public override async Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var target = LocalPath(remotePath);
        await RunAsync(remotePath, () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            return LocalFileProvider.CopyFileHandler(localPath, target, onBytes, cancellationToken);
        }).ConfigureAwait(false);
    }

    // MARK: - Mutations

    /// <summary>Deletes for good locally; iCloud keeps the item in "Recently Deleted" for 30 days.</summary>
    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Run(path, () =>
        {
            var target = LocalPath(path);
            if (target == RequireRoot() || (!File.Exists(target) && !Directory.Exists(target)))
            {
                throw CloudProviderException.NotFound(path);
            }

            LocalFileProvider.DeletePermanently(target);
            return true;
        }, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Run(path, () => Directory.CreateDirectory(LocalPath(path)), cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Run(path, () =>
        {
            if (newName.Length == 0 || newName is "." or ".." || newName.IndexOfAny(['/', '\\', ':']) >= 0)
            {
                throw CloudProviderException.CommandFailed(Localization.L("Invalid file name."));
            }

            Move(path, CloudPath.Combine(CloudPath.Parent(path), newName));
            return true;
        }, cancellationToken);

    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => Run(path, () =>
        {
            Move(path, newPath);
            return true;
        }, cancellationToken);

    public override async Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        var source = LocalPath(path);
        var target = LocalPath(newPath);
        await RunAsync(path, async () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(source))
            {
                await CopyDirectoryAsync(source, target, cancellationToken).ConfigureAwait(false);
            }
            else if (File.Exists(source))
            {
                await LocalFileProvider.CopyFileHandler(source, target, null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw CloudProviderException.NotFound(path);
            }
        }).ConfigureAwait(false);
    }

    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => Run(path, () =>
        {
            var target = LocalPath(path);
            if (Directory.Exists(target))
            {
                Directory.SetLastWriteTimeUtc(target, date.UtcDateTime);
            }
            else if (File.Exists(target))
            {
                File.SetLastWriteTimeUtc(target, date.UtcDateTime);
            }
            else
            {
                throw CloudProviderException.NotFound(path);
            }

            return true;
        }, cancellationToken);

    private void Move(string path, string newPath)
    {
        var source = LocalPath(path);
        var target = LocalPath(newPath);
        if (source == RequireRoot())
        {
            throw CloudProviderException.NotFound(path);
        }

        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(source))
        {
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                // A case-only folder rename needs a hop through a temporary name.
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
            throw CloudProviderException.NotFound(path);
        }
    }

    private static async Task CopyDirectoryAsync(string source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos("*", ListingOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                if (!IsLink(directory))
                {
                    await CopyDirectoryAsync(directory.FullName, destination, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await LocalFileProvider.CopyFileHandler(entry.FullName, destination, null, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // MARK: - Items

    internal static CloudFileItem ToItem(FileSystemInfo info, string remotePath)
    {
        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
        return new CloudFileItem(
            remotePath,
            info.Name,
            remotePath,
            isDirectory,
            isDirectory ? 0 : ((FileInfo)info).Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))
        {
            CreationDate = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            IsHidden = CloudFileItem.IsDotHidden(info.Name),
            IsSymlink = (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null,
            DownloadStatus = DownloadStatus(info.Attributes, isDirectory),
        };
    }

    /// <summary>Folders are always "local", as on macOS; a file is evicted while it is a dehydrated placeholder.</summary>
    internal static CloudDownloadStatus DownloadStatus(FileAttributes attributes, bool isDirectory)
        => !isDirectory && (attributes & Unpopulated) != 0 ? CloudDownloadStatus.Evicted : CloudDownloadStatus.Local;

    /// <summary>A junction or symbolic link (which could loop); a Cloud Files placeholder folder has no link target.</summary>
    private static bool IsLink(FileSystemInfo info)
        => (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null;

    // MARK: - Error mapping

    private static Task<T> Run<T>(string path, Func<T> work, CancellationToken cancellationToken)
        => Task.Run(() => Map(path, work), cancellationToken);

    private static async Task RunAsync(string path, Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw CloudProviderException.NotFound(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A failed hydration ("The cloud operation was not completed…") lands here with
            // the system's own explanation, which is what the user needs to see.
            throw CloudProviderException.CommandFailed(e.Message, e);
        }
    }

    private static T Map<T>(string path, Func<T> work)
    {
        try
        {
            return work();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw CloudProviderException.NotFound(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw CloudProviderException.CommandFailed(e.Message, e);
        }
    }
}
