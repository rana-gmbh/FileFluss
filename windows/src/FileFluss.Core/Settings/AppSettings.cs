// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using FileFluss.Core.Commands;
using FileFluss.Core.Models;

namespace FileFluss.Core.Settings;

/// <summary>
/// Every preference, persisted as JSON by <see cref="SettingsStore"/>. Each property maps to
/// a macOS <c>UserDefaults</c> key (noted where the name differs); Windows-only ones are
/// marked. Collections are replaced, never mutated in place, so a change always raises
/// <see cref="PropertyChanged"/> and gets saved.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    public const int DefaultCacheMaxSizeMB = 1000;
    public const int DefaultCacheAutoDeleteDays = 7;

    private AppLanguage _language = AppLanguage.System;
    private bool _showHiddenFiles;
    private bool _confirmDelete = true;
    private bool _showSidebarAddAccount = true;
    private bool _allowSidebarRemoveAccount;
    private string _terminal = string.Empty;
    private bool _showSidebarTransfers = true;
    private bool _showSidebarFolderSizes = true;
    private PanelStartupMode _panelStartupMode = PanelStartupMode.Home;
    private PanelLocation? _lastSessionLeft;
    private PanelLocation? _lastSessionRight;
    private PanelLocation? _specificLeft;
    private PanelLocation? _specificRight;
    private bool _automaticUpdateChecks = true;
    private DateTimeOffset? _lastUpdateCheck;
    private bool _shareLinkPreferDirectDownload = true;
    private bool _checkSpaceBeforeTransfer;
    private bool _hasCompletedWelcome;
    private bool _hideFileExtensions;
    private bool _showStatusBar = true;
    private bool _singlePaneMode;
    private bool _cacheAutoManage = true;
    private int _cacheAutoDeleteDays = DefaultCacheAutoDeleteDays;
    private int _cacheMaxSizeMB = DefaultCacheMaxSizeMB;
    private string? _customCacheFolder;
    private bool _searchIncludeOffline = true;
    private FileListRowSize _rowSize = FileListRowSize.Medium;
    private double _leftSidebarWidth = 220;
    private double _rightSidebarWidth = 220;
    private bool _leftSidebarCompact;
    private bool _rightSidebarCompact;
    private Dictionary<string, bool> _sectionExpanded = [];
    private List<FileListColumn> _localColumns = [.. FileListColumns.Defaults];
    private List<FileListColumn> _cloudColumns = [.. FileListColumns.Defaults];
    private Dictionary<string, double> _columnWidths = [];
    private bool _nameAutoResize = true;
    private List<SidebarFavorite>? _leftFavorites;
    private List<SidebarFavorite>? _rightFavorites;
    private KeyboardPreset _keyboardPreset = KeyboardPreset.Explorer;
    private Dictionary<string, ShortcutBinding> _customBindings = [];
    private WindowPlacement? _mainWindow;
    private Dictionary<string, WindowPlacement> _windowFrames = [];
    private int _mountReadCacheMaxMB = 2048;
#if DEBUG
    private List<Guid> _versionTestAccountIds = [];
