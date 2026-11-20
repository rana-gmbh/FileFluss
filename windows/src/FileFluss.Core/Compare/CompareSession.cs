// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Sync;
using static FileFluss.Core.Localization;

namespace FileFluss.Core.Compare;

/// <summary>The filter chips above the comparison.</summary>
public enum CompareFilter
{
    All,
    Differences,
    OnlyLeft,
    OnlyRight,
    Identical,
}

public static class CompareFilterExtensions
{
    public static IReadOnlyList<CompareFilter> All { get; } =
        [CompareFilter.All, CompareFilter.Differences, CompareFilter.OnlyLeft, CompareFilter.OnlyRight, CompareFilter.Identical];

    public static string Title(this CompareFilter filter) => filter switch
    {
        CompareFilter.All => L("All"),
        CompareFilter.Differences => L("Differences"),
        CompareFilter.OnlyLeft => L("Only Left"),
        CompareFilter.OnlyRight => L("Only Right"),
        _ => L("Identical"),
    };

    public static bool Matches(this CompareFilter filter, FolderCompareEntry entry) => filter switch
    {
        CompareFilter.All => true,
        CompareFilter.Differences => entry.Status != FolderCompareStatus.Identical,
        CompareFilter.OnlyLeft => entry.Status == FolderCompareStatus.OnlyLeft,
        CompareFilter.OnlyRight => entry.Status == FolderCompareStatus.OnlyRight,
        _ => entry.Status == FolderCompareStatus.Identical,
    };

    public static int Count(this CompareFilter filter, FolderComparisonResult result) => filter switch
    {
        CompareFilter.All => result.Entries.Count,
        CompareFilter.Differences => result.DiffersCount + result.OnlyLeftCount + result.OnlyRightCount,
        CompareFilter.OnlyLeft => result.OnlyLeftCount,
        CompareFilter.OnlyRight => result.OnlyRightCount,
        _ => result.IdenticalCount,
    };

