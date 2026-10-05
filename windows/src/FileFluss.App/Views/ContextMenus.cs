// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Commands;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Native;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views;

/// <summary>
/// The file list's right-click menus, in the macOS order: open/reveal/terminal, clipboard,
/// the other panel, rename/new folder, folder tools, sharing, and delete last.
/// </summary>
public static class ContextMenus
{
    public static IEnumerable<object> ForPanel(MainViewModel main, PanelViewModel panel, PanelView view)
    {
        var selection = panel.Selection;
        var items = new List<object>();
        string Shortcut(KeyboardCommand command) => view.ShortcutText(command);

        if (selection.Count == 0)
        {
            if (panel.CanCreateFolders)
            {
                items.Add(Item(L("New Folder"), Glyphs.NewFolder, Shortcut(KeyboardCommand.NewFolder), () => _ = main.NewFolderAsync(panel)));
            }

            if (!panel.IsReadOnly && !(panel.IsLocal && panel.CurrentPath.Length == 0))
            {
                items.Add(Item(L("Paste"), Glyphs.Paste, Shortcut(KeyboardCommand.PasteFiles), () => _ = main.PasteAsync(panel)));
            }

            items.Add(new Separator());
            if (panel.IsLocal && panel.CurrentPath.Length > 0)
            {
                items.Add(Item(L("Open in Terminal"), Glyphs.Terminal, Shortcut(KeyboardCommand.OpenInTerminal), () => _ = main.ExecuteAsync(KeyboardCommand.OpenInTerminal)));
                items.Add(Item(L("Show in Explorer"), Glyphs.OpenFolder, null, () => ShellFileOperations.Open(panel.CurrentPath)));
            }

            items.Add(Item(L("Refresh"), Glyphs.Refresh, Shortcut(KeyboardCommand.RefreshActive), () => _ = panel.RefreshAsync()));
            items.Add(Check(L("Show Hidden Files"), panel.ShowHiddenFiles, () => panel.ShowHiddenFiles = !panel.ShowHiddenFiles));
            var sort = new MenuItem { Header = L("Sort By") };
            var fields = new List<(SortField Field, string Title)> { (SortField.Name, "Name"), (SortField.Date, "Date Modified") };
            if (panel.IsLocal)
            {
                // Cloud listings carry no creation date (macOS shows the column for local panels only).
                fields.Add((SortField.DateCreated, "Date Created"));
            }

            fields.AddRange([(SortField.Size, "Size"), (SortField.Kind, "Kind")]);
            foreach (var (field, title) in fields)
            {
                sort.Items.Add(Check(L(title), panel.SortField == field, () => panel.SortBy(field)));
            }

            items.Add(sort);
            if (panel.IsCloud && panel.Location.AccountId is { } accountId)
            {
                items.Add(new Separator());
                items.Add(Item(L("Storage Analysis"), Glyphs.Pie, null, () => main.Shell.ShowStorageAnalysis(panel.Location)));
            }

            return TrimSeparators(items);
        }

        var single = selection.Count == 1 ? selection[0] : null;
        var modifiable = selection.All(r => r.Item.IsModifiable) && !panel.IsReadOnly;
        var otherName = panel.Side == PanelSide.Left ? L("Right") : L("Left");

        if (single is not null)
        {
            items.Add(Bold(Item(single.IsDirectory ? L("Open") : L("Open"), Glyphs.OpenInNew, single.IsDirectory ? Shortcut(KeyboardCommand.OpenDirectory) : null, () => _ = main.OpenAsync(panel, single))));
            if (!single.IsDirectory && panel.IsLocal)
            {
                items.Add(Item(L("Open With…"), null, null, () => ShellFileOperations.OpenWith(single.Path, WindowHandle(view))));
            }

            if (!main.SinglePaneMode && single.IsDirectory)
            {
                items.Add(Item(L("Open in Other Panel"), null, Shortcut(KeyboardCommand.OpenInOtherPanel), () => _ = main.OtherPanel.NavigateAsync(panel.Location with { Path = single.Path })));
            }

            items.Add(Item(L("Quick Look"), Glyphs.Eye, Shortcut(KeyboardCommand.QuickLook), () => main.Shell.ShowQuickLook(panel)));
        }

        if (panel.IsLocal)
        {
            items.Add(Item(L("Show in Explorer"), Glyphs.OpenFolder, null, () => ShellFileOperations.RevealInExplorer(selection.Select(r => r.Path).ToList())));
            items.Add(Item(L("Open in Terminal"), Glyphs.Terminal, Shortcut(KeyboardCommand.OpenInTerminal), () => _ = main.ExecuteAsync(KeyboardCommand.OpenInTerminal)));
        }

        items.Add(new Separator());
        if (modifiable)
        {
            items.Add(Item(L("Cut"), Glyphs.Cut, Shortcut(KeyboardCommand.CutFiles), () => main.CaptureClipboard(panel, isCut: true)));
        }

        items.Add(Item(L("Copy"), Glyphs.Copy, Shortcut(KeyboardCommand.CopyFiles), () => main.CaptureClipboard(panel, isCut: false)));
        if (!panel.IsReadOnly)
        {
            items.Add(Item(L("Paste"), Glyphs.Paste, Shortcut(KeyboardCommand.PasteFiles), () => _ = main.PasteAsync(panel)));
        }

        if (!main.SinglePaneMode)
        {
            items.Add(new Separator());
            items.Add(Item(L("Copy to {0} Panel", otherName), null, Shortcut(KeyboardCommand.CopyToOtherPanel), () => _ = main.TransferAsync(panel.Provider, panel.SelectedItems, main.OtherPanel.Location, false, main.OtherPanel.Side)));
            if (modifiable)
            {
                items.Add(Item(L("Move to {0} Panel", otherName), Glyphs.MoveTo, Shortcut(KeyboardCommand.MoveToOtherPanel), () => _ = main.TransferAsync(panel.Provider, panel.SelectedItems, main.OtherPanel.Location, true, main.OtherPanel.Side)));
            }
        }

        items.Add(new Separator());
        if (single is not null && modifiable)
        {
            items.Add(Item(L("Rename"), Glyphs.Rename, Shortcut(KeyboardCommand.Rename), view.BeginRename));
        }

        if (panel.CanCreateFolders)
        {
            items.Add(Item(L("New Folder"), Glyphs.NewFolder, Shortcut(KeyboardCommand.NewFolder), () => _ = main.NewFolderAsync(panel)));
        }

        if (modifiable && panel.Provider is not null && !panel.IsReadOnly)
        {
            items.Add(Item(L("Duplicate"), null, Shortcut(KeyboardCommand.Duplicate), () => _ = main.ExecuteAsync(KeyboardCommand.Duplicate)));
        }

        if (single is { IsDirectory: true })
        {
            items.Add(new Separator());
            items.Add(Item(L("Add to Favorites"), Glyphs.Star, null, () => main.Sidebar(panel.Side).AddFavorite(panel.Location with { Path = single.Path }, single.Name)));
            items.Add(Item(L("Calculate Folder Size"), Glyphs.Calculator, null, () => CalculateFolderSize(main, panel, single)));
            items.Add(Item(L("Storage Analysis"), Glyphs.Pie, null, () => main.Shell.ShowStorageAnalysis(panel.Location with { Path = single.Path })));
        }

        // Share links are per-file: folders, multi-selections and synthetic rows have no single
        // shareable path.
        if (single is { IsDirectory: false, Item.Role: CloudItemRole.Normal } && panel.Provider is CloudProvider cloud && cloud.ShareLinkCapabilities.CanCreate)
        {
            items.Add(new Separator());
            items.AddRange(ShareItems(main, panel, view, cloud, single.Item));
        }

        items.Add(new Separator());
        items.Add(Item(L("Copy Path"), Glyphs.Link, Shortcut(KeyboardCommand.CopyPath), () => main.CopyPaths(panel)));
        if (single is not null)
        {
            items.Add(Item(L("Properties"), Glyphs.Info, Shortcut(KeyboardCommand.GetInfo), () => main.Shell.ShowProperties(panel, single.Item)));
        }

        if (modifiable)
        {
            items.Add(new Separator());
            var deleteTitle = panel.IsLocal ? L("Move to Recycle Bin") : L("Delete");
            var delete = Item(deleteTitle, Glyphs.Delete, Shortcut(KeyboardCommand.DeleteToTrash), () => _ = main.DeleteSelectionAsync(panel));
            delete.SetResourceReference(Control.ForegroundProperty, "DangerBrush");
            items.Add(delete);
        }

        return TrimSeparators(items);
    }

