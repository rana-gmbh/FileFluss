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

    /// <summary>How long exit waits for provider connections to close.</summary>
    private static readonly TimeSpan ProviderDisposalWait = TimeSpan.FromSeconds(3);

    public AppServices Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Until the main window exists nothing may end the app by closing: with
        // OnMainWindowClose, a window shown before it (an error, a prompt) would become
        // Application.MainWindow — WPF makes the first window constructed the main one — and
        // closing it would quietly shut FileFluss down.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
#if DEBUG
        // Its own data folder, started once from the release app's before anything writes to
        // it (macOS 1.6): debugging never changes or removes the accounts of the FileFluss the
        // user works with.
        if (Core.Diagnostics.TestAccountSnapshot.SeedDebugFolder(SettingsStore.ReleaseDataDirectory, SettingsStore.DataDirectory))
        {
            FileFluss.Native.PrivateFolder.Apply(System.IO.Path.Combine(SettingsStore.DataDirectory, "credentials"));
        }
#endif
        CrashLog.Install(this);

        // One FileFluss at a time: a second launch brings the running window forward.
        if (!SingleInstance.TryClaim(BringToFront))
        {
            Shutdown();
            return;
        }

        StartupTrace.Mark("OnStartup");
        MainWindow window;
        MainViewModel vm;
        AppServices services;
        try
        {
            services = new AppServices(SettingsStore.DataDirectory);
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
                catch (Exception logFailure) when (logFailure is System.IO.IOException or UnauthorizedAccessException)
                {
                }
            };
#endif
            SupportLogger.Shared.Log($"FileFluss for Windows starting (process {Environment.ProcessId})", "App");
            Lifecycle.Install(this);
            Features.Register(services);

            vm = new MainViewModel(services);
            window = new MainWindow(vm, services);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
#if DEBUG
            window.ContentRendered += (_, _) => StartupTrace.Mark("first window rendered");
#endif
            window.Show();
            StartupTrace.Mark("window shown");
        }
        catch (Exception failure)
        {
            // Nothing to show and nothing to run: an invisible FileFluss would keep holding the
            // single-instance lock, and every later launch would hand over to it and vanish.
            CrashLog.Write("startup", failure);
            MessageBox.Show(
                $"FileFluss could not start: {failure.Message}{Environment.NewLine}{Environment.NewLine}{CrashLog.LogPath}",
                "FileFluss",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Everything below is best-effort: one step failing must not keep the others (or the
        // window) from working. Startup never waits on the network.

        // The index opens on the thread pool meanwhile; only a panel restoring an unplugged
        // drive waits for it (the index knows those drives).
        var indexStarted = Step("search index", () => services.Search.StartAsync(services.Accounts.Accounts.Select(a => a.Id).ToHashSet()));

        // Local folders first, before any network work: with a dozen accounts reconnecting,
        // a panel shouldn't wait on all of them to show the home folder.
        await Step("local panels", () => vm.RestoreStartupLocationsAsync(cloudOnly: false, indexStarted));
        StartupTrace.Mark("local panels listed");
        await indexStarted;
        await Step("accounts", () => services.Accounts.ReconnectSavedAccountsAsync());

        // Cloud panels list in the background: a slow or hanging server must not hold back
        // the rest of startup (command-line folders, updates, the welcome screen).
        var cloudPanels = Step("cloud panels", () => vm.RestoreStartupLocationsAsync(cloudOnly: true));
        await Step("features", () =>
        {
            Features.AfterStartup(window, vm, services);
            return Task.CompletedTask;
        });

        if (services.Settings.CacheAutoManage)
        {
            _ = Step("cache maintenance", () => CacheManager.RunAutoMaintenanceAsync(services.Settings.CacheAutoDeleteDays, services.Settings.CacheMaxSizeMB));
        }

        await cloudPanels;
    }

    /// <summary>Runs one startup step; a failure is logged and the rest of startup goes on.</summary>
    private static async Task Step(string name, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception failure)
        {
            CrashLog.Write($"startup: {name}", failure);
        }
    }

    /// <summary>A second launch's request to come forward. Never blocks the caller (the pipe listener).</summary>
    private void BringToFront()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is { IsLoaded: true } window)
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Activate();
            }
        });
    }

    /// <summary>
    /// Every step runs on its own, bounded where it can block: one failing (a clipboard held by
    /// another app, a drive that won't unmap) must not skip the others.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        SupportLogger.Shared.Log($"FileFluss for Windows exiting (code {e.ApplicationExitCode})", "App");

        // Settings are written shortly after they change; the last ones (window frame, session folders) now.
        ExitStep("settings", () => Services?.SettingsStore.Flush());

        // A launch from now on waits for this process to finish instead of handing over to it.
        ExitStep("stop listening", SingleInstance.StopListening);
        if (Services is { } services)
        {
            ExitStep("cancel transfers", () =>
            {
                foreach (var transfer in services.Transfers.Running.ToList())
                {
                    transfer.Cancel();
                }
            });
            ExitStep("unmount", () => services.Mount.UnmountAllOnExit());

            // Virtual files can't be pasted once FileFluss is gone; don't leave a dead promise behind.
            ExitStep("clipboard", () => services.Export.ReleaseClipboard(clear: true));
            // Closing connections says goodbye to servers (FTP QUIT, SSH disconnect), which can
            // wait on a dead network for a long time each: off this thread, and not for long.
            var providers = services.Accounts.Accounts.Select(a => services.Hub.Get(a.Id)).OfType<Core.Providers.CloudProvider>().ToList();
            ExitStep("providers", () =>
            {
                var disposal = Task.Run(() =>
                {
                    foreach (var provider in providers)
                    {
                        ExitStep("provider", provider.Dispose);
                    }
                });
                if (!disposal.Wait(ProviderDisposalWait))
                {
                    SupportLogger.Shared.Log("Closing provider connections took too long; leaving the rest to the system", "App", LogLevel.Notice);
                }
            });
        }

        ExitStep("base", () => base.OnExit(e));

        // Last: until here a new launch must not start against half-released drives and files.
        ExitStep("release instance", SingleInstance.Release);
    }

    private static void ExitStep(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception failure)
        {
            CrashLog.Write($"exit: {step}", failure);
        }
    }
}
