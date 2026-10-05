// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FileFluss.Core.Models;
using FileFluss.Core.Settings;

namespace FileFluss.Core.Search;

/// <summary>One searched source's progress in the search window: searching, done or failed, and how many hits.</summary>
public sealed class SearchSourceProgress : INotifyPropertyChanged
{
    private SearchSourceState _state = SearchSourceState.Searching;
    private int _resultCount;
    private string? _errorMessage;

    public SearchSourceProgress(SearchSourceInfo source) => Source = source;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SearchSourceInfo Source { get; }

    public string Key => Source.Key;

    public string Name => Source.Name;

    public SearchSourceKind Kind => Source.Kind;

    public SearchSourceState State { get => _state; internal set => Set(ref _state, value); }

    public int ResultCount { get => _resultCount; internal set => Set(ref _resultCount, value); }

    /// <summary>Why the source failed; its results so far (cached hits, say) stay in the list.</summary>
    public string? ErrorMessage { get => _errorMessage; internal set => Set(ref _errorMessage, value); }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>"Open in Left Panel" / "Open in Right Panel" / a double-click: where to go, and on which side.</summary>
public sealed class SearchOpenRequestedEventArgs(SearchResultItem item, PanelSide side) : EventArgs
{
    public SearchResultItem Item { get; } = item;

    public PanelSide Side { get; } = side;

    public SearchNavigationTarget Target { get; } = item.Navigation;
}

/// <summary>
/// The search window's view model (macOS <c>SearchViewModel</c>). Setting
/// <see cref="SearchText"/> cancels the running search and starts a new one after a short
/// pause; results stream into <see cref="Results"/> per source as each responds, with each
/// source's state in <see cref="Sources"/>.
///
/// <para>Create and use it on the UI thread: collections and properties change on the thread
/// that started the search, which the awaits inside return to.</para>
/// </summary>
public sealed class SearchSession : INotifyPropertyChanged, IDisposable
{
    /// <summary>Typing pause before a search starts, as on macOS.</summary>
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(200);

    private readonly SearchCoordinator _coordinator;
    private readonly Func<SearchContext> _context;
    private readonly DriveRegistry? _drives;
    private readonly AppSettings? _settings;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cancellation;
    private string _searchText = string.Empty;
    private bool _includeOffline;
    private SearchScope _scope = SearchScope.AllSources;
    private SearchStatus _status = SearchStatus.Idle;
    private int _sourcesCompleted;
    private int _sourcesTotal;
    private SearchSourceFilter _sourceFilter = SearchSourceFilter.All;

    /// <param name="context">Asked at the start of every search: the active panel's location and the accounts.</param>
    /// <param name="drives">Mounted drives are searched on disk, unplugged indexed ones from the index.</param>
    /// <param name="settings">Supplies and persists "Show offline results" (<see cref="AppSettings.SearchIncludeOffline"/>).</param>
    public SearchSession(SearchCoordinator coordinator, Func<SearchContext> context, DriveRegistry? drives = null, AppSettings? settings = null)
    {
        _coordinator = coordinator;
        _context = context;
        _drives = drives;
        _settings = settings;
        if (settings is not null)
        {
            settings.PropertyChanged += OnSettingsChanged;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A result is to be shown in a panel. The window stays open so more can be picked.</summary>
    public event EventHandler<SearchOpenRequestedEventArgs>? OpenRequested;

    /// <summary>The query. Changing it cancels the running search and starts a new one after <see cref="DebounceDelay"/>.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty))
            {
                Restart(debounce: true);
            }
        }
    }

    /// <summary>"Show offline results": also search unplugged indexed drives and disconnected accounts. Persisted in the settings.</summary>
    public bool IncludeOffline
    {
        get => _settings?.SearchIncludeOffline ?? _includeOffline;
        set
        {
            if (IncludeOffline == value)
            {
                return;
            }

            if (_settings is not null)
            {
                // The settings' change notification re-runs the search (OnSettingsChanged).
                _settings.SearchIncludeOffline = value;
                return;
            }

            _includeOffline = value;
            OnPropertyChanged();
            Restart(debounce: false);
        }
    }

    public SearchScope Scope
    {
        get => _scope;
        set
        {
            if (Set(ref _scope, value))
            {
                Restart(debounce: false);
            }
        }
    }

    /// <summary>Every result so far, de-duplicated, in arrival order.</summary>
    public ObservableCollection<SearchResultItem> Results { get; } = [];

