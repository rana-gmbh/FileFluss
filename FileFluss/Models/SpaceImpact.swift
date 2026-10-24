import Foundation
import FileFlussCore

/// The projected effect of a transfer on its destination's available space.
/// Produced by `AppState.spaceImpact(...)` and consumed by the copy/move
/// pre-flight warning and the sync planner. Destination-agnostic: `available`
/// is a cloud account's `total − used` quota or a local volume's free bytes.
struct SpaceImpact: Sendable {
    let destinationName: String
    let isCloud: Bool
    /// Free space at the destination right now. `nil` means unlimited or
    /// unknown (e.g. a provider that reports no total) — never warns.
    let available: Int64?
    /// Net bytes the destination will gain (already accounts for files that
    /// overwrite existing ones, which free their old size).
    let addedBytes: Int64
    /// Room the transfer needs on this Mac on its way through, when it has
    /// to pass through. nil when nothing is staged locally.
    let staging: Staging?

    init(
        destinationName: String,
        isCloud: Bool,
        available: Int64?,
        addedBytes: Int64,
        staging: Staging? = nil
    ) {
        self.destinationName = destinationName
        self.isCloud = isCloud
        self.available = available
        self.addedBytes = addedBytes
        self.staging = staging
    }

    /// What a transfer needs on the local disk while it is running.
    ///
    /// A cloud-to-cloud transfer is a download followed by an upload: every
    /// byte lands on this Mac first. The destination's quota can be ample
    /// while the Mac's disk is not, and nothing used to say so — the hole
    /// a user pointed out, with the words "a nearly full Mac SSD".
    struct Staging: Sendable {
        /// Where the bytes queue up — the cache folder from Settings →
        /// Storage, which may be on another disk entirely.
        let volumeName: String
        /// Most the folder will hold at once.
        let needed: Int64
        /// Free space there now, or nil when it couldn't be read.
        let free: Int64?

        var wouldExceed: Bool {
            guard let free else { return false }
            return needed > free
        }
    }

    /// Free space remaining after the transfer, or `nil` when unlimited.
    var projectedFree: Int64? {
        available.map { $0 - addedBytes }
    }

    /// True when the destination can't take it.
    var destinationWouldExceed: Bool {
        guard let available else { return false }
        return addedBytes > available
    }

    /// True when either end is short: the destination, or the disk the
    /// bytes travel across.
    var wouldExceed: Bool {
        destinationWouldExceed || (staging?.wouldExceed ?? false)
    }
}

/// Free-space helpers shared by the pre-flight and the sync planner so the
/// "available bytes" figure is computed identically everywhere.
enum SpaceCheck {
    /// UserDefaults key for the Settings → General toggle that enables the
    /// copy/move space pre-flight. Off by default so transfers stay fast.
    static let enabledKey = "checkSpaceBeforeTransfer"

    /// Whether the pre-flight is enabled. Defaults to false when unset.
    static var isEnabled: Bool {
        UserDefaults.standard.bool(forKey: enabledKey)
    }

    /// Bytes free on the volume backing `url` (the directory data would land
    /// in). `nil` if it can't be read.
    ///
    /// Takes the *larger* of the "important usage" capacity and the plain
    /// available capacity. The important-usage figure is meant to include
    /// purgeable space, but on external and network volumes it's frequently
    /// reported as a tiny value (or zero) even when gigabytes are free —
    /// which produced false "not enough space" warnings. The plain key is the
    /// reliable floor, so the maximum gives a correct, non-pessimistic figure
    /// on every volume type.
    static func localVolumeFreeBytes(at url: URL) -> Int64? {
        let v = try? url.resourceValues(forKeys: [
            .volumeAvailableCapacityForImportantUsageKey,
            .volumeAvailableCapacityKey,
        ])
        let important = v?.volumeAvailableCapacityForImportantUsage  // Int64?
        let plain = v?.volumeAvailableCapacity.map { Int64($0) }     // Int64?
        let candidates = [important, plain].compactMap { $0 }
        return candidates.max()
    }

    /// Free and total bytes for the volume backing `url`, for display. `nil` if
    /// the figures can't be read. Free space uses `localVolumeFreeBytes`.
    static func localVolumeCapacity(at url: URL) -> (free: Int64, total: Int64)? {
        guard let free = localVolumeFreeBytes(at: url),
              let total = (try? url.resourceValues(forKeys: [.volumeTotalCapacityKey]))?.volumeTotalCapacity,
              total > 0 else { return nil }
        return (free, Int64(total))
    }

