import SwiftUI

struct FileCommands: Commands {
    let appState: AppState
    @AppStorage("showStatusBar") private var showStatusBar = true

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("New Folder") {
                NotificationCenter.default.post(name: .menuNewFolder, object: nil)
            }
            .keyboardShortcut("n", modifiers: [.command, .shift])
            .disabled(!appState.canCreateFolderInActivePanel)

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

            Divider()

            Toggle("Show Status Bar", isOn: $showStatusBar)
                .keyboardShortcut("/", modifiers: [.command, .shift])
        }

        CommandMenu("Sync") {
            Button("Sync Left and Right Panels…") {
                appState.showSyncSheet = true
            }
            .keyboardShortcut("s", modifiers: [.command, .shift])
        }

        CommandGroup(replacing: .appInfo) {
            Button("About FileFluss") {
                AboutWindowController.shared.show()
            }
        }

        CommandGroup(replacing: .help) {
            HelpMenuButton()
            Divider()
            Button("Check for Updates…") {
                AboutWindowController.shared.show()
            }
            Button("GitHub Repository") {
                NSWorkspace.shared.open(URL(string: "https://github.com/rana-gmbh/filefluss")!)
            }
        }

        CommandGroup(before: .help) {
            Button("Support the FileFluss Project") {
                NSWorkspace.shared.open(URL(string: "https://buymeacoffee.com/robertrudolph")!)
            }
        }

        CommandGroup(after: .help) {
            Button(SupportLogService.shared.isRecording ? "Support Log (Recording…)" : "Support Log") {
                SupportLogService.shared.start()
            }
            .disabled(SupportLogService.shared.isRecording)

            #if DEBUG
            Button("Run Version Test…") {
                Task { await VersionTestRunner.run(appState: appState) }
            }
            #endif
        }
    }
}

/// The "FileFluss Help" entry under the Help menu. Opens our custom Help
/// window via openWindow — needs an environment value, so it's wrapped in
/// its own View instead of being inlined in the CommandGroup.
private struct HelpMenuButton: View {
    @Environment(\.openWindow) private var openWindow
    var body: some View {
        Button("FileFluss Help") {
            openWindow(id: "help")
        }
        .keyboardShortcut("?", modifiers: .command)
    }
}
