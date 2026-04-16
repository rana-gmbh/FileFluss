import SwiftUI

struct FileCommands: Commands {
    let appState: AppState

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
        }

        CommandMenu("Sync") {
            Button("Sync Left and Right Panels…") {
                appState.showSyncSheet = true
            }
            .keyboardShortcut("s", modifiers: [.command, .shift])
        }

        CommandGroup(replacing: .appInfo) {
            Button("About FileFluss") {
                NSApplication.shared.orderFrontStandardAboutPanel(options: [
                    .credits: NSAttributedString(
                        html: Data("""
                        <div style="text-align: center; font-family: -apple-system; font-size: 11px;">
                            <p>Conveniently handle files across cloud storage providers.</p>
                            <p>Licensed under the \
                        <a href="https://www.gnu.org/licenses/gpl-3.0.html">GNU General Public License v3.0</a>.<br>\
                        Copyright © 2026 Rana GmbH.</p>
                            <p>If you want to support the FileFluss project<br>please consider \
                        <a href="https://buymeacoffee.com/robertrudolph">Buying me a coffee</a>.</p>
                        </div>
                        """.utf8),
                        documentAttributes: nil
                    )!
                ])
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
