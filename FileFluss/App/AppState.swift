import SwiftUI
import Combine

enum PanelSide: Hashable {
    case left, right
}

/// Forces drag & drop operations to a specific outcome, bypassing the
/// "Move or Copy?" confirmation dialog. `.ask` keeps the default prompt.
enum DragDropMode: String {
    case ask
    case copy
    case move
}

extension Notification.Name {
    // Aliased to the unified `KeyboardCommand.notification` names so a
    // single post from `FileCommands` reaches both the legacy
    // FileListView/CloudFileListView listeners and any new listener that
    // observes the command directly.
    static let menuNewFolder = KeyboardCommand.newFolder.notification
    static let menuRename = KeyboardCommand.rename.notification
    static let menuDelete = KeyboardCommand.deleteToTrash.notification
    static let menuCopyToOtherPanel = KeyboardCommand.copyToOtherPanel.notification
    static let menuMoveToOtherPanel = KeyboardCommand.moveToOtherPanel.notification

    /// Sent by AppState when the user triggers Compare via menu/shortcut.
    /// ContentView listens for it and calls `openWindow(id: "compare")`
    /// — `openWindow` is a SwiftUI environment value not reachable from
    /// AppState.
    static let requestShowCompareWindow = Notification.Name("FileFluss.requestShowCompareWindow")
}

@Observable @MainActor
final class AppState {
    var leftFileManager: FileManagerViewModel
    var rightFileManager: FileManagerViewModel
    var syncManager: SyncViewModel
    var searchVM = SearchViewModel()
    var showSyncSheet = false

    // Drive detection + background indexing. Both are singletons but exposed
    // here so SwiftUI views can observe them via the AppState environment.
    let driveMonitor = DriveMonitor.shared
    let indexingService = IndexingService.shared

    /// In-app file clipboard for Cmd+C / Cmd+X / Cmd+V. Tracks both the
    /// item URLs and whether it was a copy or cut so paste can move
    /// rather than copy when requested. Files captured by a cut are only
    /// deleted on a successful paste — if the user does Cmd+X but never
    /// pastes, the originals stay untouched.
    enum ClipboardOperation: String, Hashable {
        case copy
        case cut
    }

    struct FileClipboardSnapshot: Hashable {
        let urls: [URL]
        let operation: ClipboardOperation
    }

    /// Current clipboard contents from this app. Non-nil only when the
    /// user pressed Cmd+C or Cmd+X within FileFluss. The system pasteboard
    /// may also carry file URLs from other apps (e.g. Finder) — `pasteFiles`
    /// falls back to it when this is nil.
    var fileClipboard: FileClipboardSnapshot?

    /// Set while a paste is in flight so a second Cmd+V doesn't queue the
    /// same files a second time before the first completes. For copy
    /// operations the user can still re-paste once the current one
    /// finishes (Finder-style "duplicate at dest").
    private(set) var isPasteInProgress: Bool = false

    /// "Go to Folder…" dialog visibility, bound by ContentView.
    var showGoToFolder = false

    /// Per-cloud-account index summary, refreshed after every indexing run
    /// and when the Settings → Index Status panel appears. Keyed by account
    /// UUID. The summary type only carries the fields the UI needs.
    struct CloudIndexInfo: Hashable {
        let lastIndexed: Date
        let totalFiles: Int
        let totalFolders: Int
        let totalBytes: Int64
    }
    var cloudIndexInfo: [UUID: CloudIndexInfo] = [:]

    func refreshIndexInfo() async {
        var next: [UUID: CloudIndexInfo] = [:]
        for account in syncManager.accounts {
            if let s = await SearchIndex.shared.cloudAccountSummary(accountId: account.id) {
                next[account.id] = CloudIndexInfo(
                    lastIndexed: s.lastIndexed,
                    totalFiles: s.totalFiles,
                    totalFolders: s.totalFolders,
                    totalBytes: s.totalBytes
                )
            }
        }
        cloudIndexInfo = next
    }

    /// Bumped each time the user clicks Compare in the toolbar (or
    /// Compare-again in the compare window). The compare window observes
    /// this via `.task(id:)` and snapshots the current panels — so simply
    /// browsing folders while the compare window is open does not re-run
    /// the comparison.
    var compareTrigger: UUID = UUID()

    var selectedLeftSidebarItem: SidebarItem? = .location(
        FileManager.default.homeDirectoryForCurrentUser
    )
    var selectedRightSidebarItem: SidebarItem? = .location(
        FileManager.default.homeDirectoryForCurrentUser
    )

    var activePanel: PanelSide = .left

    /// When set to `.copy` or `.move`, drag & drop operations skip the
    /// confirmation dialog and run that operation directly. Session-only;
    /// resets to `.ask` on relaunch so the destructive Move Mode can't
    /// silently persist across sessions.
    var dragDropMode: DragDropMode = .ask

    var activeFileManager: FileManagerViewModel {
        activePanel == .left ? leftFileManager : rightFileManager
    }

    var isActivePanelCloud: Bool {
        cloudAccountId(for: activePanel) != nil
    }

    /// Returns the provider type of the active cloud panel, if any.
    var activePanelProviderType: CloudProviderType? {
        guard let accountId = cloudAccountId(for: activePanel) else { return nil }
        return syncManager.accountFor(id: accountId)?.providerType
    }

    /// Whether the active panel supports creating folders (WordPress doesn't).
    var canCreateFolderInActivePanel: Bool {
        if let providerType = activePanelProviderType {
            return providerType != .wordpress
        }
        return true // Local file system always supports it
    }

    var hasSelection: Bool {
        if isActivePanelCloud {
            if let cloudId = cloudAccountId(for: activePanel) {
                return !cloudFileManager(for: cloudId, side: activePanel).selectedItems.isEmpty
            }
            return false
        }
        return !activeFileManager.selectedItems.isEmpty
    }

    var hasSingleSelection: Bool {
        if isActivePanelCloud {
            if let cloudId = cloudAccountId(for: activePanel) {
                return cloudFileManager(for: cloudId, side: activePanel).selectedItems.count == 1
            }
            return false
        }
        return activeFileManager.selectedItems.count == 1
    }

    func fileManager(for panel: PanelSide) -> FileManagerViewModel {
        panel == .left ? leftFileManager : rightFileManager
    }

    func sidebarSelection(for panel: PanelSide) -> SidebarItem? {
        panel == .left ? selectedLeftSidebarItem : selectedRightSidebarItem
    }

    /// Returns the cloud account ID if the given panel is showing a cloud view, nil otherwise.
    func cloudAccountId(for panel: PanelSide) -> UUID? {
        switch sidebarSelection(for: panel) {
        case .cloudAccount(let account): return account.id
        case .cloudFolder(let accountId, _): return accountId
        default: return nil
        }
    }

    func setSidebarSelection(_ item: SidebarItem?, for panel: PanelSide) {
        if panel == .left { selectedLeftSidebarItem = item }
        else { selectedRightSidebarItem = item }
    }

    /// True when either panel is sitting on an offline source — an
    /// unmounted indexed drive, a disconnected cloud account, or an
    /// offline-folder selection opened from a search result. Used by the
    /// toolbar to disable Sync, since the offline snapshot has no live
    /// destination to write to.
    var hasOfflineSelection: Bool {
        isPanelOffline(.left) || isPanelOffline(.right)
    }

