import Foundation
import FileFlussCore

/// One side of a sync: either a local directory or a cloud folder on a specific account.
enum SyncEndpoint: Sendable {
    case local(URL)
    case cloud(accountId: UUID, rootPath: String)
    /// Read-only snapshot from the offline search index. Used by Compare
    /// when one side is a drive or cloud account that's currently
    /// disconnected — we enumerate from the persisted index rather than
    /// hitting the unreachable source. Sync execution against this kind
    /// is not supported (no live destination to write to).
    case offlineIndexed(sourceId: String, kind: OfflineKind, rootPath: String, displayName: String)

    enum OfflineKind: Sendable {
        /// Entries live in `indexed_files` (drive snapshot).
        case drive
        /// Entries live in `cloud_files` (cloud-account snapshot).
        case cloud(accountId: UUID)
    }

    var isCloud: Bool {
        if case .cloud = self { return true }
        return false
    }

    var isOffline: Bool {
        if case .offlineIndexed = self { return true }
        return false
    }

    var displayPath: String {
        switch self {
        case .local(let url): return url.path(percentEncoded: false)
        case .cloud(_, let root): return root.isEmpty ? "/" : root
        case .offlineIndexed(_, _, let root, let name):
            let path = root.isEmpty || root == "/" ? "" : root
            return L10n.format("%@ (offline)", name) + path
        }
    }
}

enum SyncPlannerError: Error {
    case providerUnavailable
    case sourceNotEnumerable(String)
    /// Compare can read from an offline indexed snapshot, but execution
    /// against one would have no live destination to write to.
    case offlineEndpointNotWritable
    /// A listing named a path that climbs out of the folder being synced.
    /// Stops the plan: skipping the entry would let Mirror delete it instead.
    case unsafeRemotePath(String)
    /// The destination can be read from but not written to, like a GoPro.
    case destinationReadOnly(String)
}

extension SyncPlannerError: LocalizedError {
    var errorDescription: String? {
        switch self {
        case .providerUnavailable:
            return L10n.text("Cloud account not connected")
        case .sourceNotEnumerable(let path):
            return L10n.format("FileFluss couldn't read “%@”, so it can't tell what needs syncing.", path)
        case .offlineEndpointNotWritable:
            return L10n.text("An offline source can't be changed.")
        case .unsafeRemotePath(let path):
            return L10n.format("The server listed an unsafe path (“%@”), so the sync was stopped.", path)
        case .destinationReadOnly(let name):
            return L10n.format("%@ can't be written to, so it can't be a sync destination.", name)
        }
    }
}

/// The first folder a scan couldn't read. Written from the enumerator's error
/// callback and read once the enumeration has finished.
private final class ScanFailure: @unchecked Sendable {
    private let lock = NSLock()
    private var url: URL?

    func record(_ failed: URL) {
        lock.lock()
        defer { lock.unlock() }
        if url == nil { url = failed }
    }

    var firstFailure: URL? {
        lock.lock()
        defer { lock.unlock() }
        return url
    }
}