#endif
    private Dictionary<string, bool> _flags = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    // MARK: - General

    [JsonPropertyName("appLanguage")]
    public AppLanguage Language { get => _language; set => Set(ref _language, value); }

    public bool ShowHiddenFiles { get => _showHiddenFiles; set => Set(ref _showHiddenFiles, value); }

    public bool ConfirmDelete { get => _confirmDelete; set => Set(ref _confirmDelete, value); }

    public bool ShowSidebarAddAccount { get => _showSidebarAddAccount; set => Set(ref _showSidebarAddAccount, value); }

    public bool AllowSidebarRemoveAccount { get => _allowSidebarRemoveAccount; set => Set(ref _allowSidebarRemoveAccount, value); }

    /// <summary>Which terminal "Open in Terminal" starts: a known id ("wt", "pwsh", "powershell", "cmd") or an .exe path. Empty = best installed.</summary>
    public string Terminal { get => _terminal; set => Set(ref _terminal, value); }

    public bool ShowSidebarTransfers { get => _showSidebarTransfers; set => Set(ref _showSidebarTransfers, value); }

    public bool ShowSidebarFolderSizes { get => _showSidebarFolderSizes; set => Set(ref _showSidebarFolderSizes, value); }

    public PanelStartupMode PanelStartupMode { get => _panelStartupMode; set => Set(ref _panelStartupMode, value); }

    public PanelLocation? LastSessionLeft { get => _lastSessionLeft; set => Set(ref _lastSessionLeft, value); }

    public PanelLocation? LastSessionRight { get => _lastSessionRight; set => Set(ref _lastSessionRight, value); }

    public PanelLocation? SpecificLeft { get => _specificLeft; set => Set(ref _specificLeft, value); }

    public PanelLocation? SpecificRight { get => _specificRight; set => Set(ref _specificRight, value); }

    [JsonPropertyName("automaticUpdateChecksEnabled")]
    public bool AutomaticUpdateChecks { get => _automaticUpdateChecks; set => Set(ref _automaticUpdateChecks, value); }

    public DateTimeOffset? LastUpdateCheck { get => _lastUpdateCheck; set => Set(ref _lastUpdateCheck, value); }

    public bool ShareLinkPreferDirectDownload { get => _shareLinkPreferDirectDownload; set => Set(ref _shareLinkPreferDirectDownload, value); }

    public bool CheckSpaceBeforeTransfer { get => _checkSpaceBeforeTransfer; set => Set(ref _checkSpaceBeforeTransfer, value); }

    public bool HasCompletedWelcome { get => _hasCompletedWelcome; set => Set(ref _hasCompletedWelcome, value); }

    public bool HideFileExtensions { get => _hideFileExtensions; set => Set(ref _hideFileExtensions, value); }

    public bool ShowStatusBar { get => _showStatusBar; set => Set(ref _showStatusBar, value); }

    public bool SinglePaneMode { get => _singlePaneMode; set => Set(ref _singlePaneMode, value); }

    // MARK: - Storage / cache

    public bool CacheAutoManage { get => _cacheAutoManage; set => Set(ref _cacheAutoManage, value); }

    public int CacheAutoDeleteDays { get => _cacheAutoDeleteDays; set => Set(ref _cacheAutoDeleteDays, Math.Clamp(value, 1, 365)); }

    public int CacheMaxSizeMB { get => _cacheMaxSizeMB; set => Set(ref _cacheMaxSizeMB, Math.Clamp(value, 100, 10_000)); }

    /// <summary>macOS <c>customCacheFolderPath</c>; null = system temp.</summary>
    public string? CustomCacheFolder { get => _customCacheFolder; set => Set(ref _customCacheFolder, value); }

    /// <summary>macOS <c>webdavReadCacheMaxMB</c> — read cache for mounted accounts.</summary>
    public int MountReadCacheMaxMB { get => _mountReadCacheMaxMB; set => Set(ref _mountReadCacheMaxMB, Math.Clamp(value, 100, 50_000)); }

    // MARK: - Search

    [JsonPropertyName("search.includeOffline")]
    public bool SearchIncludeOffline { get => _searchIncludeOffline; set => Set(ref _searchIncludeOffline, value); }

    // MARK: - File list and sidebar layout

    [JsonPropertyName("FileList.rowSize")]
    public FileListRowSize RowSize { get => _rowSize; set => Set(ref _rowSize, value); }

    /// <summary>Visible columns in display order (macOS "FileList.local.visibleColumns"); see <see cref="FileListColumns"/>.</summary>
    public List<FileListColumn> LocalColumns { get => _localColumns; set => Set(ref _localColumns, value); }

    public List<FileListColumn> CloudColumns { get => _cloudColumns; set => Set(ref _cloudColumns, value); }

    /// <summary>Column widths keyed "local.Size", "cloud.Name", …</summary>
    public Dictionary<string, double> ColumnWidths { get => _columnWidths; set => Set(ref _columnWidths, value); }

    public bool NameAutoResize { get => _nameAutoResize; set => Set(ref _nameAutoResize, value); }

    [JsonPropertyName("sidebarWidth.left")]
    public double LeftSidebarWidth { get => _leftSidebarWidth; set => Set(ref _leftSidebarWidth, value); }

    [JsonPropertyName("sidebarWidth.right")]
    public double RightSidebarWidth { get => _rightSidebarWidth; set => Set(ref _rightSidebarWidth, value); }

    public bool LeftSidebarCompact { get => _leftSidebarCompact; set => Set(ref _leftSidebarCompact, value); }

    public bool RightSidebarCompact { get => _rightSidebarCompact; set => Set(ref _rightSidebarCompact, value); }

    /// <summary>"left.favorites" → expanded. Absent = expanded.</summary>
    public Dictionary<string, bool> SectionExpanded { get => _sectionExpanded; set => Set(ref _sectionExpanded, value); }

    [JsonPropertyName("sidebarFavorites.left")]
    public List<SidebarFavorite>? LeftFavorites { get => _leftFavorites; set => Set(ref _leftFavorites, value); }

    [JsonPropertyName("sidebarFavorites.right")]
    public List<SidebarFavorite>? RightFavorites { get => _rightFavorites; set => Set(ref _rightFavorites, value); }

    // MARK: - Keyboard

    [JsonPropertyName("keyboard.preset")]
    public KeyboardPreset KeyboardPreset { get => _keyboardPreset; set => Set(ref _keyboardPreset, value); }

    /// <summary>Overrides by command name; a binding with an empty key means "explicitly cleared".</summary>
    [JsonPropertyName("keyboard.customBindings")]
    public Dictionary<string, ShortcutBinding> CustomBindings { get => _customBindings; set => Set(ref _customBindings, value); }

    // MARK: - Windows

    [JsonPropertyName("mainWindowFrame")]
    public WindowPlacement? MainWindow { get => _mainWindow; set => Set(ref _mainWindow, value); }

    /// <summary>Search, Compare, Storage Analysis… — "size and position remembered across launches".</summary>
    public Dictionary<string, WindowPlacement> WindowFrames { get => _windowFrames; set => Set(ref _windowFrames, value); }

    // MARK: - Misc

