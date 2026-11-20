// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.Core.Models;
using FileFluss.Core.Search;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views.Search;

/// <summary>
/// The search window (macOS <c>SearchPopupView</c>): one field searching the active panel's
/// folder tree, every mounted drive and every connected account — and, when asked, the offline
/// index of unplugged drives and disconnected accounts. Results stream in per source, grouped
/// by source, filterable by source chip. A result opens in a panel; the window stays open so
/// more can be picked. The query and results survive closing and reopening the window.
/// </summary>
public sealed class SearchWindow : Window
{
    private static SearchWindow? _open;
    private static SearchSession? _session;

    private readonly MainViewModel _main;
    private readonly SearchSession _search;
    private readonly TextBox _query = new() { FontSize = 15, BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _placeholder = new() { FontSize = 15, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
    private readonly ProgressBar _spinner = new() { IsIndeterminate = true, Width = 40, Height = 3, Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
    private readonly Button _clear;
    private readonly CheckBox _offline = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _chips = new() { Margin = new Thickness(12, 6, 12, 6) };
    private readonly Border _chipBar;
    private readonly ListBox _results = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 380 };
    private readonly DispatcherTimer _refresh;

    private SearchWindow(Window owner, MainViewModel main)
    {
        _main = main;
        _search = _session ??= CreateSession(main);
        Owner = owner;
        Title = L("Search");
        Width = 720;
        Height = 540;
        MinWidth = 460;
        MinHeight = 300;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowFrames.Remember(this, "search", main.Services.Settings);

        _clear = GlyphButton(Glyphs.Cancel, L("Clear search"), () =>
        {
            _search.Clear();
            _query.Focus();
        });
        var close = GlyphButton(Glyphs.Cancel, L("Close (Esc)"), Close);
        close.Visibility = Visibility.Collapsed; // The title bar has a close button; Esc closes too.

        // Header: magnifier, field, status, clear.
        var field = new Grid();
        field.Children.Add(_placeholder);
        field.Children.Add(_query);
        _placeholder.Text = L("Search files across all sources...");
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        AutomationProperties.SetName(_query, L("Search"));
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var header = new DockPanel { Margin = new Thickness(14, 10, 10, 10) };
        var magnifier = SettingsGlyph(Glyphs.Search, 14);
        magnifier.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(magnifier, Dock.Left);
        header.Children.Add(magnifier);
        foreach (var right in new FrameworkElement[] { close, _clear, _status, _spinner })
        {
            DockPanel.SetDock(right, Dock.Right);
            header.Children.Add(right);
        }

        header.Children.Add(field);

        // "Show offline results".
        var offlineBar = new DockPanel { Margin = new Thickness(14, 4, 14, 6) };
        if (Application.Current.TryFindResource("ToggleSwitchStyle") is Style toggle)
        {
            _offline.Style = toggle;
        }

        _offline.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { SettingsGlyph(Glyphs.Offline, 12), new TextBlock { Text = L("Show offline results"), FontSize = 12, Margin = new Thickness(6, 0, 0, 0) } },
        };
        AutomationProperties.SetName(_offline, L("Show offline results"));
        _offline.IsChecked = _search.IncludeOffline;
        _offline.Checked += (_, _) => _search.IncludeOffline = true;
        _offline.Unchecked += (_, _) => _search.IncludeOffline = false;
        DockPanel.SetDock(_offline, Dock.Left);
        offlineBar.Children.Add(_offline);
        var hint = new TextBlock { Text = L("Includes indexed drives & disconnected accounts"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        offlineBar.Children.Add(hint);

        _chipBar = new Border { Child = _chips, Visibility = Visibility.Collapsed };

        // Results.
        _results.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _results.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        _results.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        _results.SetValue(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel);
        _results.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(RowPresenter)) };
        _results.ItemContainerStyle = RowContainerStyle();
        _results.MouseDoubleClick += (_, e) =>
        {
            if (ItemUnder(e.OriginalSource) is { } item)
            {
                OpenInPanel(item, _main.ActiveSide);
            }
        };
        _results.PreviewKeyDown += OnResultsKeyDown;
        _results.ContextMenuOpening += OnResultsContextMenu;
        _results.ContextMenu = new ContextMenu();
        AutomationProperties.SetName(_results, L("Results"));

        var body = new Grid();
        body.Children.Add(_results);
        body.Children.Add(_empty);

        var root = new DockPanel();
        foreach (var top in new FrameworkElement[] { header, Divider(), offlineBar, Divider(), _chipBar })
        {
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
        }

        root.Children.Add(body);
        Content = root;

        _query.Text = _search.SearchText;
        _query.TextChanged += (_, _) =>
        {
            _search.SearchText = _query.Text;
            UpdateHeader();
        };
        _query.PreviewKeyDown += OnQueryKeyDown;

        // Batches arrive many times a second on a big search; redraw at most every 150 ms.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            Rebuild();
        };
        _search.PropertyChanged += OnSearchChanged;
        Core.Localization.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            _search.PropertyChanged -= OnSearchChanged;
            Core.Localization.LanguageChanged -= OnLanguageChanged;
            _refresh.Stop();
            _open = null;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        Loaded += (_, _) =>
        {
            _query.Focus();
            _query.SelectAll();
        };

        Rebuild();
        UpdateHeader();
    }

