// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Search;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Search;

/// <summary>A search index in a temporary file, removed (with its WAL files) on dispose.</summary>
internal sealed class TempIndex : IDisposable
{
    public TempIndex()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff-index-test-" + Guid.NewGuid().ToString("N") + ".sqlite");
        Index = new SearchIndex(Path);
    }

    public string Path { get; }

    public SearchIndex Index { get; }

    /// <summary>Another instance on the same file — "the next launch".</summary>
    public SearchIndex Reopen() => new(Path);

    public void Dispose()
    {
        Index.Dispose();
        foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>A temporary folder tree for drive and local-search tests.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff-search-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string relative, string content = "x")
    {
        var path = System.IO.Path.Combine(Root, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public string Folder(string relative)
    {
        var path = System.IO.Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// A <see cref="MemoryCloudProvider"/> with the optional extras search and indexing care about:
/// server-side search, folders that fail to list, and listings that wait for the test.
/// </summary>
internal sealed class SearchableProvider : CloudProvider
{
    public SearchableProvider(MemoryCloudProvider? inner = null, bool supportsSearch = true)
        : base(Guid.NewGuid(), new MemoryCredentialStore())
    {
        Inner = inner ?? new MemoryCloudProvider();
        SupportsSearch = supportsSearch;
    }

    public MemoryCloudProvider Inner { get; }

    public bool SupportsSearch { get; set; }

    public Exception? SearchFailure { get; set; }

    public int SearchCalls { get; private set; }

    public HashSet<string> FailingListings { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, every listing waits for it first.</summary>
    public TaskCompletionSource? ListingGate { get; set; }

    public int Listings { get; private set; }

    public override CloudProviderType ProviderType => CloudProviderType.Dropbox;

    protected override string CredentialKeyPrefix => "searchable";

    public override bool IsAuthenticated => true;

    public override ConnectSpec ConnectSpec => new();

    public override Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
        => Task.FromResult(new ConnectResult("memory"));

    public override async Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Listings++;
        if (ListingGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (FailingListings.Contains(CloudPath.Normalize(path)))
        {
            throw CloudProviderException.ServerError(500);
        }

        return await Inner.ListDirectoryAsync(path, cancellationToken);
    }

    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
    {
        if (!SupportsSearch)
        {
            return null;
        }

        SearchCalls++;
        if (SearchFailure is { } failure)
        {
            throw failure;
        }

        var hits = new List<CloudFileItem>();
        foreach (var candidate in Inner.AllPaths.Where(p => p != "/" && CloudPath.Name(p).Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            if (path is null || CloudPath.IsWithin(candidate, path))
            {
                // Server ids differ from paths, as they do for most real providers.
                hits.Add(await Inner.GetFileMetadataAsync(candidate, cancellationToken) with { Id = "srv-" + candidate });
            }
        }

        return hits;
    }

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Inner.DownloadFileAsync(remotePath, localPath, onBytes, cancellationToken);

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Inner.UploadFileAsync(localPath, remotePath, onBytes, cancellationToken);

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Inner.DeleteItemAsync(path, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Inner.CreateDirectoryAsync(path, cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Inner.RenameItemAsync(path, newName, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => Inner.GetFileMetadataAsync(path, cancellationToken);
}

internal static class SearchTestData
{
    public static readonly DateTimeOffset Stamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    public static CloudFileItem CloudItem(string path, bool isDirectory = false, long size = 10)
        => new(path, CloudPath.Name(path), path, isDirectory, size, Stamp);

    public static CloudAccount Account(Guid id, string name = "Test Cloud", bool connected = true, bool offlineMode = false)
        => new() { Id = id, ProviderType = CloudProviderType.Dropbox, DisplayName = name, IsConnected = connected, IsOfflineMode = offlineMode };
}
