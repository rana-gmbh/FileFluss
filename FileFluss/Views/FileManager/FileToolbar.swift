import SwiftUI

struct FileToolbar: CustomizableToolbarContent {
    @Environment(AppState.self) private var appState

    private var activeCloudVM: CloudFileManagerViewModel? {
        guard let id = appState.cloudAccountId(for: appState.activePanel) else { return nil }
        return appState.cloudFileManager(for: id, side: appState.activePanel)
    }

    private var canGoBack: Bool {
        activeCloudVM?.canGoBack ?? appState.activeFileManager.canGoBack
    }

    private var canGoForward: Bool {
        activeCloudVM?.canGoForward ?? appState.activeFileManager.canGoForward
    }

    var body: some CustomizableToolbarContent {
        // Navigation buttons stay pinned — they're effectively a back/forward
        // pair, not a customizable widget.
        ToolbarItem(id: "navigation.back", placement: .navigation) {
            Button {
                if let cloudVM = activeCloudVM {
                    Task { await cloudVM.navigateBack() }
                } else {
                    Task { await appState.activeFileManager.navigateBack() }
                }
            } label: {
                Image(systemName: "chevron.left")
            }
            .disabled(!canGoBack)
        }
        .customizationBehavior(.disabled)

        ToolbarItem(id: "navigation.forward", placement: .navigation) {
            Button {
                if let cloudVM = activeCloudVM {
                    Task { await cloudVM.navigateForward() }
                } else {
                    Task { await appState.activeFileManager.navigateForward() }
                }
            } label: {
                Image(systemName: "chevron.right")
            }
            .disabled(!canGoForward)
        }
        .customizationBehavior(.disabled)

        ToolbarItem(id: "search", placement: .primaryAction) {
            OpenWindowToolbarButton(
                windowId: "search",
                label: "Search",
                systemImage: "magnifyingglass",
                help: "Search all sources (⌘F)"
            )
            .keyboardShortcut("f", modifiers: .command)
        }

        ToolbarItem(id: "sync", placement: .primaryAction) {
            SyncToolbarButton()
        }

        ToolbarItem(id: "compare", placement: .primaryAction) {
            CompareToolbarButton()
        }

        ToolbarItem(id: "refresh", placement: .primaryAction) {
            Button {
                Task { await appState.refreshAllPanels() }
            } label: {
                Label("Refresh", systemImage: "arrow.clockwise")
            }
            .help("Refresh both panels")
        }

        ToolbarItem(id: "hiddenFiles", placement: .primaryAction) {
            // Binds to whichever panel is active — cloud OR local. Toggling
            // updates that panel's VM and triggers a refresh.
            Toggle(isOn: Binding(
                get: {
                    if let cloud = activeCloudVM { return cloud.showHiddenFiles }
                    return appState.activeFileManager.showHiddenFiles
                },
                set: { newValue in
                    if let cloud = activeCloudVM {
                        cloud.showHiddenFiles = newValue
                    } else {
                        appState.activeFileManager.showHiddenFiles = newValue
                        Task { await appState.activeFileManager.refresh() }
                    }
                }
            )) {
                Label {
                    Text("Hidden Files")
                } icon: {
                    // Custom asset images in a toolbar Label don't pick
                    // up SF Symbol auto-sizing, so the SVG would render
                    // at its intrinsic 800pt — fix to 16pt to match the
                    // surrounding system symbols.
                    Image("HiddenFilesIcon")
                        .renderingMode(.template)
                        .resizable()
                        .scaledToFit()
                        .frame(width: 16, height: 16)
                }
            }
            .help("Show hidden files")
        }

        ToolbarItem(id: "copyMode", placement: .primaryAction) {
            Toggle(isOn: Binding(
                get: { appState.dragDropMode == .copy },
                set: { appState.dragDropMode = $0 ? .copy : .ask }
            )) {
                Label("Copy Mode", systemImage: "plus.square.on.square")
            }
            .help("Force all drag & drop operations to copy (skips the Move/Copy prompt)")
        }

        ToolbarItem(id: "moveMode", placement: .primaryAction) {
            Toggle(isOn: Binding(
                get: { appState.dragDropMode == .move },
                set: { appState.dragDropMode = $0 ? .move : .ask }
            )) {
                Label("Move Mode", systemImage: "arrow.right.square")
            }
            .help("Force all drag & drop operations to move (skips the Move/Copy prompt)")
        }
    }
}

/// Sync toolbar button. Disabled when either panel sits on an offline
/// source — there's no live destination to write to — and shows a tooltip
/// explaining why. Clicking the disabled button does nothing; macOS
/// surfaces the `.help` text on hover, including for disabled controls.
private struct SyncToolbarButton: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        let blocked = appState.hasOfflineSelection
        Button {
            if !blocked { appState.showSyncSheet = true }
        } label: {
            Label("Sync", systemImage: "arrow.triangle.2.circlepath")
        }
        .disabled(blocked)
        .help(blocked
              ? "It's not possible to sync with an offline folder."
              : "Sync left and right panels")
    }
}

/// Wrapper that pulls `openWindow` out of the SwiftUI environment so we can
/// invoke it from a toolbar button (CustomizableToolbarContent can't read
/// `@Environment` directly).
private struct OpenWindowToolbarButton: View {
    @Environment(\.openWindow) private var openWindow
    let windowId: String
    let label: String
    let systemImage: String
    let help: String

    var body: some View {
        Button {
            openWindow(id: windowId)
        } label: {
            Label(label, systemImage: systemImage)
        }
        .help(help)
    }
}

/// Compare needs to bump `compareTrigger` so the compare window snapshots
/// the *currently* selected folders, instead of re-running every time the
/// user navigates a panel.
private struct CompareToolbarButton: View {
    @Environment(AppState.self) private var appState
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Button {
            appState.compareTrigger = UUID()
            openWindow(id: "compare")
        } label: {
            Label("Compare", systemImage: "arrow.left.arrow.right.square")
        }
        .help("Compare the two folders side by side")
    }
}

// MARK: - Quick Filter bar

/// Inline filter bar shown above a panel's file list when the user invokes
/// Edit → Quick Filter… (default ⌘S / ⌥⌘F). Mutates the panel's `searchText`
/// directly — the existing `filteredItems` pipeline picks the result up
/// without further plumbing. Escape clears + hides the bar.
struct PanelFilterBar: View {
    @Binding var text: String
    @Binding var isVisible: Bool
    @FocusState private var focused: Bool

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "line.3.horizontal.decrease.circle")
                .foregroundStyle(.secondary)
            TextField("Filter visible items…", text: $text)
                .textFieldStyle(.plain)
                .focused($focused)
                .onExitCommand {
                    text = ""
                    isVisible = false
                }
            if !text.isEmpty {
                Button {
                    text = ""
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
                .help("Clear filter")
            }
            Button {
                text = ""
                isVisible = false
            } label: {
                Image(systemName: "xmark")
            }
            .buttonStyle(.borderless)
            .controlSize(.small)
            .help("Close filter (Esc)")
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 4)
        .background(Color(NSColor.windowBackgroundColor))
        .onAppear { focused = true }
    }
}
