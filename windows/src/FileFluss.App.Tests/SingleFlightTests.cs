// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.App.Services;

namespace FileFluss.App.Tests;

/// <summary>
/// Opening the same cloud file twice shares one download; a caller walking away (Quick Look
/// moving on) stops it only when nobody else is waiting.
/// </summary>
public sealed class SingleFlightTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TwoCallersShareOneRun()
    {
        var flights = new SingleFlight<string, int>();
        var runs = 0;
        var release = new TaskCompletionSource();

        async Task<int> Download(CancellationToken token)
        {
            Interlocked.Increment(ref runs);
            await release.Task.WaitAsync(token);
            return 42;
        }

        var first = flights.RunAsync("a", Download);
        var second = flights.RunAsync("a", Download);
        release.SetResult();

        Assert.Equal(42, await first.WaitAsync(Patience));
        Assert.Equal(42, await second.WaitAsync(Patience));
        Assert.Equal(1, runs);
        Assert.Equal(0, flights.RunningCount);
    }

    [Fact]
    public async Task DifferentKeysRunSeparately()
    {
        var flights = new SingleFlight<string, string>();
        var runs = 0;

        Task<string> Download(string name) => flights.RunAsync(name, _ =>
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(name);
        });

        Assert.Equal(["a", "b"], await Task.WhenAll(Download("a"), Download("b")).WaitAsync(Patience));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task ACallerWalkingAwayLeavesTheRunForTheOneStillWaiting()
    {
        // Quick Look starts the download, a real "open" joins it, then Quick Look moves on:
        // the open must still get its file.
        var flights = new SingleFlight<string, int>();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        CancellationToken runToken = default;

        async Task<int> Download(CancellationToken token)
        {
            runToken = token;
            started.SetResult();
            await release.Task.WaitAsync(token);
            return 7;
        }

        using var quickLook = new CancellationTokenSource();
        var preview = flights.RunAsync("a", Download, quickLook.Token);
        await started.Task.WaitAsync(Patience);
        var open = flights.RunAsync("a", Download);

        await quickLook.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview.WaitAsync(Patience));
        Assert.False(runToken.IsCancellationRequested);

        release.SetResult();
        Assert.Equal(7, await open.WaitAsync(Patience));
    }

    [Fact]
    public async Task TheRunIsCancelledWhenTheLastCallerWalksAway()
    {
        var flights = new SingleFlight<string, int>();
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();

        async Task<int> Download(CancellationToken token)
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            return 0;
        }

        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var a = flights.RunAsync("a", Download, first.Token);
        await started.Task.WaitAsync(Patience);
        var b = flights.RunAsync("a", Download, second.Token);

        await first.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.WaitAsync(Patience));
        Assert.False(cancelled.Task.IsCompleted);

        await second.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.WaitAsync(Patience));
        await cancelled.Task.WaitAsync(Patience);
    }

    [Fact]
    public async Task ACallerAfterAGiveUpStartsAfreshOnceTheOldRunHasStopped()
    {
        // The old download is still winding down (deleting its partial file) when the user
        // opens the file for real: the new one must not run alongside it.
        var flights = new SingleFlight<string, int>();
        var running = 0;
        var overlapped = false;
        var firstStarted = new TaskCompletionSource();
        var letFirstFinishWindingDown = new TaskCompletionSource();
        var runs = 0;

        async Task<int> Download(CancellationToken token)
        {
            var run = Interlocked.Increment(ref runs);
            if (Interlocked.Increment(ref running) > 1)
            {
                overlapped = true;
            }

            try
            {
                if (run == 1)
                {
                    firstStarted.SetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    catch (OperationCanceledException)
                    {
                        await letFirstFinishWindingDown.Task;
                        throw;
                    }
                }

                return run;
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        using var quickLook = new CancellationTokenSource();
        var preview = flights.RunAsync("a", Download, quickLook.Token);
        await firstStarted.Task.WaitAsync(Patience);
        await quickLook.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview.WaitAsync(Patience));

        var open = flights.RunAsync("a", Download);
        await Task.Delay(100);
        Assert.Equal(1, runs);

        letFirstFinishWindingDown.SetResult();
        Assert.Equal(2, await open.WaitAsync(Patience));
        Assert.False(overlapped);
    }

    [Fact]
    public async Task AFailureReachesEveryCallerAndTheNextCallTriesAgain()
    {
        var flights = new SingleFlight<string, int>();
        var release = new TaskCompletionSource();
        var runs = 0;

        async Task<int> Failing(CancellationToken token)
        {
            Interlocked.Increment(ref runs);
            await release.Task;
            throw new InvalidOperationException("server said no");
        }

        var a = flights.RunAsync("a", Failing);
        var b = flights.RunAsync("a", Failing);
        release.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => a.WaitAsync(Patience));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.WaitAsync(Patience));
        Assert.Equal(1, runs);

        Assert.Equal(5, await flights.RunAsync("a", _ => Task.FromResult(5)).WaitAsync(Patience));
    }
}
