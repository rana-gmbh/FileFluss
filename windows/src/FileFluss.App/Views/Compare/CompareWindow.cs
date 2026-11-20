// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.App.Views.Dialogs;
using FileFluss.App.Views.Sync;
using FileFluss.Core.Compare;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Transfers;
using FileFluss.Native;
using static FileFluss.App.Views.Sync.WindowKit;
using static FileFluss.Core.Localization;

using Localization = FileFluss.Core.Localization;

namespace FileFluss.App.Views.Compare;

/// <summary>
/// Compare Folders (macOS <c>CompareFoldersView</c>): the two panels' folders, read
/// recursively and matched entry by entry — different, only left, only right, identical —
/// as a collapsible tree with filter chips. The folders are snapshotted when the comparison
/// starts, so browsing the panels changes nothing here until Compare Again (or the toolbar's
/// Compare) re-reads the panels' current folders.
///
/// <para>Beyond the macOS window, selected entries can be copied to the other side or
/// deleted from one side; the comparison refreshes once that is done. All comparison logic
/// lives in <see cref="CompareSession"/>.</para>
/// </summary>
public sealed class CompareWindow : Window
{
    private const string FrameKey = "compare";

    /// <summary>Above this many rows changing at once, the list is rebuilt rather than patched.</summary>
    private const int IncrementalLimit = 400;

    private static readonly Dictionary<Window, CompareWindow> Open = [];

    private readonly MainViewModel _vm;
    private readonly CompareSession _session = new();
    private readonly HashSet<TransferProgress> _pendingCopies = [];

    private readonly TextBlock _heading = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = Secondary(size: 13);
    private readonly EndpointCard _leftCard = new();
    private readonly EndpointCard _rightCard = new();
    private readonly CheckBox _compareDates = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _compareAgain = GlyphButton(Glyphs.Refresh, string.Empty, "CompareAgainButton");

    private readonly ContentControl _body = new() { Focusable = false };
    private readonly FrameworkElement _loadingView;
    private readonly TextBlock _loadingText = Secondary(size: 13);
    private readonly Button _cancelLoading = new() { MinWidth = 96, Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly FrameworkElement _errorView;
    private readonly TextBlock _errorText = Secondary(size: 13);
    private readonly Button _tryAgain = new() { MinWidth = 96, Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly FrameworkElement _placeholderView;
    private readonly TextBlock _placeholderTitle = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _placeholderText = Secondary(size: 13);
    private readonly Button _idleCompare = new() { MinWidth = 96, Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly FrameworkElement _resultsView;
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<CompareFilter, RadioButton> _chipButtons = [];
    private readonly ListBox _list = new();
    private readonly TextBlock _nothingMatches = Secondary(size: 13);
    private readonly Button _copyToLeft = GlyphButton(Glyphs.Back, string.Empty, "CompareCopyToLeftButton");
    private readonly Button _copyToRight = GlyphButton(Glyphs.Forward, string.Empty, "CompareCopyToRightButton");
    private readonly Button _delete = GlyphButton(Glyphs.Delete, string.Empty, "CompareDeleteButton");

    private ObservableCollection<CompareRowItem> _items = [];
    private PanelSnapshot? _leftSide;
    private PanelSnapshot? _rightSide;
    private bool _updating;
    private bool _busy;
    private bool _closed;
    private int _refreshQueued;

    private CompareWindow(MainViewModel vm)
    {
        _vm = vm;
        Width = 680;
        Height = 600;
        MinWidth = 540;
        MinHeight = 420;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "CompareWindow");
        WindowFrames.Attach(this, vm.Services.Settings, FrameKey);

        _loadingView = BuildLoadingView();
        _errorView = BuildErrorView();
        _placeholderView = BuildPlaceholderView();
        _resultsView = BuildResultsView();
        Content = Build();
        ApplyTexts();

        _session.PropertyChanged += OnSessionChanged;
        vm.Transfers.TransferCompleted += OnTransferCompleted;
        Localization.LanguageChanged += OnLanguageChanged;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                e.Handled = true;
                Close();
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _session.PropertyChanged -= OnSessionChanged;
            vm.Transfers.TransferCompleted -= OnTransferCompleted;
            Localization.LanguageChanged -= OnLanguageChanged;
            _session.Cancel();
        };
    }

    /// <summary>
    /// Opens Compare Folders for the two panels' current folders. When it is already open it
    /// comes forward and compares again against the panels' current folders, as the macOS
    /// toolbar button does.
    /// </summary>
    public static void Show(Window owner, MainViewModel vm)
    {
        if (Open.TryGetValue(owner, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            existing.CompareCurrentFolders();
            return;
        }

        var window = new CompareWindow(vm) { Owner = owner };
        Open[owner] = window;
        window.Closed += (_, _) => Open.Remove(owner);
        window.Show();
        window.Activate();
        window.CompareCurrentFolders();
    }

    // MARK: - Layout

    private FrameworkElement Build()
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        _summary.TextWrapping = TextWrapping.NoWrap;
        _summary.TextTrimming = TextTrimming.CharacterEllipsis;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.Margin = new Thickness(16, 2, 0, 0);
        AutomationProperties.SetAutomationId(_summary, "CompareSummary");
        DockPanel.SetDock(_heading, Dock.Left);
        header.Children.Add(_heading);
        _summary.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(_summary);

        var endpoints = new Grid();
        endpoints.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        endpoints.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        endpoints.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var arrows = Glyph(Glyphs.Compare, 18);
        arrows.HorizontalAlignment = HorizontalAlignment.Center;
        arrows.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetColumn(arrows, 1);
        Grid.SetColumn(_rightCard, 2);
        endpoints.Children.Add(_leftCard);
        endpoints.Children.Add(arrows);
        endpoints.Children.Add(_rightCard);

        var options = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetAutomationId(_compareDates, "CompareDatesCheckBox");
        _compareDates.Checked += (_, _) => SetCompareDates(true);
        _compareDates.Unchecked += (_, _) => SetCompareDates(false);
        _compareAgain.Click += (_, _) => CompareCurrentFolders();
        DockPanel.SetDock(_compareAgain, Dock.Right);
        options.Children.Add(_compareAgain);
        options.Children.Add(_compareDates);

        var root = new DockPanel { Margin = new Thickness(20, 16, 20, 18) };
        var top = new StackPanel { Children = { header, endpoints, options, Divider(new Thickness(0, 12, 0, 12)) } };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_body);
        return root;
    }

