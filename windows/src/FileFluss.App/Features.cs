// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Windows;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using static FileFluss.Core.Localization;

namespace FileFluss.App;

/// <summary>
/// Wires the feature windows (search, compare, sync, storage analysis, help, about, updates,
/// welcome) into the main window at startup, keeping MainWindow free of their details.
/// </summary>
internal static class Features
{
    public static void Register(AppServices services)
    {
        WindowsRegistry.About = owner => Views.AboutWindow.Show(owner);
        WindowsRegistry.Help = owner => Views.HelpWindow.Show(owner);
        WindowsRegistry.Search = (owner, vm) => Views.Search.SearchWindow.Show(owner, vm);
        WindowsRegistry.Compare = (owner, vm) => Views.Compare.CompareWindow.Show(owner, vm);
        WindowsRegistry.Sync = (owner, vm) => Views.Sync.SyncWindow.Show(owner, vm);
        WindowsRegistry.StorageAnalysis = (owner, vm, location) => Views.Storage.StorageAnalysisWindow.Show(owner, vm, location);
    }

    public static void AfterStartup(MainWindow window, MainViewModel vm, AppServices services)
    {
        // A folder passed on the command line (or forwarded by a second launch) opens in the active panel.
        SingleInstance.Forwarded += args => window.Dispatcher.Invoke(() => OpenArgument(vm, args));
        OpenArgument(vm, Environment.GetCommandLineArgs().Skip(1).ToArray());
        WireIndex(window, vm, services);

        // Drives left mapped by a FileFluss that crashed point at a server that is gone.
        _ = MountService.CleanupOrphansAsync();

        // First launch, or "Show Welcome Screen Again" in Settings.
        Views.WelcomeWindow.ShowIfNeeded(window, services);
        Views.Settings.SettingsWindow.WelcomeRequested += (_, _) => Views.WelcomeWindow.Show(window, services);

        // Updates: Help → Check for Updates…, and a quiet daily check (macOS Sparkle).
        var updates = new UpdateService(services);
        window.UpdateRequested += (_, _) => _ = updates.CheckAsync(true, window);
        _ = updates.CheckAsync(false, window);
    }

    /// <summary>The search index learns from browsing, follows account changes and runs "Index Current Source".</summary>
    private static void WireIndex(MainWindow window, MainViewModel vm, AppServices services)
    {
        var search = services.Search;
        foreach (var panel in new[] { vm.Left, vm.Right })
        {
            panel.ItemsListed += (_, items) =>
            {
                if (panel.IsCloud && !panel.IsOffline && panel.Location.AccountId is { } id)
                {
                    search.LearnListing(id, panel.CurrentPath, items);
                }
            };
        }

        services.Accounts.AccountRemoved += (_, id) => search.Safe(() => search.Index.DropIndexedCloudSource(id));
        services.Accounts.AccountChanged += (_, id) =>
        {
            // A renamed account keeps its index under the new name (Index Status, offline results).
            if (services.Accounts.Find(id) is { } account && search.CloudSource(id) is { } source && source.DisplayName != account.DisplayName)
            {
                search.Safe(() => search.Index.RenameIndexedSource(source.SourceId, account.DisplayName));
            }
        };

        vm.IndexRequested += (_, location) => IndexLocation(window, services, location);
        search.Indexing.IndexingFinished += (_, e) =>
        {
            if (e.Job.ErrorMessage is { } error && !e.Job.IsCancelled)
            {
                SupportLogger.Shared.Log($"Indexing {e.Job.DisplayName} failed: {error}", "Indexing", LogLevel.Error);
            }
        };
    }

    /// <summary>macOS <c>indexActiveSource</c>: the drive the panel is on, or the connected account it shows.</summary>
    private static void IndexLocation(Window owner, AppServices services, PanelLocation location)
    {
        var search = services.Search;
        SupportLogger.Shared.Log($"Index requested for {location.Kind} {location.Path}", "Indexing");
        if (location.IsCloud && services.Accounts.Find(location.AccountId!.Value) is { } account)
        {
            if (account.IsConnected && !account.IsOfflineMode && services.Hub.Get(account.Id) is not null)
            {
                search.Indexing.IndexCloudAccount(account);
            }
            else
            {
                MessageDialog.Info(owner, L("Index Current Source"), L("Cloud account not connected"));
            }

            return;
        }

        if (location.IsLocal && search.DriveContaining(location.Path) is { RootPath: { } root } drive)
        {
            search.Indexing.IndexDrive(drive, root);
            return;
        }

        MessageDialog.Info(owner, L("Index Current Source"),
            L("Drives other than the Windows drive and cloud accounts can be indexed for offline search. Open one in the active panel and try again."));
    }

    private static void OpenArgument(MainViewModel vm, string[] args)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (path is null)
        {
            return;
        }

        path = Path.GetFullPath(path.Trim('"'));
        if (Directory.Exists(path))
        {
            _ = vm.ActivePanel.NavigateAsync(PanelLocation.Local(path));
        }
        else if (File.Exists(path))
        {
            _ = vm.ActivePanel.NavigateAsync(PanelLocation.Local(Path.GetDirectoryName(path)!), Path.GetFileName(path));
        }
    }
}
