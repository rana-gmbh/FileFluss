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
    @Environment(\.openWindow) private var openWindow
    @State private var supportLog = SupportLogService.shared

    /// Persistent per-side sidebar width. Defaults to ~200pt, the same
    /// `idealWidth` used by the previous fixed-frame layout.
    @AppStorage("sidebarWidth.left") private var leftSidebarWidth: Double = 200
    @AppStorage("sidebarWidth.right") private var rightSidebarWidth: Double = 200

    /// Drag-bounds for the resize handle. The lower bound is just wide
    /// enough to show icons + the small selection indicator without
    /// truncation; the upper bound matches the previous `maxWidth` cap
    /// of the fixed layout.
    private static let sidebarMinWidth: Double = 50
    private static let sidebarMaxWidth: Double = 320
    /// Width restored when the user double-clicks the resize handle.
    /// Matches the `@AppStorage` default so first-launch and
    /// double-click land on the same column width.
    private static let sidebarDefaultWidth: Double = 200
    /// Below this threshold the sidebar drops its row text and tooltips
    /// take over the labelling role. Picked so the user can drag wider
    /// to ~100pt and still see truncated text before the switch flips.
    private static let sidebarCollapseThreshold: Double = 100

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
        .sheet(isPresented: Bindable(appState).showGoToFolder) {
            GoToFolderSheet()
                .environment(appState)
        }
        .onReceive(NotificationCenter.default.publisher(for: .requestShowCompareWindow)) { _ in
            openWindow(id: "compare")
        }
        .onReceive(NotificationCenter.default.publisher(for: KeyboardCommand.openSearch.notification)) { _ in
            openWindow(id: "search")
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
        let width = side == .left ? leftSidebarWidth : rightSidebarWidth
        let collapsed = width < Self.sidebarCollapseThreshold

        HStack(spacing: 0) {
            if side == .left {
                SidebarView(panelSide: side, collapsed: collapsed)
                    .frame(width: width)
                SidebarResizeHandle(
                    width: side == .left ? $leftSidebarWidth : $rightSidebarWidth,
                    side: side,
                    minWidth: Self.sidebarMinWidth,
                    maxWidth: Self.sidebarMaxWidth,
                    defaultWidth: Self.sidebarDefaultWidth
                )
                filePanelContent(side: side, isActive: isActive)
            } else {
                filePanelContent(side: side, isActive: isActive)
                SidebarResizeHandle(
                    width: side == .left ? $leftSidebarWidth : $rightSidebarWidth,
                    side: side,
                    minWidth: Self.sidebarMinWidth,
                    maxWidth: Self.sidebarMaxWidth,
                    defaultWidth: Self.sidebarDefaultWidth
                )
                SidebarView(panelSide: side, collapsed: collapsed)
                    .frame(width: width)
            }
        }
    }

    @ViewBuilder
    private func filePanelContent(side: PanelSide, isActive: Bool) -> some View {
        let sidebarItem = appState.sidebarSelection(for: side)

        VStack(spacing: 0) {
            switch sidebarItem {
            case .cloudAccount(let account):
                if account.isConnected && !account.isOfflineMode {
                    CloudFileListView(panelSide: side, accountId: account.id)
                } else {
                    OfflineSourceView(sourceId: account.id.uuidString, panelSide: side)
                }
            case .cloudFolder(let accountId, let path):
                if let account = appState.syncManager.accountFor(id: accountId),
                   account.isConnected,
                   !account.isOfflineMode {
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

/// Small modal prompt for "Go to Folder…" — the user types a path,
/// presses Return, the active panel navigates there. Supports `~` and
/// paths relative to the active panel's current directory.
private struct GoToFolderSheet: View {
    @Environment(AppState.self) private var appState
    @Environment(\.dismiss) private var dismiss
    @State private var path: String = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Go to Folder")
                .font(.headline)
            TextField("/path/to/folder", text: $path)
                .textFieldStyle(.roundedBorder)
                .frame(width: 360)
                .onSubmit { go() }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Go") { go() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(path.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(20)
    }

    private func go() {
        appState.goToFolder(path)
        dismiss()
    }
}
