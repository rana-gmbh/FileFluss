import SwiftUI
import SwiftData

@main
struct FileFlussApp: App {
    @State private var appState = AppState()
    @NSApplicationDelegateAdaptor private var appDelegate: FileFlussAppDelegate

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environment(appState)
        }
        .windowStyle(.titleBar)
        .windowToolbarStyle(.unified(showsTitle: true))
        .defaultSize(width: 1600, height: 800)
        .commands {
            FileCommands(appState: appState)
            CommandGroup(replacing: .undoRedo) {}
        }

        Settings {
            SettingsView()
                .environment(appState)
        }

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
    }
}
