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
    private readonly DispatcherTimer _springTimer;
    private SidebarEntry? _springEntry;
    private SidebarEntry? _hoverEntry;
    private Point _dragStart;
    private SidebarEntry? _dragCandidate;

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

        DataContextChanged += (_, _) => ApplySectionState();
    }

    public MainViewModel Main { get; set; } = null!;

    private SidebarViewModel? Vm => DataContext as SidebarViewModel;

    private PanelViewModel Panel => Main.Panel(Vm!.Side);

    public void Attach(MainViewModel main)
    {
        Main = main;
        main.Transfers.All.CollectionChanged += (_, _) => RefreshTransfers();
        main.Services.SettingsStore.Changed += (_, _) => RefreshTransfers();
        RefreshTransfers();
    }

    private void RefreshTransfers()
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var transfers = Main.Transfers.For(vm.Side).ToList();
        TransfersList.ItemsSource = transfers;
        TransfersSection.Visibility = vm.ShowTransfers && transfers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private static SidebarEntry? EntryAt(ItemsControl list, Point point)
    {
        var hit = list.InputHitTest(point) as DependencyObject;
        while (hit is not null and not ListBoxItem)
        {
            hit = VisualTreeHelper.GetParent(hit);
        }

        return (hit as ListBoxItem)?.DataContext as SidebarEntry;
    }

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

    // MARK: - Dragging favourites to reorder

    private void OnEntryMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not { Kind: SidebarEntryKind.Favorite } entry)
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
        var data = new DataObject(FavoriteFormat, entry);
        DragDrop.DoDragDrop(list, data, DragDropEffects.Move);
        ClearHover();
    }

    // MARK: - Dropping onto entries

    private void OnEntryDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var list = (ListBox)sender;
        var entry = EntryAt(list, e.GetPosition(list));

        if (e.Data.GetDataPresent(FavoriteFormat))
        {
            e.Effects = list == FavoritesList ? DragDropEffects.Move : DragDropEffects.None;
            Highlight(entry is { Kind: SidebarEntryKind.Favorite } ? entry : null);
            return;
        }

        var payload = DragPayload.From(e.Data);
        var accepts = entry is { AcceptsDrops: true } && (payload is not null || e.Data.GetDataPresent(DataFormats.FileDrop)) &&
                      (payload is null || payload.SourceFolder != entry.Location);
        e.Effects = accepts ? ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0 ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None;
        Highlight(accepts ? entry : null);

        // Spring-loaded: rest on a row and the panel opens there.
        if (accepts && !ReferenceEquals(entry, _springEntry))
        {
            _springEntry = entry;
            _springTimer.Stop();
            _springTimer.Start();
        }
        else if (!accepts)
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
        _springTimer.Stop();
        _springEntry = null;
    }

    private async void OnEntryDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var list = (ListBox)sender;
        var entry = EntryAt(list, e.GetPosition(list));
        ClearHover();
        if (Vm is not { } vm)
        {
            return;
        }

        if (e.Data.GetData(FavoriteFormat) is SidebarEntry { FavoriteId: { } movingId })
        {
            var index = entry is null ? vm.Favorites.Count : vm.Favorites.IndexOf(entry);
            vm.MoveFavorite(movingId, index);
            return;
        }

        if (entry is not { AcceptsDrops: true, Location: { } destination })
        {
            return;
        }

        var forced = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 ? true : (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? false : (bool?)null;
        if (DragPayload.From(e.Data) is { } payload)
        {
            if (Main.ResolveDropVerb(payload.Items.Count, entry.Title, forced) is { } isMove)
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

            if (Main.ResolveDropVerb(items.Count, entry.Title, forced) is { } isMove)
            {
                await Main.TransferAsync(LocalFileProvider.Shared, items, destination, isMove, vm.Side);
            }
        }
    }

    /// <summary>A folder dropped on the Favorites header becomes a favourite (macOS: drag onto Favorites).</summary>
    private void OnFavoritesHeaderDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var payload = DragPayload.From(e.Data);
        e.Effects = payload?.Items.Any(i => i.IsDirectory) == true || e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
    }

    private void OnFavoritesHeaderDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Vm is not { } vm)
        {
            return;
        }

        if (DragPayload.From(e.Data) is { } payload)
        {
            foreach (var folder in payload.Items.Where(i => i.IsDirectory))
            {
                vm.AddFavorite(payload.SourceFolder with { Path = folder.Path }, folder.Name);
            }
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths.Where(System.IO.Directory.Exists))
            {
                vm.AddFavorite(PanelLocation.Local(path), System.IO.Path.GetFileName(path.TrimEnd('\\')));
            }
        }
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
