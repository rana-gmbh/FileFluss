// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.ObjectModel;
using System.Windows.Threading;
using FileFluss.App.Mvvm;
using FileFluss.Core.Models;
using FileFluss.Core.Transfers;
using static FileFluss.Core.Localization;

namespace FileFluss.App.Services;

/// <summary>
/// Every transfer in both panels. Starts transfers on the thread pool, pumps their live
/// figures a few times a second on the UI thread, and tells the panels when one finishes so
/// they can refresh. The toolbar's Transfers button shows <see cref="All"/>; each sidebar
/// shows its own panel's.
/// </summary>
public sealed class TransferCenter : Observable
{
    private readonly DispatcherTimer _pump;

    public TransferCenter()
    {
        _pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _pump.Tick += (_, _) => Pump();
    }

    /// <summary>Newest first, running before finished — the order the toolbar list uses.</summary>
    public ObservableCollection<TransferProgress> All { get; } = [];

    public event EventHandler<TransferProgress>? TransferCompleted;

    public IEnumerable<TransferProgress> For(PanelSide side) => All.Where(t => t.Panel == side);

    public IEnumerable<TransferProgress> Running => All.Where(t => t.IsRunning);

    public bool HasRunning => All.Any(t => t.IsRunning);

    public bool HasFinished => All.Any(t => !t.IsRunning);

    /// <summary>Mean progress of what is running, or null when nothing is (the toolbar shows a plain icon).</summary>
    public double? RunningFraction
    {
        get
        {
            var running = Running.ToList();
            return running.Count == 0 ? null : running.Average(t => t.Fraction);
        }
    }

    public string ToolbarText
    {
        get
        {
            var running = Running.ToList();
            if (running.Count == 0)
            {
                return All.Count == 0 ? L("No transfers") : L("Finished transfers");
            }

            var percent = (int)Math.Round((RunningFraction ?? 0) * 100);
            return running.Count == 1 ? L("1 transfer — {0}%", percent) : L("{0} transfers — {1}%", running.Count, percent);
        }
    }

    /// <summary>Starts <paramref name="request"/> and shows it in <paramref name="panel"/>'s sidebar.</summary>
    public TransferProgress Start(TransferRequest request, PanelSide panel, Action<TransferProgress>? onFinished = null)
    {
        var progress = TransferEngine.CreateProgress(request, panel);
        Track(progress, onFinished);
        _ = Task.Run(() => TransferEngine.RunAsync(request, progress));
        return progress;
    }

    /// <summary>Shows an externally driven progress (sync, opening a cloud file).</summary>
    public void Track(TransferProgress progress, Action<TransferProgress>? onFinished = null)
    {
        // The list is bound in the UI: changing it from another thread throws there.
        if (!_pump.Dispatcher.CheckAccess())
        {
            _pump.Dispatcher.BeginInvoke(() => Track(progress, onFinished));
            return;
        }

        All.Insert(0, progress);

        // Completed is raised on the transfer's thread; the list and its observers live on this one.
        progress.Completed += (_, _) => _pump.Dispatcher.BeginInvoke(() =>
        {
            Reorder();
            try
            {
                onFinished?.Invoke(progress);
            }
            finally
            {
                TransferCompleted?.Invoke(this, progress);
                OnAllPropertiesChanged();
            }
        });
        _pump.Start();
        OnAllPropertiesChanged();
    }

    public void Remove(TransferProgress progress)
    {
        All.Remove(progress);
        OnAllPropertiesChanged();
    }

    /// <summary>A tidy-up, not a cancel: running transfers stay.</summary>
    public void ClearFinished()
    {
        foreach (var done in All.Where(t => !t.IsRunning).ToList())
        {
            All.Remove(done);
        }

        OnAllPropertiesChanged();
    }

    private void Pump()
    {
        var any = false;
        foreach (var transfer in All)
        {
            if (transfer.IsRunning)
            {
                transfer.Tick();
                any = true;
            }
        }

        OnAllPropertiesChanged();
        if (!any)
        {
            _pump.Stop();
        }
    }

    private void Reorder()
    {
        var ordered = All.OrderByDescending(t => t.IsRunning).ThenByDescending(t => t.StartTime).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = All.IndexOf(ordered[i]);
            if (current != i)
            {
                All.Move(current, i);
            }
        }
    }
}
