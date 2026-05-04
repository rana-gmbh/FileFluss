import SwiftUI
import Combine
import QuickLookUI

@Observable @MainActor
final class FileManagerViewModel {
    var currentDirectory: URL
    var items: [FileItem] = []
    var selectedItemIDs: Set<String> = []
    var sortOrder: SortOrder = .name
    var sortAscending: Bool = true
    var showHiddenFiles: Bool = false
    var isLoading: Bool = false
    var error: String?
    var pathHistory: [URL] = []
    var pathHistoryIndex: Int = -1
    var searchText: String = ""

    // Drag & Drop
    var draggedItems: [FileItem] = []
    var pendingDrop: PendingDrop?
    // Drag & Drop pending state

    struct PendingDrop {
        let items: [FileItem]
        let destinationFolder: URL
    }

    // MARK: - Per-item conflict resolution

    var conflictDirection: ConflictDirection = .leftToRight
    var pendingConflict: PendingConflict?
    private var conflictContinuation: CheckedContinuation<ConflictResolution, Never>?

    func resolveConflict(_ resolution: ConflictResolution) {
        conflictContinuation?.resume(returning: resolution)
        conflictContinuation = nil
        pendingConflict = nil
    }

    // Quick Look — controlled directly, no SwiftUI binding
    let quickLookController = QuickLookController()

    enum SortOrder: String, CaseIterable {
        case name, date, dateCreated, size, kind

        var label: String {
            switch self {
            case .name: return "Name"
            case .date: return "Date Modified"
            case .dateCreated: return "Date Created"
            case .size: return "Size"
            case .kind: return "Kind"
            }
        }
    }

    init() {
        self.currentDirectory = Foundation.FileManager.default.homeDirectoryForCurrentUser
        Task { await loadDirectory() }
    }

    var filteredItems: [FileItem] {
        let filtered = searchText.isEmpty
            ? items
            : items.filter { $0.name.localizedCaseInsensitiveContains(searchText) }
        return sorted(filtered)
    }

    func loadDirectory(at url: URL? = nil) async {
        let targetURL = url ?? currentDirectory
        isLoading = true
        error = nil

        do {
            let loadedItems = try await FileSystemService.shared.listDirectory(
                at: targetURL,
                showHidden: showHiddenFiles
            )

            await MainActor.run {
                self.items = loadedItems
                if let url, url != self.currentDirectory {
                    self.currentDirectory = url
                    self.pushToHistory(url)
                }
                self.selectedItemIDs.removeAll()
                self.isLoading = false
            }
        } catch {
            await MainActor.run {
                self.error = error.localizedDescription
                self.isLoading = false
            }
        }
    }

    func navigateTo(_ url: URL) async {
        if url != currentDirectory {
            await loadDirectory(at: url)
        }
    }

    func navigateUp() async {
        let parent = currentDirectory.deletingLastPathComponent()
        await navigateTo(parent)
    }

    func navigateBack() async {
        guard pathHistoryIndex > 0 else { return }
        pathHistoryIndex -= 1
        let url = pathHistory[pathHistoryIndex]
        currentDirectory = url
        await loadDirectory()
    }

    func navigateForward() async {
        guard pathHistoryIndex < pathHistory.count - 1 else { return }
        pathHistoryIndex += 1
        let url = pathHistory[pathHistoryIndex]
        currentDirectory = url
        await loadDirectory()
    }

    var canGoBack: Bool { pathHistoryIndex > 0 }
    var canGoForward: Bool { pathHistoryIndex < pathHistory.count - 1 }

    func openItem(_ item: FileItem) async {
        if item.isDirectory {
            await navigateTo(item.url)
        } else {
            NSWorkspace.shared.open(item.url)
        }
    }

