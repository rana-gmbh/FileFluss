// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using FileFluss.Core.Formatting;
using FileFluss.Core.Models;
using static FileFluss.Core.Localization;

namespace FileFluss.Core.Transfers;

public enum TransferOperation
{
    Copying,
    Moving,
    Uploading,
    Downloading,
    Opening,
    Mirroring,
    SyncingNewer,
    Adding,
}

public enum TransferPhase
{
    Preparing,
    Downloading,
    Uploading,
    Copying,
}

public enum TransferItemStatus
{
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
}

public sealed record TransferItemResult(string Name, TransferItemStatus Status, string? ErrorMessage);

/// <summary>
/// One running or finished transfer — the macOS <c>TransferProgress</c>, made thread-safe.
///
/// <para>The engine reports from worker threads through the <c>Begin…</c>/<c>Add…</c>/
/// <c>Record…</c> methods; byte deltas from providers arrive per network chunk and land in
/// counters with no notification. The UI calls <see cref="Tick"/> a few times a second, which
/// re-measures the live speed and raises one <see cref="PropertyChanged"/> for everything —
/// the display can't show more than that anyway (issue #50).</para>
///
/// <para>Figures appear only when they are genuinely measured: speed, "x of y" and time
/// left are null whenever they would be a guess.</para>
/// </summary>
public sealed class TransferProgress : INotifyPropertyChanged
{
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(4);

    private readonly Lock _gate = new();
    private readonly List<TransferItemResult> _results = [];
    private readonly Queue<(DateTimeOffset Time, long Bytes)> _samples = new();
    private readonly CancellationTokenSource _cancellation = new();
    private long _downloadBytes;
    private long _uploadBytes;
    private long _currentFileBytes;

