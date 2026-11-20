// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Transfers;

/// <summary>
/// Asks the user about name clashes one question at a time, for every transfer at once
/// (macOS 1.6 <c>ConflictPrompter</c>).
///
/// <para>Each transfer used to put its question up the moment it had one, so two transfers
/// clashing together stacked two dialogs, the second inside the first's modal loop — and the
/// first could only be answered once the second was. Questions now wait their turn, in the
/// order they were asked. A cancelled transfer gets <see cref="ConflictChoice.Stop"/>: a
/// question still waiting is withdrawn, and one on screen is closed, so Cancel works while
/// the question is up.</para>
/// </summary>
public sealed class ConflictPrompter
{
    private static readonly ConflictResolution Stop = new(ConflictChoice.Stop, false);

    private readonly ConflictResolver _show;
    private readonly Lock _gate = new();
    private readonly LinkedList<TaskCompletionSource> _waiting = [];
    private bool _busy;

    /// <param name="show">
    /// Shows one question and returns the answer. Called for one question at a time; it should
    /// close its question when the token is cancelled.
    /// </param>
    public ConflictPrompter(ConflictResolver show)
    {
        _show = show;
    }

    /// <summary>Questions waiting behind the one on screen.</summary>
    public int WaitingCount
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count;
            }
        }
    }

    /// <summary>A <see cref="ConflictResolver"/> for a transfer request.</summary>
    public async Task<ConflictResolution> AskAsync(ConflictInfo conflict, CancellationToken cancellationToken)
    {
        if (!await WaitForTurnAsync(cancellationToken).ConfigureAwait(false))
        {
            return Stop;
        }

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Stop;
            }

            return await _show(conflict, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Stop;
        }
        finally
        {
            PassTurn();
        }
    }

    /// <summary>True once it is this question's turn; false when its transfer was cancelled while waiting.</summary>
    private async Task<bool> WaitForTurnAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource turn;
        LinkedListNode<TaskCompletionSource> node;
        lock (_gate)
        {
            if (!_busy)
            {
                _busy = true;
                return true;
            }

            turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiting.AddLast(turn);
        }

        await using var registration = cancellationToken.Register(() =>
        {
            bool withdrawn;
            lock (_gate)
            {
                withdrawn = node.List is not null;
                if (withdrawn)
                {
                    _waiting.Remove(node);
                }
            }

            // Only a question still in the queue is withdrawn; one whose turn already came owns it.
            if (withdrawn)
            {
                turn.TrySetCanceled(cancellationToken);
            }
        }).ConfigureAwait(false);

        try
        {
            await turn.Task.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void PassTurn()
    {
        TaskCompletionSource? next = null;
        lock (_gate)
        {
            if (_waiting.First is { } first)
            {
                _waiting.RemoveFirst();
                next = first.Value;
            }
            else
            {
                _busy = false;
            }
        }

        next?.TrySetResult();
    }
}
