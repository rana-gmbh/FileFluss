// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;

namespace FileFluss.Core.Providers.Ftp;

/// <summary>
/// Logged-in FTP control connections, leased one operation at a time. An FTP control
/// connection serialises everything, so one per concurrent operation (panel listing, a
/// transfer, the indexer) keeps a big upload from freezing the panel.
///
/// <para>Servers cap connections per user (vsftpd's <c>max_per_ip</c>, "421 Too many
/// connections"). When opening another one is refused while others are alive, the cap drops to
/// what the server allowed and the caller waits for a connection to come back instead of
/// failing. Idle connections are checked with NOOP before reuse, since servers drop them after
/// a few minutes.</para>
/// </summary>
internal sealed class FtpConnectionPool : IDisposable
{
    /// <summary>Idle longer than this and a connection is probed before reuse.</summary>
    private static readonly TimeSpan ProbeAfter = TimeSpan.FromSeconds(20);

    private readonly Func<CancellationToken, Task<IFtpConnection>> _connect;
    private readonly Lock _gate = new();
    private readonly Stack<(IFtpConnection Connection, long ReturnedAt)> _idle = new();
    private readonly Queue<TaskCompletionSource<IFtpConnection?>> _waiters = new();
    private int _live;
    private int _limit;
    private bool _disposed;

    public FtpConnectionPool(Func<CancellationToken, Task<IFtpConnection>> connect, int limit = 3)
    {
        _connect = connect;
        _limit = Math.Max(1, limit);
    }

    /// <summary>Connections open right now, leased or idle. For tests.</summary>
    internal int LiveCount
    {
        get
        {
            lock (_gate)
            {
                return _live;
            }
        }
    }

    internal int Limit
    {
        get
        {
            lock (_gate)
            {
                return _limit;
            }
        }
    }

    public async Task<IFtpConnection> LeaseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IFtpConnection? idle = null;
            long returnedAt = 0;
            TaskCompletionSource<IFtpConnection?>? wait = null;
            var create = false;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_idle.Count > 0)
                {
                    (idle, returnedAt) = _idle.Pop();
                }
                else if (_live < _limit)
                {
                    _live++;
                    create = true;
                }
                else
                {
                    wait = new TaskCompletionSource<IFtpConnection?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.Enqueue(wait);
                }
            }

            if (idle is not null)
            {
                bool usable;
                try
                {
                    usable = await IsUsableAsync(idle, returnedAt, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    Discard(idle);
                    throw;
                }

                if (usable)
                {
                    return idle;
                }

                Discard(idle);
                continue;
            }

            if (create)
            {
                try
                {
                    return await _connect(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    bool waitForOthers;
                    lock (_gate)
                    {
                        _live--;
                        waitForOthers = _live > 0 && IsTooManyConnections(e);
                        if (waitForOthers)
                        {
                            _limit = _live;
                            SupportLogger.Shared.Log($"Server refused another connection; limiting to {_limit}", "ftp", LogLevel.Notice);
                        }
                    }

                    WakeOne();
                    if (waitForOthers)
                    {
                        continue;
                    }

                    throw;
                }
                catch
                {
                    lock (_gate)
                    {
                        _live--;
                    }

                    WakeOne();
                    throw;
                }
            }

            IFtpConnection? handed;
            using (cancellationToken.Register(() => wait!.TrySetCanceled(cancellationToken)))
            {
                // A cancelled waiter stays queued; Return skips it because TrySetResult fails.
                handed = await wait!.Task.ConfigureAwait(false);
            }

            if (handed is not null)
            {
                return handed;
            }

            // Null: a slot freed up. Go round again.
        }
    }

    /// <summary>
    /// Gives a connection back. <paramref name="reusable"/> is false after a transport failure
    /// or a cancelled transfer, which leave the control connection in an unknown state.
    /// </summary>
    public void Return(IFtpConnection connection, bool reusable)
    {
        lock (_gate)
        {
            if (!_disposed && reusable && connection.IsConnected)
            {
                while (_waiters.Count > 0)
                {
                    if (_waiters.Dequeue().TrySetResult(connection))
                    {
                        return;
                    }
                }

                _idle.Push((connection, Environment.TickCount64));
                return;
            }

            _live--;
        }

        connection.Dispose();
        WakeOne();
    }

    /// <summary>Takes in a connection opened outside the pool (the sign-in probe) as an idle one.</summary>
    public void Adopt(IFtpConnection connection)
    {
        lock (_gate)
        {
            if (!_disposed && _live < _limit)
            {
                _live++;
                _idle.Push((connection, Environment.TickCount64));
                return;
            }
        }

        connection.Dispose();
    }

    public void Dispose()
    {
        List<IFtpConnection> idle;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            idle = _idle.Select(i => i.Connection).ToList();
            _live -= _idle.Count;
            _idle.Clear();
            while (_waiters.Count > 0)
            {
                _waiters.Dequeue().TrySetException(new ObjectDisposedException(nameof(FtpConnectionPool)));
            }
        }

        foreach (var connection in idle)
        {
            connection.Dispose();
        }
    }

    private async Task<bool> IsUsableAsync(IFtpConnection connection, long returnedAt, CancellationToken cancellationToken)
    {
        if (!connection.IsConnected)
        {
            return false;
        }

        return Environment.TickCount64 - returnedAt < ProbeAfter.TotalMilliseconds ||
               await connection.IsAliveAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Discard(IFtpConnection connection)
    {
        lock (_gate)
        {
            _live--;
        }

        connection.Dispose();
        WakeOne();
    }

    /// <summary>Tells one waiter that a slot is free (it then opens its own connection).</summary>
    private void WakeOne()
    {
        lock (_gate)
        {
            while (_waiters.Count > 0)
            {
                if (_waiters.Dequeue().TrySetResult(null))
                {
                    return;
                }
            }
        }
    }

    /// <summary>"421 Too many connections", "530 … too many users", and servers that just hang up.</summary>
    internal static bool IsTooManyConnections(Exception e)
    {
        var message = e.Message;
        return message.Contains("421", StringComparison.Ordinal) ||
               message.Contains("too many", StringComparison.OrdinalIgnoreCase) ||
               (e is FluentFTP.Exceptions.FtpCommandException { CompletionCode: "421" });
    }
}
