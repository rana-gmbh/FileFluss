import SwiftUI

struct FileToolbar: CustomizableToolbarContent {
    @Environment(AppState.self) private var appState

    private var activeCloudVM: CloudFileManagerViewModel? {
        guard let id = appState.cloudAccountId(for: appState.activePanel) else { return nil }
        return appState.cloudFileManager(for: id)
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
            Button {
                appState.showSearchPopup = true
            } label: {
                Label("Search", systemImage: "magnifyingglass")
            }
            .help("Search all sources (⌘F)")
            .keyboardShortcut("f", modifiers: .command)
        }

        ToolbarItem(id: "sync", placement: .primaryAction) {
            Button {
                appState.showSyncSheet = true
            } label: {
                Label("Sync", systemImage: "arrow.triangle.2.circlepath")
            }
            .help("Sync left and right panels")
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
            Toggle(isOn: Bindable(appState.activeFileManager).showHiddenFiles) {
                Label("Hidden Files", systemImage: "eye")
            }
            .help("Show hidden files")
            .onChange(of: appState.activeFileManager.showHiddenFiles) { _, _ in
                Task { await appState.activeFileManager.refresh() }
            }
        }
    }
}
