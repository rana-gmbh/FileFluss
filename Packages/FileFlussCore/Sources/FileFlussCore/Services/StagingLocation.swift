import Foundation

/// Single source of truth for where FileFluss stages downloads and keeps its
/// caches. By default this lives on the system temporary volume (the internal
/// disk), but the user can point it at any folder — e.g. an external drive —
/// via Settings → Storage. That matters when copying large files from a cloud
/// account to an external drive: the bytes are staged here first, so keeping
/// the staging folder on the destination's volume avoids filling the internal
/// disk (and makes the final move a fast same-volume rename).
public enum StagingLocation {
    /// UserDefaults key holding the absolute path of the user-chosen folder.
    /// Empty / unset means "use the system temporary folder".
    public static let customFolderKey = "customCacheFolderPath"

    /// The user-chosen folder, or nil when unset or currently unusable (e.g.
    /// the external drive is unplugged) — callers then fall back to the system
    /// temp so transfers never hard-fail on a missing cache folder.
    public static var customFolder: URL? {
        guard let path = UserDefaults.standard.string(forKey: customFolderKey), !path.isEmpty else {
            return nil
        }
        let url = URL(fileURLWithPath: path, isDirectory: true)
        let fm = FileManager.default
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue,
              fm.isWritableFile(atPath: url.path) else {
            return nil
        }
        return url
    }

    /// The directory FileFluss stages and caches files under. Always exists on
    /// return. Lives inside a "FileFluss-Cache" subfolder so a custom location
    /// (which may be a folder the user also uses for other things) stays tidy.
    public static func base() -> URL {
        let root = (customFolder ?? FileManager.default.temporaryDirectory)
            .appendingPathComponent("FileFluss-Cache", isDirectory: true)
        try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    /// A unique path for a single download's staging file, under `base()`.
    public static func downloadStagingFile() -> URL {
        base().appendingPathComponent("dl-\(UUID().uuidString)")
    }

    /// A unique staging subdirectory (e.g. for cloud↔cloud transfers), created.
    public static func stagingDirectory(prefix: String) -> URL {
        let dir = base().appendingPathComponent("\(prefix)-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    /// Roots to scan when measuring or clearing the cloud preview cache: the
    /// current base plus the legacy system-temp root, so caches written before
    /// a custom folder was chosen (or by older builds) are still found.
    public static func cacheScanRoots() -> [URL] {
        [base(), FileManager.default.temporaryDirectory]
    }
}
