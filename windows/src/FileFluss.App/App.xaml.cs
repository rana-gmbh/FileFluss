// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.Core;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers.ICloud;
using FileFluss.Core.Settings;
using FileFluss.Native;

using Localization = FileFluss.Core.Localization;

namespace FileFluss.App;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public AppServices Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashLog.Install(this);

        // One FileFluss at a time: a second launch brings the running window forward.
        if (!SingleInstance.TryClaim(() => Dispatcher.Invoke(() =>
            {
                if (MainWindow is { } window)
                {
                    if (window.WindowState == WindowState.Minimized)
                    {
                        window.WindowState = WindowState.Normal;
                    }

                    window.Activate();
                }
            })))
        {
            Shutdown();
            return;
        }

        StartupTrace.Mark("OnStartup");
        var services = new AppServices(SettingsStore.DataDirectory);
        AppServices.Current = Services = services;

        // Logos and the shell's first icons are decoded on other threads while the window is built.
        IconCache.Prewarm(services.Accounts.Accounts.Select(a => a.ProviderType.LogoAssetName()));
        Localization.Use(services.Settings.Language);
        ThemeBrushes.Install(this);

        // Esc closes every secondary window (Help, About, Transfer Details, …), as on macOS. Bubbling,
        // so a control that uses Esc itself — an open drop-down, an inline editor — gets it first.
        EventManager.RegisterClassHandler(typeof(Window), UIElement.KeyDownEvent, new System.Windows.Input.KeyEventHandler((sender, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && !e.Handled && sender is Window window && window != MainWindow)
            {
                e.Handled = true;
                window.Close();
            }
        }));
        ICloudDriveLocator.Registry = new WindowsRegistryReader();
#if DEBUG
        // Debug builds mirror the support log to a file, for diagnosing runs driven by UI automation.
        var debugLog = System.IO.Path.Combine(SettingsStore.DataDirectory, "debug.log");
        SupportLogger.Shared.EntryAdded += entry =>
        {
            try
            {
                System.IO.File.AppendAllText(debugLog, $"{entry.Date:HH:mm:ss.fff} [{entry.Level}] {entry.Category}: {entry.Message}{Environment.NewLine}");
            }
            catch (System.IO.IOException)
            {
            }
        };
#endif
        SupportLogger.Shared.Log("FileFluss for Windows starting", "App");
        Features.Register(services);

        var vm = new MainViewModel(services);
        var window = new MainWindow(vm, services);
        MainWindow = window;
#if DEBUG
        window.ContentRendered += (_, _) => StartupTrace.Mark("first window rendered");
#endif
        window.Show();
        StartupTrace.Mark("window shown");

        // The index opens on the thread pool meanwhile; only a panel restoring an unplugged
        // drive waits for it (the index knows those drives).
        var indexStarted = services.Search.StartAsync(services.Accounts.Accounts.Select(a => a.Id).ToHashSet());

        // Local folders first, before any network work: with a dozen accounts reconnecting,
        // a panel shouldn't wait on all of them to show the home folder.
        await vm.RestoreStartupLocationsAsync(cloudOnly: false, indexStarted);
        StartupTrace.Mark("local panels listed");
        await indexStarted;
        await services.Accounts.ReconnectSavedAccountsAsync();
        await vm.RestoreStartupLocationsAsync(cloudOnly: true);
        StartupTrace.Mark("accounts reconnected, cloud panels restored");
        Features.AfterStartup(window, vm, services);

        if (services.Settings.CacheAutoManage)
        {
            _ = CacheManager.RunAutoMaintenanceAsync(services.Settings.CacheAutoDeleteDays, services.Settings.CacheMaxSizeMB);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SingleInstance.Release();

        // Settings are written shortly after they change; the last ones (window frame, session folders) now.
        Services?.SettingsStore.Flush();
        Services?.Mount.UnmountAllOnExit();

        // Virtual files can't be pasted once FileFluss is gone; don't leave a dead promise behind.
        Services?.Export.ReleaseClipboard(clear: true);
        foreach (var account in Services?.Accounts.Accounts.ToList() ?? [])
        {
            Services!.Hub.Get(account.Id)?.Dispose();
        }

        base.OnExit(e);
    }
}
