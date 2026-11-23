// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Input;
using FileFluss.App.Mvvm;
using FileFluss.App.Services;
using FileFluss.App.Views.Dialogs;
using FileFluss.Core.Commands;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Transfers;
using FileFluss.Native;
using static FileFluss.Core.Localization;

namespace FileFluss.App.ViewModels;

/// <summary>What the main view model needs the window to do: open other windows, move focus.</summary>
public interface IShell
{
    Window Window { get; }

    void FocusPanel(PanelSide side);

    void BeginRename(PanelSide side);

    void ApplySelection(PanelSide side, SelectionAction action);

    void ShowGoToFolder();

    void ShowSettings(string? page = null);

    void ShowAddAccount();

    void ShowSearch();

    void ShowCompare();

    void ShowSync();

    void ShowStorageAnalysis(PanelLocation location);

    void ShowQuickLook(PanelViewModel panel);

    void ShowShareLink(PanelViewModel panel, CloudFileItem item);

    void ShowProperties(PanelViewModel panel, CloudFileItem item);
}

public enum SelectionAction
{
    SelectAll,
    DeselectAll,
    Invert,
}

/// <summary>Files captured by Ctrl+C / Ctrl+X inside FileFluss.</summary>
public sealed record FileClipboard(FileProvider Source, PanelLocation SourceFolder, IReadOnlyList<CloudFileItem> Items, bool IsCut);

