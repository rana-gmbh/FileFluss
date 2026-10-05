// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using FileFluss.App.Mvvm;
using FileFluss.App.Services;
using FileFluss.Core.Formatting;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Transfers;
using static FileFluss.Core.Localization;

namespace FileFluss.App.ViewModels;

public enum SidebarEntryKind
{
    Favorite,
    Drive,
    Account,
    AddAccount,
}

/// <summary>One row of a sidebar: a favourite, a drive or a cloud account.</summary>
public sealed class SidebarEntry : Observable
{
    private bool _isSelected;
    private bool _isDropTarget;
    private string? _trailing;
    private string? _toolTip;

    public required SidebarEntryKind Kind { get; init; }

    public required string Title { get; set; }

    public PanelLocation? Location { get; init; }

    /// <summary>A Segoe Fluent glyph, used when there is no image.</summary>
    public string? Glyph { get; init; }

    public ImageSource? Image { get; init; }

    public Guid? FavoriteId { get; init; }

    /// <summary>A favourite's stored icon name (<see cref="SidebarFavorite.Icon"/>).</summary>
    public string? Icon { get; init; }

    public CloudAccount? Account { get; init; }

    public string? Trailing
    {
        get => _trailing;
        set => Set(ref _trailing, value);
    }

    /// <summary>The drive registry's "vol:…" id, for drives it tracks (not the system drive).</summary>
    public string? DriveId { get; init; }

    /// <summary>The source id indexing runs are tracked under: a drive's id or an account's.</summary>
    public string? IndexSourceId => DriveId ?? (Account is { } account ? Core.Search.SearchIndex.SourceIdFor(account.Id) : null);

    /// <summary>What <see cref="Trailing"/> shows when no indexing run is going.</summary>
    public string? RestingTrailing { get; init; }

    /// <summary>The hover text: a favourite's name and path, a drive's free space, an account's quota.</summary>
    public string? ToolTip
    {
        get => _toolTip;
        set => Set(ref _toolTip, value);
    }

    /// <summary>Green when connected, grey when not; null hides the dot.</summary>
    public bool? IsConnected { get; init; }

    public bool IsOffline { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => Set(ref _isDropTarget, value);
    }

    public bool AcceptsDrops => Location is not null && Kind != SidebarEntryKind.AddAccount && !(Account?.ProviderType.IsReadOnly() ?? false) && !IsOffline;

    /// <summary>What screen readers announce for the row.</summary>
    public override string ToString() => Title;
}

/// <summary>A folder whose size the user asked for, listed in the sidebar (macOS "Folder Sizes").</summary>
public sealed class FolderSizeEntry : Observable
{
    private long? _size;

    public required string Name { get; init; }

    public required PanelLocation Location { get; init; }

    public long? Size
    {
        get => _size;
        set
        {
            if (Set(ref _size, value))
            {
                OnPropertyChanged(nameof(SizeText));
                OnPropertyChanged(nameof(IsCalculating));
            }
        }
    }

    public bool IsCalculating => _size is null;

    public string SizeText => _size is { } size ? ByteFormatter.Format(size) : L("Calculating…");
}

/// <summary>
/// One panel's sidebar: favourites (per panel, freely ordered), drives, cloud accounts, and
/// the panel's transfers and calculated folder sizes.
/// </summary>
public sealed class SidebarViewModel : Observable
{
    private readonly AppServices _services;
    private readonly PanelViewModel _panel;

