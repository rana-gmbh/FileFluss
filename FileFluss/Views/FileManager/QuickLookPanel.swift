import SwiftUI
import AppKit
import QuickLookUI

/// Manages the QLPreviewPanel directly, without SwiftUI binding-driven show/hide.
/// This avoids race conditions where the panel re-opens after being closed.
@MainActor
final class QuickLookController: NSObject, @preconcurrency QLPreviewPanelDataSource, @preconcurrency QLPreviewPanelDelegate {
    var urls: [URL] = []
    weak var sourceTableView: NSTableView?

    func toggle() {
        guard let panel = QLPreviewPanel.shared() else { return }

        if panel.isVisible {
            panel.orderOut(nil)
        } else {
            guard !urls.isEmpty else { return }
            // Ensure the table view is first responder so QLPreviewPanel
            // finds it via the responder chain.
            if let tv = sourceTableView {
                tv.window?.makeFirstResponder(tv)
            }
            panel.makeKeyAndOrderFront(nil)
        }
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

    /// Forward arrow key events back to the source table view so the user
    /// can navigate files while Quick Look is open. Space closes the panel.
    func previewPanel(_ panel: QLPreviewPanel!, handle event: NSEvent!) -> Bool {
        guard let event, event.type == .keyDown else { return false }

        // Space toggles (closes) the panel
        if event.charactersIgnoringModifiers == " " {
            panel.orderOut(nil)
            return true
        }

        // Arrow keys → forward to table view to change selection
        let arrowKeyCodes: Set<UInt16> = [125, 126] // down, up
        if arrowKeyCodes.contains(event.keyCode), let tableView = sourceTableView {
            tableView.keyDown(with: event)
            return true
        }

        return false
    }
}
