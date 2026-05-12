import Foundation
import SwiftUI

/// Every user-bindable command in FileFluss. Each command lives in one
/// `Section` and has both a Finder-style and a Commander-style default
/// shortcut. The user can override either via Settings → Keyboard Map.
///
/// Adding a new command is a 4-step process:
///   1. Add a case here and its `displayName` / `section`.
///   2. Add a notification name in `Notification.Name` (see AppState.swift).
///   3. Provide default bindings in `KeyboardShortcutManager.finderDefaults`
///      and `commanderDefaults`.
///   4. Wire a menu item in `FileCommands.swift` that posts the notification,
///      and add a listener wherever the action needs to run.
enum KeyboardCommand: String, CaseIterable, Codable, Identifiable, Hashable {
    // Panel navigation
    case toggleActivePanel
    case parentDirectory
    case historyBack
    case historyForward
    case openInOtherPanel
    case focusPathBar
    case jumpToRoot

    // File operations
    case quickLook
    case copyToOtherPanel
    case moveToOtherPanel
    case rename
    case newFolder
    case deleteToTrash
    case duplicate

    // Clipboard
    case copyFiles
    case cutFiles
    case pasteFiles
    case copyPath

    // Selection
    case selectAll
    case deselectAll
    case invertSelection
    case getInfo

    // View / sort
    case sortByName
    case sortByDate
    case sortBySize
    case sortByKind
    case toggleHiddenFiles

    // Cross-panel & sync
    case swapPanels
    case syncPanels
    case compareFolders
    case targetToSource
    case refreshActive
    case refreshAll

    // Search
    case openSearch
    case quickFilter

    // Misc
    case openSettings
    case indexCurrentSource

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .toggleActivePanel: return "Toggle Active Panel"
        case .parentDirectory: return "Parent Directory"
        case .historyBack: return "Back"
        case .historyForward: return "Forward"
        case .openInOtherPanel: return "Open in Other Panel"
        case .focusPathBar: return "Go to Folder…"
        case .jumpToRoot: return "Jump to Root"
        case .quickLook: return "Quick Look"
        case .copyToOtherPanel: return "Copy to Other Panel"
        case .moveToOtherPanel: return "Move to Other Panel"
        case .rename: return "Rename"
        case .newFolder: return "New Folder"
        case .deleteToTrash: return "Move to Trash"
        case .duplicate: return "Duplicate"
        case .copyFiles: return "Copy"
        case .cutFiles: return "Cut"
        case .pasteFiles: return "Paste"
        case .copyPath: return "Copy Path"
        case .selectAll: return "Select All"
        case .deselectAll: return "Deselect All"
        case .invertSelection: return "Invert Selection"
        case .getInfo: return "Get Info"
        case .sortByName: return "Sort by Name"
        case .sortByDate: return "Sort by Date"
        case .sortBySize: return "Sort by Size"
        case .sortByKind: return "Sort by Kind"
        case .toggleHiddenFiles: return "Show Hidden Files"
        case .swapPanels: return "Swap Panels"
        case .syncPanels: return "Sync Left and Right Panels"
        case .compareFolders: return "Compare Folders"
        case .targetToSource: return "Target → Source"
        case .refreshActive: return "Refresh Active Panel"
        case .refreshAll: return "Refresh Both Panels"
        case .openSearch: return "Search"
        case .quickFilter: return "Quick Filter"
        case .openSettings: return "Settings"
        case .indexCurrentSource: return "Index Current Source"
        }
    }

    enum Section: String, CaseIterable {
        case panelNavigation = "Panel Navigation"
        case fileOperations = "File Operations"
        case clipboard = "Clipboard"
        case selection = "Selection"
        case viewSort = "View & Sort"
        case crossPanelTools = "Cross-panel & Tools"
        case search = "Search"
        case misc = "Misc"
    }

    var section: Section {
        switch self {
        case .toggleActivePanel, .parentDirectory, .historyBack, .historyForward,
             .openInOtherPanel, .targetToSource, .focusPathBar, .jumpToRoot:
            return .panelNavigation
        case .quickLook, .copyToOtherPanel, .moveToOtherPanel, .rename, .newFolder,
             .deleteToTrash, .duplicate:
            return .fileOperations
        case .copyFiles, .cutFiles, .pasteFiles, .copyPath:
            return .clipboard
        case .selectAll, .deselectAll, .invertSelection, .getInfo:
            return .selection
        case .sortByName, .sortByDate, .sortBySize, .sortByKind, .toggleHiddenFiles:
            return .viewSort
        case .swapPanels, .syncPanels, .compareFolders, .refreshActive, .refreshAll:
            return .crossPanelTools
        case .openSearch, .quickFilter:
            return .search
        case .openSettings, .indexCurrentSource:
            return .misc
        }
    }

    /// Notification posted when the user activates this command via menu
    /// shortcut. Listeners in views/panels observe it to perform the action.
    var notification: Notification.Name {
        Notification.Name("FileFluss.command.\(rawValue)")
    }
}

/// A user-bindable keyboard shortcut.
///
/// Keys are stored as a canonical string identifier — single character for
/// letter/digit keys, and a token like `"return"`, `"up"`, `"space"` for
/// special keys. Modifiers are an OptionSet. The struct round-trips through
/// JSON / `UserDefaults` and maps both to a printable glyph string and to
/// SwiftUI's `KeyboardShortcut` for binding on menu items.
struct ShortcutBinding: Codable, Hashable {
    var key: String
    var modifiers: Modifiers

