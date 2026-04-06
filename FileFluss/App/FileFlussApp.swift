import SwiftUI
import SwiftData

@main
struct FileFlussApp: App {
    @State private var appState = AppState()

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
    }
}
