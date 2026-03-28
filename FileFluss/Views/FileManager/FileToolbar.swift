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
                Task { await appState.activeFileManager.refresh() }
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .keyboardShortcut("r", modifiers: .command)

            Toggle(isOn: Bindable(appState.activeFileManager).showHiddenFiles) {
                Image(systemName: "eye")
            }
            .help("Show hidden files")
            .onChange(of: appState.activeFileManager.showHiddenFiles) { _, _ in
                Task { await appState.activeFileManager.refresh() }
            }

            Menu {
                ForEach(FileManagerViewModel.SortOrder.allCases, id: \.self) { order in
                    Button {
                        if appState.activeFileManager.sortOrder == order {
                            appState.activeFileManager.sortAscending.toggle()
                        } else {
                            appState.activeFileManager.sortOrder = order
                            appState.activeFileManager.sortAscending = true
                        }
                    } label: {
                        HStack {
                            Text(order.label)
                            if appState.activeFileManager.sortOrder == order {
                                Image(systemName: appState.activeFileManager.sortAscending
                                    ? "chevron.up" : "chevron.down")
                            }
                        }
                    }
                }
            } label: {
                Image(systemName: "arrow.up.arrow.down")
            }

            Button {
                Task { await appState.syncManager.syncAll() }
            } label: {
                Image(systemName: "arrow.triangle.2.circlepath")
            }
            .help("Sync all enabled rules")
        }
    }
}