    func isPanelOffline(_ side: PanelSide) -> Bool {
        switch sidebarSelection(for: side) {
        case .cloudAccount(let account):
            return !account.isConnected
        case .cloudFolder(let accountId, _):
            return !(syncManager.accountFor(id: accountId)?.isConnected ?? false)
        case .drive(let driveId):
            return driveMonitor.mountURL(for: driveId) == nil
        case .offlineFolder:
            return true
        default:
            return false
        }
    }

    /// Refresh both panels (used after cross-panel move)
    func refreshAllPanels() async {
        await leftFileManager.refresh()
        await rightFileManager.refresh()
    }

    // Per-panel favorites — fully customizable, persisted across launches.
    // Each panel keeps its own ordered list, mixing local-folder and
    // cloud-folder entries. System defaults (Home / Desktop / etc.) are
    // seeded once on first run as ordinary entries, so the user can
    // remove or rename them just like any custom favorite.
    var leftFavorites: [SidebarFavorite] = []
    var rightFavorites: [SidebarFavorite] = []

    private static let leftFavoritesKey = "sidebarFavorites.left"
    private static let rightFavoritesKey = "sidebarFavorites.right"
    private static let favoritesInitializedKey = "sidebarFavorites.initialized"

    func favorites(for side: PanelSide) -> [SidebarFavorite] {
        side == .left ? leftFavorites : rightFavorites
    }

    func addLocalFavorite(url: URL, to side: PanelSide) {
        var favs = favorites(for: side)
        guard !favs.contains(where: { $0.kind == .localPath && $0.url == url }) else { return }
        favs.append(.local(name: url.lastPathComponent, icon: "folder.fill", url: url))
        write(favs, to: side)
    }

    func addCloudFavorite(accountId: UUID, path: String, name: String, to side: PanelSide) {
        var favs = favorites(for: side)
        guard !favs.contains(where: {
            $0.kind == .cloudFolder && $0.accountId == accountId && $0.cloudPath == path
        }) else { return }
        let account = syncManager.accountFor(id: accountId)
        let providerSuffix = account?.providerType.displayName ?? "Cloud"
        let displayName = "\(name) (\(providerSuffix))"
        favs.append(.cloud(
            name: displayName,
            accountId: accountId,
            path: path,
            providerType: account?.providerType ?? .pCloud
        ))
        write(favs, to: side)
    }

    func removeFavorite(id: UUID, from side: PanelSide) {
        var favs = favorites(for: side)
        favs.removeAll { $0.id == id }
        write(favs, to: side)
    }

    func renameFavorite(id: UUID, to newName: String, in side: PanelSide) {
        var favs = favorites(for: side)
        guard let idx = favs.firstIndex(where: { $0.id == id }) else { return }
        favs[idx].displayName = newName
        write(favs, to: side)
    }

    /// Replaces a favorite's icon. Accepts either an SF Symbol name or an
    /// `@asset:<asset-catalog-name>` reference (use `String.favoriteAssetIcon`
    /// to build one).
    func setFavoriteIcon(id: UUID, to icon: String, in side: PanelSide) {
        var favs = favorites(for: side)
        guard let idx = favs.firstIndex(where: { $0.id == id }) else { return }
        favs[idx].icon = icon
        write(favs, to: side)
    }

    func moveFavorites(in side: PanelSide, fromOffsets: IndexSet, toOffset: Int) {
        var favs = favorites(for: side)
        favs.move(fromOffsets: fromOffsets, toOffset: toOffset)
        write(favs, to: side)
    }

    private func write(_ favs: [SidebarFavorite], to side: PanelSide) {
        if side == .left { leftFavorites = favs } else { rightFavorites = favs }
        saveFavorites()
    }

    private func saveFavorites() {
        let defaults = UserDefaults.standard
        let encoder = JSONEncoder()
        if let leftData = try? encoder.encode(leftFavorites) {
            defaults.set(leftData, forKey: Self.leftFavoritesKey)
        }
        if let rightData = try? encoder.encode(rightFavorites) {
            defaults.set(rightData, forKey: Self.rightFavoritesKey)
        }
    }

    private func loadFavorites() {
        let defaults = UserDefaults.standard
        let decoder = JSONDecoder()

        if defaults.bool(forKey: Self.favoritesInitializedKey) {
            if let data = defaults.data(forKey: Self.leftFavoritesKey),
               let decoded = try? decoder.decode([SidebarFavorite].self, from: data) {
                leftFavorites = decoded
            }
            if let data = defaults.data(forKey: Self.rightFavoritesKey),
               let decoded = try? decoder.decode([SidebarFavorite].self, from: data) {
                rightFavorites = decoded
            }
            return
        }

        // First run after this update — seed both panels with the system
        // defaults but with independent UUIDs so reordering or removing
        // on one side doesn't affect the other.
        leftFavorites = SidebarFavorite.defaultLocalFavorites()
        rightFavorites = SidebarFavorite.defaultLocalFavorites()
        defaults.set(true, forKey: Self.favoritesInitializedKey)
        saveFavorites()
    }

    // Cloud file managers (cached per account and panel side, so the
    // same cloud account on both panels keeps independent navigation
    // state, just like local folders).
    private struct CloudFMKey: Hashable {
        let accountId: UUID
        let side: PanelSide
    }
    private var cloudFileManagers: [CloudFMKey: CloudFileManagerViewModel] = [:]

    func cloudFileManager(for accountId: UUID, side: PanelSide) -> CloudFileManagerViewModel {
        let key = CloudFMKey(accountId: accountId, side: side)
        if let existing = cloudFileManagers[key] {
            return existing
        }
        let providerType = syncManager.accountFor(id: accountId)?.providerType
        let vm = CloudFileManagerViewModel(accountId: accountId, providerType: providerType)
        cloudFileManagers[key] = vm
        return vm
    }

    /// Removes both panel sides' VMs for an account (used when an account
    /// is disconnected).
    func removeCloudFileManager(for accountId: UUID) {
        cloudFileManagers = cloudFileManagers.filter { $0.key.accountId != accountId }
    }

    // Cloud drag source tracking (for cross-panel drag from cloud to local
    // or between same-account panels). The side identifies which panel
    // initiated the drag so the source VM can be looked up correctly.
    var cloudDragSourceItems: [CloudFileItem] = []
    var cloudDragSourceAccountId: UUID?
    var cloudDragSourceSide: PanelSide?

    // Transfer progress per panel (shown in the sidebar of the destination panel)
    var leftTransfers: [TransferProgress] = []
    var rightTransfers: [TransferProgress] = []

    func transfers(for panel: PanelSide) -> [TransferProgress] {
        panel == .left ? leftTransfers : rightTransfers
    }

    func addTransfer(_ transfer: TransferProgress, panel: PanelSide) {
        if panel == .left {
            leftTransfers.append(transfer)
        } else {
            rightTransfers.append(transfer)
        }
    }

    func startTransfer(_ transfer: TransferProgress, panel: PanelSide, operation: @escaping @Sendable () async -> Void) {
        addTransfer(transfer, panel: panel)
        transfer.task = Task { await operation() }
    }

    func removeTransfer(id: UUID, panel: PanelSide) {
        if panel == .left {
            leftTransfers.removeAll { $0.id == id }
        } else {
            rightTransfers.removeAll { $0.id == id }
        }
    }

    // Folder size calculations per panel
    var leftFolderSizes: [FolderSizeEntry] = []
    var rightFolderSizes: [FolderSizeEntry] = []

    func folderSizes(for panel: PanelSide) -> [FolderSizeEntry] {
        panel == .left ? leftFolderSizes : rightFolderSizes
    }

