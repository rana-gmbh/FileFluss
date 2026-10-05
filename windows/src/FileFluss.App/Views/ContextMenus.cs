// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
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
            foreach (var (field, title) in new[] { (SortField.Name, "Name"), (SortField.Date, "Date Modified"), (SortField.Size, "Size"), (SortField.Kind, "Kind") })
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

        if (single is { IsDirectory: false } && panel.Provider is CloudProvider cloud && cloud.ShareLinkCapabilities.CanCreate)
        {
            items.Add(new Separator());
            items.Add(Item(L("Share Link…"), Glyphs.Share, null, () => main.Shell.ShowShareLink(panel, single.Item)));
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
