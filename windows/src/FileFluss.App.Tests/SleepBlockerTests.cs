// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows.Threading;
using FileFluss.App.Services;
using FileFluss.App.Tests.Support;
using FileFluss.Core.Models;
using FileFluss.Core.Transfers;

namespace FileFluss.App.Tests;

/// <summary>macOS 1.6: the computer stays awake during a transfer.</summary>
public sealed class SleepBlockerTests
{
    private const uint Hold = SleepBlocker.EsContinuous | SleepBlocker.EsSystemRequired;
    private const uint Release = SleepBlocker.EsContinuous;

    [Fact]
    public void HoldsWhileSomethingRunsAndLetsGoAfter()
    {
        var calls = new List<uint>();
        var blocker = new SleepBlocker(flags =>
        {
            calls.Add(flags);
            return 1;
        });

        blocker.Update(false);
        Assert.Empty(calls);

        blocker.Update(true);
        blocker.Update(true);
        Assert.True(blocker.IsHolding);

        blocker.Update(false);
        Assert.False(blocker.IsHolding);
        Assert.Equal([Hold, Release], calls);
    }

    [Fact]
    public void AFailedCallIsTriedAgain()
    {
        var answer = 0u;
        var calls = 0;
        var blocker = new SleepBlocker(_ =>
        {
            calls++;
            return answer;
        });

        blocker.Update(true);
        Assert.False(blocker.IsHolding);
        answer = 1;
        blocker.Update(true);
        Assert.True(blocker.IsHolding);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void TheTransferCenterKeepsThePcAwakeExactlyAsLongAsATransferRuns()
        => Bench.OnSta(() =>
        {
            var calls = new List<uint>();
            var center = new TransferCenter(new SleepBlocker(flags =>
            {
                calls.Add(flags);
                return 1;
            }));

            var first = new TransferProgress(TransferOperation.Uploading, 1, PanelSide.Left);
            var second = new TransferProgress(TransferOperation.Downloading, 1, PanelSide.Right);
            center.Track(first);
            center.Track(second);
            Assert.Equal([Hold], calls);

            first.Complete();
            Drain();
            Assert.Equal([Hold], calls);

            second.Complete();
            Drain();
            Assert.Equal([Hold, Release], calls);
        });

    /// <summary>Runs what the transfers posted to the UI thread.</summary>
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
