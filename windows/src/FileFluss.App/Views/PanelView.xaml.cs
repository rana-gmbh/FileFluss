// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Commands;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Native;
using static FileFluss.Core.Localization;

using Localization = FileFluss.Core.Localization;

namespace FileFluss.App.Views;

/// <summary>What is being dragged out of a panel (in-process; Explorer gets a FileDrop alongside).</summary>
public sealed record DragPayload(PanelViewModel? SourcePanel, FileProvider Provider, PanelLocation SourceFolder, IReadOnlyList<CloudFileItem> Items)
{
    public const string Format = "FileFluss.DragPayload";
}

public partial class PanelView : UserControl
{
    private readonly DispatcherTimer _springTimer;
    private Point _dragStart;
    private FileRowViewModel? _dragCandidate;
    private FileRowViewModel? _springRow;
    private FileRowViewModel? _hoverRow;
    private bool _committingRename;

    public PanelView()
    {
        InitializeComponent();
        _springTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _springTimer.Tick += OnSpringTimer;
        DataContextChanged += OnDataContextChanged;
        Localization.LanguageChanged += (_, _) => UpdateHeaders();
        Loaded += (_, _) => UpdateHeaders();
    }

    public MainViewModel Main { get; set; } = null!;

    private PanelViewModel? Vm => DataContext as PanelViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PanelViewModel old)
        {
            old.Loaded -= OnVmLoaded;
            old.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is PanelViewModel vm)
        {
            vm.Loaded += OnVmLoaded;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PanelViewModel.SortField):
            case nameof(PanelViewModel.SortAscending):
                UpdateHeaders();
                break;
            case nameof(PanelViewModel.Error):
                ErrorPanel.Visibility = Vm?.Error is null ? Visibility.Collapsed : Visibility.Visible;
                break;
            case nameof(PanelViewModel.Breadcrumbs):
                Dispatcher.BeginInvoke(() => CrumbScroller.ScrollToRightEnd(), DispatcherPriority.Loaded);
                break;
            case nameof(PanelViewModel.IsFilterVisible):
                if (Vm?.IsFilterVisible == true)
                {
                    Dispatcher.BeginInvoke(() => FilterBox.Focus(), DispatcherPriority.Input);
                }

                break;
        }
    }

    public void SetActive(bool active) => ActiveLine.Opacity = active ? 1 : 0;

    // MARK: - Selection and focus

    private void OnVmLoaded(object? sender, IReadOnlyCollection<string> names)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Vm is not { } vm)
            {
                return;
            }

            var rows = names.Count == 0 ? [] : vm.Rows.Where(r => names.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            List.SelectedItems.Clear();
            foreach (var row in rows)
            {
                List.SelectedItems.Add(row);
            }

            var anchor = rows.FirstOrDefault() ?? (IsKeyboardFocusWithin ? vm.Rows.FirstOrDefault() : null);
            if (anchor is not null)
            {
                if (rows.Count == 0)
                {
                    List.SelectedItem = anchor;
                }

                List.ScrollIntoView(anchor);
                if (IsKeyboardFocusWithin || Main.ActiveSide == vm.Side)
                {
                    FocusRow(anchor);
                }
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.Selection = List.SelectedItems.Cast<FileRowViewModel>().ToList();
        }
    }

    private void OnListFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Vm is { } vm)
        {
            Main.ActiveSide = vm.Side;
        }
    }

    /// <summary>Puts keyboard focus on the selected row (or the list), so arrows work straight away.</summary>
    public void FocusList()
    {
        var row = List.SelectedItem as FileRowViewModel ?? Vm?.Rows.FirstOrDefault();
        if (row is null)
        {
            List.Focus();
            return;
        }

        if (List.SelectedItems.Count == 0)
        {
            List.SelectedItem = row;
        }

        FocusRow(row);
    }

    private void FocusRow(FileRowViewModel row)
    {
        List.UpdateLayout();
        if (List.ItemContainerGenerator.ContainerFromItem(row) is ListViewItem container)
        {
            container.Focus();
        }
        else
        {
            List.ScrollIntoView(row);
            Dispatcher.BeginInvoke(() =>
            {
                if (List.ItemContainerGenerator.ContainerFromItem(row) is ListViewItem later)
                {
                    later.Focus();
                }
                else
                {
                    List.Focus();
                }
            }, DispatcherPriority.Loaded);
        }
    }

    public void SelectByName(string name)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Vm?.Rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) is { } row)
            {
                List.SelectedItems.Clear();
                List.SelectedItem = row;
                List.ScrollIntoView(row);
                FocusRow(row);
            }
        }, DispatcherPriority.Loaded);
    }

    public void ApplySelection(SelectionAction action)
    {
        switch (action)
        {
            case SelectionAction.SelectAll:
                List.SelectAll();
                break;
            case SelectionAction.DeselectAll:
                List.UnselectAll();
                break;
            case SelectionAction.Invert:
                var selected = List.SelectedItems.Cast<object>().ToHashSet();
                List.SelectedItems.Clear();
                foreach (var row in List.Items)
                {
                    if (!selected.Contains(row))
                    {
                        List.SelectedItems.Add(row);
                    }
                }

                break;
        }
    }

    // MARK: - Opening

    private async void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || RowAt(e.GetPosition(List)) is not { } row || Vm is not { } vm)
        {
            return;
        }

        e.Handled = true;
        await Main.OpenAsync(vm, row);
    }

    private FileRowViewModel? RowAt(Point point)
    {
        var hit = List.InputHitTest(point) as DependencyObject;
        while (hit is not null and not ListViewItem)
        {
            if (hit is GridViewColumnHeader or ScrollBar)
            {
                return null;
            }

            hit = VisualTreeHelper.GetParent(hit);
        }

        return (hit as ListViewItem)?.DataContext as FileRowViewModel;
    }

    // MARK: - Sorting

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is GridViewColumnHeader { Tag: string tag } && Enum.TryParse<SortField>(tag, out var field))
        {
            Vm?.SortBy(field);
        }
    }

    private void UpdateHeaders()
    {
        foreach (var column in Grid.Columns)
        {
            if (column.Header is not GridViewColumnHeader { Tag: string tag } header)
            {
                continue;
            }

            var title = tag switch
            {
                "Date" => L("Date Modified"),
                _ => L(tag),
            };
            var arrow = Vm is { } vm && vm.SortField.ToString() == tag ? (vm.SortAscending ? " " : " ") : string.Empty;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = title });
            if (arrow.Length > 0)
            {
                panel.Children.Add(new TextBlock { Text = arrow, FontFamily = new FontFamily(Glyphs.FontFamily), FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 1, 0, 0) });
            }

            header.Content = panel;
            header.HorizontalContentAlignment = tag == "Size" ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
    }

    // MARK: - Inline rename

    public void BeginRename()
    {
        if (List.SelectedItem is not FileRowViewModel row || Vm is not { } vm || vm.IsReadOnly)
        {
            return;
        }

        foreach (var other in vm.Rows.Where(r => r.IsRenaming))
        {
            other.IsRenaming = false;
        }

        row.EditName = row.Name;
        row.IsRenaming = true;
        List.ScrollIntoView(row);
    }

    private void OnRenameVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                var dot = box.Text.LastIndexOf('.');
                if (dot > 0 && box.DataContext is FileRowViewModel { IsDirectory: false })
                {
                    box.Select(0, dot);
                }
                else
                {
                    box.SelectAll();
                }
            }, DispatcherPriority.Input);
        }
    }

    private async void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileRowViewModel row })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitRenameAsync(row);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            row.IsRenaming = false;
            FocusRow(row);
        }
    }

    private async void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: FileRowViewModel { IsRenaming: true } row })
        {
            await CommitRenameAsync(row);
        }
    }

    private async Task CommitRenameAsync(FileRowViewModel row)
    {
        if (_committingRename || Vm is not { } vm)
        {
            return;
        }

        _committingRename = true;
        try
        {
            var name = row.EditName;
            row.IsRenaming = false;
            if (!string.Equals(name.Trim(), row.Name, StringComparison.Ordinal))
            {
                await Main.RenameAsync(vm, row, name);
            }
            else
            {
                FocusRow(row);
            }
        }
        finally
        {
            _committingRename = false;
        }
    }

    // MARK: - Path bar

    private async void OnCrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BreadcrumbSegment segment } && Vm is { } vm)
        {
            Main.ActiveSide = vm.Side;
            if (segment.IsLast)
            {
                BeginEditPath();
                return;
            }

            await vm.NavigateAsync(segment.Location);
            FocusList();
        }
    }

    /// <summary>Turns the crumbs into a text box holding the full path (Explorer's address bar).</summary>
    public void BeginEditPath()
    {
        if (Vm is not { } vm)
        {
            return;
        }

        PathEditor.Text = vm.IsCloud ? vm.CurrentPath : vm.CurrentPath;
        PathEditor.Visibility = Visibility.Visible;
        CrumbScroller.Visibility = Visibility.Hidden;
        PathEditor.Focus();
        PathEditor.SelectAll();
    }

    private void EndEditPath()
    {
        PathEditor.Visibility = Visibility.Collapsed;
        CrumbScroller.Visibility = Visibility.Visible;
    }

    private async void OnPathEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndEditPath();
            FocusList();
        }
        else if (e.Key == Key.Enter && Vm is { } vm)
        {
            e.Handled = true;
            var text = PathEditor.Text.Trim();
            EndEditPath();
            await GoToTypedPathAsync(vm, text);
        }
    }

    private void OnPathEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndEditPath();

    /// <summary>
    /// "Go to Folder": cloud panels stay in the cloud (absolute, or relative to the current
    /// folder); local panels expand %VARIABLES% and resolve relative paths. A file path opens
    /// its folder with the file selected.
    /// </summary>
    public async Task GoToTypedPathAsync(PanelViewModel vm, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (vm.IsCloud)
        {
            var target = text.StartsWith('/') ? text : Core.IO.CloudPath.Combine(vm.CurrentPath, text);
            await vm.NavigateAsync(vm.Location with { Path = Core.IO.CloudPath.Normalize(target) });
            FocusList();
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(text.Trim('"'));
        if (expanded.StartsWith('~'))
        {
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + expanded[1..];
        }

        if (!Path.IsPathRooted(expanded) && vm.CurrentPath.Length > 0)
        {
            expanded = Path.GetFullPath(Path.Combine(vm.CurrentPath, expanded));
        }

        if (Directory.Exists(expanded))
        {
            await vm.NavigateAsync(PanelLocation.Local(expanded));
            FocusList();
        }
        else if (File.Exists(expanded))
        {
            await vm.NavigateAsync(PanelLocation.Local(Path.GetDirectoryName(expanded)!), Path.GetFileName(expanded));
        }
        else
        {
            MessageDialog.Error(Window.GetWindow(this), L("Go to Folder…"), L("The folder \"{0}\" can't be found.", text));
        }
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Vm is { } vm)
        {
            vm.IsFilterVisible = false;
            FocusList();
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Enter)
        {
            FocusList();
            e.Handled = true;
        }
    }

    private async void OnReauthenticate(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            await Reauthentication.RunAsync(Window.GetWindow(this), Main.Services, vm);
        }
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.ProviderInvalidated();
            await vm.RefreshAsync();
        }
    }

    // MARK: - Context menu

    private void OnListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        Main.ActiveSide = Vm?.Side ?? Main.ActiveSide;
        var row = RowAt(e.GetPosition(List));
        if (row is null)
        {
            List.UnselectAll();
        }
        else if (!List.SelectedItems.Contains(row))
        {
            List.SelectedItems.Clear();
            List.SelectedItem = row;
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Vm is not { } vm)
        {
            e.Handled = true;
            return;
        }

        RowMenu.Items.Clear();
        foreach (var item in ContextMenus.ForPanel(Main, vm, this))
        {
            RowMenu.Items.Add(item);
        }

        if (RowMenu.Items.Count == 0)
        {
            e.Handled = true;
        }
    }

    // MARK: - Drag source

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        Main.ActiveSide = Vm?.Side ?? Main.ActiveSide;
        _dragStart = e.GetPosition(List);
        _dragCandidate = RowAt(_dragStart);
        if (_dragCandidate is null)
        {
            // A click on empty space clears the selection, as in Explorer.
            var hit = List.InputHitTest(_dragStart) as DependencyObject;
            if (hit is not null && FindAncestor<ScrollBar>(hit) is null && FindAncestor<GridViewColumnHeader>(hit) is null)
            {
                List.UnselectAll();
                List.Focus();
            }
        }
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null || Vm is not { Provider: { } provider } vm)
        {
            return;
        }

        var position = e.GetPosition(List);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var rows = List.SelectedItems.Cast<FileRowViewModel>().ToList();
        if (!rows.Contains(_dragCandidate))
        {
            rows = [_dragCandidate];
        }

        _dragCandidate = null;
        var items = rows.Where(r => r.Item.Role != CloudItemRole.VirtualContainer).Select(r => r.Item).ToList();
        if (items.Count == 0)
        {
            return;
        }

        var data = new DataObject();
        data.SetData(DragPayload.Format, new DragPayload(vm, provider, vm.Location, items));
        if (vm.IsLocal)
        {
            var paths = new System.Collections.Specialized.StringCollection();
            paths.AddRange(items.Select(i => i.Path).ToArray());
            data.SetFileDropList(paths);
        }

        DragDrop.DoDragDrop(List, data, DragDropEffects.Copy | DragDropEffects.Move);
        ClearDropHighlight();
    }

    // MARK: - Drop target

    /// <summary>Where a drop at the pointer would land: a folder row under it, or the folder on screen.</summary>
    private (PanelLocation Location, string Name, FileRowViewModel? Row)? DropTargetAt(DragEventArgs e)
    {
        if (Vm is not { } vm || vm.IsReadOnly)
        {
            return null;
        }

        var row = RowAt(e.GetPosition(List));
        if (row is { AcceptsDrops: true })
        {
            if (DragData(e) is { } payload && payload.Items.Any(i => i.Path == row.Path))
            {
                return null;
            }

            return (vm.Location with { Path = row.Path }, row.Name, row);
        }

        if (vm.IsLocal && vm.CurrentPath.Length == 0)
        {
            return null;
        }

        return (vm.Location, vm.Title, null);
    }

    private static DragPayload? DragData(DragEventArgs e)
        => e.Data.GetDataPresent(DragPayload.Format) ? e.Data.GetData(DragPayload.Format) as DragPayload : null;

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var target = DropTargetAt(e);
        var payload = DragData(e);
        var accepts = target is not null && (payload is not null || e.Data.GetDataPresent(DataFormats.FileDrop));

        // Dropping onto the folder the items already sit in is a no-op.
        if (accepts && payload is not null && target!.Value.Row is null && payload.SourceFolder == target.Value.Location)
        {
            accepts = false;
        }

        e.Effects = accepts ? DropEffect(e) : DragDropEffects.None;
        HighlightRow(accepts ? target!.Value.Row : null);

        // Spring-loaded folders: rest on a folder and it opens.
        if (accepts && target!.Value.Row is { } row)
        {
            if (!ReferenceEquals(row, _springRow))
            {
                _springRow = row;
                _springTimer.Stop();
                _springTimer.Start();
            }
        }
        else
        {
            _springRow = null;
            _springTimer.Stop();
        }

        AutoScroll(e.GetPosition(List));
    }

    private void OnListDragLeave(object sender, DragEventArgs e)
    {
        ClearDropHighlight();
    }

    private async void OnSpringTimer(object? sender, EventArgs e)
    {
        _springTimer.Stop();
        if (_springRow is { } row && Vm is { } vm)
        {
            _springRow = null;
            ClearDropHighlight();
            await vm.NavigateAsync(vm.Location with { Path = row.Path });
        }
    }

    private void HighlightRow(FileRowViewModel? row)
    {
        if (ReferenceEquals(row, _hoverRow))
        {
            return;
        }

        if (_hoverRow is not null)
        {
            _hoverRow.IsDropTarget = false;
        }

        _hoverRow = row;
        if (row is not null)
        {
            row.IsDropTarget = true;
        }
    }

    private void ClearDropHighlight()
    {
        HighlightRow(null);
        _springTimer.Stop();
        _springRow = null;
    }

    private void AutoScroll(Point position)
    {
        if (FindChild<ScrollViewer>(List) is not { } scroller)
        {
            return;
        }

        const double edge = 24;
        if (position.Y < edge + 24)
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - 8);
        }
        else if (position.Y > List.ActualHeight - edge)
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + 8);
        }
    }

    /// <summary>Explorer's modifiers: Shift moves, Ctrl copies; otherwise the toolbar mode or the prompt decides.</summary>
    private static DragDropEffects DropEffect(DragEventArgs e)
    {
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0 && (e.AllowedEffects & DragDropEffects.Move) != 0)
        {
            return DragDropEffects.Move;
        }

        return (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
    }

    private static bool? ForcedVerb(DragEventArgs e)
    {
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0)
        {
            return true;
        }

        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0)
        {
            return false;
        }

        return null;
    }

    private async void OnListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var target = DropTargetAt(e);
        ClearDropHighlight();
        if (target is not { } destination || Vm is not { } vm)
        {
            return;
        }

        await DropOntoAsync(e, destination.Location, destination.Name, vm.Side);
    }

    /// <summary>Shared by rows, the list background and breadcrumbs.</summary>
    private async Task DropOntoAsync(DragEventArgs e, PanelLocation destination, string destinationName, PanelSide side)
    {
        var forced = ForcedVerb(e);
        if (DragData(e) is { } payload)
        {
            if (payload.SourceFolder == destination)
            {
                return;
            }

            var verb = Main.ResolveDropVerb(payload.Items.Count, destinationName, forced);
            if (verb is { } isMove)
            {
                await Main.TransferAsync(payload.Provider, payload.Items, destination, isMove, side);
            }

            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
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

            var verb = Main.ResolveDropVerb(items.Count, destinationName, forced);
            if (verb is { } isMove)
            {
                await Main.TransferAsync(LocalFileProvider.Shared, items, destination, isMove, side);
            }
        }
    }

    // MARK: - Drops onto breadcrumbs

    private void OnCrumbDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var ok = sender is FrameworkElement { Tag: BreadcrumbSegment segment } &&
                 !(Vm?.IsReadOnly ?? true) &&
                 !(segment.Location.Path.Length == 0 && !segment.Location.IsCloud) &&
                 (DragData(e) is { } payload ? payload.SourceFolder != segment.Location : e.Data.GetDataPresent(DataFormats.FileDrop));
        e.Effects = ok ? DropEffect(e) : DragDropEffects.None;
        if (sender is Button button)
        {
            button.Background = ok ? (Brush)FindResource("RowSelectedBrush") : Brushes.Transparent;
        }
    }

    private void OnCrumbDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button)
        {
            button.ClearValue(BackgroundProperty);
        }
    }

    private async void OnCrumbDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is Button button)
        {
            button.ClearValue(BackgroundProperty);
        }

        if (sender is FrameworkElement { Tag: BreadcrumbSegment segment } && Vm is { } vm)
        {
            await DropOntoAsync(e, segment.Location, segment.Title, vm.Side);
        }
    }

    // MARK: - Helpers

    private static T? FindAncestor<T>(DependencyObject start)
        where T : DependencyObject
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    public string ShortcutText(KeyboardCommand command) => Main.Services.Keyboard.BindingFor(command)?.DisplayString ?? string.Empty;
}
