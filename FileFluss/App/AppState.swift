import SwiftUI
import Combine

@Observable @MainActor
final class AppState {
    var fileManager: FileManagerViewModel
    var syncManager: SyncViewModel
    var selectedSidebarItem: SidebarItem? = .home

    init() {
        self.fileManager = FileManagerViewModel()
        self.syncManager = SyncViewModel()
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
