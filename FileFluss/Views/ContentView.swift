import SwiftUI

struct ContentView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        @Bindable var state = appState
        NavigationSplitView {
            SidebarView(selection: $state.selectedSidebarItem)
        } detail: {
            detailView
        }
        .navigationSplitViewStyle(.balanced)
        .searchable(text: Bindable(appState.fileManager).searchText, prompt: "Search files")
        .toolbar {
            FileToolbar()
        }
    }

    @ViewBuilder
    private var detailView: some View {
        switch appState.selectedSidebarItem {
        case .home, .location, .none:
            FileListView()
        case .favorites:
            FileListView()
        case .cloudAccount:
            Text("Cloud account browser coming soon")
                .foregroundStyle(.secondary)
        case .syncRules:
            SyncRulesView()
        }
    }
}
