// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Models;

namespace FileFluss.Core.Mount;

/// <summary>
/// Folder listings the server fetched in the last few seconds, so the burst of requests
/// Explorer fires around every folder view doesn't each become a cloud API call: after the
/// Depth-1 PROPFIND of a folder it asks Depth-0 about the folder again, about every file it
/// opens or shows a thumbnail for, and about names that usually don't exist
/// (<c>desktop.ini</c>, <c>autorun.inf</c>, <c>folder.jpg</c>). macOS hit the same storm
/// with Finder ("hundreds of PROPFINDs … stall Finder until it gives up with -43") and
/// answered it with the sidecar store; this cache answers the rest, including fast 404s for
/// names absent from a fresh listing.
///
/// <para>Every write through the server invalidates the folders it touched before it
/// answers, so the mount never shows its own changes late. Changes made elsewhere (in the
/// cloud's web UI) appear after <see cref="Lifetime"/>.</para>
/// </summary>
internal sealed class WebDavMetadataCache(StringComparer nameComparer, TimeSpan lifetime)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Listing> _listings = new(StringComparer.Ordinal);

    private sealed record Listing(Dictionary<string, CloudFileItem> Children, DateTimeOffset Fetched);

    public TimeSpan Lifetime { get; } = lifetime;

    public void Store(string folder, IEnumerable<CloudFileItem> children)
    {
        var map = new Dictionary<string, CloudFileItem>(nameComparer);
        foreach (var child in children)
        {
            map.TryAdd(child.Name, child);
        }

        lock (_gate)
        {
            _listings[folder] = new Listing(map, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>A fresh listing of <paramref name="folder"/>, or null.</summary>
    public IReadOnlyCollection<CloudFileItem>? Children(string folder)
    {
        lock (_gate)
        {
            return Fresh(folder) is { } listing ? listing.Children.Values.ToList() : null;
        }
    }

    /// <summary>
    /// True when a fresh listing of the parent answers the question: <paramref name="item"/>
    /// is then the entry, or null when the name is known to be absent.
    /// </summary>
    public bool TryGet(string path, out CloudFileItem? item)
    {
        item = null;
        if (CloudPath.IsRoot(path))
        {
            return false;
        }

        lock (_gate)
        {
            if (Fresh(CloudPath.Parent(path)) is not { } listing)
            {
                return false;
            }

            item = listing.Children.GetValueOrDefault(CloudPath.Name(path));
            return true;
        }
    }

    /// <summary>Forgets the folder that contains <paramref name="path"/>.</summary>
    public void InvalidateParentOf(string path)
    {
        lock (_gate)
        {
            _listings.Remove(CloudPath.Parent(path));
        }
    }

    /// <summary>Forgets <paramref name="path"/>'s own listing, everything beneath it and its parent's.</summary>
    public void InvalidateTree(string path)
    {
        lock (_gate)
        {
            _listings.Remove(CloudPath.Parent(path));
            foreach (var key in _listings.Keys.Where(k => CloudPath.IsWithin(k, path)).ToList())
            {
                _listings.Remove(key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _listings.Clear();
        }
    }

    private Listing? Fresh(string folder)
    {
        if (!_listings.TryGetValue(folder, out var listing))
        {
            return null;
        }

        if (DateTimeOffset.UtcNow - listing.Fetched > Lifetime)
        {
            _listings.Remove(folder);
            return null;
        }

        return listing;
    }
}
