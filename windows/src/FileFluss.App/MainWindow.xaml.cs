// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.App.Views;
using FileFluss.App.Views.Accounts;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Commands;
using FileFluss.Core.Formatting;
using FileFluss.Core.Models;
using FileFluss.Core.Settings;
using FileFluss.Native;
using static FileFluss.Core.Localization;

using Localization = FileFluss.Core.Localization;

namespace FileFluss.App;

public partial class MainWindow : Window, IShell
{
    private readonly MainViewModel _vm;
    private readonly AppServices _services;
    private Popup? _transfersPopup;

    public MainWindow(MainViewModel vm, AppServices services)
    {
        InitializeComponent();
        _vm = vm;
        _services = services;
        vm.Shell = this;
        DataContext = vm;

        LeftPanel.DataContext = vm.Left;
        RightPanel.DataContext = vm.Right;
        LeftPanel.Main = vm;
        RightPanel.Main = vm;
        LeftSidebar.DataContext = vm.LeftSidebar;
        RightSidebar.DataContext = vm.RightSidebar;
        LeftSidebar.Attach(vm);
        RightSidebar.Attach(vm);

        vm.PropertyChanged += OnViewModelChanged;
        vm.Left.PropertyChanged += OnPanelChanged;
        vm.Right.PropertyChanged += OnPanelChanged;
        vm.SelectRequested += (_, request) => View(request.Side).SelectByName(request.Name);
        services.Transfers.PropertyChanged += (_, _) => UpdateTransfersButton();
        services.SettingsStore.Changed += (_, name) =>
        {
            if (name is null or nameof(AppSettings.CustomBindings) or nameof(AppSettings.KeyboardPreset))
            {
                BuildMenu();
            }

            if (name is null or nameof(AppSettings.SinglePaneMode))
            {
                ApplyPaneMode();
            }

            if (name is null or nameof(AppSettings.RowSize))
            {
                ApplyRowSize();
            }

            if (name is null or nameof(AppSettings.ShowStatusBar))
            {
                ApplyStatusBar();
            }
        };
        Localization.LanguageChanged += (_, _) =>
        {
            BuildMenu();
            vm.NotifyLanguageChanged();
            UpdateToolbar();
        };

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            ApplyActivePanel();
            View(PanelSide.Left).FocusList();
        };
        Closing += OnClosing;
        SourceInitialized += (_, _) => DeviceNotifications.Register(this, async () =>
        {
            vm.LeftSidebar.RebuildDrives();
            vm.RightSidebar.RebuildDrives();

            // Learn which indexed drives came or went; the registry's change event rebuilds again.
            await services.Search.RefreshDrivesAsync();
        });

        WireStatusOverlays();
        RestorePlacement();
        ApplySidebarWidths();
        BuildMenu();
        ApplyPaneMode();
        ApplyRowSize();
        ApplyStatusBar();
        UpdateToolbar();
    }

    public Window Window => this;

    private PanelView View(PanelSide side) => side == PanelSide.Left ? LeftPanel : RightPanel;

    // MARK: - Keyboard

    /// <summary>
    /// Every bound shortcut is dispatched here, before controls see the key — except while a
    /// text field has focus, where typing must win (renaming, the path bar, the filter).
    /// </summary>
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox || e.OriginalSource is TextBoxBase)
        {
            return;
        }

        // A modal dialog owns the keyboard; a shortcut reaching the main window then (focus
        // forced onto it) must not start a second operation underneath the dialog.
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
        {
            return;
        }

        if (Keyboard.FocusedElement is DependencyObject focused && FindParent<MenuBase>(focused) is not null)
        {
            return;
        }

#if DEBUG
        if (ShortcutFromKey(e) is { Modifiers: not ShortcutModifiers.None } debugPressed)
        {
            Core.Diagnostics.SupportLogger.Shared.Log($"Key {debugPressed} → {_services.Keyboard.CommandFor(debugPressed)?.ToString() ?? "none"} (focus: {Keyboard.FocusedElement?.GetType().Name})", "Keys");
        }
