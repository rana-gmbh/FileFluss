// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using FileFluss.Core.Formatting;
using static FileFluss.Core.Localization;

namespace FileFluss.Core.Storage;

/// <summary>One "Show" choice: hides everything smaller, so a long tail of small files doesn't bury the few that matter.</summary>
public sealed record StorageSizeThreshold(string Label, long Bytes);

/// <summary>A row of the folder tree as currently expanded: indent, disclosure, name, share bar, size.</summary>
/// <param name="Fraction">Share of the whole analysis, 0…1, for the bar.</param>
/// <param name="ShareText">Tooltip of the bar: "12.5% of the total".</param>
public sealed record StorageRow(StorageNode Node, int Depth, bool IsExpanded, bool IsExpandable, double Fraction, string SizeText, string ShareText);

/// <summary>
/// The Storage Analysis window (macOS <c>StorageAnalysisView</c> + <c>StorageScanner</c>):
/// "where has my space gone" for a cloud account, a folder inside one, or a local folder — a
/// size-ordered folder tree beside the largest individual files. Results fill in while the
/// scan runs; Cancel keeps what was found and says the totals are a lower bound.
///
/// <para>UI-agnostic. Properties change on worker threads; <see cref="PropertyChanged"/> is
/// raised with an empty name (re-read everything) and the UI marshals it to its own thread.</para>
/// </summary>
public sealed class StorageAnalysisSession : INotifyPropertyChanged
{
    private readonly Lock _gate = new();
    private HashSet<string> _expanded = new(StringComparer.Ordinal);
    private StorageScanSource? _source;
    private bool _isScanning;
    private StorageReport? _report;
    private int _filesSeen;
    private long _bytesSeen;
    private string _currentPath = string.Empty;
    private string? _errorMessage;
    private long _minimumSize;
    private bool _expandRootPending;
    private int _generation;
    private CancellationTokenSource? _scan;
    private IReadOnlyList<StorageRow>? _rows;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The "Show" picker, in the current language. Binary multiples, matching how sizes are written on Windows.</summary>
    public static IReadOnlyList<StorageSizeThreshold> SizeThresholds =>
    [
        new(L("All sizes"), 0),
        new(L("1 MB and up"), 1L << 20),
        new(L("10 MB and up"), 10L << 20),
        new(L("100 MB and up"), 100L << 20),
        new(L("1 GB and up"), 1L << 30),
    ];

    public static string PlaceholderText => L("Pick a cloud account or a folder, then choose Analyse.");

    public static string NoLargeFilesText => L("No files above this size.");

    /// <summary>The source the current <see cref="Report"/> belongs to.</summary>
    public StorageScanSource? ScannedSource
    {
        get
        {
            lock (_gate)
            {
                return _source;
            }
        }
    }

    public bool IsScanning
    {
        get
        {
            lock (_gate)
            {
                return _isScanning;
            }
        }
    }

    /// <summary>The latest report: partial while scanning, final afterwards.</summary>
    public StorageReport? Report
    {
        get
        {
            lock (_gate)
            {
                return _report;
            }
        }
    }

    public int FilesSeen
    {
        get
        {
            lock (_gate)
            {
                return _filesSeen;
            }
        }
    }

    public long BytesSeen
    {
        get
        {
            lock (_gate)
            {
                return _bytesSeen;
            }
        }
    }

    /// <summary>The folder being read right now; empty when idle.</summary>
    public string CurrentPath
    {
        get
        {
            lock (_gate)
            {
                return _currentPath;
            }
        }
    }

    /// <summary>The scan couldn't start (the root folder can't be read); shown instead of results.</summary>
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

