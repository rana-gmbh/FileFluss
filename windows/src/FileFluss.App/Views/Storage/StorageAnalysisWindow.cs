// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Formatting;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Storage;
using FileFluss.Native;
using Microsoft.Win32;
using static FileFluss.Core.Localization;

using Glyphs = FileFluss.App.Services.Glyphs;

namespace FileFluss.App.Views.Storage;

/// <summary>
/// Storage Analysis (macOS <c>StorageAnalysisView</c>): "where has my space gone" for a cloud
/// account, a folder inside one, a local folder or a drive — a size-ordered folder tree with
/// share bars beside a list of the biggest individual files. Results fill in while the scan
/// runs; Cancel keeps what was found. One window, as on macOS: opening it again for another
/// location switches it to that location.
/// </summary>
public sealed partial class StorageAnalysisWindow : Window
{
    private static StorageAnalysisWindow? _open;

    private readonly MainViewModel _vm;
    private readonly StorageAnalysisSession _session = new();

    /// <summary>Folders chosen with "Choose Folder…" or opened from a context menu, kept in the picker.</summary>
    private readonly List<SourceOption> _extraSources = [];

    private readonly ProgressBar _spinner = new() { IsIndeterminate = true, Width = 64, Height = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Button _cancel = new() { Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _sourcePicker = new() { MinWidth = 240, MaxWidth = 320, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _thresholdPicker = new() { Width = 150, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _analyse = new() { MinWidth = 96, Padding = new Thickness(12, 5, 12, 6), Margin = new Thickness(8, 0, 0, 0), IsDefault = true, VerticalAlignment = VerticalAlignment.Center };

    private readonly ContentControl _content = new() { Focusable = false, IsTabStop = false };
    private readonly FrameworkElement _placeholder;
    private readonly FrameworkElement _scanningView;
    private readonly TextBlock _scanningText = new() { FontSize = 12.5, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520, Margin = new Thickness(0, 12, 0, 0) };
    private readonly FrameworkElement _errorView;
    private readonly TextBlock _errorText = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 380, Margin = new Thickness(0, 10, 0, 0) };
    private readonly FrameworkElement _results;
    private readonly Border _cancelledBanner;
    private readonly TextBlock _cancelledText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly StorageList _tree;
    private readonly StorageList _largest;
    private readonly TextBlock _noLargeFiles = new() { FontSize = 12.5, Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap };

    private List<SourceOption> _sources = [];
    private bool _rebuildingSources;
    private string? _scannedKey;
    private IReadOnlyList<StorageRow>? _shownRows;
    private (StorageReport? Report, long Minimum) _shownLargest;
    private int _refreshQueued;

    private StorageAnalysisWindow(MainViewModel vm, Window? owner)
    {
        _vm = vm;
        Title = L("Storage Analysis");
        Width = 900;
        Height = 620;
        MinWidth = 680;
        MinHeight = 440;
        UseLayoutRounding = true;
        AutomationProperties.SetAutomationId(this, "StorageAnalysisWindow");
        PlaceOver(owner);

        _tree = CreateList(L("Folders"), "StorageFolderTree", typeof(TreeRowView));
        _largest = CreateList(L("Largest files"), "StorageLargestFiles", typeof(LargestRowView));
        _placeholder = BuildPlaceholder();
        _scanningView = BuildScanningView();
        _errorView = BuildErrorView();
        _cancelledBanner = BuildBanner(_cancelledText);
        _results = BuildResults();

        var root = new DockPanel();
        var header = BuildHeader();
        var sourceBar = BuildSourceBar();
        foreach (var top in new FrameworkElement[] { header, Divider(), sourceBar, Divider() })
        {
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
        }

        root.Children.Add(_content);
        Content = root;

        _session.PropertyChanged += OnSessionChanged;
        Activated += (_, _) =>
        {
            if (!_sourcePicker.IsDropDownOpen)
            {
                // Accounts connect and panels move while the window sits in the background.
                RebuildSources();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_sourcePicker.IsDropDownOpen && !_thresholdPicker.IsDropDownOpen)
            {
                e.Handled = true;
                Close();
            }
        };
        Closed += (_, _) =>
        {
            _session.PropertyChanged -= OnSessionChanged;
            _session.Cancel();
            _open = null;
        };

        RebuildSources();
        Refresh();
    }

    /// <summary>
    /// Opens the window (or brings the open one forward) on <paramref name="location"/> and
    /// analyses it. "This PC" has nothing to analyse as a whole, so it only opens the window
    /// with the system drive picked.
    /// </summary>
    public static void Show(Window owner, MainViewModel vm, PanelLocation location)
    {
        if (_open is null)
        {
            _open = new StorageAnalysisWindow(vm, owner);
            _open.Show();
        }
        else if (_open.WindowState == WindowState.Minimized)
        {
            _open.WindowState = WindowState.Normal;
        }

        _open.Activate();
        _open.Open(location);
    }

    // MARK: - Layout

    /// <summary>
    /// Centred over the main window but not owned by it: Reveal brings the main window forward,
    /// and an owned window would stay on top and cover the panel it just navigated.
    /// </summary>
    private void PlaceOver(Window? owner)
    {
        if (owner is { IsLoaded: true, WindowState: WindowState.Normal })
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = owner.Left + Math.Max(0, (owner.ActualWidth - Width) / 2);
            Top = owner.Top + Math.Max(0, (owner.ActualHeight - Height) / 2);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private FrameworkElement BuildHeader()
    {
        var heading = new TextBlock { Text = L("Storage Analysis"), FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetAutomationId(_status, "StorageStatus");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _cancel.Content = L("Cancel");
        AutomationProperties.SetName(_cancel, L("Cancel"));
        AutomationProperties.SetAutomationId(_cancel, "StorageCancelScan");
        _cancel.Click += (_, _) => _session.Cancel();

        var grid = new Grid { Margin = new Thickness(20, 14, 20, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(grid, heading, 0);
        Place(grid, _spinner, 1);
        Place(grid, _status, 2);
        Place(grid, _cancel, 3);
        return grid;
    }

    private FrameworkElement BuildSourceBar()
    {
        AutomationProperties.SetName(_sourcePicker, L("Location"));
        AutomationProperties.SetAutomationId(_sourcePicker, "StorageSourcePicker");
        _sourcePicker.SelectionChanged += (_, _) =>
        {
            if (!_rebuildingSources)
            {
                UpdateAnalyseEnabled();
            }
        };

        var choose = new Button { Content = L("Choose Folder…"), Padding = new Thickness(12, 5, 12, 6), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        choose.ToolTip = L("Analyse any folder on this PC");
        AutomationProperties.SetName(choose, L("Choose Folder…"));
        AutomationProperties.SetAutomationId(choose, "StorageChooseFolder");
        choose.Click += (_, _) => ChooseLocalFolder();

        _thresholdPicker.ItemsSource = StorageAnalysisSession.SizeThresholds.Select(t => new ThresholdOption(t)).ToList();
        _thresholdPicker.SelectedIndex = 0;
        AutomationProperties.SetName(_thresholdPicker, L("Show"));
        AutomationProperties.SetAutomationId(_thresholdPicker, "StorageSizeFilter");
        _thresholdPicker.SelectionChanged += (_, _) =>
        {
            if (_thresholdPicker.SelectedItem is ThresholdOption option)
            {
                _session.MinimumSize = option.Threshold.Bytes;
            }
        };

        _analyse.Content = L("Analyse");
        AutomationProperties.SetName(_analyse, L("Analyse"));
        AutomationProperties.SetAutomationId(_analyse, "StorageAnalyse");
        if (Application.Current?.TryFindResource("AccentButtonStyle") is Style accent)
        {
            _analyse.Style = accent;
        }

        _analyse.Click += (_, _) =>
        {
            if (_sourcePicker.SelectedItem is SourceOption option)
            {
                StartScan(option);
            }
        };

        // Added in reading order, so Tab moves left to right.
        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), Children = { _thresholdPicker, _analyse } };
        var bar = new DockPanel { Margin = new Thickness(20, 10, 20, 10), LastChildFill = false };
        foreach (var (element, dock) in new (FrameworkElement, Dock)[] { (_sourcePicker, Dock.Left), (choose, Dock.Left), (right, Dock.Right) })
        {
            DockPanel.SetDock(element, dock);
            bar.Children.Add(element);
        }

        return bar;
    }

    private FrameworkElement BuildResults()
    {
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 320 });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 260 });

        var tree = new DockPanel();
        var treeTitle = ColumnTitle(L("Folders"));
        DockPanel.SetDock(treeTitle, Dock.Top);
        tree.Children.Add(treeTitle);
        tree.Children.Add(_tree);
        Place(columns, tree, 0);

        var line = new Border();
        line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        Place(columns, line, 1);

        // A wider, invisible grip over the one-pixel line (macOS HSplitView).
        var splitter = new GridSplitter
        {
            Width = 7,
            Margin = new Thickness(-3, 0, -3, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = GridResizeDirection.Columns,
            Background = Brushes.Transparent,
            Focusable = false,
        };
        Panel.SetZIndex(splitter, 1);
        Place(columns, splitter, 1);

        var largest = new DockPanel();
        var largestTitle = ColumnTitle(L("Largest files"));
        DockPanel.SetDock(largestTitle, Dock.Top);
        largest.Children.Add(largestTitle);
        _noLargeFiles.Text = StorageAnalysisSession.NoLargeFilesText;
        _noLargeFiles.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        largest.Children.Add(new Grid { Children = { _largest, _noLargeFiles } });
        Place(columns, largest, 2);

        var results = new DockPanel();
        DockPanel.SetDock(_cancelledBanner, Dock.Top);
        results.Children.Add(_cancelledBanner);
        results.Children.Add(columns);
        return results;
    }

    private static FrameworkElement BuildPlaceholder()
    {
        var glyph = new TextBlock { Text = Glyphs.Pie, FontFamily = new FontFamily(Glyphs.FontFamily), FontSize = 34, HorizontalAlignment = HorizontalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var text = new TextBlock { Text = StorageAnalysisSession.PlaceholderText, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 420, Margin = new Thickness(0, 12, 0, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return Centered(glyph, text);
    }

    private FrameworkElement BuildScanningView()
    {
        var bar = new ProgressBar { IsIndeterminate = true, Width = 220, Height = 4 };
        AutomationProperties.SetName(bar, L("Scanning…"));
        _scanningText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return Centered(bar, _scanningText);
    }

    private FrameworkElement BuildErrorView()
    {
        var glyph = new TextBlock
        {
            Text = Glyphs.Warning,
            FontFamily = new FontFamily(Glyphs.FontFamily),
            FontSize = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0x9B, 0x00)),
        };
        AutomationProperties.SetAutomationId(_errorText, "StorageError");
        return Centered(glyph, _errorText);
    }

    private static Border BuildBanner(TextBlock text)
    {
        var glyph = new TextBlock { Text = Glyphs.Warning, FontFamily = new FontFamily(Glyphs.FontFamily), FontSize = 13, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetAutomationId(text, "StorageCancelledNotice");
        var row = new DockPanel();
        DockPanel.SetDock(glyph, Dock.Left);
        row.Children.Add(glyph);
        row.Children.Add(text);
        var banner = new Border { Padding = new Thickness(20, 8, 20, 8), Child = row };
        banner.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        return banner;
    }

    private static Border ColumnTitle(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetHeadingLevel(label, AutomationHeadingLevel.Level2);
        var title = new Border { Padding = new Thickness(12, 6, 12, 6), Child = label };
        title.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        return title;
    }

    private static StackPanel Centered(params UIElement[] children)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return stack;
    }

    private static Border Divider()
    {
        var line = new Border { Height = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return line;
    }

    private static void Place(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private StorageList CreateList(string name, string automationId, Type rowView)
    {
        var list = new StorageList
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 4, 0, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemContainerStyle = RowStyle.Value,
            ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(rowView) },
            ContextMenu = new ContextMenu(),
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(list, name);
        AutomationProperties.SetAutomationId(list, automationId);
        list.ContextMenuOpening += OnContextMenuOpening;
        list.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnListMouseDown), handledEventsToo: true);
        list.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnChevronClick));
        list.PreviewKeyDown += OnListKeyDown;
        return list;
    }

    // MARK: - Sources

    /// <summary>
    /// Connected accounts, the folder each visible panel shows, the ready local drives (the
    /// Windows counterpart of "analyse this Mac"), and folders opened here before — no duplicates.
    /// </summary>
    private List<SourceOption> BuildSources()
    {
        var result = new List<SourceOption>();
        var services = _vm.Services;
        foreach (var account in services.Accounts.Accounts)
        {
            if (account.IsConnected && services.Hub.Get(account.Id) is { } provider)
            {
                result.Add(CloudOption(account, provider, account.EffectiveRootPath));
            }
        }

        foreach (var panel in _vm.SinglePaneMode ? new[] { _vm.ActivePanel } : [_vm.Left, _vm.Right])
        {
            if (OptionFor(panel.Location) is { } option)
            {
                result.Add(option);
            }
        }

        foreach (var drive in LocalDrives())
        {
            result.Add(LocalOption(drive));
        }

        result.AddRange(_extraSources);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return result.Where(o => seen.Add(o.Key)).ToList();
    }

    private void RebuildSources(string? selectKey = null)
    {
        selectKey ??= (_sourcePicker.SelectedItem as SourceOption)?.Key;
        _sources = BuildSources();
        _rebuildingSources = true;
        try
        {
            _sourcePicker.ItemsSource = _sources;
            _sourcePicker.SelectedItem = _sources.FirstOrDefault(s => s.Key == selectKey) ?? _sources.FirstOrDefault();
        }
        finally
        {
            _rebuildingSources = false;
        }

        UpdateAnalyseEnabled();
    }

    /// <summary>A picker entry for a panel location; null for "This PC" or an account that isn't connected.</summary>
    private SourceOption? OptionFor(PanelLocation location)
    {
        if (location.IsCloud)
        {
            var id = location.AccountId!.Value;
            return _vm.Services.Accounts.Find(id) is { } account && _vm.Services.Hub.Get(id) is { } provider
                ? CloudOption(account, provider, location.Path.Length == 0 ? account.EffectiveRootPath : location.Path)
                : null;
        }

        return location.Path.Length == 0 ? null : LocalOption(location.Path);
    }

    private static SourceOption CloudOption(CloudAccount account, CloudProvider provider, string path)
    {
        var normalized = NormalizeCloudPath(path);
        var isAccountRoot = provider.IsRoot(normalized) || normalized == NormalizeCloudPath(account.EffectiveRootPath);
        var title = isAccountRoot ? account.DisplayName : $"{account.DisplayName} — {normalized}";
        return new SourceOption(StorageScanSource.ForAccount(provider, account.DisplayName, normalized), title, $"cloud:{account.Id}:{normalized}");
    }

    private static SourceOption LocalOption(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(path);
        if (normalized.Length == 2 && normalized[1] == ':')
        {
            // "C:" means "the current folder on C:", not the drive.
            normalized += Path.DirectorySeparatorChar;
        }

        var isDrive = string.Equals(Path.GetPathRoot(normalized), normalized, StringComparison.OrdinalIgnoreCase);
        var title = isDrive ? LocalFileProvider.DriveLabel(normalized) : normalized;
        return new SourceOption(StorageScanSource.ForLocal(normalized), title, "local:" + normalized.ToUpperInvariant());
    }

    private static string NormalizeCloudPath(string path)
    {
        var trimmed = path.Length == 0 ? "/" : path;
        while (trimmed.Length > 1 && trimmed.EndsWith('/'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }

    private static List<string> LocalDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Open(PanelLocation location)
    {
        if (OptionFor(location) is not { } option)
        {
            if (location.IsCloud)
            {
                MessageDialog.Error(this, L("Storage Analysis"), L("Cloud account not connected"));
                return;
            }

            // "This PC": nothing to scan as a whole — offer the system drive.
            var system = Path.GetPathRoot(Environment.SystemDirectory);
            RebuildSources(_session.Report is null && !_session.IsScanning && system is not null ? LocalOption(system).Key : null);
            return;
        }

        if (!_extraSources.Any(s => s.Key == option.Key))
        {
            _extraSources.Add(option);
        }

        RebuildSources(option.Key);

        // Asking again for what is already on screen (or still being read) just shows it.
        if (_scannedKey == option.Key && (_session.IsScanning || _session.Report is { WasCancelled: false }))
        {
            return;
        }

        StartScan(option);
    }

    private void ChooseLocalFolder()
    {
        var dialog = new OpenFolderDialog { Title = L("Analyse any folder on this PC") };
        if (_sourcePicker.SelectedItem is SourceOption { Source.Provider.IsLocal: true } current && Directory.Exists(current.Source.RootPath))
        {
            dialog.InitialDirectory = current.Source.RootPath;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var option = LocalOption(dialog.FolderName);
        if (!_extraSources.Any(s => s.Key == option.Key))
        {
            _extraSources.Add(option);
        }

        RebuildSources(option.Key);
        StartScan(option);
    }

    private void StartScan(SourceOption option)
    {
        _scannedKey = option.Key;
        _session.Scan(option.Source);
    }

    private void UpdateAnalyseEnabled() => _analyse.IsEnabled = _sourcePicker.SelectedItem is SourceOption && !_session.IsScanning;

    // MARK: - Rendering

    /// <summary>The session reports from the scanning thread, several times a second: coalesce onto the UI thread.</summary>
    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                Refresh();
            });
        }
    }

