// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using FileFluss.App.Tests.Support;
using FileFluss.App.Views;
using FileFluss.Core.Transfers;

namespace FileFluss.App.Tests;

/// <summary>
/// Transfer Details adds the newly finished items to its list instead of rebuilding the list
/// for every update: with 20,000 files it rebuilt everything every 150 ms.
/// </summary>
public sealed class TransferDetailsTests
{
    [Fact]
    public void OnlyNewResultsAreAddedAndRowsAlreadyShownStay()
    {
        var rows = new ObservableCollection<TransferDetailsWindow.ResultRow>();
        var results = Enumerable.Range(0, 20_000).Select(i => new TransferItemResult($"f{i}", TransferItemStatus.Succeeded, null)).ToList();

        TransferDetailsWindow.AppendNew(rows, results);
        var first = rows[0];
        var resets = 0;
        var added = 0;
        rows.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
            else if (e.Action == NotifyCollectionChangedAction.Add)
            {
                added++;
            }
        };

        results.Add(new TransferItemResult("broken", TransferItemStatus.Failed, "denied"));
        results.Add(new TransferItemResult("old", TransferItemStatus.Skipped, null));
        TransferDetailsWindow.AppendNew(rows, results);
        TransferDetailsWindow.AppendNew(rows, results);

        Assert.Equal(20_002, rows.Count);
        Assert.Equal(0, resets);
        Assert.Equal(2, added);
        Assert.Same(first, rows[0]);
        Assert.Equal(new TransferDetailsWindow.ResultRow("✕  broken — denied", true), rows[^2]);
        Assert.Equal(new TransferDetailsWindow.ResultRow("↷  old", false), rows[^1]);
    }

    [Fact]
    public void TheRowTemplateBuildsATextRow()
        => Bench.OnSta(() =>
        {
            var content = TransferDetailsWindow.CreateResultTemplate().LoadContent();
            var text = Assert.IsType<System.Windows.Controls.TextBlock>(content);
            text.DataContext = new TransferDetailsWindow.ResultRow("✓  a.txt", false);

            // Bindings attach through the dispatcher; let it run once.
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.Equal("✓  a.txt", text.Text);
        });
}