    public SidebarViewModel(PanelSide side, PanelViewModel panel, AppServices services)
    {
        Side = side;
        _panel = panel;
        _services = services;
        EnsureFavorites();
        Rebuild();
        services.Accounts.Accounts.CollectionChanged += (_, _) => RebuildAccounts();
        services.Accounts.QuotaChanged += (_, id) => OnQuotaChanged(id);
        services.SettingsStore.Changed += (_, name) =>
        {
            if (name is null || name.Contains("Favorites", StringComparison.Ordinal))
            {
                RebuildFavorites();
            }

            OnPropertyChanged(nameof(ShowTransfers));
            OnPropertyChanged(nameof(ShowFolderSizes));
            OnPropertyChanged(nameof(ShowAddAccount));
            OnPropertyChanged(nameof(IsCompact));
        };
        panel.LocationChanged += (_, _) => UpdateSelection();
        services.Transfers.All.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Transfers));
        services.Search.Drives.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(RebuildDrives);
        services.Search.Indexing.Jobs.CollectionChanged += (_, e) =>
        {
            foreach (var job in e.NewItems?.OfType<Core.Search.IndexingJob>() ?? [])
            {
                job.PropertyChanged += (_, _) => UpdateIndexingBadges();
            }

            UpdateIndexingBadges();
        };
        services.Search.Indexing.IndexingFinished += (_, _) => Rebuild();
    }

    public PanelSide Side { get; }

    private string Key => Side == PanelSide.Left ? "left" : "right";

    public ObservableCollection<SidebarEntry> Favorites { get; } = [];

    public ObservableCollection<SidebarEntry> Drives { get; } = [];

    public ObservableCollection<SidebarEntry> Accounts { get; } = [];

    public ObservableCollection<FolderSizeEntry> FolderSizes { get; } = [];

    public IEnumerable<TransferProgress> Transfers => _services.Transfers.For(Side);

    public bool ShowTransfers => _services.Settings.ShowSidebarTransfers;

    public bool ShowFolderSizes => _services.Settings.ShowSidebarFolderSizes && FolderSizes.Count > 0;

    public bool ShowAddAccount => _services.Settings.ShowSidebarAddAccount;

    /// <summary>Icon-only mode.</summary>
    public bool IsCompact
    {
        get => Side == PanelSide.Left ? _services.Settings.LeftSidebarCompact : _services.Settings.RightSidebarCompact;
        set
        {
            if (Side == PanelSide.Left)
            {
                _services.Settings.LeftSidebarCompact = value;
            }
            else
            {
                _services.Settings.RightSidebarCompact = value;
            }

            OnPropertyChanged();
        }
    }

    public bool IsSectionExpanded(string section) => _services.Settings.IsSectionExpanded($"{Key}.{section}");

    public void SetSectionExpanded(string section, bool expanded) => _services.Settings.SetSectionExpanded($"{Key}.{section}", expanded);

    // MARK: - Favourites

    private List<SidebarFavorite> StoredFavorites
    {
        get => (Side == PanelSide.Left ? _services.Settings.LeftFavorites : _services.Settings.RightFavorites) ?? [];
        set
        {
            if (Side == PanelSide.Left)
            {
                _services.Settings.LeftFavorites = value;
            }
            else
            {
                _services.Settings.RightFavorites = value;
            }
        }
    }

    /// <summary>First run: both panels get the Windows known folders, with independent ids.</summary>
    private void EnsureFavorites()
    {
        if ((Side == PanelSide.Left ? _services.Settings.LeftFavorites : _services.Settings.RightFavorites) is null)
        {
            StoredFavorites = SidebarFavorite.DefaultLocalFavorites();
        }
    }

    public void AddFavorite(PanelLocation location, string name, int? index = null) => AddFavorites([(location, name)], index);

    /// <summary>
    /// Adds folders as favourites, in order, at the gap <paramref name="index"/> (the end when
    /// null) — macOS inserts a dropped folder where the insertion line was. Folders that are
    /// already favourites are skipped.
    /// </summary>
    public void AddFavorites(IEnumerable<(PanelLocation Location, string Name)> folders, int? index = null)
    {
        var list = StoredFavorites.ToList();
        var at = index is { } i ? Math.Clamp(i, 0, list.Count) : list.Count;
        var added = false;
        foreach (var (location, name) in folders)
        {
            if (list.Any(f => f.Location == location))
            {
                continue;
            }

            var entry = location.IsCloud && _services.Accounts.Find(location.AccountId!.Value) is { } account
                ? SidebarFavorite.ForCloud($"{name} ({account.ProviderType.DisplayName()})", account.ProviderType, account.Id, location.Path)
                : SidebarFavorite.ForLocal(name, "folder", location.Path);
            list.Insert(at++, entry);
            added = true;
        }

        if (added)
        {
            StoredFavorites = list;
        }
    }

    public void RemoveFavorite(Guid id) => StoredFavorites = StoredFavorites.Where(f => f.Id != id).ToList();

    public void RenameFavorite(Guid id, string name)
        => StoredFavorites = StoredFavorites.Select(f => f.Id == id ? f with { DisplayName = name } : f).ToList();

    /// <summary>A glyph name <see cref="Glyphs.ForFavorite"/> knows, or "@logo:&lt;asset&gt;" (macOS "Change Icon").</summary>
    public void SetFavoriteIcon(Guid id, string icon)
        => StoredFavorites = StoredFavorites.Select(f => f.Id == id ? f with { Icon = icon } : f).ToList();

    /// <summary>Moves a favourite to the gap <paramref name="insertionIndex"/> of the list as it is now.</summary>
    public void MoveFavorite(Guid id, int insertionIndex)
    {
        var current = StoredFavorites;
        var moved = Reordering.Move(current, f => f.Id == id, insertionIndex);
        if (!moved.SequenceEqual(current))
        {
            StoredFavorites = moved;
        }
    }

    /// <summary>Accounts are one list for both sidebars, so moving one reorders both (macOS too).</summary>
    public void MoveAccount(Guid id, int insertionIndex) => _services.Accounts.Move(id, insertionIndex);

    // MARK: - Building

    public void Rebuild()
    {
        RebuildFavorites();
        RebuildDrives();
        RebuildAccounts();
    }

    private void RebuildFavorites()
    {
        Favorites.Clear();
        foreach (var favorite in StoredFavorites)
        {
            var title = favorite.IsCloud ? favorite.DisplayName : L(favorite.DisplayName);
            var logo = Glyphs.LogoAsset(favorite.Icon) is { } asset ? IconCache.Logo(asset) : null;
            Favorites.Add(new SidebarEntry
            {
                Kind = SidebarEntryKind.Favorite,
                Title = title,
                Location = favorite.Location,
                FavoriteId = favorite.Id,
                Icon = favorite.Icon,

                // The logo when there is one, the glyph otherwise — never both stacked.
                Glyph = logo is null ? Glyphs.ForFavorite(favorite.Icon) : null,
                Image = logo,

                // The name too: in icon-only mode the tooltip is all there is (macOS .help(name)).
                ToolTip = $"{title}{Environment.NewLine}{favorite.Location.Path}",
            });
        }

        UpdateSelection();
    }

    public void RebuildDrives()
    {
        Drives.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try
            {
                ready = drive.IsReady;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ready = false;
            }

            if (!ready)
            {
                continue;
            }

            var root = drive.RootDirectory.FullName;
            string title;
            try
            {
                title = LocalFileProvider.DriveLabel(root);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A drive that went away between the listing and now.
                title = root;
            }

            var tooltip = title;
            try
            {
                // macOS driveTooltip: "Name: 120 GB free of 500 GB".
                tooltip = $"{title}: {Core.Storage.LocalVolumeFormatter.Summary(drive.AvailableFreeSpace, drive.TotalSize)}";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            Drives.Add(new SidebarEntry
            {
                Kind = SidebarEntryKind.Drive,
                Title = title,
                Location = PanelLocation.Local(root),
                Image = IconCache.ForPath(root, true),
                ToolTip = tooltip,
                DriveId = _services.Search.DriveAt(root)?.Id,
            });
        }

        // Unplugged drives with an index stay listed, browsable and searchable offline (macOS DriveRow).
        foreach (var drive in _services.Search.Drives.OfflineIndexedDrives)
        {
            Drives.Add(new SidebarEntry
            {
                Kind = SidebarEntryKind.Drive,
                Title = drive.DisplayName,
                Location = PanelLocation.OfflineDrive(drive.Id, "/"),
                Glyph = drive.Kind switch
                {
                    Core.Search.DriveKind.Network => Glyphs.NetworkDrive,
                    Core.Search.DriveKind.External => Glyphs.Usb,
                    _ => Glyphs.HardDrive,
                },
                DriveId = drive.Id,
                IsOffline = true,
                RestingTrailing = L("Offline"),
                Trailing = L("Offline"),
                ToolTip = drive.LastIndexed is { } when
                    ? Core.Search.IndexingText.IndexedSummary(when, drive.TotalFiles, DateTimeOffset.Now)
                    : drive.DisplayName,
            });
        }

        UpdateIndexingBadges();
        UpdateSelection();
    }

    private void RebuildAccounts()
    {
        Accounts.Clear();
        foreach (var account in _services.Accounts.Accounts)
        {
            Accounts.Add(new SidebarEntry
            {
                Kind = SidebarEntryKind.Account,
                Title = account.DisplayName,
                Location = PanelLocation.Cloud(account.Id, account.EffectiveRootPath),
                Account = account,
                Image = IconCache.Logo(account.ProviderType),
                Glyph = Glyphs.Cloud,
                IsConnected = account.IsConnected && !account.IsOfflineMode,
                IsOffline = account.IsOfflineMode,
                RestingTrailing = account.IsOfflineMode ? L("Offline") : null,
                Trailing = account.IsOfflineMode ? L("Offline") : null,
                ToolTip = AccountToolTip(account),
            });
        }

        UpdateIndexingBadges();
        UpdateSelection();
        RefreshQuotas();
    }

    // MARK: - Quota tooltips (macOS cloudAccountTooltip)

    /// <summary>"Dropbox: 12.0 GB of 2.00 TB used (1%)" once a quota is known, the name until then.</summary>
    private string AccountToolTip(CloudAccount account)
        => _services.Accounts.CachedQuota(account.Id) is { } quota ? CloudQuotaFormatter.Summary(quota, account.DisplayName) : account.DisplayName;

    /// <summary>
    /// Asks for every account's quota; fresh figures arrive through
    /// <see cref="AccountsService.QuotaChanged"/>. The service's cache (two minutes) makes
    /// rebuilds free, and both sidebars asking at once share one request.
    /// </summary>
    private void RefreshQuotas()
    {
        foreach (var account in _services.Accounts.Accounts)
        {
            if (account.IsConnected && !account.IsOfflineMode)
            {
                _ = _services.Accounts.QuotaAsync(account.Id);
            }
        }
    }

    private void OnQuotaChanged(Guid id)
    {
        UpdateQuotaToolTips();

        // A transfer or delete made the figure stale: fetch it again so the tooltip follows.
        if (_services.Accounts.Find(id) is { IsConnected: true, IsOfflineMode: false })
        {
            _ = _services.Accounts.QuotaAsync(id);
        }
    }

    private void UpdateQuotaToolTips()
    {
        foreach (var entry in Accounts)
        {
            if (entry.Account is { } account && _services.Accounts.Find(account.Id) is { } current)
            {
                entry.ToolTip = AccountToolTip(current);
            }
        }
    }

    /// <summary>A running index shows its file count where "Offline" would be (macOS DriveRow).</summary>
    public void UpdateIndexingBadges()
    {
        foreach (var entry in Drives.Concat(Accounts))
        {
            entry.Trailing = entry.IndexSourceId is { } source && _services.Search.Indexing.ActiveJob(source) is { } job
                ? job.FilesProcessed.ToString("N0", Culture)
                : entry.RestingTrailing;
        }
    }

    /// <summary>Highlights the entry that best matches where the panel is.</summary>
    public void UpdateSelection()
    {
        var location = _panel.Location;
        SidebarEntry? best = null;
        var bestLength = -1;
        foreach (var entry in Favorites.Concat(Drives).Concat(Accounts))
        {
            entry.IsSelected = false;
            if (entry.Location is not { } target || target.AccountId != location.AccountId || target.IsOfflineDrive != location.IsOfflineDrive)
            {
                continue;
            }

            var matches = location.IsOfflineDrive ? target.DriveId == location.DriveId
                : location.IsCloud
                ? (entry.Kind == SidebarEntryKind.Account || string.Equals(Core.IO.CloudPath.Normalize(target.Path), Core.IO.CloudPath.Normalize(location.Path), StringComparison.Ordinal))
                : IsSameOrParentLocal(target.Path, location.Path);
            var length = entry.Kind == SidebarEntryKind.Account ? 0 : target.Path.Length;
            if (matches && length > bestLength)
            {
                best = entry;
                bestLength = length;
            }
        }

        if (best is not null)
        {
            best.IsSelected = true;
        }
    }

    private static bool IsSameOrParentLocal(string candidate, string path)
    {
        if (candidate.Length == 0)
        {
            return false;
        }

        var a = Path.TrimEndingDirectorySeparator(candidate);
        var b = Path.TrimEndingDirectorySeparator(path);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
               || (a.Length > 2 && b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && candidate.Length > 3);
    }

    // MARK: - Folder sizes

    public void CalculateFolderSize(FileProvider provider, CloudFileItem folder, PanelLocation location, Action<long>? onDone = null)
    {
        if (FolderSizes.Any(f => f.Location == location))
        {
            return;
        }

        var entry = new FolderSizeEntry { Name = folder.Name, Location = location };
        FolderSizes.Insert(0, entry);
        OnPropertyChanged(nameof(ShowFolderSizes));
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _ = Task.Run(async () =>
        {
            long size;
            try
            {
                size = await provider.FolderSizeAsync(folder.Path);
            }
            catch (Exception)
            {
                size = 0;
            }

            // Queued, not waited for: a walk ending while FileFluss closes must not block on
            // (or deadlock with) a UI thread that is shutting down.
            if (!dispatcher.HasShutdownStarted)
            {
                _ = dispatcher.BeginInvoke(() =>
                {
                    entry.Size = size;
                    onDone?.Invoke(size);
                });
            }
        });
    }

    public void RemoveFolderSize(FolderSizeEntry entry)
    {
        FolderSizes.Remove(entry);
        OnPropertyChanged(nameof(ShowFolderSizes));
    }

    public void NotifyLanguageChanged()
    {
        Rebuild();
        OnAllPropertiesChanged();
    }
}
