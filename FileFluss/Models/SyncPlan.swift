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
}
