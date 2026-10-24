import Testing
import Foundation
@testable import FileFluss

/// What happens to the copy of a cloud file already in the cache.
///
/// A user reported that changes made in Pages to a file opened from a
/// cloud account were gone the next time they opened it. Part of that was
/// FileFluss: the cache deleted any copy that no longer matched the
/// server, which for an edited file meant deleting the only copy of the
/// work.
@Suite("Cached cloud copies")
struct CachedCopyDecisionTests {

    private let remoteModified = Date(timeIntervalSince1970: 1_700_000_000)
    private let remoteSize: Int64 = 1_024

    private func decide(localSize: Int64, localModified: Date?) -> CachedCopyDecision {
        CachedCopyDecision.decide(
            localSize: localSize,
            localModified: localModified,
            remoteSize: remoteSize,
            remoteModified: remoteModified
        )
    }

    /// The copy FileFluss wrote carries the server's modification date, so
    /// matching date and size means it is ours and current.
    @Test("An untouched copy is reused")
    func untouchedCopyIsReused() {
        #expect(decide(localSize: remoteSize, localModified: remoteModified) == .reuse)
    }

    /// The case that lost work: an app saved over our copy, so its date is
    /// later than the server's. It is no longer ours to delete.
    @Test("A copy edited since the download is kept, not deleted")
    func editedCopyIsPreserved() {
        let edited = remoteModified.addingTimeInterval(120)

        #expect(decide(localSize: 2_048, localModified: edited) == .preserveEditsThenRefresh)
        // Even when the edit happened to leave the size alone.
        #expect(decide(localSize: remoteSize, localModified: edited) == .preserveEditsThenRefresh)
    }

    /// The server's copy moved on while ours sat in the cache: ours is
    /// stale and nobody has touched it, so replacing it loses nothing.
    @Test("A stale copy nobody touched is replaced")
    func staleCopyIsRefreshed() {
        // Older than the server's version, which is what a changed remote
        // looks like from here.
        let older = remoteModified.addingTimeInterval(-3600)

        #expect(decide(localSize: remoteSize, localModified: older) == .refresh)
        #expect(decide(localSize: 99, localModified: older) == .refresh)
    }

    /// Whole-second filesystems and clock skew must not make an untouched
    /// copy look edited — that would strew "(edited …)" files around for
    /// people who edited nothing.
    @Test("Sub-second differences are not an edit")
    func toleratesClockSkew() {
        #expect(decide(localSize: remoteSize, localModified: remoteModified.addingTimeInterval(0.4)) == .reuse)
        #expect(decide(localSize: remoteSize, localModified: remoteModified.addingTimeInterval(-0.4)) == .reuse)
        // Comfortably past the tolerance is an edit.
        #expect(decide(localSize: remoteSize, localModified: remoteModified.addingTimeInterval(5)) == .preserveEditsThenRefresh)
    }

    /// A provider that reports no size for an item shouldn't force a
    /// re-download of a copy that is otherwise current.
    @Test("An unknown remote size doesn't invalidate a current copy")
    func unknownRemoteSize() {
        let decision = CachedCopyDecision.decide(
            localSize: 4_096,
            localModified: remoteModified,
            remoteSize: 0,
            remoteModified: remoteModified
        )

        #expect(decision == .reuse)
    }

    @Test("A copy with no readable date is treated as ours and stale")
    func missingLocalDate() {
        #expect(decide(localSize: remoteSize, localModified: nil) == .refresh)
    }
}
