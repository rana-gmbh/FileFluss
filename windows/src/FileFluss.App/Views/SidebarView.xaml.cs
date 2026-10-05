// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.App.Views.Accounts;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Search;
using FileFluss.Core.Transfers;
using FileFluss.Native;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views;

public partial class SidebarView : UserControl
{
    private const string FavoriteFormat = "FileFluss.Favorite";
    private const string AccountFormat = "FileFluss.Account";

    /// <summary>How long after a popup closed a click on its badge counts as the click that closed it.</summary>
    private static readonly TimeSpan PopupReopenGuard = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _springTimer;
    private readonly Dictionary<Popup, DateTime> _popupClosedAt = [];
    private SidebarEntry? _springEntry;
    private SidebarEntry? _hoverEntry;
    private InsertionLineAdorner? _insertion;
    private Point _dragStart;
    private SidebarEntry? _dragCandidate;
    private SidebarViewModel? _observed;
    private (IDataObject Data, bool HasFolders)? _folderCheck;

    public SidebarView()
    {
        InitializeComponent();
        _springTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _springTimer.Tick += OnSpringTimer;
        foreach (var list in new[] { FavoritesList, DrivesList, AccountsList })
        {
            list.PreviewMouseLeftButtonDown += OnEntryMouseDown;
            list.PreviewMouseLeftButtonUp += OnEntryMouseUp;
            list.PreviewMouseMove += OnEntryMouseMove;
            list.MouseRightButtonUp += OnEntryRightClick;
            list.DragOver += OnEntryDragOver;
            list.DragEnter += OnEntryDragOver;
            list.DragLeave += (_, _) => ClearHover();
            list.Drop += OnEntryDrop;
        }

        DataContextChanged += (_, _) => OnViewModelChanged();
    }

    public MainViewModel Main { get; set; } = null!;

    private SidebarViewModel? Vm => DataContext as SidebarViewModel;

    private PanelViewModel Panel => Main.Panel(Vm!.Side);

    public void Attach(MainViewModel main)
    {
        Main = main;
        main.Transfers.All.CollectionChanged += (_, _) => UpdateInfoSections();
        main.Services.SettingsStore.Changed += (_, _) => UpdateInfoSections();
        UpdateInfoSections();
    }

    private void OnViewModelChanged()
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _observed = Vm;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnViewModelPropertyChanged;

