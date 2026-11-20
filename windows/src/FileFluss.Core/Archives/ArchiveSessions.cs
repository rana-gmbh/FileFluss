// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;

namespace FileFluss.Core.Archives;

/// <summary>
/// The archives open in the panels: one <see cref="ArchiveProvider"/> per archive, shared by
/// both panels and by their back/forward history. An archive no panel shows any more gives up
/// its unpacked files and its entry list (<see cref="ArchiveProvider.Release"/>), but stays
/// registered, so "Back" into it simply reads it again.
/// </summary>
public sealed class ArchiveSessions
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<object, string> _shownBy = [];

    private sealed record Session(ArchiveProvider Provider, DateTime Stamp, long Length);

    /// <summary>The key of the archive a location is inside, or null when it isn't inside one.</summary>
    public static string? KeyFor(PanelLocation location)
        => location.IsArchive ? KeyFor(location.ArchiveAccountId, location.ArchivePath!) : null;

    public static string KeyFor(Guid? accountId, string archivePath)
        => accountId is { } id ? $"{id:N}|{archivePath}" : "local|" + archivePath.ToUpperInvariant();

    /// <summary>
    /// The provider for the archive at <paramref name="archivePath"/>, read from
    /// <paramref name="localFile"/>. An archive already open is reused while its file is
    /// unchanged; a changed one (a newer download of a cloud archive) gets a fresh provider.
    /// </summary>
    public ArchiveProvider Open(Guid? accountId, string archivePath, string localFile, ArchiveFormat format, string displayName)
    {
        var key = KeyFor(accountId, archivePath);
        var info = new FileInfo(localFile);
        var stamp = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
        var length = info.Exists ? info.Length : -1;
        ArchiveProvider? replaced = null;
        ArchiveProvider provider;
        lock (_gate)
        {
            if (_sessions.TryGetValue(key, out var existing)
                && string.Equals(existing.Provider.ArchiveFile, localFile, StringComparison.OrdinalIgnoreCase)
                && existing.Stamp == stamp && existing.Length == length)
            {
                return existing.Provider;
            }

            replaced = existing?.Provider;
            provider = new ArchiveProvider(localFile, format, displayName);
            _sessions[key] = new Session(provider, stamp, length);
        }

        replaced?.Release();
        return provider;
    }

    /// <summary>The provider for a location inside an archive opened this session, or null.</summary>
    public ArchiveProvider? Find(PanelLocation location)
    {
        if (KeyFor(location) is not { } key)
        {
            return null;
        }

        lock (_gate)
        {
            return _sessions.TryGetValue(key, out var session) ? session.Provider : null;
        }
    }

    /// <summary>
    /// <paramref name="viewer"/> (a panel) now shows <paramref name="location"/>. An archive it
    /// left that nothing else shows is released.
    /// </summary>
    public void Show(object viewer, PanelLocation location)
    {
        ArchiveProvider? release = null;
        lock (_gate)
        {
            var previous = _shownBy.GetValueOrDefault(viewer);
            var current = KeyFor(location);
            if (current is null)
            {
                _shownBy.Remove(viewer);
            }
            else
            {
                _shownBy[viewer] = current;
            }

            if (previous is not null && previous != current && !_shownBy.ContainsValue(previous) && _sessions.TryGetValue(previous, out var left))
            {
                release = left.Provider;
            }
        }

        release?.Release();
    }

    /// <summary>Every open archive lets go of its unpacked files (FileFluss is quitting).</summary>
    public void ReleaseAll()
    {
        List<ArchiveProvider> all;
        lock (_gate)
        {
            all = _sessions.Values.Select(s => s.Provider).ToList();
        }

        foreach (var provider in all)
        {
            provider.Release();
        }
    }
}
