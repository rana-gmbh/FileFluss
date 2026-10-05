// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.Core;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
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

        var services = new AppServices(SettingsStore.DataDirectory);
        AppServices.Current = Services = services;
        Localization.Use(services.Settings.Language);
        ThemeBrushes.Install(this);
        ICloudDriveLocator.Registry = new WindowsRegistryReader();
        SupportLogger.Shared.Log("FileFluss for Windows starting", "App");
        Features.Register(services);

        var vm = new MainViewModel(services);
        var window = new MainWindow(vm, services);
        MainWindow = window;
        window.Show();

        // The index knows unplugged drives a restored panel may be showing.
        await services.Search.StartAsync(services.Accounts.Accounts.Select(a => a.Id).ToHashSet());

        // Local folders first, before any network work: with a dozen accounts reconnecting,
        // a panel shouldn't wait on all of them to show the home folder.
        await vm.RestoreStartupLocationsAsync(cloudOnly: false);
        await services.Accounts.ReconnectSavedAccountsAsync();
        await vm.RestoreStartupLocationsAsync(cloudOnly: true);
        Features.AfterStartup(window, vm, services);

        if (services.Settings.CacheAutoManage)
        {
            _ = CacheManager.RunAutoMaintenanceAsync(services.Settings.CacheAutoDeleteDays, services.Settings.CacheMaxSizeMB);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SingleInstance.Release();
        foreach (var account in Services?.Accounts.Accounts.ToList() ?? [])
        {
            Services!.Hub.Get(account.Id)?.Dispose();
        }

        base.OnExit(e);
    }
}