            // The popups open towards the panel, away from the window edge.
            var placement = _observed.Side == PanelSide.Left ? PlacementMode.Right : PlacementMode.Left;
            TransfersPopup.Placement = placement;
            FolderSizesPopup.Placement = placement;
        }

        ApplySectionState();
        UpdateInfoSections();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(SidebarViewModel.IsCompact) or nameof(SidebarViewModel.ShowFolderSizes) or nameof(SidebarViewModel.ShowTransfers))
        {
            UpdateInfoSections();
        }
    }

    // MARK: - Transfers and folder sizes: full sections, or badges in icon-only mode (macOS collapsedInfoRow)

    private void UpdateInfoSections()
    {
        if (Vm is not { } vm || Main is null)
        {
            return;
        }

        var compact = vm.IsCompact;
        var transfers = Main.Transfers.For(vm.Side).ToList();
        TransfersList.ItemsSource = transfers;
        TransfersPopupList.ItemsSource = transfers;
        var showTransfers = vm.ShowTransfers && transfers.Count > 0;
        TransfersSection.Visibility = showTransfers && !compact ? Visibility.Visible : Visibility.Collapsed;
        UpdateBadge(TransfersBadge, TransfersPopup, TransfersCountBadge, TransfersCountText, showTransfers && compact, transfers.Count,
            transfers.Count == 1 ? L("1 transfer — click for details") : L("{0} transfers — click for details", transfers.Count));

        var sizes = vm.FolderSizes.Count;
        FolderSizesSection.Visibility = vm.ShowFolderSizes && !compact ? Visibility.Visible : Visibility.Collapsed;
        UpdateBadge(FolderSizesBadge, FolderSizesPopup, FolderSizesCountBadge, FolderSizesCountText, vm.ShowFolderSizes && compact, sizes,
            sizes == 1 ? L("1 folder size — click for details") : L("{0} folder sizes — click for details", sizes));

        CompactInfo.Visibility = TransfersBadge.Visibility == Visibility.Visible || FolderSizesBadge.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void UpdateBadge(ToggleButton badge, Popup popup, Border countBadge, TextBlock countText, bool visible, int count, string toolTip)
    {
        badge.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        badge.ToolTip = toolTip;
        countText.Text = count.ToString(Culture);
        countBadge.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            popup.IsOpen = false;
        }
    }

    private void OnInfoBadgeClicked(object sender, RoutedEventArgs e)
    {
        var badge = (ToggleButton)sender;
        var popup = badge == TransfersBadge ? TransfersPopup : FolderSizesPopup;

        // A click on the badge of an open popup closes it on mouse-down; the click itself must not reopen it.
        var justClosed = _popupClosedAt.TryGetValue(popup, out var closedAt) && DateTime.UtcNow - closedAt < PopupReopenGuard;
        var open = badge.IsChecked == true && !justClosed;
        badge.IsChecked = open;
        popup.IsOpen = open;
    }

    private void OnInfoPopupClosed(object? sender, EventArgs e)
    {
        var popup = (Popup)sender!;
        _popupClosedAt[popup] = DateTime.UtcNow;
        (popup == TransfersPopup ? TransfersBadge : FolderSizesBadge).IsChecked = false;
    }

    // MARK: - Sections

    private void ApplySectionState()
    {
        if (Vm is not { } vm)
        {
            return;
        }

        foreach (var (header, content) in new (ToggleButton, FrameworkElement)[] { (FavoritesHeader, FavoritesList), (DrivesHeader, DrivesList), (CloudHeader, CloudSection) })
        {
            var expanded = vm.IsSectionExpanded((string)header.Tag);
            header.IsChecked = expanded;
            content.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnSectionToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string key } header && Vm is { } vm)
        {
            vm.SetSectionExpanded(key, header.IsChecked == true);
            ApplySectionState();
        }
    }

    // MARK: - Clicking

    private void OnEntryMouseDown(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        _dragStart = e.GetPosition(list);
        _dragCandidate = EntryAt(list, _dragStart);
        e.Handled = true;
    }

    private async void OnEntryMouseUp(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        var entry = EntryAt(list, e.GetPosition(list));
        _dragCandidate = null;
        if (entry?.Location is not { } location || Vm is not { } vm)
        {
            return;
        }

        Main.ActiveSide = vm.Side;
        if (entry.Account is { } account)
        {
            await OpenAccountAsync(account, Panel);
        }
        else
        {
            await Panel.NavigateAsync(location);
        }

        Main.Shell.FocusPanel(vm.Side);
    }

    /// <summary>Opens an account in a panel; one that needs signing in again offers it first.</summary>
    private async Task OpenAccountAsync(CloudAccount account, PanelViewModel panel)
    {
        await panel.NavigateAsync(PanelLocation.Cloud(account.Id, account.EffectiveRootPath));
    }

    private void OnAddAccount(object sender, RoutedEventArgs e) => AccountDialog.Add(Window.GetWindow(this), Main.Services);

    // MARK: - Context menus

    private void OnEntryRightClick(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        if (EntryAt(list, e.GetPosition(list)) is not { } entry || Vm is not { } vm)
        {
            return;
        }

        var menu = new ContextMenu();
        var other = Main.Panel(vm.Side.Other());
        if (entry.Location is { } location)
        {
            menu.Items.Add(ContextMenus.Item(L("Open"), Glyphs.OpenInNew, null, async () => await Panel.NavigateAsync(location)));
            if (!Main.SinglePaneMode)
            {
                menu.Items.Add(ContextMenus.Item(L("Open in Other Panel"), null, null, async () => await other.NavigateAsync(location)));
            }
        }

        switch (entry.Kind)
        {
            case SidebarEntryKind.Favorite when entry.FavoriteId is { } id:
                menu.Items.Add(new Separator());
                menu.Items.Add(ContextMenus.Item(L("Rename…"), Glyphs.Rename, null, () =>
                {
                    if (MessageDialog.Prompt(Window.GetWindow(this), L("Rename Favorite"), string.Empty, entry.Title, L("Rename")) is { } name)
                    {
                        vm.RenameFavorite(id, name);
                    }
                }));
                menu.Items.Add(ChangeIconMenu(vm, entry, id));
                menu.Items.Add(new Separator());
                menu.Items.Add(ContextMenus.Item(L("Remove from Favorites"), Glyphs.Delete, null, () => vm.RemoveFavorite(id)));
                break;
            case SidebarEntryKind.Drive when entry.Location is { IsOfflineDrive: false } drive:
                menu.Items.Add(new Separator());
                menu.Items.Add(ContextMenus.Item(L("Show in Explorer"), Glyphs.OpenFolder, null, () => ShellFileOperations.Open(drive.Path)));
                menu.Items.Add(ContextMenus.Item(L("Storage Analysis"), Glyphs.Pie, null, () => Main.Shell.ShowStorageAnalysis(drive)));
                menu.Items.Add(ContextMenus.Item(L("Properties"), Glyphs.Info, null, () => ShellFileOperations.ShowProperties(drive.Path, 0)));
                if (entry.DriveId is { } driveId)
                {
                    AddDriveIndexItems(menu, driveId, isOnline: true);
                }

                break;
            case SidebarEntryKind.Drive when entry.DriveId is { } offlineDrive:
                AddDriveIndexItems(menu, offlineDrive, isOnline: false);
                break;
            case SidebarEntryKind.Account when entry.Account is { } account:
                menu.Items.Add(new Separator());
                menu.Items.Add(ContextMenus.Item(L("Edit…"), Glyphs.Settings, null, () => AccountDialog.Edit(Window.GetWindow(this), Main.Services, account.Id)));
                menu.Items.Add(ContextMenus.Item(L("Rename…"), Glyphs.Rename, null, () =>
                {
                    if (MessageDialog.Prompt(Window.GetWindow(this), L("Rename Account"), string.Empty, account.DisplayName, L("Rename")) is { } name)
                    {
                        Main.Services.Accounts.Rename(account.Id, name);
                    }
                }));
                if (account.IsConnected && !account.IsOfflineMode)
                {
                    menu.Items.Add(ContextMenus.Item(L("Storage Analysis"), Glyphs.Pie, null, () => Main.Shell.ShowStorageAnalysis(PanelLocation.Cloud(account.Id, account.EffectiveRootPath))));

                    // macOS mountMenu: Mount in Finder / Reveal + Unmount.
                    if (Main.Services.Mount.MountFor(account.Id) is { } mount)
                    {
                        menu.Items.Add(ContextMenus.Item(L("Reveal in Finder"), Glyphs.OpenFolder, null, () => Main.Services.Mount.OpenInExplorer(account.Id)));
                        menu.Items.Add(ContextMenus.Item(L("Disconnect Network Drive ({0})", mount.DriveLetter), Glyphs.Mount, null,
                            async () => await Main.Services.Mount.UnmountAsync(account.Id)));
                    }
                    else
                    {
                        var map = ContextMenus.Item(L("Mount in Finder"), Glyphs.Mount, null, async () =>
                        {
                            if (await Main.MountAccountAsync(account) is not null)
                            {
                                Main.Services.Mount.OpenInExplorer(account.Id);
                            }
                        });
                        map.ToolTip = L("Open this account as a drive in Finder");
                        menu.Items.Add(map);
                    }
                }

                AddAccountIndexItems(menu, account);

                if (Main.Services.Settings.AllowSidebarRemoveAccount)
                {
                    menu.Items.Add(new Separator());
                    var remove = ContextMenus.Item(L("Remove from FileFluss"), Glyphs.Delete, null, async () =>
                    {
                        if (MessageDialog.Confirm(Window.GetWindow(this), L("Remove Account"),
                                L("Remove \"{0}\" from FileFluss? Files in the account are not touched.", account.DisplayName), L("Remove"), destructive: true))
                        {
                            await Main.Services.Accounts.RemoveAsync(account.Id);
                        }
                    });
                    remove.SetResourceReference(ForegroundProperty, "DangerBrush");
                    menu.Items.Add(remove);
                }

                break;
        }

        menu.PlacementTarget = list;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// macOS changeIconMenu: the provider's logo for a cloud favourite, then the curated glyphs.
    /// The current choice is shown in bold.
    /// </summary>
    private MenuItem ChangeIconMenu(SidebarViewModel vm, SidebarEntry entry, Guid id)
    {
        var menu = new MenuItem { Header = L("Change Icon") };
        var current = entry.Icon ?? string.Empty;
        if (entry.Location?.AccountId is { } accountId && Main.Services.Accounts.Find(accountId) is { } account
            && IconCache.Logo(account.ProviderType) is { } logo)
        {
            var icon = Glyphs.LogoPrefix + account.ProviderType.LogoAssetName();
            var item = new MenuItem
            {
                Header = L("{0} Logo", account.ProviderType.DisplayName()),
                Icon = new Image { Source = logo, Width = 16, Height = 16 },
                FontWeight = icon == current ? FontWeights.SemiBold : FontWeights.Normal,
            };
            item.Click += (_, _) => vm.SetFavoriteIcon(id, icon);
            menu.Items.Add(item);
            menu.Items.Add(new Separator());
        }

        foreach (var (icon, name) in Glyphs.FavoriteIconLibrary)
        {
            var item = ContextMenus.Item(L(name), Glyphs.ForFavorite(icon), null, () => vm.SetFavoriteIcon(id, icon));
            if (Glyphs.LogoAsset(current) is null && Glyphs.ForFavorite(current) == Glyphs.ForFavorite(icon))
            {
                item.FontWeight = FontWeights.SemiBold;
            }

            menu.Items.Add(item);
        }

        return menu;
    }

    // MARK: - Offline index items (macOS driveContextMenu / cloudIndexMenu / offlineModeMenu)

    private void AddDriveIndexItems(ContextMenu menu, string driveId, bool isOnline)
    {
        var search = Main.Services.Search;
        if (search.Drives.Find(driveId) is not { } drive)
        {
            return;
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Label(drive.LastIndexed is { } last
            ? IndexingText.IndexedSummary(last, drive.TotalFiles, DateTimeOffset.Now)
            : IndexingText.NotIndexed(IndexingJobKind.Drive)));
        if (isOnline && drive.RootPath is { } root)
        {
            menu.Items.Add(search.Indexing.ActiveJob(driveId) is null
                ? ContextMenus.Item(drive.IsIndexed ? L("Re-index for Offline Search…") : L("Index for Offline Search…"), Glyphs.Search, null,
                    () => search.Indexing.IndexDrive(drive, root))
                : ContextMenus.Item(L("Cancel Indexing"), Glyphs.Cancel, null, () => search.Indexing.Cancel(driveId)));
        }

        if (drive.IsIndexed)
        {
            menu.Items.Add(Danger(ContextMenus.Item(L("Remove Offline Index"), Glyphs.Delete, null, () =>
            {
                search.Safe(() => search.Drives.RemoveOfflineIndex(driveId));
                LeaveOfflineDrive(driveId);
            })));
        }
        else if (!isOnline)
        {
            menu.Items.Add(Danger(ContextMenus.Item(L("Forget Drive"), Glyphs.Delete, null, () =>
            {
                search.Safe(() => search.Drives.Forget(driveId));
                LeaveOfflineDrive(driveId);
            })));
        }
    }

    private void AddAccountIndexItems(ContextMenu menu, CloudAccount account)
    {
        var search = Main.Services.Search;
        var sourceId = SearchIndex.SourceIdFor(account.Id);
        var source = search.CloudSource(account.Id);
        var indexed = source?.LastIndexed is not null;

        if (account.IsOfflineMode)
        {
            menu.Items.Add(ContextMenus.Item(L("Go Online"), Glyphs.Cloud, null, () => Main.Services.Accounts.SetOfflineMode(account.Id, false)));
        }
        else
        {
            var goOffline = ContextMenus.Item(L("Go Offline"), Glyphs.Offline, null, () => Main.Services.Accounts.SetOfflineMode(account.Id, true));
            goOffline.IsEnabled = indexed;
            goOffline.ToolTip = indexed
                ? L("Browse and search this account's last-indexed contents without contacting the server.")
                : L("Index this account for offline use first.");
            ToolTipService.SetShowOnDisabled(goOffline, true);
            menu.Items.Add(goOffline);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Label(source?.LastIndexed is { } last
            ? IndexingText.IndexedSummary(last, source.TotalFiles, DateTimeOffset.Now)
            : IndexingText.NotIndexed(IndexingJobKind.Cloud)));
        if (!account.IsConnected)
        {
            return;
        }

        menu.Items.Add(search.Indexing.ActiveJob(sourceId) is null
            ? ContextMenus.Item(indexed ? L("Re-index for Offline Search…") : L("Index for Offline Search…"), Glyphs.Search, null,
                () => search.Indexing.IndexCloudAccount(account))
            : ContextMenus.Item(L("Cancel Indexing"), Glyphs.Cancel, null, () => search.Indexing.Cancel(sourceId)));
        if (indexed)
        {
            menu.Items.Add(Danger(ContextMenus.Item(L("Remove Offline Index"), Glyphs.Delete, null, () =>
            {
                search.Safe(() =>
                {
                    search.Index.DropCloudAccount(account.Id);
                    search.Index.DropSource(sourceId);
                });
                if (account.IsOfflineMode)
                {
                    Main.Services.Accounts.SetOfflineMode(account.Id, false);
                }

                Main.LeftSidebar.Rebuild();
                Main.RightSidebar.Rebuild();
            })));
        }
    }

    /// <summary>A panel showing a drive whose offline index just went has nothing left to show.</summary>
    private void LeaveOfflineDrive(string driveId)
    {
        foreach (var side in new[] { PanelSide.Left, PanelSide.Right })
        {
            if (Main.Panel(side).Location.DriveId == driveId)
            {
                _ = Main.Panel(side).NavigateAsync(PanelLocation.Local(string.Empty));
            }
        }

        Main.LeftSidebar.RebuildDrives();
        Main.RightSidebar.RebuildDrives();
    }

    private static MenuItem Label(string text) => new() { Header = text, IsEnabled = false };

    private MenuItem Danger(MenuItem item)
    {
        item.SetResourceReference(ForegroundProperty, "DangerBrush");
        return item;
    }

    // MARK: - Dragging rows to reorder (favourites, and cloud accounts as on macOS)

    private void OnEntryMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not { Kind: SidebarEntryKind.Favorite or SidebarEntryKind.Account } entry)
        {
            return;
        }

        var list = (ListBox)sender;
        var position = e.GetPosition(list);
        if (Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragCandidate = null;
        var data = new DataObject(entry.Kind == SidebarEntryKind.Favorite ? FavoriteFormat : AccountFormat, entry);
        DragDrop.DoDragDrop(list, data, DragDropEffects.Move);
        ClearHover();
    }

    /// <summary>The format a list's own rows are dragged under, or null for a list that can't be reordered.</summary>
    private string? ReorderFormat(ListBox list) => list == FavoritesList ? FavoriteFormat : list == AccountsList ? AccountFormat : null;

    /// <summary>The row being dragged to reorder, when the drag is one.</summary>
    private static (string Format, SidebarEntry Entry)? DraggedRow(IDataObject data)
    {
        foreach (var format in new[] { FavoriteFormat, AccountFormat })
        {
            if (data.GetDataPresent(format) && data.GetData(format) is SidebarEntry entry)
            {
                return (format, entry);
            }
        }

        return null;
    }

    /// <summary>Whether a dragged row can be reordered within <paramref name="list"/> — its own list, not the other sidebar's.</summary>
    private bool CanReorder(ListBox list, (string Format, SidebarEntry Entry) row)
        => row.Format == ReorderFormat(list)
           && (row.Format == AccountFormat ? Vm?.Accounts.Any(a => a.Account?.Id == row.Entry.Account?.Id) == true : list.Items.Contains(row.Entry));

    // MARK: - Row geometry

    private readonly record struct RowHit(SidebarEntry? Entry, int Index, double Fraction);

    private static ListBoxItem? ContainerAt(ItemsControl list, Point point)
    {
        var hit = list.InputHitTest(point) as DependencyObject;
        while (hit is not null and not ListBoxItem)
        {
            hit = VisualTreeHelper.GetParent(hit);
        }

        return hit as ListBoxItem;
    }

    private static SidebarEntry? EntryAt(ItemsControl list, Point point) => ContainerAt(list, point)?.DataContext as SidebarEntry;

    /// <summary>The row under the pointer, and how far down it the pointer is (0 = top edge, 1 = bottom).</summary>
    private static RowHit HitRow(ListBox list, DragEventArgs e)
    {
        if (ContainerAt(list, e.GetPosition(list)) is not { } container)
        {
            return new RowHit(null, list.Items.Count, 1);
        }

        var index = list.ItemContainerGenerator.IndexFromContainer(container);
        var fraction = container.ActualHeight > 0 ? e.GetPosition(container).Y / container.ActualHeight : 0.5;
        return new RowHit(container.DataContext as SidebarEntry, index, fraction);
    }

    /// <summary>The gap a drop would insert at: above the row in its upper half, below it in its lower half.</summary>
    private static int InsertionIndex(RowHit hit) => hit.Entry is null ? hit.Index : hit.Fraction < 0.5 ? hit.Index : hit.Index + 1;

    private void ShowInsertion(ListBox? list, int index)
    {
        if (list is null)
        {
            _insertion?.Remove();
            _insertion = null;
            return;
        }

        _insertion = InsertionLineAdorner.Show(_insertion, list, index);
    }

    // MARK: - Dropping onto entries

    private enum DropAction
    {
        None,

        /// <summary>Copy or move the dragged items into the row.</summary>
        Into,

        /// <summary>Make the dragged folders favourites, in the gap the insertion line shows.</summary>
        Insert,
    }

    /// <summary>
    /// Finder's rule, as on macOS: on a favourite, the outer quarters insert the dragged folders
    /// as favourites and the middle half transfers into it. A favourite that can't take files,
    /// or a drag with no folders, leaves only the one meaning. Drives and accounts only ever
    /// take files.
    /// </summary>
    private DropAction ActionFor(ListBox list, RowHit hit, IDataObject data)
    {
        var payload = data.GetData(DragPayload.Format) as DragPayload;
        if (payload is null && !data.GetDataPresent(DataFormats.FileDrop))
        {
            return DropAction.None;
        }

        var canInto = hit.Entry is { AcceptsDrops: true } entry && (payload is null || payload.SourceFolder != entry.Location);
        if (list != FavoritesList)
        {
            return canInto ? DropAction.Into : DropAction.None;
        }

        if (canInto && hit.Fraction > 0.25 && hit.Fraction < 0.75)
        {
            return DropAction.Into;
        }

        return HasFolders(data) ? DropAction.Insert : canInto ? DropAction.Into : DropAction.None;
    }

    /// <summary>Whether the drag carries a folder; remembered per drag so the disk isn't asked on every move.</summary>
    private bool HasFolders(IDataObject data)
    {
        if (_folderCheck is { } check && ReferenceEquals(check.Data, data))
        {
            return check.HasFolders;
        }

        var has = data.GetData(DragPayload.Format) is DragPayload payload
            ? payload.Items.Any(i => i.IsDirectory)
            : data.GetData(DataFormats.FileDrop) is string[] paths && paths.Any(System.IO.Directory.Exists);
        _folderCheck = (data, has);
        return has;
    }

    /// <summary>The dragged folders as favourite locations and names.</summary>
    private static List<(PanelLocation Location, string Name)> DroppedFolders(IDataObject data)
    {
        if (data.GetData(DragPayload.Format) is DragPayload payload)
        {
            return payload.Items.Where(i => i.IsDirectory).Select(f => (payload.SourceFolder with { Path = f.Path }, f.Name)).ToList();
        }

        if (data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            return paths.Where(System.IO.Directory.Exists)
                .Select(p => (PanelLocation.Local(p), System.IO.Path.GetFileName(p.TrimEnd('\\')) is { Length: > 0 } name ? name : p))
                .ToList();
        }

        return [];
    }

    private void OnEntryDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var list = (ListBox)sender;
        var hit = HitRow(list, e);

        if (DraggedRow(e.Data) is { } row)
        {
            var reorder = CanReorder(list, row);
            e.Effects = reorder ? DragDropEffects.Move : DragDropEffects.None;
            Highlight(null);
            ShowInsertion(reorder ? list : null, InsertionIndex(hit));
            return;
        }

        var action = ActionFor(list, hit, e.Data);
        e.Effects = action switch
        {
            DropAction.Into => (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 ? DragDropEffects.Move : DragDropEffects.Copy,
            DropAction.Insert => DragDropEffects.Link,
            _ => DragDropEffects.None,
        };

        // A filled row means "into this folder", the line means "add here" — never both at once.
        Highlight(action == DropAction.Into ? hit.Entry : null);
        ShowInsertion(action == DropAction.Insert ? list : null, InsertionIndex(hit));

        // Spring-loaded: rest on a row and the panel opens there. Only over the drop-into band,
        // so a drag heading for a gap doesn't navigate the panel away.
        if (action == DropAction.Into && !ReferenceEquals(hit.Entry, _springEntry))
        {
            _springEntry = hit.Entry;
            _springTimer.Stop();
            _springTimer.Start();
        }
        else if (action != DropAction.Into)
        {
            _springEntry = null;
            _springTimer.Stop();
        }
    }

    private async void OnSpringTimer(object? sender, EventArgs e)
    {
        _springTimer.Stop();
        if (_springEntry?.Location is { } location)
        {
            _springEntry = null;
            await Panel.NavigateAsync(location);
        }
    }

    private void Highlight(SidebarEntry? entry)
    {
        if (ReferenceEquals(entry, _hoverEntry))
        {
            return;
        }

        if (_hoverEntry is not null)
        {
            _hoverEntry.IsDropTarget = false;
        }

        _hoverEntry = entry;
        if (entry is not null)
        {
            entry.IsDropTarget = true;
        }
    }

    private void ClearHover()
    {
        Highlight(null);
        ShowInsertion(null, 0);
        _springTimer.Stop();
        _springEntry = null;
    }

    private async void OnEntryDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var list = (ListBox)sender;
        var hit = HitRow(list, e);
        ClearHover();
        if (Vm is not { } vm)
        {
            return;
        }

        if (DraggedRow(e.Data) is { } row)
        {
            if (!CanReorder(list, row))
            {
                return;
            }

            if (row.Entry.FavoriteId is { } favoriteId)
            {
                vm.MoveFavorite(favoriteId, InsertionIndex(hit));
            }
            else if (row.Entry.Account is { } account)
            {
                vm.MoveAccount(account.Id, InsertionIndex(hit));
            }

            return;
        }

        switch (ActionFor(list, hit, e.Data))
        {
            case DropAction.Insert:
                vm.AddFavorites(DroppedFolders(e.Data), InsertionIndex(hit));
                break;
            case DropAction.Into when hit.Entry is { Location: { } destination } entry:
                await TransferIntoAsync(vm, entry.Title, destination, e);
                break;
        }
    }

    private async Task TransferIntoAsync(SidebarViewModel vm, string destinationName, PanelLocation destination, DragEventArgs e)
    {
        var forced = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 ? true : (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? false : (bool?)null;
        if (e.Data.GetData(DragPayload.Format) is DragPayload payload)
        {
            if (Main.ResolveDropVerb(payload.Items.Count, destinationName, forced) is { } isMove)
            {
                await Main.TransferAsync(payload.Provider, payload.Items, destination, isMove, vm.Side);
            }
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            var items = new List<CloudFileItem>();
            foreach (var path in paths)
            {
                try
                {
                    items.Add(await LocalFileProvider.Shared.GetFileMetadataAsync(path));
                }
                catch (CloudProviderException)
                {
                }
            }

            if (Main.ResolveDropVerb(items.Count, destinationName, forced) is { } isMove)
            {
                await Main.TransferAsync(LocalFileProvider.Shared, items, destination, isMove, vm.Side);
            }
        }
    }

    // MARK: - The Favorites header: a folder dropped on it becomes the first favourite (macOS)

    private void OnFavoritesHeaderDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var reorder = DraggedRow(e.Data) is { } row && CanReorder(FavoritesList, row);
        var accepts = reorder || (DraggedRow(e.Data) is null && HasFolders(e.Data));
        e.Effects = reorder ? DragDropEffects.Move : accepts ? DragDropEffects.Link : DragDropEffects.None;
        ShowInsertion(accepts && FavoritesList.IsVisible ? FavoritesList : null, 0);
    }

    private void OnFavoritesHeaderDragLeave(object sender, DragEventArgs e) => ClearHover();

    private void OnFavoritesHeaderDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearHover();
        if (Vm is not { } vm)
        {
            return;
        }

        if (DraggedRow(e.Data) is { } row)
        {
            if (row.Entry.FavoriteId is { } id && CanReorder(FavoritesList, row))
            {
                vm.MoveFavorite(id, 0);
            }

            return;
        }

        vm.AddFavorites(DroppedFolders(e.Data), 0);
    }

    // MARK: - Transfers and folder sizes

    private void OnTransferClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferProgress transfer })
        {
            TransferDetailsWindow.Show(Window.GetWindow(this), transfer);
        }
    }

    private void OnTransferDismiss(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: TransferProgress transfer })
        {
            if (transfer.IsRunning)
            {
                if (MessageDialog.Confirm(Window.GetWindow(this), L("Cancel Transfer"),
                        L("Files already transferred will remain. Any partial file currently in flight will be discarded."), L("Cancel Transfer"), destructive: true))
                {
                    transfer.Cancel();
                }
            }
            else
            {
                Main.Transfers.Remove(transfer);
            }
        }
    }

    private void OnFolderSizeRemove(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderSizeEntry entry })
        {
            Vm?.RemoveFolderSize(entry);
        }
    }
}