    public TransferProgress(TransferOperation operation, int totalItems, PanelSide panel)
    {
        Operation = operation;
        TotalItems = totalItems;
        Panel = panel;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised once, on whatever thread finishes the transfer.</summary>
    public event EventHandler? Completed;

    public Guid Id { get; } = Guid.NewGuid();

    public TransferOperation Operation { get; }

    /// <summary>The panel whose sidebar shows this transfer (the destination's).</summary>
    public PanelSide Panel { get; }

    public int TotalItems { get; }

    public DateTimeOffset StartTime { get; } = DateTimeOffset.Now;

    public DateTimeOffset? EndTime { get; private set; }

    public CancellationToken CancellationToken => _cancellation.Token;

    public bool IsCancelled { get; private set; }

    public bool IsComplete { get; private set; }

    public bool IsRunning => !IsComplete;

    /// <summary>A failure before any per-item work could start (provider unavailable).</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Downloaded then uploaded: every byte travels twice and the phases are shown.</summary>
    public bool IsCloudToCloud { get; set; }

    public TransferPhase Phase { get; private set; } = TransferPhase.Preparing;

    public int CompletedItems { get; private set; }

    /// <summary>Files discovered in the whole tree (0 until the plan is known).</summary>
    public int TotalFiles { get; private set; }

    public int CompletedFiles { get; private set; }

    public long ExpectedBytes { get; private set; }

    public string CurrentFileName { get; private set; } = string.Empty;

    public long CurrentFileSize { get; private set; }

    public long CurrentFileBytes => Interlocked.Read(ref _currentFileBytes);

    public long DownloadBytes => Interlocked.Read(ref _downloadBytes);

    public long UploadBytes => Interlocked.Read(ref _uploadBytes);

    public long MovedBytes => DownloadBytes + UploadBytes;

    /// <summary>Bytes per second over the last few seconds; null until measured.</summary>
    public double? LiveBytesPerSecond { get; private set; }

    /// <summary>Top-level names, as the summary lists them.</summary>
    public IReadOnlyList<string> ItemNames { get; private set; } = [];

    public IReadOnlySet<string> FolderNames { get; private set; } = new HashSet<string>();

    public IReadOnlyList<TransferItemResult> Results
    {
        get
        {
            lock (_gate)
            {
                return _results.ToList();
            }
        }
    }

    // MARK: - Reporting (any thread)

    public void SetItems(IEnumerable<CloudFileItem> topLevel)
    {
        var list = topLevel.ToList();
        ItemNames = list.Select(i => i.Name).ToList();
        FolderNames = list.Where(i => i.IsDirectory).Select(i => i.Name).ToHashSet();
    }

    public void SetPlan(int totalFiles, long expectedBytes)
    {
        TotalFiles = totalFiles;
        ExpectedBytes = expectedBytes;
    }

    public void SetPhase(TransferPhase phase) => Phase = phase;

    public void BeginFile(string name, long size)
    {
        CurrentFileName = name;
        CurrentFileSize = size;
        Interlocked.Exchange(ref _currentFileBytes, 0);
    }

    public void AddDownloadBytes(long delta)
    {
        if (delta <= 0)
        {
            return;
        }

        Interlocked.Add(ref _downloadBytes, delta);
        Interlocked.Add(ref _currentFileBytes, delta);
    }

    public void AddUploadBytes(long delta)
    {
        if (delta <= 0)
        {
            return;
        }

        Interlocked.Add(ref _uploadBytes, delta);
        Interlocked.Add(ref _currentFileBytes, delta);
    }

    /// <summary>Credits bytes a provider didn't stream-report (or under-reported) once a file is done.</summary>
    public void TopUp(bool upload, long reportedForFile, long fileSize)
    {
        if (reportedForFile < fileSize)
        {
            if (upload)
            {
                Interlocked.Add(ref _uploadBytes, fileSize - reportedForFile);
            }
            else
            {
                Interlocked.Add(ref _downloadBytes, fileSize - reportedForFile);
            }
        }
    }

    public void FileFinished()
    {
        lock (_gate)
        {
            CompletedFiles++;
        }
    }

    public void RecordSuccess(string name) => Record(name, TransferItemStatus.Succeeded, null);

    public void RecordFailure(string name, string error) => Record(name, TransferItemStatus.Failed, error);

    public void RecordSkip(string name) => Record(name, TransferItemStatus.Skipped, null);

    private void Record(string name, TransferItemStatus status, string? error)
    {
        lock (_gate)
        {
            _results.Add(new TransferItemResult(name, status, error));
            CompletedItems = Math.Min(TotalItems, CompletedItems + 1);
        }
    }

    public void Fail(string message) => ErrorMessage = message;

    /// <summary>Requests cancellation; the engine stops after discarding the file in flight.</summary>
    public void Cancel()
    {
        if (IsComplete)
        {
            return;
        }

        IsCancelled = true;
        LiveBytesPerSecond = null;
        _cancellation.Cancel();
        Notify();
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (IsComplete)
            {
                return;
            }

            IsComplete = true;
            EndTime = DateTimeOffset.Now;
            CurrentFileName = string.Empty;
            LiveBytesPerSecond = null;
        }

        Notify();
        Completed?.Invoke(this, EventArgs.Empty);
    }

    // MARK: - Outcome

    public int SuccessCount => Results.Count(r => r.Status == TransferItemStatus.Succeeded);

    public int FailureCount => Results.Count(r => r.Status == TransferItemStatus.Failed);

    public int SkippedCount => Results.Count(r => r.Status == TransferItemStatus.Skipped);

    public bool HasErrors => ErrorMessage is not null || FailureCount > 0;

    /// <summary>
    /// Names of top-level items that actually arrived — the only safe basis for deleting a
    /// source after a move: a batch can end with failures, skips or a cancellation.
    /// </summary>
    public IReadOnlySet<string> SucceededNames
        => Results.Where(r => r.Status == TransferItemStatus.Succeeded).Select(r => r.Name).ToHashSet();

    /// <summary>Every attempted item succeeded and nothing was skipped or cancelled.</summary>
    public bool CompletedCleanly => !HasErrors && !IsCancelled && SkippedCount == 0;

    // MARK: - Live figures

