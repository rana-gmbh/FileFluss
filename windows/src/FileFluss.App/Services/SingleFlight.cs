// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.App.Services;

/// <summary>
/// One running operation per key, shared by everyone who asks for it while it runs — opening
/// the same cloud file twice (a double double-click, Enter after Quick Look) used to start two
/// downloads into the same partial file, and the second failed with "file in use".
///
/// <para>Cancellation is per caller. A caller that brings a token stops <em>waiting</em> when
/// it is cancelled; the operation itself is cancelled only once no caller is left waiting. So
/// Quick Look moving on stops a download nobody else wants, but never one a real "open" (which
/// waits without a token) is also waiting for. A caller arriving after an operation was given
/// up waits for it to wind down and then starts afresh, so two runs never overlap.</para>
///
/// <para>The operation is started on the caller's thread and context (the UI thread for the
/// app's callers), and so are the callers' continuations.</para>
/// </summary>
internal sealed class SingleFlight<TKey, TResult>
    where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, Flight> _running;

    public SingleFlight(IEqualityComparer<TKey>? comparer = null) => _running = new Dictionary<TKey, Flight>(comparer);

    /// <summary>How many operations are running; for tests.</summary>
    public int RunningCount
    {
        get
        {
            lock (_gate)
            {
                return _running.Count;
            }
        }
    }

    /// <summary>
    /// The running operation for <paramref name="key"/>, or <paramref name="operation"/> started
    /// now. Throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/>
    /// is cancelled before the result arrives (the operation goes on for any other caller).
    /// </summary>
    public async Task<TResult> RunAsync(TKey key, Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flight flight;
        Flight? previous = null;
        var start = false;
        lock (_gate)
        {
            if (_running.TryGetValue(key, out var existing) && !existing.Cancellation.IsCancellationRequested)
            {
                flight = existing;
            }
            else
            {
                // Either nothing runs, or what runs was given up and is winding down: this one
                // starts after it, never alongside it.
                previous = existing;
                flight = new Flight();
                _running[key] = flight;
                start = true;
            }

            flight.Waiters++;
        }

        if (start)
        {
            _ = FlyAsync(key, flight, previous, operation);
        }

        try
        {
            return await flight.Completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            Leave(flight);
        }
    }

    private async Task FlyAsync(TKey key, Flight flight, Flight? previous, Func<CancellationToken, Task<TResult>> operation)
    {
        try
        {
            if (previous is not null)
            {
                try
                {
                    await previous.Completion.Task;
                }
                catch (Exception)
                {
                    // Its outcome was for its own callers; only its end matters here.
                }
            }

            flight.Cancellation.Token.ThrowIfCancellationRequested();
            flight.Completion.TrySetResult(await operation(flight.Cancellation.Token));
        }
        catch (OperationCanceledException e)
        {
            flight.Completion.TrySetCanceled(e.CancellationToken);
        }
        catch (Exception e)
        {
            flight.Completion.TrySetException(e);
        }
        finally
        {
            lock (_gate)
            {
                flight.Done = true;
                if (_running.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
                {
                    _running.Remove(key);
                }
            }
        }
    }

    private void Leave(Flight flight)
    {
        var giveUp = false;
        lock (_gate)
        {
            flight.Waiters--;
            if (flight.Waiters == 0 && !flight.Done)
            {
                giveUp = true;
            }
        }

        if (giveUp)
        {
            flight.Cancellation.Cancel();
        }
    }

    private sealed class Flight
    {
        public readonly CancellationTokenSource Cancellation = new();

        public readonly TaskCompletionSource<TResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Waiters;

        public bool Done;
    }
}
