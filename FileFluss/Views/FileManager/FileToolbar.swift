import SwiftUI

struct FileToolbar: ToolbarContent {
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

    var body: some ToolbarContent {
        ToolbarItemGroup(placement: .navigation) {
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

        ToolbarItemGroup(placement: .primaryAction) {
            Button {
                appState.showSearchPopup = true
            } label: {
                Image(systemName: "magnifyingglass")
            }
            .help("Search all sources (⌘F)")
            .keyboardShortcut("f", modifiers: .command)

            Button {
                appState.showSyncSheet = true
            } label: {
                Image(systemName: "arrow.triangle.2.circlepath")
            }
            .help("Sync left and right panels")

            Button {
                Task { await appState.refreshAllPanels() }
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .help("Refresh both panels")

            Toggle(isOn: Bindable(appState.activeFileManager).showHiddenFiles) {
                Image(systemName: "eye")
            }
            .help("Show hidden files")
            .onChange(of: appState.activeFileManager.showHiddenFiles) { _, _ in
                Task { await appState.activeFileManager.refresh() }
            }
        }
    }
}
