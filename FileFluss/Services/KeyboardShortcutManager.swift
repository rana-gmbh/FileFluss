import Foundation
import SwiftUI

/// Single source of truth for keyboard shortcut bindings. The user picks a
/// preset (Finder or Commander) and can override any individual command.
/// `FileCommands.swift` reads `binding(for:)` per command and applies the
/// resulting `KeyboardShortcut` to its menu item — when the manager's
/// state changes, the Commands view rebuilds and the shortcut updates.
@Observable @MainActor
final class KeyboardShortcutManager {
    static let shared = KeyboardShortcutManager()

    enum Preset: String, CaseIterable, Codable, Identifiable {
        case finder
        case commander
        var id: String { rawValue }
        var displayName: String {
            switch self {
            case .finder: return "macOS / Finder Style"
            case .commander: return "Commander Style"
            }
        }
    }

    var preset: Preset {
        didSet { savePreset() }
    }
    /// Per-command overrides that win over the preset default. A nil entry
    /// (or absence from the dict) means "use preset default". A binding
    /// whose `key` is the empty string means "user explicitly cleared
    /// this shortcut" — menu item still appears but without a hotkey.
    var customBindings: [KeyboardCommand: ShortcutBinding] {
        didSet { saveCustom() }
    }

    private static let presetKey = "keyboard.preset"
    private static let customKey = "keyboard.customBindings"

    private init() {
        if let raw = UserDefaults.standard.string(forKey: Self.presetKey),
           let p = Preset(rawValue: raw) {
            self.preset = p
        } else {
            self.preset = .finder
        }
        if let data = UserDefaults.standard.data(forKey: Self.customKey),
           let decoded = try? JSONDecoder().decode([String: ShortcutBinding].self, from: data) {
            var bindings: [KeyboardCommand: ShortcutBinding] = [:]
            for (k, v) in decoded {
                if let cmd = KeyboardCommand(rawValue: k) { bindings[cmd] = v }
            }
            self.customBindings = bindings
        } else {
            self.customBindings = [:]
        }
    }

    // MARK: - Public

    /// Effective binding for the command. If the user cleared the binding
    /// (empty key), returns nil so the menu item appears without a shortcut.
    func binding(for command: KeyboardCommand) -> ShortcutBinding? {
        if let custom = customBindings[command] {
            return custom.key.isEmpty ? nil : custom
        }
        return defaults(for: preset)[command]
    }

    /// Whether the user has overridden the preset default for this command.
    func isCustomised(_ command: KeyboardCommand) -> Bool {
        customBindings[command] != nil
    }

    func setBinding(_ binding: ShortcutBinding?, for command: KeyboardCommand) {
        customBindings[command] = binding
    }

    /// Clears the override so this command falls back to the preset default.
    func resetToPresetDefault(_ command: KeyboardCommand) {
        customBindings.removeValue(forKey: command)
    }

    func resetAllToPresetDefaults() {
        customBindings = [:]
    }

    // MARK: - Conflict detection

    /// Returns any other commands whose effective binding matches `candidate`
    /// (excluding `command` itself). Used by the recorder UI to warn the
    /// user before letting them overwrite a duplicate.
    func conflicts(with candidate: ShortcutBinding, ignoring command: KeyboardCommand) -> [KeyboardCommand] {
        KeyboardCommand.allCases.filter { other in
            other != command && binding(for: other) == candidate
        }
    }

    // MARK: - Persistence

    private func savePreset() {
        UserDefaults.standard.set(preset.rawValue, forKey: Self.presetKey)
    }

    private func saveCustom() {
        let stringKeyed = Dictionary(uniqueKeysWithValues: customBindings.map { ($0.key.rawValue, $0.value) })
        if let data = try? JSONEncoder().encode(stringKeyed) {
            UserDefaults.standard.set(data, forKey: Self.customKey)
        }
    }

    // MARK: - Presets

    func defaults(for preset: Preset) -> [KeyboardCommand: ShortcutBinding] {
        switch preset {
        case .finder: return Self.finderDefaults
        case .commander: return Self.commanderDefaults
        }
    }