    func calculateFolderSize(for url: URL, panel: PanelSide) {
        let entries = panel == .left ? leftFolderSizes : rightFolderSizes
        guard !entries.contains(where: { $0.url == url }) else { return }

        let entry = FolderSizeEntry(url: url)
        let entryId = entry.id
        if panel == .left {
            leftFolderSizes.append(entry)
        } else {
            rightFolderSizes.append(entry)
        }

        Task {
            do {
                let size = try await FileSystemService.shared.directorySize(at: url)
                updateFolderSizeEntry(id: entryId, panel: panel, size: size)
            } catch {
                updateFolderSizeEntry(id: entryId, panel: panel, size: 0)
            }
        }
    }

    func calculateCloudFolderSize(path: String, name: String, accountId: UUID, panel: PanelSide) {
        let entries = panel == .left ? leftFolderSizes : rightFolderSizes
        guard !entries.contains(where: { $0.cloudPath == path && $0.accountId == accountId }) else { return }

        let entry = FolderSizeEntry(cloudPath: path, accountId: accountId, name: name)
        let entryId = entry.id
        if panel == .left {
            leftFolderSizes.append(entry)
        } else {
            rightFolderSizes.append(entry)
        }

        Task {
            do {
                guard let provider = await SyncEngine.shared.provider(for: accountId) else {
                    throw CloudProviderError.notAuthenticated
                }
                let size = try await provider.folderSize(at: path)
                updateFolderSizeEntry(id: entryId, panel: panel, size: size)
            } catch {
                updateFolderSizeEntry(id: entryId, panel: panel, size: 0)
            }
        }
    }

    private func updateFolderSizeEntry(id: UUID, panel: PanelSide, size: Int64) {
        if panel == .left {
            if let idx = leftFolderSizes.firstIndex(where: { $0.id == id }) {
                leftFolderSizes[idx].size = size
                leftFolderSizes[idx].isCalculating = false
            }
        } else {
            if let idx = rightFolderSizes.firstIndex(where: { $0.id == id }) {
                rightFolderSizes[idx].size = size
                rightFolderSizes[idx].isCalculating = false
            }
        }
    }

    func removeFolderSize(id: UUID, panel: PanelSide) {
        if panel == .left {
            leftFolderSizes.removeAll { $0.id == id }
        } else {
            rightFolderSizes.removeAll { $0.id == id }
        }
    }

    // MARK: - Keyboard command routing

    /// Subscribes to every `KeyboardCommand.notification` we handle at the
    /// AppState level — anything that mutates panel state or routes a
    /// generic "act on the active panel" action. Commands that need
    /// view-local state (Quick Look, in-table rename, etc.) keep their
    /// existing per-view listeners.
    private func setupCommandHandlers() {
        let nc = NotificationCenter.default
        func on(_ cmd: KeyboardCommand, _ handler: @escaping @MainActor () -> Void) {
            nc.addObserver(forName: cmd.notification, object: nil, queue: .main) { _ in
                MainActor.assumeIsolated { handler() }
            }
        }

        // --- Panel navigation
        on(.parentDirectory) { [weak self] in
            guard let self else { return }
            Task { await self.navigateActiveParent() }
        }
        on(.historyBack) { [weak self] in
            guard let self else { return }
            Task { await self.navigateActiveBack() }
        }
        on(.historyForward) { [weak self] in
            guard let self else { return }
            Task { await self.navigateActiveForward() }
        }
        on(.jumpToRoot) { [weak self] in
            guard let self else { return }
            Task { await self.navigateActiveToRoot() }
        }
        on(.openInOtherPanel) { [weak self] in
            self?.openSelectionInOtherPanel()
        }
        on(.toggleActivePanel) { [weak self] in
            guard let self else { return }
            activePanel = (activePanel == .left) ? .right : .left
        }

        // --- Selection
        on(.deselectAll) { [weak self] in self?.activeClearSelection() }
        on(.invertSelection) { [weak self] in self?.activeInvertSelection() }

        // --- View / sort
        on(.sortByName) { [weak self] in self?.setActiveSortOrder(.name) }
        on(.sortByDate) { [weak self] in self?.setActiveSortOrder(.date) }
        on(.sortBySize) { [weak self] in self?.setActiveSortOrder(.size) }
        on(.sortByKind) { [weak self] in self?.setActiveSortOrder(.kind) }
        on(.toggleHiddenFiles) { [weak self] in self?.toggleActiveHiddenFiles() }

        // --- Cross-panel
        on(.swapPanels) { [weak self] in self?.swapPanelSelections() }
        on(.targetToSource) { [weak self] in self?.copyActivePathToOtherPanel() }
        on(.refreshActive) { [weak self] in
            guard let self else { return }
            Task { await self.refreshActivePanel() }
        }
        // Compare needs to open the dedicated Window scene, which only the
        // SwiftUI environment can do (openWindow). AppState bumps the
        // trigger so any already-open Compare window re-snapshots, AND
        // posts a separate "show compare window" request that ContentView
        // listens for to call openWindow.
        on(.compareFolders) { [weak self] in
            guard let self else { return }
            self.compareTrigger = UUID()
            NotificationCenter.default.post(name: .requestShowCompareWindow, object: nil)
        }

        // --- File operations / clipboard
        on(.copyPath) { [weak self] in self?.copyActiveSelectionPath() }
        on(.duplicate) { [weak self] in
            guard let self else { return }
            Task { await self.duplicateActiveSelection() }
        }
        on(.getInfo) { [weak self] in self?.revealActiveSelectionInFinder() }
        on(.copyFiles) { [weak self] in
            NSLog("[FileFluss] observer fired: copyFiles")
            self?.captureFileClipboard(operation: .copy)
        }
        on(.cutFiles) { [weak self] in
            NSLog("[FileFluss] observer fired: cutFiles")
            self?.captureFileClipboard(operation: .cut)
        }
        on(.pasteFiles) { [weak self] in
            NSLog("[FileFluss] observer fired: pasteFiles")
            guard let self else { return }
            Task { await self.pasteFileClipboard() }
        }

        // --- Navigation: Go to Folder dialog
        on(.focusPathBar) { [weak self] in self?.showGoToFolder = true }

        // --- Indexing
        on(.indexCurrentSource) { [weak self] in self?.indexActiveSource() }
    }

    // MARK: - Command implementations

    private func navigateActiveParent() async {
        if let id = cloudAccountId(for: activePanel) {
            let vm = cloudFileManager(for: id, side: activePanel)
            let parent = (vm.currentPath as NSString).deletingLastPathComponent
            await vm.navigateTo(parent.isEmpty ? "/" : parent)
        } else {
            await activeFileManager.navigateUp()
        }
    }

    private func navigateActiveBack() async {
        if let id = cloudAccountId(for: activePanel) {
            await cloudFileManager(for: id, side: activePanel).navigateBack()
        } else {
            await activeFileManager.navigateBack()
        }
    }

    private func navigateActiveForward() async {
        if let id = cloudAccountId(for: activePanel) {
            await cloudFileManager(for: id, side: activePanel).navigateForward()
        } else {
            await activeFileManager.navigateForward()
        }
    }

    private func navigateActiveToRoot() async {
        if let id = cloudAccountId(for: activePanel) {
            let account = syncManager.accountFor(id: id)
            let root = account?.rootPath.isEmpty == false ? account!.rootPath : "/"
            await cloudFileManager(for: id, side: activePanel).navigateTo(root)
        } else {
            await activeFileManager.navigateTo(URL(fileURLWithPath: "/"))
        }
    }