    private void Refresh()
    {
        var scanning = _session.IsScanning;
        var report = _session.Report;
        var error = _session.ErrorMessage;

        _spinner.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        _cancel.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = scanning ? _session.ProgressText : _session.SummaryText ?? string.Empty;
        _status.ToolTip = _status.Text.Length > 0 ? _status.Text : null;
        UpdateAnalyseEnabled();

        if (error is not null)
        {
            _errorText.Text = error;
            ShowContent(_errorView);
        }
        else if (report is not null)
        {
            var notice = _session.CancelledNotice;
            _cancelledText.Text = notice ?? string.Empty;
            _cancelledBanner.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
            UpdateTree();
            UpdateLargest(report);
            ShowContent(_results);
        }
        else if (scanning)
        {
            _scanningText.Text = _session.ProgressText;
            ShowContent(_scanningView);
        }
        else
        {
            ShowContent(_placeholder);
        }
    }

    private void ShowContent(FrameworkElement view)
    {
        if (!ReferenceEquals(_content.Content, view))
        {
            _content.Content = view;
        }
    }

    private void UpdateTree()
    {
        var rows = _session.Rows;
        if (ReferenceEquals(rows, _shownRows))
        {
            return;
        }

        _shownRows = rows;
        var rootIcon = _session.ScannedSource?.Provider is CloudProvider cloud ? IconCache.Logo(cloud.ProviderType) : null;
        ReplaceItems(_tree, rows.Select(r => new TreeRowItem(r, r.Depth == 0 ? rootIcon : null)).ToList());
    }

