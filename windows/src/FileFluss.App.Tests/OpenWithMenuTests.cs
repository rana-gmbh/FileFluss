// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Windows.Controls;
using FileFluss.App.ViewModels;
using FileFluss.App.Views;
using FileFluss.Core.Models;

namespace FileFluss.App.Tests;

/// <summary>Right-click → Open With: offered for files only, and built without asking the shell anything.</summary>
public sealed class OpenWithMenuTests
{
    [Fact]
    public async Task OfferedForFilesButNotWhenAFolderIsSelected()
    {
        var folder = Directory.CreateTempSubdirectory("FileFluss-openwith-").FullName;
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        File.WriteAllText(Path.Combine(folder, "b.png"), "b");
        Directory.CreateDirectory(Path.Combine(folder, "Sub"));
        var services = new AppServices(Directory.CreateTempSubdirectory("FileFluss-openwith-data-").FullName);
        var panel = new PanelViewModel(PanelSide.Left, services);
        await panel.NavigateAsync(PanelLocation.Local(folder));

        var a = panel.RowNamed("a.txt")!;
        var b = panel.RowNamed("b.png")!;
        var sub = panel.RowNamed("Sub")!;
        Assert.True(OpenWithMenu.Offers(panel, [a]));
        Assert.True(OpenWithMenu.Offers(panel, [a, b]));
        Assert.False(OpenWithMenu.Offers(panel, [a, sub]));
        Assert.False(OpenWithMenu.Offers(panel, []));
    }

    [Fact]
    public async Task TheSubmenuIsFilledOnlyWhenOpened()
    {
        var services = new AppServices(Directory.CreateTempSubdirectory("FileFluss-openwith-data-").FullName);
        var panel = new PanelViewModel(PanelSide.Left, services);
        var done = new TaskCompletionSource<(string Header, int Count, bool PlaceholderEnabled)>();
        var thread = new Thread(() =>
        {
            try
            {
                var item = OpenWithMenu.Create(null!, panel);
                var placeholder = (MenuItem)item.Items[0]!;
                done.SetResult(((string)item.Header, item.Items.Count, placeholder.IsEnabled));
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var (header, count, placeholderEnabled) = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Core.Localization.L("Open With"), header);
        Assert.Equal(1, count);
        Assert.False(placeholderEnabled);
    }

    [Fact]
    public async Task TheLookupOffersAppsForTextWithIconsAndAtMostOneDefault()
    {
        // Enumerates only; nothing is opened.
        var done = new TaskCompletionSource<IReadOnlyList<OpenWithMenu.Entry>>();
        var withoutExtension = -1;
        var thread = new Thread(() =>
        {
            try
            {
                // A file no app claims leaves nothing that opens the whole selection.
                withoutExtension = OpenWithMenu.Lookup(["notes.txt", "README"]).Count;
                done.SetResult(OpenWithMenu.Lookup(["notes.txt", "todo.TXT"]));
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var entries = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, withoutExtension);

        // A machine with no app for text at all (a bare CI image) has nothing to check.
        if (entries.Count == 0)
        {
            return;
        }

        Assert.All(entries, e => Assert.Equal(".txt", e.Extension));
        Assert.True(entries.Count(e => e.IsDefault) <= 1);
        if (entries.Any(e => e.IsDefault))
        {
            Assert.True(entries[0].IsDefault);
        }

        Assert.Contains(entries, e => e.Icon is not null);
    }
}