    private func openSelectionInOtherPanel() {
        let otherSide: PanelSide = activePanel == .left ? .right : .left
        if let id = cloudAccountId(for: activePanel) {
            let vm = cloudFileManager(for: id, side: activePanel)
            guard let item = vm.selectedItems.first, item.isDirectory else { return }
            setSidebarSelection(.cloudFolder(accountId: id, path: item.path), for: otherSide)
        } else {
            guard let item = activeFileManager.selectedItems.first, item.isDirectory else { return }
            setSidebarSelection(.location(item.url), for: otherSide)
        }
    }

    private func activeClearSelection() {
        if let id = cloudAccountId(for: activePanel) {
            cloudFileManager(for: id, side: activePanel).selectedItemIDs = []
        } else {
            activeFileManager.selectedItemIDs = []
        }
    }

    private func activeInvertSelection() {
        if let id = cloudAccountId(for: activePanel) {
            let vm = cloudFileManager(for: id, side: activePanel)
            let allIDs = Set(vm.filteredItems.map(\.id))
            vm.selectedItemIDs = allIDs.symmetricDifference(vm.selectedItemIDs)
        } else {
            let fm = activeFileManager
            let allIDs = Set(fm.filteredItems.map(\.id))
            fm.selectedItemIDs = allIDs.symmetricDifference(fm.selectedItemIDs)
        }
    }

    private func setActiveSortOrder(_ order: FileManagerViewModel.SortOrder) {
        if let id = cloudAccountId(for: activePanel) {
            let vm = cloudFileManager(for: id, side: activePanel)
            // Map between the two enums by raw value — both use the same
            // four cases ("name", "date", "size", "kind").
            if let mapped = CloudFileManagerViewModel.SortOrder(rawValue: order.rawValue) {
                vm.sortOrder = mapped
            }
        } else {
            activeFileManager.sortOrder = order
        }
    }

    private func toggleActiveHiddenFiles() {
        if let id = cloudAccountId(for: activePanel) {
            let vm = cloudFileManager(for: id, side: activePanel)
            vm.showHiddenFiles.toggle()
        } else {
            activeFileManager.showHiddenFiles.toggle()
            Task { await activeFileManager.refresh() }
        }
    }

    /// Captured state of one panel — local URL or (cloud account, path).
    /// Used by `swapPanelSelections` so the swap preserves the actual
    /// current folder rather than falling back to the sidebar root.
    private enum PanelSnapshot {
        case local(URL)
        case cloud(accountId: UUID, path: String)
    }

    private func snapshot(for side: PanelSide) -> PanelSnapshot {
        if let id = cloudAccountId(for: side) {
            let vm = cloudFileManager(for: id, side: side)
            return .cloud(accountId: id, path: vm.currentPath)
        }
        return .local(fileManager(for: side).currentDirectory)
    }

    private func apply(_ snapshot: PanelSnapshot, to side: PanelSide) {
        switch snapshot {
        case .local(let url):
            setSidebarSelection(.location(url), for: side)
            Task { await fileManager(for: side).navigateTo(url) }
        case .cloud(let id, let path):
            if let account = syncManager.accountFor(id: id) {
                let root = account.rootPath.isEmpty ? "/" : account.rootPath
                if path == root {
                    setSidebarSelection(.cloudAccount(account), for: side)
                } else {
                    setSidebarSelection(.cloudFolder(accountId: id, path: path), for: side)
                }
            } else {
                setSidebarSelection(.cloudFolder(accountId: id, path: path), for: side)
            }
            Task { await cloudFileManager(for: id, side: side).navigateTo(path) }
        }
    }

    private func swapPanelSelections() {
        let leftSnapshot = snapshot(for: .left)
        let rightSnapshot = snapshot(for: .right)
        apply(rightSnapshot, to: .left)
        apply(leftSnapshot, to: .right)
    }

    private func copyActivePathToOtherPanel() {
        let otherSide: PanelSide = activePanel == .left ? .right : .left
        if let id = cloudAccountId(for: activePanel) {
            let vm = cloudFileManager(for: id, side: activePanel)
            setSidebarSelection(.cloudFolder(accountId: id, path: vm.currentPath), for: otherSide)
        } else {
            setSidebarSelection(.location(activeFileManager.currentDirectory), for: otherSide)
        }
    }

    private func refreshActivePanel() async {
        if let id = cloudAccountId(for: activePanel) {
            await cloudFileManager(for: id, side: activePanel).refresh()
        } else {
            await activeFileManager.refresh()
        }
    }

    private func copyActiveSelectionPath() {
        let pb = NSPasteboard.general
        var paths: [String] = []
        if let id = cloudAccountId(for: activePanel) {
            paths = cloudFileManager(for: id, side: activePanel).selectedItems.map(\.path)
        } else {
            paths = activeFileManager.selectedItems.map { $0.url.path(percentEncoded: false) }
        }
        guard !paths.isEmpty else { return }
        pb.clearContents()
        pb.setString(paths.joined(separator: "\n"), forType: .string)
    }

    private func duplicateActiveSelection() async {
        // Only meaningful for the local panel — cloud providers don't have
        // a generic in-place copy primitive yet.
        guard cloudAccountId(for: activePanel) == nil else { return }
        let fm = activeFileManager
        for item in fm.selectedItems {
            let dir = item.url.deletingLastPathComponent()
            let nameNS = item.name as NSString
            let ext = nameNS.pathExtension
            let base = nameNS.deletingPathExtension
            var target = dir.appendingPathComponent(ext.isEmpty ? "\(base) copy" : "\(base) copy.\(ext)")
            var i = 2
            while FileManager.default.fileExists(atPath: target.path) {
                let suffix = ext.isEmpty ? "\(base) copy \(i)" : "\(base) copy \(i).\(ext)"
                target = dir.appendingPathComponent(suffix)
                i += 1
            }
            try? await FileSystemService.shared.copyItem(from: item.url, to: target)
        }
        await fm.refresh()
    }

    private func revealActiveSelectionInFinder() {
        // For local files only; cloud items don't have a Finder path.
        guard cloudAccountId(for: activePanel) == nil else { return }
        let urls = activeFileManager.selectedItems.map(\.url)
        guard !urls.isEmpty else { return }
        NSWorkspace.shared.activateFileViewerSelecting(urls)
    }

    // MARK: - File clipboard

    /// Snapshots the active panel's selected local files into the in-app
    /// clipboard and also writes them to NSPasteboard so the user can paste
    /// into Finder or other apps. Cloud selections aren't placed on the
    /// system pasteboard (no local URL); they're stashed in-app and the
    /// paste operation uploads/downloads as needed. Cut operations leave
    /// originals in place — they're removed only after a successful paste.
    func captureFileClipboard(operation: ClipboardOperation) {
        NSLog("[FileFluss] captureFileClipboard op=\(operation.rawValue) activePanel=\(activePanel) isCloud=\(isActivePanelCloud)")
        // Local panel — selection has real file URLs.
        if cloudAccountId(for: activePanel) == nil {
            let urls = activeFileManager.selectedItems.map(\.url)
            guard !urls.isEmpty else { return }
            fileClipboard = FileClipboardSnapshot(urls: urls, operation: operation)
            let pb = NSPasteboard.general
            pb.clearContents()
            pb.writeObjects(urls as [NSURL])
            return
        }
        // Cloud panel — only support cut/copy within FileFluss for now;
        // pasteboard would have no useful URL representation.
        guard let accountId = cloudAccountId(for: activePanel) else { return }
        let vm = cloudFileManager(for: accountId, side: activePanel)
        guard !vm.selectedItems.isEmpty else { return }
        // Wrap paths in URLs with a custom scheme so paste can decode them.
        let urls: [URL] = vm.selectedItems.compactMap { item in
            var components = URLComponents()
            components.scheme = "filefluss-cloud"
            components.host = accountId.uuidString
            components.path = item.path.hasPrefix("/") ? item.path : "/" + item.path
            return components.url
        }
        guard !urls.isEmpty else { return }
        fileClipboard = FileClipboardSnapshot(urls: urls, operation: operation)
    }

