import Foundation
import FileFlussCore

/// What each panel shows when FileFluss opens.
///
/// Requested in issue #49: either carry on where the last session left off,
/// or start from folders the user picked once and wants every time.
enum PanelStartupMode: String, CaseIterable, Identifiable {
    /// The home folder in both panels — the behaviour before this existed,
    /// and still the default so nobody's app changes under them.
    case home
    /// Whatever each panel was showing when the app last quit.
    case lastSession
    /// Folders the user chose in Settings.
    case specific

    var id: String { rawValue }

    var label: String {
        switch self {
        case .home: return "Home folder"
        case .lastSession: return "The folders from last time"
        case .specific: return "Specific folders"
        }
    }
}

/// A panel's location, in a form that survives a relaunch.
///
/// Deliberately not `SidebarItem`: that carries a whole `CloudAccount`,
/// which would go stale, and cases (favourites, offline sources) that are
/// not meaningful to restore into.
enum PanelLocation: Codable, Equatable {
    case local(path: String)
    case cloud(accountId: UUID, path: String)

    var isCloud: Bool {
        if case .cloud = self { return true }
        return false
    }
}

/// Stores and restores where each panel was.
///
/// Restoring has to cope with a world that moved on while the app was
/// closed: an external drive unplugged, a folder deleted, a cloud account
/// removed or signed out. Every one of those falls back to the home folder
/// rather than presenting an error at launch — the user opened the app to
/// look at files, not to be told about yesterday's state.
@MainActor
enum PanelStartupState {
    private static let modeKey = "panelStartupMode"
    private static let lastSessionKey = "panelLastSession"
    private static let specificKey = "panelSpecificFolders"

    static var mode: PanelStartupMode {
        get {
            guard let raw = UserDefaults.standard.string(forKey: modeKey),
                  let mode = PanelStartupMode(rawValue: raw) else { return .home }
            return mode
        }
        set { UserDefaults.standard.set(newValue.rawValue, forKey: modeKey) }
    }

    // MARK: - Recording

    /// Records where the panels are. Called as the app quits rather than on
    /// every navigation: the value is only ever read at launch, so writing
    /// it continuously would be noise.
    static func recordSession(left: PanelLocation?, right: PanelLocation?) {
        store(left: left, right: right, forKey: lastSessionKey)
    }

    static func lastSession() -> (left: PanelLocation?, right: PanelLocation?) {
        load(forKey: lastSessionKey)
    }

    static func setSpecificFolders(left: PanelLocation?, right: PanelLocation?) {
        store(left: left, right: right, forKey: specificKey)
    }

    static func specificFolders() -> (left: PanelLocation?, right: PanelLocation?) {
        load(forKey: specificKey)
    }

    /// The locations to open with, per the current mode. Nil for a panel
    /// means "use the home folder".
    static func startupLocations() -> (left: PanelLocation?, right: PanelLocation?) {
        switch mode {
        case .home: return (nil, nil)
        case .lastSession: return lastSession()
        case .specific: return specificFolders()
        }
    }

    // MARK: - Validation

    /// True when this location can still be opened. A path that has gone —
    /// deleted folder, unplugged drive — must not be restored into, or the
    /// panel opens onto an error.
    static func isUsable(_ location: PanelLocation, connectedAccountIDs: Set<UUID>) -> Bool {
        switch location {
        case .local(let path):
            var isDirectory: ObjCBool = false
            let exists = FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory)
            return exists && isDirectory.boolValue
        case .cloud(let accountId, _):
            // A signed-out or removed account can't be opened. The panel
            // falls back rather than showing a connection error at launch.
            return connectedAccountIDs.contains(accountId)
        }
    }

    // MARK: - Storage

    private struct StoredPair: Codable {
        var left: PanelLocation?
        var right: PanelLocation?
    }

    private static func store(left: PanelLocation?, right: PanelLocation?, forKey key: String) {
        let pair = StoredPair(left: left, right: right)
        guard let data = try? JSONEncoder().encode(pair) else { return }
        UserDefaults.standard.set(data, forKey: key)
    }

    private static func load(forKey key: String) -> (left: PanelLocation?, right: PanelLocation?) {
        guard let data = UserDefaults.standard.data(forKey: key),
              let pair = try? JSONDecoder().decode(StoredPair.self, from: data) else {
            return (nil, nil)
        }
        return (pair.left, pair.right)
    }
}
