import SwiftUI
import AppKit
import FileFlussCore

/// Transparent NSView overlay that accepts drag-and-drop onto a sidebar row
/// — a favourite or a cloud account. Mirrors the routing in
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
///
/// Three things can therefore happen on one row, distinguished the way
/// Finder distinguishes them: near the row's edges the drop reorders the
/// favourites, over its middle it copies or moves the dragged items *into*
/// that folder, and resting there without dropping opens the row in the
/// panel so the drag can be carried further in.
struct SidebarDropTarget: NSViewRepresentable {
    enum Position: Equatable {
        case row(index: Int)
        case header
        case trailing(count: Int)
        /// A row that isn't part of the reorderable list — a cloud account.
        /// The whole row is a drop-into target and no insertion line is ever
        /// shown, because there is nothing to insert between.
        case fixedRow
    }

    let panelSide: PanelSide
    let appState: AppState
    let position: Position
    let setHoverInsertIndex: (Int?) -> Void
    /// Where a drop *onto the middle* of this row should copy or move to.
    /// Nil for rows that are only reorder targets. Finder's rule: between
    /// rows reorders, on a row transfers into it (issue #47).
    var transferDestination: TransferDestination?
    /// Row label for the copy-or-move prompt.
    var transferDestinationName: String = ""
    /// Reports whether the cursor is over the transfer band, so the row can
    /// highlight itself — a reorder line and a "drop into" have to look
    /// different or the user can't tell what will happen.
    var setTransferHovered: ((Bool) -> Void)?
    /// Opens this row in the panel when a drag rests on it, so the user can
    /// go on to drop into one of its folders (issue #47, Finder's
    /// spring-loaded folders). Nil for rows there is nothing to open.
    var springLoadAction: (@MainActor () -> Void)?

    func makeNSView(context: Context) -> NSView {
        let view = DropView()
        view.appState = appState
        view.panelSide = panelSide
        view.position = position
        view.setHoverInsertIndex = setHoverInsertIndex
        view.transferDestination = transferDestination
        view.destinationName = transferDestinationName
        view.setTransferHovered = setTransferHovered
        view.springLoadAction = springLoadAction
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        guard let view = nsView as? DropView else { return }
        view.appState = appState
        view.panelSide = panelSide
        view.position = position
        view.setHoverInsertIndex = setHoverInsertIndex
        view.transferDestination = transferDestination
        view.destinationName = transferDestinationName
        view.setTransferHovered = setTransferHovered
        view.springLoadAction = springLoadAction
    }

    final class DropView: NSView {
        var appState: AppState?
        var panelSide: PanelSide = .left
        var position: Position = .row(index: 0)
        var setHoverInsertIndex: ((Int?) -> Void)?
        var transferDestination: TransferDestination?
        var setTransferHovered: ((Bool) -> Void)?
        var springLoadAction: (@MainActor () -> Void)?
        private let springLoad = SpringLoadTimer()
        /// Row label, used in the copy-or-move prompt so it names where the
        /// files are going.
        var destinationName: String = ""

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
            setTransferHovered?(false)
            springLoad.disarm()
        }

        override func draggingEnded(_ sender: any NSDraggingInfo) {
            setHoverInsertIndex?(nil)
            setTransferHovered?(false)
            springLoad.disarm()
        }

        override func performDragOperation(_ sender: any NSDraggingInfo) -> Bool {
            let overTransferBand = isOverTransferBand(sender.draggingLocation)
            let insertIdx = insertionIndex(forCursorAt: sender.draggingLocation)
            setHoverInsertIndex?(nil)
            setTransferHovered?(false)
            springLoad.disarm()

            if overTransferBand, let destination = transferDestination {
                return startTransfer(to: destination, sender: sender)
            }
            // A fixed row has no reorder meaning, so there is nothing else
            // this drop could be.
            if case .fixedRow = position { return false }
            return doDrop(at: insertIdx, pasteboard: sender.draggingPasteboard)
        }