    func pasteFileClipboard() async {
        NSLog("[FileFluss] pasteFileClipboard activePanel=\(activePanel) hasClipboard=\(fileClipboard != nil)")
        // Re-entrancy guard — a second Cmd+V while the first paste is still
        // running would otherwise re-process the same clipboard contents.
        guard !isPasteInProgress else {
            NSLog("[FileFluss] pasteFileClipboard ignored: paste already in flight")
            return
        }
        isPasteInProgress = true
        defer { isPasteInProgress = false }
        // Prefer the in-app clipboard (preserves cut state). If empty, fall
        // back to the system pasteboard so files copied from Finder paste in.
        let snapshot: FileClipboardSnapshot
        if let local = fileClipboard {
            snapshot = local
        } else if let pbUrls = NSPasteboard.general.readObjects(forClasses: [NSURL.self]) as? [URL],
                  !pbUrls.isEmpty {
            snapshot = FileClipboardSnapshot(urls: pbUrls, operation: .copy)
        } else {
            return
        }

        let isCut = snapshot.operation == .cut
        let localURLs = snapshot.urls.filter { $0.isFileURL }
        let cloudURLs = snapshot.urls.filter { $0.scheme == "filefluss-cloud" }

        // Clear the cut clipboard NOW (before the async work) so a second
        // Cmd+V on the way doesn't accidentally cut the same files again
        // after the first paste finishes deleting the originals.
        if isCut { fileClipboard = nil }

        // Destination — active panel.
        if let destAccountId = cloudAccountId(for: activePanel) {
            // Pasting into cloud panel.
            let destVM = cloudFileManager(for: destAccountId, side: activePanel)
            let leftFM = leftFileManager
            let rightFM = rightFileManager
            if !localURLs.isEmpty {
                let transfer = TransferProgress(operation: isCut ? "Moving" : "Uploading", totalItems: localURLs.count)
                addTransfer(transfer, panel: activePanel)
                transfer.task = Task { [destVM] in
                    await destVM.uploadFiles(from: localURLs, progress: transfer)
                    if isCut && !transfer.hasErrors {
                        // Only remove originals after a clean upload, so
                        // a partial failure never silently destroys data.
                        for url in localURLs {
                            try? await FileSystemService.shared.deleteItem(at: url)
                        }
                        // Refresh whichever local panel(s) held the source.
                        await leftFM.refresh()
                        await rightFM.refresh()
                    }
                    await destVM.refresh()
                }
            }
            // Cloud-to-cloud paste — uses raw provider methods so the
            // single TransferProgress is the only place item counts /
            // bytes are recorded. Going through the VM `downloadItems` +
            // `uploadFiles` would double-write item results (the screen
            // bug where the same filename appeared twice and progress
            // showed 50% after completion).
            if !cloudURLs.isEmpty {
                var byAccount: [UUID: [(remotePath: String, name: String)]] = [:]
                for url in cloudURLs {
                    guard let host = url.host,
                          let sourceAccountId = UUID(uuidString: host) else { continue }
                    let remotePath = url.path
                    let name = (remotePath as NSString).lastPathComponent
                    byAccount[sourceAccountId, default: []].append((remotePath, name))
                }
                for (sourceAccountId, sourceItems) in byAccount {
                    let destAccount = destAccountId
                    let panelForRefresh = activePanel
                    let isCutCopy = isCut
                    Task { @MainActor in
                        await self.runCloudToCloudPaste(
                            sourceAccountId: sourceAccountId,
                            destAccountId: destAccount,
                            destPath: destVM.currentPath,
                            sourceItems: sourceItems,
                            isCut: isCutCopy,
                            destPanel: panelForRefresh
                        )
                    }
                }
            }
        } else {
            // Pasting into local panel.
            let destFM = activeFileManager
            if !localURLs.isEmpty {
                let transfer = TransferProgress(operation: isCut ? "Moving" : "Copying", totalItems: localURLs.count)
                addTransfer(transfer, panel: activePanel)
                let destDir = destFM.currentDirectory
                transfer.task = Task { [destFM] in
                    if isCut {
                        await destFM.performMove(items: localURLs.map { FileItem(url: $0) }, to: destDir, progress: transfer)
                    } else {
                        await destFM.performCopy(items: localURLs.map { FileItem(url: $0) }, to: destDir, progress: transfer)
                    }
                    await destFM.refresh()
                }
            }
            // Cloud-to-local paste: group by account so each account uses
            // its own VM, and use the VM's batch downloadItems.
            if !cloudURLs.isEmpty {
                var byAccount: [UUID: [CloudFileItem]] = [:]
                for url in cloudURLs {
                    guard let host = url.host, let accountId = UUID(uuidString: host) else { continue }
                    let remotePath = url.path
                    let item = CloudFileItem(
                        id: remotePath,
                        name: (remotePath as NSString).lastPathComponent,
                        path: remotePath,
                        isDirectory: false,
                        size: 0,
                        modificationDate: Date(),
                        checksum: nil
                    )
                    byAccount[accountId, default: []].append(item)
                }
                for (accountId, items) in byAccount {
                    let sourceVM = cloudFileManager(for: accountId, side: activePanel)
                    let transfer = TransferProgress(operation: isCut ? "Moving" : "Downloading", totalItems: items.count)
                    addTransfer(transfer, panel: activePanel)
                    let destDir = destFM.currentDirectory
                    transfer.task = Task { [sourceVM, destFM] in
                        await sourceVM.downloadItems(items, to: destDir, progress: transfer)
                        if isCut, !transfer.hasErrors {
                            // Only remove originals once the download
                            // landed cleanly — otherwise we'd lose data.
                            await sourceVM.deleteItems(items)
                            await sourceVM.refresh()
                        }
                        await destFM.refresh()
                    }
                }
            }
        }
    }

