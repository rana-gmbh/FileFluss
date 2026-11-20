// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Security.Cryptography;
using System.Text;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;

namespace FileFluss.Core.Mount;

/// <summary>
/// LRU disk cache of downloaded file bytes for one mount (macOS <c>WebDAVReadCache</c>).
///
/// <para>The mount's <see cref="WebDavServer"/> checks it on every GET: when the cached
/// version of a provider path matches the version the provider reports now (modification
/// date + size), the cached file streams back to Explorer and the cloud is never asked. On a
/// miss the server downloads to a temp file and hands it to <see cref="Store"/>, which moves
/// it in and registers it.</para>
///
/// <para>On-disk names are the SHA-256 of <c>path\0version</c>: filesystem-safe whatever the
/// cloud name contains (":" or "?" are fine in a cloud, not on NTFS), and a second version of
/// the same file can sit next to the old one until eviction reclaims it.</para>
///
/// <para>Windows-specific: every stream this cache opens shares <see cref="FileShare.Delete"/>,
/// so evicting or invalidating an entry that Explorer is still reading never fails — the file
/// is unlinked and the reader finishes from the open handle, as it would on macOS.</para>
/// </summary>
public sealed class WebDavReadCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _totalBytes;
    private long _maxBytes;
    private long _clock;

    public WebDavReadCache(string root, long maxBytes)
    {
        Root = root;
        _maxBytes = Math.Max(0, maxBytes);
        Directory.CreateDirectory(root);

        // No persisted index: (path, version) pairs aren't stored anywhere, so files left by
        // a previous run can never be found again. Drop them; the index rebuilds as GETs land.
        ClearOnDisk();
    }

    private sealed class Entry(string key, string providerPath, string version, long size)
    {
        public string Key { get; } = key;

        public string ProviderPath { get; } = providerPath;

        public string Version { get; } = version;

        public long Size { get; } = size;

        public long LastAccess { get; set; }
    }

    public string Root { get; }

    public long MaxBytes
    {
        get
        {
            lock (_gate)
            {
                return _maxBytes;
            }
        }
    }

    public long TotalBytes
    {
        get
        {
            lock (_gate)
            {
                return _totalBytes;
            }
        }
    }

    /// <summary>Settings → Storage changed the budget; shrinking evicts right away.</summary>
    public void SetMaxBytes(long maxBytes)
    {
        lock (_gate)
        {
            _maxBytes = Math.Max(0, maxBytes);
            EvictLocked(keep: null);
        }
    }

    /// <summary>
    /// The cached file for a (path, version) hit, or null. Marks the entry as recently used.
    /// Prefer <see cref="TryOpen"/> for serving: a path can be evicted before it is opened.
    /// </summary>
    public string? Lookup(string providerPath, string version)
    {
        lock (_gate)
        {
            var key = Key(providerPath, version);
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            entry.LastAccess = ++_clock;
            return FilePath(key);
        }
    }

    /// <summary>
    /// Opens the cached file for a (path, version) hit — under the cache lock, so a concurrent
    /// eviction can't delete it between lookup and open. Null on a miss.
    /// </summary>
    public FileStream? TryOpen(string providerPath, string version)
    {
        lock (_gate)
        {
            var key = Key(providerPath, version);
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            try
            {
                var stream = OpenShared(FilePath(key));
                entry.LastAccess = ++_clock;
                return stream;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Someone removed the file behind our back (Settings → Clear Cache).
                RemoveLocked(key);
                return null;
            }
        }
    }

    /// <summary>
    /// Moves the freshly downloaded <paramref name="tempFile"/> into the cache under
    /// (path, version) and evicts least-recently-used entries until the budget fits again.
    /// Returns false — leaving <paramref name="tempFile"/> where it is, for the caller to
    /// serve and delete — when the cache is off, the file alone exceeds the budget, or the
    /// move fails. (macOS stored an oversize file and then evicted it on the spot.)
    /// </summary>
    public bool Store(string providerPath, string version, string tempFile)
    {
        long size;
        try
        {
            size = new FileInfo(tempFile).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        lock (_gate)
        {
            if (_maxBytes <= 0 || size > _maxBytes)
            {
                return false;
            }

            var key = Key(providerPath, version);
            var target = FilePath(key);
            try
            {
                File.Move(tempFile, target, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SupportLogger.Shared.Log($"Read cache: move into cache failed: {e.Message}", WebDavServer.LogCategory);
                return false;
            }

            if (_entries.Remove(key, out var previous))
            {
                _totalBytes -= previous.Size;
            }

            _entries[key] = new Entry(key, providerPath, version, size) { LastAccess = ++_clock };
            _totalBytes += size;
            EvictLocked(keep: key);
            return true;
        }
    }

    /// <summary>
    /// Drops every cached version of <paramref name="providerPath"/>. The server calls this
    /// when a PUT, MOVE, COPY, DELETE or PROPPATCH touches the path, so the next GET fetches
    /// the new bytes.
    /// </summary>
    public void Invalidate(string providerPath)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Values.Where(e => e.ProviderPath == providerPath).Select(e => e.Key).ToList())
            {
                RemoveLocked(key);
            }
        }
    }

    /// <summary>
    /// Drops <paramref name="providerPath"/> and everything cached beneath it — a folder that
    /// was moved or deleted takes its files' cache entries with it. (macOS invalidated the
    /// exact path only, which left a moved folder's children cached under their old paths.)
    /// </summary>
    public void InvalidateTree(string providerPath)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Values.Where(e => CloudPath.IsWithin(e.ProviderPath, providerPath)).Select(e => e.Key).ToList())
            {
                RemoveLocked(key);
            }
        }
    }

    /// <summary>Wipes the whole cache. Called when the mount is torn down for good.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _totalBytes = 0;
            ClearOnDisk();
        }
    }

    /// <summary>A read stream that never blocks eviction, renames or deletes of the file.</summary>
    internal static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private void EvictLocked(string? keep)
    {
        if (_totalBytes <= _maxBytes)
        {
            return;
        }

        foreach (var entry in _entries.Values.OrderBy(e => e.LastAccess).ToList())
        {
            if (_totalBytes <= _maxBytes)
            {
                break;
            }

            if (entry.Key != keep)
            {
                RemoveLocked(entry.Key);
            }
        }
    }

    private void RemoveLocked(string key)
    {
        if (!_entries.Remove(key, out var entry))
        {
            return;
        }

        _totalBytes -= entry.Size;
        TryDelete(FilePath(key));
    }

    private void ClearOnDisk()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root))
            {
                TryDelete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string FilePath(string key) => Path.Combine(Root, key);

    private static string Key(string providerPath, string version)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(providerPath + "\0" + version)));
}
