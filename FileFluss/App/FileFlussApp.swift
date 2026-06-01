import SwiftUI
import SwiftData
import FileFlussCore

@main
struct FileFlussApp: App {
    @State private var appState = AppState()
    @NSApplicationDelegateAdaptor private var appDelegate: FileFlussAppDelegate

    var body: some Scene {
        WindowGroup {
            LocalizedRoot {
                RootView()
                    .environment(appState)
                    .task {
                        // Give the AppDelegate a handle to AppState so it can
                        // unmount Finder volumes during applicationShouldTerminate.
                        appDelegate.appState = appState
                    }
            }
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
        Window(L10n.text("FileFluss Settings"), id: "settings") {
            LocalizedRoot {
                SettingsView()
                    .environment(appState)
                    .frame(minWidth: 480, minHeight: 400)
            }
        }
        .defaultSize(width: 760, height: 820)
        .windowResizability(.contentMinSize)

        // Search and Compare run as separate, movable, modeless windows so
        // the user can keep both panels visible behind them while dragging
        // the utility window aside. They are resizable; macOS persists the
        // last frame per scene id automatically across launches.
        Window(L10n.text("Search"), id: "search") {
            LocalizedRoot {
                SearchPopupView()
                    .environment(appState)
                    .frame(minWidth: 480, minHeight: 360)
            }
        }
        .defaultSize(width: 700, height: 500)
        .windowResizability(.contentMinSize)

        Window(L10n.text("Compare Folders"), id: "compare") {
            LocalizedRoot {
                CompareFoldersView()
                    .environment(appState)
                    .frame(minWidth: 540, minHeight: 420)
            }
        }
        .defaultSize(width: 640, height: 560)
        .windowResizability(.contentMinSize)

        Window(L10n.text("FileFluss Help"), id: "help") {
            LocalizedRoot {
                HelpView()
            }
        }
        .defaultSize(width: 880, height: 600)
        .windowResizability(.contentMinSize)
    }
}