    /// Performs a robust cloud→cloud transfer for one source account.
    /// Uses raw provider download/upload so the single TransferProgress is
    /// the only place item-level success/failure and byte counters are
    /// recorded — going through the VM batch methods double-bookkeeps and
    /// produces the duplicate-row / wrong-percentage artifact the user
    /// reported. On cut, source items are removed only after their upload
    /// landed cleanly, so a partial failure never silently deletes data.
    @MainActor
    private func runCloudToCloudPaste(
        sourceAccountId: UUID,
        destAccountId: UUID,
        destPath: String,
        sourceItems: [(remotePath: String, name: String)],
        isCut: Bool,
        destPanel: PanelSide
    ) async {
        guard let sourceProvider = await SyncEngine.shared.provider(for: sourceAccountId),
              let destProvider = await SyncEngine.shared.provider(for: destAccountId) else {
            return
        }
        let sourceVM = cloudFileManager(for: sourceAccountId, side: destPanel)
        let destVM = cloudFileManager(for: destAccountId, side: destPanel)

        let transfer = TransferProgress(operation: isCut ? "Moving" : "Copying",
                                        totalItems: sourceItems.count)
        transfer.isCloudToCloud = true
        transfer.totalFiles = sourceItems.count
        addTransfer(transfer, panel: destPanel)

        let tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("filefluss-paste-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)

        let task = Task.detached(priority: .userInitiated) {
            // Best-effort pre-size lookup so the progress bar can show a
            // meaningful percentage. We pre-fetch metadata for each file
            // (often a cheap operation) and total it up.
            var expectedTotal: Int64 = 0
            var sized: [(path: String, name: String, size: Int64)] = []
            for item in sourceItems {
                if Task.isCancelled { break }
                if let meta = try? await sourceProvider.getFileMetadata(at: item.remotePath) {
                    expectedTotal += meta.size
                    sized.append((item.remotePath, item.name, meta.size))
                } else {
                    sized.append((item.remotePath, item.name, 0))
                }
            }
            await MainActor.run {
                transfer.expectedBytesDownload = expectedTotal
                transfer.expectedBytesUpload = expectedTotal
                transfer.currentPhase = .downloading
                transfer.downloadStartTime = Date()
            }

            // For each item: download → upload → (cut) delete source.
            // Doing them serially keeps the transfer ordering predictable
            // and avoids hammering both providers in parallel; for typical
            // file counts this is the simpler, more reliable choice.
            var successfulSources: [(path: String, name: String)] = []
            for item in sized {
                if Task.isCancelled { break }
                await MainActor.run { transfer.currentFileName = item.name }

                let tempURL = tempDir.appendingPathComponent(item.name)
                try? FileManager.default.removeItem(at: tempURL)

                // --- Download phase
                do {
                    try await sourceProvider.downloadFile(
                        remotePath: item.path,
                        to: tempURL,
                        onBytes: { bytes in
                            Task { @MainActor in transfer.addDownloadBytes(bytes) }
                        }
                    )
                } catch {
                    await MainActor.run {
                        transfer.recordFailure(item.name, error: "Download failed: \(error.localizedDescription)")
                        transfer.completedItems += 1
                    }
                    continue
                }
                await MainActor.run { transfer.totalBytes += item.size }

                // --- Upload phase
                await MainActor.run {
                    transfer.currentPhase = .uploading
                    if transfer.uploadStartTime == nil { transfer.uploadStartTime = Date() }
                }
                let dstPath: String = {
                    let root = destPath.hasSuffix("/") ? String(destPath.dropLast()) : destPath
                    if root.isEmpty { return "/" + item.name }
                    return root + "/" + item.name
                }()
                do {
                    try await destProvider.uploadFile(
                        from: tempURL,
                        to: dstPath,
                        onBytes: { bytes in
                            Task { @MainActor in transfer.addUploadBytes(bytes) }
                        }
                    )
                    await MainActor.run {
                        transfer.recordSuccess(item.name)
                        transfer.transferredFileNames.append(item.name)
                        transfer.completedItems += 1
                    }
                    successfulSources.append((item.path, item.name))
                } catch {
                    await MainActor.run {
                        transfer.recordFailure(item.name, error: "Upload failed: \(error.localizedDescription)")
                        transfer.completedItems += 1
                    }
                }
                try? FileManager.default.removeItem(at: tempURL)

                // Flip back to downloading for the next item, so the
                // dual-phase progress visualisation stays in sync.
                await MainActor.run { transfer.currentPhase = .downloading }
            }

            try? FileManager.default.removeItem(at: tempDir)

            // --- Delete originals (cut only, only the ones that uploaded)
            if isCut && !successfulSources.isEmpty {
                let items = successfulSources.map { src in
                    CloudFileItem(
                        id: src.path,
                        name: src.name,
                        path: src.path,
                        isDirectory: false,
                        size: 0,
                        modificationDate: Date(),
                        checksum: nil
                    )
                }
                await sourceVM.deleteItems(items)
            }

            await MainActor.run {
                transfer.downloadEndTime = transfer.downloadEndTime ?? Date()
                transfer.uploadEndTime = Date()
                transfer.isComplete = true
                transfer.endTime = Date()
            }

            // Refresh both source and destination panels so the user sees
            // the moved/copied files immediately.
            await sourceVM.refresh()
            await destVM.refresh()
        }
        transfer.task = task
    }

    // MARK: - Go to Folder

    /// Resolves a user-typed path and navigates the active panel there.
    /// Cloud panels stay in the cloud (path is interpreted as a remote path
    /// rooted at "/"). Local panels — including external drives, which are
    /// just local mount points — expand `~` and resolve relative paths
    /// against the panel's current directory.
    func goToFolder(_ rawPath: String) {
        let trimmed = rawPath.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return }

        // Cloud panel: navigate the cloud VM along the (remote) path.
        if let accountId = cloudAccountId(for: activePanel) {
            let vm = cloudFileManager(for: accountId, side: activePanel)
            let target: String
            if trimmed.hasPrefix("/") {
                target = trimmed
            } else {
                // Treat as relative to the cloud VM's current path.
                let base = vm.currentPath.hasSuffix("/") ? vm.currentPath : vm.currentPath + "/"
                target = base + trimmed
            }
            if case .cloudAccount(let account) = sidebarSelection(for: activePanel),
               target == (account.rootPath.isEmpty ? "/" : account.rootPath) {
                // Already at root — leave sidebar selection as-is.
            } else {
                setSidebarSelection(.cloudFolder(accountId: accountId, path: target), for: activePanel)
            }
            Task { await vm.navigateTo(target) }
            return
        }

        // Local panel (or external drive mount).
        let expanded = (trimmed as NSString).expandingTildeInPath
        var resolved = expanded
        if !resolved.hasPrefix("/") {
            let base = activeFileManager.currentDirectory.path
            resolved = (base as NSString).appendingPathComponent(resolved)
        }
        let url = URL(fileURLWithPath: resolved)
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue else {
            return
        }
        setSidebarSelection(.location(url), for: activePanel)
        Task { await fileManager(for: activePanel).navigateTo(url) }
    }

    private func indexActiveSource() {
        // Drive case: active panel is on .drive() and mounted.
        if case .drive(let driveId) = sidebarSelection(for: activePanel),
           let drive = driveMonitor.drives.first(where: { $0.id == driveId }),
           let mount = driveMonitor.mountURL(for: driveId) {
            indexingService.indexDrive(drive, mountURL: mount)
            return
        }
        // Cloud case: active panel is on a connected cloud account.
        if let id = cloudAccountId(for: activePanel),
           let account = syncManager.accountFor(id: id),
           account.isConnected {
            indexingService.indexCloudAccount(account)
        }
    }

    private static func runCacheMaintenanceIfEnabled() async {
        let defaults = UserDefaults.standard
        // Default ON if the key has never been written.
        let enabled = defaults.object(forKey: "cacheAutoManage") as? Bool ?? true
        guard enabled else {
            await CacheManager.shared.refreshSize()
            return
        }
        let days = (defaults.object(forKey: "cacheAutoDeleteDays") as? Int) ?? CacheManager.defaultAutoDeleteDays
        let sizeMB = (defaults.object(forKey: "cacheMaxSizeMB") as? Int) ?? CacheManager.defaultMaxSizeMB
        await CacheManager.shared.runAutoMaintenance(maxAgeDays: days, maxSizeMB: sizeMB)
    }

