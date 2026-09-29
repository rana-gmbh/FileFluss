import Testing
import Foundation
@testable import FileFluss

/// What the progress bar reads, especially where the counters can't say.
@Suite("Transfer progress fraction")
@MainActor
struct TransferProgressFractionTests {

    /// The reported bug: an empty folder copied from one cloud account to
    /// another arrived intact and the bar stopped at 50%, because a folder
    /// with nothing in it moves no bytes and no files, leaving the
    /// cloud-to-cloud fallback parked between its two phases.
    @Test("An empty folder copied cloud-to-cloud finishes at 100%")
    func emptyFolderCloudToCloud() {
        let transfer = TransferProgress(operation: "Copying", totalItems: 1)
        transfer.isCloudToCloud = true
        transfer.currentPhase = .uploading
        transfer.recordSuccess("Empty Folder")

        // Mid-flight it still reads as halfway — the phases are all there is
        // to go on until something has a size.
        #expect(abs(transfer.fraction - 0.5) < 0.0001)

        transfer.isComplete = true
        transfer.endTime = Date()

        #expect(transfer.fraction == 1)
        #expect(transfer.percentText == "100%")
        #expect(transfer.completedCleanly)
    }

    @Test("An empty folder uploaded to a cloud account finishes at 100%")
    func emptyFolderSinglePhase() {
        let transfer = TransferProgress(operation: "Uploading", totalItems: 1)
        transfer.isCloudUpload = true
        transfer.recordSuccess("Empty Folder")
        transfer.isComplete = true

        #expect(transfer.fraction == 1)
    }

    @Test("A cancelled transfer keeps the progress it reached")
    func cancelledKeepsItsProgress() {
        let transfer = TransferProgress(operation: "Copying", totalItems: 4)
        transfer.totalFiles = 4
        transfer.completedItems = 1
        transfer.cancel()
        transfer.isComplete = true

        #expect(abs(transfer.fraction - 0.25) < 0.0001)
    }

    @Test("A part-failed transfer still fills — the colour carries the outcome")
    func partialFailureFills() {
        let transfer = TransferProgress(operation: "Copying", totalItems: 2)
        transfer.totalFiles = 2
        transfer.recordSuccess("a.txt")
        transfer.recordFailure("b.txt", error: "Forbidden")
        transfer.isComplete = true

        #expect(transfer.fraction == 1)
        #expect(transfer.hasErrors)
    }

    @Test("A running transfer is still measured by its bytes")
    func runningUsesBytes() {
        let transfer = TransferProgress(operation: "Downloading", totalItems: 1)
        transfer.isCloudDownload = true
        transfer.expectedBytesSingle = 200
        transfer.downloadBytes = 50

        #expect(abs(transfer.fraction - 0.25) < 0.0001)
    }
}