    /// <summary>A folder row's right-hand column: what's beneath it that this filter shows ("2 different · 1 only left").</summary>
    public static string FolderSummary(this CompareFilter filter, CompareCounts counts)
    {
        var parts = new List<string>(4);
        if (filter is CompareFilter.All or CompareFilter.Differences && counts.Different > 0)
        {
            parts.Add(L("{0} different", counts.Different));
        }

        if (filter is CompareFilter.All or CompareFilter.Differences or CompareFilter.OnlyLeft && counts.OnlyLeft > 0)
        {
            parts.Add(L("{0} only left", counts.OnlyLeft));
        }

        if (filter is CompareFilter.All or CompareFilter.Differences or CompareFilter.OnlyRight && counts.OnlyRight > 0)
        {
            parts.Add(L("{0} only right", counts.OnlyRight));
        }

        if (filter is CompareFilter.All or CompareFilter.Identical && counts.Identical > 0)
        {
            parts.Add(L("{0} same", counts.Identical));
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>A filter chip: "Differences (6)".</summary>
public sealed record CompareFilterChip(CompareFilter Filter, string Title, int Count, bool IsSelected);

/// <summary>
/// A visible row of the comparison. A <see cref="IsGroup"/> row is a folder with matching
/// entries beneath it — a disclosure header with the folder's own status (none for a
/// synthetic folder) and <see cref="FolderSummary"/>. Any other row is a leaf: a file, or a
/// folder with nothing beneath it that the filter shows.
/// </summary>
/// <param name="ShowsDates">Compare Date was on for this result.</param>
public sealed record CompareRow(CompareTreeNode Node, int Depth, bool IsGroup, bool IsExpanded, string? FolderSummary, bool ShowsDates)
{
    public string Name => Node.Name;

    public bool IsDirectory => Node.IsDirectory;

    public FolderCompareEntry? Entry => Node.Entry;

    /// <summary>The status icon (✓ green, ← blue, → orange, ⚠ red); null for a synthetic folder (neutral folder icon).</summary>
    public FolderCompareStatus? Status => Node.Entry?.Status;

    /// <summary>The size line of a leaf row.</summary>
    public string? DetailText => IsGroup ? null : Node.Entry?.DetailText;

    private FolderCompareEntry? DifferingWithDates
        => !IsGroup && ShowsDates && Node.Entry is { Status: FolderCompareStatus.Differs } entry ? entry : null;

    /// <summary>"Left newer" / "Right newer" capsule; only with Compare Date on, for a differing row.</summary>
    public string? NewerBadge => DifferingWithDates?.NewerSide switch
    {
        CompareSide.Left => L("Left newer"),
        CompareSide.Right => L("Right newer"),
        _ => null,
    };

    public string? ModifiedDetail => DifferingWithDates?.ModifiedDetail;

    public string? CreatedDetail => DifferingWithDates?.CreatedDetail;
}

/// <summary>
/// The Compare Folders window (macOS <c>CompareFoldersView</c>). Compares the folders the two
/// panels showed when it started — endpoints are snapshotted, so browsing while the window is
/// open changes neither what is compared nor what is shown; only <see cref="CompareAsync"/>
/// (the toolbar's Compare, re-snapshotting) or <see cref="CompareAgainAsync"/> runs it again.
/// Toggling Compare Date re-diffs the cached listings without reading the folders again.
///
/// <para>UI-agnostic. Properties change on worker threads; <see cref="PropertyChanged"/> is
/// raised with an empty name (re-read everything) and the UI marshals it to its own thread.</para>
/// </summary>
public sealed class CompareSession : INotifyPropertyChanged
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private SyncEndpoint? _left;
    private SyncEndpoint? _right;
    private bool _isCalculating;
    private string? _errorMessage;
    private CompareFilter _filter = CompareFilter.Differences;
    private bool _compareDates;
    private IReadOnlyList<SyncEntry> _leftEntries = [];
    private IReadOnlyList<SyncEntry> _rightEntries = [];
    private bool _ignoreCase;
    private FolderComparisonResult? _result;
    private CompareTreeNode? _tree;
    private HashSet<CompareTreeNode> _visible = [];
    private IReadOnlyList<CompareRow>? _rows;
    private int _generation;
    private CancellationTokenSource? _calculation;

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string LoadingText => L("Reading folders…");

    public static string PlaceholderTitle => L("Open a folder on each side to compare");

    public static string PlaceholderDescription => L("Both panels need a folder selected to run a comparison.");

    public static string NothingMatchesText => L("Nothing matches this filter.");

    public static string CompareDatesHelp => L("Treat files with matching size but different modification dates as different. Shows which side is newer along with both modify and create dates.");

    public static string CompareAgainHelp => L("Re-run the comparison against the panels' current folders");

    /// <summary>The left folder being compared (snapshotted when the comparison started).</summary>
    public SyncEndpoint? LeftEndpoint
    {
        get
        {
            lock (_gate)
            {
                return _left;
            }
        }
    }

    public SyncEndpoint? RightEndpoint
    {
        get
        {
            lock (_gate)
            {
                return _right;
            }
        }
    }

    /// <summary>False shows the "Open a folder on each side to compare" placeholder.</summary>
    public bool HasEndpoints
    {
        get
        {
            lock (_gate)
            {
                return _left is { IsUsable: true } && _right is { IsUsable: true };
            }
        }
    }

    /// <summary>Show <see cref="LoadingText"/> instead of results; Compare Again is disabled.</summary>
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

    /// <summary>"Could not read folders: …", shown with a Try Again button (<see cref="CompareAgainAsync"/>).</summary>
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

    public FolderComparisonResult? Result
    {
        get
        {
            lock (_gate)
            {
                return _result;
            }
        }
    }

    /// <summary>The whole tree, for a hierarchical view; <see cref="Rows"/> is the flattened visible part.</summary>
    public CompareTreeNode? Tree
    {
        get
        {
            lock (_gate)
            {
                return _tree;
            }
        }
    }

    /// <summary>The header's summary line; null while calculating or without a result.</summary>
    public string? SummaryText
    {
        get
        {
            lock (_gate)
            {
                return _isCalculating ? null : _result?.SummaryText;
            }
        }
    }

    /// <summary>Defaults to Differences.</summary>
    public CompareFilter Filter
    {
        get
        {
            lock (_gate)
            {
                return _filter;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_filter == value)
                {
                    return;
                }

                _filter = value;
                RecomputeVisibleLocked();
            }

            Notify();
        }
    }

    /// <summary>"Compare Date": same-size files with different dates count as different. Re-diffs without re-reading.</summary>
    public bool CompareDates
    {
        get
        {
            lock (_gate)
            {
                return _compareDates;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_compareDates == value)
                {
                    return;
                }

                _compareDates = value;
                if (_leftEntries.Count > 0 || _rightEntries.Count > 0)
                {
                    SetResultLocked(FolderComparison.Compare(_leftEntries, _rightEntries, value, _ignoreCase));
                }
            }

            Notify();
        }
    }

