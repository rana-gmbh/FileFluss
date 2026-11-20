import Foundation

/// How a panel lays out its folder: rows with columns, or a grid of
/// thumbnails. Each panel keeps its own choice, so a photo folder can sit in
/// a gallery on one side while the other side stays a list.
enum FileViewMode: String, CaseIterable {
    case list
    case gallery
}

/// Where each panel's view mode is kept between launches. List is the
/// default, so a first launch — and anyone who never touches the switch —
/// sees exactly what FileFluss always showed.
enum FileViewModePrefs {
    static func storageKey(for side: PanelSide) -> String {
        switch side {
        case .left: return "FileList.viewMode.left"
        case .right: return "FileList.viewMode.right"
        }
    }

    static func load(for side: PanelSide, from defaults: UserDefaults = .standard) -> FileViewMode {
        defaults.string(forKey: storageKey(for: side)).flatMap(FileViewMode.init(rawValue:)) ?? .list
    }

    static func save(_ mode: FileViewMode, for side: PanelSide, to defaults: UserDefaults = .standard) {
        defaults.set(mode.rawValue, forKey: storageKey(for: side))
    }
}