    /// <summary>The sources of the current search and how each is getting on.</summary>
    public ObservableCollection<SearchSourceProgress> Sources { get; } = [];

    public SearchStatus Status { get => _status; private set => Set(ref _status, value); }

    public int SourcesCompleted { get => _sourcesCompleted; private set => Set(ref _sourcesCompleted, value); }

    public int SourcesTotal { get => _sourcesTotal; private set => Set(ref _sourcesTotal, value); }

    public int ResultCount => Results.Count;

    /// <summary>The header's status: "2/5" while searching, "37 results" when done, empty when idle.</summary>
    public string StatusText => Status switch
    {
        SearchStatus.Searching => SourcesTotal > 0 ? $"{SourcesCompleted}/{SourcesTotal}" : string.Empty,
        SearchStatus.Complete => Localization.L("{0} results", ResultCount),
        _ => string.Empty,
    };

    /// <summary>The source chip that is selected; <see cref="SearchSourceFilter.All"/> by default.</summary>
    public SearchSourceFilter SourceFilter
    {
        get => _sourceFilter;
        set
        {
            if (Set(ref _sourceFilter, value))
            {
                OnPropertyChanged(nameof(FilteredResults));
                OnPropertyChanged(nameof(GroupedResults));
            }
        }
    }

    public IReadOnlyList<SearchResultItem> FilteredResults
    {
        get
        {
            lock (_gate)
            {
                return Results.Where(_sourceFilter.Matches).ToList();
            }
        }
    }

    /// <summary>
    /// The filtered results in sections: local files first, then one section per account and
    /// one per offline source, each sorted by name.
    /// </summary>
    public IReadOnlyList<SearchResultGroup> GroupedResults
    {
        get
        {
            var filtered = FilteredResults;
            var groups = new List<SearchResultGroup>();
            var local = filtered.Where(r => r.Kind == SearchResultKind.Local).ToList();
            if (local.Count > 0)
            {
                groups.Add(new SearchResultGroup("local", Localization.L("Local Files"), SearchGroupKind.Local, null, local));
            }

            foreach (var kind in new[] { SearchResultKind.Cloud, SearchResultKind.Offline })
            {
                groups.AddRange(filtered
                    .Where(r => r.Kind == kind)
                    .GroupBy(r => r.GroupKey)
                    .Select(g => new SearchResultGroup(
                        g.Key,
                        g.First().GroupTitle,
                        kind == SearchResultKind.Cloud ? SearchGroupKind.Cloud : SearchGroupKind.Offline,
                        g.First().ProviderType,
                        g.ToList()))
                    .OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase));
            }

            return groups;
        }
    }

    /// <summary>The filter chips for the current results: All, Local (when there are local hits), each account, each offline source.</summary>
    public IReadOnlyList<SearchFilterOption> AvailableFilters
    {
        get
        {
            List<SearchResultItem> results;
            lock (_gate)
            {
                results = Results.ToList();
            }

            var options = new List<SearchFilterOption> { new(Localization.L("All"), SearchSourceFilter.All) };
            if (results.Any(r => r.Kind == SearchResultKind.Local))
            {
                options.Add(new SearchFilterOption(Localization.L("Local"), SearchSourceFilter.Local));
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in results.Where(r => r.Kind != SearchResultKind.Local))
            {
                if (!seen.Add(result.GroupKey))
                {
                    continue;
                }

                options.Add(result.Kind == SearchResultKind.Cloud
                    ? new SearchFilterOption(result.SourceName, SearchSourceFilter.Cloud(result.AccountId!.Value), result.ProviderType)
                    : new SearchFilterOption(result.GroupTitle, SearchSourceFilter.Offline(result.SourceId!)));
            }

            return options;
        }
    }

    /// <summary>The running search, or a completed task when idle — await it to know results are all in.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>Runs the current query again now (Enter in the search field).</summary>
    public void Refresh() => Restart(debounce: false);

