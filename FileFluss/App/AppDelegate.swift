import AppKit

/// Provides the custom Dock right-click menu so secondary windows (Search,
/// Compare Folders) are reachable from the Dock just like the main window.
/// SwiftUI's `Window` scenes don't surface in the Dock menu by default, so
/// we list each visible secondary window manually.
final class FileFlussAppDelegate: NSObject, NSApplicationDelegate {

    /// Titles we expose in the Dock menu. The strings must match the titles
    /// declared in the corresponding `Window(...)` scenes in FileFlussApp.
    private static let secondaryWindowTitles: Set<String> = [
        "Search",
        "Compare Folders"
    ]

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