    struct Modifiers: OptionSet, Codable, Hashable {
        let rawValue: Int
        static let command = Modifiers(rawValue: 1 << 0)
        static let shift   = Modifiers(rawValue: 1 << 1)
        static let option  = Modifiers(rawValue: 1 << 2)
        static let control = Modifiers(rawValue: 1 << 3)
    }

    /// Human-readable representation like `⇧⌘N` or `⌘↑`. Order follows
    /// macOS conventions: ⌃⌥⇧⌘ then key glyph.
    var displayString: String {
        var s = ""
        if modifiers.contains(.control) { s += "⌃" }
        if modifiers.contains(.option)  { s += "⌥" }
        if modifiers.contains(.shift)   { s += "⇧" }
        if modifiers.contains(.command) { s += "⌘" }
        s += keyGlyph
        return s
    }

    private var keyGlyph: String {
        switch key {
        case "return":    return "↩"
        case "tab":       return "⇥"
        case "space":     return "Space"
        case "delete":    return "⌫"
        case "fwdDelete": return "⌦"
        case "up":        return "↑"
        case "down":      return "↓"
        case "left":      return "←"
        case "right":     return "→"
        case "esc":       return "⎋"
        case "home":      return "↖"
        case "end":       return "↘"
        case "pgUp":      return "⇞"
        case "pgDown":    return "⇟"
        case "plus":      return "+"
        case "minus":     return "-"
        case "equals":    return "="
        case "slash":     return "/"
        case "backslash": return "\\"
        case "period":    return "."
        case "comma":     return ","
        case "f1", "f2", "f3", "f4", "f5", "f6",
             "f7", "f8", "f9", "f10", "f11", "f12":
            return "F" + key.dropFirst()
        default:          return key.uppercased()
        }
    }

    /// Maps to SwiftUI `KeyboardShortcut` for use in `.keyboardShortcut(...)`.
    /// Returns nil when the binding can't be expressed as a SwiftUI shortcut
    /// (e.g. function-key tokens we haven't mapped). Menu items with a nil
    /// shortcut still appear, just without a key combo.
    var swiftUIShortcut: KeyboardShortcut? {
        let keyEquiv: KeyEquivalent
        switch key {
        case "return":    keyEquiv = .return
        case "tab":       keyEquiv = .tab
        case "space":     keyEquiv = .space
        case "delete":    keyEquiv = .delete
        case "fwdDelete": keyEquiv = .deleteForward
        case "up":        keyEquiv = .upArrow
        case "down":      keyEquiv = .downArrow
        case "left":      keyEquiv = .leftArrow
        case "right":     keyEquiv = .rightArrow
        case "esc":       keyEquiv = .escape
        case "home":      keyEquiv = .home
        case "end":       keyEquiv = .end
        case "pgUp":      keyEquiv = .pageUp
        case "pgDown":    keyEquiv = .pageDown
        case "plus":      keyEquiv = KeyEquivalent("+")
        case "minus":     keyEquiv = KeyEquivalent("-")
        case "equals":    keyEquiv = KeyEquivalent("=")
        case "slash":     keyEquiv = KeyEquivalent("/")
        case "backslash": keyEquiv = KeyEquivalent("\\")
        case "period":    keyEquiv = KeyEquivalent(".")
        case "comma":     keyEquiv = KeyEquivalent(",")
        // Function keys use the AppKit Unicode private-use range that
        // NSEvent reports for F1–F12 (NSF1FunctionKey = 0xF704, …).
        case "f1":  keyEquiv = KeyEquivalent("\u{F704}")
        case "f2":  keyEquiv = KeyEquivalent("\u{F705}")
        case "f3":  keyEquiv = KeyEquivalent("\u{F706}")
        case "f4":  keyEquiv = KeyEquivalent("\u{F707}")
        case "f5":  keyEquiv = KeyEquivalent("\u{F708}")
        case "f6":  keyEquiv = KeyEquivalent("\u{F709}")
        case "f7":  keyEquiv = KeyEquivalent("\u{F70A}")
        case "f8":  keyEquiv = KeyEquivalent("\u{F70B}")
        case "f9":  keyEquiv = KeyEquivalent("\u{F70C}")
        case "f10": keyEquiv = KeyEquivalent("\u{F70D}")
        case "f11": keyEquiv = KeyEquivalent("\u{F70E}")
        case "f12": keyEquiv = KeyEquivalent("\u{F70F}")
        default:
            guard let scalar = key.unicodeScalars.first, key.unicodeScalars.count == 1 else { return nil }
            keyEquiv = KeyEquivalent(Character(scalar))
        }
        var mods: EventModifiers = []
        if modifiers.contains(.command) { mods.insert(.command) }
        if modifiers.contains(.shift)   { mods.insert(.shift) }
        if modifiers.contains(.option)  { mods.insert(.option) }
        if modifiers.contains(.control) { mods.insert(.control) }
        return KeyboardShortcut(keyEquiv, modifiers: mods)
    }

    // MARK: - Convenience constructors

    static func cmd(_ key: String) -> ShortcutBinding { .init(key: key, modifiers: .command) }
    static func cmdShift(_ key: String) -> ShortcutBinding { .init(key: key, modifiers: [.command, .shift]) }
    static func cmdOption(_ key: String) -> ShortcutBinding { .init(key: key, modifiers: [.command, .option]) }
    static func cmdControl(_ key: String) -> ShortcutBinding { .init(key: key, modifiers: [.command, .control]) }
    static func cmdOptionShift(_ key: String) -> ShortcutBinding { .init(key: key, modifiers: [.command, .option, .shift]) }
    static func plain(_ key: String) -> ShortcutBinding { .init(key: key, modifiers: []) }
    static func fn(_ key: String) -> ShortcutBinding { .init(key: key, modifiers: []) }
}
