// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows.Threading;
using FileFluss.App.Services;
using FileFluss.App.Tests.Support;
using FileFluss.Core.Models;
using FileFluss.Core.Transfers;

namespace FileFluss.App.Tests;

/// <summary>The transfers list keeps a bounded history; running transfers always stay.</summary>
public sealed class TransferCenterTests
{
    [Fact]
    public void OnlyTheNewestFinishedTransfersAreKept()
        => Bench.OnSta(() =>
        {
            var center = new TransferCenter(new SleepBlocker(_ => 1));
            var running = new TransferProgress(TransferOperation.Copying, 1, PanelSide.Left);
            center.Track(running);

            var finished = new List<TransferProgress>();
            for (var i = 0; i < TransferCenter.FinishedLimit + 20; i++)
            {
                var transfer = new TransferProgress(TransferOperation.Copying, 1, PanelSide.Left);
                center.Track(transfer);
                transfer.Complete();
                finished.Add(transfer);
                Drain();
            }

            Assert.Equal(TransferCenter.FinishedLimit + 1, center.All.Count);
            Assert.Contains(running, center.All);
            Assert.True(running.IsRunning);

            // The oldest went, the newest stayed.
            Assert.DoesNotContain(finished[0], center.All);
            Assert.Contains(finished[^1], center.All);
        });

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
