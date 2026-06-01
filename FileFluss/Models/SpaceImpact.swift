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

    /// Free space remaining after the transfer, or `nil` when unlimited.
    var projectedFree: Int64? {
        available.map { $0 - addedBytes }
    }

    /// True when the transfer would not fit in the known available space.
    var wouldExceed: Bool {
        guard let available else { return false }
        return addedBytes > available
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
    /// in). Uses the "important usage" capacity macOS reports for user data,
    /// which already accounts for purgeable space. `nil` if it can't be read.
    static func localVolumeFreeBytes(at url: URL) -> Int64? {
        let values = try? url.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        if let bytes = values?.volumeAvailableCapacityForImportantUsage {
            return Int64(bytes)
        }
        // Fall back to the plain available-capacity key on volumes that don't
        // surface the "important usage" figure.
        let plain = try? url.resourceValues(forKeys: [.volumeAvailableCapacityKey])
        if let capacity = plain?.volumeAvailableCapacity {
            return Int64(capacity)
        }
        return nil
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

/// Renders the copy/move pre-flight warning message.
enum SpaceImpactFormatter {
    /// e.g. "Moving 4.2 GB into Dropbox would exceed the available space
    /// (only 1.1 GB free). Continue anyway?"
    static func warning(_ impact: SpaceImpact, verb: String) -> String {
        let added = ByteCountFormatter.string(fromByteCount: impact.addedBytes, countStyle: .file)
        let freeStr = impact.available.map { ByteCountFormatter.string(fromByteCount: max(0, $0), countStyle: .file) }
        let location = impact.isCloud ? impact.destinationName : "\"\(impact.destinationName)\""
        let verbL = L10n.text(verb)
        if let freeStr {
            return L10n.format("%@ %@ into %@ would exceed the available space (only %@ free). Continue anyway?", verbL, added, location, freeStr)
        }
        return L10n.format("%@ %@ into %@ may exceed the available space. Continue anyway?", verbL, added, location)
    }
}