    /// <summary>The chips with their counts; empty without a result.</summary>
    public IReadOnlyList<CompareFilterChip> FilterChips
    {
        get
        {
            lock (_gate)
            {
                if (_result is not { } result)
                {
                    return [];
                }

                var selected = _filter;
                return CompareFilterExtensions.All.Select(f => new CompareFilterChip(f, f.Title(), f.Count(result), f == selected)).ToList();
            }
        }
    }

    /// <summary>The visible rows, expanded groups opened, for a virtualizing list.</summary>
    public IReadOnlyList<CompareRow> Rows
    {
        get
        {
            lock (_gate)
            {
                return _rows ??= BuildRowsLocked();
            }
        }
    }

    /// <summary>Show <see cref="NothingMatchesText"/>: there is a result, but the filter hides everything.</summary>
    public bool NothingMatchesFilter
    {
        get
        {
            lock (_gate)
            {
                return _result is not null && _tree is { } tree && !tree.Children.Any(_visible.Contains);
            }
        }
    }

    // MARK: - Actions

    /// <summary>
    /// Snapshots the two endpoints and compares them (both read in parallel). Either one null
    /// or "This PC" shows the placeholder. Supersedes a comparison in progress. Never throws.
    /// </summary>
    public async Task CompareAsync(SyncEndpoint? left, SyncEndpoint? right, CancellationToken cancellationToken = default)
    {
        int generation;
        CancellationToken token;
        lock (_gate)
        {
            _calculation?.Cancel();
            generation = ++_generation;
            _left = left;
            _right = right;
            _errorMessage = null;
            if (left is not { IsUsable: true } || right is not { IsUsable: true })
            {
                _isCalculating = false;
                _leftEntries = [];
                _rightEntries = [];
                SetResultLocked(null);
                token = CancellationToken.None;
            }
            else
            {
                _calculation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                token = _calculation.Token;
                _isCalculating = true;
            }
        }

        Notify();
        if (left is not { IsUsable: true } || right is not { IsUsable: true })
        {
            return;
        }

        IReadOnlyList<SyncEntry>? leftEntries = null, rightEntries = null;
        string? error = null;
        try
        {
            var leftTask = Task.Run(() => SyncPlanner.EnumerateAsync(left, token), token);
            var rightTask = Task.Run(() => SyncPlanner.EnumerateAsync(right, token), token);
            await Task.WhenAll(leftTask, rightTask).ConfigureAwait(false);
            leftEntries = await leftTask.ConfigureAwait(false);
            rightEntries = await rightTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            error = L("Could not read folders: {0}", e.Message);
            SupportLogger.Shared.Log($"Compare failed: {e.Message}", "Compare", LogLevel.Error);
        }

        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            _isCalculating = false;
            if (leftEntries is not null && rightEntries is not null)
            {
                _leftEntries = leftEntries;
                _rightEntries = rightEntries;
                _ignoreCase = !left.IsCloud || !right.IsCloud;
                SetResultLocked(FolderComparison.Compare(leftEntries, rightEntries, _compareDates, _ignoreCase));
            }
            else if (error is not null)
            {
                _errorMessage = error;
                _leftEntries = [];
                _rightEntries = [];
                SetResultLocked(null);
            }
        }

