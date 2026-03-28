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
        .defaultSize(width: 1100, height: 700)

        Settings {
            SettingsView()
                .environment(appState)
        }
    }
}
