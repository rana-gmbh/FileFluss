import SwiftUI
import QuickLookUI

@Observable @MainActor
final class CloudFileManagerViewModel {
    let accountId: UUID
    var currentPath: String = "/"
    var items: [CloudFileItem] = []
    var selectedItemIDs: Set<String> = []
    var isLoading = false
    var error: String?
    var searchText: String = ""
    var sortOrder: SortOrder = .name
    var sortAscending: Bool = true

    let quickLookController = QuickLookController()

    // MARK: - Per-item conflict resolution

    var conflictDirection: ConflictDirection = .leftToRight
    var pendingConflict: PendingConflict?
    private var conflictContinuation: CheckedContinuation<ConflictResolution, Never>?

    func resolveConflict(_ resolution: ConflictResolution) {
        conflictContinuation?.resume(returning: resolution)
        conflictContinuation = nil
        pendingConflict = nil
    }


    private var pathHistory: [String] = ["/"]
    private var pathHistoryIndex: Int = 0
    private var tempDownloadDir: URL

    enum SortOrder: String, CaseIterable {
        case name, date, size

        var label: String {
            switch self {
            case .name: return "Name"
            case .date: return "Date Modified"
            case .size: return "Size"
            }
        }
    }

    init(accountId: UUID) {
        self.accountId = accountId
        self.tempDownloadDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFluss-cloud-\(accountId.uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: tempDownloadDir, withIntermediateDirectories: true)
    }

    var filteredItems: [CloudFileItem] {
        let filtered = searchText.isEmpty
            ? items
            : items.filter { $0.name.localizedCaseInsensitiveContains(searchText) }
        return sorted(filtered)
    }

    var selectedItems: [CloudFileItem] {
        items.filter { selectedItemIDs.contains($0.id) }
    }

    // MARK: - Selection Size (for footer)

    var selectionSize: Int64? = nil
    var isCalculatingSelectionSize = false
    private var selectionSizeTask: Task<Void, Never>?

    func recalculateSelectionSize() {
        selectionSizeTask?.cancel()

        let selected = selectedItems
        guard !selected.isEmpty else {
            selectionSize = nil
            isCalculatingSelectionSize = false
            return
        }

        let files = selected.filter { !$0.isDirectory }
        let folders = selected.filter { $0.isDirectory }
        let fileSize = files.reduce(Int64(0)) { $0 + $1.size }

        if folders.isEmpty {
            selectionSize = fileSize
            isCalculatingSelectionSize = false
            return
        }

        isCalculatingSelectionSize = true
        selectionSize = fileSize

        selectionSizeTask = Task {
            var total = fileSize
            guard let provider = await SyncEngine.shared.provider(for: accountId) else { return }
            for folder in folders {
                guard !Task.isCancelled else { return }
                do {
                    let size = try await provider.folderSize(at: folder.path)
                    total += size
                } catch {
                    // skip folders that fail
                }
            }
            guard !Task.isCancelled else { return }
            self.selectionSize = total
            self.isCalculatingSelectionSize = false
        }
    }

    var canGoBack: Bool { pathHistoryIndex > 0 }
    var canGoForward: Bool { pathHistoryIndex < pathHistory.count - 1 }

    // MARK: - Navigation

    func loadDirectory(at path: String? = nil) async {
        let targetPath = path ?? currentPath
        isLoading = true
        error = nil

        do {
            let provider = await SyncEngine.shared.provider(for: accountId)
            guard let provider else {
                error = "Cloud account not connected"
                isLoading = false
                return
            }

            let loadedItems = try await provider.listDirectory(at: targetPath)

            // Feed into search index (fire-and-forget)
            let accId = self.accountId
            Task.detached(priority: .utility) {
                await SearchIndex.shared.upsertItems(loadedItems, accountId: accId)
            }

            self.items = loadedItems
            if let path, path != self.currentPath {
                self.currentPath = path
                pushToHistory(path)
            }
            self.selectedItemIDs.removeAll()
            self.isLoading = false
        } catch {
            self.error = error.localizedDescription
            self.isLoading = false
        }
    }

    func navigateTo(_ path: String) async {
        if path != currentPath {
            await loadDirectory(at: path)
        }
    }

    func navigateUp() async {
        guard currentPath != "/" else { return }
        let parent = (currentPath as NSString).deletingLastPathComponent
        await navigateTo(parent.isEmpty ? "/" : parent)
    }

