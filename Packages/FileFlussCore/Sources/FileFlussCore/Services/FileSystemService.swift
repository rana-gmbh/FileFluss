import Foundation
import os

/// Local file operations, each run off the main thread.
///
/// Not an actor: an actor does one thing at a time, so a 50 GB copy or a
/// folder-size walk used to make every folder opened in either panel wait
/// for it to finish. This holds no state that needs guarding, so calls run
/// side by side, on a queue because blocking disk work doesn't belong on the
/// cooperative thread pool.
public final class FileSystemService: Sendable {
    public static let shared = FileSystemService()

    private static let queue = DispatchQueue(
        label: "com.rana.FileFluss.fileSystem",
        qos: .userInitiated,
        attributes: .concurrent
    )

    private static func perform<T: Sendable>(_ work: @escaping @Sendable () throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            queue.async { continuation.resume(with: Result(catching: work)) }
        }
    }

    private static let resourceKeys: Set<URLResourceKey> = [
        .nameKey, .isDirectoryKey, .fileSizeKey, .totalFileSizeKey,
        .contentModificationDateKey, .creationDateKey, .isHiddenKey,
        .isSymbolicLinkKey, .contentTypeKey
    ]

    public func listDirectory(at url: URL, showHidden: Bool = false) async throws -> [FileItem] {
        try await Self.perform {
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
    }

    public func createDirectory(at url: URL) async throws {
        try await Self.perform {
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
    }

    public func deleteItem(at url: URL) async throws {
        try await Self.perform {
            do {
                try Foundation.FileManager.default.removeItem(at: url)
                SupportLogger.shared.log("deleteItem: \(url.path)", category: "fs")
            } catch {
                SupportLogger.shared.log("deleteItem FAILED: \(url.path) — \(error.localizedDescription) [\(Self.errorChain(error))]", category: "fs", level: .error)
                throw error
            }
        }
    }

    public func trashItem(at url: URL) async throws {
        try await Self.perform {
            do {
                try Foundation.FileManager.default.trashItem(at: url, resultingItemURL: nil)
                SupportLogger.shared.log("trashItem: \(url.path)", category: "fs")
            } catch {
                SupportLogger.shared.log("trashItem FAILED: \(url.path) — \(error.localizedDescription) [\(Self.errorChain(error))]", category: "fs", level: .error)
                throw error
            }
        }
    }

    /// Whether a `trashItem` failure is because the item's volume has no Trash
    /// (some external/USB and read-only volumes). macOS reports this as
    /// `NSFeatureUnsupportedError`, regardless of the file name — callers then
    /// offer an immediate permanent delete instead, like Finder. Detecting it by
    /// error code (rather than probing for a `.Trashes` folder) is filename- and
    /// state-independent: the earlier probe wrongly reported "Trash supported"
    /// once a volume already had a `.Trashes`, or for names beginning with `~`.
    public static func isNoTrashOnVolumeError(_ error: Error) -> Bool {
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
    public static func isPermissionError(_ error: Error) -> Bool {
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

    /// The domains and codes of an error and everything wrapped inside it,
    /// e.g. "NSCocoaErrorDomain 513 ← NSPOSIXErrorDomain 13". A localized
    /// message alone can't tell a support log reader which call failed or why.
    public static func errorChain(_ error: Error) -> String {
        var parts: [String] = []
        var current: NSError? = error as NSError
        while let ns = current, parts.count < 6 {
            parts.append("\(ns.domain) \(ns.code)")
            current = ns.userInfo[NSUnderlyingErrorKey] as? NSError
        }
        return parts.joined(separator: " ← ")
    }

    /// What a folder involved in a refused Trash looks like to this user.
    public struct FolderFacts: Equatable, Sendable {
        public var path: String
        public var ownerUID: uid_t
        public var isWritable: Bool

        public init(path: String, ownerUID: uid_t, isWritable: Bool) {
            self.path = path
            self.ownerUID = ownerUID
            self.isWritable = isWritable
        }

        /// stat and access only: both work on the Trash folder without Full
        /// Disk Access, unlike listing it. access() takes ACLs into account.
        public static func read(_ url: URL) -> FolderFacts? {
            var info = stat()
            guard stat(url.path, &info) == 0 else { return nil }
            return FolderFacts(path: url.path, ownerUID: info.st_uid,
                               isWritable: access(url.path, W_OK) == 0)
        }
    }

    /// Why macOS refused to move an item to the Trash, as far as it can be
    /// told from outside. Full Disk Access was the only answer offered
    /// before, but a process without it trashes files normally; a customer
    /// who had granted it, and could delete the same file outright, was
    /// still sent to that setting.
    public enum TrashRefusal: Equatable, Sendable {
        /// The Trash folder itself can't take the item: it belongs to
        /// someone else, or its permissions shut this user out. Finder
        /// fails the same way.
        case trashFolderUnwritable(trashPath: String, ownerUID: uid_t)
        /// The folder the item is in can't be changed by this user, so it
        /// can neither be trashed nor deleted.
        case folderNotWritable(folderPath: String)
        /// The item is in iCloud Drive, which is where Desktop and Documents
        /// live when they're synced with iCloud. It goes to iCloud Drive's
        /// own Trash, and macOS didn't let FileFluss use it. Both customer
        /// reports of a refused Trash were about files in those two folders.
        case iCloudDrive
        /// Nothing visible explains it.
        case unknown
    }

    public static func classifyTrashRefusal(trash: FolderFacts?, parent: FolderFacts?,
                                            userID: uid_t, inICloudDrive: Bool = false) -> TrashRefusal {
        // An iCloud Drive item doesn't go to the user's own Trash folder, so
        // what that folder looks like says nothing about it.
        if inICloudDrive {
            if let parent, !parent.isWritable {
                return .folderNotWritable(folderPath: parent.path)
            }
            return .iCloudDrive
        }
        if let trash, trash.ownerUID != userID || !trash.isWritable {
            return .trashFolderUnwritable(trashPath: trash.path, ownerUID: trash.ownerUID)
        }
        if let parent, !parent.isWritable {
            return .folderNotWritable(folderPath: parent.path)
        }
        return .unknown
    }

    /// Looks at the Trash this item would go to and at the folder it's in.
    public func diagnoseTrashRefusal(for url: URL) async -> TrashRefusal {
        (try? await Self.perform {
            var lookup = "ok"
            var trashURL: URL?
            do {
                trashURL = try Foundation.FileManager.default.url(
                    for: .trashDirectory, in: .userDomainMask, appropriateFor: url, create: false)
            } catch {
                lookup = Self.errorChain(error)
            }
            let trash = trashURL.flatMap(FolderFacts.read)
            let parent = FolderFacts.read(url.deletingLastPathComponent())
            let iCloud = Self.isInICloudDrive(url)
            let verdict = Self.classifyTrashRefusal(trash: trash, parent: parent, userID: getuid(), inICloudDrive: iCloud)
            SupportLogger.shared.log(
                "trash refusal: trash=\(Self.describe(trash)) (lookup \(lookup)) parent=\(Self.describe(parent)) "
                    + "icloud=\(iCloud ? "yes" : "no") uid=\(getuid()) → \(verdict)",
                category: "fs", level: .error)
            return verdict
        }) ?? .unknown
    }

    /// Whether an item lives in iCloud Drive. With "Desktop & Documents
    /// Folders" turned on in iCloud settings, the Desktop and Documents
    /// folders are part of it although their paths don't say so; iCloud
    /// marks them, and everything else it manages, with its file provider
    /// domain.
    public static func isInICloudDrive(
        _ url: URL,
        home: String = NSHomeDirectory(),
        fileProviderDomain: (String) -> String? = FileSystemService.fileProviderDomain(ofPath:)
    ) -> Bool {
        let path = url.standardizedFileURL.path
        if path.hasPrefix(home + "/Library/Mobile Documents/") { return true }
        var current = path
        while current.hasPrefix(home + "/"), current != home {
            if fileProviderDomain(current)?.hasPrefix("com.apple.CloudDocs") == true { return true }
            current = (current as NSString).deletingLastPathComponent
        }
        return false
    }

    /// The file provider domain macOS records on an item it syncs, e.g.
    /// `com.apple.CloudDocs.iCloudDriveFileProvider/…` for iCloud Drive.
    public static func fileProviderDomain(ofPath path: String) -> String? {
        extendedAttribute("com.apple.file-provider-domain-id", ofPath: path)
    }

    static func extendedAttribute(_ name: String, ofPath path: String) -> String? {
        let size = getxattr(path, name, nil, 0, 0, XATTR_NOFOLLOW)
        guard size > 0 else { return nil }
        var buffer = [UInt8](repeating: 0, count: size)
        let read = getxattr(path, name, &buffer, size, 0, XATTR_NOFOLLOW)
        guard read > 0 else { return nil }
        return String(decoding: buffer.prefix(read), as: UTF8.self)
    }

    /// Reads the iCloud Drive folder once. If macOS hasn't asked yet
    /// whether FileFluss may use iCloud Drive, this is what makes it ask,
    /// in the moment the user wants something from it. True when FileFluss
    /// can read it.
    public func openICloudDrive() async -> Bool {
        (try? await Self.perform {
            let folder = NSHomeDirectory() + "/Library/Mobile Documents"
            let readable = (try? Foundation.FileManager.default.contentsOfDirectory(atPath: folder)) != nil
            SupportLogger.shared.log("iCloud Drive readable: \(readable ? "yes" : "no")", category: "fs")
            return readable
        }) ?? false
    }

    private static func describe(_ facts: FolderFacts?) -> String {
        guard let facts else { return "unreadable" }
        return "\(facts.path) owner \(facts.ownerUID) \(facts.isWritable ? "writable" : "not writable")"
    }

    /// A Terminal command that gives the Trash folder back to this user.
    /// Asks for an administrator password, which is why FileFluss hands it
    /// over rather than running it.
    public static func trashRepairCommand(trashPath: String) -> String {
        let quoted = "'" + trashPath.replacingOccurrences(of: "'", with: "'\\''") + "'"
        return "sudo chown \"$(id -u)\" \(quoted) && sudo chmod u+rwx \(quoted)"
    }

    public func moveItem(from source: URL, to destination: URL) async throws {
        try await Self.perform {
            do {
                try Foundation.FileManager.default.moveItem(at: source, to: destination)
                SupportLogger.shared.log("moveItem: \(source.path) → \(destination.path)", category: "fs")
            } catch {
                SupportLogger.shared.log("moveItem FAILED: \(source.path) → \(destination.path) — \(error.localizedDescription)", category: "fs", level: .error)
                throw error
            }
        }
    }

    public func copyItem(from source: URL, to destination: URL) async throws {
        try await Self.perform {
            do {
                try Foundation.FileManager.default.copyItem(at: source, to: destination)
                SupportLogger.shared.log("copyItem: \(source.path) → \(destination.path)", category: "fs")
            } catch {
                SupportLogger.shared.log("copyItem FAILED: \(source.path) → \(destination.path) — \(error.localizedDescription)", category: "fs", level: .error)
                throw error
            }
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
    public func replaceItem(at destination: URL, withMoved source: URL) async throws {
        try await Self.perform {
            let temporary = Self.temporarySibling(of: destination)
            let fm = Foundation.FileManager.default
            // A move across volumes is a copy, which is the slow part that can
            // fail. Doing it first leaves the swap as a single rename.
            try fm.moveItem(at: source, to: temporary)
            do {
                try Self.install(temporary, at: destination)
            } catch {
                try? fm.moveItem(at: temporary, to: source)
                throw error
            }
        }
    }

    /// Replaces `destination` with a copy of `source`. As above: the old item
    /// stays until the copy is complete.
    public func replaceItem(at destination: URL, withCopyOf source: URL) async throws {
        try await Self.perform {
            let temporary = Self.temporarySibling(of: destination)
            let fm = Foundation.FileManager.default
            do {
                try fm.copyItem(at: source, to: temporary)
                try Self.install(temporary, at: destination)
            } catch {
                try? fm.removeItem(at: temporary)
                throw error
            }
        }
    }

    /// Moves a finished download to where it belongs. The new file goes beside
    /// the old one first, because a move to another volume is really a copy
    /// and can fail part way, and removing the old file up front would then
    /// lose both. A crash can't leave a short file under the real name either.
    public static func placeDownload(from temporary: URL, at destination: URL) throws {
        let fm = Foundation.FileManager.default
        let sibling = temporarySibling(of: destination)
        do {
            try fm.moveItem(at: temporary, to: sibling)
            try install(sibling, at: destination)
        } catch {
            try? fm.removeItem(at: sibling)
            throw error
        }
    }

    /// Swaps a finished copy into place. `temporary` has to be on the same
    /// volume as `destination`, which is what makes this a rename.
    public func installReplacement(_ temporary: URL, at destination: URL) async throws {
        try await Self.perform { try Self.install(temporary, at: destination) }
    }

    private static func install(_ temporary: URL, at destination: URL) throws {
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

    public func itemExists(at url: URL) async -> Bool {
        // `path`, not `path()`: the latter is percent-encoded, so a folder
        // like "Backup Übersicht" read as missing and Mirror's deletes there
        // did nothing while reporting success.
        (try? await Self.perform { Foundation.FileManager.default.fileExists(atPath: url.path) }) ?? false
    }

    /// Adds up a folder. Stops when the calling task is cancelled, so a size
    /// nobody is waiting for any more doesn't keep walking a huge tree.
    public func directorySize(at url: URL) async throws -> Int64 {
        let cancelled = OSAllocatedUnfairLock(initialState: false)
        return try await withTaskCancellationHandler {
            try await Self.perform {
                let enumerator = Foundation.FileManager.default.enumerator(
                    at: url,
                    includingPropertiesForKeys: [.fileSizeKey, .isDirectoryKey],
                    options: [.skipsHiddenFiles]
                )
                var totalSize: Int64 = 0
                while let fileURL = enumerator?.nextObject() as? URL {
                    if cancelled.withLock({ $0 }) { throw CancellationError() }
                    let values = try fileURL.resourceValues(forKeys: [.fileSizeKey, .isDirectoryKey])
                    if values.isDirectory != true {
                        totalSize += Int64(values.fileSize ?? 0)
                    }
                }
                return totalSize
            }
        } onCancel: {
            cancelled.withLock { $0 = true }
        }
    }
}
