import AppKit
import SwiftUI
import FileFlussCore

/// Forwards an Edit-menu action to the first responder in the key
/// window. Returns `true` if AppKit found a responder that handled it
/// (typically a text field or text view), `false` otherwise. We use
/// this in the Cut/Copy/Paste menu items so a focused text field gets
/// the standard text-editing behaviour while the file manager view
/// gets the file-clipboard behaviour. Without this, the file-clipboard
/// menu items swallow ⌘C/⌘X/⌘V everywhere, including inside sheets
/// like Add Cloud Account.
@MainActor
private func forwardToFirstResponder(_ selector: Selector) -> Bool {
    return NSApp.sendAction(selector, to: nil, from: nil)
}

struct FileCommands: Commands {
    let appState: AppState
    @AppStorage("showStatusBar") private var showStatusBar = true
    @AppStorage("hideFileExtensions") private var hideFileExtensions = false
    /// Drives the enabled/disabled state of the Increase/Decrease Row
    /// Size commands so SwiftUI re-evaluates them when the pref changes.
    @AppStorage(FileListRowSizePrefs.storageKey) private var rowSizeRaw = FileListRowSize.regular.rawValue

    // Read manager via the singleton so SwiftUI observes its state and the
    // Commands rebuild when bindings change.
    private var shortcuts: KeyboardShortcutManager { .shared }

    /// Adds the appropriate `.keyboardShortcut(...)` modifier to a menu
    /// button when the user (or preset) has a binding for `command`. If
    /// the binding is empty or unsupported by SwiftUI, the menu item
    /// still works — it just has no key combo.
    @ViewBuilder
    private func applyShortcut<V: View>(_ command: KeyboardCommand, to view: V) -> some View {
        if let binding = shortcuts.binding(for: command),
           let sk = binding.swiftUIShortcut {
            view.keyboardShortcut(sk)
        } else {
            view
        }
    }

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            applyShortcut(.newFolder, to: Button(L10n.text("New Folder")) {
                NotificationCenter.default.post(name: KeyboardCommand.newFolder.notification, object: nil)
            })
            .disabled(!appState.canCreateFolderInActivePanel)

            Divider()

            applyShortcut(.rename, to: Button(L10n.text("Rename")) {
                NotificationCenter.default.post(name: KeyboardCommand.rename.notification, object: nil)
            })
            .disabled(!appState.hasSingleSelection)

            applyShortcut(.duplicate, to: Button(L10n.text("Duplicate")) {
                NotificationCenter.default.post(name: KeyboardCommand.duplicate.notification, object: nil)
            })
            .disabled(!appState.hasSelection)

            applyShortcut(.getInfo, to: Button(L10n.text("Get Info")) {
                NotificationCenter.default.post(name: KeyboardCommand.getInfo.notification, object: nil)
            })
            .disabled(!appState.hasSelection)

            Divider()

            applyShortcut(.copyToOtherPanel, to: Button(L10n.text("Copy to Other Panel")) {
                NotificationCenter.default.post(name: KeyboardCommand.copyToOtherPanel.notification, object: nil)
            })
            .disabled(!appState.hasSelection)

            applyShortcut(.moveToOtherPanel, to: Button(L10n.text("Move to Other Panel")) {
                NotificationCenter.default.post(name: KeyboardCommand.moveToOtherPanel.notification, object: nil)
            })
            .disabled(!appState.hasSelection)

            applyShortcut(.copyPath, to: Button(L10n.text("Copy Path")) {
                NotificationCenter.default.post(name: KeyboardCommand.copyPath.notification, object: nil)
            })
            .disabled(!appState.hasSelection)

            Divider()