    init() {
        self.leftFileManager = FileManagerViewModel()
        self.rightFileManager = FileManagerViewModel()
        self.syncManager = SyncViewModel()
        loadFavorites()

        NotificationCenter.default.addObserver(
            forName: .indexingDidFinish, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                Task { @MainActor in
                    await self.refreshIndexInfo()
                }
            }
        }

        setupCommandHandlers()

        Task {
            await syncManager.reconnectSavedAccounts()
            try? await SearchIndex.shared.open()
            await refreshIndexInfo()
            await Self.runCacheMaintenanceIfEnabled()
        }
    }
}

/// A single sidebar favorite — either a local folder or a cloud folder.
/// Both kinds live in the same per-panel array so the user can reorder
/// them, rename them, or remove them uniformly.
struct SidebarFavorite: Identifiable, Hashable, Codable {
    enum Kind: String, Codable, Hashable {
        case localPath
        case cloudFolder
    }

    var id: UUID
    var kind: Kind
    var displayName: String
    var icon: String

    // Set when kind == .localPath
    var url: URL?

    // Set when kind == .cloudFolder
    var accountId: UUID?
    var cloudPath: String?
    var providerType: CloudProviderType?

    init(
        id: UUID = UUID(),
        kind: Kind,
        displayName: String,
        icon: String,
        url: URL? = nil,
        accountId: UUID? = nil,
        cloudPath: String? = nil,
        providerType: CloudProviderType? = nil
    ) {
        self.id = id
        self.kind = kind
        self.displayName = displayName
        self.icon = icon
        self.url = url
        self.accountId = accountId
        self.cloudPath = cloudPath
        self.providerType = providerType
    }

    static func local(name: String, icon: String, url: URL) -> SidebarFavorite {
        SidebarFavorite(kind: .localPath, displayName: name, icon: icon, url: url)
    }

    static func cloud(name: String, accountId: UUID, path: String, providerType: CloudProviderType) -> SidebarFavorite {
        SidebarFavorite(
            kind: .cloudFolder,
            displayName: name,
            icon: "cloud.fill",
            accountId: accountId,
            cloudPath: path,
            providerType: providerType
        )
    }

    /// The system defaults seeded into both panels on first run. These
    /// are ordinary entries — users can reorder, rename, or remove them.
    static func defaultLocalFavorites() -> [SidebarFavorite] {
        let home = FileManager.default.homeDirectoryForCurrentUser
        return [
            .local(name: "Home", icon: "house", url: home),
            .local(name: "Desktop", icon: "menubar.dock.rectangle", url: home.appendingPathComponent("Desktop")),
            .local(name: "Documents", icon: "doc", url: home.appendingPathComponent("Documents")),
            .local(name: "Downloads", icon: "arrow.down.circle", url: home.appendingPathComponent("Downloads")),
            .local(name: "Pictures", icon: "photo", url: home.appendingPathComponent("Pictures")),
            .local(name: "Music", icon: "music.note", url: home.appendingPathComponent("Music")),
        ]
    }
}

@available(*, deprecated, message: "Replaced by SidebarFavorite. Kept temporarily for one transitional release.")
struct FavoriteFolder: Identifiable {
    let id = UUID()
    let url: URL
    var displayName: String
    let icon: String = "folder.fill"
}

struct FolderSizeEntry: Identifiable {
    let id = UUID()
    let url: URL?
    let cloudPath: String?
    let accountId: UUID?
    var size: Int64?
    var isCalculating: Bool = true

    init(url: URL) {
        self.url = url
        self.cloudPath = nil
        self.accountId = nil
    }

    init(cloudPath: String, accountId: UUID, name: String) {
        self.url = nil
        self.cloudPath = cloudPath
        self.accountId = accountId
        self._name = name
    }

    private var _name: String?

    var name: String { _name ?? url?.lastPathComponent ?? "Unknown" }

    var formattedSize: String {
        guard let size else { return "Calculating…" }
        return ByteCountFormatter.string(fromByteCount: size, countStyle: .file)
    }
}

struct CloudFavorite: Identifiable {
    let id = UUID()
    let accountId: UUID
    let path: String
    var displayName: String
    let providerType: CloudProviderType
    let icon: String = "cloud.fill"
}

struct TransferItemResult: Identifiable, Hashable {
    enum Status: Hashable { case succeeded, failed, skipped, cancelled }

    let id = UUID()
    let name: String
    let status: Status
    let errorMessage: String?
}

@Observable @MainActor
final class TransferProgress: Identifiable {
    let id = UUID()
    let operation: String  // "Copying", "Moving", "Downloading"
    let totalItems: Int
    var completedItems: Int = 0
    var totalFiles: Int = 0 // actual file count discovered during recursive traversal
    var currentFileName: String = ""
    var isComplete: Bool = false
    /// Set when the entire transfer fails before any per-item work could be
    /// done (e.g. provider unavailable). For per-item failures during the
    /// loop, use `itemResults` instead.
    var errorMessage: String?
    /// Per-item outcomes (one entry per top-level item attempted).
    var itemResults: [TransferItemResult] = []
    var transferredFileNames: [String] = []
    var totalBytes: Int64 = 0
    let startTime = Date()
    var endTime: Date?

    func recordSuccess(_ name: String) {
        itemResults.append(TransferItemResult(name: name, status: .succeeded, errorMessage: nil))
    }

    func recordFailure(_ name: String, error: String) {
        itemResults.append(TransferItemResult(name: name, status: .failed, errorMessage: error))
    }

    func recordSkip(_ name: String) {
        itemResults.append(TransferItemResult(name: name, status: .skipped, errorMessage: nil))
    }

    var successCount: Int { itemResults.lazy.filter { $0.status == .succeeded }.count }
    var failureCount: Int { itemResults.lazy.filter { $0.status == .failed }.count }
    var skippedCount: Int { itemResults.lazy.filter { $0.status == .skipped }.count }

    /// True when the transfer ended with at least one item failing. Reflects
    /// both fatal `errorMessage` failures and per-item failures recorded via
    /// `recordFailure`.
    var hasErrors: Bool { errorMessage != nil || failureCount > 0 }

    // Byte-weighted progress
    /// Expected bytes for the download phase (local→cloud uploads: 0; cloud→cloud: source bytes).
    var expectedBytesDownload: Int64 = 0
    /// Expected bytes for the upload phase (cloud→local downloads: 0; cloud→cloud: source bytes).
    var expectedBytesUpload: Int64 = 0
    /// Expected bytes for a single-phase transfer (download OR upload).
    var expectedBytesSingle: Int64 = 0

    /// Task running this transfer, set by the caller after `Task { ... }` is created.
    /// Call `cancel()` to request cancellation.
    var task: Task<Void, Never>?
    var isCancelled: Bool = false

    func cancel() {
        isCancelled = true
        task?.cancel()
    }

    // Cloud-to-cloud phase tracking
    var isCloudToCloud: Bool = false
    var currentPhase: TransferPhase = .downloading
    var downloadBytes: Int64 = 0
    var uploadBytes: Int64 = 0
    var downloadStartTime: Date?
    var downloadEndTime: Date?
    var uploadStartTime: Date?
    var uploadEndTime: Date?

    // Transfer direction for speed display
    var isCloudDownload: Bool = false
    var isCloudUpload: Bool = false

    enum TransferPhase {
        case downloading, uploading
    }

    init(operation: String, totalItems: Int) {
        self.operation = operation
        self.totalItems = totalItems
    }

