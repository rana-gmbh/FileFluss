import SwiftUI
import Combine

enum PanelSide: Hashable {
    case left, right
}

@Observable @MainActor
final class AppState {
    var leftFileManager: FileManagerViewModel
    var rightFileManager: FileManagerViewModel
    var syncManager: SyncViewModel

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

    func fileManager(for panel: PanelSide) -> FileManagerViewModel {
        panel == .left ? leftFileManager : rightFileManager
    }

    func sidebarSelection(for panel: PanelSide) -> SidebarItem? {
        panel == .left ? selectedLeftSidebarItem : selectedRightSidebarItem
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

    // Folder size calculations per panel
    var leftFolderSizes: [FolderSizeEntry] = []
    var rightFolderSizes: [FolderSizeEntry] = []

    func folderSizes(for panel: PanelSide) -> [FolderSizeEntry] {
        panel == .left ? leftFolderSizes : rightFolderSizes
    }

    func calculateFolderSize(for url: URL, panel: PanelSide) {
        let entries = panel == .left ? leftFolderSizes : rightFolderSizes
        // Don't add duplicate
        guard !entries.contains(where: { $0.url == url }) else { return }

        let entry = FolderSizeEntry(url: url)
        if panel == .left {
            leftFolderSizes.append(entry)
        } else {
            rightFolderSizes.append(entry)
        }

        Task {
            do {
                let size = try await FileSystemService.shared.directorySize(at: url)
                if panel == .left {
                    if let idx = leftFolderSizes.firstIndex(where: { $0.url == url }) {
                        leftFolderSizes[idx].size = size
                        leftFolderSizes[idx].isCalculating = false
                    }
                } else {
                    if let idx = rightFolderSizes.firstIndex(where: { $0.url == url }) {
                        rightFolderSizes[idx].size = size
                        rightFolderSizes[idx].isCalculating = false
                    }
                }
            } catch {
                if panel == .left {
                    if let idx = leftFolderSizes.firstIndex(where: { $0.url == url }) {
                        leftFolderSizes[idx].size = 0
                        leftFolderSizes[idx].isCalculating = false
                    }
                } else {
                    if let idx = rightFolderSizes.firstIndex(where: { $0.url == url }) {
                        rightFolderSizes[idx].size = 0
                        rightFolderSizes[idx].isCalculating = false
                    }
                }
            }
        }
    }

    func removeFolderSize(at url: URL, panel: PanelSide) {
        if panel == .left {
            leftFolderSizes.removeAll { $0.url == url }
        } else {
            rightFolderSizes.removeAll { $0.url == url }
        }
    }

    init() {
        self.leftFileManager = FileManagerViewModel()
        self.rightFileManager = FileManagerViewModel()
        self.syncManager = SyncViewModel()
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
    let url: URL
    var size: Int64?
    var isCalculating: Bool = true

    var name: String { url.lastPathComponent }

    var formattedSize: String {
        guard let size else { return "Calculating…" }
        return ByteCountFormatter.string(fromByteCount: size, countStyle: .file)
    }
}

enum SidebarItem: Hashable, Identifiable {
    case home
    case favorites
    case location(URL)
    case cloudAccount(CloudAccount)
    case syncRules

    var id: String {
        switch self {
        case .home: return "home"
        case .favorites: return "favorites"
        case .location(let url): return url.path()
        case .cloudAccount(let account): return account.id.uuidString
        case .syncRules: return "syncRules"
        }
    }
}
