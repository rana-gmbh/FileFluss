// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.Windows.Controls;
using FileFluss.App.Tests.Support;
using FileFluss.App.Views.Compare;

namespace FileFluss.App.Tests;

/// <summary>
/// After a re-compare the Compare window puts the previous selection back. Item by item that
/// was one SelectionChanged (and one round of button checks) per row, which hung the window
/// with 20,000 selected rows.
/// </summary>
public sealed class CompareSelectionRestoreTests
{
    [Fact]
    public void TwentyThousandRowsAreSelectedInOneChange()
    {
        Bench.OnSta(() =>
        {
            var rows = Enumerable.Range(0, 20_000).Select(i => $"row {i}").ToList();
            var list = new ListBox { SelectionMode = SelectionMode.Extended, ItemsSource = rows };
            var changes = 0;
            list.SelectionChanged += (_, _) => changes++;

            var watch = Stopwatch.StartNew();
            CompareWindow.SelectItems(list, rows);
            watch.Stop();

            Assert.Equal(20_000, list.SelectedItems.Count);
            Assert.Equal(1, changes);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.ElapsedMilliseconds} ms");
        });
    }

    [Fact]
    public void OneRowIsSelectedToo()
    {
        Bench.OnSta(() =>
        {
            var rows = new List<string> { "a", "b", "c" };
            var list = new ListBox { SelectionMode = SelectionMode.Extended, ItemsSource = rows };

            CompareWindow.SelectItems(list, new List<string> { "b" });

            Assert.Equal(["b"], list.SelectedItems.Cast<string>());
        });
    }
}