    private void UpdateLargest(StorageReport report)
    {
        var minimum = _session.MinimumSize;
        if (ReferenceEquals(_shownLargest.Report, report) && _shownLargest.Minimum == minimum)
        {
            return;
        }

        _shownLargest = (report, minimum);
        var provider = _session.ScannedSource?.Provider ?? LocalFileProvider.Shared;
        var total = report.Root.Size;
        var items = _session.LargestFiles
            .Select(f => new LargestRowItem(f, StorageLayout.ContainingFolder(f, provider), ByteFormatter.Format(f.Size), StorageLayout.ShareText(f.Fraction(total))))
            .ToList();
        ReplaceItems(_largest, items);
        _noLargeFiles.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _largest.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Swaps a list's items for a newer report's without the list jumping: the selected entry
    /// (by path), the scroll position and keyboard focus all survive.
    /// </summary>
    private void ReplaceItems<T>(ListBox list, List<T> items)
        where T : IStorageListItem
    {
        var selectedId = (list.SelectedItem as IStorageListItem)?.Node.Id;
        var hadFocus = list.IsKeyboardFocusWithin;
        var scroll = FindDescendant<ScrollViewer>(list);
        var offset = scroll?.VerticalOffset ?? 0;

        list.ItemsSource = items;
        var index = selectedId is null ? -1 : items.FindIndex(i => i.Node.Id == selectedId);
        list.SelectedIndex = index;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            scroll?.ScrollToVerticalOffset(offset);
            if (hadFocus)
            {
                FocusRow(list, index);
            }
        });
    }

    private static void FocusRow(ListBox list, int index)
    {
        list.UpdateLayout();
        if (index >= 0 && list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem container)
        {
            container.Focus();
        }
        else
        {
            list.Focus();
        }
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // MARK: - Interaction

    /// <summary>The list item under an event source (a row or anything inside it), or null.</summary>
    private static IStorageListItem? ItemAt(ListBox list, object source)
    {
        var element = source as DependencyObject;
        while (element is not null and not Visual)
        {
            element = LogicalTreeHelper.GetParent(element);
        }

        return element is null ? null : (ItemsControl.ContainerFromElement(list, element) as ListBoxItem)?.DataContext as IStorageListItem;
    }

    private static bool IsInsideButton(object source, ListBox list)
    {
        for (var element = source as DependencyObject; element is not null && !ReferenceEquals(element, list); element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>As on macOS: a click opens or closes a folder row, a double-click reveals the item.</summary>
    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        if (IsInsideButton(e.OriginalSource, list) || ItemAt(list, e.OriginalSource) is not { } item)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            _ = RevealAsync(item.Node);
        }
        else if (e.ClickCount == 1 && item is TreeRowItem { Row.IsExpandable: true } row)
        {
            _session.ToggleExpanded(row.Node);
        }
    }

    private void OnChevronClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: TreeRowItem item })
        {
            e.Handled = true;
            _tree.SelectedItem = item;
            _session.ToggleExpanded(item.Node);
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        var list = (ListBox)sender;
        if (list.SelectedItem is not IStorageListItem item)
        {
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Enter:
                // Handled here, or the window's default button (Analyse) would rescan instead.
                e.Handled = true;
                _ = RevealAsync(item.Node);
                break;
            case Key.C when ctrl:
                e.Handled = true;
                CopyPath(item.Node);
                break;
            case Key.Right when item is TreeRowItem row:
                e.Handled = true;
                if (row.Row.IsExpandable && !row.Row.IsExpanded)
                {
                    _session.ToggleExpanded(row.Node);
                }
                else if (row.Row.IsExpanded && list.SelectedIndex + 1 < list.Items.Count)
                {
                    SelectRow(list, list.SelectedIndex + 1);
                }

                break;
            case Key.Left when item is TreeRowItem row:
                e.Handled = true;
                if (row.Row.IsExpanded)
                {
                    _session.ToggleExpanded(row.Node);
                }
                else if (_shownRows is { } rows && StorageLayout.ParentRowIndex(rows, list.SelectedIndex) is var parent and >= 0)
                {
                    SelectRow(list, parent);
                }

                break;
            case Key.Space when item is TreeRowItem row:
                e.Handled = true;
                if (row.Row.IsExpandable)
                {
                    _session.ToggleExpanded(row.Node);
                }

                break;
        }
    }

    private static void SelectRow(ListBox list, int index)
    {
        list.SelectedIndex = index;
        list.ScrollIntoView(list.SelectedItem);
        FocusRow(list, index);
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var list = (ListBox)sender;

        // From the keyboard (Shift+F10, the menu key) the menu is for the selection; with the mouse, for the row clicked.
        var item = e.CursorLeft < 0 ? list.SelectedItem as IStorageListItem : ItemAt(list, e.OriginalSource);
        if (item is null || list.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }

        menu.Items.Clear();
        foreach (var entry in MenuItems(item.Node))
        {
            menu.Items.Add(entry);
        }
    }

    private List<object> MenuItems(StorageNode node)
    {
        var items = new List<object>
        {
            ContextMenus.Item(L("Reveal"), Glyphs.OpenInNew, "Enter", () => _ = RevealAsync(node)),
        };
        if (_session.ScannedSource?.Provider.IsLocal == true)
        {
            items.Add(ContextMenus.Item(L("Show in Explorer"), Glyphs.OpenFolder, null, () => ShowInExplorer(node)));
        }

        items.Add(new Separator());
        items.Add(ContextMenus.Item(L("Copy Path"), Glyphs.Link, "Ctrl+C", () => CopyPath(node)));
        return items;
    }

    /// <summary>
    /// Shows the item where the user can act on it: in the active panel of the main window —
    /// a folder opened, a file selected in its folder (macOS: the active panel for cloud items).
    /// </summary>
    private async Task RevealAsync(StorageNode node)
    {
        if (_session.ScannedSource is not { } source)
        {
            return;
        }

        var (folder, selectName) = StorageLayout.RevealTarget(node, source.Provider);
        var location = source.Provider is CloudProvider cloud ? PanelLocation.Cloud(cloud.AccountId, folder) : PanelLocation.Local(folder);
        var side = _vm.ActiveSide;
        var main = _vm.Shell.Window;
        if (main.WindowState == WindowState.Minimized)
        {
            main.WindowState = WindowState.Normal;
        }

        main.Activate();
        try
        {
            await _vm.Panel(side).NavigateAsync(location, selectName);
            _vm.Shell.FocusPanel(side);
        }
        catch (Exception e)
        {
            MessageDialog.Error(main, L("Storage Analysis"), e.Message);
        }
    }

    private void ShowInExplorer(StorageNode node)
    {
        try
        {
            ShellFileOperations.RevealInExplorer([node.Path]);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            MessageDialog.Error(this, L("Show in Explorer"), e.Message);
        }
    }

    private static void CopyPath(StorageNode node)
    {
        try
        {
            Clipboard.SetText(node.Path);
        }
        catch (ExternalException)
        {
            // The clipboard is held by another app; nothing useful to tell the user.
        }
    }

    // MARK: - Picker entries

    /// <param name="Key">Identity in the picker: local paths compare without case, cloud paths with it.</param>
    private sealed record SourceOption(StorageScanSource Source, string Title, string Key)
    {
        public override string ToString() => Title;
    }

    private sealed record ThresholdOption(StorageSizeThreshold Threshold)
    {
        public override string ToString() => Threshold.Label;
    }
}
