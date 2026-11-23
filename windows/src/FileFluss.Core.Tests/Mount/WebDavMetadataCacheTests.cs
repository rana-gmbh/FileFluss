// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Mount;

namespace FileFluss.Core.Tests.Mount;

/// <summary>
/// A listing that was on its way while a write landed must not be remembered: it shows the
/// folder as it was before the write.
/// </summary>
public sealed class WebDavMetadataCacheTests
{
    private static CloudFileItem File(string name) => new("/f/" + name, name, "/f/" + name, false, 1, DateTimeOffset.UnixEpoch);

    [Fact]
    public void AListingFetchedAcrossAWriteIsNotStored()
    {
        var cache = new WebDavMetadataCache(StringComparer.Ordinal, TimeSpan.FromMinutes(1));

        // The PROPFIND starts, then a PUT of "/f/new.txt" lands and invalidates the folder,
        // then the (old) listing arrives.
        var generation = cache.Generation;
        cache.InvalidateParentOf("/f/new.txt");
        cache.Store("/f", [File("old.txt")], generation);

        Assert.Null(cache.Children("/f"));
        Assert.False(cache.TryGet("/f/new.txt", out _));
    }

    [Fact]
    public void AListingWithNoWriteInBetweenIsStored()
    {
        var cache = new WebDavMetadataCache(StringComparer.Ordinal, TimeSpan.FromMinutes(1));

        cache.Store("/f", [File("old.txt")], cache.Generation);

        Assert.NotNull(cache.Children("/f"));
        Assert.True(cache.TryGet("/f/missing.txt", out var item));
        Assert.Null(item);
    }

    [Fact]
    public void TreeInvalidationAndClearAlsoMakeAPendingListingStale()
    {
        var cache = new WebDavMetadataCache(StringComparer.Ordinal, TimeSpan.FromMinutes(1));

        var beforeTree = cache.Generation;
        cache.InvalidateTree("/f/sub");
        cache.Store("/f", [File("a.txt")], beforeTree);
        Assert.Null(cache.Children("/f"));

        var beforeClear = cache.Generation;
        cache.Clear();
        cache.Store("/f", [File("a.txt")], beforeClear);
        Assert.Null(cache.Children("/f"));
    }
}