    /// <summary>Stops the running search; results so far stay.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _cancellation?.Cancel();
            _cancellation = null;
            foreach (var source in Sources.Where(s => s.State == SearchSourceState.Searching))
            {
                source.State = SearchSourceState.Done;
            }
        }

        if (Status == SearchStatus.Searching)
        {
            Status = SearchStatus.Complete;
            NotifyResultsChanged();
        }
    }

    /// <summary>Clears the query, the results and the filter.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _cancellation?.Cancel();
            _cancellation = null;
            ResetResults();
        }

        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        _sourceFilter = SearchSourceFilter.All;
        OnPropertyChanged(nameof(SourceFilter));
        Status = SearchStatus.Idle;
        SourcesCompleted = 0;
        SourcesTotal = 0;
        NotifyResultsChanged();
    }

    /// <summary>Asks the app to show <paramref name="item"/> in the panel on <paramref name="side"/> (raises <see cref="OpenRequested"/>).</summary>
    public void OpenInPanel(SearchResultItem item, PanelSide side)
        => OpenRequested?.Invoke(this, new SearchOpenRequestedEventArgs(item, side));

    public void Dispose()
    {
        if (_settings is not null)
        {
            _settings.PropertyChanged -= OnSettingsChanged;
        }

        lock (_gate)
        {
            _cancellation?.Cancel();
            _cancellation = null;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.SearchIncludeOffline))
        {
            OnPropertyChanged(nameof(IncludeOffline));
            Restart(debounce: false);
        }
    }

    private void Restart(bool debounce)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _cancellation?.Cancel();
            _cancellation = null;
            if (string.IsNullOrWhiteSpace(_searchText))
            {
                ResetResults();
                cancellation = null;
            }
            else
            {
                cancellation = new CancellationTokenSource();
                _cancellation = cancellation;
            }
        }

        if (cancellation is null)
        {
            Status = SearchStatus.Idle;
            SourcesCompleted = 0;
            SourcesTotal = 0;
            NotifyResultsChanged();
            Completion = Task.CompletedTask;
            return;
        }

        Status = SearchStatus.Searching;
        Completion = RunAsync(_searchText, debounce, cancellation.Token);
    }

    // Deliberately without ConfigureAwait(false): every await returns to the thread the search
    // was started on, so the collections the UI binds to only ever change there.
    private async Task RunAsync(string query, bool debounce, CancellationToken cancellationToken)
    {
        try
        {
            if (debounce)
            {
                await Task.Delay(DebounceDelay, cancellationToken);
            }

            var context = _context();
            var request = SearchRequest.Build(query, context, _drives?.Drives ?? [], IncludeOffline, Scope);
            var run = _coordinator.Start(request, cancellationToken);
            lock (_gate)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                ResetResults();
                foreach (var source in run.Sources)
                {
                    Sources.Add(new SearchSourceProgress(source));
                }
            }

            SourcesCompleted = 0;
            SourcesTotal = run.Sources.Count;
            NotifyResultsChanged();

            await foreach (var batch in run.Batches)
            {
                if (!Apply(batch, cancellationToken))
                {
                    return;
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                Status = SearchStatus.Complete;
                OnPropertyChanged(nameof(StatusText));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Superseded by a newer query, cleared or cancelled.
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Sources report their own failures; this is the search itself failing to start.
            Diagnostics.SupportLogger.Shared.Log($"Search failed: {e.Message}", "Search", Diagnostics.LogLevel.Error);
            if (!cancellationToken.IsCancellationRequested)
            {
                Status = SearchStatus.Complete;
            }
        }
    }

    /// <returns>False once the search has been superseded.</returns>
    private bool Apply(SearchResultBatch batch, CancellationToken cancellationToken)
    {
        var completed = false;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var source = Sources.FirstOrDefault(s => s.Key == batch.SourceKey);
            if (batch.IsComplete)
            {
                if (source is not null)
                {
                    source.ErrorMessage = batch.ErrorMessage;
                    source.State = batch.ErrorMessage is null ? SearchSourceState.Done : SearchSourceState.Failed;
                }

                completed = true;
            }
            else
            {
                // Kept across batches: re-deriving every id already on screen per batch made
                // the list stall exactly while it was filling.
                foreach (var item in batch.Items)
                {
                    if (_seenIds.Add(item.Id))
                    {
                        Results.Add(item);
                        if (source is not null)
                        {
                            source.ResultCount++;
                        }
                    }
                }
            }
        }

        if (completed)
        {
            SourcesCompleted++;
        }

        NotifyResultsChanged();
        return true;
    }

    private void ResetResults()
    {
        Results.Clear();
        Sources.Clear();
        _seenIds.Clear();
    }

    private void NotifyResultsChanged()
    {
        OnPropertyChanged(nameof(ResultCount));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(FilteredResults));
        OnPropertyChanged(nameof(GroupedResults));
        OnPropertyChanged(nameof(AvailableFilters));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        if (name is nameof(Status) or nameof(SourcesCompleted) or nameof(SourcesTotal))
        {
            OnPropertyChanged(nameof(StatusText));
        }

        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