    // MARK: - Sharing

    /// <summary>
    /// "Copy Share Link", then — for a file known to be shared — a checked "Shared" marker,
    /// "Change Sharing…" and "Stop Sharing", or else "Share Link…" when there are options to
    /// set (macOS <c>NativeCloudFileList</c>). The menu is built at once; where the state isn't
    /// known yet it is looked up now and the entries are swapped while the menu is still open.
    /// </summary>
    private static List<object> ShareItems(MainViewModel main, PanelViewModel panel, PanelView view, CloudProvider provider, CloudFileItem item)
    {
        var sharing = ShareLinkService.Shared;
        var known = sharing.IsShared(provider, item.Path);
        var items = ShareEntries(main, panel, view, provider, item, ShareLinkMenu.For(provider.ShareLinkCapabilities, known));
        if (known is null && provider.ShareLinkCapabilities.CanQueryExisting)
        {
            _ = SwapWhenSharedAsync(items, main, panel, view, provider, item);
        }

        return items;
    }

    private static List<object> ShareEntries(MainViewModel main, PanelViewModel panel, PanelView view, CloudProvider provider, CloudFileItem item, ShareLinkMenu menu)
    {
        var items = new List<object> { Item(L("Copy Share Link"), Glyphs.Link, null, () => _ = CopyShareLinkAsync(view, provider, item)) };
        if (menu.SharedMarker)
        {
            // The state marker: a checked "Shared" tells the user at a glance.
            items.Add(new MenuItem { Header = L("Shared"), IsChecked = true, IsEnabled = false });
        }

        if (menu.ChangeSharing)
        {
            items.Add(Item(L("Change Sharing…"), Glyphs.Share, null, () => main.Shell.ShowShareLink(panel, item)));
        }

        if (menu.StopSharing)
        {
            items.Add(Item(L("Stop Sharing"), null, null, () => _ = StopSharingAsync(view, provider, item)));
        }

        if (menu.ShareWithOptions)
        {
            items.Add(Item(L("Share Link…"), Glyphs.Share, null, () => main.Shell.ShowShareLink(panel, item)));
        }

        return items;
    }