    /// <summary>Hide files below this many bytes (folders always show). One of <see cref="SizeThresholds"/>.</summary>
    public long MinimumSize
    {
        get
        {
            lock (_gate)
            {
                return _minimumSize;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_minimumSize == value)
                {
                    return;
                }

                _minimumSize = value;
                _rows = null;
            }

            Notify();
        }
    }

    /// <summary>"1234 files · 5.6 GB · /Photos/2026" beside the spinner.</summary>
    public string ProgressText
    {
        get
        {
            lock (_gate)
            {
                var bytes = ByteFormatter.Format(_bytesSeen);
                return _currentPath.Length == 0
                    ? L("{0} files · {1}", _filesSeen, bytes)
                    : L("{0} files · {1} · {2}", _filesSeen, bytes, _currentPath);
            }
        }
    }

    /// <summary>"5.6 GB · 1234 files · 56 folders" once a report exists.</summary>
    public string? SummaryText
        => Report is { } report
            ? L("{0} · {1} files · {2} folders", ByteFormatter.Format(report.TotalBytes), report.FileCount, report.FolderCount)
            : null;

    /// <summary>The banner over a cancelled scan's results; null otherwise.</summary>
    public string? CancelledNotice
        => Report is { WasCancelled: true } ? L("Stopped early, so these totals are a lower bound — the real figures are larger.") : null;

    /// <summary>The expanded part of the tree, flattened — a virtualizing list stays fast on a 100k-file account.</summary>
    public IReadOnlyList<StorageRow> Rows
    {
        get
        {
            lock (_gate)
            {
                return _rows ??= BuildRowsLocked();
            }
        }
    }

    /// <summary>The largest files at or above <see cref="MinimumSize"/>, biggest first.</summary>
    public IReadOnlyList<StorageNode> LargestFiles
    {
        get
        {
            lock (_gate)
            {
                var minimum = _minimumSize;
                return _report?.LargestFiles.Where(f => f.Size >= minimum).ToList() ?? [];
            }
        }
    }

    // MARK: - Actions

    /// <summary>Starts a scan in the background, replacing any running one.</summary>
    public void Scan(StorageScanSource source) => _ = ScanAsync(source);

    /// <summary>Scans <paramref name="source"/>, replacing any running scan; completes when it ends. Never throws.</summary>
    public async Task ScanAsync(StorageScanSource source, CancellationToken cancellationToken = default)
    {
        int generation;
        CancellationToken token;
        lock (_gate)
        {
            _scan?.Cancel();
            _scan = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            token = _scan.Token;
            generation = ++_generation;
            _source = source;
            _isScanning = true;
            _errorMessage = null;
            _report = null;
            _filesSeen = 0;
            _bytesSeen = 0;
            _currentPath = string.Empty;
            _expanded = new HashSet<string>(source.Provider.IsLocal ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            // A collapsed root tells nobody anything: open it as soon as there is one.
            _expandRootPending = true;
            _rows = null;
        }

        Notify();

        StorageReport? report = null;
        string? error = null;
        try
        {
            report = await Task.Run(() => StorageScanner.ScanAsync(source, update => Apply(generation, update), token), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _isScanning = false;
            _currentPath = string.Empty;
            if (report is not null)
            {
                SetReportLocked(report);
                _filesSeen = report.FileCount;
                _bytesSeen = report.TotalBytes;
            }
            else
            {
                _errorMessage = error;
            }
        }

        Notify();
    }

    /// <summary>Stops the scan; the results so far stay, marked as a lower bound.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _scan?.Cancel();
        }
    }

    public bool IsExpanded(StorageNode node)
    {
        lock (_gate)
        {
            return _expanded.Contains(node.Id);
        }
    }

    /// <summary>Opens or closes a folder row.</summary>
    public void ToggleExpanded(StorageNode node)
    {
        if (!node.IsDirectory)
        {
            return;
        }

        lock (_gate)
        {
            if (!_expanded.Remove(node.Id))
            {
                _expanded.Add(node.Id);
            }

            _rows = null;
        }

        Notify();
    }

    // MARK: - Internals

    private void Apply(int generation, StorageScanUpdate update)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _filesSeen = update.FilesSeen;
            _bytesSeen = update.BytesSeen;
            _currentPath = update.CurrentPath;
            if (update.Partial is { } partial)
            {
                SetReportLocked(partial);
            }
        }

        Notify();
    }

    private void SetReportLocked(StorageReport report)
    {
        _report = report;
        if (_expandRootPending)
        {
            _expanded.Add(report.Root.Id);
            _expandRootPending = false;
        }

        _rows = null;
    }

    private List<StorageRow> BuildRowsLocked()
    {
        var rows = new List<StorageRow>();
        if (_report is not { } report)
        {
            return rows;
        }

        var total = report.Root.Size;
        Append(report.Root, 0);
        return rows;

        void Append(StorageNode node, int depth)
        {
            var expanded = _expanded.Contains(node.Id);
            var fraction = node.Fraction(total);
            rows.Add(new StorageRow(
                node,
                depth,
                expanded,
                node.IsDirectory && node.Children.Count > 0,
                fraction,
                ByteFormatter.Format(node.Size),
                L("{0}% of the total", (fraction * 100).ToString("0.0", Culture))));
            if (!expanded)
            {
                return;
            }

            foreach (var child in node.Children)
            {
                if (child.IsDirectory || child.Size >= _minimumSize)
                {
                    Append(child, depth + 1);
                }
            }
        }
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
