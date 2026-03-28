import SwiftUI
import AppKit
import QuickLookUI

/// Manages the QLPreviewPanel directly, without SwiftUI binding-driven show/hide.
/// This avoids race conditions where the panel re-opens after being closed.
@MainActor
final class QuickLookController: NSObject, @preconcurrency QLPreviewPanelDataSource, QLPreviewPanelDelegate {
    var urls: [URL] = []
    private var anchorView: QuickLookAnchorView?

    func setAnchorView(_ view: QuickLookAnchorView) {
        anchorView = view
        view.controller = self
    }

    func toggle() {
        guard let panel = QLPreviewPanel.shared() else { return }

        if panel.isVisible {
            panel.orderOut(nil)
        } else {
            guard !urls.isEmpty else { return }
            anchorView?.window?.makeFirstResponder(anchorView)
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
}

/// Invisible NSView that participates in the responder chain for QLPreviewPanel.
class QuickLookAnchorView: NSView {
    var controller: QuickLookController?

    override var acceptsFirstResponder: Bool { true }

    override func acceptsPreviewPanelControl(_ panel: QLPreviewPanel!) -> Bool {
        true
    }

    override func beginPreviewPanelControl(_ panel: QLPreviewPanel!) {
        MainActor.assumeIsolated {
            panel.dataSource = controller
            panel.delegate = controller
        }
    }

    override func endPreviewPanelControl(_ panel: QLPreviewPanel!) {
        MainActor.assumeIsolated {
            panel.dataSource = nil
            panel.delegate = nil
        }
    }
}

/// NSViewRepresentable that embeds the anchor view into the SwiftUI hierarchy.
struct QuickLookBridge: NSViewRepresentable {
    let controller: QuickLookController

    func makeNSView(context: Context) -> QuickLookAnchorView {
        let view = QuickLookAnchorView()
        controller.setAnchorView(view)
        return view
    }

    func updateNSView(_ nsView: QuickLookAnchorView, context: Context) {
        // No-op: panel is controlled directly via QuickLookController.toggle()
    }
}
