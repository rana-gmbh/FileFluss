import SwiftUI
import AppKit
import QuickLookUI

/// Manages the QLPreviewPanel directly, without SwiftUI binding-driven show/hide.
/// This avoids race conditions where the panel re-opens after being closed.
@MainActor
final class QuickLookController: NSObject, @preconcurrency QLPreviewPanelDataSource, @preconcurrency QLPreviewPanelDelegate {
    var urls: [URL] = []
    /// The panel's list or gallery: the view Quick Look hands its arrow
    /// keys back to.
    weak var sourceView: NSView?

    func toggle() {
        guard let panel = QLPreviewPanel.shared() else { return }

        if panel.isVisible {
            panel.orderOut(nil)
        } else {
            guard !urls.isEmpty else { return }
            // Ensure the list or gallery is first responder so
            // QLPreviewPanel finds it via the responder chain.
            if let view = sourceView {
                view.window?.makeFirstResponder(view)
            }
            panel.makeKeyAndOrderFront(nil)
        }
    }

    /// Whether the shared panel is on screen. The archive panel checks this
    /// before unpacking anything for a preview: a selection change arrives on
    /// every arrow-key press, and unpacking a file nobody is looking at would
    /// turn holding the down arrow into a stream of extractions.
    var isPanelVisible: Bool {
        QLPreviewPanel.shared()?.isVisible ?? false
    }

    func updateAndReload(urls: [URL]) {
        self.urls = urls
        guard let panel = QLPreviewPanel.shared(), panel.isVisible else { return }
        if urls.isEmpty {
            panel.orderOut(nil)
        } else {
            panel.reloadData()
        }
    }

    // MARK: - QLPreviewPanelDataSource

    func numberOfPreviewItems(in panel: QLPreviewPanel!) -> Int {
        urls.count
    }

    func previewPanel(_ panel: QLPreviewPanel!, previewItemAt index: Int) -> (any QLPreviewItem)! {
        guard index >= 0, index < urls.count else { return nil }
        return urls[index] as NSURL
    }

    // MARK: - QLPreviewPanelDelegate

    /// Forward arrow key events back to the source list or gallery so the
    /// user can navigate files while Quick Look is open. Space closes the
    /// panel.
    func previewPanel(_ panel: QLPreviewPanel!, handle event: NSEvent!) -> Bool {
        guard let event, event.type == .keyDown else { return false }

        // Space toggles (closes) the panel
        if event.charactersIgnoringModifiers == " " {
            panel.orderOut(nil)
            return true
        }

        // Arrow keys → forward to the list or gallery to change selection.
        // A gallery is a grid, so left and right move through it as well.
        guard let view = sourceView else { return false }
        var arrowKeyCodes: Set<UInt16> = [125, 126] // down, up
        if view is NSCollectionView { arrowKeyCodes.formUnion([123, 124]) } // left, right
        if arrowKeyCodes.contains(event.keyCode) {
            view.keyDown(with: event)
            return true
        }

        return false
    }
}
