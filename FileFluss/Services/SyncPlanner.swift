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
            return "\(name) (offline)\(path)"
        }
    }
}

enum SyncPlannerError: Error {
    case providerUnavailable
    case sourceNotEnumerable(String)
    /// Compare can read from an offline indexed snapshot, but execution
    /// against one would have no live destination to write to.
    case offlineEndpointNotWritable
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
            .isDirectoryKey, .fileSizeKey, .totalFileSizeKey,
            .contentModificationDateKey, .creationDateKey
        ]
        guard let enumerator = fm.enumerator(at: root, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles]) else {
            throw SyncPlannerError.sourceNotEnumerable(root.path(percentEncoded: false))
        }
        let rootPath = root.path(percentEncoded: false)
        var results: [SyncEntry] = []
        while let itemURL = enumerator.nextObject() as? URL {
            guard let values = try? itemURL.resourceValues(forKeys: Set(keys)) else { continue }
            let itemPath = itemURL.path(percentEncoded: false)
            guard itemPath.hasPrefix(rootPath) else { continue }
            var relative = String(itemPath.dropFirst(rootPath.count))
            while relative.hasPrefix("/") { relative.removeFirst() }
            if relative.isEmpty { continue }
            let isDir = values.isDirectory ?? false
            let size = Int64(values.totalFileSize ?? values.fileSize ?? 0)
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

    func plan(sourceEntries: [SyncEntry],
              destEntries: [SyncEntry],
              mode: SyncMode,
              direction: PlanDirection,
              sourceIsCloud: Bool,
              destIsCloud: Bool) -> SyncPlan {
        var destMap: [String: SyncEntry] = [:]
        destMap.reserveCapacity(destEntries.count)
        for entry in destEntries { destMap[entry.relativePath] = entry }
        var sourceMap: [String: SyncEntry] = [:]
        sourceMap.reserveCapacity(sourceEntries.count)
        for entry in sourceEntries { sourceMap[entry.relativePath] = entry }

        var ops: [SyncOperation] = []
        var adds = 0
        var replaces = 0
        var deletes = 0
        var folderAdds = 0
        var folderDeletes = 0
        var bytesMoved: Int64 = 0
        // Net bytes the destination gains (adds + replaced growth − replaced
        // old size − mirror deletes). Drives the planner's space projection.
        var netDelta: Int64 = 0

        // Sort source entries so directories are created before their children.
        let sourceSorted = sourceEntries.sorted { $0.relativePath < $1.relativePath }

        for entry in sourceSorted {
            if let existing = destMap[entry.relativePath] {
                // Both sides have it. Skip directories (they already exist).
                if entry.isDirectory { continue }
                switch mode {
                case .mirror:
                    // Skip when source and destination look identical. Cloud
                    // providers often quantize mod dates to whole seconds, so
                    // use a small tolerance to avoid re-uploading every file.
                    if Self.filesLookIdentical(entry, existing) { continue }
                    ops.append(.replace(relativePath: entry.relativePath, bytes: entry.size))
                    replaces += 1
                    bytesMoved += entry.size
                    netDelta += entry.size - existing.size
                case .newer:
                    if entry.modificationDate.timeIntervalSince(existing.modificationDate) > Self.modDateTolerance {
                        ops.append(.replace(relativePath: entry.relativePath, bytes: entry.size))
                        replaces += 1
                        bytesMoved += entry.size
                        netDelta += entry.size - existing.size
                    }
                case .additive:
                    let unique = uniqueName(for: entry.relativePath, existing: destMap)
                    ops.append(.addRenamed(sourceRelativePath: entry.relativePath, destRelativePath: unique, bytes: entry.size))
                    adds += 1
                    bytesMoved += entry.size
                    netDelta += entry.size
                }
            } else {
                ops.append(.add(relativePath: entry.relativePath, isDirectory: entry.isDirectory, bytes: entry.size))
                if entry.isDirectory { folderAdds += 1 } else { adds += 1 }
                bytesMoved += entry.size
                netDelta += entry.size
            }
        }

        if mode == .mirror {
            // Delete dest entries not present on source. Sort deepest-first so children go before parents.
            let destSorted = destEntries.sorted { $0.relativePath.count > $1.relativePath.count }
            for entry in destSorted where sourceMap[entry.relativePath] == nil {
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
            netDestinationDelta: netDelta
        )
    }

    /// Tolerance used when comparing modification dates across endpoints.
    /// Cloud APIs typically truncate timestamps to whole seconds, and FAT-like
    /// filesystems have 2-second granularity — so anything within this window
    /// should be treated as equal.
    private static let modDateTolerance: TimeInterval = 2.0

    /// Returns true when the two entries look byte-identical for sync purposes:
    /// same size and mod date within the tolerance above.
    private static func filesLookIdentical(_ a: SyncEntry, _ b: SyncEntry) -> Bool {
        guard a.size == b.size else { return false }
        return abs(a.modificationDate.timeIntervalSince(b.modificationDate)) <= modDateTolerance
    }

    private func uniqueName(for relativePath: String, existing: [String: SyncEntry]) -> String {
        let ns = relativePath as NSString
        let ext = ns.pathExtension
        let base = ns.deletingPathExtension
        var index = 2
        while true {
            let candidate = ext.isEmpty ? "\(base) (\(index))" : "\(base) (\(index)).\(ext)"
            if existing[candidate] == nil { return candidate }
            index += 1
        }
    }
}