    func navigateBack() async {
        guard canGoBack else { return }
        pathHistoryIndex -= 1
        let path = pathHistory[pathHistoryIndex]
        currentPath = path
        await loadDirectory()
    }

    func navigateForward() async {
        guard canGoForward else { return }
        pathHistoryIndex += 1
        let path = pathHistory[pathHistoryIndex]
        currentPath = path
        await loadDirectory()
    }

    func refresh() async {
        await loadDirectory()
    }

    func openItem(_ item: CloudFileItem) async {
        if item.isDirectory {
            await navigateTo(item.path)
        }
    }

    // MARK: - Create & Rename

    func createNewFolder(named name: String) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return }
        let folderPath = currentPath == "/" ? "/\(name)" : "\(currentPath)/\(name)"
        do {
            try await provider.createDirectory(at: folderPath)
            await loadDirectory()
        } catch {
            self.error = "Failed to create folder: \(error.localizedDescription)"
        }
    }

    func renameItem(_ item: CloudFileItem, to newName: String) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return }
        do {
            try await provider.renameItem(at: item.path, to: newName)
            await loadDirectory()
        } catch {
            self.error = "Failed to rename: \(error.localizedDescription)"
        }
    }

    // MARK: - Delete

    func deleteSelectedItems() async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return }

        for item in selectedItems {
            do {
                try await provider.deleteItem(at: item.path)
            } catch {
                self.error = "Failed to delete \(item.name): \(error.localizedDescription)"
                return
            }
        }
        selectedItemIDs.removeAll()
        await loadDirectory()
    }

    func deleteItems(_ items: [CloudFileItem]) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return }

        for item in items {
            do {
                try await provider.deleteItem(at: item.path)
            } catch {
                self.error = "Failed to delete \(item.name): \(error.localizedDescription)"
                return
            }
        }
        await loadDirectory()
    }

    // MARK: - Pre-flight Conflict Resolution (for cloud-to-cloud)

    enum PreFlightResult: Equatable {
        case transfer          // download + upload normally
        case replace           // delete existing on target, then transfer
        case keepBoth          // upload with a unique name
        case skip              // don't transfer this item
    }

    /// Check source items against this VM's current items and resolve conflicts
    /// before any downloads happen. Returns a resolution for each source item.
    func preFlightConflictCheck(sourceItems: [CloudFileItem]) async -> [(CloudFileItem, PreFlightResult)] {
        let existingByName = Dictionary(items.map { ($0.name, $0) }, uniquingKeysWith: { first, _ in first })
        var results: [(CloudFileItem, PreFlightResult)] = []
        var applyToAllChoice: ConflictChoice?

        for (index, item) in sourceItems.enumerated() {
            guard let existing = existingByName[item.name] else {
                results.append((item, .transfer))
                continue
            }

            let choice: ConflictChoice
            if let saved = applyToAllChoice {
                choice = saved
            } else {
                let remaining = sourceItems[(index + 1)...].filter { existingByName[$0.name] != nil }.count
                let resolution = await withCheckedContinuation { continuation in
                    self.conflictContinuation = continuation
                    self.pendingConflict = PendingConflict(
                        source: ConflictFileInfo(name: item.name, date: item.modificationDate, size: item.size, fileExtension: (item.name as NSString).pathExtension, localURL: nil),
                        destination: ConflictFileInfo(name: existing.name, date: existing.modificationDate, size: existing.size, fileExtension: (existing.name as NSString).pathExtension, localURL: nil),
                        remainingConflicts: remaining,
                        direction: self.conflictDirection
                    )
                }
                choice = resolution.choice
                if resolution.applyToAll { applyToAllChoice = choice }
            }
            switch choice {
            case .skip: results.append((item, .skip))
            case .stop:
                // Mark remaining items as skip
                for remaining in sourceItems[index...] {
                    results.append((remaining, .skip))
                }
                return results
            case .keepBoth: results.append((item, .keepBoth))
            case .replace: results.append((item, .replace))
            }
        }
        return results
    }

    // MARK: - Download

    func downloadItems(_ items: [CloudFileItem], to localDirectory: URL, progress: TransferProgress? = nil, skipConflictCheck: Bool = false) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return }

        progress?.transferredFileNames = items.map(\.name)
        progress?.transferredFolderNames = Set(items.filter(\.isDirectory).map(\.name))
        progress?.isCloudDownload = true

        let convertedExtensions = ["docx", "xlsx", "pptx", "pdf"]
        var applyToAllChoice: ConflictChoice?
        var downloadedCount = 0

        for (index, item) in items.enumerated() {
            var base = localDirectory.appendingPathComponent(item.name)
            let existingURL = Self.findExistingFile(base: base, convertedExtensions: convertedExtensions)
            let exists = existingURL != nil

            if exists && !skipConflictCheck {
                let choice: ConflictChoice
                if let saved = applyToAllChoice {
                    choice = saved
                } else {
                    let remaining = items[(index + 1)...].filter { nextItem in
                        let nextBase = localDirectory.appendingPathComponent(nextItem.name)
                        return Self.findExistingFile(base: nextBase, convertedExtensions: convertedExtensions) != nil
                    }.count
                    let destInfo = FileManagerViewModel.localFileInfo(at: existingURL!)
                    let resolution = await withCheckedContinuation { continuation in
                        self.conflictContinuation = continuation
                        self.pendingConflict = PendingConflict(
                            source: ConflictFileInfo(name: item.name, date: item.modificationDate, size: item.size, fileExtension: (item.name as NSString).pathExtension, localURL: nil),
                            destination: destInfo,
                            remainingConflicts: remaining,
                            direction: self.conflictDirection
                        )
                    }
                    choice = resolution.choice
                    if resolution.applyToAll { applyToAllChoice = choice }
                }
                switch choice {
                case .skip: progress?.completedItems = index + 1; continue
                case .stop:
                    if !(progress?.isCloudToCloud ?? false) { progress?.endTime = Date(); progress?.isComplete = true }
                    return
                case .keepBoth:
                    base = FileManagerViewModel.uniqueDestination(for: base)
                case .replace: break
                }
            }

            do {
                if exists {
                    if !skipConflictCheck, let existing = existingURL {
                        try FileManager.default.removeItem(at: existing)
                    }
                    for ext in convertedExtensions {
                        let converted = localDirectory.appendingPathComponent(item.name).appendingPathExtension(ext)
                        if FileManager.default.fileExists(atPath: converted.path) { try FileManager.default.removeItem(at: converted) }
                    }
                }
                try await downloadRecursively(item: item, to: localDirectory, provider: provider, progress: progress, downloadedCount: &downloadedCount)
                progress?.completedItems = index + 1
            } catch {
                self.error = "Failed to download \(item.name): \(error.localizedDescription)"
                progress?.errorMessage = error.localizedDescription
                if !(progress?.isCloudToCloud ?? false) { progress?.endTime = Date(); progress?.isComplete = true }
                return
            }
        }

        if !(progress?.isCloudToCloud ?? false) {
            progress?.endTime = Date()
            progress?.isComplete = true
        }
    }

    private func downloadRecursively(item: CloudFileItem, to localDirectory: URL, provider: any CloudProvider, progress: TransferProgress?, downloadedCount: inout Int) async throws {
        if item.isDirectory {
            let folderURL = localDirectory.appendingPathComponent(item.name)
            try FileManager.default.createDirectory(at: folderURL, withIntermediateDirectories: true)
            let contents = try await provider.listDirectory(at: item.path)
            for child in contents {
                try await downloadRecursively(item: child, to: folderURL, provider: provider, progress: progress, downloadedCount: &downloadedCount)
            }
        } else {
            let localURL = localDirectory.appendingPathComponent(item.name)
            progress?.currentFileName = item.name
            try await provider.downloadFile(remotePath: item.path, to: localURL)
            // Preserve original cloud modification date on the local file
            // so conflict dialogs and file listings show the correct date
            let finalURL: URL
            // Google Workspace files may get an extension appended
            let convertedExts = ["docx", "xlsx", "pptx", "pdf"]
            if let converted = convertedExts.lazy.map({ localURL.appendingPathExtension($0) }).first(where: { FileManager.default.fileExists(atPath: $0.path) }) {
                finalURL = converted
            } else {
                finalURL = localURL
            }
            try? FileManager.default.setAttributes(
                [.modificationDate: item.modificationDate],
                ofItemAtPath: finalURL.path
            )
            progress?.totalBytes += item.size
            downloadedCount += 1
            progress?.totalFiles = downloadedCount
            progress?.completedItems = downloadedCount
        }
    }

    func downloadToTemp(_ item: CloudFileItem) async -> URL? {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return nil }
        let localURL = tempDownloadDir.appendingPathComponent(item.name)

        // Use cached version if it exists (check both original and converted names)
        if FileManager.default.fileExists(atPath: localURL.path) {
            return localURL
        }
        for ext in ["docx", "xlsx", "pptx", "pdf"] {
            let converted = localURL.appendingPathExtension(ext)
            if FileManager.default.fileExists(atPath: converted.path) {
                return converted
            }
        }

        do {
            try await provider.downloadFile(remotePath: item.path, to: localURL)
            // Google Workspace files get written with an appended extension
            for ext in ["docx", "xlsx", "pptx", "pdf"] {
                let converted = localURL.appendingPathExtension(ext)
                if FileManager.default.fileExists(atPath: converted.path) {
                    return converted
                }
            }
            return localURL
        } catch {
            return nil
        }
    }

    // MARK: - Upload

    func uploadFiles(from urls: [URL], progress: TransferProgress? = nil, skipConflictCheck: Bool = false) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else {
            self.error = "Cloud account not connected"
            progress?.isComplete = true
            return
        }

        let existingByName = Dictionary(items.map { ($0.name, $0) }, uniquingKeysWith: { first, _ in first })

        progress?.transferredFileNames = urls.map(\.lastPathComponent)
        let fm = FileManager.default
        progress?.transferredFolderNames = Set(urls.filter { var isDir: ObjCBool = false; return fm.fileExists(atPath: $0.path, isDirectory: &isDir) && isDir.boolValue }.map(\.lastPathComponent))
        progress?.isCloudUpload = true

        var applyToAllChoice: ConflictChoice?
        var uploadedCount = 0

        for (index, url) in urls.enumerated() {
            var uploadURL = url
            let name = url.lastPathComponent
            if let existing = existingByName[name], !skipConflictCheck {
                let choice: ConflictChoice
                if let saved = applyToAllChoice {
                    choice = saved
                } else {
                    let remaining = urls[(index + 1)...].filter { existingByName[$0.lastPathComponent] != nil }.count
                    let sourceInfo = FileManagerViewModel.localFileInfo(at: url)
                    let resolution = await withCheckedContinuation { continuation in
                        self.conflictContinuation = continuation
                        self.pendingConflict = PendingConflict(
                            source: sourceInfo,
                            destination: ConflictFileInfo(name: existing.name, date: existing.modificationDate, size: existing.size, fileExtension: (existing.name as NSString).pathExtension, localURL: nil),
                            remainingConflicts: remaining,
                            direction: self.conflictDirection
                        )
                    }
                    choice = resolution.choice
                    if resolution.applyToAll { applyToAllChoice = choice }
                }
                switch choice {
                case .skip: continue
                case .stop:
                    if !(progress?.isCloudToCloud ?? false) { progress?.endTime = Date(); progress?.isComplete = true }
                    await loadDirectory(); return
                case .keepBoth:
                    let uniqueName = Self.uniqueCloudName(for: name, existing: Set(existingByName.keys))
                    let tempCopy = FileManager.default.temporaryDirectory.appendingPathComponent(uniqueName)
                    try? FileManager.default.removeItem(at: tempCopy)
                    do { try FileManager.default.copyItem(at: url, to: tempCopy) } catch { break }
                    uploadURL = tempCopy
                case .replace:
                    do { try await provider.deleteItem(at: existing.path) } catch {
                        self.error = "Failed to overwrite \(name): \(error.localizedDescription)"
                        progress?.errorMessage = error.localizedDescription
                        progress?.endTime = Date(); progress?.isComplete = true
                        return
                    }
                }
            }

            do {
                try await uploadRecursively(urls: [uploadURL], toRemotePath: currentPath, provider: provider, progress: progress, uploadedCount: &uploadedCount)
                if uploadURL != url { try? FileManager.default.removeItem(at: uploadURL) }
            } catch {
                self.error = error.localizedDescription
                progress?.errorMessage = error.localizedDescription
                if !(progress?.isCloudToCloud ?? false) { progress?.endTime = Date(); progress?.isComplete = true }
                return
            }
        }

        if !(progress?.isCloudToCloud ?? false) {
            progress?.endTime = Date()
            progress?.isComplete = true
        }
        await loadDirectory()
    }

    private func uploadRecursively(urls: [URL], toRemotePath remotePath: String, provider: CloudProvider, progress: TransferProgress?, uploadedCount: inout Int) async throws {
        let fm = FileManager.default
        for url in urls {
            var isDir: ObjCBool = false
            guard fm.fileExists(atPath: url.path, isDirectory: &isDir) else {
                print("[Upload] File not found, skipping: \(url.path)")
                continue
            }

            let itemRemotePath = remotePath == "/" ? "/\(url.lastPathComponent)" : "\(remotePath)/\(url.lastPathComponent)"

            if isDir.boolValue {
                print("[Upload] Creating directory: \(itemRemotePath)")
                do {
                    try await provider.createDirectory(at: itemRemotePath)
                } catch let error as CloudProviderError {
                    switch error {
                    case .notAuthenticated, .unauthorized:
                        throw error // Auth errors must propagate
                    default:
                        // Folder may already exist — continue uploading contents
                        print("[Upload] Create directory failed (may already exist): \(itemRemotePath) — \(error.localizedDescription)")
                    }
                }
                let contents = try fm.contentsOfDirectory(at: url, includingPropertiesForKeys: [.fileSizeKey])
                print("[Upload] Directory \(url.lastPathComponent) has \(contents.count) items")
                try await uploadRecursively(urls: contents, toRemotePath: itemRemotePath, provider: provider, progress: progress, uploadedCount: &uploadedCount)
            } else {
                progress?.currentFileName = url.lastPathComponent
                let fileSize = (try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0
                print("[Upload] Uploading file: \(url.lastPathComponent) (\(fileSize) bytes) to \(itemRemotePath)")
                try await provider.uploadFile(from: url, to: itemRemotePath)
                print("[Upload] Success: \(url.lastPathComponent)")
                progress?.totalBytes += Int64(fileSize)
                uploadedCount += 1
                progress?.totalFiles = uploadedCount
                progress?.completedItems = uploadedCount
            }
        }
    }

    // MARK: - Quick Look

    func toggleQuickLook() {
        let fileItems = selectedItems.filter { !$0.isDirectory }
        guard !fileItems.isEmpty else { return }

        Task {
            var urls: [URL] = []
            for item in fileItems {
                if let url = await downloadToTemp(item) {
                    urls.append(url)
                }
            }
            quickLookController.urls = urls
            quickLookController.toggle()
        }
    }

    func updateQuickLookSelection() {
        let fileItems = selectedItems.filter { !$0.isDirectory }
        guard !fileItems.isEmpty else {
            quickLookController.updateAndReload(urls: [])
            return
        }

        Task {
            var urls: [URL] = []
            for item in fileItems {
                if let url = await downloadToTemp(item) {
                    urls.append(url)
                }
            }
            quickLookController.updateAndReload(urls: urls)
        }
    }

    // MARK: - Helpers

    static func uniqueCloudName(for name: String, existing: Set<String>) -> String {
        let nsName = name as NSString
        let ext = nsName.pathExtension
        let base = nsName.deletingPathExtension
        var counter = 2
        while true {
            let candidate = ext.isEmpty ? "\(base) \(counter)" : "\(base) \(counter).\(ext)"
            if !existing.contains(candidate) { return candidate }
            counter += 1
        }
    }

    static func findExistingFile(base: URL, convertedExtensions: [String]) -> URL? {
        if FileManager.default.fileExists(atPath: base.path) { return base }
        for ext in convertedExtensions {
            let converted = base.appendingPathExtension(ext)
            if FileManager.default.fileExists(atPath: converted.path) { return converted }
        }
        return nil
    }

    // MARK: - Private

    private func pushToHistory(_ path: String) {
        if pathHistoryIndex < pathHistory.count - 1 {
            pathHistory.removeSubrange((pathHistoryIndex + 1)...)
        }
        pathHistory.append(path)
        pathHistoryIndex = pathHistory.count - 1
    }

    private func sorted(_ items: [CloudFileItem]) -> [CloudFileItem] {
        items.sorted { a, b in
            if a.isDirectory != b.isDirectory {
                return a.isDirectory
            }
            let result: Bool
            switch sortOrder {
            case .name:
                result = a.name.localizedStandardCompare(b.name) == .orderedAscending
            case .date:
                result = a.modificationDate < b.modificationDate
            case .size:
                result = a.size < b.size
            }
            return sortAscending ? result : !result
        }
    }
}
