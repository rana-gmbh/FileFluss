import SwiftUI

/// Root wrapper that owns the first-launch welcome sheet. The sheet lives
/// here rather than on `ContentView` because that view already presents two
/// other sheets (sync planner, add account) and stacking three `.sheet`
/// modifiers on the same view doesn't reliably surface the late-set one.
struct RootView: View {
    @AppStorage("hasCompletedWelcome") private var hasCompletedWelcome = false
    @State private var showWelcome = false

    var body: some View {
        ContentView()
            .sheet(isPresented: $showWelcome) {
                WelcomeView()
            }
            .onChange(of: hasCompletedWelcome, initial: true) { _, completed in
                showWelcome = !completed
            }
    }
}

struct ContentView: View {
    @Environment(AppState.self) private var appState
    @State private var supportLog = SupportLogService.shared

    var body: some View {
        HStack(spacing: 0) {
            // Left panel: sidebar + file list
            panelView(side: .left)

            Divider()

            // Right panel: file list + sidebar
            panelView(side: .right)
        }
        .toolbar(id: "main") {
            FileToolbar()
        }
        .overlay(alignment: .top) {
            if supportLog.isRecording {
                supportLogBanner
                    .padding(.top, 8)
                    .transition(.move(edge: .top).combined(with: .opacity))
            }
        }
        .animation(.easeInOut(duration: 0.2), value: supportLog.isRecording)
        .sheet(isPresented: Bindable(appState).showSyncSheet) {
            SyncPlannerView()
                .environment(appState)
        }
        .sheet(isPresented: Bindable(appState.syncManager).isAddingAccount) {
            AddCloudAccountView()
                .environment(appState)
        }
    }

    private var supportLogBanner: some View {
        HStack(spacing: 10) {
            Image(systemName: "record.circle.fill")
                .foregroundStyle(.red)
                .symbolEffect(.pulse)
            Text("Recording support log — reproduce the issue now")
                .font(.callout)
            Text("\(supportLog.secondsRemaining)s")
                .font(.callout.monospacedDigit())
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(.regularMaterial, in: Capsule())
        .overlay(Capsule().stroke(.secondary.opacity(0.2)))
        .shadow(radius: 4, y: 2)
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
                if account.isConnected {
                    CloudFileListView(panelSide: side, accountId: account.id)
                } else {
                    OfflineSourceView(sourceId: account.id.uuidString, panelSide: side)
                }
            case .cloudFolder(let accountId, let path):
                if let account = appState.syncManager.accountFor(id: accountId), account.isConnected {
                    CloudFileListView(panelSide: side, accountId: accountId)
                } else {
                    OfflineSourceView(sourceId: accountId.uuidString, panelSide: side, initialPath: path)
                }
            case .drive(let driveId):
                if appState.driveMonitor.mountURL(for: driveId) != nil {
                    // Online — sidebar onChange already navigated the local FM.
                    FileListView(panelSide: side)
                } else {
                    OfflineSourceView(sourceId: driveId, panelSide: side)
                }
            case .offlineFolder(let sourceId, let path):
                OfflineSourceView(sourceId: sourceId, panelSide: side, initialPath: path)
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
