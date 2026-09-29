import Testing
import Foundation
@testable import FileFluss

/// The live figures behind issue #50: current file, speed, time left.
@Suite("Live transfer figures")
@MainActor
struct TransferLiveProgressTests {

    private func uploading(totalItems: Int = 1) -> TransferProgress {
        let transfer = TransferProgress(operation: "Uploading", totalItems: totalItems)
        transfer.isCloudUpload = true
        return transfer
    }

    @Test("Bytes posted off the main actor reach the transfer in a batch")
    func inboxIsDrained() {
        let transfer = uploading()
        transfer.beginFile("big.zip", size: 1000)

        transfer.byteInbox.addUpload(200)
        transfer.byteInbox.addUpload(300)
        // Nothing has been applied yet — that is the point of batching.
        #expect(transfer.uploadBytes == 0)

        transfer.flushBytes()

        #expect(transfer.uploadBytes == 500)
        #expect(transfer.currentFileBytes == 500)
        // Drained, not re-counted.
        transfer.flushBytes()
        #expect(transfer.uploadBytes == 500)
    }

    @Test("Per-file progress is relative to that file, not the whole transfer")
    func perFileFraction() {
        let transfer = uploading(totalItems: 2)
        transfer.beginFile("first.bin", size: 400)
        transfer.byteInbox.addUpload(300)
        transfer.flushBytes()

        #expect(transfer.currentFileFraction == 0.75)

        transfer.beginFile("second.bin", size: 400)
        #expect(transfer.currentFileBytes == 0)
        #expect(transfer.currentFileFraction == nil)
        // The transfer's own total keeps what the first file contributed.
        #expect(transfer.uploadBytes == 300)
    }

    @Test("A file of unknown size gets no per-file bar")
    func unknownFileSizeHasNoBar() {
        let transfer = uploading()
        transfer.beginFile("mystery.bin")
        transfer.byteInbox.addUpload(100)
        transfer.flushBytes()

        #expect(transfer.currentFileFraction == nil)
        #expect(transfer.currentFileBytes == 100)
    }

    @Test("Speed is measured over the recent window, and a stall clears it")
    func liveSpeedAndStall() async throws {
        let transfer = uploading()
        transfer.beginFile("big.zip", size: 10_000_000)
        transfer.expectedBytesSingle = 10_000_000

        // The pump samples on its own; feed it for a second.
        for _ in 0..<8 {
            transfer.byteInbox.addUpload(125_000)
            try await Task.sleep(for: .milliseconds(125))
        }

        let speed = try #require(transfer.liveBytesPerSecond)
        // ~1 MB/s, with plenty of room for timer jitter.
        #expect(speed > 300_000)
        #expect(transfer.liveSpeedText != nil)
        #expect(transfer.secondsRemaining != nil)

        // Nothing more arrives: after the window empties, the app stops
        // claiming a speed it can no longer see.
        try await Task.sleep(for: .seconds(5))
        #expect(transfer.liveBytesPerSecond == nil)
        #expect(transfer.secondsRemaining == nil)
    }

    @Test("A transfer that reports no bytes shows no invented figures")
    func noByteReportingShowsNothing() {
        // A local copy: FileManager has nothing to report.
        let transfer = TransferProgress(operation: "Copying", totalItems: 3)
        transfer.beginFile("Documents", size: 0)

        #expect(!transfer.hasByteReporting)
        #expect(transfer.liveDetailLine == nil)
        #expect(transfer.movedOfExpectedText == nil)
        #expect(transfer.secondsRemaining == nil)
    }

    @Test("The detail line carries only what is known")
    func detailLineComposition() {
        let transfer = uploading()
        transfer.expectedBytesSingle = 1_000_000
        transfer.beginFile("big.zip", size: 1_000_000)
        transfer.byteInbox.addUpload(250_000)
        transfer.flushBytes()

        let line = try? #require(transfer.liveDetailLine)
        #expect(line?.contains("of") == true)
        // No speed yet — one sample is not a measurement — so the line has
        // no speed segment rather than "0 bytes/s".
        #expect(transfer.liveSpeedText == nil)
        #expect(line?.contains("/s") == false)
    }

    @Test("A finished transfer stops showing live figures")
    func finishedHasNoLiveLine() {
        let transfer = uploading()
        transfer.expectedBytesSingle = 100
        transfer.byteInbox.addUpload(100)
        transfer.flushBytes()
        transfer.isComplete = true

        #expect(transfer.liveDetailLine == nil)
        #expect(transfer.secondsRemaining == nil)
    }

    @Test("Time left is phrased the way a countdown reads")
    func durationPhrasing() {
        #expect(TransferProgress.durationText(5) == "5 s")
        #expect(TransferProgress.durationText(90) == "2 min")
        #expect(TransferProgress.durationText(3600) == "1 h")
        #expect(TransferProgress.durationText(4500) == "1 h 15 min")
        // Never "0 s" — something still running needs at least a second.
        #expect(TransferProgress.durationText(0.2) == "1 s")
    }
}
