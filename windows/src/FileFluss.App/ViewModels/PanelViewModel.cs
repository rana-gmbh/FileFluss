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
using FileFluss.Native;
using static FileFluss.Core.Localization;

namespace FileFluss.App.ViewModels;

/// <summary>One crumb of the path bar.</summary>
public sealed record BreadcrumbSegment(string Title, PanelLocation Location, bool IsLast);

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
    private int _historyIndex = -1;
    private List<FileRowViewModel> _allRows = [];
    private IReadOnlyList<FileRowViewModel> _rows = [];
    private IReadOnlyList<FileRowViewModel> _selection = [];
    private PanelLocation _location = PanelLocation.Local(string.Empty);
    private FileProvider? _provider;
    private bool _isLoading;
    private string? _error;
    private bool _needsReauthentication;
    private SortField _sortField = SortField.Name;
    private bool _sortAscending = true;
    private string _filterText = string.Empty;
    private bool _isFilterVisible;
    private bool _showHiddenFiles;
    private string _quotaText = string.Empty;
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
        services.Accounts.AccountChanged += (_, id) =>
        {
            if (_location.AccountId == id)
            {
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

    public bool IsLocal => !_location.IsCloud;

    public CloudAccount? Account => _location.AccountId is { } id ? _services.Accounts.Find(id) : null;

    public string CurrentPath => _location.Path;

    /// <summary>Read-only: a camera, a source that is offline.</summary>
    public bool IsReadOnly => Account is { } account && (account.ProviderType.IsReadOnly() || account.IsEffectivelyOffline);

    public bool CanCreateFolders => IsLocal ? CurrentPath.Length > 0 : Account?.ProviderType.CanCreateFolders() == true && !IsReadOnly;

    /// <summary>The current folder's name, for window titles and prompts.</summary>
    public string Title => _provider is null ? string.Empty
        : IsCloud && _provider.IsRoot(CurrentPath) ? Account?.DisplayName ?? string.Empty
        : _provider.GetName(CurrentPath);

    public IReadOnlyList<BreadcrumbSegment> Breadcrumbs => BuildBreadcrumbs();

    // MARK: - Contents

    public IReadOnlyList<FileRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            _rows = value;
            OnPropertyChanged();
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
            OnPropertyChanged(nameof(SelectionText));
            OnPropertyChanged(nameof(StatusText));
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
            _sortAscending = field != SortField.Date && field != SortField.Size;
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

    public string StatusText
    {
        get
        {
            if (_selection.Count > 1)
            {
                return SelectionText;
            }

            var files = _rows.Count(r => !r.IsDirectory);
            var folders = _rows.Count(r => r.IsDirectory);
            var bytes = _rows.Where(r => !r.IsDirectory).Sum(r => r.Item.Size);
            return L("{0} files, {1} folders — {2}", files, folders, ByteFormatter.Format(bytes));
        }
    }

    public string SelectionText
    {
        get
        {
            if (_selection.Count == 0)
            {
                return string.Empty;
            }

            var bytes = _selection.Where(r => !r.IsDirectory).Sum(r => r.Item.Size) + _selection.Sum(r => r.FolderSize ?? 0);
            return L("Selected: {0} items, {1}", _selection.Count, ByteFormatter.Format(bytes));
        }
    }

    /// <summary>"12.3 GB of 2 TB used" for cloud accounts with a quota; drive free space locally.</summary>
    public string QuotaText
    {
        get => _quotaText;
        private set => Set(ref _quotaText, value);
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
        var provider = _services.Hub.For(location);
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
            _allRows = [];
            Rows = [];
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

        await LoadAsync(select, quiet: false);
        _ = UpdateQuotaAsync();
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
        if (IsCloud)
        {
            return NavigateAsync(_location with { Path = Account?.EffectiveRootPath ?? "/" });
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
            Rows = [];
            return;
        }

        if (!quiet)
        {
            IsLoading = true;
        }

        try
        {
            var items = await provider.ListDirectoryAsync(location.Path, ct);
            if (generation != _loadGeneration)
            {
                return;
            }

            var hide = _services.Settings.HideFileExtensions;
            var local = provider.IsLocal;
            var previous = _allRows.ToDictionary(r => r.Name, r => r.FolderSize, StringComparer.Ordinal);
            _allRows = items.Select(i => new FileRowViewModel(i, local, hide)
            {
                FolderSize = previous.GetValueOrDefault(i.Name),
            }).ToList();
            Error = null;
            NeedsReauthentication = false;
            ApplyView();
            ItemsListed?.Invoke(this, items);
            Loaded?.Invoke(this, select);
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
            _allRows = [];
            Rows = [];
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

    private void ApplyView()
    {
        IEnumerable<FileRowViewModel> rows = _allRows;
        if (!_showHiddenFiles)
        {
            rows = rows.Where(r => !(r.Item.IsHidden || (IsCloud && CloudFileItem.IsDotHidden(r.Name))));
        }

        if (!string.IsNullOrWhiteSpace(_filterText))
        {
            var needle = _filterText.Trim();
            rows = rows.Where(r => r.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase));
        }

        Rows = Sort(rows).ToList();
    }

    private IEnumerable<FileRowViewModel> Sort(IEnumerable<FileRowViewModel> rows)
    {
        var names = NaturalStringComparer.Instance;
        Comparison<FileRowViewModel> byField = _sortField switch
        {
            SortField.Date => (a, b) => a.Item.ModificationDate.CompareTo(b.Item.ModificationDate),
            SortField.Size => (a, b) => a.SortSize.CompareTo(b.SortSize),
            SortField.Kind => (a, b) => string.Compare(a.KindText, b.KindText, StringComparison.CurrentCultureIgnoreCase),
            _ => (a, b) => names.Compare(a.Name, b.Name),
        };

        var list = rows.ToList();
        list.Sort((a, b) =>
        {
            // Folders first, whichever way the column is sorted — Explorer's rule.
            if (a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }

            var result = byField(a, b);
            if (result == 0 && _sortField != SortField.Name)
            {
                result = names.Compare(a.Name, b.Name);
            }

            return _sortAscending ? result : -result;
        });
        return list;
    }

    private async Task UpdateQuotaAsync()
    {
        var location = _location;
        string text = string.Empty;
        if (location.AccountId is { } id)
        {
            var quota = await _services.Accounts.QuotaAsync(id);
            if (quota is not null)
            {
                text = quota.TotalBytes is { } total
                    ? L("{0} of {1} used", ByteFormatter.Format(quota.UsedBytes), ByteFormatter.Format(total))
                    : L("{0} used", ByteFormatter.Format(quota.UsedBytes));
            }
        }
        else if (location.Path.Length > 0)
        {
            var free = await LocalFileProvider.Shared.AvailableSpaceAsync(location.Path);
            if (free is { } bytes)
            {
                text = L("{0} free", ByteFormatter.Format(bytes));
            }
        }

        if (location == _location)
        {
            QuotaText = text;
        }
    }

    // MARK: - Live updates of local folders

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!IsLocal || CurrentPath.Length == 0 || !Directory.Exists(CurrentPath))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(CurrentPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
            };
            FileSystemEventHandler changed = (_, _) => Application.Current?.Dispatcher.BeginInvoke(Restart);
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Changed += changed;
            _watcher.Renamed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(Restart);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _watcher = null;
        }

        void Restart()
        {
            _watchDebounce.Stop();
            _watchDebounce.Start();
        }
    }

    // MARK: - Breadcrumbs

    private List<BreadcrumbSegment> BuildBreadcrumbs()
    {
        var segments = new List<(string Title, PanelLocation Location)>();
        if (IsCloud)
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
        _provider = _services.Hub.For(_location);
        OnPropertyChanged(nameof(Provider));
        await LoadAsync([], quiet: false);
    }

    /// <summary>A removed or disconnected account: the provider is gone, refresh the error state.</summary>
    public void ProviderInvalidated()
    {
        _provider = _services.Hub.For(_location);
        OnPropertyChanged(nameof(Provider));
    }

    public void NotifyLanguageChanged()
    {
        foreach (var row in _allRows)
        {
            row.OnLanguageChanged();
        }

        OnAllPropertiesChanged();
    }
}
