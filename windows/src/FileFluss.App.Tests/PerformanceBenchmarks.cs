// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.IO;
using FileFluss.App.Tests.Support;
using FileFluss.App.ViewModels;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Native;
using Xunit.Abstractions;

namespace FileFluss.App.Tests;

/// <summary>
/// Timings of the paths that decide whether FileFluss feels instant: startup, listing and
/// sorting a big folder, filtering, drive and icon probes. Opt-in (FILEFLUSS_BENCH=1); the
/// numbers are printed, not asserted, because they depend on the machine.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class PerformanceBenchmarks(ITestOutputHelper output)
{
    private const int LargeCount = 30_000;

    [BenchFact]
    public void Startup()
    {
        var data = Directory.CreateTempSubdirectory("FileFluss-bench-data-").FullName;
        Bench.OnSta(() =>
        {
            var total = Stopwatch.StartNew();
            AppServices services = null!;
            MainViewModel vm = null!;
            Bench.Once(output, "AppServices constructor (settings, index, drive registry)", () => services = new AppServices(data));
            Bench.Once(output, "  first string lookup (resources)", () => Core.Localization.L("Favorites"));
            Bench.Once(output, "  WPF dispatcher (the real app has it already)", () => _ = System.Windows.Threading.Dispatcher.CurrentDispatcher);
            Bench.Once(output, "  a PanelViewModel", () => _ = new PanelViewModel(PanelSide.Left, services));
            Bench.Once(output, "  a SidebarViewModel", () => _ = new SidebarViewModel(PanelSide.Left, new PanelViewModel(PanelSide.Left, services), services));
            Bench.Once(output, "MainViewModel constructor (panels, both sidebars)", () => vm = new MainViewModel(services));
            output.WriteLine($"{"  => UI thread busy before the first window",-58} {total.Elapsed.TotalMilliseconds,9:F2} ms");

            // As App.OnStartup does it: the index starts in the background, both panels list home meanwhile.
            Task index = null!;
            Bench.Once(output, "Search.StartAsync (UI-thread part)", () => index = services.Search.StartAsync(new HashSet<Guid>()));
            Bench.Once(output, "Both panels list home", () => vm.RestoreStartupLocationsAsync(cloudOnly: false, index).GetAwaiter().GetResult());
            output.WriteLine($"{"  => time to listed home (in-process, excl. WPF window)",-58} {total.Elapsed.TotalMilliseconds,9:F2} ms");
            Bench.Once(output, "Index start finished (awaited after)", () => index.GetAwaiter().GetResult());
            var drives = Stopwatch.StartNew();
            while (vm.LeftSidebar.Drives.Count == 0 && drives.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(1);
            }

            output.WriteLine($"{"  sidebar drives listed",-58} {total.Elapsed.TotalMilliseconds,9:F2} ms after start");
        });
    }

    /// <summary>The cold cost of each piece the UI thread builds before the first window (run alone, in a fresh process).</summary>
    [BenchFact]
    public void StartupPieces()
    {
        var data = Directory.CreateTempSubdirectory("FileFluss-bench-data-").FullName;
        Bench.OnSta(() =>
        {
            Core.Settings.SettingsStore settings = null!;
            Bench.Once(output, "SettingsStore (read settings.json)", () => settings = new Core.Settings.SettingsStore(Path.Combine(data, "settings.json")));
            Bench.Once(output, "SearchServices (SQLite open + drive registry)", () => _ = new Services.SearchServices(Path.Combine(data, "index.sqlite"), new Core.Services.ProviderHub()));
            Bench.Once(output, "Drive icon (C:\\) via IconCache.ForPath", () => Services.IconCache.ForPath(@"C:\", true));
            Bench.Once(output, "Provider logo decode + trim (Dropbox)", () => Services.IconCache.Logo(CloudProviderType.Dropbox));
            Bench.Once(output, "Provider logos decode + trim (all others)", () =>
            {
                foreach (var type in Enum.GetValues<CloudProviderType>())
                {
                    Services.IconCache.Logo(type);
                }
            });
            Bench.Once(output, "DriveInfo.GetDrives + IsReady + label + free", () =>
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.IsReady)
                    {
                        _ = LocalFileProvider.DriveLabel(drive.RootDirectory.FullName);
                        _ = drive.AvailableFreeSpace;
                    }
                }
            });
        });
    }

    [BenchFact]
    public async Task LargeFolderListingSortingFiltering()
    {
        var folder = Bench.LargeFolder(LargeCount);
        var data = Directory.CreateTempSubdirectory("FileFluss-bench-data-").FullName;
        var services = new AppServices(data);
        var panel = new PanelViewModel(PanelSide.Left, services);

        Bench.Once(output, "Raw listing (LocalFileProvider)", () => LocalFileProvider.Shared.ListDirectoryAsync(folder).GetAwaiter().GetResult());
        var watch = Stopwatch.StartNew();
        await panel.NavigateAsync(PanelLocation.Local(folder));
        output.WriteLine($"{"NavigateAsync (list + rows + sort), " + panel.Rows.Count + " rows",-58} {watch.Elapsed.TotalMilliseconds,9:F2} ms");

        watch.Restart();
        await panel.RefreshAsync(quiet: true);
        output.WriteLine($"{"RefreshAsync (watcher refresh of the same folder)",-58} {watch.Elapsed.TotalMilliseconds,9:F2} ms");

        foreach (var field in new[] { SortField.Date, SortField.Name, SortField.Kind, SortField.Name })
        {
            // A column click, then the same column again (reverse): UI-thread cost, and time until the list shows it.
            foreach (var click in new[] { "", " again (reverse)" })
            {
                var before = panel.Rows;
                var clock = Stopwatch.StartNew();
                panel.SortBy(field);
                var ui = clock.Elapsed.TotalMilliseconds;
                while (ReferenceEquals(panel.Rows, before) && clock.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Thread.Sleep(1);
                }

                output.WriteLine($"{"Sort by " + field + click,-58} UI {ui,9:F2} ms   shown after {clock.Elapsed.TotalMilliseconds,9:F2} ms");
            }
        }
        panel.SortBy(SortField.Name);
        if (!panel.SortAscending)
        {
            panel.SortBy(SortField.Name);
        }

        Bench.Measure(output, "Filter keystrokes I, IM, IMG, IMG_, IMG_1, clear (6)", () =>
        {
            foreach (var text in new[] { "I", "IM", "IMG", "IMG_", "IMG_1", string.Empty })
            {
                panel.FilterText = text;
            }
        }, 3);

        Bench.Measure(output, "StatusText x100 (selection changes)", () =>
        {
            for (var i = 0; i < 100; i++)
            {
                _ = panel.StatusText;
            }
        }, 3);

        var names = panel.Rows.Select(r => r.Name).ToList();
        Bench.Measure(output, $"NaturalStringComparer sort of {names.Count} names", () => names.ToList().Sort(NaturalStringComparer.Instance), 3);
    }

    [BenchFact]
    public void DriveAndIconProbes()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            var root = drive.RootDirectory.FullName;
            Bench.Once(output, $"{root} IsReady + VolumeLabel + free ({drive.DriveType})", () =>
            {
                if (drive.IsReady)
                {
                    _ = drive.VolumeLabel;
                    _ = drive.AvailableFreeSpace;
                    _ = drive.TotalSize;
                }
            });
            Bench.Measure(output, $"{root} LocalFileProvider.DriveLabel", () => LocalFileProvider.DriveLabel(root), 5);
        }

        Bench.Once(output, "VolumeIdentity.Mounted", () => VolumeIdentity.Mounted());

        var folder = Bench.LargeFolder(LargeCount);
        var folders = Directory.GetDirectories(folder).Take(200).ToList();
        Bench.Once(output, $"ShellIcons.IconIndex per folder path x{folders.Count} (cold)", () =>
        {
            foreach (var path in folders)
            {
                ShellIcons.IconIndex(path, true, exists: true);
            }
        });
        Bench.Measure(output, $"ShellIcons.IconIndex per folder path x{folders.Count} (warm)", () =>
        {
            foreach (var path in folders)
            {
                ShellIcons.IconIndex(path, true, exists: true);
            }
        }, 3);
        Bench.OnSta(() =>
        {
            var rows = Directory.GetDirectories(folder).Skip(200).Take(200).Select(p => LocalFileProvider.ToItem(new DirectoryInfo(p))).ToList();
            Bench.Once(output, $"UI-thread cost of {rows.Count} folder row icons (IconCache.ForRow)", () =>
            {
                foreach (var row in rows)
                {
                    Services.IconCache.ForRow(row, isLocal: true, isDrive: false, 16, _ => { });
                }
            });
        });
        Bench.Measure(output, "ShellIcons.IconIndex by extension x200 (cached)", () =>
        {
            for (var i = 0; i < 200; i++)
            {
                ShellIcons.IconIndex("x.pdf", false, exists: false);
            }
        }, 3);
    }

    [BenchFact]
    public void SettingsAndStrings()
    {
        var data = Directory.CreateTempSubdirectory("FileFluss-bench-data-").FullName;
        var store = new Core.Settings.SettingsStore(Path.Combine(data, "settings.json"));
        Bench.Measure(output, "20 settings changes (each one saved)", () =>
        {
            for (var i = 0; i < 20; i++)
            {
                store.Settings.LeftSidebarWidth = 200 + (i % 2);
            }
        }, 3);

        Bench.Measure(output, "Localization.L x10000 (hit)", () =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                Core.Localization.L("Empty Folder");
            }
        }, 3);
        Bench.Measure(output, "Localization.L x10000 (miss)", () =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                Core.Localization.L("No such key at all");
            }
        }, 3);
    }
}
