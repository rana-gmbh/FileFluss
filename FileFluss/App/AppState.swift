import SwiftUI
import Combine

enum PanelSide: Hashable {
    case left, right
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
    var showSearchPopup = false
    var showSyncSheet = false

    var selectedLeftSidebarItem: SidebarItem? = .location(
        FileManager.default.homeDirectoryForCurrentUser
    )
    var selectedRightSidebarItem: SidebarItem? = .location(
        FileManager.default.homeDirectoryForCurrentUser
    )

    var activePanel: PanelSide = .left

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
                return !cloudFileManager(for: cloudId).selectedItems.isEmpty
            }
            return false
        }
        return !activeFileManager.selectedItems.isEmpty
    }

    var hasSingleSelection: Bool {
        if isActivePanelCloud {
            if let cloudId = cloudAccountId(for: activePanel) {
                return cloudFileManager(for: cloudId).selectedItems.count == 1
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

    // Custom favorites (shared across both panels)
    var customFavorites: [FavoriteFolder] = []

    func addFavorite(url: URL) {
        guard !customFavorites.contains(where: { $0.url == url }) else { return }
        customFavorites.append(FavoriteFolder(url: url, displayName: url.lastPathComponent))
    }

    func removeFavorite(id: UUID) {
        customFavorites.removeAll { $0.id == id }
    }

    func renameFavorite(id: UUID, to newName: String) {
        if let idx = customFavorites.firstIndex(where: { $0.id == id }) {
            customFavorites[idx].displayName = newName
        }
    }

    // Cloud favorites
    var cloudFavorites: [CloudFavorite] = []

    func addCloudFavorite(accountId: UUID, path: String, name: String) {
        guard !cloudFavorites.contains(where: { $0.accountId == accountId && $0.path == path }) else { return }
        let account = syncManager.accountFor(id: accountId)
        let providerSuffix = account?.providerType.displayName ?? "Cloud"
        let displayName = "\(name) (\(providerSuffix))"
        cloudFavorites.append(CloudFavorite(
            accountId: accountId,
            path: path,
            displayName: displayName,
            providerType: account?.providerType ?? .pCloud
        ))
    }

    func removeCloudFavorite(id: UUID) {
        cloudFavorites.removeAll { $0.id == id }
    }

    func renameCloudFavorite(id: UUID, to newName: String) {
        if let idx = cloudFavorites.firstIndex(where: { $0.id == id }) {
            cloudFavorites[idx].displayName = newName
        }
    }

    // Cloud file managers (cached per account)
    private var cloudFileManagers: [UUID: CloudFileManagerViewModel] = [:]

    func cloudFileManager(for accountId: UUID) -> CloudFileManagerViewModel {
        if let existing = cloudFileManagers[accountId] {
            return existing
        }
        let vm = CloudFileManagerViewModel(accountId: accountId)
        cloudFileManagers[accountId] = vm
        return vm
    }

    func removeCloudFileManager(for accountId: UUID) {
        cloudFileManagers.removeValue(forKey: accountId)
    }

    // Cloud drag source tracking (for cross-panel drag from cloud to local)
    var cloudDragSourceItems: [CloudFileItem] = []
    var cloudDragSourceAccountId: UUID?

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

    init() {
        self.leftFileManager = FileManagerViewModel()
        self.rightFileManager = FileManagerViewModel()
        self.syncManager = SyncViewModel()

        Task {
            await syncManager.reconnectSavedAccounts()
            try? await SearchIndex.shared.open()
        }
    }
}

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

@Observable @MainActor
final class TransferProgress: Identifiable {
    let id = UUID()
    let operation: String  // "Copying", "Moving", "Downloading"
    let totalItems: Int
    var completedItems: Int = 0
    var totalFiles: Int = 0 // actual file count discovered during recursive traversal
    var currentFileName: String = ""
    var isComplete: Bool = false
    var errorMessage: String?
    var transferredFileNames: [String] = []
    var totalBytes: Int64 = 0
    let startTime = Date()
    var endTime: Date?

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

        if let errorMessage {
            let label = names.count == 1 ? names[0] : "\(names.count) items"
            return "Failed: \(operation) \(label) — \(errorMessage)"
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

    var id: String {
        switch self {
        case .home: return "home"
        case .favorites: return "favorites"
        case .location(let url): return url.path()
        case .cloudAccount(let account): return account.id.uuidString
        case .cloudFolder(let accountId, let path): return "cloud:\(accountId.uuidString):\(path)"
        case .syncRules: return "syncRules"
        }
    }
}
