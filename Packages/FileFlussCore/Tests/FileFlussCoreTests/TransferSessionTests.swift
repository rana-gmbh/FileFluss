import Testing
import Foundation
@testable import FileFlussCore

/// How a session that moves files is allowed to time out.
@Suite("File transfer session")
struct TransferSessionTests {

    /// The bug this exists to prevent: every provider capped the whole
    /// request at five to thirty minutes, so a transfer that simply took
    /// longer than the link allowed was killed in the middle — a 1.5 GB
    /// upload to Nextcloud stopped at about half, every single time.
    @Test("No wall-clock cap on a transfer")
    func noResourceTimeout() {
        let config = URLSessionConfiguration.fileTransfer()

        // The system default, which is seven days: effectively uncapped.
        #expect(config.timeoutIntervalForResource == 7 * 24 * 60 * 60)
    }

    /// A stalled connection still has to be caught — that is what the
    /// inactivity timeout is for, and it is the one worth setting.
    @Test("A stalled transfer is still cut off")
    func keepsInactivityTimeout() {
        #expect(URLSessionConfiguration.fileTransfer().timeoutIntervalForRequest == 30)
        #expect(URLSessionConfiguration.fileTransfer(inactivityTimeout: 60).timeoutIntervalForRequest == 60)
    }
}