    /// macOS / Finder-style defaults. Standard system idioms first; Commander
    /// muscle memory accommodated via alternates in the UI table only when
    /// it doesn't conflict with macOS conventions.
    static let finderDefaults: [KeyboardCommand: ShortcutBinding] = [
        // Panel navigation
        .toggleActivePanel:  .plain("tab"),
        .parentDirectory:    .cmd("up"),
        .historyBack:        .cmd("["),
        .historyForward:     .cmd("]"),
        .openInOtherPanel:   .cmdControl("right"),
        .focusPathBar:       .cmd("l"),
        .jumpToRoot:         .cmdShift("up"),

        // File operations
        .quickLook:          .plain("space"),
        .copyToOtherPanel:   .fn("f5"),
        .moveToOtherPanel:   .fn("f6"),
        .rename:             .plain("return"),
        .newFolder:          .cmdShift("n"),
        .deleteToTrash:      .cmd("delete"),
        .duplicate:          .cmd("d"),

        // Clipboard
        .copyFiles:          .cmd("c"),
        .cutFiles:           .cmd("x"),
        .pasteFiles:         .cmd("v"),
        .copyPath:           .cmdOption("c"),

        // Selection
        .selectAll:          .cmd("a"),
        .deselectAll:        .cmdShift("a"),
        .invertSelection:    .cmdOption("i"),
        .getInfo:            .cmd("i"),

        // View / sort
        .sortByName:         .cmd("1"),
        .sortByDate:         .cmd("2"),
        .sortBySize:         .cmd("3"),
        .sortByKind:         .cmd("4"),
        .toggleHiddenFiles:  .cmdShift("period"),

        // Cross-panel & sync
        .swapPanels:         .cmd("u"),
        .syncPanels:         .cmdShift("s"),
        .compareFolders:     .cmdControl("c"),
        .targetToSource:     .cmd("equals"),
        .refreshActive:      .cmd("r"),
        .refreshAll:         .cmdShift("r"),

        // Search
        .openSearch:         .cmd("f"),
        .quickFilter:        .cmdOption("f"),

        // Misc
        .openSettings:       .cmd("comma"),
        .indexCurrentSource: .cmdControl("i"),
    ]

    /// Total Commander-style defaults — preserves the iconic F-key bindings
    /// and TC's letter combinations where they don't clash with macOS.
    static let commanderDefaults: [KeyboardCommand: ShortcutBinding] = [
        // Panel navigation
        .toggleActivePanel:  .plain("tab"),
        .parentDirectory:    .plain("delete"),       // Backspace
        .historyBack:        .cmd("left"),
        .historyForward:     .cmd("right"),
        .openInOtherPanel:   .cmdControl("right"),
        .focusPathBar:       .cmd("l"),
        .jumpToRoot:         .cmd("backslash"),

        // File operations
        .quickLook:          .fn("f3"),
        .copyToOtherPanel:   .fn("f5"),
        .moveToOtherPanel:   .fn("f6"),
        .rename:             .fn("f2"),
        .newFolder:          .fn("f7"),
        .deleteToTrash:      .fn("f8"),
        .duplicate:          .init(key: "f5", modifiers: .shift),

        // Clipboard
        .copyFiles:          .cmd("c"),
        .cutFiles:           .cmd("x"),
        .pasteFiles:         .cmd("v"),
        .copyPath:           .cmd("p"),

        // Selection
        .selectAll:          .cmd("a"),
        .deselectAll:        .cmdShift("a"),
        .invertSelection:    .init(key: "8", modifiers: [.command]),  // ⌘8 ≈ NumPad *
        .getInfo:            .init(key: "return", modifiers: .option),

        // View / sort
        .sortByName:         .init(key: "f3", modifiers: .control),
        .sortByDate:         .init(key: "f5", modifiers: .control),
        .sortBySize:         .init(key: "f6", modifiers: .control),
        .sortByKind:         .init(key: "f4", modifiers: .control),
        .toggleHiddenFiles:  .cmdShift("period"),

        // Cross-panel & sync
        .swapPanels:         .cmd("u"),
        .syncPanels:         .cmdShift("s"),
        .compareFolders:     .init(key: "f2", modifiers: .shift),
        .targetToSource:     .cmd("i"),
        .refreshActive:      .cmd("r"),
        .refreshAll:         .fn("f2"),

        // Search
        .openSearch:         .init(key: "f7", modifiers: .option),
        .quickFilter:        .cmd("s"),

        // Misc
        .openSettings:       .cmd("comma"),
        .indexCurrentSource: .cmdControl("i"),
    ]
}
