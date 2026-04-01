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
    var pendingOverwrite: PendingOverwrite?

    struct PendingDrop {
        let items: [FileItem]
        let destinationFolder: URL
    }

    struct PendingOverwrite {
        let conflicting: [String]  // names that already exist
        let items: [FileItem]
        let destinationFolder: URL
        let operation: OverwriteOperation
        let progress: TransferProgress?
    }

    enum OverwriteOperation {
        case copy, move
    }

    // Quick Look — controlled directly, no SwiftUI binding
    let quickLookController = QuickLookController()

    enum SortOrder: String, CaseIterable {
        case name, date, size, kind

        var label: String {
            switch self {
            case .name: return "Name"
            case .date: return "Date Modified"
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

    func performMove(items: [FileItem], to folder: URL, progress: TransferProgress? = nil, overwrite: Bool = false) async {
        if !overwrite {
            let conflicts = items.filter { FileManager.default.fileExists(atPath: folder.appendingPathComponent($0.name).path) }
            if !conflicts.isEmpty {
                pendingOverwrite = PendingOverwrite(
                    conflicting: conflicts.map(\.name),
                    items: items,
                    destinationFolder: folder,
                    operation: .move,
                    progress: progress
                )
                return
            }
        }
        progress?.transferredFileNames = items.map(\.name)
        progress?.transferredFolderNames = Set(items.filter(\.isDirectory).map(\.name))
        for (index, item) in items.enumerated() {
            let dest = folder.appendingPathComponent(item.name)
            progress?.currentFileName = item.name
            do {
                if overwrite && FileManager.default.fileExists(atPath: dest.path) {
                    try FileManager.default.removeItem(at: dest)
                }
                progress?.totalBytes += item.size
                try await FileSystemService.shared.moveItem(from: item.url, to: dest)
                progress?.completedItems = index + 1
            } catch {
                self.error = "Failed to move \(item.name): \(error.localizedDescription)"
                progress?.errorMessage = error.localizedDescription
                progress?.endTime = Date()
                progress?.isComplete = true
                return
            }
        }
        progress?.endTime = Date()
        progress?.isComplete = true
        await loadDirectory()
    }

    func performCopy(items: [FileItem], to folder: URL, progress: TransferProgress? = nil, overwrite: Bool = false) async {
        if !overwrite {
            let conflicts = items.filter { FileManager.default.fileExists(atPath: folder.appendingPathComponent($0.name).path) }
            if !conflicts.isEmpty {
                pendingOverwrite = PendingOverwrite(
                    conflicting: conflicts.map(\.name),
                    items: items,
                    destinationFolder: folder,
                    operation: .copy,
                    progress: progress
                )
                return
            }
        }
        progress?.transferredFileNames = items.map(\.name)
        progress?.transferredFolderNames = Set(items.filter(\.isDirectory).map(\.name))
        for (index, item) in items.enumerated() {
            let dest = folder.appendingPathComponent(item.name)
            progress?.currentFileName = item.name
            do {
                if overwrite && FileManager.default.fileExists(atPath: dest.path) {
                    try FileManager.default.removeItem(at: dest)
                }
                progress?.totalBytes += item.size
                try await FileSystemService.shared.copyItem(from: item.url, to: dest)
                progress?.completedItems = index + 1
            } catch {
                self.error = "Failed to copy \(item.name): \(error.localizedDescription)"
                progress?.errorMessage = error.localizedDescription
                progress?.endTime = Date()
                progress?.isComplete = true
                return
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
            case .size:
                result = a.size < b.size
            case .kind:
                let aExt = a.url.pathExtension
                let bExt = b.url.pathExtension
                result = aExt.localizedStandardCompare(bExt) == .orderedAscending
            }
            return sortAscending ? result : !result
        }
    }
}
