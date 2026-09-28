import Testing
import Foundation
@testable import FileFluss

/// The toolbar's transfers popover mixes both panels' transfers into one
/// list. These cover the ordering and the summary figure it shows, using the
/// pure helpers — an `AppState` instance reconnects cloud accounts on init
/// and has no business being built in a test.
@Suite("Toolbar transfers list")
@MainActor
struct ToolbarTransfersTests {

    private func transfer(_ operation: String, complete: Bool = false) -> TransferProgress {
        let t = TransferProgress(operation: operation, totalItems: 1)
        t.isComplete = complete
        return t
    }

    @Test("Running transfers come before finished ones")
    func runningFirst() {
        let finished = transfer("Copying", complete: true)
        let running = transfer("Uploading")

        let ordered = AppState.ordered(left: [finished], right: [running])

        #expect(ordered.map(\.transfer.operation) == ["Uploading", "Copying"])
        #expect(ordered.map(\.panel) == [.right, .left])
    }

    @Test("Within a group the newest is first")
    func newestFirstWithinGroup() async throws {
        let older = transfer("Copying")
        try await Task.sleep(for: .milliseconds(20))
        let newer = transfer("Moving")

        let ordered = AppState.ordered(left: [older], right: [newer])

        #expect(ordered.map(\.transfer.operation) == ["Moving", "Copying"])
    }

    @Test("Both panels' transfers appear, each tagged with its panel")
    func mixesBothPanels() {
        let left = transfer("Copying")
        let right = transfer("Uploading")

        let ordered = AppState.ordered(left: [left], right: [right])

        #expect(ordered.count == 2)
        #expect(Set(ordered.map(\.panel)) == [.left, .right])
        #expect(Set(ordered.map(\.id)) == [left.id, right.id])
    }

    @Test("A cancelling transfer still counts as running")
    func cancellingCountsAsRunning() {
        let cancelling = transfer("Copying")
        cancelling.isCancelled = true

        #expect(AppState.isRunning(cancelling))
    }

    @Test("Nothing running means no ring to draw")
    func noFractionWhenIdle() {
        #expect(AppState.meanFraction(of: []) == nil)
    }

    @Test("The ring shows the mean of what's running")
    func meanOfRunningTransfers() throws {
        let half = transfer("Copying")
        half.expectedBytesSingle = 100
        half.downloadBytes = 50
        half.isCloudDownload = true

        let empty = transfer("Uploading")

        let fraction = try #require(AppState.meanFraction(of: [half, empty]))
        #expect(abs(fraction - (half.fraction + empty.fraction) / 2) < 0.0001)
        #expect(fraction > 0)
    }
}