    /// <summary>Opens the window, or brings it forward and focuses the field.</summary>
    public static void Show(Window owner, MainViewModel main)
    {
        if (_open is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            open._query.Focus();
            open._query.SelectAll();
            return;
        }

        _open = new SearchWindow(owner, main);
        _open.Show();
    }

    private static SearchSession CreateSession(MainViewModel main)
    {
        var services = main.Services;
        var session = new SearchSession(services.Search.Coordinator, () => Context(main), services.Search.Drives, services.Settings);
        session.OpenRequested += (_, e) => Navigate(main, e.Item, e.Side);
        return session;
    }

    /// <summary>
    /// Where the search starts: the active panel. A cloud panel also searches a local folder
    /// (macOS searched the panel's local folder whatever it showed) — the other panel's, or home.
    /// </summary>
    private static SearchContext Context(MainViewModel main)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var active = main.ActivePanel.Location;
        var other = main.OtherPanel.Location;
        string LocalFallback() => other.IsLocal && other.Path.Length > 0 ? other.Path : home;

        // Inside an archive the search starts where the archive sits.
        active = active.ArchiveContainer ?? active;

        // "This PC" or an unplugged drive have no folder tree to walk; search home instead.
        if (active.IsOfflineDrive || (active.IsLocal && active.Path.Length == 0))
        {
            active = PanelLocation.Local(LocalFallback());
        }