    /// <summary>
    /// 0…1. A finished transfer reads its outcome, not its counters (an empty folder moves no
    /// bytes and must still reach 100%); a cancelled one stays where it stopped.
    /// </summary>
    public double Fraction
    {
        get
        {
            if (IsComplete && !IsCancelled)
            {
                return 1;
            }

            if (ExpectedBytes > 0)
            {
                return Math.Clamp((double)MovedBytes / ExpectedBytes, 0, 1);
            }

            var total = TotalFiles > 0 ? TotalFiles : TotalItems;
            return total > 0 ? Math.Clamp((double)(TotalFiles > 0 ? CompletedFiles : CompletedItems) / total, 0, 1) : 0;
        }
    }

    public string PercentText => $"{(int)Math.Round(Fraction * 100)}%";

    public double? CurrentFileFraction
        => CurrentFileSize > 0 && CurrentFileBytes > 0 ? Math.Min(1, (double)CurrentFileBytes / CurrentFileSize) : null;

    public bool HasByteReporting => MovedBytes > 0;

    public TimeSpan? TimeRemaining
    {
        get
        {
            if (IsComplete || IsCancelled || LiveBytesPerSecond is not { } speed || speed <= 0 || ExpectedBytes <= 0)
            {
                return null;
            }

            var remaining = ExpectedBytes - MovedBytes;
            return remaining > 0 ? TimeSpan.FromSeconds(remaining / speed) : null;
        }
    }

    public TimeSpan Duration => (EndTime ?? DateTimeOffset.Now) - StartTime;

    public string? LiveSpeedText => LiveBytesPerSecond is { } speed ? ByteFormatter.FormatSpeed(speed) : null;

    /// <summary>"340 MB of 1.2 GB".</summary>
    public string? MovedOfExpectedText
        => ExpectedBytes > 0 && HasByteReporting
            ? L("{0} of {1}", ByteFormatter.Format(Math.Min(MovedBytes, ExpectedBytes)), ByteFormatter.Format(ExpectedBytes))
            : null;

    public string? EtaText => TimeRemaining is { } eta ? L("~{0} left", ByteFormatter.FormatDuration(eta)) : null;

