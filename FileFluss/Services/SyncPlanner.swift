import Foundation

/// One side of a sync: either a local directory or a cloud folder on a specific account.
enum SyncEndpoint: Sendable {
    case local(URL)
    case cloud(accountId: UUID, rootPath: String)

    var isCloud: Bool {
        if case .cloud = self { return true }
        return false
    }

    var displayPath: String {
        switch self {
        case .local(let url): return url.path(percentEncoded: false)
        case .cloud(_, let root): return root.isEmpty ? "/" : root
        }
    }
}

enum SyncPlannerError: Error {
    case providerUnavailable
    case sourceNotEnumerable(String)
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
        }
    }

    private func enumerateLocal(root: URL) throws -> [SyncEntry] {
        let fm = Foundation.FileManager.default
        let keys: [URLResourceKey] = [.isDirectoryKey, .fileSizeKey, .totalFileSizeKey, .contentModificationDateKey]
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
            results.append(SyncEntry(relativePath: relative, isDirectory: isDir, size: isDir ? Int64(0) : size, modificationDate: mod))
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

        // Sort source entries so directories are created before their children.
        let sourceSorted = sourceEntries.sorted { $0.relativePath < $1.relativePath }

        for entry in sourceSorted {
            if let existing = destMap[entry.relativePath] {
                // Both sides have it. Skip directories (they already exist).
                if entry.isDirectory { continue }
                switch mode {
                case .mirror:
                    ops.append(.replace(relativePath: entry.relativePath, bytes: entry.size))
                    replaces += 1
                    bytesMoved += entry.size
                case .newer:
                    if entry.modificationDate > existing.modificationDate {
                        ops.append(.replace(relativePath: entry.relativePath, bytes: entry.size))
                        replaces += 1
                        bytesMoved += entry.size
                    }
                case .additive:
                    let unique = uniqueName(for: entry.relativePath, existing: destMap)
                    ops.append(.addRenamed(sourceRelativePath: entry.relativePath, destRelativePath: unique, bytes: entry.size))
                    adds += 1
                    bytesMoved += entry.size
                }
            } else {
                ops.append(.add(relativePath: entry.relativePath, isDirectory: entry.isDirectory, bytes: entry.size))
                if entry.isDirectory { folderAdds += 1 } else { adds += 1 }
                bytesMoved += entry.size
            }
        }

        if mode == .mirror {
            // Delete dest entries not present on source. Sort deepest-first so children go before parents.
            let destSorted = destEntries.sorted { $0.relativePath.count > $1.relativePath.count }
            for entry in destSorted where sourceMap[entry.relativePath] == nil {
                ops.append(.delete(relativePath: entry.relativePath, isDirectory: entry.isDirectory, bytes: entry.size))
                if entry.isDirectory { folderDeletes += 1 } else { deletes += 1 }
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
            totalBytes: bytesMoved
        )
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