        return new SearchContext(active, main.Services.Accounts.Accounts.ToList())
        {
            LocalFolder = active.IsCloud ? LocalFallback() : null,
        };
    }

    // MARK: - Opening results

    private void OpenInPanel(SearchResultItem item, PanelSide side) => _search.OpenInPanel(item, side);

    private static async void Navigate(MainViewModel main, SearchResultItem item, PanelSide side)
    {
        var target = item.Navigation;
        PanelLocation? location = target.ToPanelLocation();
        if (target.Kind == SearchLocationKind.OfflineDrive && target.DriveId is { } driveId)
        {
            // Plugged back in since it was indexed? Then show the real folder.
            location = main.Services.Search.Drives.RootPathFor(driveId) is { } root
                ? PanelLocation.Local(Path.Combine(root, target.FolderPath.TrimStart('/').Replace('/', '\\')))
                : PanelLocation.OfflineDrive(driveId, target.FolderPath);
        }

        if (location is null)
        {
            return;
        }

        var panel = main.Panel(side);
        main.ActiveSide = side;
        var select = target.SelectPath is null ? null : item.Name;
        try
        {
            await panel.NavigateAsync(location, select);
            main.Shell.FocusPanel(side);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            TaskFailures.ShowFailure(e, L("Search"), main.Shell.Window);
        }
    }

    // MARK: - Updating

    private void OnSearchChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SearchSession.GroupedResults) or nameof(SearchSession.AvailableFilters):
                _refresh.Start();
                break;
            case nameof(SearchSession.Status) or nameof(SearchSession.StatusText):
                UpdateHeader();
                break;
            case nameof(SearchSession.IncludeOffline):
                _offline.IsChecked = _search.IncludeOffline;
                break;
            case nameof(SearchSession.SearchText) when _query.Text != _search.SearchText:
                _query.Text = _search.SearchText;
                break;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Title = L("Search");
        _placeholder.Text = L("Search files across all sources...");
        Rebuild(force: true);
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        var searching = _search.Status == SearchStatus.Searching;
        _spinner.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = _search.StatusText;
        _clear.Visibility = _query.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _placeholder.Visibility = _query.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_search.Status != SearchStatus.Searching)
        {
            _refresh.Start();
        }
    }

    /// <summary>What the list last showed; the 150 ms refresh skips a rebuild when nothing it depends on changed.</summary>
    private (Task Run, int Count, SearchSourceFilter Filter, SearchStatus Status, bool HasQuery)? _shown;

    private void Rebuild() => Rebuild(force: false);

    private void Rebuild(bool force)
    {
        var shown = (_search.Completion, _search.ResultCount, _search.SourceFilter, _search.Status, _search.SearchText.Length > 0);
        if (!force && _shown == shown)
        {
            return;
        }

        _shown = shown;
        RebuildChips();

        var selected = (_results.SelectedItem as ResultRow)?.Item.Id;
        var rows = new List<object>();
        foreach (var group in _search.GroupedResults)
        {
            rows.Add(new GroupRow(group));
            rows.AddRange(group.Items.Select(i => new ResultRow(i)));
        }

        _results.ItemsSource = rows;
        if (selected is not null && rows.OfType<ResultRow>().FirstOrDefault(r => r.Item.Id == selected) is { } again)
        {
            _results.SelectedItem = again;
        }

        // The empty states (macOS ContentUnavailableView): nothing typed, searching, no results.
        _empty.Children.Clear();
        if (_search.SearchText.Length == 0)
        {
            AddEmpty(Glyphs.Search, L("Search All Sources"), L("Search across local files and all connected cloud accounts"));
        }
        else if (rows.Count == 0 && _search.Status == SearchStatus.Searching)
        {
            AddEmpty(null, L("Searching..."), null);
        }
        else if (rows.Count == 0 && _search.Status == SearchStatus.Complete)
        {
            AddEmpty(Glyphs.Search, L("No Results for \"{0}\"", _search.SearchText), L("Check the spelling or try a new search."));
        }

        _empty.Visibility = _empty.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddEmpty(string? glyph, string title, string? message)
    {
        if (glyph is not null)
        {
            var icon = SettingsGlyph(glyph, 34);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 0, 12);
            _empty.Children.Add(icon);
        }

        _empty.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        if (message is not null)
        {
            var caption = new TextBlock { Text = message, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _empty.Children.Add(caption);
        }
    }

    /// <summary>The source chips; shown once results come from more than one source.</summary>
    private void RebuildChips()
    {
        var filters = _search.AvailableFilters;
        _chips.Children.Clear();
        _chipBar.Visibility = filters.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (filters.Count <= 1)
        {
            if (_search.SourceFilter != SearchSourceFilter.All)
            {
                _search.SourceFilter = SearchSourceFilter.All;
            }

            return;
        }

        foreach (var option in filters)
        {
            var selected = option.Filter == _search.SourceFilter;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (option.ProviderType is { } type && IconCache.Logo(type) is { } logo)
            {
                content.Children.Add(new Image { Source = logo, Width = 12, Height = 12, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            }

            content.Children.Add(new TextBlock { Text = option.Label, FontSize = 11.5, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal });
            var chip = new ToggleButton
            {
                Content = content,
                IsChecked = selected,
                Padding = new Thickness(10, 3, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                MinHeight = 0,
            };
            AutomationProperties.SetName(chip, option.Label);
            chip.Click += (_, _) =>
            {
                _search.SourceFilter = option.Filter;
                Rebuild();
            };
            _chips.Children.Add(chip);
        }
    }

    // MARK: - Keyboard and menus

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                // As on macOS: Enter searches again now instead of waiting for the typing pause.
                _search.Refresh();
                e.Handled = true;
                break;
            case Key.Down when FirstResult() is { } row:
                _results.SelectedItem = row;
                _results.UpdateLayout();
                (_results.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
        }
    }

    private ResultRow? FirstResult() => (_results.ItemsSource as IEnumerable<object>)?.OfType<ResultRow>().FirstOrDefault();

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _results.SelectedItem is ResultRow row)
        {
            OpenInPanel(row.Item, Keyboard.Modifiers == ModifierKeys.Shift ? _main.ActiveSide.Other() : _main.ActiveSide);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && ReferenceEquals(_results.SelectedItem, FirstResult()))
        {
            _query.Focus();
            e.Handled = true;
        }
    }

    private void OnResultsContextMenu(object sender, ContextMenuEventArgs e)
    {
        if (ItemUnder(e.OriginalSource) is not { } item)
        {
            e.Handled = true;
            return;
        }

        var menu = _results.ContextMenu!;
        menu.Items.Clear();
        menu.Items.Add(ContextMenus.Item(L("Open in Left Panel"), Glyphs.TwoPanes, null, () => OpenInPanel(item, PanelSide.Left)));
        menu.Items.Add(ContextMenus.Item(L("Open in Right Panel"), Glyphs.TwoPanes, null, () => OpenInPanel(item, PanelSide.Right)));
        if (item.Kind == SearchResultKind.Local)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(ContextMenus.Item(L("Show in Explorer"), Glyphs.OpenFolder, null, () => Native.ShellFileOperations.RevealInExplorer([item.Path])));
            menu.Items.Add(ContextMenus.Item(L("Copy Path"), Glyphs.Copy, null, () => Clipboard.SetText(item.Path)));
        }
    }

    private SearchResultItem? ItemUnder(object source)
    {
        for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element))
        {
            if (element is ListBoxItem { DataContext: ResultRow row })
            {
                _results.SelectedItem = row;
                return row.Item;
            }
        }

        return null;
    }

    // MARK: - Building blocks

    private static Border Divider()
    {
        var line = new Border { Height = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return line;
    }

    private static TextBlock SettingsGlyph(string glyph, double size)
    {
        var text = new TextBlock { Text = glyph, FontFamily = new FontFamily(Glyphs.FontFamily), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return text;
    }

    private static Button GlyphButton(string glyph, string name, Action click)
    {
        var button = new Button { Content = glyph, FontFamily = new FontFamily(Glyphs.FontFamily), FontSize = 10, Width = 26, Height = 26, Padding = new Thickness(0), ToolTip = name };
        if (Application.Current.TryFindResource("GlyphButton") is Style style)
        {
            button.Style = style;
        }

        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private static Style RowContainerStyle()
    {
        var style = new Style(typeof(ListBoxItem), Application.Current.TryFindResource(typeof(ListBoxItem)) as Style);
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(AutomationProperties.NameProperty, new System.Windows.Data.Binding(nameof(ResultRow.AccessibleName))));
        var header = new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(GroupRow.IsHeader)), Value = true };
        header.Setters.Add(new Setter(FocusableProperty, false));
        header.Setters.Add(new Setter(IsHitTestVisibleProperty, false));
        style.Triggers.Add(header);
        return style;
    }

    /// <summary>A section header in the results list.</summary>
    public sealed record GroupRow(SearchResultGroup Group)
    {
        public bool IsHeader => true;

        public string AccessibleName => $"{Group.Title} ({Group.Items.Count})";
    }

    /// <summary>One result in the list.</summary>
    public sealed record ResultRow(SearchResultItem Item)
    {
        public bool IsHeader => false;

        public string AccessibleName => $"{Item.Name}, {Item.LocationDescription}";
    }

    /// <summary>Draws whichever row its data context is: a section header or a result (macOS SearchGroupHeader / SearchResultRow).</summary>
    private sealed class RowPresenter : ContentControl
    {
        public RowPresenter()
        {
            Focusable = false;
            DataContextChanged += (_, _) => Content = DataContext switch
            {
                GroupRow group => GroupHeader(group.Group),
                ResultRow row => Result(row.Item),
                _ => null,
            };
        }

        private static FrameworkElement GroupHeader(SearchResultGroup group)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 10, 5) };
            if (group.ProviderType is { } type && IconCache.Logo(type) is { } logo)
            {
                panel.Children.Add(new Image { Source = logo, Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            }
            else
            {
                var glyph = SettingsGlyph(group.Kind == SearchGroupKind.Offline ? Glyphs.Offline : Glyphs.HardDrive, 12);
                glyph.Margin = new Thickness(0, 0, 6, 0);
                panel.Children.Add(glyph);
            }

            var title = new TextBlock { Text = group.Title, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            var count = new TextBlock { Text = $"({group.Items.Count})", FontSize = 11, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            count.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            panel.Children.Add(title);
            panel.Children.Add(count);
            var border = new Border { Child = panel, Margin = new Thickness(-4, 4, -4, 0) };
            border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            return border;
        }

        private static FrameworkElement Result(SearchResultItem item)
        {
            var grid = new Grid { Margin = new Thickness(8, 4, 8, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });

            var icon = new Image
            {
                Width = 20,
                Height = 20,
                Opacity = item.IsOffline ? 0.55 : 1,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            // Local folders' own icons are read off the UI thread; the generic one shows meanwhile.
            icon.Source = IconCache.ForRow(item.Item, item.Kind == SearchResultKind.Local, isDrive: false, 20, loaded => icon.Source = loaded).Icon;
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            grid.Children.Add(icon);

            var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
            var name = new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontStyle = item.IsOffline ? FontStyles.Italic : FontStyles.Normal };
            if (item.IsOffline)
            {
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            }

            nameLine.Children.Add(name);
            if (item.IsOffline)
            {
                var badgeText = new TextBlock { Text = L("Offline"), FontSize = 9.5, FontWeight = FontWeights.Medium };
                badgeText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                var badge = new Border { Child = badgeText, CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                badge.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorTertiaryBrush");
                nameLine.Children.Add(badge);
            }

            var location = new TextBlock { Text = item.LocationDescription, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
            location.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, ToolTip = item.Path };
            text.Children.Add(nameLine);
            text.Children.Add(location);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var size = new TextBlock { Text = item.IsDirectory ? string.Empty : item.FormattedSize, FontSize = 11.5, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            size.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Grid.SetColumn(size, 2);
            grid.Children.Add(size);

            var date = new TextBlock
            {
                Text = item.Item.HasKnownModificationDate ? item.ModificationDate.LocalDateTime.ToString("g", Culture) : string.Empty,
                FontSize = 11.5,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            date.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Grid.SetColumn(date, 3);
            grid.Children.Add(date);
            return grid;
        }
    }
}
