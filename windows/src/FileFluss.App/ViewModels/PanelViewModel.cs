// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using System.Windows;
using System.Windows.Threading;
using FileFluss.App.Mvvm;
using FileFluss.App.Services;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Formatting;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Storage;
using FileFluss.Native;
using static FileFluss.Core.Localization;

namespace FileFluss.App.ViewModels;

/// <summary>One crumb of the path bar.</summary>
public sealed record BreadcrumbSegment(string Title, PanelLocation Location, bool IsLast)
{
    /// <summary>What screen readers announce for the crumb.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// One file panel — the merged macOS <c>FileManagerViewModel</c> (local) and
/// <c>CloudFileManagerViewModel</c> (cloud): on Windows both sides of a panel are a
/// <see cref="FileProvider"/>, so navigation, history, sorting, filtering and the status line
/// exist once. Same account in both panels? Each panel keeps its own location and history.
/// </summary>
public sealed class PanelViewModel : Observable
{
    private readonly AppServices _services;
    private readonly List<PanelLocation> _history = [];
    private readonly DispatcherTimer _watchDebounce;
    /// <summary>Lists this long are sorted on the thread pool; shorter ones at once, so small folders never flicker.</summary>
    private const int SortOffThreadThreshold = 2_000;

    /// <summary>While a folder keeps changing (a big copy into it), it is still re-listed at least this often.</summary>
    private const long MaxWatchDelayMs = 2_000;

    private int _historyIndex = -1;
    private List<FileRowViewModel> _allRows = [];

    // _allRows in ascending order of _sortedField, folders first; the direction, the hidden-files
    // switch and the quick filter are applied on top without sorting again.
    private List<FileRowViewModel> _sortedRows = [];
    private SortField? _sortedField;
    private int _sortedFolderCount;
    private int _viewGeneration;
    private int _listingFiles;
    private int _listingFolders;
    private long _listingBytes;
    private int _watchSignalled;
    private long _watchBurstStart;
    private int _watchGeneration;
    private IReadOnlyList<FileRowViewModel> _rows = [];
    private IReadOnlyList<FileRowViewModel> _selection = [];
    private PanelLocation _location = PanelLocation.Local(string.Empty);
    private FileProvider? _provider;
    private Type? _lastProviderType;
    private bool _isLoading;
    private string? _error;
    private bool _needsReauthentication;
    private SortField _sortField = SortField.Name;
    private bool _sortAscending = true;
    private string _filterText = string.Empty;
    private bool _isFilterVisible;
    private bool _showHiddenFiles;
    private string _quotaText = string.Empty;
    private long? _selectionSize;
    private bool _isCalculatingSelectionSize;
    private CancellationTokenSource? _selectionSizeCancellation;
    private string? _walkKey;
    private long? _walkResult;
    private PanelLocation? _listedLocation;
    private int _loadGeneration;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _loadCancellation;

    public PanelViewModel(PanelSide side, AppServices services)
    {
        Side = side;
        _services = services;
        _showHiddenFiles = services.Settings.ShowHiddenFiles;
        _watchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _watchDebounce.Tick += async (_, _) =>
        {
            _watchDebounce.Stop();
            await RefreshAsync(quiet: true);
        };
        services.Accounts.AccountChanged += async (_, id) =>
        {
            if (_location.AccountId == id)
            {
                // Going offline or online swaps the live provider for the index view and back.
                var provider = _services.ProviderFor(_location);
                if (provider?.GetType() != _provider?.GetType())
                {
                    _provider = provider;
                    OnOfflineStateChanged();
                    await LoadAsync([], quiet: false);
                }

                OnPropertyChanged(nameof(Account));
                OnPropertyChanged(nameof(Breadcrumbs));
                OnPropertyChanged(nameof(Title));
            }
        };
    }

    public PanelSide Side { get; }

    // MARK: - Where the panel is

    public PanelLocation Location => _location;

    public FileProvider? Provider => _provider;

    public bool IsCloud => _location.IsCloud;

    public bool IsLocal => _location.IsLocal;