#if DEBUG
    /// <summary>The accounts last ticked for the debug-only version test (macOS <c>versionTestSelectedAccountIds</c>).</summary>
    public List<Guid> VersionTestAccountIds { get => _versionTestAccountIds; set => Set(ref _versionTestAccountIds, value); }
#endif

    /// <summary>
    /// Small per-account facts learned at run time (macOS <c>shareLinksNotPublic.&lt;id&gt;</c>,
    /// <c>shareOptionRejected.password.&lt;id&gt;</c>, …), keyed the same way.
    /// </summary>
    public Dictionary<string, bool> Flags { get => _flags; set => Set(ref _flags, value); }

    public bool GetFlag(string key) => _flags.TryGetValue(key, out var value) && value;

    public void SetFlag(string key, bool value)
    {
        if (GetFlag(key) == value)
        {
            return;
        }

        var copy = new Dictionary<string, bool>(_flags) { [key] = value };
        Flags = copy;
    }

    public bool IsSectionExpanded(string key) => !_sectionExpanded.TryGetValue(key, out var value) || value;

    public void SetSectionExpanded(string key, bool expanded)
    {
        var copy = new Dictionary<string, bool>(_sectionExpanded) { [key] = expanded };
        SectionExpanded = copy;
    }

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

/// <summary>A window's restore bounds in device-independent units.</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);