    private FrameworkElement BuildLoadingView()
    {
        _loadingText.HorizontalAlignment = HorizontalAlignment.Center;
        _loadingText.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetAutomationId(_cancelLoading, "CompareCancelButton");
        _cancelLoading.Click += (_, _) => _session.Cancel();
        return Centered(new ProgressBar { IsIndeterminate = true, Width = 220, Height = 4 }, _loadingText, _cancelLoading);
    }

    private FrameworkElement BuildErrorView()
    {
        var icon = Glyph(Glyphs.Warning, 36);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Foreground = CompareColors.For(FolderCompareStatus.OnlyRight);
        _errorText.TextAlignment = TextAlignment.Center;
        _errorText.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetAutomationId(_errorText, "CompareError");
        AutomationProperties.SetAutomationId(_tryAgain, "CompareTryAgainButton");
        _tryAgain.Click += (_, _) => CompareCurrentFolders();
        return Centered(icon, _errorText, _tryAgain);
    }

    private FrameworkElement BuildPlaceholderView()
    {
        var icon = Glyph(Glyphs.TwoPanes, 36);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 0, 10);
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _placeholderText.TextAlignment = TextAlignment.Center;
        _placeholderText.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetAutomationId(_idleCompare, "ComparePlaceholderCompareButton");
        _idleCompare.Click += (_, _) => CompareCurrentFolders();
        return Centered(icon, _placeholderTitle, _placeholderText, _idleCompare);
    }

    private static FrameworkElement Centered(params FrameworkElement[] children)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 440, Margin = new Thickness(24, 0, 24, 24) };
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return stack;
    }

    private FrameworkElement BuildResultsView()
    {
        foreach (var filter in CompareFilterExtensions.All)
        {
            var chip = new RadioButton { GroupName = "CompareFilter", Style = ChipStyle(), Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(chip, "CompareFilter" + filter);
            var captured = filter;
            chip.Checked += (_, _) =>
            {
                if (!_updating)
                {
                    _session.Filter = captured;
                }
            };
            _chipButtons[filter] = chip;
            _chips.Children.Add(chip);
        }

        var chipScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _chips,
            Focusable = false,
            Margin = new Thickness(0, 0, 0, 10),
        };

        _list.ItemsSource = _items;
        _list.SelectionMode = SelectionMode.Extended;
        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        _list.Padding = new Thickness(0, 4, 0, 4);
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _list.ItemContainerStyle = RowContainerStyle();
        _list.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(CompareRowView)) };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        AutomationProperties.SetAutomationId(_list, "CompareList");
        _list.SelectionChanged += (_, _) => UpdateActions();
        _list.PreviewKeyDown += OnListKeyDown;
        _list.MouseDoubleClick += OnListDoubleClick;
        _list.ContextMenuOpening += OnContextMenuOpening;
        _list.ContextMenu = new ContextMenu();

        _nothingMatches.HorizontalAlignment = HorizontalAlignment.Center;
        _nothingMatches.VerticalAlignment = VerticalAlignment.Top;
        _nothingMatches.Margin = new Thickness(0, 30, 0, 0);
        _nothingMatches.IsHitTestVisible = false;

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = new Grid { Children = { _list, _nothingMatches } },
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        _copyToLeft.Click += async (_, _) => await CopyAsync(PanelSide.Left);
        _copyToRight.Click += async (_, _) => await CopyAsync(PanelSide.Right);
        _delete.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = _delete, Placement = PlacementMode.Top };
            menu.Items.Add(DeleteItem(PanelSide.Left));
            menu.Items.Add(DeleteItem(PanelSide.Right));
            menu.IsOpen = true;
        };
        _copyToRight.Margin = new Thickness(8, 0, 0, 0);
        var actions = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(_delete, Dock.Right);
        actions.Children.Add(_delete);
        actions.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _copyToLeft, _copyToRight } });

        var results = new DockPanel();
        DockPanel.SetDock(chipScroller, Dock.Top);
        DockPanel.SetDock(actions, Dock.Bottom);
        results.Children.Add(chipScroller);
        results.Children.Add(actions);
        results.Children.Add(card);
        return results;
    }

    /// <summary>A filter chip: a capsule, accent-tinted when chosen (macOS filter chips).</summary>
    private static Style ChipStyle()
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        chrome.SetValue(Border.PaddingProperty, new Thickness(11, 4, 11, 5));
        chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        chrome.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(content);

        var template = new ControlTemplate(typeof(RadioButton)) { VisualTree = chrome };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SubtleFillColorSecondaryBrush"), "Chrome"));
        var chosen = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        chosen.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RowSelectedBrush"), "Chrome"));
        chosen.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("AccentTextBrush"), "Chrome"));
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "Chrome"));
        focused.Setters.Add(new Setter(Border.PaddingProperty, new Thickness(10, 3, 10, 4), "Chrome"));
        template.Triggers.Add(hover);
        template.Triggers.Add(chosen);
        template.Triggers.Add(focused);

        var style = new Style(typeof(RadioButton));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextFillColorPrimaryBrush")));
        return style;
    }

    /// <summary>Compact rounded rows with Explorer's hover and accent selection, like the file panels.</summary>
    private static Style RowContainerStyle()
    {
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        chrome.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        chrome.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        chrome.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = chrome };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SubtleFillColorSecondaryBrush"), "Chrome"));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RowSelectedInactiveBrush"), "Chrome"));
        var active = new MultiTrigger();
        active.Conditions.Add(new System.Windows.Condition(ListBoxItem.IsSelectedProperty, true));
        active.Conditions.Add(new System.Windows.Condition(Selector.IsSelectionActiveProperty, true));
        active.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RowSelectedBrush"), "Chrome"));
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("RowFocusBrush"), "Chrome"));
        template.Triggers.Add(hover);
        template.Triggers.Add(selected);
        template.Triggers.Add(active);
        template.Triggers.Add(focused);

        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextFillColorPrimaryBrush")));
        return style;
    }

    private void ApplyTexts()
    {
        Title = L("Compare Folders");
        _heading.Text = L("Compare Folders");
        _compareDates.Content = L("Compare Date");
        _compareDates.ToolTip = CompareSession.CompareDatesHelp;
        AutomationProperties.SetHelpText(_compareDates, CompareSession.CompareDatesHelp);
        SetGlyphCaption(_compareAgain, Glyphs.Refresh, L("Compare Again"));
        _compareAgain.ToolTip = CompareSession.CompareAgainHelp;
        _loadingText.Text = CompareSession.LoadingText;
        _cancelLoading.Content = L("Cancel");
        _tryAgain.Content = L("Try Again");
        _idleCompare.Content = L("Compare Again");
        _nothingMatches.Text = CompareSession.NothingMatchesText;
        SetGlyphCaption(_copyToLeft, Glyphs.Back, CopyCaption(PanelSide.Left));
        SetGlyphCaption(_copyToRight, Glyphs.Forward, CopyCaption(PanelSide.Right));
        SetGlyphCaption(_delete, Glyphs.Delete, L("Delete"));
        AutomationProperties.SetName(_list, L("Compare Folders"));
        Refresh();
    }

    private static string SideName(PanelSide side) => side == PanelSide.Left ? L("Left") : L("Right");

    private static string CopyCaption(PanelSide destination) => L("Copy to {0} Panel", SideName(destination));

    private static string DeleteCaption(PanelSide side) => L("Delete from {0} Panel", SideName(side));

    // MARK: - Comparing

    /// <summary>Snapshots both panels' folders and compares them (Compare Again, Try Again, the toolbar).</summary>
    private void CompareCurrentFolders()
    {
        _leftSide = PanelSnapshot.Of(_vm.Left);
        _rightSide = PanelSnapshot.Of(_vm.Right);
        _pendingCopies.Clear();
        _ = _session.CompareAsync(_leftSide.UsableEndpoint, _rightSide.UsableEndpoint);
    }

    private void SetCompareDates(bool on)
    {
        if (!_updating)
        {
            _session.CompareDates = on;
        }
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            Refresh();
        }
        else if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _refreshQueued = 0;
                Refresh();
            });
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        ApplyTexts();
        foreach (var item in _items)
        {
            item.Invalidate();
        }
    });

    private void Refresh()
    {
        if (_heading.Text.Length == 0)
        {
            return; // still building
        }

        _updating = true;
        try
        {
            var session = _session;
            var summary = session.SummaryText;
            _summary.Text = summary ?? string.Empty;
            _summary.ToolTip = summary;
            _leftCard.Show(L("Left"), _leftSide);
            _rightCard.Show(L("Right"), _rightSide);
            _compareDates.IsChecked = session.CompareDates;
            _compareAgain.IsEnabled = !session.IsCalculating && !_busy;

            FrameworkElement view;
            if (session.IsCalculating)
            {
                view = _loadingView;
            }
            else if (session.ErrorMessage is { } error)
            {
                _errorText.Text = error;
                view = _errorView;
            }
            else if (session.Result is not null)
            {
                view = _resultsView;
            }
            else
            {
                // No folders to compare — or a comparison cancelled before it had a result.
                var hasEndpoints = session.HasEndpoints;
                _placeholderTitle.Text = hasEndpoints ? L("Compare Folders") : CompareSession.PlaceholderTitle;
                _placeholderText.Text = hasEndpoints ? CompareSession.CompareAgainHelp : CompareSession.PlaceholderDescription;
                view = _placeholderView;
            }

            if (!ReferenceEquals(_body.Content, view))
            {
                _body.Content = view;
            }

            foreach (var chip in session.FilterChips)
            {
                var button = _chipButtons[chip.Filter];
                var count = Secondary($"({chip.Count:N0})", 13);
                count.Margin = new Thickness(4, 0, 0, 0);
                count.TextWrapping = TextWrapping.NoWrap;
                button.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = chip.Title }, count } };
                AutomationProperties.SetName(button, $"{chip.Title} ({chip.Count})");
                button.IsChecked = chip.IsSelected;
            }

            UpdateRows(session.Rows);
            _nothingMatches.Visibility = session.NothingMatchesFilter ? Visibility.Visible : Visibility.Collapsed;
            UpdateActions();
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>
    /// Brings the list in line with <paramref name="rows"/>, keeping the items of rows that
    /// stay (so selection and scrolling survive): the common head and tail are updated in
    /// place and only the middle is replaced — which is exactly what opening or closing a
    /// folder changes.
    /// </summary>
    private void UpdateRows(IReadOnlyList<CompareRow> rows)
    {
        var items = _items;
        var head = 0;
        while (head < items.Count && head < rows.Count && items[head].Path == rows[head].Node.Path)
        {
            head++;
        }

        var tail = 0;
        while (tail < items.Count - head && tail < rows.Count - head &&
               items[items.Count - 1 - tail].Path == rows[rows.Count - 1 - tail].Node.Path)
        {
            tail++;
        }

        var removed = items.Count - head - tail;
        var added = rows.Count - head - tail;
        if (removed + added > IncrementalLimit)
        {
            Rebuild(rows);
            return;
        }

        for (var i = 0; i < head; i++)
        {
            items[i].Row = rows[i];
        }

        for (var i = 0; i < tail; i++)
        {
            items[items.Count - 1 - i].Row = rows[rows.Count - 1 - i];
        }

        for (var i = 0; i < removed; i++)
        {
            items.RemoveAt(head);
        }

        for (var i = 0; i < added; i++)
        {
            items.Insert(head + i, new CompareRowItem(rows[head + i]));
        }
    }

    /// <summary>A wholesale change (a filter switch, a new result): one reset instead of thousands of events.</summary>
    private void Rebuild(IReadOnlyList<CompareRow> rows)
    {
        var existing = new Dictionary<string, CompareRowItem>(StringComparer.Ordinal);
        foreach (var item in _items)
        {
            existing[item.Path] = item;
        }

        var selected = _list.SelectedItems.Cast<CompareRowItem>().Select(i => i.Path).ToHashSet(StringComparer.Ordinal);
        var next = new List<CompareRowItem>(rows.Count);
        foreach (var row in rows)
        {
            if (existing.Remove(row.Node.Path, out var item))
            {
                item.Row = row;
            }
            else
            {
                item = new CompareRowItem(row);
            }

            next.Add(item);
        }

        _items = new ObservableCollection<CompareRowItem>(next);
        _list.ItemsSource = _items;
        foreach (var item in _items.Where(i => selected.Contains(i.Path)))
        {
            _list.SelectedItems.Add(item);
        }
    }

    internal void ToggleGroup(CompareRowItem item)
    {
        if (item.Row.IsGroup)
        {
            _session.ToggleExpanded(item.Row.Node);
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is not ListBoxItem { DataContext: CompareRowItem item })
        {
            return;
        }

        var row = item.Row;
        switch (e.Key)
        {
            case Key.Right when row.IsGroup && !row.IsExpanded:
            case Key.Left when row.IsGroup && row.IsExpanded:
            case Key.Enter when row.IsGroup && Keyboard.Modifiers == ModifierKeys.None:
                ToggleGroup(item);
                e.Handled = true;
                break;
            case Key.Left when row.Depth > 0:
                // To the folder this entry sits in, as in Explorer's tree.
                var index = _items.IndexOf(item);
                for (var i = index - 1; i >= 0; i--)
                {
                    if (_items[i].Row.Depth == row.Depth - 1)
                    {
                        _list.SelectedItem = _items[i];
                        _list.ScrollIntoView(_items[i]);
                        (_list.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem)?.Focus();
                        break;
                    }
                }

                e.Handled = true;
                break;
            case Key.Delete when _delete.IsEnabled:
                // Which side is ambiguous; offer the choice.
                _delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                e.Handled = true;
                break;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(_list, source) is ListBoxItem { DataContext: CompareRowItem item })
        {
            ToggleGroup(item);
            e.Handled = true;
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = _list.ContextMenu!;
        menu.Items.Clear();
        if (_list.SelectedItems.Count == 0)
        {
            e.Handled = true;
            return;
        }

        menu.Items.Add(CopyItem(PanelSide.Left));
        menu.Items.Add(CopyItem(PanelSide.Right));
        menu.Items.Add(new Separator());
        menu.Items.Add(DeleteItem(PanelSide.Left));
        menu.Items.Add(DeleteItem(PanelSide.Right));
    }

    private MenuItem CopyItem(PanelSide destination)
    {
        var item = new MenuItem
        {
            Header = CopyCaption(destination),
            Icon = Glyph(destination == PanelSide.Left ? Glyphs.Back : Glyphs.Forward),
            IsEnabled = CanCopy(destination),
        };
        item.Click += async (_, _) => await CopyAsync(destination);
        return item;
    }

    private MenuItem DeleteItem(PanelSide side)
    {
        var item = new MenuItem { Header = DeleteCaption(side), Icon = Glyph(Glyphs.Delete), IsEnabled = CanDelete(side) };
        AutomationProperties.SetAutomationId(item, "CompareDeleteFrom" + side);
        item.Click += async (_, _) => await DeleteAsync(side);
        return item;
    }

    // MARK: - Copy and delete

    private PanelSnapshot? Side(PanelSide side) => side == PanelSide.Left ? _leftSide : _rightSide;

    /// <summary>Whether the entry is present on <paramref name="side"/>. A folder only implied by its contents is there when something beneath it is.</summary>
    private static bool ExistsOn(CompareTreeNode node, PanelSide side)
    {
        if (node.Entry is { } entry)
        {
            return entry.Status != (side == PanelSide.Left ? FolderCompareStatus.OnlyRight : FolderCompareStatus.OnlyLeft);
        }

        var counts = node.DescendantCounts;
        return counts.Different + counts.Identical + (side == PanelSide.Left ? counts.OnlyLeft : counts.OnlyRight) > 0;
    }

    /// <summary>The selected entries, leaving out any whose folder is selected too (copying or deleting the folder covers them).</summary>
    private List<CompareTreeNode> TopmostSelection()
    {
        var nodes = _list.SelectedItems.Cast<CompareRowItem>().Select(i => i.Row.Node).ToList();
        var folders = nodes.Where(n => n.IsDirectory).Select(n => n.Path + "/").ToList();
        return nodes.Where(n => !folders.Any(f => n.Path.StartsWith(f, StringComparison.Ordinal))).ToList();
    }

    private bool Writable(PanelSide side) => Side(side) is { UsableEndpoint: { IsOffline: false }, IsReadOnly: false };

    private bool CanCopy(PanelSide destination)
        => !_busy && !_session.IsCalculating && _session.Result is not null &&
           Side(destination.Other())?.UsableEndpoint is not null && Writable(destination) &&
           TopmostSelection().Any(n => ExistsOn(n, destination.Other()));

    private bool CanDelete(PanelSide side)
        => !_busy && !_session.IsCalculating && _session.Result is not null && Writable(side) &&
           TopmostSelection().Any(n => ExistsOn(n, side));

    private void UpdateActions()
    {
        var hasResult = _session.Result is not null;
        _copyToLeft.IsEnabled = hasResult && CanCopy(PanelSide.Left);
        _copyToRight.IsEnabled = hasResult && CanCopy(PanelSide.Right);
        _delete.IsEnabled = hasResult && (CanDelete(PanelSide.Left) || CanDelete(PanelSide.Right));
    }

    /// <summary>
    /// Copies the selected entries present on the other side into the same relative folders
    /// on <paramref name="destination"/>, creating folders that only exist on the source.
    /// Runs through the app's ordinary transfer path (space check, conflict prompt, progress
    /// in the destination panel's sidebar); compares again once every copy has finished.
    /// </summary>
    private async Task CopyAsync(PanelSide destination)
    {
        if (!CanCopy(destination) || _session.Tree is not { } tree ||
            Side(destination.Other()) is not { UsableEndpoint: { } source } || Side(destination) is not { UsableEndpoint: { } target } targetSide)
        {
            return;
        }

        var sourceSide = destination.Other();
        var nodes = TopmostSelection().Where(n => ExistsOn(n, sourceSide)).ToList();
        var before = _vm.Transfers.All.ToHashSet();
        SetBusy(true);
        try
        {
            foreach (var group in nodes.GroupBy(n => ParentOf(n.Path)))
            {
                if (!await EnsureFolderAsync(tree, target, group.Key, destination))
                {
                    break;
                }

                var items = group.Select(n =>
                {
                    var entry = n.Entry;
                    var path = source.Resolve(n.Path);
                    var size = sourceSide == PanelSide.Left ? entry?.LeftSize : entry?.RightSize;
                    var date = sourceSide == PanelSide.Left ? entry?.LeftDate : entry?.RightDate;
                    return new CloudFileItem(path, n.Name, path, n.IsDirectory, n.IsDirectory ? 0 : size ?? 0, date ?? CloudFileItem.UnknownDate);
                }).ToList();
                await _vm.TransferAsync(source.Provider, items, targetSide.LocationOf(group.Key), isMove: false, destination);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            TaskFailures.ShowFailure(e, CopyCaption(destination), this);
        }
        finally
        {
            SetBusy(false);
        }

        if (_closed)
        {
            // The copies run on in the panels' transfer lists; there is nothing to compare again.
            return;
        }

        // The transfers just started are ours; compare again when the last one finishes.
        foreach (var transfer in _vm.Transfers.All.Where(t => !before.Contains(t) && t.IsRunning))
        {
            _pendingCopies.Add(transfer);
        }

        if (_pendingCopies.Count == 0 && _vm.Transfers.All.Any(t => !before.Contains(t)))
        {
            _ = _session.CompareAgainAsync();
        }
    }

    /// <summary>Creates the folders leading to <paramref name="relativeFolder"/> that the destination lacks.</summary>
    private async Task<bool> EnsureFolderAsync(CompareTreeNode tree, Core.Sync.SyncEndpoint target, string relativeFolder, PanelSide destination)
    {
        CompareTreeNode? node = tree;
        var path = string.Empty;
        foreach (var part in relativeFolder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path.Length == 0 ? part : path + "/" + part;
            node = node?.Children.FirstOrDefault(c => c.Name == part);
            if (node is not null && ExistsOn(node, destination))
            {
                continue;
            }

            try
            {
                var folder = target.Resolve(path);
                await Task.Run(() => target.Provider.CreateDirectoryAsync(folder));
            }
            catch (Exception e)
            {
                MessageDialog.Error(this, CopyCaption(destination), e.Message);
                return false;
            }
        }

        return true;
    }

    private static string ParentOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    private void OnTransferCompleted(object? sender, TransferProgress transfer)
    {
        if (_pendingCopies.Remove(transfer) && _pendingCopies.Count == 0)
        {
            _ = _session.CompareAgainAsync();
        }
    }

    /// <summary>
    /// Deletes the selected entries present on <paramref name="side"/>, after confirming: to
    /// the Recycle Bin where the drive has one, otherwise permanently. Compares again after.
    /// </summary>
    private async Task DeleteAsync(PanelSide side)
    {
        if (!CanDelete(side) || Side(side) is not { UsableEndpoint: { } endpoint } snapshot)
        {
            return;
        }

        var nodes = TopmostSelection().Where(n => ExistsOn(n, side)).ToList();
        var paths = nodes.Select(n => endpoint.Resolve(n.Path)).ToList();
        var local = endpoint.Provider.IsLocal;
        var recycle = local && LocalFileProvider.RecycleHandler is not null && paths.All(ShellFileOperations.HasRecycleBin);
        var label = nodes.Count == 1 ? $"\"{nodes[0].Name}\"" : L("{0} items", nodes.Count);
        string title, message, verb;
        if (recycle)
        {
            title = verb = L("Move to Recycle Bin");
            message = nodes.Count == 1
                ? L("Are you sure you want to move \"{0}\" to the Recycle Bin?", nodes[0].Name)
                : L("Are you sure you want to move {0} items to the Recycle Bin?", nodes.Count);
        }
        else
        {
            title = verb = L("Delete");
            message = local
                ? L("{0} can't be moved to the Recycle Bin because its drive doesn't have one. Delete permanently? This can't be undone.", label)
                : nodes.Count == 1
                    ? L("Are you sure you want to delete \"{0}\" from {1}?", nodes[0].Name, snapshot.PlaceName)
                    : L("Are you sure you want to delete {0} items from {1}?", nodes.Count, snapshot.PlaceName);
        }

        if (!MessageDialog.Confirm(this, title, message, verb, destructive: !recycle))
        {
            return;
        }

        SetBusy(true);
        var failures = new List<string>();
        try
        {
            await Task.Run(async () =>
            {
                for (var i = 0; i < paths.Count; i++)
                {
                    try
                    {
                        if (local && !recycle)
                        {
                            LocalFileProvider.DeletePermanently(paths[i]);
                        }
                        else
                        {
                            await endpoint.Provider.DeleteItemAsync(paths[i]);
                        }
                    }
                    catch (Exception e)
                    {
                        failures.Add($"{nodes[i].Name}: {e.Message}");
                    }
                }
            });
        }
        finally
        {
            SetBusy(false);
        }

        if (failures.Count > 0)
        {
            MessageDialog.Error(this, L("Delete"), string.Join(Environment.NewLine, failures));
        }

        foreach (var panel in new[] { _vm.Left, _vm.Right })
        {
            if (ReferenceEquals(panel.Provider, endpoint.Provider))
            {
                _ = panel.RefreshAsync(quiet: true);
            }
        }

        if (snapshot.Location.AccountId is { } id)
        {
            _vm.Services.Accounts.InvalidateQuota(id);
        }

        // Closed while deleting: the session was cancelled with the window; don't start it again.
        if (!_closed)
        {
            await _session.CompareAgainAsync();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _compareAgain.IsEnabled = !busy && !_session.IsCalculating;
        UpdateActions();
    }
}