        Notify();
    }

    /// <summary>"Compare Again" / "Try Again": re-reads the snapshotted endpoints.</summary>
    public Task CompareAgainAsync(CancellationToken cancellationToken = default)
    {
        SyncEndpoint? left, right;
        lock (_gate)
        {
            left = _left;
            right = _right;
        }

        return CompareAsync(left, right, cancellationToken);
    }

    /// <summary>Stops reading the folders; the previous result (if any) stays.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _calculation?.Cancel();
        }
    }

    public bool IsExpanded(CompareTreeNode node)
    {
        lock (_gate)
        {
            return _expanded.Contains(node.Path);
        }
    }

    /// <summary>Opens or closes a folder group. Expansion is kept by path, across filter changes and re-comparisons.</summary>
    public void ToggleExpanded(CompareTreeNode node)
    {
        lock (_gate)
        {
            if (!_expanded.Remove(node.Path))
            {
                _expanded.Add(node.Path);
            }

            _rows = null;
        }

        Notify();
    }

    public void SetExpanded(CompareTreeNode node, bool expanded)
    {
        lock (_gate)
        {
            if (!(expanded ? _expanded.Add(node.Path) : _expanded.Remove(node.Path)))
            {
                return;
            }

            _rows = null;
        }

        Notify();
    }

    // MARK: - Internals

    private void SetResultLocked(FolderComparisonResult? result)
    {
        _result = result;
        _tree = result is null ? null : CompareTreeBuilder.Build(result.Entries);
        RecomputeVisibleLocked();
    }

    /// <summary>
    /// Marks every node the filter keeps — a node matches itself or keeps a matching
    /// descendant, so folders holding matches stay visible. One bottom-up pass.
    /// </summary>
    private void RecomputeVisibleLocked()
    {
        var visible = new HashSet<CompareTreeNode>(ReferenceEqualityComparer.Instance);
        var filter = _filter;
        if (_tree is { } tree)
        {
            foreach (var child in tree.Children)
            {
                Mark(child);
            }
        }

        _visible = visible;
        _rows = null;

        bool Mark(CompareTreeNode node)
        {
            var keep = node.Entry is { } entry && filter.Matches(entry);
            foreach (var child in node.Children)
            {
                if (Mark(child))
                {
                    keep = true;
                }
            }

            if (keep)
            {
                visible.Add(node);
            }

            return keep;
        }
    }

    private List<CompareRow> BuildRowsLocked()
    {
        var rows = new List<CompareRow>();
        if (_tree is not { } tree || _result is not { } result)
        {
            return rows;
        }

        foreach (var child in tree.Children)
        {
            if (_visible.Contains(child))
            {
                Append(child, 0);
            }
        }

        return rows;

        void Append(CompareTreeNode node, int depth)
        {
            var visibleChildren = node.Children.Where(_visible.Contains).ToList();
            if (node.IsDirectory && visibleChildren.Count > 0)
            {
                var expanded = _expanded.Contains(node.Path);
                rows.Add(new CompareRow(node, depth, IsGroup: true, expanded, _filter.FolderSummary(node.DescendantCounts), result.ComparedDates));
                if (expanded)
                {
                    foreach (var child in visibleChildren)
                    {
                        Append(child, depth + 1);
                    }
                }
            }
            else
            {
                rows.Add(new CompareRow(node, depth, IsGroup: false, IsExpanded: false, null, result.ComparedDates));
            }
        }
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