    var fraction: Double {
        if isCloudToCloud {
            let totalExpected = expectedBytesDownload + expectedBytesUpload
            if totalExpected > 0 {
                let transferred = min(downloadBytes + uploadBytes, totalExpected)
                return Double(transferred) / Double(totalExpected)
            }
            // Fall back to file-count halves until sizes are known
            let effectiveTotal = totalFiles > 0 ? totalFiles : totalItems
            guard effectiveTotal > 0 else { return 0 }
            let half = Double(effectiveTotal)
            switch currentPhase {
            case .downloading: return Double(completedItems) / (half * 2)
            case .uploading: return (half + Double(completedItems)) / (half * 2)
            }
        }

        // Single-phase transfer — prefer byte-weighted if we have expected bytes.
        if expectedBytesSingle > 0 {
            let transferred = isCloudUpload ? uploadBytes : downloadBytes
            let clamped = min(transferred, expectedBytesSingle)
            return Double(clamped) / Double(expectedBytesSingle)
        }

        let effectiveTotal = totalFiles > 0 ? totalFiles : totalItems
        guard effectiveTotal > 0 else { return 0 }
        return Double(completedItems) / Double(effectiveTotal)
    }

    /// Percentage string for display inside the progress bar (e.g. "42%").
    var percentText: String {
        "\(Int((fraction * 100).rounded()))%"
    }

    /// Thread-safe-ish byte accumulator for the current phase.
    /// Call from @MainActor contexts only.
    func addDownloadBytes(_ delta: Int64) {
        guard delta > 0 else { return }
        downloadBytes += delta
    }

    func addUploadBytes(_ delta: Int64) {
        guard delta > 0 else { return }
        uploadBytes += delta
    }

    var statusText: String {
        if isComplete { return completionSummary }
        if isCancelled { return "Cancelling…" }
        if isCloudToCloud {
            let names = transferredFileNames
            let label = names.count == 1 ? names[0] : "\(names.count) items"
            switch currentPhase {
            case .downloading:
                return "Downloading \(label)"
            case .uploading:
                return "Uploading \(label)"
            }
        }
        return "\(operation) \(itemSummary) — \(completedItems) of \(totalFiles > 0 ? totalFiles : totalItems) files"
    }

    private var itemSummary: String {
        let names = transferredFileNames
        if names.count == 1 {
            return names[0]
        }
        let folders = names.filter { transferredFolderNames.contains($0) }
        let files = names.filter { !transferredFolderNames.contains($0) }
        var parts: [String] = []
        if !folders.isEmpty {
            parts.append("\(folders.count) \(folders.count == 1 ? "folder" : "folders")")
        }
        if !files.isEmpty {
            parts.append("\(files.count) \(files.count == 1 ? "file" : "files")")
        }
        return parts.joined(separator: ", ")
    }

    /// Names of top-level items that are directories (set by the caller)
    var transferredFolderNames: Set<String> = []

    private var pastTenseOperation: String {
        switch operation {
        case "Copying": return "Copied"
        case "Moving": return "Moved"
        case "Downloading": return "Downloaded"
        case "Uploading": return "Uploaded"
        case "Opening": return "Opened"
        default: return operation
        }
    }

    var completionSummary: String {
        let names = transferredFileNames
        let pastTense = pastTenseOperation

        if isCancelled {
            let label = names.count == 1 ? names[0] : "\(names.count) items"
            return "Cancelled: \(operation) \(label)"
        }

        // Partial success: some items succeeded, some failed. Surface a
        // mixed summary so the user knows to check the details popover.
        if failureCount > 0 && successCount > 0 {
            let total = successCount + failureCount + skippedCount
            return "\(pastTense) \(successCount) of \(total) — \(failureCount) failed"
        }

        // Whole-transfer failure (either a fatal errorMessage with no
        // per-item progress, or every per-item attempt failed).
        if errorMessage != nil || (failureCount > 0 && successCount == 0) {
            let label: String
            if failureCount > 0 {
                label = failureCount == 1 ? itemResults.first(where: { $0.status == .failed })?.name ?? "1 item" : "\(failureCount) items"
            } else {
                label = names.count == 1 ? names[0] : "\(names.count) items"
            }
            let detail = errorMessage ?? itemResults.first(where: { $0.status == .failed })?.errorMessage ?? "Unknown error"
            return "Failed: \(operation) \(label) — \(detail)"
        }

        if names.isEmpty { return "Done" }

        let maxInline = 3
        let header = "Done: \(pastTense) "
        if names.count == 1 {
            let fileCount = totalFiles > 0 ? totalFiles : totalItems
            if !transferredFolderNames.isEmpty && fileCount > 1 {
                return "\(header)\(names[0]) (\(fileCount) files)"
            }
            return "\(header)\(names[0])"
        }

        let shown = names.prefix(maxInline).joined(separator: ", ")
        let remaining = names.count - maxInline
        if remaining > 0 {
            return "\(header)\(shown) +\(remaining) more"
        }
        return "\(header)\(shown)"
    }

    /// Full item list for tooltip display when the summary is truncated.
    var completionDetailNames: String? {
        let names = transferredFileNames
        guard names.count > 3 else { return nil }
        return names.joined(separator: "\n")
    }

    var duration: TimeInterval {
        (endTime ?? Date()).timeIntervalSince(startTime)
    }

    var averageSpeed: String {
        guard duration > 0, totalBytes > 0 else { return "--" }
        let bytesPerSec = Double(totalBytes) / duration
        return ByteCountFormatter.string(fromByteCount: Int64(bytesPerSec), countStyle: .file) + "/s"
    }

    var downloadSpeed: String {
        guard let start = downloadStartTime else { return "--" }
        let end = downloadEndTime ?? Date()
        let dur = end.timeIntervalSince(start)
        guard dur > 0, downloadBytes > 0 else { return "--" }
        let bytesPerSec = Double(downloadBytes) / dur
        return ByteCountFormatter.string(fromByteCount: Int64(bytesPerSec), countStyle: .file) + "/s"
    }

    var uploadSpeed: String {
        guard let start = uploadStartTime else { return "--" }
        let end = uploadEndTime ?? Date()
        let dur = end.timeIntervalSince(start)
        guard dur > 0, uploadBytes > 0 else { return "--" }
        let bytesPerSec = Double(uploadBytes) / dur
        return ByteCountFormatter.string(fromByteCount: Int64(bytesPerSec), countStyle: .file) + "/s"
    }

    var formattedEndTime: String {
        guard let end = endTime else { return "--" }
        let f = DateFormatter()
        f.dateStyle = .none
        f.timeStyle = .medium
        return f.string(from: end)
    }
}

enum SidebarItem: Hashable, Identifiable {
    case home
    case favorites
    case location(URL)
    case cloudAccount(CloudAccount)
    case cloudFolder(accountId: UUID, path: String)
    case syncRules
    /// An external or network drive. When online, panel navigates to its
    /// mount path via `.location`. When offline, panel shows OfflineSourceView.
    case drive(driveId: String)
    /// An offline-source folder view, opened from a search result.
    /// Used for both indexed drives and disconnected cloud accounts.
    case offlineFolder(sourceId: String, path: String)

    var id: String {
        switch self {
        case .home: return "home"
        case .favorites: return "favorites"
        case .location(let url): return url.path()
        case .cloudAccount(let account): return account.id.uuidString
        case .cloudFolder(let accountId, let path): return "cloud:\(accountId.uuidString):\(path)"
        case .syncRules: return "syncRules"
        case .drive(let id): return "drive:\(id)"
        case .offlineFolder(let sourceId, let path): return "offline:\(sourceId):\(path)"
        }
    }
}