    /// <summary>Showing an offline account or an unplugged drive from the search index.</summary>
    public bool IsOffline => _provider is Core.Search.OfflineIndexProvider;

    /// <summary>The offline view's banner: "Offline view — Dropbox · Read only · Indexed 3 days ago".</summary>
    public string OfflineBanner => _provider is Core.Search.OfflineIndexProvider offline && _services.Search.Safe(offline.GetInfo) is { } info
        ? $"{info.Title}  ·  {Core.Search.OfflineSourceInfo.ReadOnlyBadge}  ·  {info.LastIndexedText}"
        : string.Empty;

    /// <summary>What an empty list says: an offline source with nothing indexed explains how to fix that.</summary>
    public string EmptyText => IsOffline && _provider!.IsRoot(CurrentPath)
        ? Core.Search.OfflineSourceInfo.EmptyTitle + Environment.NewLine + Core.Search.OfflineSourceInfo.EmptyMessage
        : L("Empty Folder");

    /// <summary>An unplugged drive's name, as the drive registry remembers it.</summary>
    private string OfflineDriveName => _location.DriveId is { } id && _services.Search.Drives.Find(id) is { } drive ? drive.DisplayName : L("Offline Source");

    private void OnOfflineStateChanged()
    {
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(OfflineBanner));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanCreateFolders));
    }

    public CloudAccount? Account => _location.AccountId is { } id ? _services.Accounts.Find(id) : null;

    public string CurrentPath => _location.Path;

    /// <summary>Read-only: a camera, a source that is offline.</summary>
    public bool IsReadOnly => IsOffline || _location.IsOfflineDrive || (Account is { } account && (account.ProviderType.IsReadOnly() || account.IsEffectivelyOffline));

    public bool CanCreateFolders => IsLocal ? CurrentPath.Length > 0 : Account?.ProviderType.CanCreateFolders() == true && !IsReadOnly;

    /// <summary>The current folder's name, for window titles and prompts.</summary>
    public string Title => _provider is null ? string.Empty
        : IsCloud && _provider.IsRoot(CurrentPath) ? Account?.DisplayName ?? string.Empty
        : _location.IsOfflineDrive && _provider.IsRoot(CurrentPath) ? OfflineDriveName
        : _provider.GetName(CurrentPath);

    public IReadOnlyList<BreadcrumbSegment> Breadcrumbs => BuildBreadcrumbs();

    // MARK: - Contents

    public IReadOnlyList<FileRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            _rows = value;

            // Counted once per list, not on every read of the status line (each selection change reads it).
            int files = 0, folders = 0;
            long bytes = 0;
            foreach (var row in value)
            {
                if (row.IsDirectory)
                {
                    folders++;
                }
                else
                {
                    files++;
                    bytes += row.Item.Size;
                }
            }

            (_listingFiles, _listingFolders, _listingBytes) = (files, folders, bytes);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ListingText));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public IReadOnlyList<FileRowViewModel> AllRows => _allRows;

    public bool IsEmpty => !_isLoading && _error is null && _rows.Count == 0;

    /// <summary>Set by the view whenever the list's selection changes.</summary>
    public IReadOnlyList<FileRowViewModel> Selection
    {
        get => _selection;
        set
        {
            _selection = value;
            OnPropertyChanged();
            RecalculateSelectionSize();
        }
    }

    public IReadOnlyList<CloudFileItem> SelectedItems => _selection.Select(r => r.Item).ToList();

    public bool HasSelection => _selection.Count > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>The account's credentials stopped working — the panel offers "Sign In Again".</summary>
    public bool NeedsReauthentication
    {
        get => _needsReauthentication;
        private set => Set(ref _needsReauthentication, value);
    }

    // MARK: - View options

    public SortField SortField
    {
        get => _sortField;
        set
        {
            if (Set(ref _sortField, value))
            {
                ApplyView();
            }
        }
    }

    public bool SortAscending
    {
        get => _sortAscending;
        set
        {
            if (Set(ref _sortAscending, value))
            {
                ApplyView();
            }
        }
    }

    /// <summary>Clicking the sorted column again reverses it, as in Explorer.</summary>
    public void SortBy(SortField field)
    {
        if (_sortField == field)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            _sortField = field;
            _sortAscending = field is not (SortField.Date or SortField.DateCreated or SortField.Size);
            OnPropertyChanged(nameof(SortField));
            OnPropertyChanged(nameof(SortAscending));
            ApplyView();
        }
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (Set(ref _filterText, value))
            {
                ApplyView();
            }
        }
    }

    public bool IsFilterVisible
    {
        get => _isFilterVisible;
        set
        {
            if (Set(ref _isFilterVisible, value) && !value)
            {
                FilterText = string.Empty;
            }
        }
    }

    public bool ShowHiddenFiles
    {
        get => _showHiddenFiles;
        set
        {
            if (Set(ref _showHiddenFiles, value))
            {
                ApplyView();
            }
        }
    }

    // MARK: - Status bar

    /// <summary>
    /// The whole status line, as on macOS: "3 files, 2 folders — 1.2 MB · Selected: 2 items,
    /// 800 KB · 120 GB free of 500 GB" (a cloud panel ends with the account's quota).
    /// </summary>
    public string StatusText => StatusBarText.Join(ListingText, SelectionText, QuotaText);

    /// <summary>"3 files, 2 folders — 1.2 MB" for what the list shows.</summary>
    public string ListingText => StatusBarText.Listing(_listingFiles, _listingFolders, _listingBytes);

    /// <summary>"Selected: 2 items, 800 KB", "… calculating…" while folder sizes are summed, or empty.</summary>
    public string SelectionText => StatusBarText.Selection(_selection.Count, _selectionSize, _isCalculatingSelectionSize);

    /// <summary>"Dropbox: 12.3 GB of 2 TB used (1%)" for cloud accounts; "120 GB free of 500 GB" for local volumes.</summary>
    public string QuotaText
    {
        get => _quotaText;
        private set
        {
            if (Set(ref _quotaText, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>
    /// Sums the selection for the status bar (macOS <c>recalculateSelectionSize</c>): file
    /// sizes at once, folders walked in the background. A different selection cancels the
    /// walk; folders whose size is already known ("Calculate Folder Size") aren't walked.
    /// The same folders selected again — a live refresh re-selects the rows it rebuilt — keep
    /// the walk under way or its result instead of starting over.
    /// </summary>
    private void RecalculateSelectionSize()
    {
        long known = 0;
        var folders = new List<string>();
        foreach (var row in _selection)
        {
            if (!row.IsDirectory || row.IsDrive)
            {
                known += row.Item.Size;
            }
            else if (row.FolderSize is { } size)
            {
                known += size;
            }
            else if (row.Item.Role != CloudItemRole.VirtualContainer)
            {
                // Virtual containers ("Shared with me", camera roots) would walk whole drives.
                folders.Add(row.Path);
            }
        }

        var provider = _provider;
        var key = folders.Count == 0 || provider is null
            ? null
            : $"{_location.Kind}|{_location.AccountId}|{_location.DriveId}|" + string.Join('\n', folders.Order(StringComparer.Ordinal));
        if (key != _walkKey)
        {
            _selectionSizeCancellation?.Cancel();
            _selectionSizeCancellation = null;
            _walkKey = key;
            _walkResult = null;
            if (key is not null)
            {
                WalkSelectedFolders(provider!, folders, key);
            }
        }

        _isCalculatingSelectionSize = key is not null && _walkResult is null;
        _selectionSize = _selection.Count == 0 ? null : known + (_walkResult ?? 0);
        OnSelectionSizeChanged();
    }

    private async void WalkSelectedFolders(FileProvider provider, List<string> folders, string key)
    {
        var cancellation = new CancellationTokenSource();
        _selectionSizeCancellation = cancellation;
        var ct = cancellation.Token;
        try
        {
            // Holding Shift+Down over folders shouldn't start a walk per step.
            await Task.Delay(200, ct);
            var total = await Task.Run(async () =>
            {
                long running = 0;
                foreach (var folder in folders)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        running += await provider.FolderSizeAsync(folder, ct).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // Skip folders that fail, as macOS does.
                    }
                }

                return running;
            }, ct);

            if (!ct.IsCancellationRequested && _walkKey == key)
            {
                _walkResult = total;
                RecalculateSelectionSize();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            SupportLogger.Shared.Log($"Selection size failed: {e.Message}", "Panel", LogLevel.Error);
            if (_walkKey == key)
            {
                _walkResult = 0;
                RecalculateSelectionSize();
            }
        }
    }

    private void OnSelectionSizeChanged()
    {
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(StatusText));
    }

    // MARK: - History

    public bool CanGoBack => _historyIndex > 0;

    public bool CanGoForward => _historyIndex >= 0 && _historyIndex < _history.Count - 1;

    public bool CanGoUp => _provider is not null && !_provider.IsRoot(CurrentPath);

    // MARK: - Navigation

    /// <summary>Raised after a load so the view can restore focus/selection (names to select).</summary>
    public event EventHandler<IReadOnlyCollection<string>>? Loaded;

    /// <summary>Raised when the location changes (sidebar highlight, window title).</summary>
    public event EventHandler? LocationChanged;

    public Task NavigateAsync(PanelLocation location, string? selectName = null)
        => NavigateCoreAsync(location, addToHistory: true, selectName is null ? [] : [selectName]);

    private async Task NavigateCoreAsync(PanelLocation location, bool addToHistory, IReadOnlyCollection<string> select)
    {
        var provider = _services.ProviderFor(location);
        var changed = location != _location;
        _location = location;
        _provider = provider;
        if (changed)
        {
            if (addToHistory)
            {
                if (_historyIndex < _history.Count - 1)
                {
                    _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                }

                _history.Add(location);
                _historyIndex = _history.Count - 1;
            }

            // Flip to the destination immediately: path bar, rows and selection reset
            // before the (possibly slow) listing, so the panel never shows one folder's
            // contents under another folder's path.
            ClearRows();
            Selection = [];
            FilterText = string.Empty;
            NeedsReauthentication = false;
            Watch();
            OnPropertyChanged(nameof(Location));
            OnPropertyChanged(nameof(Provider));
            OnPropertyChanged(nameof(Account));
            OnPropertyChanged(nameof(IsCloud));
            OnPropertyChanged(nameof(IsLocal));
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(CanCreateFolders));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Breadcrumbs));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
            OnPropertyChanged(nameof(CanGoUp));
            LocationChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (provider?.GetType() != _lastProviderType)
        {
            OnOfflineStateChanged();
        }

        _lastProviderType = provider?.GetType();
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(OfflineBanner));
        OnPropertyChanged(nameof(EmptyText));

        _ = UpdateQuotaAsync();
        await LoadAsync(select, quiet: false);
    }

    public async Task RefreshAsync(bool quiet = false)
        => await LoadAsync(_selection.Select(r => r.Name).ToList(), quiet);

    public Task GoUpAsync()
    {
        if (_provider is null || _provider.IsRoot(CurrentPath))
        {
            return Task.CompletedTask;
        }

        var child = _provider.GetName(CurrentPath);
        var parent = _provider.GetParentPath(CurrentPath);
        var location = _location with { Path = parent };
        // Coming back up selects the folder we came from, like Explorer.
        return NavigateCoreAsync(location, addToHistory: true, [IsLocal && parent.Length == 0 ? CurrentPath : child]);
    }

    public Task GoBackAsync()
    {
        if (!CanGoBack)
        {
            return Task.CompletedTask;
        }

        var from = _location;
        _historyIndex--;
        return NavigateCoreAsync(_history[_historyIndex], addToHistory: false, SelectionHint(from));
    }

    public Task GoForwardAsync()
    {
        if (!CanGoForward)
        {
            return Task.CompletedTask;
        }

        _historyIndex++;
        return NavigateCoreAsync(_history[_historyIndex], addToHistory: false, []);
    }

    /// <summary>Going back into a parent selects the child folder we came out of.</summary>
    private IReadOnlyCollection<string> SelectionHint(PanelLocation from)
    {
        if (_provider is null || from.AccountId != _history[_historyIndex].AccountId)
        {
            return [];
        }

        return _provider.GetParentPath(from.Path) == _history[_historyIndex].Path ? [_provider.GetName(from.Path)] : [];
    }

    public Task JumpToRootAsync()
    {
        if (!IsLocal)
        {
            return NavigateAsync(_location with { Path = IsCloud ? Account?.EffectiveRootPath ?? "/" : "/" });
        }

        var root = Path.GetPathRoot(CurrentPath);
        return NavigateAsync(PanelLocation.Local(string.IsNullOrEmpty(root) ? string.Empty : root));
    }

    // MARK: - Loading

    private async Task LoadAsync(IReadOnlyCollection<string> select, bool quiet)
    {
        var generation = ++_loadGeneration;
        _loadCancellation?.Cancel();
        _loadCancellation = new CancellationTokenSource();
        var ct = _loadCancellation.Token;
        var location = _location;
        var provider = _provider;

        if (provider is null)
        {
            Error = IsCloud ? L("Cloud account not connected") : null;
            NeedsReauthentication = IsCloud && Account is { IsConnected: false } && !(Account?.IsOfflineMode ?? false);
            ClearRows();
            return;
        }

        if (!quiet)
        {
            IsLoading = true;
        }

        try
        {
            // Started on the thread pool: a provider's synchronous first steps (reading its
            // credentials, a token refresh, an SSH handshake) must not run on the UI thread.
            var items = await Task.Run(() => provider.ListDirectoryAsync(location.Path, ct), ct);
            if (generation != _loadGeneration)
            {
                return;
            }

            // Rows are made and sorted on the thread pool: a 30,000-item folder costs the UI
            // thread only the swap. Unchanged items keep their row (icon, thumbnail, folder size).
            var hide = _services.Settings.HideFileExtensions;
            var local = provider.IsLocal;
            var previous = _allRows;
            var field = _sortField;
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            var (rows, sorted, folderCount) = await Task.Run(() =>
            {
                var built = BuildRows(items, previous, local, hide);
                var (ordered, folders) = SortRows(built, field, culture);
                return (built, ordered, folders);
            }, ct);
            if (generation != _loadGeneration)
            {
                return;
            }

            _viewGeneration++;
            _allRows = rows;
            (_sortedRows, _sortedField, _sortedFolderCount) = (sorted, field, folderCount);
            Error = null;
            NeedsReauthentication = false;
            ApplyView();
            ItemsListed?.Invoke(this, items);
            Loaded?.Invoke(this, select);

            // An upload, delete or new folder changed what this folder holds, and the free
            // space or quota with it: macOS re-probes when the item count changes.
            if (_listedLocation == location && previous.Count != _allRows.Count)
            {
                if (location.AccountId is { } changed)
                {
                    _services.Accounts.InvalidateQuota(changed);
                }

                _ = UpdateQuotaAsync();
            }

            _listedLocation = location;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            SupportLogger.Shared.Log($"Listing {location.Path} failed: {e.Message}", "Panel", LogLevel.Error);
            ClearRows();
            Error = e.Message;
            if (e is CloudProviderException { NeedsReauthentication: true } && location.AccountId is { } id)
            {
                NeedsReauthentication = true;
                _services.Accounts.MarkNeedsReauthentication(id);
            }
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Every successful listing, so the search index can learn from browsing.</summary>
    public event EventHandler<IReadOnlyList<CloudFileItem>>? ItemsListed;

    /// <summary>A folder's size became known ("Calculate Folder Size"): a list sorted by size moves it into place.</summary>
    public void OnFolderSizeKnown()
    {
        if (_sortField == SortField.Size)
        {
            _sortedField = null;
            ApplyView();
        }
    }

    private void ClearRows()
    {
        _viewGeneration++;
        _allRows = [];
        (_sortedRows, _sortedField, _sortedFolderCount) = ([], _sortField, 0);
        Rows = [];
    }

    /// <summary>
    /// Shows <see cref="_allRows"/> as the view options say. Only a different sort column
    /// sorts again (off the UI thread for big folders); the direction, hidden files and the
    /// quick filter work on the sorted order in one pass.
    /// </summary>
    private void ApplyView()
    {
        if (_sortedField == _sortField)
        {
            Rows = VisibleRows();
            return;
        }

        var generation = ++_viewGeneration;
        var rows = _allRows;
        var field = _sortField;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        if (rows.Count < SortOffThreadThreshold)
        {
            Apply(SortRows(rows, field, culture));
            return;
        }

        _ = SortInBackgroundAsync();

        async Task SortInBackgroundAsync()
        {
            try
            {
                var sorted = await Task.Run(() => SortRows(rows, field, culture));
                if (generation == _viewGeneration && ReferenceEquals(rows, _allRows))
                {
                    Apply(sorted);
                }
            }
            catch (Exception e)
            {
                SupportLogger.Shared.Log($"Sorting failed: {e.Message}", "Panel", LogLevel.Error);
            }
        }

        void Apply((List<FileRowViewModel> Rows, int FolderCount) sorted)
        {
            (_sortedRows, _sortedField, _sortedFolderCount) = (sorted.Rows, field, sorted.FolderCount);
            Rows = VisibleRows();
        }
    }

    /// <summary>The sorted rows in the chosen direction (folders stay first), without hidden or filtered-out ones.</summary>
    private List<FileRowViewModel> VisibleRows()
    {
        var hideHidden = !_showHiddenFiles;
        var isCloud = IsCloud;
        var needle = string.IsNullOrWhiteSpace(_filterText) ? null : _filterText.Trim();
        var sorted = _sortedRows;
        var visible = new List<FileRowViewModel>(sorted.Count);

        void Add(FileRowViewModel row)
        {
            if (hideHidden && (row.Item.IsHidden || (isCloud && CloudFileItem.IsDotHidden(row.Name))))
            {
                return;
            }

            if (needle is not null && !row.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
            {
                return;
            }

            visible.Add(row);
        }

        if (_sortAscending)
        {
            foreach (var row in sorted)
            {
                Add(row);
            }
        }
        else
        {
            // Descending reverses folders and files separately: folders first, as in Explorer.
            for (var i = _sortedFolderCount - 1; i >= 0; i--)
            {
                Add(sorted[i]);
            }

            for (var i = sorted.Count - 1; i >= _sortedFolderCount; i--)
            {
                Add(sorted[i]);
            }
        }

        return visible;
    }

    /// <summary>The listing as rows, keeping the row of every item that did not change (thread-safe: no UI state is touched).</summary>
    internal static List<FileRowViewModel> BuildRows(IReadOnlyList<CloudFileItem> items, IReadOnlyList<FileRowViewModel> previous, bool isLocal, bool hideExtension)
    {
        var byName = new Dictionary<string, FileRowViewModel>(previous.Count, StringComparer.Ordinal);
        foreach (var row in previous)
        {
            byName.TryAdd(row.Name, row);
        }

        var rows = new List<FileRowViewModel>(items.Count);
        foreach (var item in items)
        {
            if (byName.TryGetValue(item.Name, out var old) && old.CanReuseFor(item, isLocal, hideExtension))
            {
                rows.Add(old);
            }
            else
            {
                rows.Add(new FileRowViewModel(item, isLocal, hideExtension) { FolderSize = old?.FolderSize });
            }
        }

        return rows;
    }

    /// <summary>
    /// <paramref name="rows"/> in ascending order of <paramref name="field"/>, folders first
    /// (Explorer's rule), ties broken by name; and how many folders lead. Thread-safe.
    /// </summary>
    internal static (List<FileRowViewModel> Rows, int FolderCount) SortRows(IReadOnlyList<FileRowViewModel> rows, SortField field, System.Globalization.CultureInfo culture)
    {
        var names = NaturalStringComparer.Instance;
        var kinds = culture.CompareInfo;
        Comparison<FileRowViewModel> byField = field switch
        {
            SortField.Date => (a, b) => a.Item.ModificationDate.CompareTo(b.Item.ModificationDate),
            SortField.DateCreated => (a, b) => a.Item.CreationDate.CompareTo(b.Item.CreationDate),
            SortField.Size => (a, b) => a.SortSize.CompareTo(b.SortSize),
            SortField.Kind => (a, b) => kinds.Compare(a.KindText, b.KindText, System.Globalization.CompareOptions.IgnoreCase),
            _ => (a, b) => names.Compare(a.Name, b.Name),
        };

        var list = rows.ToList();
        list.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }

            var result = byField(a, b);
            return result == 0 && field != SortField.Name ? names.Compare(a.Name, b.Name) : result;
        });
        return (list, list.Count(r => r.IsDirectory));
    }

    /// <summary>
    /// The storage part of the status bar: the account's quota (macOS <c>CloudQuotaFormatter</c>)
    /// or the local volume's "X free of Y" (<c>LocalVolumeFormatter</c>).
    /// </summary>
    private async Task UpdateQuotaAsync()
    {
        var location = _location;
        string text = string.Empty;
        try
        {
            if (IsOffline || location.IsOfflineDrive)
            {
                // Nothing to ask: the source is not reachable, and that is the point.
            }
            else if (location.AccountId is { } id)
            {
                var quota = await _services.Accounts.QuotaAsync(id);
                if (quota is not null)
                {
                    text = CloudQuotaFormatter.Summary(quota, Account?.DisplayName ?? string.Empty);
                }
            }
            else if (location.IsLocal && location.Path.Length > 0)
            {
                var capacity = await Task.Run(() => VolumeCapacity(location.Path));
                if (capacity is { } volume)
                {
                    text = LocalVolumeFormatter.Summary(volume.Free, volume.Total);
                }
            }
        }
        catch (Exception e)
        {
            SupportLogger.Shared.Log($"Storage summary failed: {e.Message}", "Panel");
        }

        if (location == _location)
        {
            QuotaText = text;
        }
    }

    /// <summary>Free and total bytes of the volume holding <paramref name="path"/>, or null (UNC shares, unready drives).</summary>
    private static (long Free, long Total)? VolumeCapacity(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady && drive.TotalSize > 0 ? (drive.AvailableFreeSpace, drive.TotalSize) : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // MARK: - Live updates of local folders

    /// <summary>
    /// Watches the local folder on screen. The watcher is set up on the thread pool — on a
    /// network share or a sleeping disk, opening the folder can take seconds.
    /// </summary>
    private void Watch()
    {
        var generation = ++_watchGeneration;
        _watcher?.Dispose();
        _watcher = null;
        if (!IsLocal || CurrentPath.Length == 0)
        {
            return;
        }

        var path = CurrentPath;
        _ = WatchAsync();

        async Task WatchAsync()
        {
            var watcher = await Task.Run(() => CreateWatcher(path));
            if (generation == _watchGeneration)
            {
                _watcher = watcher;
            }
            else
            {
                watcher?.Dispose();
            }
        }
    }

    private FileSystemWatcher? CreateWatcher(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
            };
            FileSystemEventHandler changed = (_, _) => OnWatcherEvent();
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Changed += changed;
            watcher.Renamed += (_, _) => OnWatcherEvent();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A copy into the folder raises thousands of events: only one hop to the UI thread is in
    /// flight at a time, however many arrive meanwhile.
    /// </summary>
    private void OnWatcherEvent()
    {
        if (Interlocked.Exchange(ref _watchSignalled, 1) == 0)
        {
            Application.Current?.Dispatcher.BeginInvoke(OnFolderChanged, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Re-lists once the folder has been quiet for a moment — and, while it keeps changing,
    /// at least every <see cref="MaxWatchDelayMs"/>, so a long copy shows its progress.
    /// </summary>
    private void OnFolderChanged()
    {
        Volatile.Write(ref _watchSignalled, 0);
        var now = Environment.TickCount64;
        if (!_watchDebounce.IsEnabled)
        {
            _watchBurstStart = now;
        }

        _watchDebounce.Stop();
        if (now - _watchBurstStart >= MaxWatchDelayMs)
        {
            _watchBurstStart = now;
            _ = RefreshAsync(quiet: true);
            return;
        }

        _watchDebounce.Start();
    }

    // MARK: - Breadcrumbs

    private List<BreadcrumbSegment> BuildBreadcrumbs()
    {
        var segments = new List<(string Title, PanelLocation Location)>();
        if (_location.IsOfflineDrive)
        {
            var root = _location with { Path = "/" };
            segments.Add((OfflineDriveName, root));
            var accumulated = "/";
            foreach (var part in Core.IO.CloudPath.Components(CurrentPath))
            {
                accumulated = Core.IO.CloudPath.Combine(accumulated, part);
                segments.Add((part, _location with { Path = accumulated }));
            }
        }
        else         if (IsCloud)
        {
            var account = Account;
            var root = account?.EffectiveRootPath ?? "/";
            segments.Add((account?.DisplayName ?? L("Cloud"), _location with { Path = root }));
            var path = CurrentPath;
            if (Core.IO.CloudPath.IsWithin(path, root) && Core.IO.CloudPath.Normalize(path) != Core.IO.CloudPath.Normalize(root))
            {
                var relative = Core.IO.CloudPath.Normalize(path)[Core.IO.CloudPath.Normalize(root).TrimEnd('/').Length..];
                var accumulated = Core.IO.CloudPath.Normalize(root);
                foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    accumulated = Core.IO.CloudPath.Combine(accumulated, part);
                    segments.Add((part, _location with { Path = accumulated }));
                }
            }
            else if (Core.IO.CloudPath.Normalize(path) != Core.IO.CloudPath.Normalize(root))
            {
                var accumulated = "/";
                foreach (var part in Core.IO.CloudPath.Components(path))
                {
                    accumulated = Core.IO.CloudPath.Combine(accumulated, part);
                    segments.Add((part, _location with { Path = accumulated }));
                }
            }
        }
        else
        {
            segments.Add((L("This PC"), PanelLocation.Local(string.Empty)));
            var path = CurrentPath;
            if (path.Length > 0)
            {
                var chain = new List<string>();
                for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)) ?? string.Empty)
                {
                    chain.Add(current);
                    if (Path.GetPathRoot(current) == current)
                    {
                        break;
                    }
                }

                chain.Reverse();
                foreach (var folder in chain)
                {
                    segments.Add((LocalFileProvider.Shared.GetName(folder), PanelLocation.Local(folder)));
                }
            }
        }

        return segments.Select((s, i) => new BreadcrumbSegment(s.Title, s.Location, i == segments.Count - 1)).ToList();
    }

    // MARK: - Helpers for commands

    public FileRowViewModel? RowNamed(string name) => _allRows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The account's provider failed sign-in; after re-auth the panel reloads.</summary>
    public async Task ReloadAfterReauthenticationAsync()
    {
        _provider = _services.ProviderFor(_location);
        OnPropertyChanged(nameof(Provider));
        await LoadAsync([], quiet: false);
    }

    /// <summary>A removed or disconnected account: the provider is gone, refresh the error state.</summary>
    public void ProviderInvalidated()
    {
        _provider = _services.ProviderFor(_location);
        OnPropertyChanged(nameof(Provider));
    }

    public void NotifyLanguageChanged()
    {
        foreach (var row in _allRows)
        {
            row.OnLanguageChanged();
        }

        if (_sortField == SortField.Kind)
        {
            // Type names are in the new language now, and may order differently.
            _sortedField = null;
            ApplyView();
        }

        OnAllPropertiesChanged();
    }
}
