// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileFluss.App.Services;
using FileFluss.App.ViewModels;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Native;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Views;

/// <summary>
/// The right-click menu's "Open With" submenu, as in Explorer and on macOS: the default app
/// first, then every other app registered to open the selection, and "Other…" for the
/// system's own chooser. With several files selected, only apps that open all of them.
///
/// <para>Filled when it is opened rather than when the context menu is built: asking the
/// shell for every handler of a type (and loading their icons) takes a few milliseconds per
/// type, and most right-clicks never open this submenu. The lookup runs on its own STA
/// thread, as the shell's handler objects want.</para>
/// </summary>
public static class OpenWithMenu
{
    /// <summary>Files only, real ones (no synthetic rows), in a panel whose files can be opened.</summary>
    public static bool Offers(PanelViewModel panel, IReadOnlyList<FileRowViewModel> selection)
        => selection.Count > 0 && panel.Provider is not null && !panel.IsOffline && !panel.Location.IsOfflineDrive &&
           selection.All(r => !r.IsDirectory && r.Item.Role == CloudItemRole.Normal);

    public static MenuItem Create(MainViewModel main, PanelViewModel panel)
    {
        // The selection as it was right-clicked; the panel's may change while the menu is open.
        var items = panel.SelectedItems;
        var parent = new MenuItem { Header = L("Open With") };

        // Without an item WPF draws no submenu arrow; this one is there for the moment the
        // lookup takes.
        parent.Items.Add(new MenuItem { Header = "…", IsEnabled = false });
        var filled = false;
        parent.SubmenuOpened += async (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, parent) || filled)
            {
                return;
            }

            filled = true;
            IReadOnlyList<Entry> entries;
            try
            {
                entries = await RunOnStaAsync(() => Lookup(items.Select(i => i.Name).ToList()));
            }
            catch (Exception ex)
            {
                // A broken shell extension: the submenu still offers the system chooser.
                SupportLogger.Shared.Log($"Open With lookup failed: {ex.Message}", "Open", LogLevel.Error);
                entries = [];
            }

            Fill(parent, entries, main, panel, items);
        };
        return parent;
    }

    /// <summary>One app, ready to show: everything here was made (and frozen) off the UI thread.</summary>
    internal sealed record Entry(string Extension, string Id, string Name, bool IsDefault, ImageSource? Icon);

    /// <summary>The submenu's apps for files with these names. Asks the shell: call on an STA thread.</summary>
    internal static IReadOnlyList<Entry> Lookup(IReadOnlyList<string> names)
    {
        var extensions = OpenWithCandidates.Extensions(names);
        if (extensions.Count == 0)
        {
            return [];
        }

        var perType = extensions.Select(e => (IReadOnlyList<OpenWithHandler>)OpenWithHandlers.ForExtension(e)).ToList();
        var first = extensions[0];
        var defaultId = OpenWithHandlers.DefaultHandlerId(first, perType[0]);
        return OpenWithCandidates.Rank(perType, defaultId, h => h.Id, h => h.Name)
            .Select(c => new Entry(first, c.Id, c.Name, c.IsDefault, IconFor(c.App)))
            .ToList();
    }

    private static void Fill(MenuItem parent, IReadOnlyList<Entry> entries, MainViewModel main, PanelViewModel panel, IReadOnlyList<CloudFileItem> items)
    {
        parent.Items.Clear();
        if (entries.Count == 0)
        {
            parent.Items.Add(new MenuItem { Header = L("No Suitable Applications"), IsEnabled = false });
        }

        foreach (var entry in entries)
        {
            // App names are shown as they are: an underscore is not an access key here.
            var title = entry.IsDefault ? L("{0} (default)", entry.Name) : entry.Name;
            var item = new MenuItem { Header = title.Replace("_", "__", StringComparison.Ordinal) };
            if (entry.Icon is { } icon)
            {
                item.Icon = new Image { Source = icon, Width = 16, Height = 16 };
            }

            item.Click += (_, _) => _ = main.OpenWithAsync(panel, items,
                paths => OpenWithHandlers.Invoke(entry.Extension, entry.Id, paths) || OpenWithHandlers.LaunchExecutable(entry.Id, paths));
            parent.Items.Add(item);

            // Explorer and Finder set the default apart from the alternatives.
            if (entry.IsDefault && entries.Count > 1)
            {
                parent.Items.Add(new Separator());
            }
        }

        parent.Items.Add(new Separator());
        var other = new MenuItem { Header = L("Other…") };
        other.Click += (_, _) => _ = main.OpenWithAsync(panel, items, paths =>
        {
            // The system chooser takes one file at a time and opens it itself.
            var owner = new WindowInteropHelper(main.Shell.Window).Handle;
            foreach (var path in paths)
            {
                ShellFileOperations.OpenWith(path, owner);
            }

            return true;
        });
        parent.Items.Add(other);
    }

    /// <summary>A packaged app's image file, or a classic app's icon resource, at menu size.</summary>
    private static ImageSource? IconFor(OpenWithHandler handler)
    {
        const int Pixels = 32;
        try
        {
            if (OpenWithHandlers.PackagedIconFile(handler) is { } file)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = Pixels;
                image.UriSource = new Uri(file);
                image.EndInit();
                image.Freeze();
                return image;
            }

            var handle = OpenWithHandlers.Icon(handler, Pixels);
            if (handle == 0)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                ShellIcons.DestroyIcon(handle);
            }
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or COMException or FileFormatException)
        {
            // An app whose icon can't be read is still offered, without one.
            return null;
        }
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        })
        { IsBackground = true, Name = "Open With" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
