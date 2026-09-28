import SwiftUI
import AppKit

/// Restores and remembers the main window's size and position.
///
/// SwiftUI does save a frame for the main window, but under a key derived
/// from the root view's generic type signature — ours reads
/// `LocalizedRoot<ModifiedContent<…, _TaskModifier2>>-1-AppWindow-1`. Any
/// change to the root view's modifiers yields a different key and orphans
/// the saved frame, so the window reopens at its default size; to the user
/// the app forgets its window after an update (issue #49).
///
/// AppKit's own `setFrameAutosaveName` doesn't help here: SwiftUI manages
/// this window's frame and the name is ignored (verified — nothing was
/// written under it). So the frame is stored under a key of our own, which
/// is stable across releases and independent of the view hierarchy.
///
/// Attached from the view tree rather than hunted down from the app
/// delegate: `view.window` is a definite answer, where "which of NSApp's
/// windows is the main one at launch" is a guess.
struct WindowFrameAutosaver: NSViewRepresentable {
    let defaultsKey: String

    func makeNSView(context: Context) -> NSView {
        let view = TrackingView()
        view.defaultsKey = defaultsKey
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}

    private final class TrackingView: NSView {
        var defaultsKey: String = ""
        private var applied = false
        // Not torn down in deinit: Swift 6 forbids touching non-Sendable
        // state from a nonisolated deinit, and these observers are scoped to
        // this window for the life of the app anyway.
        private var observers: [NSObjectProtocol] = []

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            guard !applied, !defaultsKey.isEmpty, let window else { return }
            applied = true

            // SwiftUI restores its own remembered frame *after* the view
            // reaches the window, so applying ours here alone is silently
            // overridden (observed: the seeded frame was replaced by
            // SwiftUI's). Apply once now and again on the next run-loop
            // tick, by which time SwiftUI has finished.
            restore(into: window)
            DispatchQueue.main.async { [weak self, weak window] in
                guard let self, let window else { return }
                self.restore(into: window, allowSeeding: false)
                self.observeFrameChanges(of: window)
            }

        }

        /// Save on every move and resize. Frame changes are user-driven and
        /// infrequent, so there is nothing to throttle. Installed only after
        /// the restore has settled, so SwiftUI's own frame isn't recorded as
        /// if the user had chosen it.
        private func observeFrameChanges(of window: NSWindow) {
            guard observers.isEmpty else { return }
            let key = defaultsKey
            for name in [NSWindow.didMoveNotification, NSWindow.didResizeNotification] {
                observers.append(
                    NotificationCenter.default.addObserver(
                        forName: name, object: window, queue: .main
                    ) { [weak window] _ in
                        MainActor.assumeIsolated {
                            guard let window else { return }
                            let frame = window.frame
                            UserDefaults.standard.set(
                                "\(frame.origin.x) \(frame.origin.y) \(frame.size.width) \(frame.size.height)",
                                forKey: key
                            )
                        }
                    }
                )
            }
        }

        private func restore(into window: NSWindow, allowSeeding: Bool = true) {
            guard let saved = UserDefaults.standard.string(forKey: defaultsKey) else {
                // First run: record what the app opened with, so there is
                // something to restore next time.
                if allowSeeding { store(window.frame) }
                return
            }
            let parts = saved.split(separator: " ").compactMap { Double($0) }
            guard parts.count == 4 else { return }
            let frame = NSRect(x: parts[0], y: parts[1], width: parts[2], height: parts[3])

            // Only restore a frame that still lands on a screen the user
            // has: an external display may be gone, and a window restored
            // onto it would be invisible with no way back.
            let fitsOnAScreen = NSScreen.screens.contains { $0.visibleFrame.intersects(frame) }
            guard fitsOnAScreen, frame.width > 200, frame.height > 200 else { return }
            window.setFrame(frame, display: true)
        }

        private func store(_ frame: NSRect) {
            let encoded = "\(frame.origin.x) \(frame.origin.y) \(frame.size.width) \(frame.size.height)"
            UserDefaults.standard.set(encoded, forKey: defaultsKey)
        }
    }
}
