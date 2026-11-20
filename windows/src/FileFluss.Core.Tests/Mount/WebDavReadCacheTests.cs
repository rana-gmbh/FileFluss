// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Mount;

namespace FileFluss.Core.Tests.Mount;

/// <summary>Port of the macOS <c>WebDAVReadCacheTests</c>, plus the Windows open-file cases.</summary>
public sealed class WebDavReadCacheTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "filefluss-cache-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void StoreAndLookupRoundTrip()
    {
        var cache = MakeCache(maxMB: 10);
        var temp = MakeTempFile(16 * 1024);
        Assert.True(cache.Store("/a.bin", "v1", temp));
        Assert.False(File.Exists(temp), "store should move into the cache dir");

        var hit = cache.Lookup("/a.bin", "v1");
        Assert.NotNull(hit);
        Assert.StartsWith(cache.Root, hit);
        Assert.Null(cache.Lookup("/a.bin", "v2"));
        Assert.Null(cache.Lookup("/other", "v1"));

        using var stream = cache.TryOpen("/a.bin", "v1");
        Assert.Equal(16 * 1024, stream!.Length);
    }

    [Fact]
    public void InvalidateRemovesEveryVersion()
    {
        var cache = MakeCache(maxMB: 10);
        cache.Store("/a", "v1", MakeTempFile(1024));
        cache.Store("/a", "v2", MakeTempFile(1024));
        cache.Store("/b", "v1", MakeTempFile(1024));
        cache.Invalidate("/a");
        Assert.Null(cache.Lookup("/a", "v1"));
        Assert.Null(cache.Lookup("/a", "v2"));
        Assert.NotNull(cache.Lookup("/b", "v1"));
        Assert.Equal(1024, cache.TotalBytes);
    }

    [Fact]
    public void InvalidateTreeTakesAFoldersFilesAlong()
    {
        var cache = MakeCache(maxMB: 10);
        cache.Store("/dir/a", "v", MakeTempFile(10));
        cache.Store("/dir/sub/b", "v", MakeTempFile(10));
        cache.Store("/dir2/c", "v", MakeTempFile(10));
        cache.InvalidateTree("/dir");
        Assert.Null(cache.Lookup("/dir/a", "v"));
        Assert.Null(cache.Lookup("/dir/sub/b", "v"));
        Assert.NotNull(cache.Lookup("/dir2/c", "v"));
    }

    [Fact]
    public void LruEvictsOldestUnderBudget()
    {
        // 3-entry budget at ~1 MB each.
        var cache = MakeCache(maxMB: 3);
        cache.Store("/oldest", "v", MakeTempFile(1024 * 1024));
        cache.Store("/middle", "v", MakeTempFile(1024 * 1024));
        cache.Store("/newest", "v", MakeTempFile(1024 * 1024));

        // Touch /oldest so it becomes most-recently-used.
        cache.Lookup("/oldest", "v");

        // A 4th entry forces eviction of the now least-recently-used (/middle).
        cache.Store("/extra", "v", MakeTempFile(1024 * 1024));
        Assert.Null(cache.Lookup("/middle", "v"));
        Assert.NotNull(cache.Lookup("/oldest", "v"));
        Assert.NotNull(cache.Lookup("/newest", "v"));
        Assert.NotNull(cache.Lookup("/extra", "v"));
    }

    [Fact]
    public void OversizeFileIsNotCached()
    {
        var cache = MakeCache(maxMB: 1);
        var temp = MakeTempFile(2 * 1024 * 1024);
        Assert.False(cache.Store("/huge", "v", temp));
        Assert.True(File.Exists(temp), "the caller still owns the temp file");
        Assert.Equal(0, cache.TotalBytes);
    }

    [Fact]
    public void EvictionWhileExplorerStillReadsDoesNotFail()
    {
        var cache = MakeCache(maxMB: 1);
        cache.Store("/a", "v", MakeTempFile(600 * 1024));
        using var reader = cache.TryOpen("/a", "v")!;

        // Storing /b pushes /a out while its stream is open.
        Assert.True(cache.Store("/b", "v", MakeTempFile(600 * 1024)));
        Assert.Null(cache.Lookup("/a", "v"));
        Assert.Equal(600 * 1024, reader.Length);
        Assert.Equal(0xA5, reader.ReadByte());
    }

    [Fact]
    public void ShrinkingTheBudgetEvicts()
    {
        var cache = MakeCache(maxMB: 10);
        cache.Store("/a", "v", MakeTempFile(1024 * 1024));
        cache.Store("/b", "v", MakeTempFile(1024 * 1024));
        cache.SetMaxBytes(1024 * 1024);
        Assert.Equal(1024 * 1024, cache.TotalBytes);
        Assert.Null(cache.Lookup("/a", "v"));
    }

    [Fact]
    public void ClearWipesCacheAndStartupDropsOrphans()
    {
        var cache = MakeCache(maxMB: 10);
        cache.Store("/x", "v", MakeTempFile(1024));
        cache.Clear();
        Assert.Null(cache.Lookup("/x", "v"));
        Assert.Empty(Directory.GetFiles(cache.Root));

        cache.Store("/y", "v", MakeTempFile(1024));
        var reopened = new WebDavReadCache(cache.Root, 10 * 1024 * 1024);
        Assert.Empty(Directory.GetFiles(reopened.Root));
    }

    [Fact]
    public void SidecarStoreRoundTripsNamesNtfsWouldReject()
    {
        var store = new WebDavSidecarStore(Path.Combine(_folder, "sidecar"));
        var source = MakeTempFile(3);
        store.Put("/what? <a|b>:c/desktop.ini", source);
        Assert.True(store.Exists("/what? <a|b>:c/desktop.ini"));
        Assert.Equal(3, store.Metadata("/what? <a|b>:c/desktop.ini")!.Value.Size);
        using (var stream = store.Open("/what? <a|b>:c/desktop.ini"))
        {
            Assert.Equal(3, stream!.Length);
        }

        store.Delete("/what? <a|b>:c/desktop.ini");
        Assert.Null(store.Open("/what? <a|b>:c/desktop.ini"));
    }

    private WebDavReadCache MakeCache(long maxMB)
        => new(Path.Combine(_folder, "cache-" + Guid.NewGuid().ToString("N")), maxMB * 1024 * 1024);

    private string MakeTempFile(int bytes)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "input-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0xA5, bytes).ToArray());
        return path;
    }
}
