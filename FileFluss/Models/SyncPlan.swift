import Foundation

enum PlanDirection: Hashable, Sendable {
    case leftToRight
    case rightToLeft
}

enum SyncMode: String, Hashable, Sendable, CaseIterable {
    case mirror       // Replace destination to match source (add + replace + delete)
    case newer        // Add missing; replace older; never delete
    case additive     // Add missing; rename source when conflict; never delete

    var title: String {
        switch self {
        case .mirror:   return "Mirror (replace all)"
        case .newer:    return "Update newer files"
        case .additive: return "Add only (keep both on conflict)"
        }
    }

    var subtitle: String {
        switch self {
        case .mirror:   return "Destination will match source exactly. Extra files on the destination will be deleted."
        case .newer:    return "Copies missing files and replaces files that are older than the source."
        case .additive: return "Copies missing files. When a file exists on both sides, the source copy is added with a unique name."
        }
    }

    var isDestructive: Bool { self == .mirror }
}

/// A flat representation of a file or directory discovered during enumeration.
struct SyncEntry: Hashable, Sendable {
    /// Path relative to the root being enumerated, e.g. "sub/file.txt". Never starts with "/".
    let relativePath: String
    let isDirectory: Bool
    let size: Int64
    let modificationDate: Date
    /// Creation date when the source exposes it (local FS does, most cloud
    /// providers don't). Nil means "unknown — don't compare against this".
    let creationDate: Date?

    init(relativePath: String, isDirectory: Bool, size: Int64, modificationDate: Date, creationDate: Date? = nil) {
        self.relativePath = relativePath
        self.isDirectory = isDirectory
        self.size = size
        self.modificationDate = modificationDate
        self.creationDate = creationDate
    }

    /// Whether this entry or a folder above it is a dotfile.
    var isHidden: Bool {
        relativePath.split(separator: "/").contains { $0.hasPrefix(".") }
    }
}

/// A single operation the planner will perform to sync one side into the other.
enum SyncOperation: Hashable, Sendable {
    /// Source file (or directory) does not exist at destination — create it.
    case add(relativePath: String, isDirectory: Bool, bytes: Int64)
    /// Source file exists at destination and will overwrite it.
    case replace(relativePath: String, bytes: Int64)
    /// Source file exists at destination; add the source with a unique name on the destination.
    case addRenamed(sourceRelativePath: String, destRelativePath: String, bytes: Int64)
    /// Destination has a file/dir not present on source — delete it (mirror mode only).
    case delete(relativePath: String, isDirectory: Bool, bytes: Int64)
}

struct SyncPlan: Sendable {
    let mode: SyncMode
    let direction: PlanDirection
    let operations: [SyncOperation]
    let filesToAdd: Int
    let filesToReplace: Int
    let filesToDelete: Int
    let foldersToAdd: Int
    let foldersToDelete: Int
    /// Bytes expected to be downloaded (source is cloud — we read from source).
    let downloadBytes: Int64
    /// Bytes expected to be uploaded (destination is cloud — we write to destination).
    let uploadBytes: Int64
    /// Total bytes moved regardless of direction (for local-to-local transfers).
    let totalBytes: Int64
    /// Net change in the destination's used space if this plan runs: bytes
    /// added (new files + the *growth* of replaced files) minus bytes freed
    /// (the old size of replaced files, and deletes in mirror mode). Used to
    /// project remaining quota / free space in the planner. Can be negative
    /// when a mirror sync removes more than it adds.
    let netDestinationDelta: Int64
    /// What each side held when the plan was made, hidden files aside.
    let sourceEntryCount: Int
    let destinationEntryCount: Int
    /// Each source file's modification date, stamped onto its copy. Without
    /// that a copy carries the time it was made, looks changed on the next
    /// run, and every Mirror replaces everything again.
    var sourceModificationDates: [String: Date] = [:]
    /// Files left alone because a folder of the same name is at the
    /// destination, in a mode that promises never to delete.
    var typeClashes: Int = 0

    /// Whether this is the plan for what's selected now. A plan calculated
    /// for one direction or mode must not run under another.
    func matches(mode: SyncMode, direction: PlanDirection) -> Bool {
        self.mode == mode && self.direction == direction
    }

    /// Mirroring nothing over something. This is what a scan gone wrong looks
    /// like far more often than a real wish to wipe the destination, so it
    /// isn't offered.
    var wouldEmptyDestination: Bool {
        mode == .mirror && sourceEntryCount == 0 && destinationEntryCount > 0
    }

    /// Deletes at least half of a destination of any real size, which needs
    /// a confirmation of its own.
    var deletesMostOfDestination: Bool {
        let deletions = filesToDelete + foldersToDelete
        return deletions >= 10 && deletions * 2 >= destinationEntryCount
    }
}
