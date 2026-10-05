// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Tests.Mount;

/// <summary>
/// Wraps a provider to count what the server asks of it and to inject failures, so tests
/// can tell a cache hit from a download and check that a failed overwrite keeps the
/// original.
/// </summary>
internal sealed class InstrumentedProvider(FileProvider inner) : FileProvider
{
    private int _downloads;
    private int _metadataCalls;
    private int _listings;

    public int Downloads => Volatile.Read(ref _downloads);

    public int MetadataCalls => Volatile.Read(ref _metadataCalls);

    public int Listings => Volatile.Read(ref _listings);

    /// <summary>Uploads whose target path matches fail with a server error.</summary>
    public Func<string, bool> FailUpload { get; set; } = _ => false;

    /// <summary>Delay inside every download, to make concurrent GETs overlap.</summary>
    public TimeSpan DownloadDelay { get; set; }

    public long? AvailableSpace { get; set; }

    public override bool IsCaseSensitive => inner.IsCaseSensitive;

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _listings);
        return inner.ListDirectoryAsync(path, cancellationToken);
    }

    public override async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _downloads);
        if (DownloadDelay > TimeSpan.Zero)
        {
            await Task.Delay(DownloadDelay, cancellationToken);
        }

        await inner.DownloadFileAsync(remotePath, localPath, onBytes, cancellationToken);
    }

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => FailUpload(remotePath)
            ? throw CloudProviderException.ServerError(500)
            : inner.UploadFileAsync(localPath, remotePath, onBytes, cancellationToken);

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => inner.DeleteItemAsync(path, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => inner.CreateDirectoryAsync(path, cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => inner.RenameItemAsync(path, newName, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _metadataCalls);
        return inner.GetFileMetadataAsync(path, cancellationToken);
    }

    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => inner.MoveItemAsync(path, newPath, cancellationToken);

    public override Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => inner.CopyItemAsync(path, newPath, cancellationToken);

    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => inner.SetModificationDateAsync(path, date, cancellationToken);

    public override Task<long?> AvailableSpaceAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(AvailableSpace);
}
