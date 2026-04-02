import SwiftUI

struct ContentView: View {
    @Environment(AppState.self) private var appState

    var body: some View {
        HStack(spacing: 0) {
            // Left panel: sidebar + file list
            panelView(side: .left)

            Divider()

            // Right panel: file list + sidebar
            panelView(side: .right)
        }
        .toolbar {
            FileToolbar()
        }
        .sheet(isPresented: Bindable(appState).showSearchPopup) {
            SearchPopupView()
                .environment(appState)
        }
    }

    @ViewBuilder
    private func panelView(side: PanelSide) -> some View {
        let isActive = appState.activePanel == side

        HStack(spacing: 0) {
            if side == .left {
                sidebarForPanel(side: side)
                Divider()
                filePanelContent(side: side, isActive: isActive)
            } else {
                filePanelContent(side: side, isActive: isActive)
                Divider()
                sidebarForPanel(side: side)
            }
        }
    }

    @ViewBuilder
    private func sidebarForPanel(side: PanelSide) -> some View {
        SidebarView(panelSide: side)
            .frame(minWidth: 160, idealWidth: 200, maxWidth: 260)
    }

    @ViewBuilder
    private func filePanelContent(side: PanelSide, isActive: Bool) -> some View {
        let sidebarItem = appState.sidebarSelection(for: side)

        VStack(spacing: 0) {
            switch sidebarItem {
            case .cloudAccount(let account):
                CloudFileListView(panelSide: side, accountId: account.id)
            case .cloudFolder(let accountId, _):
                CloudFileListView(panelSide: side, accountId: accountId)
            default:
                FileListView(panelSide: side)
            }
        }
        .overlay(alignment: .top) {
            // Active panel indicator
            if isActive {
                Rectangle()
                    .fill(Color.accentColor)
                    .frame(height: 2)
            }
        }
        .contentShape(Rectangle())
        .onTapGesture {
            appState.activePanel = side
        }
    }
}
