import SwiftUI

struct FileToolbar: ToolbarContent {
    @Environment(AppState.self) private var appState

    var body: some ToolbarContent {
        ToolbarItemGroup(placement: .navigation) {
            Button {
                Task { await appState.activeFileManager.navigateBack() }
            } label: {
                Image(systemName: "chevron.left")
            }
            .disabled(!appState.activeFileManager.canGoBack)

            Button {
                Task { await appState.activeFileManager.navigateForward() }
            } label: {
                Image(systemName: "chevron.right")
            }
            .disabled(!appState.activeFileManager.canGoForward)
        }

        ToolbarItemGroup(placement: .primaryAction) {
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
