import SwiftUI

struct FileToolbar: ToolbarContent {
    @Environment(AppState.self) private var appState

    var body: some ToolbarContent {
        ToolbarItemGroup(placement: .navigation) {
            Button {
                Task { await appState.fileManager.navigateBack() }
            } label: {
                Image(systemName: "chevron.left")
            }
            .disabled(!appState.fileManager.canGoBack)

            Button {
                Task { await appState.fileManager.navigateForward() }
            } label: {
                Image(systemName: "chevron.right")
            }
            .disabled(!appState.fileManager.canGoForward)
        }

        ToolbarItemGroup(placement: .primaryAction) {
            Button {
                Task { await appState.fileManager.refresh() }
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .keyboardShortcut("r", modifiers: .command)

            Toggle(isOn: Bindable(appState.fileManager).showHiddenFiles) {
                Image(systemName: "eye")
            }
            .help("Show hidden files")
            .onChange(of: appState.fileManager.showHiddenFiles) { _, _ in
                Task { await appState.fileManager.refresh() }
            }

            Menu {
                ForEach(FileManagerViewModel.SortOrder.allCases, id: \.self) { order in
                    Button {
                        if appState.fileManager.sortOrder == order {
                            appState.fileManager.sortAscending.toggle()
                        } else {
                            appState.fileManager.sortOrder = order
                            appState.fileManager.sortAscending = true
                        }
                    } label: {
                        HStack {
                            Text(order.label)
                            if appState.fileManager.sortOrder == order {
                                Image(systemName: appState.fileManager.sortAscending
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
