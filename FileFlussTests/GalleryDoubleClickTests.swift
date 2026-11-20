import Testing
import AppKit
@testable import FileFluss

/// A double-click on a tile opens it, as it does on a row of the list —
/// in a gallery that is the only way into a folder.
@Suite("Gallery double-click", .serialized)
@MainActor
struct GalleryDoubleClickTests {

    private final class Source: NSObject, NSCollectionViewDataSource, NSCollectionViewDelegate, NSMenuDelegate {
        let count: Int
        init(count: Int) { self.count = count }

        func collectionView(_ collectionView: NSCollectionView, numberOfItemsInSection section: Int) -> Int { count }

        func collectionView(_ collectionView: NSCollectionView, itemForRepresentedObjectAt indexPath: IndexPath) -> NSCollectionViewItem {
            let item = collectionView.makeItem(withIdentifier: FileGalleryItem.identifier, for: indexPath)
            (item as? FileGalleryItem)?.configure(
                id: "\(indexPath.item)",
                name: "Folder \(indexPath.item)",
                toolTip: "",
                icon: NSImage(size: NSSize(width: 16, height: 16)),
                metrics: FileGalleryMetrics()
            )
            return item
        }

        func collectionView(_ collectionView: NSCollectionView, canDragItemsAt indexPaths: Set<IndexPath>, with event: NSEvent) -> Bool { true }

        func collectionView(_ collectionView: NSCollectionView, pasteboardWriterForItemAt indexPath: IndexPath) -> (any NSPasteboardWriting)? {
            NSURL(fileURLWithPath: "/tmp/\(indexPath.item)")
        }
    }

    private struct Harness {
        let window: NSWindow
        let gallery: FileGalleryView
        let source: Source
    }

    private func makeHarness() -> Harness {
        let source = Source(count: 6)
        let (scrollView, gallery) = FileGalleryView.makeScrollView(
            dataSource: source, delegate: source, menuDelegate: source, draggedTypes: [.fileURL]
        )
        let window = NSWindow(
            contentRect: NSRect(x: 100, y: 100, width: 640, height: 480),
            styleMask: [.titled, .resizable], backing: .buffered, defer: false
        )
        window.isReleasedWhenClosed = false
        window.contentView = scrollView
        window.orderFront(nil)
        gallery.frame = NSRect(x: 0, y: 0, width: 640, height: 480)
        gallery.reloadData()
        window.layoutIfNeeded()
        gallery.layoutSubtreeIfNeeded()
        return Harness(window: window, gallery: gallery, source: source)
    }

    private func event(_ type: NSEvent.EventType, at point: NSPoint, in window: NSWindow, clicks: Int) -> NSEvent {
        NSEvent.mouseEvent(
            with: type,
            location: point,
            modifierFlags: [],
            timestamp: ProcessInfo.processInfo.systemUptime,
            windowNumber: window.windowNumber,
            context: nil,
            eventNumber: 0,
            clickCount: clicks,
            pressure: type == .leftMouseDown ? 1 : 0
        )!
    }

    /// The point in the middle of a tile's preview, in window coordinates.
    private func point(onItem item: Int, in harness: Harness) -> NSPoint {
        let frame = harness.gallery.frameForItem(at: item)
        return harness.gallery.convert(NSPoint(x: frame.midX, y: frame.minY + 40), to: nil)
    }

    /// A press as the gallery receives it, with its release already queued
    /// for the collection view's tracking loop. Handed to the gallery
    /// directly: a press sent through an inactive test window would only
    /// activate the window.
    private func click(_ harness: Harness, item: Int, clicks: Int) {
        let at = point(onItem: item, in: harness)
        NSApp.postEvent(event(.leftMouseUp, at: at, in: harness.window, clicks: clicks), atStart: false)
        harness.gallery.mouseDown(with: event(.leftMouseDown, at: at, in: harness.window, clicks: clicks))
    }

    @Test("a click on a tile reaches the gallery, which knows which tile it was")
    func clicksFindTheTile() throws {
        let harness = makeHarness()
        defer { harness.window.close() }
        let at = point(onItem: 2, in: harness)

        // The tile answers the hit test (its label and image don't), so the
        // press travels up the responder chain to the gallery…
        // The window's frame view hit-tests in window coordinates.
        let hit = try #require(harness.window.contentView?.superview?.hitTest(at))
        #expect(hit is FileGalleryTileView)
        // …and the collection view, which finds items by hit-testing their
        // views, sees the tile under the point. A tile answering nil made
        // every click land "between" tiles.
        #expect(harness.gallery.indexPathForItem(at: harness.gallery.convert(at, from: nil)) == IndexPath(item: 2, section: 0))
    }

    @Test("a double-click on a tile opens that tile")
    func doubleClickOpens() {
        let harness = makeHarness()
        defer { harness.window.close() }
        var opened: [Int] = []
        harness.gallery.onDoubleClick = { opened.append($0) }

        click(harness, item: 2, clicks: 1)
        click(harness, item: 2, clicks: 2)

        #expect(opened == [2])
    }

    @Test("a single click opens nothing")
    func singleClickDoesNotOpen() {
        let harness = makeHarness()
        defer { harness.window.close() }
        var opened: [Int] = []
        harness.gallery.onDoubleClick = { opened.append($0) }

        click(harness, item: 1, clicks: 1)

        #expect(opened.isEmpty)
    }
}