    var selectedItems: [FileItem] {
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
        selectionSize = fileSize // show file size immediately

        selectionSizeTask = Task {
            var total = fileSize
            let fm = Foundation.FileManager.default
            for folder in folders {
                guard !Task.isCancelled else { return }
                let enumerator = fm.enumerator(at: folder.url, includingPropertiesForKeys: [.fileSizeKey, .isRegularFileKey], options: [.skipsHiddenFiles])
                while let fileURL = enumerator?.nextObject() as? URL {
                    guard !Task.isCancelled else { return }
                    if let values = try? fileURL.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey]),
                       values.isRegularFile == true {
                        total += Int64(values.fileSize ?? 0)
                    }
                }
            }
            guard !Task.isCancelled else { return }
            self.selectionSize = total
            self.isCalculatingSelectionSize = false
        }
    }

    func deleteSelectedItems() async {
        for item in selectedItems {
            do {
                try await FileSystemService.shared.deleteItem(at: item.url)
            } catch {
                self.error = "Failed to delete \(item.name): \(error.localizedDescription)"
                return
            }
        }
        selectedItemIDs.removeAll()
        await loadDirectory()
    }

    func deleteItems(_ items: [FileItem]) async {
        for item in items {
            do {
                try await FileSystemService.shared.deleteItem(at: item.url)
            } catch {
                self.error = "Failed to delete \(item.name): \(error.localizedDescription)"
                return
            }
        }
    }

    func trashSelectedItems() async {
        for item in selectedItems {
            do {
                try await FileSystemService.shared.trashItem(at: item.url)
            } catch {
                self.error = "Failed to trash \(item.name): \(error.localizedDescription)"
                return
            }
        }
        selectedItemIDs.removeAll()
        await loadDirectory()
    }

    func createNewFolder(named name: String) async {
        let folderURL = currentDirectory.appendingPathComponent(name)
        do {
            try await FileSystemService.shared.createDirectory(at: folderURL)
            await loadDirectory()
        } catch {
            self.error = "Failed to create folder: \(error.localizedDescription)"
        }
    }

    func renameItem(_ item: FileItem, to newName: String) async {
        let newURL = item.url.deletingLastPathComponent().appendingPathComponent(newName)
        do {
            try await FileSystemService.shared.moveItem(from: item.url, to: newURL)
            await loadDirectory()
        } catch {
            self.error = "Failed to rename: \(error.localizedDescription)"
        }
    }

    func refresh() async {
        await loadDirectory()
    }

    // MARK: - Drag & Drop

    func performMove(items: [FileItem], to folder: URL, progress: TransferProgress? = nil) async {
        progress?.transferredFileNames = items.map(\.name)
        progress?.transferredFolderNames = Set(items.filter(\.isDirectory).map(\.name))

        var applyToAllChoice: ConflictChoice?
        for (index, item) in items.enumerated() {
            var dest = folder.appendingPathComponent(item.name)
            progress?.currentFileName = item.name

            if FileManager.default.fileExists(atPath: dest.path) {
                let choice: ConflictChoice
                if let saved = applyToAllChoice {
                    choice = saved
                } else {
                    let remaining = items[(index + 1)...].filter {
                        FileManager.default.fileExists(atPath: folder.appendingPathComponent($0.name).path)
                    }.count
                    let destInfo = Self.localFileInfo(at: dest)
                    let resolution = await withCheckedContinuation { continuation in
                        self.conflictContinuation = continuation
                        self.pendingConflict = PendingConflict(
                            source: ConflictFileInfo(name: item.name, date: item.modificationDate, size: item.size, fileExtension: item.url.pathExtension, localURL: item.url),
                            destination: destInfo,
                            remainingConflicts: remaining,
                            direction: self.conflictDirection
                        )
                    }
                    choice = resolution.choice
                    if resolution.applyToAll { applyToAllChoice = choice }
                }
                switch choice {
                case .skip:
                    progress?.recordSkip(item.name)
                    progress?.completedItems = index + 1
                    continue
                case .stop:
                    progress?.endTime = Date(); progress?.isComplete = true; await loadDirectory(); return
                case .keepBoth:
                    dest = Self.uniqueDestination(for: dest)
                case .replace: break
                }
            }

            do {
                if FileManager.default.fileExists(atPath: dest.path) {
                    try FileManager.default.removeItem(at: dest)
                }
                progress?.totalBytes += item.size
                try await FileSystemService.shared.moveItem(from: item.url, to: dest)
                progress?.completedItems = index + 1
                progress?.recordSuccess(item.name)
            } catch {
                self.error = "Failed to move \(item.name): \(error.localizedDescription)"
                progress?.recordFailure(item.name, error: error.localizedDescription)
            }
        }
        progress?.endTime = Date()
        progress?.isComplete = true
        await loadDirectory()
    }

    func performCopy(items: [FileItem], to folder: URL, progress: TransferProgress? = nil) async {
        progress?.transferredFileNames = items.map(\.name)
        progress?.transferredFolderNames = Set(items.filter(\.isDirectory).map(\.name))

        var applyToAllChoice: ConflictChoice?
        for (index, item) in items.enumerated() {
            var dest = folder.appendingPathComponent(item.name)
            progress?.currentFileName = item.name

            if FileManager.default.fileExists(atPath: dest.path) {
                let choice: ConflictChoice
                if let saved = applyToAllChoice {
                    choice = saved
                } else {
                    let remaining = items[(index + 1)...].filter {
                        FileManager.default.fileExists(atPath: folder.appendingPathComponent($0.name).path)
                    }.count
                    let destInfo = Self.localFileInfo(at: dest)
                    let resolution = await withCheckedContinuation { continuation in
                        self.conflictContinuation = continuation
                        self.pendingConflict = PendingConflict(
                            source: ConflictFileInfo(name: item.name, date: item.modificationDate, size: item.size, fileExtension: item.url.pathExtension, localURL: item.url),
                            destination: destInfo,
                            remainingConflicts: remaining,
                            direction: self.conflictDirection
                        )
                    }
                    choice = resolution.choice
                    if resolution.applyToAll { applyToAllChoice = choice }
                }
                switch choice {
                case .skip:
                    progress?.recordSkip(item.name)
                    progress?.completedItems = index + 1
                    continue
                case .stop:
                    progress?.endTime = Date(); progress?.isComplete = true; await loadDirectory(); return
                case .keepBoth:
                    dest = Self.uniqueDestination(for: dest)
                case .replace: break
                }
            }

            do {
                if FileManager.default.fileExists(atPath: dest.path) {
                    try FileManager.default.removeItem(at: dest)
                }
                progress?.totalBytes += item.size
                try await FileSystemService.shared.copyItem(from: item.url, to: dest)
                progress?.completedItems = index + 1
                progress?.recordSuccess(item.name)
            } catch {
                self.error = "Failed to copy \(item.name): \(error.localizedDescription)"
                progress?.recordFailure(item.name, error: error.localizedDescription)
            }
        }
        progress?.endTime = Date()
        progress?.isComplete = true
        await loadDirectory()
    }

    // MARK: - Quick Look

    func toggleQuickLook() {
        let urls = selectedItems.filter { !$0.isDirectory }.map(\.url)
        quickLookController.urls = urls
        quickLookController.toggle()
    }

    func updateQuickLookSelection() {
        let urls = selectedItems.filter { !$0.isDirectory }.map(\.url)
        quickLookController.updateAndReload(urls: urls)
    }

    // MARK: - Private

    private func pushToHistory(_ url: URL) {
        if pathHistoryIndex < pathHistory.count - 1 {
            pathHistory.removeSubrange((pathHistoryIndex + 1)...)
        }
        pathHistory.append(url)
        pathHistoryIndex = pathHistory.count - 1
    }

    static func localFileInfo(at url: URL) -> ConflictFileInfo {
        let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey, .totalFileSizeKey])
        return ConflictFileInfo(
            name: url.lastPathComponent,
            date: values?.contentModificationDate ?? .distantPast,
            size: Int64(values?.totalFileSize ?? values?.fileSize ?? 0),
            fileExtension: url.pathExtension,
            localURL: url
        )
    }

    static func uniqueDestination(for url: URL) -> URL {
        let fm = FileManager.default
        let dir = url.deletingLastPathComponent()
        let name = url.deletingPathExtension().lastPathComponent
        let ext = url.pathExtension
        var counter = 2
        while true {
            let candidate: URL
            if ext.isEmpty {
                candidate = dir.appendingPathComponent("\(name) \(counter)")
            } else {
                candidate = dir.appendingPathComponent("\(name) \(counter).\(ext)")
            }
            if !fm.fileExists(atPath: candidate.path) { return candidate }
            counter += 1
        }
    }

    private func sorted(_ items: [FileItem]) -> [FileItem] {
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
            case .dateCreated:
                result = a.creationDate < b.creationDate
            case .size:
                result = a.size < b.size
            case .kind:
                result = a.kind.localizedStandardCompare(b.kind) == .orderedAscending
            }
            return sortAscending ? result : !result
        }
    }
}
