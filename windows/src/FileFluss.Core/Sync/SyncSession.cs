// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Formatting;
using FileFluss.Core.Models;
using FileFluss.Core.Storage;
using FileFluss.Core.Transfers;
using static FileFluss.Core.Localization;

namespace FileFluss.Core.Sync;

/// <summary>
/// The Sync sheet (macOS <c>SyncPlannerView</c>): pick a direction and a mode, Calculate a
/// plan, read what it will add/replace/delete and how much space is left afterwards, then
/// start it. UI-agnostic — properties change from worker threads, so the UI marshals
/// <see cref="PropertyChanged"/> (always raised with an empty name: re-read everything) to
/// its own thread. State is consistent under a lock.
/// </summary>
public sealed class SyncSession : INotifyPropertyChanged
{
    private readonly Lock _gate = new();
    private PlanDirection _direction = PlanDirection.LeftToRight;
    private SyncMode _mode = SyncMode.Newer;
    private bool _confirmDestructive;
    private bool _isCalculating;
    private SyncPlan? _plan;
    private string? _errorMessage;
    private long? _destinationAvailable;
    private int _generation;
    private CancellationTokenSource? _calculation;

    /// <param name="left">The left panel's folder; null when it shows none.</param>
    /// <param name="right">The right panel's folder; null when it shows none.</param>
    public SyncSession(SyncEndpoint? left, SyncEndpoint? right)
    {
        Left = left;
        Right = right;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SyncEndpoint? Left { get; }

    public SyncEndpoint? Right { get; }

    /// <summary>The modes in the order the sheet lists them.</summary>
    public IReadOnlyList<SyncMode> Modes => SyncModeExtensions.All;

    /// <summary>Changing it discards the plan, which no longer describes what would happen.</summary>
    public PlanDirection Direction
    {
        get
        {
            lock (_gate)
            {
                return _direction;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_direction == value)
                {
                    return;
                }

                _direction = value;
                InvalidateLocked();
            }

            Notify();
        }
    }

