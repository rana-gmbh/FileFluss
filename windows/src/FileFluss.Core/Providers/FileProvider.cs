// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;

namespace FileFluss.Core.Providers;

/// <summary>
/// Byte-level progress: called with the number of bytes moved since the previous call, from
/// whatever thread the transfer runs on. Callers accumulate; implementations must never
/// report a cumulative total.
/// </summary>
public delegate void ByteProgress(long deltaBytes);

/// <summary>
/// Everything a panel, a transfer, a sync or a comparison needs from a place that holds
/// files — a cloud account or the local disk. The macOS app kept two parallel stacks for
/// local and cloud panels; on Windows both are a <see cref="FileProvider"/>, so every
/// operation has one code path whichever side it runs on.
///
/// <para><b>Paths</b> are the provider's own: POSIX (<c>/Photos/2026</c>, root <c>/</c>)
/// for every cloud provider, native Windows paths for <see cref="LocalFileProvider"/>.
/// Callers build paths only through <see cref="CombinePath"/>, <see cref="GetParentPath"/>
/// and <see cref="GetName"/>, never by string concatenation.</para>
///
/// <para><b>Errors</b> that a user can act on are thrown as
/// <see cref="CloudProviderException"/> with a localized message. Cancellation surfaces as
/// <see cref="OperationCanceledException"/>.</para>
/// </summary>
public abstract class FileProvider
{
    /// <summary>True for the local file system, whose paths are native Windows paths.</summary>
    public virtual bool IsLocal => false;

    /// <summary>Where "Jump to root" goes.</summary>
    public virtual string RootPath => "/";

    /// <summary>
    /// Whether "a.txt" and "A.txt" are two different files here. True by default for cloud
    /// storage (S3, WebDAV, SFTP keep both); NTFS and the big consumer clouds (OneDrive,
    /// Dropbox, Box) fold case and override this to false. Name clashes are matched with this
    /// rule — matching case-insensitively on a case-sensitive store would make "replace"
    /// delete a different file.
    /// </summary>
    public virtual bool IsCaseSensitive => true;

    /// <summary>The comparer for names in one folder of this provider.</summary>
    public StringComparer NameComparer => IsCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    // MARK: - Paths

    public virtual string CombinePath(string directory, string name)
    {
        var trimmed = directory.TrimEnd('/');
        return trimmed.Length == 0 ? "/" + name : trimmed + "/" + name;
    }

    /// <summary>The containing folder; the root's parent is the root.</summary>
    public virtual string GetParentPath(string path)
    {
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    public virtual string GetName(string path)
    {
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    public virtual bool IsRoot(string path) => path is "/" or "";

    // MARK: - Required operations

    /// <summary>The direct children of <paramref name="path"/> (not recursive).</summary>
    public abstract Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the remote file to <paramref name="localPath"/>, replacing anything there.
    /// Must not leave a partial file behind on failure — stage and rename
    /// (<see cref="Net.HttpTransfer.SaveToFileAsync"/> does that for HTTP).
    /// </summary>
    public abstract Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads <paramref name="localPath"/> to <paramref name="remotePath"/> (the full target
    /// path, file name included). The parent folder exists. Replaces an existing file where
    /// the provider allows it natively; callers that want "replace" semantics on providers
    /// that would create a duplicate delete the existing item first.
    /// </summary>
    public abstract Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file, or a folder with everything in it.</summary>
    public abstract Task DeleteItemAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Creates one folder; its parent exists. Succeeds quietly if it already exists.</summary>
    public abstract Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Renames in place; <paramref name="newName"/> is a bare name.</summary>
    public abstract Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default);

    public abstract Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default);

    // MARK: - Optional operations

    /// <summary>
    /// Server-side move within the account to <paramref name="newPath"/> (full destination
    /// path). Throws <see cref="CloudErrorKind.NotImplemented"/> by default so callers fall
    /// back to download + upload.
    /// </summary>
    public virtual Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    /// <summary>Server-side copy, same conventions as <see cref="MoveItemAsync"/>.</summary>
    public virtual Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    /// <summary>Total bytes beneath a folder. The default walks the tree; override when the API can answer directly.</summary>
    public virtual async Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in await ListDirectoryAsync(pending.Pop(), cancellationToken).ConfigureAwait(false))
            {
                if (item.IsDirectory)
                {
                    pending.Push(item.Path);
                }
                else
                {
                    total += item.Size;
                }
            }
        }

        return total;
    }

    /// <summary>Server-side name search, or null when the provider has none (search then walks the index).</summary>
    public virtual Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CloudFileItem>?>(null);

    /// <summary>
    /// Stamps a modification date, so a copy keeps the source's "Date modified" as Explorer
    /// does. Best-effort: callers ignore failures, and the default throws NotImplemented.
    /// </summary>
    public virtual Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    /// <summary>
    /// The extension (without the dot) that <see cref="DownloadFileAsync"/> appends when it
    /// writes <paramref name="item"/>, or null when the download lands under the requested
    /// name. Google Workspace documents are exported ("Report" arrives as "Report.docx", see
    /// <see cref="IO.DownloadedFile"/>); a size reported for such an item is not the size of
    /// the export. Needed wherever a name must be promised before the bytes exist — dragging
    /// cloud files out to Explorer names every file up front.
    /// </summary>
    public virtual ValueTask<string?> ExportExtensionAsync(CloudFileItem item, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>(null);

    /// <summary>Free space at <paramref name="path"/>, for the pre-flight space check; null when unknown.</summary>
    public virtual Task<long?> AvailableSpaceAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult<long?>(null);
}
