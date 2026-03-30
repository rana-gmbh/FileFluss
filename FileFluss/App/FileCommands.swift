import SwiftUI

struct FileCommands: Commands {
    let appState: AppState

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("New Folder") {
                NotificationCenter.default.post(name: .menuNewFolder, object: nil)
            }
            .keyboardShortcut("n", modifiers: [.command, .shift])

            Divider()

            Button("Rename") {
                NotificationCenter.default.post(name: .menuRename, object: nil)
            }
            .keyboardShortcut(.return, modifiers: [])
            .disabled(!appState.hasSingleSelection)

            Divider()

            Button("Copy to Other Panel") {
                NotificationCenter.default.post(name: .menuCopyToOtherPanel, object: nil)
            }
            .keyboardShortcut("c", modifiers: [.command, .shift])
            .disabled(!appState.hasSelection)

            Button("Move to Other Panel") {
                NotificationCenter.default.post(name: .menuMoveToOtherPanel, object: nil)
            }
            .keyboardShortcut("m", modifiers: [.command, .shift])
            .disabled(!appState.hasSelection)

            Divider()

            Button("Delete") {
                NotificationCenter.default.post(name: .menuDelete, object: nil)
            }
            .keyboardShortcut(.delete, modifiers: [.command])
            .disabled(!appState.hasSelection)
        }

        CommandGroup(after: .toolbar) {
            Button("Refresh") {
                Task { await appState.refreshAllPanels() }
            }
            .keyboardShortcut("r", modifiers: .command)
        }
    }
}
