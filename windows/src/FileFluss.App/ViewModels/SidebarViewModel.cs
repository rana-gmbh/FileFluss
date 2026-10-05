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

    public required SidebarEntryKind Kind { get; init; }

    public required string Title { get; set; }

    public PanelLocation? Location { get; init; }

    /// <summary>A Segoe Fluent glyph, used when there is no image.</summary>
    public string? Glyph { get; init; }

    public ImageSource? Image { get; init; }

    public Guid? FavoriteId { get; init; }

    public CloudAccount? Account { get; init; }

    public string? Trailing { get; init; }

    public string? ToolTip { get; set; }

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

    public void AddFavorite(PanelLocation location, string name, int? index = null)
    {
        var list = StoredFavorites.ToList();
        if (list.Any(f => f.Location == location))
        {
            return;
        }

        SidebarFavorite entry;
        if (location.IsCloud && _services.Accounts.Find(location.AccountId!.Value) is { } account)
        {
            entry = SidebarFavorite.ForCloud($"{name} ({account.ProviderType.DisplayName()})", account.ProviderType, account.Id, location.Path);
        }
        else
        {
            entry = SidebarFavorite.ForLocal(name, "folder", location.Path);
        }

        if (index is { } i && i >= 0 && i <= list.Count)
        {
            list.Insert(i, entry);
        }
        else
        {
            list.Add(entry);
        }

        StoredFavorites = list;
    }

    public void RemoveFavorite(Guid id) => StoredFavorites = StoredFavorites.Where(f => f.Id != id).ToList();

    public void RenameFavorite(Guid id, string name)
        => StoredFavorites = StoredFavorites.Select(f => f.Id == id ? f with { DisplayName = name } : f).ToList();

    public void MoveFavorite(Guid id, int newIndex)
    {
        var list = StoredFavorites.ToList();
        var index = list.FindIndex(f => f.Id == id);
        if (index < 0)
        {
            return;
        }

        var item = list[index];
        list.RemoveAt(index);
        list.Insert(Math.Clamp(newIndex > index ? newIndex - 1 : newIndex, 0, list.Count), item);
        StoredFavorites = list;
    }

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
            Favorites.Add(new SidebarEntry
            {
                Kind = SidebarEntryKind.Favorite,
                Title = favorite.IsCloud ? favorite.DisplayName : L(favorite.DisplayName),
                Location = favorite.Location,
                FavoriteId = favorite.Id,
                Glyph = Glyphs.ForFavorite(favorite.Icon),
                Image = favorite.Icon.StartsWith("@logo:", StringComparison.Ordinal) ? IconCache.Logo(favorite.Icon[6..]) : null,
                ToolTip = favorite.IsCloud ? favorite.Location.Path : favorite.Location.Path,
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
            catch (IOException)
            {
                ready = false;
            }

            if (!ready)
            {
                continue;
            }

            var root = drive.RootDirectory.FullName;
            string? tooltip = null;
            try
            {
                tooltip = L("{0} free of {1}", ByteFormatter.Format(drive.AvailableFreeSpace), ByteFormatter.Format(drive.TotalSize));
            }
            catch (IOException)
            {
            }

            Drives.Add(new SidebarEntry
            {
                Kind = SidebarEntryKind.Drive,
                Title = LocalFileProvider.DriveLabel(root),
                Location = PanelLocation.Local(root),
                Image = IconCache.ForPath(root, true),
                ToolTip = tooltip,
            });
        }

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
                Trailing = account.IsOfflineMode ? L("Offline") : null,
                ToolTip = account.DisplayName,
            });
        }

        UpdateSelection();
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
            if (entry.Location is not { } target || target.AccountId != location.AccountId)
            {
                continue;
            }

            var matches = location.IsCloud
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

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                entry.Size = size;
                onDone?.Invoke(size);
            });
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