    private static async Task SwapWhenSharedAsync(List<object> shown, MainViewModel main, PanelViewModel panel, PanelView view, CloudProvider provider, CloudFileItem item)
    {
        if (!await ShareLinkService.Shared.RefreshStateAsync(provider, item.Path))
        {
            return;
        }

        // Only while the same menu is still open: a closed one is rebuilt from the cache next time.
        if (shown[0] is not MenuItem { Parent: ContextMenu { IsOpen: true } menu } first)
        {
            return;
        }

        var index = menu.Items.IndexOf(first);
        if (index < 0)
        {
            return;
        }

        foreach (var entry in shown)
        {
            menu.Items.Remove(entry);
        }

        var replacement = ShareEntries(main, panel, view, provider, item, ShareLinkMenu.For(provider.ShareLinkCapabilities, isShared: true));
        for (var i = 0; i < replacement.Count; i++)
        {
            menu.Items.Insert(index + i, replacement[i]);
        }
    }

    /// <summary>One click: create (or fetch) the link, copy it, confirm with the panel's banner.</summary>
    private static async Task CopyShareLinkAsync(PanelView view, CloudProvider provider, CloudFileItem item)
    {
        var sharing = ShareLinkService.Shared;
        var owner = Window.GetWindow(view);
        var isShared = sharing.IsShared(provider, item.Path) == true;
        CloudShareLink? link = null;
        Exception? failure = null;

        // Creating and checking a link is a round trip or two: the "working in the background"
        // pointer says something is happening without blocking the window.
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            link = isShared
                ? await sharing.CopyExistingAsync(provider, item.Path)
                : await sharing.CreateAsync(provider, item.Path, ShareLinkOptions.Default, reuseExisting: true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            failure = e;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (failure is not null)
        {
            var message = ShareLinkService.IsSignInError(failure) ? CloudProviderException.Unauthorized().Message
                : isShared ? L("Couldn't read this file's share link: {0}", failure.Message)
                : L("Couldn't create the share link: {0}", ShareLinkService.FailureMessage(failure, ShareLinkOptions.Default));
            MessageDialog.Error(owner, L("Sharing failed"), message);
            return;
        }

        if (link is null)
        {
            // Someone removed it elsewhere in the meantime.
            MessageDialog.Error(owner, L("Sharing failed"), L("This file is no longer shared."));
            return;
        }

        ShareLinkBanner.Show(view, link, sharing.UrlFor(link));
    }

    private static async Task StopSharingAsync(PanelView view, CloudProvider provider, CloudFileItem item)
    {
        var owner = Window.GetWindow(view);
        if (!MessageDialog.Confirm(owner, L("Stop sharing this file?"), L("The link to \"{0}\" stops working for everyone who has it. This can't be undone.", item.Name), L("Stop Sharing"), destructive: true))
        {
            return;
        }

        try
        {
            await ShareLinkService.Shared.RemoveAsync(provider, item.Path);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            MessageDialog.Error(owner, L("Sharing failed"), L("Couldn't stop sharing: {0}", e.Message));
        }
    }

    private static void CalculateFolderSize(MainViewModel main, PanelViewModel panel, FileRowViewModel row)
    {
        if (panel.Provider is not { } provider)
        {
            return;
        }

        main.Sidebar(panel.Side).CalculateFolderSize(provider, row.Item, panel.Location with { Path = row.Path }, size => row.FolderSize = size);
    }

    public static MenuItem Item(string header, string? glyph, string? shortcut, Action action)
    {
        var item = new MenuItem { Header = header, InputGestureText = shortcut ?? string.Empty };
        if (glyph is not null)
        {
            item.Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily(Glyphs.FontFamily), FontSize = 14 };
        }

        item.Click += (_, _) => action();
        return item;
    }

    public static MenuItem Check(string header, bool isChecked, Action action)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Bold(MenuItem item)
    {
        item.FontWeight = FontWeights.SemiBold;
        return item;
    }

    /// <summary>No separator first, last, or twice in a row.</summary>
    public static List<object> TrimSeparators(List<object> items)
    {
        var result = new List<object>();
        foreach (var item in items)
        {
            if (item is Separator && (result.Count == 0 || result[^1] is Separator))
            {
                continue;
            }

            result.Add(item);
        }

        while (result.Count > 0 && result[^1] is Separator)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static nint WindowHandle(DependencyObject view)
        => Window.GetWindow(view) is { } window ? new System.Windows.Interop.WindowInteropHelper(window).Handle : 0;
}
