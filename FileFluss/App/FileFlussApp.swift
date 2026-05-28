import SwiftUI
import SwiftData

@main
struct FileFlussApp: App {
    @State private var appState = AppState()
    @NSApplicationDelegateAdaptor private var appDelegate: FileFlussAppDelegate

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(appState)
        }
        .windowStyle(.titleBar)
        .windowToolbarStyle(.unified(showsTitle: true))
        .defaultSize(width: 1600, height: 800)
        .commands {
            FileCommands(appState: appState)
            CommandGroup(replacing: .undoRedo) {}
        }

        // We deliberately do NOT use SwiftUI's `Settings` scene here. On
        // macOS Tahoe the Settings scene's NSWindow re-applies its own
        // (non-resizable) style mask after we change it, so the user can't
        // drag the edges. A regular `Window` honours `.windowResizability`,
        // shows real close/minimize/zoom controls, and macOS persists the
        // last frame for us. The Cmd+, shortcut is wired up below.
        Window("FileFluss Settings", id: "settings") {
            SettingsView()
                .environment(appState)
                .frame(minWidth: 480, minHeight: 400)
        }
        .defaultSize(width: 760, height: 820)
        .windowResizability(.contentMinSize)

        // Search and Compare run as separate, movable, modeless windows so
        // the user can keep both panels visible behind them while dragging
        // the utility window aside. They are resizable; macOS persists the
        // last frame per scene id automatically across launches.
        Window("Search", id: "search") {
            SearchPopupView()
                .environment(appState)
                .frame(minWidth: 480, minHeight: 360)
        }
        .defaultSize(width: 700, height: 500)
        .windowResizability(.contentMinSize)

        Window("Compare Folders", id: "compare") {
            CompareFoldersView()
                .environment(appState)
                .frame(minWidth: 540, minHeight: 420)
        }
        .defaultSize(width: 640, height: 560)
        .windowResizability(.contentMinSize)

        Window("FileFluss Help", id: "help") {
            HelpView()
        }
        .defaultSize(width: 880, height: 600)
        .windowResizability(.contentMinSize)
    }
}
