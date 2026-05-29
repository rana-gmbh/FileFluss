import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

/// Verifies `SyncPlanner` computes `netDestinationDelta` correctly — the net
/// bytes the destination gains, used to project remaining quota / free space
/// in the planner UI (storage-quota awareness feature).
@Suite("SyncPlanner net destination delta")
struct SyncPlannerTests {

    private func file(_ path: String, _ size: Int64, mod: Date = Date(timeIntervalSince1970: 1_000)) -> SyncEntry {
        SyncEntry(relativePath: path, isDirectory: false, size: size, modificationDate: mod)
    }

    @Test("Pure adds sum their sizes")
    func addsSum() async {
        let plan = await SyncPlanner().plan(
            sourceEntries: [file("a.txt", 100), file("b.txt", 50)],
            destEntries: [],
            mode: .newer, direction: .leftToRight,
            sourceIsCloud: false, destIsCloud: true
        )
        #expect(plan.netDestinationDelta == 150)
    }

    @Test("Replace counts only growth (new larger than old)")
    func replaceGrows() async {
        let old = Date(timeIntervalSince1970: 1_000)
        let new = Date(timeIntervalSince1970: 9_000)
        let plan = await SyncPlanner().plan(
            sourceEntries: [file("a.txt", 300, mod: new)],
            destEntries: [file("a.txt", 100, mod: old)],
            mode: .newer, direction: .leftToRight,
            sourceIsCloud: false, destIsCloud: true
        )
        // Replaces a 100-byte file with 300 bytes → +200 net.
        #expect(plan.netDestinationDelta == 200)
    }

    @Test("Replace with smaller file frees space (negative delta)")
    func replaceShrinks() async {
        let old = Date(timeIntervalSince1970: 1_000)
        let new = Date(timeIntervalSince1970: 9_000)
        let plan = await SyncPlanner().plan(
            sourceEntries: [file("a.txt", 40, mod: new)],
            destEntries: [file("a.txt", 100, mod: old)],
            mode: .mirror, direction: .leftToRight,
            sourceIsCloud: false, destIsCloud: true
        )
        #expect(plan.netDestinationDelta == -60)
    }

    @Test("Additive mode adds the renamed copy in full")
    func additiveAddsFull() async {
        let plan = await SyncPlanner().plan(
            sourceEntries: [file("a.txt", 80)],
            destEntries: [file("a.txt", 100)],
            mode: .additive, direction: .leftToRight,
            sourceIsCloud: false, destIsCloud: true
        )
        // Conflict → source added under a unique name; nothing overwritten.
        #expect(plan.netDestinationDelta == 80)
    }

    @Test("Mirror delete frees the removed file's bytes")
    func mirrorDeleteFrees() async {
        let plan = await SyncPlanner().plan(
            sourceEntries: [file("keep.txt", 10)],
            destEntries: [file("keep.txt", 10), file("stale.txt", 500)],
            mode: .mirror, direction: .leftToRight,
            sourceIsCloud: false, destIsCloud: true
        )
        // keep.txt identical (skipped), stale.txt deleted → −500.
        #expect(plan.netDestinationDelta == -500)
    }
}
