import AppKit

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
    private let updateNotifier = UpdateNotifier()

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Default automatic update checks to on the first time the app
        // is launched. Users can toggle this with
        // `defaults write com.rana-gmbh.FileFluss automaticUpdateChecksEnabled -bool false`.
        UserDefaults.standard.register(defaults: [
            "automaticUpdateChecksEnabled": true
        ])

        applyAppIconForCurrentAppearance()
        // Swap the dock icon when the user (or system) toggles between
        // light and dark mode.
        appearanceObservation = NSApp.observe(\.effectiveAppearance, options: [.new]) { [weak self] _, _ in
            // The KVO callback fires on the main thread, but its closure
            // is nonisolated — bridge explicitly to satisfy Swift 6.
            MainActor.assumeIsolated {
                self?.applyAppIconForCurrentAppearance()
            }
        }

        let notifier = updateNotifier
        Task { await notifier.start() }
    }

    private func applyAppIconForCurrentAppearance() {
        let isDark = NSApp.effectiveAppearance
            .bestMatch(from: [.darkAqua, .vibrantDark, .accessibilityHighContrastDarkAqua, .accessibilityHighContrastVibrantDark]) != nil
        let resourceName = isDark ? "AppIcon-Dark" : "AppIcon-Light"
        guard let url = Bundle.main.url(forResource: resourceName, withExtension: "icns"),
              let image = NSImage(contentsOf: url) else { return }
        NSApp.applicationIconImage = image
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
}