    /// The volume's name as the user knows it — "Macintosh HD", the name
    /// of an external drive — falling back to the path when it can't be
    /// read. Used to say *which* disk needs the room.
    static func volumeName(at url: URL) -> String {
        if let name = (try? url.resourceValues(forKeys: [.volumeNameKey]))?.volumeName, !name.isEmpty {
            return name
        }
        return url.path
    }

    /// Whether two locations live on the same volume. A move within one
    /// volume is a rename and consumes no extra space, so the pre-flight can
    /// skip the warning. Returns false when either identifier can't be read.
    static func sameVolume(_ a: URL, _ b: URL) -> Bool {
        let lhs = (try? a.resourceValues(forKeys: [.volumeIdentifierKey]))?.volumeIdentifier
        let rhs = (try? b.resourceValues(forKeys: [.volumeIdentifierKey]))?.volumeIdentifier
        guard let lhs = lhs as? NSObject, let rhs = rhs as? NSObject else { return false }
        return lhs.isEqual(rhs)
    }
}

/// Renders a local volume's free space the way cloud accounts show their quota,
/// e.g. "120 GB free of 500 GB".
enum LocalVolumeFormatter {
    static func summary(free: Int64, total: Int64) -> String {
        let freeStr = ByteCountFormatter.string(fromByteCount: max(0, free), countStyle: .file)
        let totalStr = ByteCountFormatter.string(fromByteCount: max(0, total), countStyle: .file)
        return L10n.format("%@ free of %@", freeStr, totalStr)
    }
}

/// Renders the copy/move pre-flight warning message.
enum SpaceImpactFormatter {
    /// e.g. "Moving 4.2 GB into Dropbox would exceed the available space
    /// (only 1.1 GB free). Continue anyway?"
    static func warning(_ impact: SpaceImpact, verb: String) -> String {
        let added = ByteCountFormatter.string(fromByteCount: impact.addedBytes, countStyle: .file)
        let freeStr = impact.available.map { ByteCountFormatter.string(fromByteCount: max(0, $0), countStyle: .file) }
        let location = impact.isCloud ? impact.destinationName : "\"\(impact.destinationName)\""
        let verbL = L10n.text(verb)

        // Which end is short decides what the user has to do about it —
        // free space on the Mac, or at the destination — so the two are
        // never merged into one vague sentence.
        var sentences: [String] = []
        if impact.destinationWouldExceed {
            if let freeStr {
                sentences.append(L10n.format("%@ %@ into %@ would exceed the available space (only %@ free).", verbL, added, location, freeStr))
            } else {
                sentences.append(L10n.format("%@ %@ into %@ may exceed the available space.", verbL, added, location))
            }
        }
        if let staging = impact.staging, staging.wouldExceed {
            let needed = ByteCountFormatter.string(fromByteCount: staging.needed, countStyle: .file)
            let free = staging.free.map { ByteCountFormatter.string(fromByteCount: max(0, $0), countStyle: .file) } ?? "?"
            sentences.append(L10n.format(
                "The files pass through this Mac on the way: that needs %@ on %@, which has %@ free. The folder they pass through is set in Settings → Storage.",
                needed, staging.volumeName, free
            ))
        }
        if sentences.isEmpty {
            // Shouldn't be reachable — the dialog only shows for a warning —
            // but say something true rather than nothing.
            sentences.append(L10n.format("%@ %@ into %@ may exceed the available space.", verbL, added, location))
        }
        sentences.append(L10n.text("Continue anyway?"))
        return sentences.joined(separator: " ")
    }

    /// The line shown before a transfer starts, whether or not anything is
    /// short: what the destination has, and what the Mac needs on the way.
    static func preview(_ impact: SpaceImpact) -> String {
        var parts: [String] = []
        let added = ByteCountFormatter.string(fromByteCount: impact.addedBytes, countStyle: .file)
        if let available = impact.available {
            let free = ByteCountFormatter.string(fromByteCount: max(0, available), countStyle: .file)
            parts.append(L10n.format("%@ to %@ — %@ free", added, impact.destinationName, free))
        } else {
            parts.append(L10n.format("%@ to %@", added, impact.destinationName))
        }
        if let staging = impact.staging, staging.needed > 0 {
            let needed = ByteCountFormatter.string(fromByteCount: staging.needed, countStyle: .file)
            if let free = staging.free {
                let freeStr = ByteCountFormatter.string(fromByteCount: max(0, free), countStyle: .file)
                parts.append(L10n.format("needs %@ on %@ on the way (%@ free)", needed, staging.volumeName, freeStr))
            } else {
                parts.append(L10n.format("needs %@ on %@ on the way", needed, staging.volumeName))
            }
        }
        return parts.joined(separator: " · ")
    }
}
