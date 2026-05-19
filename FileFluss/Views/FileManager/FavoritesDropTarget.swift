import SwiftUI
import AppKit
import FileFlussCore

/// Transparent NSView overlay that accepts drag-and-drop onto the
/// sidebar's Favorites section. Mirrors the routing in
/// `PathComponentButton`: a fileURL drag from Finder or the local
/// file-list panel is read directly off the pasteboard, while a drag
/// originating from a cloud panel is identified by the in-memory
/// `AppState.cloudDragSource*` channel and short-circuits the
/// `NSFilePromiseProvider` so we never trigger a download. Only
/// folders are turned into favorites — dropping a file is rejected.
///
/// Each overlay knows its row's index (or is configured as the section
/// header / trailing zone) and reports a "would-insert-here" index back
/// to its parent via `setHoverInsertIndex` so SwiftUI can render the
/// blue insertion line that matches a row-reorder operation. The same
/// computed index drives the actual insertion at drop time.
struct FavoritesDropTarget: NSViewRepresentable {
    enum Position: Equatable {
        case row(index: Int)
        case header
        case trailing(count: Int)
    }

    let panelSide: PanelSide
    let appState: AppState
    let position: Position
    let setHoverInsertIndex: (Int?) -> Void

    func makeNSView(context: Context) -> NSView {
        let view = DropView()
        view.appState = appState
        view.panelSide = panelSide
        view.position = position
        view.setHoverInsertIndex = setHoverInsertIndex
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        guard let view = nsView as? DropView else { return }
        view.appState = appState
        view.panelSide = panelSide
        view.position = position
        view.setHoverInsertIndex = setHoverInsertIndex
    }

    final class DropView: NSView {
        var appState: AppState?
        var panelSide: PanelSide = .left
        var position: Position = .row(index: 0)
        var setHoverInsertIndex: ((Int?) -> Void)?

        override init(frame frameRect: NSRect) {
            super.init(frame: frameRect)
            commonInit()
        }

        required init?(coder: NSCoder) {
            super.init(coder: coder)
            commonInit()
        }

        private func commonInit() {
            registerForDraggedTypes([
                .fileURL,
                .init(rawValue: kUTTypeFileURL as String),
                .init(rawValue: "com.apple.pasteboard.promised-file-content-type"),
            ])
        }

        override var isFlipped: Bool { true }

        // Mouse clicks fall through so the underlying List row stays
        // selectable; only drag enter/over/perform are intercepted.
        override func hitTest(_ point: NSPoint) -> NSView? { nil }

        // MARK: Drag destination

        override func draggingEntered(_ sender: any NSDraggingInfo) -> NSDragOperation {
            updateHover(at: sender.draggingLocation)
            return operation(for: sender)
        }

        override func draggingUpdated(_ sender: any NSDraggingInfo) -> NSDragOperation {
            updateHover(at: sender.draggingLocation)
            return operation(for: sender)
        }

        override func draggingExited(_ sender: (any NSDraggingInfo)?) {
            setHoverInsertIndex?(nil)
        }

        override func draggingEnded(_ sender: any NSDraggingInfo) {
            setHoverInsertIndex?(nil)
        }

        override func performDragOperation(_ sender: any NSDraggingInfo) -> Bool {
            let insertIdx = insertionIndex(forCursorAt: sender.draggingLocation)
            setHoverInsertIndex?(nil)
            return doDrop(at: insertIdx, pasteboard: sender.draggingPasteboard)
        }

        // MARK: Helpers

        /// Picks the insertion index based on where in this overlay the
        /// cursor is. For a `.row` the upper half means "insert above
        /// this row" and the lower half means "insert below it". The
        /// header and trailing positions are fixed.
        private func insertionIndex(forCursorAt screenPoint: NSPoint) -> Int {
            switch position {
            case .header:
                return 0
            case .trailing(let count):
                return count
            case .row(let index):
                let local = convert(screenPoint, from: nil)
                return local.y < bounds.midY ? index : index + 1
            }
        }

        private func updateHover(at screenPoint: NSPoint) {
            setHoverInsertIndex?(insertionIndex(forCursorAt: screenPoint))
        }

        private func operation(for sender: any NSDraggingInfo) -> NSDragOperation {
            let pb = sender.draggingPasteboard
            // In-app cloud drag (file-promise) — accept only if at
            // least one dragged item is a directory.
            if let app = appState, !app.cloudDragSourceItems.isEmpty {
                return app.cloudDragSourceItems.contains(where: { $0.isDirectory }) ? .generic : []
            }
            // Local file URL drag — accept anything URL-shaped at this
            // stage; we filter to directories at drop time.
            if pb.canReadObject(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) {
                return .generic
            }
            return []
        }

        private func doDrop(at insertIdx: Int, pasteboard pb: NSPasteboard) -> Bool {
            // Cloud drag wins — never resolve the file-promise here.
            if let app = appState,
               !app.cloudDragSourceItems.isEmpty,
               let sourceAccountId = app.cloudDragSourceAccountId {
                return MainActor.assumeIsolated {
                    var inserted = 0
                    for item in app.cloudDragSourceItems where item.isDirectory {
                        app.addCloudFavorite(
                            accountId: sourceAccountId,
                            path: item.path,
                            name: item.name,
                            to: panelSide,
                            at: insertIdx + inserted
                        )
                        inserted += 1
                    }
                    return inserted > 0
                }
            }

            guard let urls = pb.readObjects(
                forClasses: [NSURL.self],
                options: [.urlReadingFileURLsOnly: true]
            ) as? [URL], !urls.isEmpty else {
                return false
            }
            return MainActor.assumeIsolated {
                guard let app = appState else { return false }
                var inserted = 0
                for url in urls {
                    var isDir: ObjCBool = false
                    if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue {
                        app.addLocalFavorite(url: url, to: panelSide, at: insertIdx + inserted)
                        inserted += 1
                    }
                }
                return inserted > 0
            }
        }
    }
}

/// Mimics the AppKit table-view reorder indicator: a 2pt accent line
/// with a hollow circle at the leading edge. Rendered as an overlay
/// at the top of the favorite row that's the current insertion target.
struct FavoritesInsertionLine: View {
    var body: some View {
        HStack(spacing: 0) {
            Circle()
                .stroke(Color.accentColor, lineWidth: 2)
                .frame(width: 7, height: 7)
            Rectangle()
                .fill(Color.accentColor)
                .frame(height: 2)
        }
        .padding(.leading, 4)
        .allowsHitTesting(false)
    }
}
