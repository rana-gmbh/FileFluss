// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Windows.Threading;
using FileFluss.App.Services;
using FileFluss.App.Tests.Support;

namespace FileFluss.App.Tests;

/// <summary>
/// While another app's paste or drop waits for a cloud listing, the window keeps responding
/// (a nested message loop), but nothing starts in the middle of that OLE call: the guard is up,
/// and what was asked for meanwhile (closing the window) runs once the call has returned.
/// </summary>
public sealed class HandOverGuardTests
{
    [Fact]
    public void WorkAskedForDuringAHandOverRunsAfterIt()
    {
        Bench.OnSta(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var listing = new TaskCompletionSource();
            bool? guardedInside = null;
            var ran = false;
            var ranInside = true;

            // Runs inside the nested loop, as a key press or a close request would.
            dispatcher.BeginInvoke(() =>
            {
                guardedInside = CloudFileExport.IsHandingOver;
                CloudFileExport.AfterHandOver(() => ran = true);
                ranInside = ran;
                listing.SetResult();
            });

            Assert.True(CloudFileExport.WaitPumping(listing.Task, TimeSpan.FromSeconds(10)));
            Assert.True(guardedInside);
            Assert.False(ranInside);
            Assert.False(CloudFileExport.IsHandingOver);

            // Queued for after the call: let the dispatcher run it.
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Assert.True(ran);
        });
    }

    [Fact]
    public void WithoutAHandOverWorkRunsAtOnce()
    {
        Bench.OnSta(() =>
        {
            var ran = false;
            Assert.False(CloudFileExport.IsHandingOver);
            CloudFileExport.AfterHandOver(() => ran = true);
            Assert.True(ran);
        });
    }
}
