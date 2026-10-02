import AppKit
import Foundation
import FileFlussCore

/// Opens a terminal at a folder (issue #61).
///
/// Two decisions live here, both of them the kind a user notices when they
/// are wrong, and both pure so they can be tested: which folder the command
/// means, and which terminal application to hand it to.
enum TerminalLauncher {

    /// Terminals FileFluss offers by name in Settings. Anything else is
    /// reachable through "Choose…", which stores whatever bundle the user
    /// picked.
    ///
    /// Only terminals that actually act on a folder handed to them are
    /// listed. Alacritty, for one, ignores it and opens at the home folder —
    /// offering it by name would look like a FileFluss bug.
    struct KnownTerminal: Identifiable, Hashable {
        let bundleID: String
        let name: String
        var id: String { bundleID }
    }

    static let knownTerminals: [KnownTerminal] = [
        KnownTerminal(bundleID: "com.apple.Terminal", name: "Terminal"),
        KnownTerminal(bundleID: "com.googlecode.iterm2", name: "iTerm"),
        KnownTerminal(bundleID: "com.mitchellh.ghostty", name: "Ghostty"),
        KnownTerminal(bundleID: "net.kovidgoyal.kitty", name: "kitty"),
        KnownTerminal(bundleID: "com.github.wez.wezterm", name: "WezTerm"),
        KnownTerminal(bundleID: "dev.warp.Warp-Stable", name: "Warp"),
    ]

    static let defaultBundleID = "com.apple.Terminal"

    /// The preference key holding the chosen terminal's bundle identifier.
    /// A bundle id rather than a path: moving an app to a different folder
    /// shouldn't quietly break the command.
    static let bundleIDKey = "terminalAppBundleID"

    // MARK: - Which folder

    /// The folder "Open in Terminal" means, given what is selected.
    ///
    /// Finder's rule: one folder selected means that folder; anything else —
    /// nothing, a file, several items — means the folder you are looking at.
    /// Opening one terminal per selected folder is rarely what was meant.
    static func targetDirectory(selection: [URL], isDirectory: (URL) -> Bool, currentDirectory: URL) -> URL {
        guard selection.count == 1, let only = selection.first, isDirectory(only) else {
            return currentDirectory
        }
        return only
    }

    /// Convenience for the local panel, where each item already knows
    /// whether it is a directory.
    static func targetDirectory(selection: [FileItem], currentDirectory: URL) -> URL {
        guard selection.count == 1, let only = selection.first, only.isDirectory else {
            return currentDirectory
        }
        return only.url
    }

    /// The same rule for a cloud panel, whose items are remote paths rather
    /// than URLs.
    static func targetDirectory(selection: [(path: String, isDirectory: Bool)], currentPath: String) -> String {
        guard selection.count == 1, let only = selection.first, only.isDirectory else {
            return currentPath
        }
        return only.path
    }

    /// Maps a remote path to its counterpart inside a mounted volume.
    ///
    /// The mount serves `providerRoot` at its root, so that prefix comes off
    /// before the rest is appended — a mount rooted at `/Backups` makes
    /// `/Backups/2026` into `<mount>/2026`, not `<mount>/Backups/2026`.
    static func mountedURL(forRemotePath remotePath: String, mountPoint: URL, providerRoot: String) -> URL {
        var relative = remotePath
        if providerRoot != "/", relative.hasPrefix(providerRoot) {
            relative = String(relative.dropFirst(providerRoot.count))
        }
        while relative.hasPrefix("/") { relative.removeFirst() }
        guard !relative.isEmpty else { return mountPoint }
        return mountPoint.appendingPathComponent(relative)
    }

    // MARK: - Which terminal

    /// Resolves the configured terminal, falling back to Terminal.app when
    /// the chosen one has been removed. Returns nil only if even Terminal is
    /// missing, which the caller reports rather than failing silently.
    static func terminalURL(
        bundleID: String?,
        resolve: (String) -> URL? = { NSWorkspace.shared.urlForApplication(withBundleIdentifier: $0) }
    ) -> URL? {
        if let bundleID, !bundleID.isEmpty, let url = resolve(bundleID) { return url }
        return resolve(defaultBundleID)
    }

    /// The terminals from `knownTerminals` that are installed right now.
    static func installedTerminals() -> [KnownTerminal] {
        knownTerminals.filter {
            NSWorkspace.shared.urlForApplication(withBundleIdentifier: $0.bundleID) != nil
        }
    }

    // MARK: - Launching

    enum LaunchError: LocalizedError {
        case noTerminalFound
        case notADirectory(URL)

        var errorDescription: String? {
            switch self {
            case .noTerminalFound:
                return L10n.text("No terminal application could be found.")
            case .notADirectory(let url):
                return L10n.format("%@ is not a folder.", url.lastPathComponent)
            }
        }
    }

    /// Opens `directory` in the configured terminal.
    @MainActor
    static func open(directory: URL, bundleID: String? = UserDefaults.standard.string(forKey: bundleIDKey)) throws {
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: directory.path, isDirectory: &isDir), isDir.boolValue else {
            throw LaunchError.notADirectory(directory)
        }
        guard let app = terminalURL(bundleID: bundleID) else {
            throw LaunchError.noTerminalFound
        }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        NSWorkspace.shared.open([directory], withApplicationAt: app, configuration: configuration)
    }
}