    /// <summary>Changing it discards the plan and the mirror confirmation.</summary>
    public SyncMode Mode
    {
        get
        {
            lock (_gate)
            {
                return _mode;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_mode == value)
                {
                    return;
                }

                _mode = value;
                _confirmDestructive = false;
                InvalidateLocked();
            }

            Notify();
        }
    }

    /// <summary>"I understand files on the destination will be deleted." — required to start a mirror.</summary>
    public bool ConfirmDestructive
    {
        get
        {
            lock (_gate)
            {
                return _confirmDestructive;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_confirmDestructive == value)
                {
                    return;
                }

                _confirmDestructive = value;
            }

            Notify();
        }
    }

    /// <summary>Show the mirror confirmation checkbox.</summary>
    public bool RequiresConfirmation => Mode.IsDestructive();

    public SyncEndpoint? Source => Direction == PlanDirection.LeftToRight ? Left : Right;

    public SyncEndpoint? Destination => Direction == PlanDirection.LeftToRight ? Right : Left;

    /// <summary>The panel whose transfer list shows the running sync and which refreshes after it.</summary>
    public PanelSide DestinationSide => Direction == PlanDirection.LeftToRight ? PanelSide.Right : PanelSide.Left;

    public bool IsCalculating
    {
        get
        {
            lock (_gate)
            {
                return _isCalculating;
            }
        }
    }

    /// <summary>The last calculated plan; null until Calculate, and after any change that invalidates it.</summary>
    public SyncPlan? Plan
    {
        get
        {
            lock (_gate)
            {
                return _plan;
            }
        }
    }

    /// <summary>"Could not compute plan: …" in red under the plan; null when fine.</summary>
    public string? ErrorMessage
    {
        get
        {
            lock (_gate)
            {
                return _errorMessage;
            }
        }
    }

    /// <summary>Free space at the destination when the plan was calculated; null = unlimited or unknown (no projection shown).</summary>
    public long? DestinationAvailableBytes
    {
        get
        {
            lock (_gate)
            {
                return _plan is null ? null : _destinationAvailable;
            }
        }
    }

    /// <summary>Projected free space after the sync; negative when it won't fit. Null without a plan or a known figure.</summary>
    public long? AvailableAfterSync
    {
        get
        {
            lock (_gate)
            {
                return _plan is { } plan && _destinationAvailable is { } available ? available - plan.NetDestinationDelta : null;
            }
        }
    }

    /// <summary>Show "This sync would exceed the available space at the destination." and the After figure in red.</summary>
    public bool WouldExceedAvailableSpace => AvailableAfterSync < 0;

    public bool CanCalculate
    {
        get
        {
            lock (_gate)
            {
                return !_isCalculating && Left is { IsUsable: true } && Right is { IsUsable: true };
            }
        }
    }

    public bool CanStart
    {
        get
        {
            lock (_gate)
            {
                if (_plan is not { } plan || _isCalculating)
                {
                    return false;
                }

                if (_mode.IsDestructive() && !_confirmDestructive)
                {
                    return false;
                }

                return !plan.IsEmpty;
            }
        }
    }

    // MARK: - Display rows

    /// <summary>"Download:" row value; null hides the row.</summary>
    public string? DownloadText => Plan is { DownloadBytes: > 0 } plan ? ByteFormatter.Format(plan.DownloadBytes) : null;

    /// <summary>"Upload:" row value; null hides the row.</summary>
    public string? UploadText => Plan is { UploadBytes: > 0 } plan ? ByteFormatter.Format(plan.UploadBytes) : null;

    /// <summary>"Transfer:" row value (local → local); null hides the row.</summary>
    public string? TransferText
        => Plan is { DownloadBytes: 0, UploadBytes: 0, TotalBytes: > 0 } plan ? ByteFormatter.Format(plan.TotalBytes) : null;

    /// <summary>"Available:" row value; null hides it and "After sync:".</summary>
    public string? AvailableText => DestinationAvailableBytes is { } available ? ByteFormatter.Format(available) : null;

    /// <summary>"After sync:" row value, floored at zero (the red colour carries the overflow).</summary>
    public string? AfterSyncText => AvailableAfterSync is { } after ? ByteFormatter.Format(Math.Max(0, after)) : null;

    /// <summary>The hint shown before the first Calculate.</summary>
    public static string PlanHint => L("Press Calculate to compute how many files will be added, replaced, or deleted.");

    // MARK: - Actions

    /// <summary>
    /// Enumerates both sides (in parallel), plans, and reads the destination's free space.
    /// A newer Calculate, or a change of direction or mode, supersedes this one. Never throws.
    /// </summary>
    public async Task CalculateAsync(CancellationToken cancellationToken = default)
    {
        SyncEndpoint source, destination;
        SyncMode mode;
        PlanDirection direction;
        int generation;
        CancellationToken token;
        lock (_gate)
        {
            if (_isCalculating || Left is not { IsUsable: true } || Right is not { IsUsable: true })
            {
                return;
            }

            source = _direction == PlanDirection.LeftToRight ? Left : Right;
            destination = _direction == PlanDirection.LeftToRight ? Right : Left;
            mode = _mode;
            direction = _direction;
            generation = ++_generation;

            // Not disposed when replaced: a superseded calculation may still be observing it.
            _calculation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            token = _calculation.Token;
            _isCalculating = true;
            _errorMessage = null;
        }

        Notify();

        SyncPlan? plan = null;
        long? available = null;
        string? error = null;
        try
        {
            if (source.Overlaps(destination))
            {
                throw new InvalidOperationException(L("A folder can't be copied or moved into itself."));
            }

            var sourceTask = Task.Run(() => SyncPlanner.EnumerateAsync(source, token), token);
            var destinationTask = Task.Run(() => SyncPlanner.EnumerateAsync(destination, token), token);
            await Task.WhenAll(sourceTask, destinationTask).ConfigureAwait(false);
            plan = SyncPlanner.Plan(
                await sourceTask.ConfigureAwait(false),
                await destinationTask.ConfigureAwait(false),
                mode,
                direction,
                source.IsCloud,
                destination.IsCloud,
                ignoreCase: !source.IsCloud || !destination.IsCloud);
            available = await SpaceCheck.AvailableSpaceAsync(destination.Provider, destination.Path, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            plan = null;
        }
        catch (Exception e)
        {
            error = L("Could not compute plan: {0}", e.Message);
            SupportLogger.Shared.Log($"Sync plan failed: {e.Message}", "Sync", LogLevel.Error);
        }

        lock (_gate)
        {
            if (generation != _generation)
            {
                // Superseded: a newer Calculate or an invalidating change owns the state now.
                return;
            }

            _isCalculating = false;
            if (plan is not null)
            {
                _plan = plan;
                _destinationAvailable = available;
            }
            else if (error is not null)
            {
                _errorMessage = error;
                _plan = null;
                _destinationAvailable = null;
            }
        }

        Notify();
    }

    /// <summary>Stops a running Calculate; the previous plan (if any) stays.</summary>
    public void CancelCalculation()
    {
        lock (_gate)
        {
            _calculation?.Cancel();
        }
    }

    /// <summary>
    /// Starts the calculated plan in the background and returns its progress, for the
    /// destination panel's transfer list (<see cref="DestinationSide"/>); the UI ticks it,
    /// and refreshes that panel on <see cref="TransferProgress.Completed"/>. Null when
    /// <see cref="CanStart"/> is false.
    /// </summary>
    public TransferProgress? StartSync()
    {
        SyncPlan plan;
        SyncEndpoint source, destination;
        PanelSide side;
        lock (_gate)
        {
            if (_plan is not { IsEmpty: false } current || _isCalculating || (_mode.IsDestructive() && !_confirmDestructive))
            {
                return null;
            }

            plan = current;
            source = (_direction == PlanDirection.LeftToRight ? Left : Right)!;
            destination = (_direction == PlanDirection.LeftToRight ? Right : Left)!;
            side = _direction == PlanDirection.LeftToRight ? PanelSide.Right : PanelSide.Left;
        }

        var progress = SyncExecutor.CreateProgress(plan, source, destination, side);
        ExecutionTask = Task.Run(() => SyncExecutor.ExecuteAsync(plan, source, destination, progress));
        return progress;
    }

    /// <summary>The running (or finished) sync started by <see cref="StartSync"/>; completes when the sync does.</summary>
    public Task? ExecutionTask { get; private set; }

    private void InvalidateLocked()
    {
        _generation++;
        _calculation?.Cancel();
        _isCalculating = false;
        _plan = null;
        _destinationAvailable = null;
        _errorMessage = null;
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
