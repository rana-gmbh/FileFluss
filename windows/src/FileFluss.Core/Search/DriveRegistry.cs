// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Search;

/// <summary>
/// The drives the sidebar's Drives section lists — the model half of macOS <c>DriveMonitor</c>.
/// Combines two sources of truth:
/// <list type="bullet">
/// <item><b>Live state</b>: the volumes mounted now, which the app reports through
/// <see cref="UpdateMounted"/> at launch and on every device change (Core does no Win32).</item>
/// <item><b>Persisted state</b>: drives seen before, kept in the index database, so an
/// indexed drive that is unplugged still appears — offline — and stays browsable and
/// searchable from its index.</item>
/// </list>
/// Thread-safe; <see cref="Changed"/> is raised on the thread that made the change.
/// </summary>
public sealed class DriveRegistry
{
    private readonly SearchIndex _index;
    private readonly string? _systemRoot;
    private readonly Lock _gate = new();
    private readonly List<KnownDrive> _drives;

    /// <summary>Drive id → its current root, for mounted drives. Absent ⇒ offline.</summary>
    private Dictionary<string, string> _liveRoots = new(StringComparer.Ordinal);

    /// <param name="systemRoot">The Windows volume, excluded from the list; defaults to the running system's.</param>
    public DriveRegistry(SearchIndex index, string? systemRoot = null)
    {
        _index = index;
        _systemRoot = systemRoot;
        _drives = index.LoadKnownDrives().ToList();

        // On a fresh launch nothing is mounted yet as far as we know: drop every entry that
        // was never indexed, so yesterday's one-off stick doesn't linger in today's sidebar.
        if (PruneUnindexedOffline())
        {
            Save();
        }
    }

    public event EventHandler? Changed;

    /// <summary>Every known drive — mounted ones and indexed offline ones — with <see cref="KnownDrive.IsOnline"/> filled in.</summary>
    public IReadOnlyList<KnownDrive> Drives
    {
        get
        {
            lock (_gate)
            {
                return _drives.Select(WithLiveState).ToList();
            }
        }
    }

    public IReadOnlyList<KnownDrive> OnlineDrives => Drives.Where(d => d.IsOnline).ToList();

    /// <summary>Unplugged drives that have an index — what offline search and offline browsing cover.</summary>
    public IReadOnlyList<KnownDrive> OfflineIndexedDrives => Drives.Where(d => !d.IsOnline && d.IsIndexed).ToList();

    public KnownDrive? Find(string driveId)
    {
        lock (_gate)
        {
            var drive = _drives.FirstOrDefault(d => d.Id == driveId);
            return drive is null ? null : WithLiveState(drive);
        }
    }

    public bool IsOnline(string driveId)
    {
        lock (_gate)
        {
            return _liveRoots.ContainsKey(driveId);
        }
    }

    /// <summary>The drive's current root (<c>E:\</c>) while it is mounted; null when offline.</summary>
    public string? RootPathFor(string driveId)
    {
        lock (_gate)
        {
            return _liveRoots.GetValueOrDefault(driveId);
        }
    }

    /// <summary>
    /// The full set of mounted volumes, as the app sees them now. Classifies each, merges new
    /// ones into the list (keeping what is known about returning ones), and drops never-indexed
    /// drives that are gone.
    /// </summary>
    public void UpdateMounted(IEnumerable<DriveIdentity> mounted)
    {
        lock (_gate)
        {
            var live = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var identity in mounted)
            {
                if (DriveClassifier.Classify(identity, _systemRoot) is not { } kind)
                {
                    continue;
                }

                var id = identity.SourceId;
                live[id] = identity.RootPath;
                var index = _drives.FindIndex(d => d.Id == id);
                if (index >= 0)
                {
                    _drives[index] = _drives[index] with { Label = identity.Label, Kind = kind, LastRootPath = identity.RootPath };
                }
                else
                {
                    _drives.Add(new KnownDrive { Id = id, VolumeId = identity.Id, Label = identity.Label, Kind = kind, LastRootPath = identity.RootPath });
                }
            }

            _liveRoots = live;

            // Every transition can leave behind a never-indexed entry whose volume just went.
            PruneUnindexedOffline();
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Inserts or replaces a drive's persisted record.</summary>
    public void Upsert(KnownDrive drive)
    {
        lock (_gate)
        {
            var stored = drive with { IsOnline = false, RootPath = null };
            var index = _drives.FindIndex(d => d.Id == drive.Id);
            if (index >= 0)
            {
                _drives[index] = stored;
            }
            else
            {
                _drives.Add(stored);
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records a completed index run, with the count the index actually holds.</summary>
    public void RecordIndexed(string driveId, DateTimeOffset when, int totalFiles, long totalBytes)
    {
        lock (_gate)
        {
            var index = _drives.FindIndex(d => d.Id == driveId);
            if (index < 0)
            {
                return;
            }

            _drives[index] = _drives[index] with { LastIndexed = when, TotalFiles = totalFiles, TotalBytes = totalBytes };
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops tracking a drive entirely ("Forget Drive"). The caller clears its index if it had one.</summary>
    public void Forget(string driveId)
    {
        lock (_gate)
        {
            if (_drives.RemoveAll(d => d.Id == driveId) == 0)
            {
                return;
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// "Remove Offline Index": drops the drive's index; a mounted drive stays listed as
    /// not indexed, an unplugged one is forgotten.
    /// </summary>
    public void RemoveOfflineIndex(string driveId)
    {
        _index.DropSource(driveId);
        lock (_gate)
        {
            var index = _drives.FindIndex(d => d.Id == driveId);
            if (index < 0)
            {
                return;
            }

            if (_liveRoots.ContainsKey(driveId))
            {
                _drives[index] = _drives[index] with { LastIndexed = null, TotalFiles = 0, TotalBytes = 0 };
            }
            else
            {
                _drives.RemoveAt(index);
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// After "Erase All Indexes": no drive claims an index any more, and unplugged ones —
    /// now with nothing to show — leave the list.
    /// </summary>
    public void ClearIndexedState()
    {
        lock (_gate)
        {
            for (var i = 0; i < _drives.Count; i++)
            {
                _drives[i] = _drives[i] with { LastIndexed = null, TotalFiles = 0, TotalBytes = 0 };
            }

            PruneUnindexedOffline();
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops every drive that is neither mounted nor indexed. Indexed offline drives stay: their index is still searchable.</summary>
    private bool PruneUnindexedOffline()
        => _drives.RemoveAll(d => d.LastIndexed is null && !_liveRoots.ContainsKey(d.Id)) > 0;

    private KnownDrive WithLiveState(KnownDrive drive)
        => _liveRoots.TryGetValue(drive.Id, out var root)
            ? drive with { IsOnline = true, RootPath = root }
            : drive with { IsOnline = false, RootPath = null };

    private void Save() => _index.SaveKnownDrives(_drives);
}
