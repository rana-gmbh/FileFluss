import Foundation

public actor FileSystemService {
    public static let shared = FileSystemService()

    private static let resourceKeys: Set<URLResourceKey> = [
        .nameKey, .isDirectoryKey, .fileSizeKey, .totalFileSizeKey,
        .contentModificationDateKey, .creationDateKey, .isHiddenKey,
        .isSymbolicLinkKey, .contentTypeKey
    ]

    public func listDirectory(at url: URL, showHidden: Bool = false) throws -> [FileItem] {
        let contents = try Foundation.FileManager.default.contentsOfDirectory(
            at: url,
            includingPropertiesForKeys: Array(Self.resourceKeys),
            options: showHidden ? [] : [.skipsHiddenFiles]
        )

        return try contents.map { itemURL in
            let values = try itemURL.resourceValues(forKeys: Self.resourceKeys)
            return FileItem(url: itemURL, resourceValues: values)
        }
    }

    public func createDirectory(at url: URL) throws {
        do {
            try Foundation.FileManager.default.createDirectory(
                at: url,
                withIntermediateDirectories: true
            )
            SupportLogger.shared.log("createDirectory: \(url.path)", category: "fs")
        } catch {
            SupportLogger.shared.log("createDirectory FAILED: \(url.path) — \(error.localizedDescription)", category: "fs", level: .error)
            throw error
        }
    }

    public func deleteItem(at url: URL) throws {
        do {
            try Foundation.FileManager.default.removeItem(at: url)
            SupportLogger.shared.log("deleteItem: \(url.path)", category: "fs")
        } catch {
            SupportLogger.shared.log("deleteItem FAILED: \(url.path) — \(error.localizedDescription)", category: "fs", level: .error)
            throw error
        }
    }

    public func trashItem(at url: URL) throws {
        do {
            try Foundation.FileManager.default.trashItem(at: url, resultingItemURL: nil)
            SupportLogger.shared.log("trashItem: \(url.path)", category: "fs")
        } catch {
            SupportLogger.shared.log("trashItem FAILED: \(url.path) — \(error.localizedDescription)", category: "fs", level: .error)
            throw error
        }
    }

    /// Whether a `trashItem` failure is because the item's volume has no Trash
    /// (some external/USB and read-only volumes). macOS reports this as
    /// `NSFeatureUnsupportedError`, regardless of the file name — callers then
    /// offer an immediate permanent delete instead, like Finder. Detecting it by
    /// error code (rather than probing for a `.Trashes` folder) is filename- and
    /// state-independent: the earlier probe wrongly reported "Trash supported"
    /// once a volume already had a `.Trashes`, or for names beginning with `~`.
    public nonisolated static func isNoTrashOnVolumeError(_ error: Error) -> Bool {
        (error as? CocoaError)?.code == .featureUnsupported
    }

    /// True when the file system refused the operation for want of
    /// permission — macOS withholding a protected folder such as Documents
    /// or Desktop, or the item's own ownership.
    ///
    /// Worth telling apart from every other failure, because the remedy is
    /// a setting the user can change rather than something the app can
    /// retry. A customer whose support log showed three of these in a row
    /// was told only "failed", which left him with nothing to do about it.
    public nonisolated static func isPermissionError(_ error: Error) -> Bool {
        let ns = error as NSError
        if ns.domain == NSCocoaErrorDomain,
           ns.code == NSFileWriteNoPermissionError || ns.code == NSFileReadNoPermissionError {
            return true
        }
        if ns.domain == NSPOSIXErrorDomain,
           ns.code == Int(EPERM) || ns.code == Int(EACCES) {
            return true
        }
        // Cocoa usually wraps the POSIX error that actually happened.
        if let underlying = ns.userInfo[NSUnderlyingErrorKey] as? NSError {
            return isPermissionError(underlying)
        }
        return false
    }

    public func moveItem(from source: URL, to destination: URL) throws {
        do {
            try Foundation.FileManager.default.moveItem(at: source, to: destination)
            SupportLogger.shared.log("moveItem: \(source.path) → \(destination.path)", category: "fs")
        } catch {
            SupportLogger.shared.log("moveItem FAILED: \(source.path) → \(destination.path) — \(error.localizedDescription)", category: "fs", level: .error)
            throw error
        }
    }

    public func copyItem(from source: URL, to destination: URL) throws {
        do {
            try Foundation.FileManager.default.copyItem(at: source, to: destination)
            SupportLogger.shared.log("copyItem: \(source.path) → \(destination.path)", category: "fs")
        } catch {
            SupportLogger.shared.log("copyItem FAILED: \(source.path) → \(destination.path) — \(error.localizedDescription)", category: "fs", level: .error)
            throw error
        }
    }

    // MARK: - Replacing

    /// A name for the new copy while it's being written next to the item it
    /// will replace. Visible on purpose: if the app dies part way, whatever
    /// is left can be seen and deleted. The extension stays last because some
    /// servers judge an upload by it.
    public static func temporaryReplacementName(for name: String) -> String {
        let ns = name as NSString
        let tag = UUID().uuidString.prefix(8).lowercased()
        // Leaves room in the 255 bytes most file systems allow for a name.
        let base = String(ns.deletingPathExtension.prefix(150))
        let stem = "\(base).filefluss-part-\(tag)"
        return ns.pathExtension.isEmpty ? stem : "\(stem).\(ns.pathExtension)"
    }

    /// Replaces `destination` with `source`, removing `source`. The old item
    /// stays until the new one is ready, so a failure loses neither.
    public func replaceItem(at destination: URL, withMoved source: URL) throws {
        let temporary = Self.temporarySibling(of: destination)
        let fm = Foundation.FileManager.default
        // A move across volumes is a copy, which is the slow part that can
        // fail. Doing it first leaves the swap as a single rename.
        try fm.moveItem(at: source, to: temporary)
        do {
            try installReplacement(temporary, at: destination)
        } catch {
            try? fm.moveItem(at: temporary, to: source)
            throw error
        }
    }

    /// Replaces `destination` with a copy of `source`. As above: the old item
    /// stays until the copy is complete.
    public func replaceItem(at destination: URL, withCopyOf source: URL) throws {
        let temporary = Self.temporarySibling(of: destination)
        let fm = Foundation.FileManager.default
        do {
            try fm.copyItem(at: source, to: temporary)
            try installReplacement(temporary, at: destination)
        } catch {
            try? fm.removeItem(at: temporary)
            throw error
        }
    }

    /// Swaps a finished copy into place. `temporary` has to be on the same
    /// volume as `destination`, which is what makes this a rename.
    public func installReplacement(_ temporary: URL, at destination: URL) throws {
        let fm = Foundation.FileManager.default
        do {
            if fm.fileExists(atPath: destination.path) {
                _ = try fm.replaceItemAt(destination, withItemAt: temporary)
            } else {
                try fm.moveItem(at: temporary, to: destination)
            }
            SupportLogger.shared.log("replaced: \(destination.path)", category: "fs")
        } catch {
            SupportLogger.shared.log("replace FAILED: \(destination.path) — \(error.localizedDescription)", category: "fs", level: .error)
            throw error
        }
    }

    private static func temporarySibling(of url: URL) -> URL {
        url.deletingLastPathComponent().appendingPathComponent(temporaryReplacementName(for: url.lastPathComponent))
    }

    public func itemExists(at url: URL) -> Bool {
        Foundation.FileManager.default.fileExists(atPath: url.path())
    }

    public func directorySize(at url: URL) throws -> Int64 {
        let fm = Foundation.FileManager.default
        let enumerator = fm.enumerator(
            at: url,
            includingPropertiesForKeys: [.fileSizeKey, .isDirectoryKey],
            options: [.skipsHiddenFiles]
        )
        var totalSize: Int64 = 0
        while let fileURL = enumerator?.nextObject() as? URL {
            let values = try fileURL.resourceValues(forKeys: [.fileSizeKey, .isDirectoryKey])
            if values.isDirectory != true {
                totalSize += Int64(values.fileSize ?? 0)
            }
        }
        return totalSize
    }
}