        /// Hands the dragged items to the app's one transfer routine, so a
        /// sidebar drop obeys exactly the same conflict, space-check and
        /// move-safety rules as a paste or a panel drop.
        private func startTransfer(to destination: TransferDestination, sender: any NSDraggingInfo) -> Bool {
            let pb = sender.draggingPasteboard
            // Finder's convention: Option forces a copy, Command forces a
            // move. Without a modifier, follow the app's own Copy/Move mode.
            let modifiers = NSEvent.modifierFlags
            return MainActor.assumeIsolated {
                guard let app = appState else { return false }
                // Finder's convention for the modifiers; without one, the
                // app's own drag-and-drop mode decides — including "ask",
                // which prompts rather than guessing.
                let forcedMove: Bool?
                if modifiers.contains(.option) {
                    forcedMove = false
                } else if modifiers.contains(.command) {
                    forcedMove = true
                } else {
                    forcedMove = nil
                }

                // A cloud drag never resolves its file promise here: that
                // would download every item just to send it somewhere else.
                if !app.cloudDragSourceItems.isEmpty, let sourceAccountId = app.cloudDragSourceAccountId {
                    let cloudURLs = AppState.cloudURLs(
                        for: app.cloudDragSourceItems,
                        accountId: sourceAccountId
                    )
                    guard !cloudURLs.isEmpty else { return false }
                    app.handleSidebarDrop(
                        localURLs: [], cloudURLs: cloudURLs,
                        to: destination, destinationName: self.destinationName,
                        forcedMove: forcedMove
                    )
                    return true
                }

                guard let urls = pb.readObjects(
                    forClasses: [NSURL.self],
                    options: [.urlReadingFileURLsOnly: true]
                ) as? [URL], !urls.isEmpty else { return false }

                app.handleSidebarDrop(
                    localURLs: urls, cloudURLs: [],
                    to: destination, destinationName: self.destinationName,
                    forcedMove: forcedMove
                )
                return true
            }
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
            case .fixedRow:
                // Not reorderable; the caller only uses this when the cursor
                // is outside the transfer band, which can't happen here.
                return 0
            }
        }

        /// True when the cursor is in the row's middle band. Finder's
        /// proportions: the outer quarters reorder, the middle half drops
        /// into the row. Only meaningful for `.row` positions that have
        /// somewhere to drop into.
        private func isOverTransferBand(_ screenPoint: NSPoint) -> Bool {
            guard transferDestination != nil else { return false }
            switch position {
            case .fixedRow:
                return true
            case .row:
                let local = convert(screenPoint, from: nil)
                return local.y > bounds.height * 0.25 && local.y < bounds.height * 0.75
            case .header, .trailing:
                return false
            }
        }

        private func updateHover(at screenPoint: NSPoint) {
            // Springing only while the cursor is over the drop-into band:
            // near the row edges the drag is a reorder, and navigating the
            // panel then would be a surprise.
            if let springLoadAction, isOverTransferBand(screenPoint) {
                springLoad.arm(true, action: springLoadAction)
            } else {
                springLoad.disarm()
            }

            if isOverTransferBand(screenPoint) {
                // Suppress the insertion line: showing both at once would
                // promise two different outcomes.
                setHoverInsertIndex?(nil)
                setTransferHovered?(true)
            } else {
                setTransferHovered?(false)
                setHoverInsertIndex?(insertionIndex(forCursorAt: screenPoint))
            }
        }

        private func operation(for sender: any NSDraggingInfo) -> NSDragOperation {
            let pb = sender.draggingPasteboard
            // Over the transfer band any draggable item is valid — files
            // included, where favouriting accepts only folders.
            if isOverTransferBand(sender.draggingLocation) {
                if let app = appState, !app.cloudDragSourceItems.isEmpty { return .copy }
                return pb.canReadObject(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) ? .copy : []
            }
            // A fixed row's only meaning is "drop into it". With no
            // destination — a disconnected account — it accepts nothing,
            // rather than showing a drop cursor that would do nothing.
            if case .fixedRow = position { return [] }
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