/// Builds sync plans by enumerating both endpoints and diffing them according to the chosen mode.
actor SyncPlanner {
    // MARK: - Enumeration

    func enumerate(_ endpoint: SyncEndpoint) async throws -> [SyncEntry] {
        switch endpoint {
        case .local(let url):
            return try enumerateLocal(root: url)
        case .cloud(let accountId, let rootPath):
            guard let provider = await SyncEngine.shared.provider(for: accountId) else {
                throw SyncPlannerError.providerUnavailable
            }
            return try await enumerateCloud(provider: provider, root: rootPath)
        case .offlineIndexed(let sourceId, let kind, let rootPath, _):
            return await enumerateOffline(sourceId: sourceId, kind: kind, rootPath: rootPath)
        }
    }

    /// Enumerates a sub-tree from the offline search index. Builds the same
    /// `SyncEntry` rows the live enumerators would produce, but reads from
    /// SQLite — so Compare works even when the drive/account is gone.
    private func enumerateOffline(sourceId: String, kind: SyncEndpoint.OfflineKind, rootPath: String) async -> [SyncEntry] {
        switch kind {
        case .drive:
            // Normalise root so "" / "/" both behave as the source root.
            let normRoot = rootPath.isEmpty ? "/" : rootPath
            let rows = await SearchIndex.shared.indexedFilesUnder(sourceId: sourceId, rootPath: normRoot)
            let rootPrefix = normRoot == "/" ? "/" : (normRoot.hasSuffix("/") ? normRoot : normRoot + "/")
            var entries: [SyncEntry] = []
            for row in rows {
                let abs = row.path
                guard abs.hasPrefix(rootPrefix) || (normRoot == "/" && abs.hasPrefix("/")) else { continue }
                var relative = String(abs.dropFirst(rootPrefix.count))
                if normRoot == "/" && relative.isEmpty {
                    relative = String(abs.drop(while: { $0 == "/" }))
                }
                while relative.hasPrefix("/") { relative.removeFirst() }
                if relative.isEmpty { continue }
                entries.append(SyncEntry(
                    relativePath: relative,
                    isDirectory: row.isDirectory,
                    size: row.isDirectory ? 0 : row.size,
                    modificationDate: row.modificationDate
                ))
            }
            return entries
        case .cloud(let accountId):
            let normRoot = rootPath.isEmpty ? "/" : rootPath
            let rows = await SearchIndex.shared.cloudFilesUnder(accountId: accountId, rootPath: normRoot)
            let rootPrefix = normRoot == "/" ? "/" : (normRoot.hasSuffix("/") ? normRoot : normRoot + "/")
            var entries: [SyncEntry] = []
            for row in rows {
                let abs = row.path
                guard abs.hasPrefix(rootPrefix) || (normRoot == "/" && abs.hasPrefix("/")) else { continue }
                var relative = String(abs.dropFirst(rootPrefix.count))
                if normRoot == "/" && relative.isEmpty {
                    relative = String(abs.drop(while: { $0 == "/" }))
                }
                while relative.hasPrefix("/") { relative.removeFirst() }
                if relative.isEmpty { continue }
                entries.append(SyncEntry(
                    relativePath: relative,
                    isDirectory: row.isDirectory,
                    size: row.isDirectory ? 0 : row.size,
                    modificationDate: row.modificationDate
                ))
            }
            return entries
        }
    }

    private func enumerateLocal(root: URL) throws -> [SyncEntry] {
        let fm = Foundation.FileManager.default
        let keys: [URLResourceKey] = [
            .isDirectoryKey, .fileSizeKey,
            .contentModificationDateKey, .creationDateKey
        ]
        // A folder that can't be read must stop the scan. Left out of the
        // results it reads as "not there", and Mirror then deletes the
        // destination's copy, or everything on an ejected drive.
        var rootIsDirectory: ObjCBool = false
        guard fm.fileExists(atPath: root.path, isDirectory: &rootIsDirectory), rootIsDirectory.boolValue else {
            throw SyncPlannerError.sourceNotEnumerable(root.path(percentEncoded: false))
        }
        let failure = ScanFailure()
        // The enumerator won't step into a root that is itself a symlink.
        guard let enumerator = fm.enumerator(
            at: root.resolvingSymlinksInPath(),
            includingPropertiesForKeys: keys,
            options: [.skipsHiddenFiles],
            errorHandler: { url, _ in
                failure.record(url)
                return false
            }
        ) else {
            throw SyncPlannerError.sourceNotEnumerable(root.path(percentEncoded: false))
        }
        var results: [SyncEntry] = []
        while let itemURL = enumerator.nextObject() as? URL {
            guard let values = try? itemURL.resourceValues(forKeys: Set(keys)) else {
                throw SyncPlannerError.sourceNotEnumerable(itemURL.path(percentEncoded: false))
            }
            // By depth, not by comparing against the root's path: the
            // enumerator may spell a root reached through a symlink
            // differently (/tmp is really /private/tmp), and an entry that
            // failed that comparison used to vanish from the scan.
            let relative = itemURL.pathComponents.suffix(enumerator.level).joined(separator: "/")
            if relative.isEmpty { continue }
            let isDir = values.isDirectory ?? false
            // The data fork alone: that's what a cloud stores and reports.
            // With the resource fork counted, a file with a custom icon never
            // looked identical and every Mirror uploaded it again.
            let size = Int64(values.fileSize ?? 0)
            let mod = values.contentModificationDate ?? .distantPast
            let created = values.creationDate
            results.append(SyncEntry(
                relativePath: relative,
                isDirectory: isDir,
                size: isDir ? Int64(0) : size,
                modificationDate: mod,
                creationDate: created
            ))
        }
        if let failed = failure.firstFailure {
            throw SyncPlannerError.sourceNotEnumerable(failed.path(percentEncoded: false))
        }
        return results
    }

    private func enumerateCloud(provider: any CloudProvider, root: String) async throws -> [SyncEntry] {
        var results: [SyncEntry] = []
        var queue: [String] = [root]
        while !queue.isEmpty {
            let dir = queue.removeFirst()
            let items = try await provider.listDirectory(at: dir)
            for item in items {
                let relative = relativeCloudPath(item.path, underRoot: root)
                guard !relative.isEmpty else { continue }
                // The name comes from the server. A hostile one could list
                // `../../Library/LaunchAgents/x.plist`.
                guard !SafeLocalPath.hasTraversal(relative) else {
                    throw SyncPlannerError.unsafeRemotePath(item.path)
                }
                results.append(SyncEntry(
                    relativePath: relative,
                    isDirectory: item.isDirectory,
                    size: item.isDirectory ? 0 : item.size,
                    modificationDate: item.modificationDate
                ))
                if item.isDirectory { queue.append(item.path) }
            }
        }
        return results
    }

    private func relativeCloudPath(_ path: String, underRoot root: String) -> String {
        let normalisedRoot = root.hasSuffix("/") ? root : root + "/"
        if path.hasPrefix(normalisedRoot) {
            return String(path.dropFirst(normalisedRoot.count))
        }
        if path == root { return "" }
        return path.hasPrefix("/") ? String(path.dropFirst()) : path
    }

    // MARK: - Diff / planning

    /// Whether names that differ only in case are one name at `endpoint`.
    /// A local folder asks its volume (APFS folds case unless formatted
    /// otherwise). A cloud folds case unless its service is known not to:
    /// treating a case-sensitive server as folding costs at most a "(2)"
    /// that wasn't needed, the other way round Mirror writes README.md over
    /// readme.md and then deletes readme.md, which is the file it wrote.
    func destinationIgnoresCase(_ endpoint: SyncEndpoint) async -> Bool {
        switch endpoint {
        case .local(let url):
            let values = try? url.resourceValues(forKeys: [.volumeSupportsCaseSensitiveNamesKey])
            return !(values?.volumeSupportsCaseSensitiveNames ?? false)
        case .cloud(let accountId, _):
            guard let provider = await SyncEngine.shared.provider(for: accountId) else { return true }
            return Self.namesIgnoreCase(on: provider.providerType)
        case .offlineIndexed:
            return true
        }
    }

    /// Services whose names are case-sensitive by design: object keys
    /// (S3 and its kin), Google Drive's freely duplicated names, git trees.
    /// Everything else, a NAS or an SFTP server among them, may sit on a
    /// disk that folds case, so it's taken to.
    static func namesIgnoreCase(on type: CloudProviderType) -> Bool {
        switch type {
        case .s3, .s3Compatible, .synologyC2, .googleDrive, .googleDrivePicker, .github:
            return false
        default:
            return true
        }
    }

    func plan(sourceEntries: [SyncEntry],
              destEntries: [SyncEntry],
              mode: SyncMode,
              direction: PlanDirection,
              sourceIsCloud: Bool,
              destIsCloud: Bool,
              destinationIgnoresCase: Bool = true,
              dateTolerance: TimeInterval = SyncPlanner.defaultDateTolerance) -> SyncPlan {
        // A local scan skips hidden files, so without this the cloud's `.git`
        // and `.env` look like extras and Mirror deletes them. Scanning them
        // locally instead would upload secrets nobody chose to upload, so
        // hidden files stay out of any sync that touches this Mac.
        let touchesThisMac = !sourceIsCloud || !destIsCloud
        let sourceEntries = touchesThisMac ? sourceEntries.filter { !$0.isHidden } : sourceEntries
        let destEntries = touchesThisMac ? destEntries.filter { !$0.isHidden } : destEntries

        // Names on the destination side are matched the way the destination
        // matches them. String equality already ignores Unicode form (a
        // Mac's NFD "Café" is a server's NFC one); case is folded as well
        // where the destination folds it. Matching exactly made Mirror plan
        // "add README.md" beside "delete readme.md" on APFS, and the delete
        // then removed the file the add had just written.
        let key: (String) -> String = destinationIgnoresCase ? { $0.lowercased() } : { $0 }

        var destMap: [String: SyncEntry] = [:]
        destMap.reserveCapacity(destEntries.count)
        for entry in destEntries { destMap[key(entry.relativePath)] = entry }

        // Directories before their children, and a fixed order that decides
        // which of two colliding names keeps the name. Spellings equal as
        // strings (NFC beside NFD) are ordered by their bytes.
        let sourceSorted = sourceEntries.sorted {
            $0.relativePath == $1.relativePath
                ? $0.relativePath.utf8.lexicographicallyPrecedes($1.relativePath.utf8)
                : $0.relativePath < $1.relativePath
        }

        // Where each source item lands, by its index in `sourceSorted`. Two
        // source names can be one name at the destination: README.md and
        // Readme.md from a case-sensitive server onto APFS, or NFC and NFD
        // spellings of Café. Only one can have it; writing both there made
        // the second fail on every run, or silently overwrite the first. So
        // the first keeps the name and each later one gets "name (2)",
        // chosen from the source's names alone so every run picks the same
        // one and finds the copy already there. A renamed folder takes its
        // contents along.
        var targetOf: [String] = []
        targetOf.reserveCapacity(sourceSorted.count)
        // Keyed by the exact bytes: as strings, the NFC and NFD folders
        // would share one entry and the second's children would follow the
        // first.
        var targetByPath: [Data: String] = [:]
        var targets: Set<String> = []
        var collisions: Set<Int> = []
        var naturalKeys: Set<String>?
        for (index, entry) in sourceSorted.enumerated() {
            let path = entry.relativePath
            let target: String
            if let slash = path.lastIndex(of: "/") {
                let parent = String(path[..<slash])
                let parentTarget = targetByPath[Data(parent.utf8)] ?? parent
                target = parentTarget + "/" + path[path.index(after: slash)...]
            } else {
                target = path
            }
            var landed = target
            if !targets.insert(key(target)).inserted {
                if naturalKeys == nil { naturalKeys = Set(sourceSorted.map { key($0.relativePath) }) }
                let taken = naturalKeys!.union(targets)
                landed = uniqueName(for: target) { taken.contains(key($0)) }
                targets.insert(key(landed))
                collisions.insert(index)
            }
            targetOf.append(landed)
            targetByPath[Data(path.utf8)] = landed
        }

        // Where a target lands: folders the destination already has keep its
        // spelling (its case and Unicode form, or a file would go into a
        // folder that isn't there, or beside an NFC copy as an NFD one), new
        // components are spelled as the source spells them.
        func destinationPath(for target: String) -> String {
            var landed = ""
            var prefix = ""
            for component in target.split(separator: "/", omittingEmptySubsequences: false) {
                prefix = prefix.isEmpty ? String(component) : prefix + "/" + component
                if let match = destMap[key(prefix)] {
                    landed = match.relativePath
                } else {
                    landed = landed.isEmpty ? String(component) : landed + "/" + component
                }
            }
            return landed
        }

        /// nil when `path` is byte for byte the source's own path.
        func differing(_ path: String, from source: String) -> String? {
            path.utf8.elementsEqual(source.utf8) ? nil : path
        }

        var ops: [SyncOperation] = []
        var adds = 0
        var replaces = 0
        var deletes = 0
        var folderAdds = 0
        var folderDeletes = 0
        var typeClashes = 0
        var folderTypeClashes = 0
        var bytesMoved: Int64 = 0
        // Net bytes the destination gains (adds + replaced growth − replaced
        // old size − mirror deletes). Drives the planner's space projection.
        var netDelta: Int64 = 0
        // Names a renamed Add-only copy must not take: anything on either
        // side, and earlier renames.
        var additiveTaken: Set<String>?
        // Source folders that can't be created because a file of that name
        // is at the destination, in a mode that never deletes. Neither they
        // nor anything in them is planned: their contents would only fail,
        // on every run.
        var blockedFolders: Set<String> = []

        for (index, entry) in sourceSorted.enumerated() {
            if !blockedFolders.isEmpty, Self.isInside(entry.relativePath, anyOf: blockedFolders) { continue }
            let target = targetOf[index]
            let renamed = collisions.contains(index)
            if let existing = destMap[key(target)] {
                if entry.isDirectory {
                    // A folder that's there already; its contents are planned
                    // one by one. A file of that name used to be skipped as
                    // "exists" too, and everything in the folder then failed
                    // beneath it on every run.
                    if !existing.isDirectory {
                        if mode == .mirror {
                            // The file goes first, then the folder is created
                            // in its place, before anything that goes into it.
                            ops.append(.delete(relativePath: existing.relativePath, isDirectory: false, bytes: existing.size))
                            deletes += 1
                            netDelta -= existing.size
                            ops.append(.add(relativePath: entry.relativePath, isDirectory: true, bytes: 0,
                                            destRelativePath: differing(existing.relativePath, from: entry.relativePath)))
                            folderAdds += 1
                        } else {
                            blockedFolders.insert(entry.relativePath)
                            folderTypeClashes += 1
                        }
                    }
                    continue
                }
                // A folded match is the same item: it's replaced (or left) in
                // place, under the destination's spelling.
                let destPath = differing(existing.relativePath, from: entry.relativePath)
                switch mode {
                case .mirror:
                    // Skip when source and destination look identical. Cloud
                    // providers often quantize mod dates to whole seconds, so
                    // use a small tolerance to avoid re-uploading every file.
                    if Self.filesLookIdentical(entry, existing, tolerance: dateTolerance) { continue }
                    ops.append(.replace(relativePath: entry.relativePath, bytes: entry.size, destRelativePath: destPath))
                    replaces += 1
                    bytesMoved += entry.size
                    netDelta += entry.size - existing.size
                case .newer:
                    // Replacing a folder with a file would delete the folder,
                    // and this mode promises never to delete.
                    if existing.isDirectory {
                        typeClashes += 1
                        continue
                    }
                    if entry.modificationDate.timeIntervalSince(existing.modificationDate) > dateTolerance {
                        ops.append(.replace(relativePath: entry.relativePath, bytes: entry.size, destRelativePath: destPath))
                        replaces += 1
                        bytesMoved += entry.size
                        netDelta += entry.size - existing.size
                    }
                case .additive:
                    if additiveTaken == nil { additiveTaken = Set(destMap.keys).union(targets) }
                    let unique = uniqueName(for: destinationPath(for: target)) { additiveTaken!.contains(key($0)) }
                    additiveTaken!.insert(key(unique))
                    ops.append(.addRenamed(sourceRelativePath: entry.relativePath, destRelativePath: unique, bytes: entry.size))
                    adds += 1
                    bytesMoved += entry.size
                    netDelta += entry.size
                }
            } else {
                // A renamed file is an add under another name; a renamed
                // folder is created at its new path and its contents follow.
                let destPath = destinationPath(for: target)
                if renamed && !entry.isDirectory {
                    ops.append(.addRenamed(sourceRelativePath: entry.relativePath, destRelativePath: destPath, bytes: entry.size))
                } else {
                    ops.append(.add(relativePath: entry.relativePath, isDirectory: entry.isDirectory, bytes: entry.size,
                                    destRelativePath: differing(destPath, from: entry.relativePath)))
                }
                if entry.isDirectory { folderAdds += 1 } else { adds += 1 }
                bytesMoved += entry.size
                netDelta += entry.size
            }
        }

        if mode == .mirror {
            // Delete dest entries no source item lands on; a renamed copy
            // counts as landed. Deepest first so children go before parents.
            let destSorted = destEntries.sorted { $0.relativePath.count > $1.relativePath.count }
            for entry in destSorted where !targets.contains(key(entry.relativePath)) {
                ops.append(.delete(relativePath: entry.relativePath, isDirectory: entry.isDirectory, bytes: entry.size))
                if entry.isDirectory { folderDeletes += 1 } else { deletes += 1 }
                netDelta -= entry.size
            }
        }

        let download: Int64 = sourceIsCloud ? bytesMoved : 0
        let upload: Int64 = destIsCloud ? bytesMoved : 0

        return SyncPlan(
            mode: mode,
            direction: direction,
            operations: ops,
            filesToAdd: adds,
            filesToReplace: replaces,
            filesToDelete: deletes,
            foldersToAdd: folderAdds,
            foldersToDelete: folderDeletes,
            downloadBytes: download,
            uploadBytes: upload,
            totalBytes: bytesMoved,
            netDestinationDelta: netDelta,
            sourceEntryCount: sourceEntries.count,
            destinationEntryCount: destEntries.count,
            sourceModificationDates: sourceEntries.reduce(into: [:]) { dates, entry in
                if !entry.isDirectory, entry.modificationDate != .distantPast {
                    dates[entry.relativePath] = entry.modificationDate
                }
            },
            typeClashes: typeClashes,
            folderTypeClashes: folderTypeClashes
        )
    }

    /// Whether `path` lies inside one of `folders`.
    private static func isInside(_ path: String, anyOf folders: Set<String>) -> Bool {
        var current = Substring(path)
        while let slash = current.lastIndex(of: "/") {
            current = current[..<slash]
            if folders.contains(String(current)) { return true }
        }
        return false
    }

    /// Tolerance used when comparing modification dates across endpoints.
    /// Cloud APIs typically truncate timestamps to whole seconds, and FAT-like
    /// filesystems have 2-second granularity — so anything within this window
    /// should be treated as equal.
    static let defaultDateTolerance: TimeInterval = 2.0
    /// SFTP and FTP listings come from `ls -l`, which shows minutes at best.
    static let minuteDateTolerance: TimeInterval = 60.0

    /// Returns true when the two entries look byte-identical for sync purposes:
    /// same size and mod date within the tolerance.
    private static func filesLookIdentical(_ a: SyncEntry, _ b: SyncEntry, tolerance: TimeInterval) -> Bool {
        guard a.size == b.size else { return false }
        return abs(a.modificationDate.timeIntervalSince(b.modificationDate)) <= tolerance
    }

    /// "sub/name (2).ext": the first "name (n)" in the same folder that
    /// isn't `taken`.
    private func uniqueName(for relativePath: String, taken: (String) -> Bool) -> String {
        let ns = relativePath as NSString
        let ext = ns.pathExtension
        let base = ns.deletingPathExtension
        var index = 2
        while true {
            let candidate = ext.isEmpty ? "\(base) (\(index))" : "\(base) (\(index)).\(ext)"
            if !taken(candidate) { return candidate }
            index += 1
        }
    }
}
