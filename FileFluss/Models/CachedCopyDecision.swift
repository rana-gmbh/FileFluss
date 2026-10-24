import Foundation

/// What to do with the copy of a cloud file already sitting in the cache.
///
/// FileFluss opens a cloud file by downloading it and handing the copy to
/// the app the user double-clicked with. It then stamps that copy with the
/// server's modification date — so a copy FileFluss wrote carries the
/// server's date, and a copy some app has saved over carries a later one.
/// That difference is the only thing needed to tell "our download" from
/// "the user's work".
///
/// Which matters, because the cache used to delete any copy that didn't
/// match the server and download a fresh one. For a file the user had
/// edited in Pages, that was the app deleting their only copy of the
/// changes — exactly the "the changes are gone again" a user reported.
enum CachedCopyDecision: Equatable {
    /// The copy is ours and still current: open it as it is.
    case reuse
    /// The copy is ours and out of date: replace it.
    case refresh
    /// The copy has been edited since FileFluss wrote it. Keep it — put it
    /// somewhere the user can find — before downloading again.
    case preserveEditsThenRefresh

    /// Clocks and filesystems disagree about sub-second timestamps, and
    /// some filesystems keep only whole seconds.
    static let dateTolerance: TimeInterval = 1.0

    static func decide(
        localSize: Int64,
        localModified: Date?,
        remoteSize: Int64,
        remoteModified: Date
    ) -> CachedCopyDecision {
        guard let localModified else {
            // No date to compare: treat it as ours and out of date, which
            // is what the cache did before any of this.
            return .refresh
        }

        // Later than the server's date means something wrote to it after
        // the download — and the only thing that does that is the app the
        // user opened it with.
        if localModified.timeIntervalSince(remoteModified) > dateTolerance {
            return .preserveEditsThenRefresh
        }

        let sameSize = remoteSize == 0 || localSize == remoteSize
        let sameDate = abs(localModified.timeIntervalSince(remoteModified)) <= dateTolerance
        return sameSize && sameDate ? .reuse : .refresh
    }
}
