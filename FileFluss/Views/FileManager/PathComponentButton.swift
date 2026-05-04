import SwiftUI
import AppKit

/// A breadcrumb-style button for the path bar that doubles as a drop target.
/// Built on AppKit so it can register for both file URLs and file promises
/// (cloud drags) — SwiftUI's drop modifiers don't expose `NSFilePromiseReceiver`.
///
/// Click runs `onClick`. Drops route through `onDropURLs` (Finder/local-panel
/// drags) and `onDropCloudPromise` (cloud-panel drags); both return `true`
/// when the receiver consumed the drop. The drop callbacks run on the main
/// actor; the view paints a subtle accent fill while a drag is hovering.
struct PathComponentButton: View {
    let title: String
    var onClick: () -> Void
    var onDropURLs: ([URL]) -> Bool
    var onDropCloudPromise: () -> Bool

    var body: some View {
        // Pin to a single text line. Without this clamp, SwiftUI lets the
        // wrapped NSView flex vertically and the path bar grows much taller
        // than the surrounding HStack expects.
        PathComponentNSViewRepresentable(
            title: title,
            onClick: onClick,
            onDropURLs: onDropURLs,
            onDropCloudPromise: onDropCloudPromise
        )
        .frame(height: 18)
        .fixedSize(horizontal: true, vertical: true)
    }
}

private struct PathComponentNSViewRepresentable: NSViewRepresentable {
    let title: String
    var onClick: () -> Void
    var onDropURLs: ([URL]) -> Bool
    var onDropCloudPromise: () -> Bool

    func makeNSView(context: Context) -> PathComponentNSButton {
        let view = PathComponentNSButton()
        view.title = title
        view.onClick = onClick
        view.onDropURLs = onDropURLs
        view.onDropCloudPromise = onDropCloudPromise
        return view
    }

    func updateNSView(_ view: PathComponentNSButton, context: Context) {
        view.title = title
        view.onClick = onClick
        view.onDropURLs = onDropURLs
        view.onDropCloudPromise = onDropCloudPromise
    }
}

final class PathComponentNSButton: NSView {
    private let label = NSTextField(labelWithString: "")
    private var trackingArea: NSTrackingArea?

    var title: String = "" {
        didSet {
            label.stringValue = title
            invalidateIntrinsicContentSize()
        }
    }
    var onClick: (() -> Void)?
    var onDropURLs: (([URL]) -> Bool)?
    var onDropCloudPromise: (() -> Bool)?

    private var isHighlighted: Bool = false {
        didSet { needsDisplay = true }
    }
    private var isHovered: Bool = false {
        didSet { needsDisplay = true }
    }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        label.translatesAutoresizingMaskIntoConstraints = false
        label.font = .systemFont(ofSize: NSFont.systemFontSize, weight: .medium)
        label.textColor = .labelColor
        label.lineBreakMode = .byTruncatingMiddle
        addSubview(label)
        NSLayoutConstraint.activate([
            label.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 4),
            label.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -4),
            label.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])

        let promiseTypes = NSFilePromiseReceiver.readableDraggedTypes.map { NSPasteboard.PasteboardType($0) }
        registerForDraggedTypes([.fileURL] + promiseTypes)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var intrinsicContentSize: NSSize {
        // Match the height of a SwiftUI `Button(.plain)` so the path bar
        // stays a single text-line tall. Width gets a small horizontal
        // padding so the hover/drag highlight has breathing room.
        let labelSize = label.intrinsicContentSize
        return NSSize(width: labelSize.width + 8, height: labelSize.height)
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingArea { removeTrackingArea(trackingArea) }
        let area = NSTrackingArea(
            rect: bounds,
            options: [.mouseEnteredAndExited, .activeInKeyWindow, .cursorUpdate],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(area)
        trackingArea = area
    }

    override func mouseEntered(with event: NSEvent) { isHovered = true }
    override func mouseExited(with event: NSEvent) { isHovered = false }
    override func cursorUpdate(with event: NSEvent) { NSCursor.pointingHand.set() }

    override func mouseDown(with event: NSEvent) {
        // Visual press feedback during the drag-detection window so a quick
        // click still feels responsive.
        let pressed = isHighlighted
        isHighlighted = true
        defer { isHighlighted = pressed }
        let upEvent = window?.nextEvent(matching: [.leftMouseUp, .leftMouseDragged])
        if upEvent?.type == .leftMouseUp {
            onClick?()
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        let path = NSBezierPath(roundedRect: bounds.insetBy(dx: 1, dy: 1), xRadius: 4, yRadius: 4)
        if isHighlighted {
            NSColor.controlAccentColor.withAlphaComponent(0.35).setFill()
            path.fill()
        } else if isHovered {
            NSColor.secondaryLabelColor.withAlphaComponent(0.12).setFill()
            path.fill()
        }
    }

    // MARK: - Drag destination

    override func draggingEntered(_ sender: any NSDraggingInfo) -> NSDragOperation {
        let pb = sender.draggingPasteboard
        if pb.canReadObject(forClasses: [NSFilePromiseReceiver.self], options: nil) {
            isHighlighted = true
            return .copy
        }
        if pb.canReadObject(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) {
            isHighlighted = true
            return .generic
        }
        return []
    }

    override func draggingUpdated(_ sender: any NSDraggingInfo) -> NSDragOperation {
        let pb = sender.draggingPasteboard
        if pb.canReadObject(forClasses: [NSFilePromiseReceiver.self], options: nil) { return .copy }
        if pb.canReadObject(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) { return .generic }
        return []
    }

    override func draggingExited(_ sender: (any NSDraggingInfo)?) { isHighlighted = false }
    override func draggingEnded(_ sender: any NSDraggingInfo) { isHighlighted = false }

    override func performDragOperation(_ sender: any NSDraggingInfo) -> Bool {
        defer { isHighlighted = false }
        let pb = sender.draggingPasteboard
        if pb.canReadObject(forClasses: [NSFilePromiseReceiver.self], options: nil) {
            return MainActor.assumeIsolated { onDropCloudPromise?() ?? false }
        }
        guard let urls = pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty else {
            return false
        }
        return MainActor.assumeIsolated { onDropURLs?(urls) ?? false }
    }
}
