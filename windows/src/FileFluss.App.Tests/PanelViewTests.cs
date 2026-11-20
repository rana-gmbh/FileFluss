// Copyright (C) 2026 Rana GmbH â€” GPLv3. See LICENSE at the repository root.

using System.IO;
using FileFluss.App.ViewModels;
using FileFluss.Core.Models;
using FileFluss.Native;

namespace FileFluss.App.Tests;

/// <summary>The panel's list order: sorted once, then reversed, hidden and filtered without sorting again.</summary>
public sealed class PanelViewTests
{
    private static readonly string[] Stems = ["IMG_", "file", "Report ", "photo (", "Notes", "a", "Z", "_draft", "10", "2"];
    private static readonly string[] Extensions = ["jpg", "pdf", "txt", "zip", "png", ""];

    [Theory]
    [InlineData(SortField.Name, true)]
    [InlineData(SortField.Name, false)]
    [InlineData(SortField.Date, true)]
    [InlineData(SortField.Date, false)]
    [InlineData(SortField.Size, true)]
    [InlineData(SortField.Size, false)]
    [InlineData(SortField.Kind, true)]
    [InlineData(SortField.DateCreated, false)]
    public async Task OrderMatchesTheExplorerRuleInBothDirections(SortField field, bool ascending)
    {
        var folder = Directory.CreateTempSubdirectory("FileFluss-sort-").FullName;
        var random = new Random(7);
        var created = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 400; i++)
        {
            var name = $"{Stems[i % Stems.Length]}{random.Next(0, 50)}-{i}";
            var extension = Extensions[i % Extensions.Length];
            var path = Path.Combine(folder, extension.Length > 0 ? $"{name}.{extension}" : name);
            if (i % 9 == 0)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                File.WriteAllBytes(path, new byte[random.Next(0, 4) * 100]);
            }

            // Plenty of equal dates and sizes, so the name tie-break matters.
            var stamp = created.AddDays(random.Next(0, 5));
            if (i % 9 == 0)
            {
                Directory.SetLastWriteTimeUtc(path, stamp);
                Directory.SetCreationTimeUtc(path, stamp);
            }
            else
            {
                File.SetLastWriteTimeUtc(path, stamp);
                File.SetCreationTimeUtc(path, stamp);
            }
        }

        var services = new AppServices(Directory.CreateTempSubdirectory("FileFluss-sort-data-").FullName);
        var panel = new PanelViewModel(PanelSide.Left, services);
        await panel.NavigateAsync(PanelLocation.Local(folder));
        panel.SortBy(field);
        if (panel.SortAscending != ascending)
        {
            panel.SortBy(field);
        }

        var expected = ReferenceOrder(panel.AllRows, field, ascending).Select(r => r.Name).ToList();
        Assert.Equal(expected, panel.Rows.Select(r => r.Name).ToList());
    }

    [Fact]
    public async Task FilterAndHiddenFilesKeepTheSortedOrder()
    {
        var folder = Directory.CreateTempSubdirectory("FileFluss-filter-").FullName;
        foreach (var name in new[] { "b10.txt", "b2.txt", "B1.txt", "a.txt", "hidden.txt" })
        {
            File.WriteAllText(Path.Combine(folder, name), name);
        }

        File.SetAttributes(Path.Combine(folder, "hidden.txt"), FileAttributes.Hidden);
        var services = new AppServices(Directory.CreateTempSubdirectory("FileFluss-filter-data-").FullName);
        var panel = new PanelViewModel(PanelSide.Left, services) { ShowHiddenFiles = false };
        await panel.NavigateAsync(PanelLocation.Local(folder));

        Assert.Equal(["a.txt", "B1.txt", "b2.txt", "b10.txt"], panel.Rows.Select(r => r.Name));
        panel.FilterText = "b";
        Assert.Equal(["B1.txt", "b2.txt", "b10.txt"], panel.Rows.Select(r => r.Name));
        panel.SortAscending = false;
        Assert.Equal(["b10.txt", "b2.txt", "B1.txt"], panel.Rows.Select(r => r.Name));
        panel.FilterText = string.Empty;
        panel.ShowHiddenFiles = true;
        Assert.Equal(["hidden.txt", "b10.txt", "b2.txt", "B1.txt", "a.txt"], panel.Rows.Select(r => r.Name));
        Assert.Contains("5", panel.ListingText);
    }

    [Fact]
    public async Task ARefreshKeepsTheRowsOfUnchangedItems()
    {
        var folder = Directory.CreateTempSubdirectory("FileFluss-reuse-").FullName;
        File.WriteAllText(Path.Combine(folder, "same.txt"), "1");
        File.WriteAllText(Path.Combine(folder, "changes.txt"), "1");
        var services = new AppServices(Directory.CreateTempSubdirectory("FileFluss-reuse-data-").FullName);
        var panel = new PanelViewModel(PanelSide.Left, services);
        await panel.NavigateAsync(PanelLocation.Local(folder));
        var before = panel.Rows.ToDictionary(r => r.Name);

        File.WriteAllText(Path.Combine(folder, "changes.txt"), "12345");
        File.WriteAllText(Path.Combine(folder, "new.txt"), "1");
        await panel.RefreshAsync(quiet: true);

        Assert.Same(before["same.txt"], panel.RowNamed("same.txt"));
        Assert.NotSame(before["changes.txt"], panel.RowNamed("changes.txt"));
        Assert.Equal(5, panel.RowNamed("changes.txt")!.Item.Size);
        Assert.NotNull(panel.RowNamed("new.txt"));
    }

    /// <summary>The comparison the panel used before sorting moved off the UI thread: one comparator, direction applied per comparison.</summary>
    private static List<FileRowViewModel> ReferenceOrder(IEnumerable<FileRowViewModel> rows, SortField field, bool ascending)
    {
        var names = NaturalStringComparer.Instance;
        Comparison<FileRowViewModel> byField = field switch
        {
            SortField.Date => (a, b) => a.Item.ModificationDate.CompareTo(b.Item.ModificationDate),
            SortField.DateCreated => (a, b) => a.Item.CreationDate.CompareTo(b.Item.CreationDate),
            SortField.Size => (a, b) => a.SortSize.CompareTo(b.SortSize),
            SortField.Kind => (a, b) => string.Compare(a.KindText, b.KindText, StringComparison.CurrentCultureIgnoreCase),
            _ => (a, b) => names.Compare(a.Name, b.Name),
        };

        var list = rows.ToList();
        list.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }

            var result = byField(a, b);
            if (result == 0 && field != SortField.Name)
            {
                result = names.Compare(a.Name, b.Name);
            }

            return ascending ? result : -result;
        });
        return list;
    }
}
