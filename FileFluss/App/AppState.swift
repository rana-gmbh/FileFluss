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
    static let menuNewFolder = Notification.Name("menuNewFolder")
    static let menuRename = Notification.Name("menuRename")
    static let menuDelete = Notification.Name("menuDelete")
    static let menuCopyToOtherPanel = Notification.Name("menuCopyToOtherPanel")
    static let menuMoveToOtherPanel = Notification.Name("menuMoveToOtherPanel")
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
