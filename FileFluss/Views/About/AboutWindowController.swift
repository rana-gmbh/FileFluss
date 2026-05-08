import AppKit
import SwiftUI

@MainActor
final class AboutWindowController: NSObject, NSWindowDelegate {
    static let shared = AboutWindowController()
    private var window: NSWindow?
    private var closingWindows: [NSWindow] = []

    func show() {
        if let window, window.isVisible {
            window.makeKeyAndOrderFront(nil)
            window.orderFrontRegardless()
            return
        }
        // Reset stale reference so a freshly opened window restarts
        // UpdateChecker in its idle state.
        window = nil

        let hosting = NSHostingController(rootView: AboutView())
        let win = NSWindow(contentViewController: hosting)
        win.title = "About FileFluss"
        win.styleMask = [.titled, .closable]
        win.isReleasedWhenClosed = false
        win.animationBehavior = .none
        win.delegate = self
        win.setContentSize(NSSize(width: 320, height: 560))
        win.center()
        win.makeKeyAndOrderFront(nil)
        win.orderFrontRegardless()
        NSApp.activate(ignoringOtherApps: true)
        self.window = win
    }

    func windowWillClose(_ notification: Notification) {
        guard let closingWindow = notification.object as? NSWindow, closingWindow == window else { return }
        window = nil
        closingWindows.append(closingWindow)

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self, weak closingWindow] in
            guard let self, let closingWindow else { return }
            closingWindow.delegate = nil
            closingWindow.contentViewController = nil
            self.closingWindows.removeAll { $0 === closingWindow }
        }
    }
}
