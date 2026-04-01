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

    var pendingOverwrite: PendingCloudOverwrite?
    var pendingUploadOverwrite: PendingUploadOverwrite?

    struct PendingCloudOverwrite {
        let conflicting: [String]
        let items: [CloudFileItem]
        let localDirectory: URL
        let progress: TransferProgress?
        let deleteAfter: Bool
    }

    struct PendingUploadOverwrite {
        let conflicting: [String]
        let urls: [URL]
        let progress: TransferProgress?
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

    // MARK: - Download

    func downloadItems(_ items: [CloudFileItem], to localDirectory: URL, progress: TransferProgress? = nil, overwrite: Bool = false) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return }

        // Check for conflicts at the top level (files only)
        let topLevelFiles = items.filter { !$0.isDirectory }
        if !overwrite {
            let conflicts = topLevelFiles.filter {
                FileManager.default.fileExists(atPath: localDirectory.appendingPathComponent($0.name).path)
            }
            if !conflicts.isEmpty {
                pendingOverwrite = PendingCloudOverwrite(
                    conflicting: conflicts.map(\.name),
                    items: items,
                    localDirectory: localDirectory,
                    progress: progress,
                    deleteAfter: false
                )
                return
            }
        }

        // Track top-level names and folder info for completion summary
        progress?.transferredFileNames = items.map(\.name)
        progress?.transferredFolderNames = Set(items.filter(\.isDirectory).map(\.name))
        progress?.isCloudDownload = true

        var downloadedCount = 0
        do {
            try await downloadRecursively(items: items, to: localDirectory, provider: provider, overwrite: overwrite, progress: progress, downloadedCount: &downloadedCount)
        } catch {
            self.error = error.localizedDescription
            progress?.errorMessage = error.localizedDescription
        }
        if !(progress?.isCloudToCloud ?? false) {
            progress?.endTime = Date()
            progress?.isComplete = true
        }
    }

    private func downloadRecursively(items: [CloudFileItem], to localDirectory: URL, provider: CloudProvider, overwrite: Bool, progress: TransferProgress?, downloadedCount: inout Int) async throws {
        for item in items {
            if item.isDirectory {
                let folderURL = localDirectory.appendingPathComponent(item.name)
                try FileManager.default.createDirectory(at: folderURL, withIntermediateDirectories: true)
                let contents = try await provider.listDirectory(at: item.path)
                try await downloadRecursively(items: contents, to: folderURL, provider: provider, overwrite: overwrite, progress: progress, downloadedCount: &downloadedCount)
            } else {
                let localURL = localDirectory.appendingPathComponent(item.name)
                progress?.currentFileName = item.name
                if overwrite && FileManager.default.fileExists(atPath: localURL.path) {
                    try FileManager.default.removeItem(at: localURL)
                }
                try await provider.downloadFile(remotePath: item.path, to: localURL)
                progress?.totalBytes += item.size
                downloadedCount += 1
                progress?.totalFiles = downloadedCount
                progress?.completedItems = downloadedCount
            }
        }
    }

    func downloadToTemp(_ item: CloudFileItem) async -> URL? {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return nil }
        let localURL = tempDownloadDir.appendingPathComponent(item.name)

        // Use cached version if it exists
        if FileManager.default.fileExists(atPath: localURL.path) {
            return localURL
        }

        do {
            try await provider.downloadFile(remotePath: item.path, to: localURL)
            return localURL
        } catch {
            self.error = "Failed to download \(item.name): \(error.localizedDescription)"
            return nil
        }
    }

    // MARK: - Upload

    func uploadFiles(from urls: [URL], progress: TransferProgress? = nil, overwrite: Bool = false) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else {
            self.error = "Cloud account not connected"
            progress?.isComplete = true
            return
        }

        // Check for conflicts at the top level
        if !overwrite {
            let existingNames = Set(items.map(\.name))
            let conflicts = urls.filter { existingNames.contains($0.lastPathComponent) }
            if !conflicts.isEmpty {
                pendingUploadOverwrite = PendingUploadOverwrite(
                    conflicting: conflicts.map(\.lastPathComponent),
                    urls: urls,
                    progress: progress
                )
                return
            }
        }

        // Delete conflicting remote items before re-uploading
        if overwrite {
            let existingByName = Dictionary(uniqueKeysWithValues: items.map { ($0.name, $0) })
            for url in urls {
                if let existing = existingByName[url.lastPathComponent] {
                    do {
                        try await provider.deleteItem(at: existing.path)
                    } catch {
                        self.error = "Failed to overwrite \(existing.name): \(error.localizedDescription)"
                        progress?.errorMessage = error.localizedDescription
                        progress?.endTime = Date()
                        progress?.isComplete = true
                        return
                    }
                }
            }
        }

        progress?.transferredFileNames = urls.map(\.lastPathComponent)
        let fm = FileManager.default
        progress?.transferredFolderNames = Set(urls.filter { var isDir: ObjCBool = false; return fm.fileExists(atPath: $0.path, isDirectory: &isDir) && isDir.boolValue }.map(\.lastPathComponent))
        progress?.isCloudUpload = true

        var uploadedCount = 0
        var uploadError: String?
        do {
            try await uploadRecursively(urls: urls, toRemotePath: currentPath, provider: provider, progress: progress, uploadedCount: &uploadedCount)
        } catch {
            uploadError = error.localizedDescription
        }
        if !(progress?.isCloudToCloud ?? false) {
            if let uploadError {
                progress?.errorMessage = uploadError
            }
            progress?.endTime = Date()
            progress?.isComplete = true
        }
        await loadDirectory()
        if let uploadError {
            self.error = uploadError
        }
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
