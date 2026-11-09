// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.App.Views.Accounts;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core;
using FileFluss.Core.Commands;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Formatting;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Native;
using Microsoft.Win32;
using static FileFluss.App.Views.Settings.SettingsKit;
using static FileFluss.Core.Localization;

using Localization = FileFluss.Core.Localization;

namespace FileFluss.App.Views.Settings;

/// <summary>
/// Settings in the shape of Windows 11 Settings: a navigation pane with the macOS tabs —
/// General, Cloud Accounts, Storage, Index Status, Keyboard Map — and one scrolling column of
/// cards per page. Changes apply and persist immediately; there is no OK button.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly ListBox _nav = new();
    private readonly ContentControl _page = new();
    private readonly TextBlock _title = new() { FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(32, 20, 32, 8) };
    private string _current = "general";

    private static readonly (string Key, string Glyph, string Label)[] Pages =
    [
        ("general", Glyphs.Settings, "General"),
        ("cloud", Glyphs.Cloud, "Cloud Accounts"),
        ("storage", Glyphs.HardDrive, "Storage"),
        ("index", Glyphs.Search, "Index Status"),
        ("keyboard", "", "Keyboard Map"),
    ];

    private SettingsWindow(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        Title = L("FileFluss Settings");
        Width = 980;
        Height = 760;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _nav.BorderThickness = new Thickness(0);
        _nav.Background = System.Windows.Media.Brushes.Transparent;
        _nav.Margin = new Thickness(8, 12, 8, 12);
        _nav.Width = 240;
        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is ListBoxItem { Tag: string key } && key != _current)
            {
                ShowPage(key);
            }
        };
        BuildNav();

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(32, 0, 32, 28),
            Content = _page,
        };
        var right = new DockPanel();
        DockPanel.SetDock(_title, Dock.Top);
        right.Children.Add(_title);
        right.Children.Add(scroller);

        var root = new DockPanel();
        DockPanel.SetDock(_nav, Dock.Left);
        root.Children.Add(_nav);
        root.Children.Add(right);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox)
            {
                Close();
            }
        };
        services.Accounts.Accounts.CollectionChanged += OnAccountsChanged;
        Localization.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            // Both sources outlive the window: left subscribed, they would keep every closed
            // Settings window (and its pages) alive and rebuilding.
            services.Accounts.Accounts.CollectionChanged -= OnAccountsChanged;
            Localization.LanguageChanged -= OnLanguageChanged;
            _open = null;
        };
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (IsLoaded)
        {
            Title = L("FileFluss Settings");
            BuildNav();
            ShowPage(_current);
        }
    }

    private void BuildNav()
    {
        _nav.Items.Clear();
        foreach (var (key, glyph, label) in Pages)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 4, 6) };
            row.Children.Add(Glyph(glyph));
            row.Children.Add(new TextBlock { Text = L(label), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var item = new ListBoxItem { Content = row, Tag = key, IsSelected = key == _current };
            System.Windows.Automation.AutomationProperties.SetName(item, L(label));
            _nav.Items.Add(item);
        }
    }

    private void OnAccountsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_current == "cloud")
        {
            ShowPage("cloud");
        }
    }

    public static void ShowPage(Window owner, AppServices services, MainViewModel main, string? page)
    {
        _open ??= new SettingsWindow(services, main) { Owner = owner };
        _open.Show();
        _open.Activate();
        _open.ShowPage(page ?? _open._current);
    }

    public void ShowPage(string key)
    {
        _current = key;
        foreach (ListBoxItem item in _nav.Items)
        {
            item.IsSelected = (string)item.Tag == key;
        }

        _title.Text = L(Pages.First(p => p.Key == key).Label);
        _page.Content = key switch
        {
            "cloud" => CloudPage(),
            "storage" => StoragePage(),
            "index" => new IndexStatusPage(_services),
            "keyboard" => new KeyboardMapPage(_services),
            _ => GeneralPage(),
        };
    }

    // MARK: - General

    private FrameworkElement GeneralPage()
    {
        var s = _services.Settings;
        var page = new StackPanel();

        var languages = Enum.GetValues<AppLanguage>().Select(l => (l, Localization.DisplayName(l))).ToList();
        page.Children.Add(Card(L("Language"), L("System Default follows the language selected in Windows."),
            Choice(languages, s.Language, value =>
            {
                s.Language = value;
                Localization.Use(value);
            })));

        page.Children.Add(Heading(L("Files")));
        page.Children.Add(Card(L("Show hidden files by default"), null, Toggle(() => s.ShowHiddenFiles, v => s.ShowHiddenFiles = v)));
        page.Children.Add(Card(L("Confirm before deleting"), null, Toggle(() => s.ConfirmDelete, v => s.ConfirmDelete = v)));
        page.Children.Add(Card(L("Check available space before copy or move"),
            L("Warns before a transfer that would exceed the destination's storage quota or free disk space. Calculating sizes can add a noticeable delay, so this is off by default for faster file operations."),
            Toggle(() => s.CheckSpaceBeforeTransfer, v => s.CheckSpaceBeforeTransfer = v)));
        page.Children.Add(Card(L("Copy direct download links when available"),
            L("\"Copy Share Link\" then copies a link that downloads the file straight away, instead of the provider's preview page. Providers that offer no direct link always give their own page."),
            Toggle(() => s.ShareLinkPreferDirectDownload, v => s.ShareLinkPreferDirectDownload = v)));

        var terminals = TerminalLauncher.InstalledTerminals().Select(t => (t.Id, t.Name)).ToList();
        if (!string.IsNullOrEmpty(s.Terminal) && terminals.All(t => t.Id != s.Terminal))
        {
            terminals.Add((s.Terminal, System.IO.Path.GetFileNameWithoutExtension(s.Terminal)));
        }

        terminals.Add(("choose", L("Choose…")));
        var current = string.IsNullOrEmpty(s.Terminal) ? terminals.FirstOrDefault().Id ?? "choose" : s.Terminal;
        ComboBox? terminalCombo = null;
        terminalCombo = Choice(terminals, current, value =>
        {
            if (value == "choose")
            {
                var dialog = new OpenFileDialog { Filter = L("Programs") + "|*.exe", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
                if (dialog.ShowDialog(this) == true)
                {
                    s.Terminal = dialog.FileName;
                }

                ShowPage("general");
                return;
            }

            s.Terminal = value;
        });
        page.Children.Add(Card(L("Open in Terminal uses"), null, terminalCombo));

        page.Children.Add(Heading(L("Sidebar")));
        page.Children.Add(Card(L("Show \"Add Cloud Account\" in sidebars"), null, Toggle(() => s.ShowSidebarAddAccount, v => s.ShowSidebarAddAccount = v)));
        page.Children.Add(Card(L("Allow removing cloud accounts from sidebar context menu"), null, Toggle(() => s.AllowSidebarRemoveAccount, v => s.AllowSidebarRemoveAccount = v)));
        page.Children.Add(Card(L("Show transfers in sidebars"), L("Transfers are always available from the Transfers button in the toolbar."), Toggle(() => s.ShowSidebarTransfers, v => s.ShowSidebarTransfers = v)));
        page.Children.Add(Card(L("Show calculated folder sizes in sidebars"), null, Toggle(() => s.ShowSidebarFolderSizes, v => s.ShowSidebarFolderSizes = v)));

        page.Children.Add(Heading(L("When FileFluss opens")));
        var modes = new List<(PanelStartupMode, string)>
        {
            (PanelStartupMode.Home, L("Home folder")),
            (PanelStartupMode.LastSession, L("The folders from last time")),
            (PanelStartupMode.Specific, L("Specific folders")),
        };
        page.Children.Add(Card(L("When FileFluss opens"),
            L("A folder that no longer exists — a deleted folder, an unplugged drive, a disconnected cloud account — opens at your home folder instead."),
            Choice(modes, s.PanelStartupMode, value =>
            {
                s.PanelStartupMode = value;
                ShowPage("general");
            })));
        if (s.PanelStartupMode == PanelStartupMode.Specific)
        {
            page.Children.Add(StartupFolderCard(L("Left panel"), () => s.SpecificLeft, v => s.SpecificLeft = v));
            page.Children.Add(StartupFolderCard(L("Right panel"), () => s.SpecificRight, v => s.SpecificRight = v));
        }

        page.Children.Add(Heading(L("Updates")));
        page.Children.Add(Card(L("Check for updates automatically"), L("FileFluss checks daily and asks before installing."),
            Toggle(() => s.AutomaticUpdateChecks, v => s.AutomaticUpdateChecks = v)));

        page.Children.Add(Heading(L("Welcome")));
        page.Children.Add(Card(L("Show Welcome Screen Again"), null, Button(L("Show"), () =>
        {
            s.HasCompletedWelcome = false;
            WelcomeRequested?.Invoke(this, EventArgs.Empty);
        })));
        return page;
    }

    /// <summary>Raised by "Show Welcome Screen Again".</summary>
    public static event EventHandler? WelcomeRequested;

    private Border StartupFolderCard(string title, Func<PanelLocation?> get, Action<PanelLocation?> set)
    {
        var path = get()?.Path;
        var label = new TextBlock { Text = string.IsNullOrEmpty(path) ? L("Home folder") : path, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Button(L("Choose…"), () =>
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog(this) == true)
            {
                set(PanelLocation.Local(dialog.FolderName));
                ShowPage("general");
            }
        }));
        if (!string.IsNullOrEmpty(path))
        {
            var reset = Button(L("Reset"), () =>
            {
                set(null);
                ShowPage("general");
            });
            reset.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(reset);
        }

        return Card(title, label.Text, buttons);
    }

    // MARK: - Cloud accounts

    private FrameworkElement CloudPage()
    {
        var page = new StackPanel();
        var accounts = _services.Accounts.Accounts;
        if (accounts.Count == 0)
        {
            page.Children.Add(Wrap(new StackPanel
            {
                Margin = new Thickness(0, 24, 0, 24),
                Children =
                {
                    new TextBlock { Text = L("No Cloud Accounts"), FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = L("Add a cloud account to get started."), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 14) },
                    WrapCenter(Button(L("Add Account…"), () => AccountDialog.Add(this, _services), accent: true)),
                },
            }));
            return page;
        }

        foreach (var account in accounts)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            if (IconCache.Logo(account.ProviderType) is { } logo)
            {
                header.Children.Add(new Image { Source = logo, Width = 22, Height = 22, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            }

            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, account.IsConnected && !_services.Accounts.NeedsSignIn(account.Id) ? "ConnectedBrush" : "DisconnectedBrush");
            header.Children.Add(dot);
            header.Children.Add(new TextBlock { Text = account.DisplayName, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (account.ProviderType != CloudProviderType.ICloud)
            {
                buttons.Children.Add(Button(L("Edit…"), () => AccountDialog.Edit(this, _services, account.Id)));
            }

            var rename = Button(L("Rename…"), () =>
            {
                if (MessageDialog.Prompt(this, L("Rename Account"), string.Empty, account.DisplayName, L("Rename")) is { } name)
                {
                    _services.Accounts.Rename(account.Id, name);
                    ShowPage("cloud");
                }
            });
            rename.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(rename);
            var remove = Button(L("Remove"), async () =>
            {
                if (MessageDialog.Confirm(this, L("Remove Account"), L("Remove \"{0}\" from FileFluss? Files in the account are not touched.", account.DisplayName), L("Remove"), destructive: true))
                {
                    await _services.Accounts.RemoveAsync(account.Id);
                }
            });
            remove.Margin = new Thickness(8, 0, 0, 0);
            remove.SetResourceReference(ForegroundProperty, "DangerBrush");
            buttons.Children.Add(remove);

            var row = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(header);
            page.Children.Add(Wrap(row));
        }

        var add = Button(L("Add Account…"), () => AccountDialog.Add(this, _services), accent: true);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 12, 0, 0);
        page.Children.Add(add);
        return page;
    }

    private static FrameworkElement WrapCenter(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        return element;
    }

    // MARK: - Storage

    private FrameworkElement StoragePage()
    {
        var s = _services.Settings;
        var page = new StackPanel();
        var size = new TextBlock { Text = L("Calculating…"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Button clear = null!;
        clear = Button(L("Clear Cache…"), async () =>
        {
            if (!MessageDialog.Confirm(this, L("Clear the cache?"),
                    L("This removes all cached cloud previews and downloaded files. Files will be re-downloaded on next preview."),
                    L("Clear Cache"), destructive: true))
            {
                return;
            }

            clear.IsEnabled = false;
            size.Text = L("Calculating…");
            try
            {
                await CacheManager.ClearAsync();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Files in use stay; the size below shows what is left.
                TaskFailures.ShowFailure(e, L("Clear Cache"), this);
            }

            await RefreshSizeAsync();
        });
        clear.IsEnabled = false;
        clear.SetResourceReference(ForegroundProperty, "DangerBrush");

        async Task RefreshSizeAsync()
        {
            long bytes;
            try
            {
                bytes = await CacheManager.SizeAsync();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Never leave "Calculating…" and a disabled button behind.
                SupportLogger.Shared.Log($"Measuring the cache failed: {e.Message}", "Settings", LogLevel.Notice);
                size.Text = string.Empty;
                clear.IsEnabled = true;
                return;
            }

            size.Text = ByteFormatter.Format(bytes);

            // Nothing to clear, nothing to confirm (macOS disables the button at zero).
            clear.IsEnabled = bytes > 0;
        }

        page.Children.Add(Card(L("Cache Usage"), L("Cloud files opened or previewed are kept here so reopening them is instant."),
            new StackPanel { Orientation = Orientation.Horizontal, Children = { size, clear } }));
        _ = RefreshSizeAsync();

        page.Children.Add(Heading(L("Automatic management")));
        page.Children.Add(Card(L("Automatically manage cache"), L("At launch, delete old cached files and keep the cache under its size limit."),
            Toggle(() => s.CacheAutoManage, v => s.CacheAutoManage = v)));
        var days = new List<(int, string)> { (1, L("1 day")), (3, L("3 days")), (7, L("1 week")), (14, L("2 weeks")), (30, L("1 month")), (90, L("3 months")) };
        if (days.All(d => d.Item1 != s.CacheAutoDeleteDays))
        {
            days.Add((s.CacheAutoDeleteDays, L("{0} days", s.CacheAutoDeleteDays)));
        }

        page.Children.Add(Card(L("Delete files older than"), null, Choice(days, s.CacheAutoDeleteDays, v => s.CacheAutoDeleteDays = v, 160)));

        // macOS: a slider in 50 MB steps beside an MB field; either one, once committed, trims
        // the cache to the new limit straight away rather than at the next launch.
        var sizeField = new TextBox { Text = s.CacheMaxSizeMB.ToString(Culture), Width = 70, TextAlignment = TextAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        System.Windows.Automation.AutomationProperties.SetName(sizeField, L("Cache Size Limit"));
        var slider = new Slider { Minimum = CacheManager.MinSizeMB, Maximum = CacheManager.MaxSizeMB, Value = s.CacheMaxSizeMB, Width = 200, SmallChange = 50, LargeChange = 500, TickFrequency = 50, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(slider, L("Cache Size Limit"));
        var committing = false;
        void Commit(int mb)
        {
            committing = true;
            slider.Value = mb;
            sizeField.Text = mb.ToString(Culture);
            committing = false;
            if (mb == s.CacheMaxSizeMB)
            {
                return;
            }

            s.CacheMaxSizeMB = mb;
            _ = EnforceAsync(mb);
        }

        async Task EnforceAsync(int mb)
        {
            try
            {
                await CacheManager.EnforceMaxSizeAsync(mb);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                SupportLogger.Shared.Log($"Trimming the cache failed: {e.Message}", "Settings", LogLevel.Notice);
            }

            await RefreshSizeAsync();
        }

        slider.ValueChanged += (_, e) =>
        {
            if (!committing)
            {
                sizeField.Text = CacheManager.SnapSliderMB(e.NewValue).ToString(Culture);
            }
        };
        slider.PreviewMouseUp += (_, _) => Commit(CacheManager.SnapSliderMB(slider.Value));
        slider.PreviewKeyUp += (_, _) => Commit(CacheManager.SnapSliderMB(slider.Value));
        sizeField.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit(CacheManager.ParseFieldMB(sizeField.Text, s.CacheMaxSizeMB));
                e.Handled = true;
            }
        };
        sizeField.LostKeyboardFocus += (_, _) => Commit(CacheManager.ParseFieldMB(sizeField.Text, s.CacheMaxSizeMB));
        var mbLabel = new TextBlock { Text = L("MB"), VerticalAlignment = VerticalAlignment.Center };
        mbLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        page.Children.Add(Card(L("Cache Size Limit"), L("When the cache exceeds this size, the oldest files are removed first. Applied on launch and when this value changes."),
            new StackPanel { Orientation = Orientation.Horizontal, Children = { slider, sizeField, mbLabel } }));

        page.Children.Add(Heading(L("Cache Location")));
        var location = string.IsNullOrEmpty(s.CustomCacheFolder) ? L("System temporary folder") : s.CustomCacheFolder;
        var locationButtons = new StackPanel { Orientation = Orientation.Horizontal };
        locationButtons.Children.Add(Button(L("Choose…"), () =>
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog(this) == true)
            {
                s.CustomCacheFolder = dialog.FolderName;
                ShowPage("storage");
            }
        }));
        if (!string.IsNullOrEmpty(s.CustomCacheFolder))
        {
            var reset = Button(L("Use Default"), () =>
            {
                s.CustomCacheFolder = null;
                ShowPage("storage");
            });
            reset.Margin = new Thickness(8, 0, 0, 0);
            locationButtons.Children.Add(reset);
        }

        page.Children.Add(Card(location,
            L("Downloads are staged through this folder. Point it at a folder on an external drive to keep big transfers to that drive off your system disk. If the folder isn't available, the system temporary folder is used."),
            locationButtons));
        page.Children.Add(Card(L("Open Cache Folder"), null, Button(L("Open"), () =>
        {
            try
            {
                var folder = StagingLocation.Base();
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                MessageDialog.Error(this, L("Open Cache Folder"), e.Message);
            }
        })));
        return page;
    }
}