#endif

        if (ShortcutFromKey(e) is not { } pressed)
        {
            return;
        }

        if (_services.Keyboard.CommandFor(pressed) is not { } command)
        {
            e.Handled = HandleFixedShortcut(pressed);
            return;
        }

        e.Handled = true;
        await _vm.ExecuteAsync(command);
    }

    public static ShortcutBinding? ShortcutFromKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = ShortcutModifiers.None;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            modifiers |= ShortcutModifiers.Control;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            modifiers |= ShortcutModifiers.Shift;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            modifiers |= ShortcutModifiers.Alt;
        }

        var token = KeyToken(key);
        return token is null ? null : new ShortcutBinding(token, modifiers);
    }

    public static string? KeyToken(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        >= Key.F1 and <= Key.F12 => "f" + (key - Key.F1 + 1),
        Key.Enter => "enter",
        Key.Tab => "tab",
        Key.Space => "space",
        Key.Back => "backspace",
        Key.Delete => "delete",
        Key.Up => "up",
        Key.Down => "down",
        Key.Left => "left",
        Key.Right => "right",
        Key.Home => "home",
        Key.End => "end",
        Key.PageUp => "pageup",
        Key.PageDown => "pagedown",
        Key.Insert => "insert",
        Key.Add => "plus",
        Key.Subtract => "minus",
        Key.Multiply => "multiply",
        Key.Decimal => "period",
        Key.Divide => "slash",

        // Punctuation keys sit in different places on every layout: name them by what they
        // type here, so Ctrl+= is the key labelled "=" (VK_OEM_PLUS on US, Shift+0 on German).
        _ when IsOemKey(key) => KeyboardLayout.CharFor(KeyInterop.VirtualKeyFromKey(key)) switch
        {
            '=' => "equals",
            '+' => "plus",
            '-' => "minus",
            ',' => "comma",
            '.' => "period",
            '/' => "slash",
            '\\' => "backslash",
            _ => null,
        },
        _ => null,
    };

    private static bool IsOemKey(Key key) => key is >= Key.Oem1 and <= Key.Oem102;

    /// <summary>Keys that are not keyboard-map commands: F1 for Help, Ctrl +/− for the row size (macOS ⌘? and ⌘+/⌘−).</summary>
    private bool HandleFixedShortcut(ShortcutBinding pressed)
    {
        switch (pressed)
        {
            case { Key: "f1", Modifiers: ShortcutModifiers.None }:
                ShowHelp();
                return true;
            case { Key: "plus", Modifiers: ShortcutModifiers.Control }:
            case { Key: "equals", Modifiers: ShortcutModifiers.Control | ShortcutModifiers.Shift }:
                StepRowSize(1);
                return true;
            case { Key: "minus", Modifiers: ShortcutModifiers.Control }:
                StepRowSize(-1);
                return true;
            default:
                return false;
        }
    }

    private static T? FindParent<T>(DependencyObject start)
        where T : DependencyObject
    {
        for (var current = start; current is not null; current = LogicalTreeHelper.GetParent(current) ?? System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    // MARK: - Menu

    private void BuildMenu()
    {
        MainMenu.Items.Clear();
        string S(KeyboardCommand c) => _services.Keyboard.BindingFor(c)?.DisplayString ?? string.Empty;
        MenuItem Cmd(KeyboardCommand command, string? title = null)
        {
            var item = ContextMenus.Item(L(title ?? command.DisplayName()), null, S(command), () => _ = _vm.ExecuteAsync(command));
            item.Tag = command;
            return item;
        }

        var file = new MenuItem { Header = "_" + L("File") };
        file.Items.Add(Cmd(KeyboardCommand.NewFolder));
        file.Items.Add(new Separator());
        file.Items.Add(Cmd(KeyboardCommand.Rename));
        file.Items.Add(Cmd(KeyboardCommand.Duplicate));
        file.Items.Add(Cmd(KeyboardCommand.GetInfo));
        file.Items.Add(new Separator());
        file.Items.Add(Cmd(KeyboardCommand.CopyToOtherPanel));
        file.Items.Add(Cmd(KeyboardCommand.MoveToOtherPanel));
        file.Items.Add(Cmd(KeyboardCommand.CopyPath));
        file.Items.Add(Cmd(KeyboardCommand.OpenInTerminal));
        file.Items.Add(new Separator());
        file.Items.Add(Cmd(KeyboardCommand.DeleteToTrash));
        file.Items.Add(new Separator());
        file.Items.Add(ContextMenus.Item(L("Add Cloud Account…"), null, null, () => ShowAddAccount()));
        file.Items.Add(ContextMenus.Item(L("Settings…"), null, S(KeyboardCommand.OpenSettings), () => ShowSettings()));
        file.Items.Add(new Separator());
        file.Items.Add(ContextMenus.Item(L("Exit"), null, "Alt+F4", Close));
        MainMenu.Items.Add(file);

        var edit = new MenuItem { Header = "_" + L("Edit") };
        edit.Items.Add(Cmd(KeyboardCommand.CutFiles));
        edit.Items.Add(Cmd(KeyboardCommand.CopyFiles));
        edit.Items.Add(Cmd(KeyboardCommand.PasteFiles));
        edit.Items.Add(new Separator());
        edit.Items.Add(Cmd(KeyboardCommand.SelectAll));
        edit.Items.Add(Cmd(KeyboardCommand.DeselectAll));
        edit.Items.Add(Cmd(KeyboardCommand.InvertSelection));
        edit.Items.Add(new Separator());
        edit.Items.Add(Cmd(KeyboardCommand.OpenSearch, "Search…"));
        edit.Items.Add(Cmd(KeyboardCommand.QuickFilter, "Quick Filter…"));
        MainMenu.Items.Add(edit);

        var view = new MenuItem { Header = "_" + L("View") };
        view.Items.Add(Cmd(KeyboardCommand.RefreshAll));
        view.Items.Add(Cmd(KeyboardCommand.RefreshActive));
        view.Items.Add(new Separator());
        view.Items.Add(Cmd(KeyboardCommand.SortByName));
        view.Items.Add(Cmd(KeyboardCommand.SortByDate));
        view.Items.Add(Cmd(KeyboardCommand.SortBySize));
        view.Items.Add(Cmd(KeyboardCommand.SortByKind));
        view.Items.Add(new Separator());
        view.Items.Add(Cmd(KeyboardCommand.ToggleHiddenFiles, "Toggle Hidden Files"));
        view.Items.Add(ContextMenus.Check(L("Show Status Bar"), _services.Settings.ShowStatusBar, () => _services.Settings.ShowStatusBar = !_services.Settings.ShowStatusBar));
        view.Items.Add(ContextMenus.Check(L("Hide File Extensions"), _services.Settings.HideFileExtensions, () =>
        {
            _services.Settings.HideFileExtensions = !_services.Settings.HideFileExtensions;
            _ = _vm.Left.RefreshAsync(true);
            _ = _vm.Right.RefreshAsync(true);
        }));
        view.Items.Add(ContextMenus.Check(L("Single Pane"), _vm.SinglePaneMode, () => _vm.SinglePaneMode = !_vm.SinglePaneMode));
        view.Items.Add(new Separator());
        var bigger = ContextMenus.Item(L("Increase Row Size"), null, "Ctrl++", () => StepRowSize(1));
        bigger.Tag = "row+";
        view.Items.Add(bigger);
        var smaller = ContextMenus.Item(L("Decrease Row Size"), null, "Ctrl+-", () => StepRowSize(-1));
        smaller.Tag = "row-";
        view.Items.Add(smaller);
        MainMenu.Items.Add(view);

        var go = new MenuItem { Header = "_" + L("Go") };
        go.Items.Add(Cmd(KeyboardCommand.OpenDirectory));
        go.Items.Add(Cmd(KeyboardCommand.ParentDirectory));
        go.Items.Add(Cmd(KeyboardCommand.HistoryBack));
        go.Items.Add(Cmd(KeyboardCommand.HistoryForward));
        go.Items.Add(Cmd(KeyboardCommand.OpenInOtherPanel));
        go.Items.Add(Cmd(KeyboardCommand.TargetToSource));
        go.Items.Add(Cmd(KeyboardCommand.FocusPathBar));
        go.Items.Add(Cmd(KeyboardCommand.JumpToRoot));
        go.Items.Add(new Separator());
        go.Items.Add(Cmd(KeyboardCommand.ToggleActivePanel));
        MainMenu.Items.Add(go);

        var tools = new MenuItem { Header = "_" + L("Tools") };
        tools.Items.Add(Cmd(KeyboardCommand.SyncPanels, "Sync Left and Right Panels…"));
        tools.Items.Add(Cmd(KeyboardCommand.CompareFolders));
        tools.Items.Add(ContextMenus.Item(L("Storage Analysis…"), null, null, () => ShowStorageAnalysis(_vm.ActivePanel.Location)));
        tools.Items.Add(Cmd(KeyboardCommand.SwapPanels));
        tools.Items.Add(new Separator());
        tools.Items.Add(Cmd(KeyboardCommand.IndexCurrentSource));
        MainMenu.Items.Add(tools);

        var help = new MenuItem { Header = "_" + L("Help") };
        help.Items.Add(ContextMenus.Item(L("FileFluss Help"), null, "F1", ShowHelp));
        help.Items.Add(ContextMenus.Item(L("FileFluss Homepage"), null, null, () => Core.Net.BrowserLauncher.Open("https://www.filefluss.de")));
        help.Items.Add(new Separator());
        help.Items.Add(ContextMenus.Item(L("Check for Updates…"), null, null, () => RequestUpdateCheck(null)));
        help.Items.Add(ContextMenus.Item(L("GitHub Repository"), null, null, () => Core.Net.BrowserLauncher.Open("https://github.com/rana-gmbh/FileFluss")));
        help.Items.Add(ContextMenus.Item(L("Support the FileFluss Project"), null, null, () => Core.Net.BrowserLauncher.Open("https://buymeacoffee.com/robertrudolph")));
        _supportLogItem = ContextMenus.Item(L("Support Log"), null, null, SupportLogService.Shared.Start);
        UpdateSupportLogItem();
        help.Items.Add(_supportLogItem);
        help.Items.Add(new Separator());
        help.Items.Add(ContextMenus.Item(L("About FileFluss"), null, null, ShowAbout));
        MainMenu.Items.Add(help);

        foreach (var top in MainMenu.Items.OfType<MenuItem>())
        {
            top.SubmenuOpened += (_, _) => UpdateMenuState(top);
        }
    }

    /// <summary>Greys out what can't apply right now, as the macOS menus do (no selection, read-only, single pane).</summary>
    private void UpdateMenuState(MenuItem menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsEnabled = item.Tag switch
            {
                KeyboardCommand command => _vm.IsCommandEnabled(command),
                "row+" => _services.Settings.RowSize < FileListRowSize.Large,
                "row-" => _services.Settings.RowSize > FileListRowSize.Small,
                _ => true,
            };
        }
    }

    /// <summary>
    /// Help → Check for Updates, and the About window's button; wired by the app. The argument
    /// is the window the answer should appear over, or null for this one.
    /// </summary>
    public event EventHandler<Window?>? UpdateRequested;

    public void RequestUpdateCheck(Window? owner) => UpdateRequested?.Invoke(this, owner);

    // MARK: - Layout

    private void ApplyPaneMode()
    {
        var single = _vm.SinglePaneMode;
        var showLeft = !single || _vm.ActiveSide == PanelSide.Left;
        var showRight = !single || _vm.ActiveSide == PanelSide.Right;
        LeftSidebarColumn.Width = showLeft ? new GridLength(_services.Settings.LeftSidebarWidth) : new GridLength(0);
        LeftSidebarColumn.MinWidth = showLeft ? 44 : 0;
        LeftPanelColumn.Width = showLeft ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        LeftPanelColumn.MinWidth = showLeft ? 200 : 0;
        RightPanelColumn.Width = showRight ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RightPanelColumn.MinWidth = showRight ? 200 : 0;
        RightSidebarColumn.Width = showRight ? new GridLength(_services.Settings.RightSidebarWidth) : new GridLength(0);
        RightSidebarColumn.MinWidth = showRight ? 44 : 0;
        LeftSidebar.Visibility = LeftCard.Visibility = showLeft ? Visibility.Visible : Visibility.Collapsed;
        RightSidebar.Visibility = RightCard.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        MiddleSplitter.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        PanesButton.IsChecked = single;
        UpdateToolbar();
    }

    private void ApplySidebarWidths()
    {
        LeftSidebarColumn.Width = new GridLength(_services.Settings.LeftSidebarWidth);
        RightSidebarColumn.Width = new GridLength(_services.Settings.RightSidebarWidth);
    }

    private void OnSplitterMoved(object sender, DragCompletedEventArgs e)
    {
        _services.SettingsStore.Batch(s =>
        {
            if (LeftSidebarColumn.ActualWidth > 0)
            {
                s.LeftSidebarWidth = LeftSidebarColumn.ActualWidth;
            }

            if (RightSidebarColumn.ActualWidth > 0)
            {
                s.RightSidebarWidth = RightSidebarColumn.ActualWidth;
            }

            s.LeftSidebarCompact = LeftSidebarColumn.ActualWidth < 90;
            s.RightSidebarCompact = RightSidebarColumn.ActualWidth < 90;
        });
    }

    private void ApplyRowSize()
    {
        Application.Current.Resources["FileRowHeight"] = _services.Settings.RowSize switch
        {
            FileListRowSize.Small => 24.0,
            FileListRowSize.Large => 34.0,
            _ => 28.0,
        };
    }

    private void StepRowSize(int delta)
    {
        var next = (int)_services.Settings.RowSize + delta;
        _services.Settings.RowSize = (FileListRowSize)Math.Clamp(next, 0, 2);
    }

    private void ApplyStatusBar()
    {
        var visible = _services.Settings.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
        LeftPanel.StatusBar.Visibility = visible;
        RightPanel.StatusBar.Visibility = visible;
    }

    // MARK: - Active panel and toolbar state

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ActiveSide) or "")
        {
            ApplyActivePanel();
            if (_vm.SinglePaneMode)
            {
                ApplyPaneMode();
            }
        }
    }

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == _vm.ActivePanel)
        {
            UpdateToolbar();
        }
    }

    private void ApplyActivePanel()
    {
        LeftPanel.SetActive(!_vm.SinglePaneMode && _vm.ActiveSide == PanelSide.Left);
        RightPanel.SetActive(!_vm.SinglePaneMode && _vm.ActiveSide == PanelSide.Right);
        UpdateToolbar();
    }

    private void UpdateToolbar()
    {
        var panel = _vm.ActivePanel;
        PanelTitle.Text = panel.Title;
        Title = string.IsNullOrEmpty(panel.Title) ? "FileFluss" : $"{panel.Title} — FileFluss";
        BackButton.IsEnabled = panel.CanGoBack;
        ForwardButton.IsEnabled = panel.CanGoForward;
        UpButton.IsEnabled = panel.CanGoUp;
        HiddenButton.IsChecked = panel.ShowHiddenFiles;
        var dual = !_vm.SinglePaneMode;
        SyncButton.IsEnabled = dual && !_vm.Left.IsReadOnly || dual && !_vm.Right.IsReadOnly;
        CompareButton.IsEnabled = dual;
        UpdateTransfersButton();
    }

    private void UpdateTransfersButton()
    {
        var transfers = _services.Transfers;
        TransfersButton.ToolTip = transfers.ToolbarText;
        TransfersButton.Content = transfers.RunningFraction is { } fraction ? $"{(int)Math.Round(fraction * 100)}%" : L("Transfers");
    }

    // MARK: - Toolbar handlers

    private async void OnBack(object sender, RoutedEventArgs e) => await _vm.ExecuteAsync(KeyboardCommand.HistoryBack);

    private async void OnForward(object sender, RoutedEventArgs e) => await _vm.ExecuteAsync(KeyboardCommand.HistoryForward);

    private async void OnUp(object sender, RoutedEventArgs e) => await _vm.ExecuteAsync(KeyboardCommand.ParentDirectory);

    private async void OnRefresh(object sender, RoutedEventArgs e) => await _vm.ExecuteAsync(KeyboardCommand.RefreshAll);

    private void OnSearch(object sender, RoutedEventArgs e) => ShowSearch();

    private void OnSync(object sender, RoutedEventArgs e) => ShowSync();

    private void OnCompare(object sender, RoutedEventArgs e) => ShowCompare();

    private void OnCloudAccounts(object sender, RoutedEventArgs e) => ShowSettings("cloud");

    private void OnToggleHidden(object sender, RoutedEventArgs e)
    {
        var panel = _vm.ActivePanel;
        panel.ShowHiddenFiles = HiddenButton.IsChecked == true;
        if (!_vm.SinglePaneMode)
        {
            _vm.OtherPanel.ShowHiddenFiles = panel.ShowHiddenFiles;
        }
    }

    private void OnTransfers(object sender, RoutedEventArgs e)
    {
        _transfersPopup ??= TransfersPopup.Create(this, _services.Transfers, TransfersButton);
        _transfersPopup.IsOpen = TransfersButton.IsChecked == true;
    }

    // MARK: - IShell

    public void FocusPanel(PanelSide side)
    {
        _vm.ActiveSide = side;
        Dispatcher.BeginInvoke(() => View(side).FocusList(), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void BeginRename(PanelSide side) => View(side).BeginRename();

    public void ApplySelection(PanelSide side, SelectionAction action) => View(side).ApplySelection(action);

    public void ShowGoToFolder() => View(_vm.ActiveSide).BeginEditPath();

    public void ShowSettings(string? page = null) => Views.Settings.SettingsWindow.ShowPage(this, _services, _vm, page);

    public void ShowAddAccount() => AccountDialog.Add(this, _services);

    public void ShowSearch() => WindowsRegistry.Search?.Invoke(this, _vm);

    public void ShowCompare()
    {
        if (!_vm.SinglePaneMode)
        {
            WindowsRegistry.Compare?.Invoke(this, _vm);
        }
    }

    public void ShowSync()
    {
        if (!_vm.SinglePaneMode)
        {
            WindowsRegistry.Sync?.Invoke(this, _vm);
        }
    }

    public void ShowStorageAnalysis(PanelLocation location) => WindowsRegistry.StorageAnalysis?.Invoke(this, _vm, location);

    public void ShowQuickLook(PanelViewModel panel) => QuickLookWindow.Toggle(this, _vm, panel);

    public void ShowShareLink(PanelViewModel panel, CloudFileItem item) => ShareLinkDialog.Show(this, _services, panel, item);

    public void ShowProperties(PanelViewModel panel, CloudFileItem item)
    {
        if (panel.IsLocal)
        {
            ShellFileOperations.ShowProperties(item.Path, new System.Windows.Interop.WindowInteropHelper(this).Handle);
            return;
        }

        var lines = new List<string>
        {
            $"{L("Kind")}: {ShellIcons.TypeName(item.Name, item.IsDirectory)}",
            $"{L("Where")}: {panel.Account?.DisplayName} — {item.Path}",
        };
        if (!item.IsDirectory)
        {
            lines.Add($"{L("Size")}: {ByteFormatter.Format(item.Size)} ({item.Size:N0} {L("bytes")})");
        }

        if (item.HasKnownModificationDate)
        {
            lines.Add($"{L("Date Modified")}: {item.ModificationDate.ToString("F", Culture)}");
        }

        if (item.CreationDate > CloudFileItem.UnknownDate)
        {
            lines.Add($"{L("Date Created")}: {item.CreationDate.ToString("F", Culture)}");
        }

        if (!string.IsNullOrEmpty(item.Checksum))
        {
            lines.Add($"{L("Checksum")}: {item.Checksum}");
        }

        MessageDialog.Info(this, item.Name, string.Join(Environment.NewLine, lines));
    }

    private void ShowHelp() => WindowsRegistry.Help?.Invoke(this);

    private void ShowAbout() => WindowsRegistry.About?.Invoke(this);

    // MARK: - Support log banner, space-check HUD, taskbar progress

    private MenuItem? _supportLogItem;
    private OverlayAdorner? _supportLogBanner;
    private TextBlock? _supportLogSeconds;
    private OverlayAdorner? _spaceCheckHud;
    private DispatcherTimer? _spaceCheckDelay;
    private DispatcherTimer? _taskbarErrorTimer;

    private void WireStatusOverlays()
    {
        SupportLogService.Shared.Owner = this;
        SupportLogService.Shared.Changed += (_, _) =>
        {
            UpdateSupportLogItem();
            UpdateSupportLogBanner();
        };

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsCheckingSpace))
            {
                UpdateSpaceCheckHud();
            }
        };

        TaskbarItemInfo = new TaskbarItemInfo();
        _services.Transfers.PropertyChanged += (_, _) => UpdateTaskbarProgress();
        _services.Transfers.TransferCompleted += (_, transfer) =>
        {
            if (transfer.HasErrors && !transfer.IsCancelled)
            {
                FlashTaskbarError();
            }
        };
    }

    /// <summary>macOS: "Support Log", or "Support Log (Recording…)" and disabled while it runs.</summary>
    private void UpdateSupportLogItem()
    {
        if (_supportLogItem is null)
        {
            return;
        }

        var recording = SupportLogService.Shared.IsRecording;
        _supportLogItem.Header = recording ? L("Support Log (Recording…)") : L("Support Log");
        _supportLogItem.IsEnabled = !recording;
    }

    /// <summary>The capsule at the top of the window while a support log records, with the seconds left.</summary>
    private void UpdateSupportLogBanner()
    {
        var log = SupportLogService.Shared;
        if (!log.IsRecording)
        {
            _supportLogBanner?.Remove();
            _supportLogBanner = null;
            _supportLogSeconds = null;
            return;
        }

        if (_supportLogBanner is null)
        {
            var dot = new TextBlock
            {
                Text = "",
                FontFamily = new System.Windows.Media.FontFamily(Glyphs.FontFamily),
                Foreground = System.Windows.Media.Brushes.Red,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            dot.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.8))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            });
            var text = new TextBlock { Text = L("Recording support log — reproduce the issue now"), VerticalAlignment = VerticalAlignment.Center };
            _supportLogSeconds = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Documents.Typography.SetNumeralAlignment(_supportLogSeconds, System.Windows.FontNumeralAlignment.Tabular);
            _supportLogSeconds.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, text, _supportLogSeconds } };
            var capsule = OverlayAdorner.Card(new Border { Padding = new Thickness(14, 8, 14, 8), Child = row }, cornerRadius: 18);
            capsule.HorizontalAlignment = HorizontalAlignment.Center;
            capsule.VerticalAlignment = VerticalAlignment.Top;
            capsule.Margin = new Thickness(16, 8, 16, 0);
            capsule.IsHitTestVisible = false;
            System.Windows.Automation.AutomationProperties.SetName(capsule, text.Text);
            _supportLogBanner = OverlayAdorner.Show(PanelsGrid, capsule);
        }

        if (_supportLogSeconds is not null)
        {
            _supportLogSeconds.Text = $"{log.SecondsRemaining}s";
        }
    }

    /// <summary>
    /// macOS <c>spaceCheckHUD</c>, plus a Cancel: shown while a transfer's size is summed, after a
    /// short delay so a quick check doesn't flash it.
    /// </summary>
    private void UpdateSpaceCheckHud()
    {
        if (!_vm.IsCheckingSpace)
        {
            _spaceCheckDelay?.Stop();
            _spaceCheckHud?.Remove();
            _spaceCheckHud = null;
            return;
        }

        if (_spaceCheckHud is not null || _spaceCheckDelay is { IsEnabled: true })
        {
            return;
        }

        _spaceCheckDelay ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _spaceCheckDelay.Tick -= OnSpaceCheckDelay;
        _spaceCheckDelay.Tick += OnSpaceCheckDelay;
        _spaceCheckDelay.Start();
    }

    private void OnSpaceCheckDelay(object? sender, EventArgs e)
    {
        _spaceCheckDelay?.Stop();
        if (!_vm.IsCheckingSpace || _spaceCheckHud is not null)
        {
            return;
        }

        var stack = new StackPanel { Width = 232 };
        stack.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 0, 0, 14) });
        stack.Children.Add(new TextBlock { Text = L("Checking available space…"), TextAlignment = TextAlignment.Center, FontWeight = FontWeights.SemiBold });
        var detail = new TextBlock
        {
            Text = L("Calculating the size of the items and the destination."),
            Style = (Style)FindResource("Caption"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            Margin = new Thickness(0, 6, 0, 14),
        };
        stack.Children.Add(detail);
        var cancel = new Button { Content = L("Cancel"), MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => _vm.CancelSpaceChecks();
        stack.Children.Add(cancel);
        var card = OverlayAdorner.Card(new Border { Padding = new Thickness(24), Child = stack }, cornerRadius: 14);
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetName(card, L("Checking available space…"));
        System.Windows.Automation.AutomationProperties.SetLiveSetting(card, System.Windows.Automation.AutomationLiveSetting.Polite);
        _spaceCheckHud = OverlayAdorner.Show(PanelsGrid, card);
    }

    /// <summary>The taskbar button's progress: the transfers' overall fraction while they run, nothing when idle.</summary>
    private void UpdateTaskbarProgress()
    {
        if (TaskbarItemInfo is not { } taskbar || _taskbarErrorTimer is { IsEnabled: true })
        {
            return;
        }

        if (_services.Transfers.RunningFraction is { } fraction)
        {
            taskbar.ProgressState = fraction > 0 ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.Indeterminate;
            taskbar.ProgressValue = fraction;
        }
        else
        {
            taskbar.ProgressState = TaskbarItemProgressState.None;
        }
    }

    /// <summary>A failed transfer turns the taskbar button red for a few seconds.</summary>
    private void FlashTaskbarError()
    {
        if (TaskbarItemInfo is not { } taskbar)
        {
            return;
        }

        taskbar.ProgressState = TaskbarItemProgressState.Error;
        taskbar.ProgressValue = 1;
        if (_taskbarErrorTimer is null)
        {
            _taskbarErrorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _taskbarErrorTimer.Tick += (_, _) =>
            {
                _taskbarErrorTimer.Stop();
                UpdateTaskbarProgress();
            };
        }

        _taskbarErrorTimer.Stop();
        _taskbarErrorTimer.Start();
    }

    // MARK: - Window placement

    private void RestorePlacement()
    {
        if (_services.Settings.MainWindow is not { } placement)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var visible = SystemParameters.VirtualScreenLeft <= placement.Left + 100 &&
                      placement.Left + 100 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                      SystemParameters.VirtualScreenTop <= placement.Top + 20 &&
                      placement.Top + 20 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (!visible)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        Left = placement.Left;
        Top = placement.Top;
        Width = Math.Max(MinWidth, placement.Width);
        Height = Math.Max(MinHeight, placement.Height);
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_services.Transfers.HasRunning &&
            !MessageDialog.Confirm(this, L("Transfers are still running"), L("Quitting cancels them. Files already transferred will remain."), L("Quit Anyway"), destructive: true))
        {
            e.Cancel = true;
            return;
        }

        foreach (var transfer in _services.Transfers.Running.ToList())
        {
            transfer.Cancel();
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _services.Settings.MainWindow = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
        _vm.RecordSessionLocations();
    }
}

/// <summary>
/// Late-bound openers for windows built in their own files, so MainWindow doesn't need
/// every window type at compile time of this file and features can be registered at startup.
/// </summary>
public static class WindowsRegistry
{
    public static Action<Window, MainViewModel>? Search { get; set; }

    public static Action<Window, MainViewModel>? Compare { get; set; }

    public static Action<Window, MainViewModel>? Sync { get; set; }

    public static Action<Window, MainViewModel, PanelLocation>? StorageAnalysis { get; set; }

    public static Action<Window>? Help { get; set; }

    public static Action<Window>? About { get; set; }
}
