import AppKit
import FileFlussCore

/// Provides the custom Dock right-click menu so secondary windows (Search,
/// Compare Folders) are reachable from the Dock just like the main window.
/// SwiftUI's `Window` scenes don't surface in the Dock menu by default, so
/// we list each visible secondary window manually.
@MainActor
final class FileFlussAppDelegate: NSObject, NSApplicationDelegate {

    /// Titles we expose in the Dock menu. The strings must match the titles
    /// declared in the corresponding `Window(...)` scenes in FileFlussApp.
    private static let secondaryWindowTitles: Set<String> = [
        "Search",
        "Compare Folders"
    ]

    private var appearanceObservation: NSKeyValueObservation?
    private var themeChangeObserver: NSObjectProtocol?
    private let updateNotifier = UpdateNotifier()

    func applicationWillFinishLaunching(_ notification: Notification) {
        // Earliest possible chance to stamp the right icon — runs before
        // the first SwiftUI scene materialises. Belt-and-suspenders for
        // issue #11 where the dark icon stayed stuck on light for some
        // users (suspected launch-time race on effectiveAppearance).
        applyAppIconForCurrentAppearance()

        // Hand the AppKit-free FileFlussCore providers a way to launch
        // the system browser for OAuth loopback flows. iOS will register
        // an ASWebAuthenticationSession-backed handler in Phase 1.
        BrowserOpener.register { url in
            NSWorkspace.shared.open(url)
        }

        // OAuth-flow providers in the package call OAuthSession.authenticate
        // for the full open-browser → await-redirect round-trip. macOS uses
        // a TCP loopback listener; iOS will register ASWebAuthenticationSession.
        OAuthSession.register(LoopbackOAuthAuthenticator())
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Default automatic update checks to on the first time the app
        // is launched. Users can toggle this with
        // `defaults write com.rana-gmbh.FileFluss automaticUpdateChecksEnabled -bool false`.
        UserDefaults.standard.register(defaults: [
            "automaticUpdateChecksEnabled": true
        ])

        applyAppIconForCurrentAppearance()
        // One more pass once SwiftUI has had a run-loop tick to propagate
        // the resolved system appearance to NSApp — addresses the launch
        // race where `effectiveAppearance` still reports `.aqua` here.
        DispatchQueue.main.async { [weak self] in
            self?.applyAppIconForCurrentAppearance()
        }

        // KVO on effectiveAppearance is documented as supported but is
        // unreliable in practice; we keep it as one of several redundant
        // triggers.
        appearanceObservation = NSApp.observe(\.effectiveAppearance, options: [.new]) { [weak self] _, _ in
            MainActor.assumeIsolated {
                self?.applyAppIconForCurrentAppearance()
            }
        }

        // System-wide Appearance toggle in System Settings. Fires reliably
        // when KVO above misses the change.
        themeChangeObserver = DistributedNotificationCenter.default().addObserver(
            forName: Notification.Name("AppleInterfaceThemeChangedNotification"),
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.applyAppIconForCurrentAppearance()
            }
        }

        let notifier = updateNotifier
        Task { await notifier.start() }
    }

    func applicationDidBecomeActive(_ notification: Notification) {
        // On some macOS builds the resolved appearance is only available
        // after the app has actually become active. Idempotent re-apply.
        applyAppIconForCurrentAppearance()
    }

    private func applyAppIconForCurrentAppearance() {
        let appearance = NSApp.effectiveAppearance
        let name = appearance.name
        let isDark = name == .darkAqua
            || name == .vibrantDark
            || name == .accessibilityHighContrastDarkAqua
            || name == .accessibilityHighContrastVibrantDark
            || appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        let resourceName = isDark ? "AppIcon-Dark" : "AppIcon-Light"
        NSLog("[FileFluss] applyAppIcon appearance=\(name.rawValue) isDark=\(isDark) file=\(resourceName)")
        guard let url = Bundle.main.url(forResource: resourceName, withExtension: "icns"),
              let image = NSImage(contentsOf: url) else {
            NSLog("[FileFluss] applyAppIcon missing icns for \(resourceName)")
            return
        }
        NSApp.applicationIconImage = image
        // Force the Dock to redraw — without this, a cached pre-launch
        // icon (from CFBundleIconFile / asset catalog) sometimes persists
        // even after applicationIconImage is reassigned.
        NSApp.dockTile.display()
    }

    func applicationDockMenu(_ sender: NSApplication) -> NSMenu? {
        let menu = NSMenu()
        for window in NSApp.windows {
            guard window.isVisible,
                  Self.secondaryWindowTitles.contains(window.title) else { continue }
            let item = NSMenuItem(
                title: window.title,
                action: #selector(activateWindow(_:)),
                keyEquivalent: ""
            )
            item.target = self
            item.representedObject = window
            menu.addItem(item)
        }
        return menu.numberOfItems > 0 ? menu : nil
    }

    @objc private func activateWindow(_ sender: NSMenuItem) {
        guard let window = sender.representedObject as? NSWindow else { return }
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    /// Forcibly accept termination requests. SwiftUI `.sheet` modals attach
    /// as `NSWindow.attachedSheet` and can stall the system's "Quit & Reopen"
    /// flow after granting Full Disk Access — end them explicitly so the
    /// app always quits cleanly. Auxiliary windows (Settings, Search,
    /// Compare) also get closed defensively.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        for window in NSApp.windows {
            if let sheet = window.attachedSheet {
                window.endSheet(sheet, returnCode: .cancel)
            }
        }
        return .terminateNow
    }
}
