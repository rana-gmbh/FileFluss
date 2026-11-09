import Testing
import AppKit
@testable import FileFluss

/// Dragging an entry out of an archive to the other panel crashed the app:
/// both panels' table views passed `draggingEnded(_:)` on to NSTableView,
/// which doesn't implement it, and the "unrecognized selector" aborted the
/// process. Delivered here the way AppKit delivers it — by selector — so a
/// regression fails this suite rather than a user's drag.
@Suite("Drag end on the panels' tables")
@MainActor
struct TableDragEndTests {

    private let selector = #selector(NSDraggingDestination.draggingEnded(_:))

    @Test("The local panel's table survives the end of a drag")
    func localTable() {
        let table = FileTableView()
        var cleared = false
        table.onDragLeft = { cleared = true }
        #expect(table.responds(to: selector))
        _ = table.perform(selector, with: NSObject())
        #expect(cleared)
    }

    @Test("The cloud panel's table survives the end of a drag")
    func cloudTable() {
        let table = CloudTableView()
        var cleared = false
        table.onDragLeft = { cleared = true }
        #expect(table.responds(to: selector))
        _ = table.perform(selector, with: NSObject())
        #expect(cleared)
    }
}
