// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Search;
using FileFluss.Core.Services;
using FileFluss.Native;

namespace FileFluss.App.Services;

/// <summary>
/// The search index and what hangs off it: known drives, indexing runs and the search
/// coordinator (macOS <c>SearchIndex.shared</c>, <c>DriveMonitor</c>, <c>IndexingService</c>).
/// Created on the UI thread — the indexing service reports its progress there.
/// </summary>
public sealed class SearchServices
{
    private readonly ProviderHub _hub;

    public SearchServices(string databasePath, ProviderHub hub)
    {
        _hub = hub;
        var index = new SearchIndex(databasePath);
        DriveRegistry drives;
        try
        {
            drives = new DriveRegistry(index);
        }
        catch (SearchIndexException e)
        {
            // A broken index must not keep the app from starting: this session works on a
            // throwaway index, and the real one is retried at the next launch.
            SupportLogger.Shared.Log($"Search index unavailable: {e.Message}", "Indexing", LogLevel.Error);
            index = new SearchIndex(Path.Combine(Path.GetTempPath(), $"FileFluss-index-{Environment.ProcessId}.sqlite"));
            drives = new DriveRegistry(index);
        }

        Index = index;
        Drives = drives;
        Indexing = new IndexingService(Index, hub, Drives);
        Coordinator = new SearchCoordinator(Index, hub);
    }

    public SearchIndex Index { get; }

    public DriveRegistry Drives { get; }

    public IndexingService Indexing { get; }

    public SearchCoordinator Coordinator { get; }

    /// <summary>False when the database could not be opened at launch.</summary>
    public bool IsAvailable { get; private set; } = true;

    /// <summary>How long launch waits for the drive survey before restoring panels without it.</summary>
    private static readonly TimeSpan StartupDriveWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Launch: open the index, drop accounts that no longer exist, learn which drives are
    /// plugged in. The drive survey asks every volume for its serial, and a mapped network
    /// drive whose server is gone can take tens of seconds to answer — so launch waits for it
    /// only briefly; it finishes in the background and the sidebars follow.
    /// </summary>
    public async Task StartAsync(IReadOnlySet<Guid> accountIds)
    {
        await Task.Run(() =>
        {
            try
            {
                Index.Open();
                Index.PurgeOrphanCloudSources(accountIds);
            }
            catch (SearchIndexException e)
            {
                IsAvailable = false;
                SupportLogger.Shared.Log($"Opening the search index failed: {e.Message}", "Indexing", LogLevel.Error);
            }
        });

        var drives = RefreshDrivesAsync();
        if (await Task.WhenAny(drives, Task.Delay(StartupDriveWait)) != drives)
        {
            SupportLogger.Shared.Log("The drive survey is slow (an unreachable network drive?); continuing without waiting", "Indexing", LogLevel.Notice);
            drives.Forget("drive survey");
            return;
        }

        await drives;
    }

    /// <summary>A drive came or went (or the user asked for a refresh).</summary>
    public async Task RefreshDrivesAsync()
    {
        // Volume serials of network drives can take a moment, and recording them writes to the
        // index (behind any indexing run's write): both stay off the UI thread. The registry's
        // Changed event is raised there; its listeners hop to the UI thread themselves.
        await Task.Run(() =>
        {
            var mounted = VolumeIdentity.Mounted();
            try
            {
                Drives.UpdateMounted(mounted);
            }
            catch (SearchIndexException e)
            {
                SupportLogger.Shared.Log($"Recording drives failed: {e.Message}", "Indexing", LogLevel.Error);
            }
        });
    }

    /// <summary>The known drive mounted at <paramref name="root"/> (<c>E:\</c>), if the registry tracks it.</summary>
    public KnownDrive? DriveAt(string root)
        => Drives.OnlineDrives.FirstOrDefault(d => d.RootPath is { } r && string.Equals(r.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    /// <summary>The known drive holding a local path, if any (the system drive is never tracked).</summary>
    public KnownDrive? DriveContaining(string path)
        => string.IsNullOrEmpty(path) ? null : DriveAt(Path.GetPathRoot(path) ?? string.Empty);

    /// <summary>Browsing a cloud folder teaches the index what is in it (macOS: cache on listing).</summary>
    public void LearnListing(Guid accountId, string path, IReadOnlyList<CloudFileItem> items)
    {
        if (!IsAvailable)
        {
            return;
        }

        var snapshot = items.ToList();
        _ = Task.Run(() =>
        {
            try
            {
                Index.ApplyListingLogging(accountId, path, snapshot);
            }
            catch (SearchIndexException e)
            {
                SupportLogger.Shared.Log($"Caching a listing failed: {e.Message}", "Indexing", LogLevel.Notice);
            }
        });
    }

    /// <summary>The account's last full index, for the sidebar menu and "Go Offline".</summary>
    public IndexedSource? CloudSource(Guid accountId) => Safe(() => Index.GetSource(SearchIndex.SourceIdFor(accountId)));

    public T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (SearchIndexException e)
        {
            SupportLogger.Shared.Log($"Reading the search index failed: {e.Message}", "Indexing", LogLevel.Notice);
            return default;
        }
    }

    public void Safe(Action write) => Safe<object?>(() =>
    {
        write();
        return null;
    });

    /// <summary>The provider for an offline account or unplugged drive; null when there is nothing to show.</summary>
    public OfflineIndexProvider? OfflineProvider(PanelLocation location, CloudAccount? account)
    {
        if (location.IsOfflineDrive)
        {
            return Drives.Find(location.DriveId!) is { } drive
                ? OfflineIndexProvider.ForDrive(Index, drive)
                : OfflineIndexProvider.ForDrive(Index, location.DriveId!, Core.Localization.L("Offline Source"));
        }

        if (location.IsCloud && account is { IsOfflineMode: true })
        {
            return OfflineIndexProvider.ForCloudAccount(Index, account);
        }

        return null;
    }

    public bool IsConnected(Guid accountId) => _hub.Get(accountId) is not null;
}