/// <summary>
/// The window's state and commands — the Windows <c>AppState</c>: both panels and sidebars,
/// which one is active, drag &amp; drop mode, the file clipboard, and every command the menus,
/// toolbar and keyboard map can trigger.
/// </summary>
public sealed partial class MainViewModel : Observable
{
    private readonly AppServices _services;
    private PanelSide _activeSide = PanelSide.Left;
    private DragDropMode _dragDropMode = DragDropMode.Ask;
    private FileClipboard? _clipboard;
    private DateTimeOffset _lastPaste = DateTimeOffset.MinValue;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Left = new PanelViewModel(PanelSide.Left, services);
        Right = new PanelViewModel(PanelSide.Right, services);
        LeftSidebar = new SidebarViewModel(PanelSide.Left, Left, services);
        RightSidebar = new SidebarViewModel(PanelSide.Right, Right, services);
        services.Transfers.All.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Transfers));
        services.Accounts.AccountRemoved += (_, id) => OnAccountRemovedAsync(id).Forget("leaving a removed account");
        services.Accounts.AccountRemoved += (_, id) => services.Mount.UnmountAsync(id).Forget("unmounting a removed account");
        services.Mount.NeedsReauthentication += (_, id) => services.Accounts.MarkNeedsReauthentication(id);
        services.Hub.ProviderChanged += (_, id) =>
        {
            foreach (var panel in new[] { Left, Right })
            {
                if (panel.Location.AccountId == id)
                {
                    panel.ProviderInvalidated();
                }
            }
        };
        services.SettingsStore.Changed += (_, name) =>
        {
            if (name is null or nameof(Core.Settings.AppSettings.SinglePaneMode))
            {
                OnPropertyChanged(nameof(SinglePaneMode));
            }
        };
    }

    public IShell Shell { get; set; } = null!;

    public AppServices Services => _services;

    public PanelViewModel Left { get; }

    public PanelViewModel Right { get; }

    public SidebarViewModel LeftSidebar { get; }

    public SidebarViewModel RightSidebar { get; }

    public TransferCenter Transfers => _services.Transfers;

    public PanelSide ActiveSide
    {
        get => _activeSide;
        set
        {
            if (Set(ref _activeSide, value))
            {
                OnPropertyChanged(nameof(ActivePanel));
                OnPropertyChanged(nameof(OtherPanel));
                OnPropertyChanged(nameof(IsLeftActive));
                OnPropertyChanged(nameof(IsRightActive));
            }
        }
    }

    public bool IsLeftActive => _activeSide == PanelSide.Left;

    public bool IsRightActive => _activeSide == PanelSide.Right;

    public PanelViewModel ActivePanel => Panel(_activeSide);

    public PanelViewModel OtherPanel => Panel(_activeSide.Other());

    public PanelViewModel Panel(PanelSide side) => side == PanelSide.Left ? Left : Right;

    public SidebarViewModel Sidebar(PanelSide side) => side == PanelSide.Left ? LeftSidebar : RightSidebar;

    /// <summary>Copy Mode / Move Mode: session-only, so a stuck Move Mode can't destroy files next launch.</summary>
    public DragDropMode DragDropMode
    {
        get => _dragDropMode;
        set
        {
            if (Set(ref _dragDropMode, value))
            {
                OnPropertyChanged(nameof(IsCopyMode));
                OnPropertyChanged(nameof(IsMoveMode));
            }
        }
    }

    public bool IsCopyMode
    {
        get => _dragDropMode == DragDropMode.Copy;
        set => DragDropMode = value ? DragDropMode.Copy : DragDropMode.Ask;
    }

    public bool IsMoveMode
    {
        get => _dragDropMode == DragDropMode.Move;
        set => DragDropMode = value ? DragDropMode.Move : DragDropMode.Ask;
    }

    public bool SinglePaneMode
    {
        get => _services.Settings.SinglePaneMode;
        set => _services.Settings.SinglePaneMode = value;
    }

    public FileClipboard? Clipboard
    {
        get => _clipboard;
        private set
        {
            var previous = _clipboard;
            _clipboard = value;
            MarkCut(previous, false);
            MarkCut(value, true);
            OnPropertyChanged();
        }
    }

    // MARK: - Startup

    /// <summary>
    /// Opens each panel where the startup setting says (issue #49): local folders now, cloud
    /// folders once accounts are back. Both panels list at the same time; whether a remembered
    /// folder still exists is asked off the UI thread (a network share may be slow to answer),
    /// and only an unplugged drive's offline view waits for <paramref name="indexReady"/>.
    /// </summary>
    public async Task RestoreStartupLocationsAsync(bool cloudOnly, Task? indexReady = null)
    {
        var settings = _services.Settings;
        var (left, right) = settings.PanelStartupMode switch
        {
            PanelStartupMode.LastSession => (settings.LastSessionLeft, settings.LastSessionRight),
            PanelStartupMode.Specific => (settings.SpecificLeft, settings.SpecificRight),
            _ => (null, null),
        };

        var home = PanelLocation.Local(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        await Task.WhenAll(RestoreAsync(Left, left), RestoreAsync(Right, right));

        async Task RestoreAsync(PanelViewModel panel, PanelLocation? target)
        {
            if (target is { IsArchive: true })
            {
                target = Rememberable(target);
            }

            if (target is null)
            {
                if (!cloudOnly)
                {
                    await panel.NavigateAsync(home);
                }

                return;
            }

            if (target.IsCloud != cloudOnly)
            {
                if (!cloudOnly)
                {
                    // Show home while the account reconnects rather than an empty panel.
                    await panel.NavigateAsync(home);
                }

                return;
            }

            bool usable;
            if (target.IsOfflineDrive)
            {
                if (indexReady is not null)
                {
                    await indexReady;
                }

                var drives = _services.Search.Drives;
                usable = await Task.Run(() => drives.Find(target.DriveId!) is not null);
            }
            else if (target.IsCloud)
            {
                usable = _services.Hub.Get(target.AccountId!.Value) is not null;
            }
            else
            {
                usable = target.Path.Length == 0 || await Task.Run(() => Directory.Exists(target.Path));
            }

            await panel.NavigateAsync(usable ? target : home);
        }
    }

    /// <summary>Called as the app closes; written once per session, only read at launch.</summary>
    public void RecordSessionLocations()
    {
        if (_services.Settings.PanelStartupMode == PanelStartupMode.LastSession)
        {
            _services.SettingsStore.Batch(s =>
            {
                s.LastSessionLeft = Rememberable(Left.Location);
                s.LastSessionRight = Rememberable(Right.Location);
            });
        }
    }

    // MARK: - Command routing

    /// <summary>Single-pane mode hides everything that needs a second panel.</summary>
    public bool CanExecute(KeyboardCommand command)
        // While another app's paste or drop waits for a listing (CloudFileExport keeps the
        // window responsive then), its OLE call is still on the stack: no command starts in the
        // middle of it — a shortcut, menu item or toolbar button is simply ignored until it returns.
        => !CloudFileExport.IsHandingOver &&
           (!SinglePaneMode || command is not (KeyboardCommand.OpenInOtherPanel or KeyboardCommand.SwapPanels or
            KeyboardCommand.TargetToSource or KeyboardCommand.CompareFolders or KeyboardCommand.SyncPanels or
            KeyboardCommand.CopyToOtherPanel or KeyboardCommand.MoveToOtherPanel or KeyboardCommand.ToggleActivePanel));

    /// <summary>Whether a menu shows the command enabled — the macOS menus' <c>.disabled(…)</c> conditions.</summary>
    public bool IsCommandEnabled(KeyboardCommand command)
    {
        var panel = ActivePanel;
        var selected = panel.Selection.Count;
        return CanExecute(command) && command switch
        {
            KeyboardCommand.NewFolder => panel.CanCreateFolders,
            KeyboardCommand.Rename => selected == 1 && !panel.IsReadOnly,
            KeyboardCommand.Duplicate or KeyboardCommand.CutFiles or KeyboardCommand.MoveToOtherPanel or KeyboardCommand.DeleteToTrash
                => selected > 0 && !panel.IsReadOnly,
            KeyboardCommand.GetInfo or KeyboardCommand.CopyToOtherPanel or KeyboardCommand.CopyPath
                => selected > 0,
            KeyboardCommand.CopyFiles => selected > 0 && !panel.IsArchive,
            KeyboardCommand.PasteFiles => !panel.IsReadOnly,
            KeyboardCommand.HistoryBack => panel.CanGoBack,
            KeyboardCommand.HistoryForward => panel.CanGoForward,
            KeyboardCommand.ParentDirectory => panel.CanGoUp,
            KeyboardCommand.SyncPanels => !Left.IsOffline && !Right.IsOffline,
            _ => true,
        };
    }

    public async Task ExecuteAsync(KeyboardCommand command)
    {
        if (!CanExecute(command))
        {
            return;
        }

        var panel = ActivePanel;
        try
        {
            switch (command)
            {
                case KeyboardCommand.ToggleActivePanel:
                    if (!SinglePaneMode)
                    {
                        ActiveSide = _activeSide.Other();
                        Shell.FocusPanel(_activeSide);
                    }

                    break;
                case KeyboardCommand.ParentDirectory:
                    await panel.GoUpAsync();
                    Shell.FocusPanel(_activeSide);
                    break;
                case KeyboardCommand.OpenDirectory:
                    if (panel.Selection.FirstOrDefault() is { } row)
                    {
                        await OpenAsync(panel, row);
                    }

                    break;
                case KeyboardCommand.HistoryBack:
                    await panel.GoBackAsync();
                    Shell.FocusPanel(_activeSide);
                    break;
                case KeyboardCommand.HistoryForward:
                    await panel.GoForwardAsync();
                    Shell.FocusPanel(_activeSide);
                    break;
                case KeyboardCommand.OpenInOtherPanel:
                    if (panel.Selection.FirstOrDefault() is { IsDirectory: true } folder)
                    {
                        await OtherPanel.NavigateAsync(panel.Location with { Path = folder.Path });
                    }

                    break;
                case KeyboardCommand.FocusPathBar:
                    Shell.ShowGoToFolder();
                    break;
                case KeyboardCommand.JumpToRoot:
                    await panel.JumpToRootAsync();
                    break;
                case KeyboardCommand.QuickLook:
                    Shell.ShowQuickLook(panel);
                    break;
                case KeyboardCommand.CopyToOtherPanel:
                    await TransferSelectionToOtherPanelAsync(isMove: false);
                    break;
                case KeyboardCommand.MoveToOtherPanel:
                    await TransferSelectionToOtherPanelAsync(isMove: true);
                    break;
                case KeyboardCommand.Rename:
                    if (panel.Selection.Count == 1 && !panel.IsReadOnly && panel.Selection[0].Item.IsModifiable)
                    {
                        Shell.BeginRename(_activeSide);
                    }

                    break;
                case KeyboardCommand.NewFolder:
                    await NewFolderAsync(panel);
                    break;
                case KeyboardCommand.DeleteToTrash:
                    await DeleteSelectionAsync(panel);
                    break;
                case KeyboardCommand.Duplicate:
                    DuplicateSelection(panel);
                    break;
                case KeyboardCommand.CopyFiles:
                    CaptureClipboard(panel, isCut: false);
                    break;
                case KeyboardCommand.CutFiles:
                    CaptureClipboard(panel, isCut: true);
                    break;
                case KeyboardCommand.PasteFiles:
                    await PasteAsync(panel);
                    break;
                case KeyboardCommand.CopyPath:
                    CopyPaths(panel);
                    break;
                case KeyboardCommand.SelectAll:
                    Shell.ApplySelection(_activeSide, SelectionAction.SelectAll);
                    break;
                case KeyboardCommand.DeselectAll:
                    Shell.ApplySelection(_activeSide, SelectionAction.DeselectAll);
                    break;
                case KeyboardCommand.InvertSelection:
                    Shell.ApplySelection(_activeSide, SelectionAction.Invert);
                    break;
                case KeyboardCommand.GetInfo:
                    if (panel.Selection.FirstOrDefault() is { } info)
                    {
                        Shell.ShowProperties(panel, info.Item);
                    }

                    break;
                case KeyboardCommand.SortByName:
                    panel.SortBy(SortField.Name);
                    break;
                case KeyboardCommand.SortByDate:
                    panel.SortBy(SortField.Date);
                    break;
                case KeyboardCommand.SortBySize:
                    panel.SortBy(SortField.Size);
                    break;
                case KeyboardCommand.SortByKind:
                    panel.SortBy(SortField.Kind);
                    break;
                case KeyboardCommand.ToggleHiddenFiles:
                    panel.ShowHiddenFiles = !panel.ShowHiddenFiles;
                    break;
                case KeyboardCommand.ViewAsList:
                    SetViewMode(panel, FileListViewMode.List);
                    break;
                case KeyboardCommand.ViewAsGallery:
                    SetViewMode(panel, FileListViewMode.Gallery);
                    break;
                case KeyboardCommand.SwapPanels:
                    await SwapPanelsAsync();
                    break;
                case KeyboardCommand.SyncPanels:
                    Shell.ShowSync();
                    break;
                case KeyboardCommand.CompareFolders:
                    Shell.ShowCompare();
                    break;
                case KeyboardCommand.TargetToSource:
                    await OtherPanel.NavigateAsync(panel.Location);
                    break;
                case KeyboardCommand.RefreshActive:
                    await panel.RefreshAsync();
                    break;
                case KeyboardCommand.RefreshAll:
                    await Task.WhenAll(Left.RefreshAsync(), Right.RefreshAsync());
                    LeftSidebar.RebuildDrives();
                    RightSidebar.RebuildDrives();
                    break;
                case KeyboardCommand.OpenSearch:
                    Shell.ShowSearch();
                    break;
                case KeyboardCommand.QuickFilter:
                    panel.IsFilterVisible = !panel.IsFilterVisible;
                    break;
                case KeyboardCommand.OpenSettings:
                    Shell.ShowSettings();
                    break;
                case KeyboardCommand.IndexCurrentSource:
                    IndexRequested?.Invoke(this, panel.Location);
                    break;
                case KeyboardCommand.OpenInTerminal:
                    OpenTerminal(panel);
                    break;
            }
        }
        catch (Exception e)
        {
            SupportLogger.Shared.Log($"{command} failed: {e.Message}", "Command", LogLevel.Error);
            MessageDialog.Error(Shell.Window, L(command.DisplayName()), e.Message);
        }
    }

    /// <summary>"Index Current Source" — handled by the indexing service once it is wired.</summary>
    public event EventHandler<PanelLocation>? IndexRequested;

    public void ExecuteIndexRequest(PanelLocation location) => IndexRequested?.Invoke(this, location);

    // MARK: - Opening

    /// <summary>Double-click / Enter: enter folders; open files in their default app (cloud files via the cache).</summary>
    public async Task OpenAsync(PanelViewModel panel, FileRowViewModel row)
    {
        // Not in the middle of another app's paste or drop (see CanExecute).
        if (CloudFileExport.IsHandingOver)
        {
            return;
        }

        if (row.IsDirectory)
        {
            await panel.NavigateAsync(panel.Location with { Path = row.Path });
            Shell.FocusPanel(panel.Side);
            return;
        }

        // Inside an archive: unpack just that entry and open it. An archive file: open it in
        // the panel, like a folder, unless it turns out not to be one.
        if (panel.Archive is { } archive)
        {
            await OpenArchiveEntryAsync(panel, archive, row.Item);
            return;
        }

        if (Core.Archives.ArchiveFormats.Detect(row.Name) is { } format && await TryOpenArchiveAsync(panel, row.Item, format))
        {
            return;
        }

        if (panel.IsLocal)
        {
            try
            {
                await ShellFileOperations.OpenAsync(row.Path);
            }
            catch (Exception e)
            {
                MessageDialog.Error(Shell.Window, L("Couldn't open \"{0}\"", row.Name), e.Message);
            }

            return;
        }

        await OpenCloudFileAsync(panel, row.Item);
    }

    /// <summary>
    /// Hands a cloud file to its default app. A mapped account opens it where it really is, on
    /// its drive, so a save goes back to the server (macOS 574038b); otherwise it is
    /// downloaded to the open cache first (<see cref="CachedCopyAsync"/>). With
    /// <paramref name="launch"/> false (Quick Look) it only fetches the cached copy.
    /// </summary>
    public async Task<string?> OpenCloudFileAsync(PanelViewModel panel, CloudFileItem item, bool launch = true, CancellationToken cancellationToken = default)
    {
        if (launch && OpensOnTheDrive(item.Name) && await MountedPathsAsync(panel, [item]) is [var mounted])
        {
            try
            {
                await ShellFileOperations.OpenAsync(mounted);
            }
            catch (Exception e)
            {
                MessageDialog.Error(Shell.Window, L("Couldn't open \"{0}\"", item.Name), e.Message);
            }

            return mounted;
        }

        if (await SharedCachedCopyAsync(panel, item, cancellationToken) is not { } copy)
        {
            return null;
        }

        var target = copy.Path;
        var kept = copy.TakeKept();
        if (launch)
        {
            try
            {
                // Downloaded from someone's server: SmartScreen and Protected View apply, as to
                // a browser download. (Quick Look's own preview stays unmarked.)
                await ShellFileOperations.OpenAsync(MarkedForOpening(target, item.Name));
            }
            catch (Exception e)
            {
                MessageDialog.Error(Shell.Window, L("Couldn't open \"{0}\"", item.Name), e.Message);
            }
        }

        if (kept is not null)
        {
            ShowKeptEdits(item.Name, kept);
        }

        return target;
    }

    /// <summary>
    /// Right-click → Open With (macOS <c>openCloudFiles(_:withApplication:)</c>): the selection
    /// as local paths — itself in a local panel, on the drive of a mapped account, else
    /// downloaded copies marked as coming from the Internet — handed to <paramref name="open"/>,
    /// which returns false when the app refused them.
    /// </summary>
    public async Task OpenWithAsync(PanelViewModel panel, IReadOnlyList<CloudFileItem> items, Func<IReadOnlyList<string>, bool> open)
    {
        if (items.Count == 0 || CloudFileExport.IsHandingOver)
        {
            return;
        }

        var paths = new List<string>();
        var kept = new List<(string Name, string Path)>();
        if (panel.IsLocal)
        {
            paths.AddRange(items.Select(i => i.Path));
        }
        else if (panel.Provider is Core.Archives.ArchiveProvider archive)
        {
            // Entries of an archive: unpack just these, as opening one does.
            try
            {
                var cursor = Mouse.OverrideCursor;
                Mouse.OverrideCursor = Cursors.AppStarting;
                try
                {
                    paths.AddRange(await archive.ExtractForOpeningAsync(items.Select(i => i.Path).ToList()));
                }
                finally
                {
                    Mouse.OverrideCursor = cursor;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                MessageDialog.Error(Shell.Window, L("Couldn't open \"{0}\"", items[0].Name), e.Message);
                return;
            }

            // Out of a downloaded archive: SmartScreen and Protected View apply.
            if (panel.Location.ArchiveAccountId is not null)
            {
                for (var i = 0; i < paths.Count; i++)
                {
                    paths[i] = MarkedForOpening(paths[i], Path.GetFileName(paths[i]));
                }
            }
        }
        else if (items.All(i => OpensOnTheDrive(i.Name)) && await MountedPathsAsync(panel, items) is { } mounted)
        {
            paths.AddRange(mounted);
        }
        else
        {
            foreach (var item in items)
            {
                if (await SharedCachedCopyAsync(panel, item, CancellationToken.None) is not { } copy)
                {
                    // Failed (and said so) or cancelled: the rest still open, as on macOS.
                    continue;
                }

                var target = copy.Path;
                var keptAt = copy.TakeKept();
                paths.Add(MarkedForOpening(target, item.Name));
                if (keptAt is not null)
                {
                    kept.Add((item.Name, keptAt));
                }
            }
        }

        if (paths.Count > 0)
        {
            string? failure = null;
            try
            {
                if (!open(paths))
                {
                    // ERROR_NO_ASSOCIATION, in the system's own words and language.
                    failure = new System.ComponentModel.Win32Exception(1155).Message;
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or InvalidOperationException or IOException)
            {
                failure = e.Message;
            }

            if (failure is not null)
            {
                SupportLogger.Shared.Log($"Open With failed: {failure}", "Open", LogLevel.Error);
                MessageDialog.Error(Shell.Window, L("Couldn't open \"{0}\"", Path.GetFileName(paths[0])), failure);
            }
        }

        foreach (var (name, path) in kept)
        {
            ShowKeptEdits(name, path);
        }
    }

    /// <summary>
    /// Whether a file of a mapped account is opened where it is, on the account's drive (so a
    /// save goes back to the server), rather than from a downloaded copy. Not for the types
    /// Windows counts as dangerous — programs, scripts, shortcuts: the drive is the loopback
    /// share <c>\\127.0.0.1@port\…</c>, which Windows maps to the Internet zone itself, but
    /// what it makes of the drive letter mapped onto it can't be relied on, and a file from
    /// someone else's server must not start without SmartScreen or the "Open File" warning.
    /// Those open from the cached copy, which carries the mark explicitly. Nobody edits a
    /// program in place, so nothing is lost by it.
    /// </summary>
    internal static bool OpensOnTheDrive(string fileName) => !ShellFileOperations.IsDangerousType(fileName);

    /// <summary>
    /// The path to hand to an app for a downloaded copy: <paramref name="target"/>, marked as
    /// coming from the Internet (SmartScreen, Protected View, the "Open File" warning). A copy
    /// on a volume that can't carry the mark — a custom cache folder on FAT32 or exFAT — opens
    /// unmarked, as a browser's download there would; for a type Windows counts as dangerous
    /// a marked copy in the system's own temporary folder is opened instead. (That copy is
    /// cleaned up with the rest of the cache; an edit saved to it isn't watched for, which for
    /// programs and scripts is the right trade.)
    /// </summary>
    internal static string MarkedForOpening(string target, string fileName)
    {
        if (MarkOfTheWeb.Apply(target) || !ShellFileOperations.IsDangerousType(fileName))
        {
            return target;
        }

        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "FileFluss-Cache", "open-marked", Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(folder);
            var marked = Path.Combine(folder, Path.GetFileName(target));
            File.Copy(target, marked, overwrite: true);
            if (MarkOfTheWeb.Apply(marked))
            {
                SupportLogger.Shared.Log($"open: {fileName} couldn't be marked where it is cached; opening a marked copy from the system's temporary folder", "Open", LogLevel.Notice);
                return marked;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SupportLogger.Shared.Log($"open: no marked copy of {fileName} could be made: {e.Message}", "Open", LogLevel.Notice);
        }

        return target;
    }

    /// <summary>
    /// Where the items are on the drive of their mapped account, or null when the account isn't
    /// mapped, a file is past the WebDAV redirector's size limit, or a path doesn't resolve on
    /// the drive — a copy then beats refusing to open anything (logged, as on macOS).
    /// </summary>
    private async Task<IReadOnlyList<string>?> MountedPathsAsync(PanelViewModel panel, IReadOnlyList<CloudFileItem> items)
    {
        if (!panel.IsCloud || panel.IsOffline || panel.Location.AccountId is not { } accountId || _services.Mount.MountFor(accountId) is not { } mount)
        {
            return null;
        }

        if (mount.FileSizeLimitBytes is { } limit && items.Any(i => i.Size > limit))
        {
            return null;
        }

        var paths = items.Select(i => DrivePathFor(mount, i.Path)).ToList();
        try
        {
            // Each check is a round trip through the redirector: off the UI thread, and bounded.
            if (await Task.Run(() => paths.All(File.Exists)).WaitAsync(TimeSpan.FromSeconds(10)))
            {
                return paths;
            }
        }
        catch (TimeoutException)
        {
        }

        SupportLogger.Shared.Log($"open: {items[0].Path} not found on the drive {mount.DriveLetter}; opening a copy instead", "Open");
        return null;
    }

    /// <summary>
    /// "Your changes were kept, but not uploaded" (macOS 851b79c): saying nothing would leave
    /// the user believing the work is gone.
    /// </summary>
    private void ShowKeptEdits(string name, string keptPath)
    {
        var choice = MessageDialog.Show(Shell.Window, L("Your changes were kept, but not uploaded"),
            L("“{0}” was changed on this Mac after FileFluss opened it. FileFluss doesn't upload changes made in another app yet, so the edited copy has been kept and the version from the account opened instead. To edit in place, mount the account in Finder.", name),
            DialogIcon.Info,
            new DialogButton("reveal", L("Show in Finder")),
            new DialogButton("ok", L("OK"), IsDefault: true, IsCancel: true));
        if (choice == "reveal")
        {
            ShellFileOperations.RevealInExplorer([keptPath]);
        }
    }

    /// <summary>Cached copies being fetched, one per account and path (see <see cref="SingleFlight{TKey, TResult}"/>).</summary>
    private readonly SingleFlight<(Guid Account, string Path), OpenedCopy?> _openingCopies = new();

    /// <summary>
    /// <see cref="CachedCopyAsync"/>, shared by everyone opening the same file at once: a second
    /// double-click, or Enter after Quick Look, waits for the download already running instead
    /// of starting another into the same partial file. <paramref name="cancellationToken"/>
    /// only stops this caller waiting (null then); the download stops once nobody waits for it.
    /// </summary>
    private async Task<OpenedCopy?> SharedCachedCopyAsync(PanelViewModel panel, CloudFileItem item, CancellationToken cancellationToken)
    {
        if (panel.Location.AccountId is not { } accountId)
        {
            return null;
        }

        try
        {
            return await _openingCopies.RunAsync((accountId, item.Path), token => CachedCopyAsync(panel, item, token), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// A cached copy, with the edited copy moved aside on the way (if any) — which only one of
    /// the callers sharing it reports, so the notice appears once.
    /// </summary>
    private sealed class OpenedCopy(string path, string? kept)
    {
        private string? _kept = kept;

        public string Path { get; } = path;

        public string? TakeKept() => Interlocked.Exchange(ref _kept, null);
    }

    /// <summary>
    /// The open cache's copy of a cloud file, downloaded unless the copy there is still current.
    /// A copy an app has saved over since it was downloaded is the user's work, not cache: it
    /// is moved aside first (<see cref="CachedCopy"/>) and returned as <c>Kept</c>. Null when the
    /// download failed (the user has been told) or was cancelled — by the transfer's own cancel
    /// button, or through <paramref name="cancellationToken"/> once nobody waits for it any more
    /// (Quick Look moved on), when it also leaves the transfers list.
    /// </summary>
    private async Task<OpenedCopy?> CachedCopyAsync(PanelViewModel panel, CloudFileItem item, CancellationToken cancellationToken)
    {
        if (panel.Provider is not { } provider || panel.Location.AccountId is not { } accountId)
        {
            return null;
        }

        string requested, target;
        FileInfo cached;
        try
        {
            var folder = Path.Combine(StagingLocation.OpenCacheDirectory(accountId), Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(CloudPath.Parent(item.Path))))[..12]);
            Directory.CreateDirectory(folder);
            requested = SafeLocalPath.Destination(item.Name, folder);
            target = DownloadedFile.Locate(requested);
            cached = new FileInfo(target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The cache folder can't be written (a full disk, a custom cache folder gone missing).
            MessageDialog.Error(Shell.Window, L("Couldn't open \"{0}\"", item.Name), e.Message);
            return null;
        }

        DateTime? remoteModified = item.HasKnownModificationDate ? item.ModificationDate.UtcDateTime : null;
        var decision = cached.Exists
            ? CachedCopy.Decide(cached.Length, cached.LastWriteTimeUtc, item.Size, CachedCopy.StampOf(remoteModified, cached.CreationTimeUtc))
            : CachedCopyDecision.Refresh;
        string? kept = null;
        if (decision == CachedCopyDecision.PreserveEditsThenRefresh)
        {
            try
            {
                kept = CachedCopy.Preserve(target);
                SupportLogger.Shared.Log($"open: kept an edited copy of {item.Name} before downloading it again", "Open", LogLevel.Notice);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still held by the app it was saved in: never write over it. That app has the
                // user's version open, and this copy is what it shows.
                SupportLogger.Shared.Log($"open: an edited copy of {item.Name} is in use ({e.Message}); opening it as it is", "Open", LogLevel.Notice);
                return new OpenedCopy(target, null);
            }
        }

        if (decision != CachedCopyDecision.Reuse)
        {
            var failed = false;
            var progress = new TransferProgress(TransferOperation.Opening, 1, panel.Side);
            progress.SetItems([item]);
            progress.SetPlan(1, item.Size);
            progress.SetPhase(TransferPhase.Downloading);
            _services.Transfers.Track(progress);

            // Nobody waits any more (Quick Look moved on): stop the download as its own cancel
            // button would. The provider discards the partial file.
            using var abandon = cancellationToken.Register(progress.Cancel);
            try
            {
                progress.BeginFile(item.Name, item.Size);
                long reported = 0;
                await Task.Run(() => provider.DownloadFileAsync(item.Path, requested, d => { Interlocked.Add(ref reported, d); progress.AddDownloadBytes(d); }, progress.CancellationToken));
                progress.TopUp(false, reported, item.Size);
                target = DownloadedFile.Locate(requested);
                CachedCopy.Stamp(target, remoteModified);
                progress.FileFinished();
                progress.RecordSuccess(item.Name);
            }
            catch (OperationCanceledException)
            {
                failed = true;
            }
            catch (Exception) when (progress.IsCancelled)
            {
                // Some providers report a download torn down by cancelling as a network
                // failure; it was cancelled, which says nothing to anyone.
                failed = true;
            }
            catch (Exception e)
            {
                progress.RecordFailure(item.Name, e.Message);
                MessageDialog.Error(Shell.Window, L("Couldn't open \"{0}\"", item.Name), e.Message);
                failed = true;
            }
            finally
            {
                progress.Complete();

                // Given up by everyone who wanted it rather than cancelled by the user: an
                // arrow-key walk through cloud files in Quick Look leaves no trail of
                // "Cancelled" entries in the transfers list.
                if (cancellationToken.IsCancellationRequested)
                {
                    _services.Transfers.Remove(progress);
                }
            }

            if (failed)
            {
                if (kept is not null)
                {
                    // Nothing opens, but the edits were still moved: say where they are.
                    ShowKeptEdits(item.Name, kept);
                }

                return null;
            }
        }

        return new OpenedCopy(target, kept);
    }

    // MARK: - Transfers

    /// <summary>F5 / F6.</summary>
    public async Task TransferSelectionToOtherPanelAsync(bool isMove)
    {
        if (SinglePaneMode)
        {
            return;
        }

        var source = ActivePanel;
        var items = source.SelectedItems;
        if (items.Count == 0)
        {
            return;
        }

        await TransferAsync(source.Provider, items, OtherPanel.Location, isMove, OtherPanel.Side);
    }

    /// <summary>
    /// Copies or moves into a destination folder — every entry point (F5/F6, paste, drops onto
    /// rows, breadcrumbs, the sidebar) ends here. Shows progress in <paramref name="panel"/>'s sidebar.
    /// </summary>
    public async Task TransferAsync(FileProvider? source, IReadOnlyList<CloudFileItem> items, PanelLocation destination, bool isMove, PanelSide panel)
    {
        if (source is null || items.Count == 0)
        {
            return;
        }

        // Nothing moves out of an archive: it is read, never rewritten.
        if (source is Core.Archives.ArchiveProvider)
        {
            isMove = false;
        }

        if (destination.IsOfflineDrive || destination.IsArchive)
        {
            MessageDialog.Error(Shell.Window, L("Transfer"), L("This location is read-only."));
            return;
        }

        var target = _services.Hub.For(destination);
        if (target is null)
        {
            MessageDialog.Error(Shell.Window, L("Transfer"), L("Cloud account not connected"));
            return;
        }

        if (destination.AccountId is { } destinationId && _services.Accounts.Find(destinationId) is { } account &&
            (account.ProviderType.IsReadOnly() || account.IsOfflineMode))
        {
            MessageDialog.Error(Shell.Window, L("Transfer"), L("This location is read-only."));
            return;
        }

        if (_services.Settings.CheckSpaceBeforeTransfer && !await SpaceCheckPassesAsync(source, items, target, destination, isMove))
        {
            return;
        }

        var request = new TransferRequest(source, items, target, destination.Path, isMove)
        {
            // One question at a time across every running transfer, in the order asked; a
            // cancelled transfer's question is withdrawn or closed (macOS 1.6 ConflictPrompter).
            ResolveConflict = Conflicts.AskAsync,
        };
        if (source is Core.Archives.ArchiveProvider archive)
        {
            StartArchiveCopy(archive, request, panel, _ => RefreshAfterTransfer(source, destination));
            return;
        }

        _services.Transfers.Start(request, panel, _ => RefreshAfterTransfer(source, destination));
    }

    private ConflictPrompter? _conflicts;

    /// <summary>The app's one conflict question, shared by every transfer started here.</summary>
    private ConflictPrompter Conflicts => _conflicts ??= new ConflictPrompter((conflict, ct) =>
    {
        // Asked from the transfer's thread. Once FileFluss is closing there is nobody to
        // ask (and no window to own the question): stop instead of waiting forever.
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.HasShutdownStarted
            ? Task.FromResult(new ConflictResolution(ConflictChoice.Stop, false))
            : dispatcher.InvokeAsync(() => ConflictDialog.Ask(Shell.Window, conflict, ct)).Task;
    });

    private readonly List<CancellationTokenSource> _spaceChecks = [];

    /// <summary>True while a pre-flight space check is summing sizes (macOS <c>isCheckingSpace</c>).</summary>
    public bool IsCheckingSpace => _spaceChecks.Count > 0;

    /// <summary>The HUD's Cancel: abandons every running space check and the transfers waiting on them.</summary>
    public void CancelSpaceChecks()
    {
        foreach (var check in _spaceChecks.ToList())
        {
            check.Cancel();
        }
    }

    /// <summary>
    /// The pre-flight space check (macOS <c>SpaceCheck</c>): sums what is about to be copied
    /// and compares it with the destination's free space or quota.
    /// </summary>
    private async Task<bool> SpaceCheckPassesAsync(FileProvider source, IReadOnlyList<CloudFileItem> items, FileProvider target, PanelLocation destination, bool isMove)
    {
        if (isMove && ReferenceEquals(source, target))
        {
            return true;
        }

        // Summing a big tree can take a while: the window shows the "Checking available
        // space…" HUD meanwhile, whose Cancel abandons the transfer before it starts.
        long needed = 0;
        long? free;
        using var cancellation = new CancellationTokenSource();
        _spaceChecks.Add(cancellation);
        OnPropertyChanged(nameof(IsCheckingSpace));
        try
        {
            // On the thread pool, as a panel's listing is: a provider's first steps run
            // synchronously (credentials, a token refresh, an SSH handshake) and must not hold
            // the window while the HUD is meant to be showing.
            var token = cancellation.Token;
            needed = await Task.Run(async () =>
            {
                long sum = 0;
                foreach (var item in items)
                {
                    sum += item.IsDirectory ? await source.FolderSizeAsync(item.Path, token).ConfigureAwait(false) : item.Size;
                }

                return sum;
            }, token);
            free = await Task.Run(() => target.AvailableSpaceAsync(destination.Path, token), token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SupportLogger.Shared.Log("Space check cancelled; transfer not started", "Transfers");
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The check is advice, not a gate: a folder that can't be summed or a quota that
            // can't be read leaves the transfer to report its own problems, file by file.
            SupportLogger.Shared.Log($"Space check failed ({e.Message}); transferring without it", "Transfers", LogLevel.Notice);
            return true;
        }
        finally
        {
            _spaceChecks.Remove(cancellation);
            OnPropertyChanged(nameof(IsCheckingSpace));
        }

        if (cancellation.IsCancellationRequested)
        {
            return false;
        }

        if (free is not { } available || needed <= available)
        {
            return true;
        }

        return MessageDialog.Confirm(
            Shell.Window,
            L("Not enough space"),
            L("The items need {0}, but only {1} is available at the destination.", Core.Formatting.ByteFormatter.Format(needed), Core.Formatting.ByteFormatter.Format(available)),
            isMove ? L("Move Anyway") : L("Copy Anyway"));
    }

    private void RefreshAfterTransfer(FileProvider source, PanelLocation destination)
    {
        foreach (var panel in new[] { Left, Right })
        {
            if (ReferenceEquals(panel.Provider, source) || panel.Location.AccountId == destination.AccountId)
            {
                _ = panel.RefreshAsync(quiet: true);
            }
        }

        if (destination.AccountId is { } id)
        {
            _services.Accounts.InvalidateQuota(id);
        }
    }

    /// <summary>A drop needs a verb: the toolbar's Copy/Move Mode answers it, otherwise ask once.</summary>
    public bool? ResolveDropVerb(int count, string destinationName, bool? forcedMove)
    {
        if (forcedMove is { } forced)
        {
            return forced;
        }

        switch (_dragDropMode)
        {
            case DragDropMode.Copy:
                return false;
            case DragDropMode.Move:
                return true;
        }

        var answer = MessageDialog.Show(
            Shell.Window,
            L("Move or Copy?"),
            L("What would you like to do with {0} items in \"{1}\"?", count, destinationName),
            DialogIcon.Question,
            new DialogButton("cancel", L("Cancel"), IsCancel: true),
            new DialogButton("move", L("Move")),
            new DialogButton("copy", L("Copy"), IsDefault: true));
        return answer switch
        {
            "move" => true,
            "copy" => false,
            _ => null,
        };
    }

    // MARK: - Clipboard

    public void CaptureClipboard(PanelViewModel panel, bool isCut)
    {
        var items = panel.SelectedItems;
        if (items.Count == 0 || panel.Provider is null)
        {
            return;
        }

        if (isCut && panel.IsReadOnly)
        {
            return;
        }

        // Entries inside an archive have no file behind them yet: "Extract to … Panel" and
        // dragging are the ways out (macOS keeps them off the clipboard the same way).
        if (panel.IsArchive)
        {
            return;
        }

        Clipboard = new FileClipboard(panel.Provider, panel.Location, items, isCut);
        if (!panel.IsLocal)
        {
            // Copied cloud files go on the Windows clipboard as virtual files too, so Explorer
            // can paste them (downloading as it goes). A cut stays inside FileFluss: deleting
            // the cloud originals after another app's paste is not something to do on its word.
            if (!isCut && CloudFileExport.CanExport(panel))
            {
                _services.Export.CopyToClipboard(Shell?.Window, panel, items);
            }
            else
            {
                _services.Export.ReleaseClipboard(clear: true);
            }
        }
        else
        {
            _services.Export.ReleaseClipboard(clear: false);

            // Also on the Windows clipboard, so Explorer can paste what FileFluss copied.
            var paths = new StringCollection();
            paths.AddRange(items.Select(i => i.Path).ToArray());
            var data = new DataObject();
            data.SetFileDropList(paths);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(isCut ? 2 : 5)));
            try
            {
                System.Windows.Clipboard.SetDataObject(data, copy: true);
            }
            catch (System.Runtime.InteropServices.ExternalException e)
            {
                // Another app holds the clipboard open: Explorer can't paste these, FileFluss still can.
                SupportLogger.Shared.Log($"The Windows clipboard is busy: {e.Message}", "Clipboard", LogLevel.Notice);
            }
        }
    }

    public async Task PasteAsync(PanelViewModel panel)
    {
        // Menu accelerators and key handling can both deliver one Ctrl+V.
        if (DateTimeOffset.Now - _lastPaste < TimeSpan.FromMilliseconds(300))
        {
            return;
        }

        _lastPaste = DateTimeOffset.Now;
        if (panel.IsReadOnly || (panel.IsLocal && panel.CurrentPath.Length == 0))
        {
            return;
        }

        var external = ExternalClipboardFiles();

        // Cloud files FileFluss put on the Windows clipboard, since replaced by files copied in
        // Explorer: the newer copy wins, as it would anywhere else.
        var replaced = external.Count > 0 && _clipboard is { Source.IsLocal: false } && _services.Export.ClipboardWasReplaced;
        if (_clipboard is { } clip && !replaced && (external.Count == 0 || !panel.IsLocal || clip.Source.IsLocal == false || SameSet(external, clip.Items)))
        {
            var isCut = clip.IsCut;
            if (isCut)
            {
                // Cleared before the work, so a second Ctrl+V can't cut the same files again.
                Clipboard = null;
            }

            await TransferAsync(clip.Source, clip.Items, panel.Location, isCut, panel.Side);
            return;
        }

        if (external.Count > 0)
        {
            var items = new List<CloudFileItem>();
            foreach (var path in external)
            {
                try
                {
                    items.Add(await LocalFileProvider.Shared.GetFileMetadataAsync(path));
                }
                catch (Exception e) when (e is CloudProviderException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                }
            }

            await TransferAsync(LocalFileProvider.Shared, items, panel.Location, IsExternalCut(), panel.Side);
        }
    }

    private static bool SameSet(IReadOnlyList<string> paths, IReadOnlyList<CloudFileItem> items)
        => paths.Count == items.Count && paths.All(p => items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase)));

    private static List<string> ExternalClipboardFiles()
    {
        try
        {
            return System.Windows.Clipboard.ContainsFileDropList()
                ? System.Windows.Clipboard.GetFileDropList().Cast<string>().ToList()
                : [];
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return [];
        }
    }

    private static bool IsExternalCut()
    {
        try
        {
            if (System.Windows.Clipboard.GetData("Preferred DropEffect") is MemoryStream stream && stream.Length >= 4)
            {
                var buffer = new byte[4];
                stream.ReadExactly(buffer);
                return (BitConverter.ToInt32(buffer) & 2) != 0;
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }

        return false;
    }

    private void MarkCut(FileClipboard? clip, bool cut)
    {
        if (clip is not { IsCut: true })
        {
            return;
        }

        foreach (var panel in new[] { Left, Right })
        {
            if (panel.Location == clip.SourceFolder)
            {
                foreach (var item in clip.Items)
                {
                    if (panel.RowNamed(item.Name) is { } row)
                    {
                        row.IsCut = cut;
                    }
                }
            }
        }
    }

    public void CopyPaths(PanelViewModel panel)
    {
        var paths = panel.SelectedItems.Select(i => i.Path).ToList();
        if (paths.Count == 0)
        {
            paths.Add(panel.CurrentPath);
        }

        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, paths));
        }
        catch (System.Runtime.InteropServices.ExternalException e)
        {
            MessageDialog.Error(Shell.Window, L(KeyboardCommand.CopyPath.DisplayName()), e.Message);
        }
    }

    // MARK: - Create, rename, delete, duplicate

    public async Task NewFolderAsync(PanelViewModel panel)
    {
        if (!panel.CanCreateFolders || panel.Provider is not { } provider)
        {
            return;
        }

        var taken = panel.AllRows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var proposal = TransferEngine.UniqueName(L("New Folder"), taken);
        var name = MessageDialog.Prompt(Shell.Window, L("New Folder"), L("Name of the new folder:"), proposal, L("Create"));
        if (name is null)
        {
            return;
        }

        if (!IsValidName(name, panel.IsLocal, out var problem))
        {
            MessageDialog.Error(Shell.Window, L("New Folder"), problem);
            return;
        }

        try
        {
            await provider.CreateDirectoryAsync(provider.CombinePath(panel.CurrentPath, name));
            await panel.RefreshAsync(quiet: true);
            Shell.FocusPanel(panel.Side);
            SelectAfterLoad(panel, name);
        }
        catch (Exception e)
        {
            MessageDialog.Error(Shell.Window, L("New Folder"), e.Message);
        }
    }

    /// <summary>Raised to ask the panel's view to select a row by name after it reloaded.</summary>
    public event EventHandler<(PanelSide Side, string Name)>? SelectRequested;

    private void SelectAfterLoad(PanelViewModel panel, string name) => SelectRequested?.Invoke(this, (panel.Side, name));

    public async Task<bool> RenameAsync(PanelViewModel panel, FileRowViewModel row, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName == row.Name || panel.Provider is not { } provider)
        {
            return false;
        }

        if (!IsValidName(newName, panel.IsLocal, out var problem))
        {
            MessageDialog.Error(Shell.Window, L("Rename"), problem);
            return false;
        }

        try
        {
            await provider.RenameItemAsync(row.Path, newName);
            if (panel.TagScope is { } tagScope)
            {
                // The item's tags, and those of everything inside it, keep up with its name.
                _services.Tags.MoveSubtree(tagScope, row.Path, provider.CombinePath(provider.GetParentPath(row.Path), newName));
            }

            await panel.RefreshAsync(quiet: true);
            SelectAfterLoad(panel, newName);
            if (Panel(panel.Side.Other()) is { } other && other.Location == panel.Location)
            {
                await other.RefreshAsync(quiet: true);
            }

            return true;
        }
        catch (Exception e)
        {
            MessageDialog.Error(Shell.Window, L("Rename"), e.Message);
            return false;
        }
    }

    public static bool IsValidName(string name, bool isLocal, out string problem)
    {
        problem = string.Empty;
        if (name is "." or ".." || name.Contains('/') || (isLocal && name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            problem = isLocal
                ? L("A file name can't contain any of the following characters: {0}", "\\ / : * ? \" < > |")
                : L("A name can't contain \"/\".");
            return false;
        }

        // Trailing dots and spaces vanish and device names (CON, COM1…) open devices.
        if (isLocal && SafeLocalPath.FileName(name) != name)
        {
            problem = L("\"{0}\" isn't a valid name on Windows.", name);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Right-click → Tags. A panel showing the same folder on the other side follows.
    /// </summary>
    public async Task ChangeTagsAsync(PanelViewModel panel, IReadOnlyList<FileRowViewModel> rows, TagChange change)
    {
        var failure = await panel.ChangeTagsAsync(rows, change);
        var other = Panel(panel.Side.Other());
        if (other is not null && !ReferenceEquals(other, panel) && other.Location == panel.Location)
        {
            other.ReloadTags();
        }

        if (failure is { } failed)
        {
            MessageDialog.Error(Shell.Window, L("Tags"), L("The tags of “{0}” couldn't be changed: {1}", failed.Name, failed.Message));
        }
    }

    public async Task DeleteSelectionAsync(PanelViewModel panel)
    {
        var rows = panel.Selection.Where(r => r.Item.IsModifiable).ToList();
        if (rows.Count == 0 || panel.Provider is not { } provider || panel.IsReadOnly && !(panel.Account?.ProviderType.IsReadOnly() ?? false)
            || !(panel.Account?.ProviderType.AllowsDeletion() ?? true))
        {
            return;
        }

        var label = rows.Count == 1 ? $"\"{rows[0].Name}\"" : L("{0} items", rows.Count);
        // Once per volume, not per row: 20,000 selected files share one or two roots.
        var permanent = !panel.IsLocal || rows.Select(r => Path.GetPathRoot(r.Path) ?? r.Path).Distinct(StringComparer.OrdinalIgnoreCase)
            .Any(root => !ShellFileOperations.HasRecycleBin(root));
        if (permanent)
        {
            // Cloud deletions and volumes without a Recycle Bin can't be undone; always ask.
            var message = panel.IsLocal
                ? L("{0} can't be moved to the Recycle Bin because its drive doesn't have one. Delete permanently? This can't be undone.", label)
                : rows.Count == 1
                    ? L("Are you sure you want to delete \"{0}\" from {1}?", rows[0].Name, panel.Account?.DisplayName ?? string.Empty)
                    : L("Are you sure you want to delete {0} items from {1}?", rows.Count, panel.Account?.DisplayName ?? string.Empty);
            if (!MessageDialog.Confirm(Shell.Window, L("Delete"), message, L("Delete"), destructive: true))
            {
                return;
            }
        }
        else if (_services.Settings.ConfirmDelete &&
                 !MessageDialog.Confirm(Shell.Window, L("Move to Recycle Bin"),
                     rows.Count == 1 ? L("Are you sure you want to move \"{0}\" to the Recycle Bin?", rows[0].Name) : L("Are you sure you want to move {0} items to the Recycle Bin?", rows.Count),
                     L("Move to Recycle Bin")))
        {
            return;
        }

        var failures = new List<string>();
        var refused = new List<(FileRowViewModel Row, RecycleRefusal Refusal)>();
        var tagScope = panel.TagScope;
        await Task.Run(async () =>
        {
            foreach (var row in rows)
            {
                try
                {
                    if (panel.IsLocal && permanent)
                    {
                        LocalFileProvider.DeletePermanently(row.Path);
                    }
                    else
                    {
                        await provider.DeleteItemAsync(row.Path);
                    }

                    if (tagScope is not null)
                    {
                        _services.Tags.RemoveSubtree(tagScope, row.Path);
                    }
                }
                catch (RecycleRefusedException e) when (e.Refusal.Kind is not (RecycleRefusalKind.NotFound or RecycleRefusalKind.Declined))
                {
                    refused.Add((row, e.Refusal));
                }
                catch (RecycleRefusedException)
                {
                    // Already gone (moved or deleted outside FileFluss), or the user declined Windows'
                    // "delete permanently?" warning: the refresh below shows how things stand.
                }
                catch (Exception e)
                {
                    failures.Add($"{row.Name}: {e.Message}");
                }
            }
        });

        if (failures.Count > 0)
        {
            MessageDialog.Error(Shell.Window, L("Delete"), string.Join(Environment.NewLine, failures));
        }

        if (refused.Count > 0)
        {
            await ExplainRecycleRefusalAsync(refused);
        }

        if (panel.Location.AccountId is { } id)
        {
            _services.Accounts.InvalidateQuota(id);
        }

        await panel.RefreshAsync(quiet: true);
        if (OtherPanel.Location == panel.Location)
        {
            await OtherPanel.RefreshAsync(quiet: true);
        }
    }

    /// <summary>
    /// macOS 1.6 "a refused Trash names the real cause": says why the Recycle Bin refused —
    /// a file open elsewhere, a folder this user can't change, a damaged Recycle Bin — and
    /// offers what can help: Storage settings for a damaged bin, deleting outright where that
    /// may still work, nothing but the explanation where deleting needs the same access.
    /// </summary>
    private async Task ExplainRecycleRefusalAsync(IReadOnlyList<(FileRowViewModel Row, RecycleRefusal Refusal)> refused)
    {
        // One explanation for the lot, led by the most telling cause (the bin itself first).
        var refusal = refused.Select(r => r.Refusal).OrderBy(r => r.Kind switch
        {
            RecycleRefusalKind.RecycleBinDamaged => 0,
            RecycleRefusalKind.NoPermission => 1,
            RecycleRefusalKind.InUse => 2,
            _ => 3,
        }).First();
        var label = refused.Count == 1 ? $"\"{refused[0].Row.Name}\"" : L("{0} items", refused.Count);

        var buttons = new List<DialogButton>();
        if (refusal.Kind == RecycleRefusalKind.RecycleBinDamaged)
        {
            buttons.Add(new DialogButton("settings", L("Open Storage Settings…"), IsDefault: true));
        }

        if (refusal.OffersPermanentDelete)
        {
            buttons.Add(new DialogButton("delete", L("Delete Permanently"), IsDestructive: true));
            buttons.Add(new DialogButton("cancel", L("Cancel"), IsCancel: true));
        }
        else
        {
            buttons.Add(new DialogButton("ok", L("OK"), IsDefault: true, IsCancel: true));
        }

        // Cancel sits first, as everywhere else in the app's dialogs.
        buttons.Sort((a, b) => b.IsCancel.CompareTo(a.IsCancel));
        switch (MessageDialog.Show(Shell.Window, refusal.Title, refusal.Message(label), DialogIcon.Warning, [.. buttons]))
        {
            case "settings":
                try
                {
                    // A fixed system URI, not one from a server: the browser launcher (web URLs only) isn't the place.
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true })?.Dispose();
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    SupportLogger.Shared.Log($"Couldn't open Storage settings: {e.Message}", "fs", LogLevel.Notice);
                }

                break;
            case "delete":
                var failures = new List<string>();
                await Task.Run(() =>
                {
                    foreach (var (row, _) in refused)
                    {
                        try
                        {
                            LocalFileProvider.DeletePermanently(row.Path);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                            failures.Add($"{row.Name}: {e.Message}");
                        }
                    }
                });
                if (failures.Count > 0)
                {
                    MessageDialog.Error(Shell.Window, L("Delete"), string.Join(Environment.NewLine, failures));
                }

                break;
        }
    }

    private void DuplicateSelection(PanelViewModel panel)
    {
        if (panel.Provider is { } provider && panel.SelectedItems is { Count: > 0 } items && !panel.IsReadOnly)
        {
            _ = TransferAsync(provider, items, panel.Location, isMove: false, panel.Side);
        }
    }

    // MARK: - Panels

    public async Task SwapPanelsAsync()
    {
        var left = Left.Location;
        var right = Right.Location;

        // The way a folder is shown travels with it, as on macOS: a photo folder in a gallery
        // stays a gallery on the other side.
        (Left.ViewMode, Right.ViewMode) = (Right.ViewMode, Left.ViewMode);
        await Task.WhenAll(Left.NavigateAsync(right), Right.NavigateAsync(left));
    }

    /// <summary>
    /// Switches a panel between list and gallery. The panel becomes the active one and gets
    /// keyboard focus back: the list or gallery that had it has just been replaced, and the
    /// arrow keys would otherwise have nowhere to go.
    /// </summary>
    public void SetViewMode(PanelViewModel panel, FileListViewMode mode)
    {
        if (panel.ViewMode == mode)
        {
            return;
        }

        panel.ViewMode = mode;
        ActiveSide = panel.Side;
        Shell.FocusPanel(panel.Side);
    }

    /// <summary>
    /// "Map as Network Drive" (macOS "Mount in Finder"): serves the account through a local
    /// WebDAV server mapped to a drive letter. Null when it failed — the user has been told why.
    /// </summary>
    public async Task<MountInfo?> MountAccountAsync(CloudAccount account)
    {
        if (_services.Mount.MountFor(account.Id) is { } existing)
        {
            return existing;
        }

        if (_services.Hub.Get(account.Id) is not { } provider || account.IsOfflineMode)
        {
            MessageDialog.Error(Shell.Window, L("Could not mount in Finder"), L("Cloud account not connected"));
            return null;
        }

        var cursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            var mount = await _services.Mount.MountAsync(account, provider);

            // WebClient's 50 MB per-file cap surprises people; say so once.
            if (mount.FileSizeLimitNotice is { } notice && !_services.Settings.GetFlag("mount.sizeNoticeShown"))
            {
                _services.Settings.SetFlag("mount.sizeNoticeShown", true);
                MessageDialog.Info(Shell.Window, L("Mount in Finder"), notice);
            }

            return mount;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SupportLogger.Shared.Log($"Mapping {account.DisplayName} failed: {e.Message}", "Mount", LogLevel.Error);
            MessageDialog.Error(Shell.Window, L("Could not mount in Finder"), e.Message);
            return null;
        }
        finally
        {
            Mouse.OverrideCursor = cursor;
        }
    }

    /// <summary>Where a cloud path of a mapped account is on its drive: <c>X:\Photos\2026</c>.</summary>
    public static string DrivePathFor(MountInfo mount, string cloudPath)
    {
        var root = Core.IO.CloudPath.Normalize(mount.RootPath);
        var path = Core.IO.CloudPath.Normalize(cloudPath);
        var relative = Core.IO.CloudPath.IsWithin(path, root) ? path[root.TrimEnd('/').Length..] : path;
        return Path.Combine(mount.DrivePath, relative.TrimStart('/').Replace('/', '\\'));
    }

    private void OpenTerminal(PanelViewModel panel) => OpenTerminalAsync(panel).ReportFailure(L("Couldn't open a terminal"), Shell.Window);

    private async Task OpenTerminalAsync(PanelViewModel panel)
    {
        string? directory = null;

        // One selected folder opens that folder; anything else opens where the panel is.
        var folder = panel.Selection is [{ IsDirectory: true } only] ? only.Path : panel.CurrentPath;
        if (panel.IsLocal)
        {
            directory = folder;
        }
        else if (panel.Location.ArchiveContainer is { IsLocal: true } container)
        {
            // Inside an archive on this PC: the folder the archive sits in.
            directory = container.Path;
        }
        else if (panel.IsCloud && !panel.IsOffline && panel.Account is { } account)
        {
            // A cloud folder has a path a terminal can open once the account is a drive (macOS: mounted in Finder).
            var mount = _services.Mount.MountFor(account.Id);
            if (mount is null &&
                MessageDialog.Confirm(Shell.Window, L("Do you want to mount “{0}” in Finder?", account.DisplayName),
                    L("A terminal can only open a folder that exists on this Mac. Mounting this account adds it as a drive in Finder, and the terminal opens inside it."),
                    L("Mount in Finder")))
            {
                mount = await MountAccountAsync(account);
            }

            if (mount is null)
            {
                return;
            }

            directory = DrivePathFor(mount, folder);
        }

        if (string.IsNullOrEmpty(directory))
        {
            MessageDialog.Info(Shell.Window, L("Couldn't open a terminal"), L("Mount this account first — a cloud folder has no path a terminal can open."));
            return;
        }

        try
        {
            TerminalLauncher.Open(directory, _services.Settings.Terminal);
        }
        catch (Exception e)
        {
            MessageDialog.Error(Shell.Window, L("Couldn't open a terminal"), e.Message);
        }
    }

    private async Task OnAccountRemovedAsync(Guid id)
    {
        var home = PanelLocation.Local(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        foreach (var panel in new[] { Left, Right })
        {
            if (panel.Location.AccountId == id)
            {
                await panel.NavigateAsync(home);
            }
        }
    }

    public void NotifyLanguageChanged()
    {
        Left.NotifyLanguageChanged();
        Right.NotifyLanguageChanged();
        LeftSidebar.NotifyLanguageChanged();
        RightSidebar.NotifyLanguageChanged();
        OnAllPropertiesChanged();
    }
}