            applyShortcut(.deleteToTrash, to: Button(L10n.text("Move to Trash")) {
                NotificationCenter.default.post(name: KeyboardCommand.deleteToTrash.notification, object: nil)
            })
            .disabled(!appState.hasSelection)
        }

        // Replace the system Edit menu's Cut/Copy/Paste so our file-aware
        // versions own ⌘C/⌘X/⌘V and the responder chain doesn't steal them.
        // These call AppState directly rather than round-tripping through
        // NotificationCenter — Notification delivery from inside a SwiftUI
        // CommandGroup is unreliable in some configurations (the closure
        // fires but the observer doesn't always see the post), so the menu
        // path uses a guaranteed direct invocation.
        CommandGroup(replacing: .pasteboard) {
            // Each handler forwards to the key-window's first responder
            // first; only if no text field/view consumes the keystroke
            // do we fall through to the file-clipboard handler. This
            // restores standard ⌘C/⌘X/⌘V inside sheets, popovers, and
            // any other text input without giving up file-clipboard
            // ownership when the file manager itself is focused.
            applyShortcut(.cutFiles, to: Button(L10n.text("Cut")) {
                if forwardToFirstResponder(#selector(NSText.cut(_:))) { return }
                NSLog("[FileFluss] menu: Cut clicked")
                appState.captureFileClipboard(operation: .cut)
            })
            applyShortcut(.copyFiles, to: Button(L10n.text("Copy")) {
                if forwardToFirstResponder(#selector(NSText.copy(_:))) { return }
                NSLog("[FileFluss] menu: Copy clicked")
                appState.captureFileClipboard(operation: .copy)
            })
            applyShortcut(.pasteFiles, to: Button(L10n.text("Paste")) {
                if forwardToFirstResponder(#selector(NSText.paste(_:))) { return }
                NSLog("[FileFluss] menu: Paste clicked")
                Task { await appState.pasteFileClipboard() }
            })
            Divider()
            applyShortcut(.selectAll, to: Button(L10n.text("Select All")) {
                if forwardToFirstResponder(#selector(NSResponder.selectAll(_:))) { return }
                NotificationCenter.default.post(name: KeyboardCommand.selectAll.notification, object: nil)
            })
            applyShortcut(.deselectAll, to: Button(L10n.text("Deselect All")) {
                NotificationCenter.default.post(name: KeyboardCommand.deselectAll.notification, object: nil)
            })
            applyShortcut(.invertSelection, to: Button(L10n.text("Invert Selection")) {
                NotificationCenter.default.post(name: KeyboardCommand.invertSelection.notification, object: nil)
            })
        }

        // Add Search / Quick Filter into the existing Edit menu after the
        // pasteboard group so they appear in the same menu.
        CommandGroup(after: .pasteboard) {
            Divider()
            applyShortcut(.openSearch, to: Button(L10n.text("Search…")) {
                NotificationCenter.default.post(name: KeyboardCommand.openSearch.notification, object: nil)
            })
            applyShortcut(.quickFilter, to: Button(L10n.text("Quick Filter…")) {
                NotificationCenter.default.post(name: KeyboardCommand.quickFilter.notification, object: nil)
            })
        }

        CommandMenu(L10n.text("Go")) {
            applyShortcut(.openDirectory, to: Button(L10n.text("Open Folder")) {
                NotificationCenter.default.post(name: KeyboardCommand.openDirectory.notification, object: nil)
            })
            applyShortcut(.parentDirectory, to: Button(L10n.text("Parent Directory")) {
                NotificationCenter.default.post(name: KeyboardCommand.parentDirectory.notification, object: nil)
            })
            applyShortcut(.historyBack, to: Button(L10n.text("Back")) {
                NotificationCenter.default.post(name: KeyboardCommand.historyBack.notification, object: nil)
            })
            applyShortcut(.historyForward, to: Button(L10n.text("Forward")) {
                NotificationCenter.default.post(name: KeyboardCommand.historyForward.notification, object: nil)
            })
            applyShortcut(.openInOtherPanel, to: Button(L10n.text("Open in Other Panel")) {
                NotificationCenter.default.post(name: KeyboardCommand.openInOtherPanel.notification, object: nil)
            })
            .disabled(appState.singlePaneMode)
            applyShortcut(.targetToSource, to: Button(L10n.text("Target → Source")) {
                NotificationCenter.default.post(name: KeyboardCommand.targetToSource.notification, object: nil)
            })
            .disabled(appState.singlePaneMode)
            applyShortcut(.focusPathBar, to: Button(L10n.text("Go to Folder…")) {
                NotificationCenter.default.post(name: KeyboardCommand.focusPathBar.notification, object: nil)
            })
            applyShortcut(.jumpToRoot, to: Button(L10n.text("Jump to Root")) {
                NotificationCenter.default.post(name: KeyboardCommand.jumpToRoot.notification, object: nil)
            })
            Divider()
            applyShortcut(.toggleActivePanel, to: Button(L10n.text("Toggle Active Panel")) {
                NotificationCenter.default.post(name: KeyboardCommand.toggleActivePanel.notification, object: nil)
            })
        }

        CommandGroup(after: .toolbar) {
            applyShortcut(.refreshAll, to: Button(L10n.text("Refresh Both Panels")) {
                Task { await appState.refreshAllPanels() }
            })
            applyShortcut(.refreshActive, to: Button(L10n.text("Refresh Active Panel")) {
                NotificationCenter.default.post(name: KeyboardCommand.refreshActive.notification, object: nil)
            })

            Divider()

            applyShortcut(.sortByName, to: Button(L10n.text("Sort by Name")) {
                NotificationCenter.default.post(name: KeyboardCommand.sortByName.notification, object: nil)
            })
            applyShortcut(.sortByDate, to: Button(L10n.text("Sort by Date")) {
                NotificationCenter.default.post(name: KeyboardCommand.sortByDate.notification, object: nil)
            })
            applyShortcut(.sortBySize, to: Button(L10n.text("Sort by Size")) {
                NotificationCenter.default.post(name: KeyboardCommand.sortBySize.notification, object: nil)
            })
            applyShortcut(.sortByKind, to: Button(L10n.text("Sort by Kind")) {
                NotificationCenter.default.post(name: KeyboardCommand.sortByKind.notification, object: nil)
            })

            Divider()

            applyShortcut(.toggleHiddenFiles, to: Button(L10n.text("Toggle Hidden Files")) {
                NotificationCenter.default.post(name: KeyboardCommand.toggleHiddenFiles.notification, object: nil)
            })
            Toggle(L10n.text("Show Status Bar"), isOn: $showStatusBar)
                .keyboardShortcut("/", modifiers: [.command, .shift])
            Toggle(L10n.text("Hide File Extensions"), isOn: $hideFileExtensions)

            Divider()

            // Finder/Preview-style row scaling. Bound to Cmd+Plus and
            // Cmd+Minus matching the convention every other macOS app
            // uses for zoom-in / zoom-out. Plus is declared with an
            // explicit .shift so SwiftUI accepts the shortcut across
            // keyboard layouts (on US the `+` key is shift-`=`).
            Button(L10n.text("Increase Row Size")) {
                FileListRowSizePrefs.set(.large)
            }
            .keyboardShortcut("+", modifiers: .command)
            .disabled(rowSizeRaw == FileListRowSize.large.rawValue)

            Button(L10n.text("Decrease Row Size")) {
                FileListRowSizePrefs.set(.regular)
            }
            .keyboardShortcut("-", modifiers: .command)
            .disabled(rowSizeRaw == FileListRowSize.regular.rawValue)
        }

        CommandMenu(L10n.text("Tools")) {
            applyShortcut(.syncPanels, to: Button(L10n.text("Sync Left and Right Panels…")) {
                appState.showSyncSheet = true
            })
            .disabled(appState.hasOfflineSelection || appState.singlePaneMode)

            applyShortcut(.compareFolders, to: Button(L10n.text("Compare Folders")) {
                NotificationCenter.default.post(name: KeyboardCommand.compareFolders.notification, object: nil)
            })
            .disabled(appState.singlePaneMode)

            applyShortcut(.swapPanels, to: Button(L10n.text("Swap Panels")) {
                NotificationCenter.default.post(name: KeyboardCommand.swapPanels.notification, object: nil)
            })
            .disabled(appState.singlePaneMode)

            Divider()

            applyShortcut(.indexCurrentSource, to: Button(L10n.text("Index Current Source")) {
                NotificationCenter.default.post(name: KeyboardCommand.indexCurrentSource.notification, object: nil)
            })
        }

        CommandGroup(replacing: .appInfo) {
            Button(L10n.text("About FileFluss")) {
                AboutWindowController.shared.show()
            }
        }

        // Replace the default Settings… menu item so Cmd+, opens our own
        // resizable Settings window scene (the SwiftUI `Settings` scene
        // produces a non-resizable window on macOS Tahoe).
        CommandGroup(replacing: .appSettings) {
            SettingsMenuButton()
        }

        CommandGroup(replacing: .help) {
            Button(L10n.text("FileFluss Homepage")) {
                NSWorkspace.shared.open(URL(string: "https://www.filefluss.de")!)
            }
            HelpMenuButton()
            Divider()
            Button(L10n.text("Check for Updates…")) {
                AboutWindowController.shared.show()
            }
            Button(L10n.text("GitHub Repository")) {
                NSWorkspace.shared.open(URL(string: "https://github.com/rana-gmbh/filefluss")!)
            }
        }

        // Grouped together so the surrounding `commands { … }` builder stays
        // under @CommandsBuilder's 10-direct-child limit.
        Group {
            CommandGroup(before: .help) {
                Button(L10n.text("Support the FileFluss Project")) {
                    NSWorkspace.shared.open(URL(string: "https://buymeacoffee.com/robertrudolph")!)
                }
            }

            CommandGroup(after: .help) {
                Button(SupportLogService.shared.isRecording ? "Support Log (Recording…)" : "Support Log") {
                    SupportLogService.shared.start()
                }
                .disabled(SupportLogService.shared.isRecording)

                #if DEBUG
                Button(L10n.text("Run Version Test…")) {
                    Task { await VersionTestRunner.run(appState: appState) }
                }
                #endif
            }
        }
    }
}

/// The "FileFluss Help" entry under the Help menu. Opens our custom Help
/// window via openWindow — needs an environment value, so it's wrapped in
/// its own View instead of being inlined in the CommandGroup.
private struct HelpMenuButton: View {
    @Environment(\.openWindow) private var openWindow
    var body: some View {
        Button(L10n.text("FileFluss Help")) {
            openWindow(id: "help")
        }
        .keyboardShortcut("?", modifiers: .command)
    }
}

/// The "Settings…" entry under the FileFluss menu. Opens the resizable
/// Settings window scene (we don't use SwiftUI's built-in `Settings` scene
/// because its NSWindow refuses to resize on macOS Tahoe).
private struct SettingsMenuButton: View {
    @Environment(\.openWindow) private var openWindow
    var body: some View {
        Button(L10n.text("Settings…")) {
            openWindow(id: "settings")
        }
        .keyboardShortcut(",", modifiers: .command)
    }
}
