// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Tests.Sync;

/// <summary>
/// Wraps a provider to script what the sync/compare/storage tests need on top of it: a free
/// space figure, failing listings, a hook before each listing, and call counters.
/// </summary>
internal sealed class WrappingProvider(FileProvider inner) : FileProvider
{
    private int _listCalls;
    private int _deleteCalls;

    public long? Available { get; set; }

    public bool AvailableThrows { get; set; }

    public HashSet<string> FailingListings { get; } = new(StringComparer.Ordinal);

    /// <summary>Awaited before every listing, with the path being listed.</summary>
    public Func<string, CancellationToken, Task>? BeforeList { get; set; }

    public int ListCalls => Volatile.Read(ref _listCalls);

    public int DeleteCalls => Volatile.Read(ref _deleteCalls);

    public override bool IsLocal => inner.IsLocal;

    public override string RootPath => inner.RootPath;

    public override string CombinePath(string directory, string name) => inner.CombinePath(directory, name);

    public override string GetParentPath(string path) => inner.GetParentPath(path);

    public override string GetName(string path) => inner.GetName(path);

    public override bool IsRoot(string path) => inner.IsRoot(path);

    public override async Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _listCalls);
        if (BeforeList is { } hook)
        {
            await hook(path, cancellationToken);
        }

        if (FailingListings.Contains(path))
        {
            throw CloudProviderException.CommandFailed("listing refused");
        }

        return await inner.ListDirectoryAsync(path, cancellationToken);
    }

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => inner.DownloadFileAsync(remotePath, localPath, onBytes, cancellationToken);

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => inner.UploadFileAsync(localPath, remotePath, onBytes, cancellationToken);

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _deleteCalls);
        return inner.DeleteItemAsync(path, cancellationToken);
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => inner.CreateDirectoryAsync(path, cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => inner.RenameItemAsync(path, newName, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => inner.GetFileMetadataAsync(path, cancellationToken);

    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => inner.SetModificationDateAsync(path, date, cancellationToken);

    public override Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
        => inner.FolderSizeAsync(path, cancellationToken);

    public override Task<long?> AvailableSpaceAsync(string path, CancellationToken cancellationToken = default)
        => AvailableThrows ? throw CloudProviderException.ServerError(503) : Task.FromResult(Available);
}