    /// <summary>Speed · moved of total · time left — each part only when known.</summary>
    public string? LiveDetailLine
    {
        get
        {
            if (IsComplete)
            {
                return null;
            }

            var parts = new[] { LiveSpeedText, MovedOfExpectedText, EtaText }.Where(p => p is not null).ToList();
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    public string AverageSpeedText
    {
        get
        {
            var seconds = Duration.TotalSeconds;
            return seconds > 0 && MovedBytes > 0 ? ByteFormatter.FormatSpeed(MovedBytes / seconds / (IsCloudToCloud ? 2 : 1)) : "--";
        }
    }

    public static string OperationLabel(TransferOperation operation) => operation switch
    {
        TransferOperation.Copying => L("Copying"),
        TransferOperation.Moving => L("Moving"),
        TransferOperation.Uploading => L("Uploading"),
        TransferOperation.Downloading => L("Downloading"),
        TransferOperation.Opening => L("Opening"),
        TransferOperation.Mirroring => L("Mirroring"),
        TransferOperation.SyncingNewer => L("Syncing newer"),
        _ => L("Adding"),
    };

    private string PastTense => Operation switch
    {
        TransferOperation.Copying => L("Copied"),
        TransferOperation.Moving => L("Moved"),
        TransferOperation.Downloading => L("Downloaded"),
        TransferOperation.Uploading => L("Uploaded"),
        TransferOperation.Opening => L("Opened"),
        _ => L("Synced"),
    };

    private string ItemLabel
    {
        get
        {
            var names = ItemNames;
            if (names.Count == 1)
            {
                return names[0];
            }

            var folders = names.Count(n => FolderNames.Contains(n));
            var files = names.Count - folders;
            var parts = new List<string>();
            if (folders > 0)
            {
                parts.Add(folders == 1 ? L("1 folder") : L("{0} folders", folders));
            }

            if (files > 0)
            {
                parts.Add(files == 1 ? L("1 file") : L("{0} files", files));
            }

            return parts.Count == 0 ? L("{0} items", names.Count) : string.Join(", ", parts);
        }
    }

    /// <summary>The headline: what it is doing now, or how it ended.</summary>
    public string StatusText
    {
        get
        {
            if (IsComplete)
            {
                return CompletionSummary;
            }

            if (IsCancelled)
            {
                return L("Cancelling…");
            }

            if (Phase == TransferPhase.Preparing)
            {
                return L("Preparing…");
            }

            if (IsCloudToCloud)
            {
                var label = CurrentFileName.Length > 0 ? CurrentFileName : ItemLabel;
                return Phase == TransferPhase.Uploading ? L("Uploading {0}", label) : L("Downloading {0}", label);
            }

            var total = TotalFiles > 0 ? TotalFiles : TotalItems;
            var done = TotalFiles > 0 ? CompletedFiles : CompletedItems;
            return L("{0} {1} — {2} of {3} files", OperationLabel(Operation), ItemLabel, done, total);
        }
    }

    public string CompletionSummary
    {
        get
        {
            var names = ItemNames;
            if (IsCancelled)
            {
                return L("Cancelled: {0} {1}", OperationLabel(Operation), names.Count == 1 ? names[0] : L("{0} items", names.Count));
            }

            var successes = SuccessCount;
            var failures = FailureCount;
            if (failures > 0 && successes > 0)
            {
                return L("{0} {1} of {2} — {3} failed", PastTense, successes, successes + failures + SkippedCount, failures);
            }

            if (ErrorMessage is not null || (failures > 0 && successes == 0))
            {
                var results = Results;
                var firstFailure = results.FirstOrDefault(r => r.Status == TransferItemStatus.Failed);
                var label = failures == 1 ? firstFailure?.Name ?? names.FirstOrDefault() ?? string.Empty
                    : failures > 0 ? L("{0} items", failures)
                    : names.Count == 1 ? names[0] : L("{0} items", names.Count);
                var detail = ErrorMessage ?? firstFailure?.ErrorMessage ?? L("Unknown error");
                return L("Failed: {0} {1} — {2}", OperationLabel(Operation), label, detail);
            }

            if (successes == 0 && SkippedCount > 0)
            {
                return L("Skipped: {0}", names.Count == 1 ? names[0] : L("{0} items", names.Count));
            }

            if (names.Count == 0)
            {
                return L("Done");
            }

            if (names.Count == 1)
            {
                return FolderNames.Count > 0 && TotalFiles > 1
                    ? L("Done: {0} {1} ({2} files)", PastTense, names[0], TotalFiles)
                    : L("Done: {0} {1}", PastTense, names[0]);
            }

            var shown = string.Join(", ", names.Take(3));
            return names.Count > 3
                ? L("Done: {0} {1} +{2} more", PastTense, shown, names.Count - 3)
                : L("Done: {0} {1}", PastTense, shown);
        }
    }

    // MARK: - Pump

    /// <summary>
    /// Re-measures the live speed from the byte counters and notifies observers. Called by
    /// the UI on a timer; runs even when nothing moved, so a stalled transfer stops
    /// claiming the speed it had a minute ago.
    /// </summary>
    public void Tick()
    {
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            _samples.Enqueue((now, MovedBytes));
            while (_samples.Count > 0 && now - _samples.Peek().Time > SpeedWindow)
            {
                _samples.Dequeue();
            }

            if (IsComplete || IsCancelled || _samples.Count < 2)
            {
                LiveBytesPerSecond = null;
            }
            else
            {
                var first = _samples.Peek();
                var seconds = (now - first.Time).TotalSeconds;
                var moved = MovedBytes - first.Bytes;
                LiveBytesPerSecond = seconds >= 0.5 && moved > 0 ? moved / seconds : null;
            }
        }

        Notify();
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
