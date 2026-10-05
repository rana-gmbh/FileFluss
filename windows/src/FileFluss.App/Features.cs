// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Windows;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.Core.Models;

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
    }

    public static void AfterStartup(MainWindow window, MainViewModel vm, AppServices services)
    {
        // A folder passed on the command line (or forwarded by a second launch) opens in the active panel.
        SingleInstance.Forwarded += args => window.Dispatcher.Invoke(() => OpenArgument(vm, args));
        OpenArgument(vm, Environment.GetCommandLineArgs().Skip(1).ToArray());
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
