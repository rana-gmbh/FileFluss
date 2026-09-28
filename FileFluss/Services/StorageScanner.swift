import Foundation
import FileFlussCore
import os

private let storageScanLog = Logger(subsystem: "com.rana.FileFluss", category: "storageScan")

/// What a storage analysis is run against: a whole cloud account, a folder
/// inside one, a local folder, or an indexed drive.
enum StorageScanSource: Hashable, Identifiable {
    case cloud(accountId: UUID, rootPath: String, displayName: String)
    case local(url: URL)

    var id: String {
        switch self {
        case .cloud(let accountId, let rootPath, _): return "cloud:\(accountId.uuidString):\(rootPath)"
        case .local(let url): return "local:\(url.path)"
        }
    }

    var title: String {
        switch self {
        case .cloud(_, let rootPath, let displayName):
            return rootPath == "/" ? displayName : "\(displayName) — \(rootPath)"
        case .local(let url):
            return url.path
        }
    }

    var rootName: String {
        switch self {
        case .cloud(_, let rootPath, let displayName):
            return rootPath == "/" ? displayName : (rootPath as NSString).lastPathComponent
        case .local(let url):
            return url.lastPathComponent.isEmpty ? url.path : url.lastPathComponent
        }
    }
}

/// Walks a source and reports which folders and files take up the space.
///
/// Two routes to the same answer:
///   * An account that has been indexed for offline search is already in the
///     local database, sizes and all — that produces a result instantly and
///     without touching the network.
///   * Anything else is walked folder by folder, streaming partial results as
///     they arrive. A cloud walk also feeds the offline index on the way, so
///     the next analysis of that account is the instant kind and offline
///     search gets better as a side effect.
@Observable @MainActor
final class StorageScanner {
    private(set) var isScanning = false
    private(set) var report: StorageReport?
    private(set) var filesSeen = 0
    private(set) var bytesSeen: Int64 = 0
    private(set) var currentPath = ""
    private(set) var errorMessage: String?
    /// Source the current `report` belongs to, so the window can tell when
    /// the user has picked something else.
    private(set) var scannedSource: StorageScanSource?

    private var task: Task<Void, Never>?

    func cancel() {
        task?.cancel()
    }

    /// Runs an analysis. `forceRescan` skips the index and walks for real,
    /// which is what the "Rescan" button needs.
    func scan(_ source: StorageScanSource, forceRescan: Bool = false) {
        task?.cancel()
        isScanning = true
        errorMessage = nil
        report = nil
        filesSeen = 0
        bytesSeen = 0
        currentPath = ""
        scannedSource = source

        task = Task { [weak self] in
            guard let self else { return }
            switch source {
            case .cloud(let accountId, let rootPath, let displayName):
                await self.scanCloud(
                    accountId: accountId,
                    rootPath: rootPath,
                    displayName: displayName,
                    forceRescan: forceRescan
                )
            case .local(let url):
                await self.scanLocal(url: url)
            }
            self.isScanning = false
        }
    }

    // MARK: - Cloud

    private func scanCloud(
        accountId: UUID,
        rootPath: String,
        displayName: String,
        forceRescan: Bool
    ) async {
        if !forceRescan, let indexed = await indexedReport(
            accountId: accountId,
            rootPath: rootPath,
            displayName: displayName
        ) {
            report = indexed
            filesSeen = indexed.fileCount
            bytesSeen = indexed.totalBytes
            return
        }

        guard let provider = await SyncEngine.shared.provider(for: accountId) else {
            errorMessage = L10n.text("This account isn't connected.")
            return
        }

        var entries: [StorageTreeBuilder.Entry] = []
        // Index cursor rather than removeFirst(): shifting a 100k-entry
        // array on every folder is O(n) each time.
        var pending: [String] = [rootPath]
        var nextIndex = 0
        var processed = 0
        var lastReport = 0
        var lastPartialReport = Date.distantPast
        var cancelled = false

        while nextIndex < pending.count {
            if Task.isCancelled { cancelled = true; break }
            let path = pending[nextIndex]
            nextIndex += 1
            let items: [CloudFileItem]
            do {
                items = try await provider.listDirectory(at: path)
            } catch {
                // A single unreadable folder must not abandon the whole
                // analysis — note it and keep going.
                storageScanLog.info("[Scan] Skipped \(path, privacy: .public): \(error.localizedDescription, privacy: .public)")
                continue
            }
            guard !items.isEmpty else { continue }

            // Feed the offline index while we're here: the walk costs the
            // same, and the next analysis of this account is then instant.
            await SearchIndex.shared.upsertItems(items, accountId: accountId)

            for item in items {
                entries.append(StorageTreeBuilder.Entry(
                    path: item.path,
                    name: item.name,
                    isDirectory: item.isDirectory,
                    size: item.size,
                    modificationDate: item.modificationDate
                ))
                if item.isDirectory {
                    pending.append(item.path)
                } else {
                    processed += 1
                    bytesSeen += item.size
                }
            }

            // Rebuilding the tree is O(n log n) over everything seen so
            // far, so doing it every 100 files makes the whole scan
            // quadratic — and on the main actor, which is where the UI
            // lives. Throttle by wall clock instead: the user cannot read
            // updates faster than this anyway.
            let now = Date()
            if processed - lastReport >= 100, now.timeIntervalSince(lastPartialReport) >= 0.75 {
                lastReport = processed
                lastPartialReport = now
                filesSeen = processed
                currentPath = path
                // Hand the window something to look at while the walk runs.
                report = makeReport(
                    entries: entries,
                    rootPath: rootPath,
                    rootName: rootPath == "/" ? displayName : (rootPath as NSString).lastPathComponent,
                    fromIndex: false,
                    indexedAt: nil,
                    wasCancelled: false
                )
            }
        }

        filesSeen = processed
        report = makeReport(
            entries: entries,
            rootPath: rootPath,
            rootName: rootPath == "/" ? displayName : (rootPath as NSString).lastPathComponent,
            fromIndex: false,
            indexedAt: nil,
            wasCancelled: cancelled
        )
    }

    /// Builds a report straight from the offline index, or nil when the
    /// account hasn't been indexed (a handful of rows from casual browsing
    /// is not an analysis, and presenting it as one would be misleading).
    private func indexedReport(
        accountId: UUID,
        rootPath: String,
        displayName: String
    ) async -> StorageReport? {
        let sources = await SearchIndex.shared.listIndexedSources()
        guard let source = sources.first(where: { $0.sourceId == accountId.uuidString }),
              let indexedAt = source.lastIndexed else { return nil }

        let rows = await SearchIndex.shared.cloudFilesUnder(accountId: accountId, rootPath: rootPath)
        guard !rows.isEmpty else { return nil }

        let entries = rows.map {
            StorageTreeBuilder.Entry(
                path: $0.path,
                name: $0.name,
                isDirectory: $0.isDirectory,
                size: $0.size,
                modificationDate: $0.modificationDate
            )
        }
        return makeReport(
            entries: entries,
            rootPath: rootPath,
            rootName: rootPath == "/" ? displayName : (rootPath as NSString).lastPathComponent,
            fromIndex: true,
            indexedAt: indexedAt,
            wasCancelled: false
        )
    }

    // MARK: - Local

    private func scanLocal(url: URL) async {
        let root = url
        let stream = Task.detached(priority: .utility) { () -> [StorageTreeBuilder.Entry] in
            var entries: [StorageTreeBuilder.Entry] = []
            let keys: [URLResourceKey] = [.isDirectoryKey, .fileSizeKey, .contentModificationDateKey]
            guard let enumerator = FileManager.default.enumerator(
                at: root,
                includingPropertiesForKeys: keys,
                options: [.skipsHiddenFiles]
            ) else { return entries }

            // `for case let … as URL in enumerator` needs a synchronous
            // iterator, which isn't available here — step the enumerator by
            // hand instead.
            while let next = enumerator.nextObject() {
                if Task.isCancelled { break }
                guard let fileURL = next as? URL else { continue }
                let values = try? fileURL.resourceValues(forKeys: Set(keys))
                let isDirectory = values?.isDirectory ?? false
                entries.append(StorageTreeBuilder.Entry(
                    path: fileURL.path,
                    name: fileURL.lastPathComponent,
                    isDirectory: isDirectory,
                    size: Int64(values?.fileSize ?? 0),
                    modificationDate: values?.contentModificationDate
                ))
            }
            return entries
        }

        let entries = await stream.value
        let cancelled = Task.isCancelled
        filesSeen = entries.filter { !$0.isDirectory }.count
        bytesSeen = entries.reduce(0) { $0 + ($1.isDirectory ? 0 : $1.size) }
        report = makeReport(
            entries: entries,
            rootPath: root.path,
            rootName: root.lastPathComponent.isEmpty ? root.path : root.lastPathComponent,
            fromIndex: false,
            indexedAt: nil,
            wasCancelled: cancelled
        )
    }

    // MARK: - Shared

    private func makeReport(
        entries: [StorageTreeBuilder.Entry],
        rootPath: String,
        rootName: String,
        fromIndex: Bool,
        indexedAt: Date?,
        wasCancelled: Bool
    ) -> StorageReport {
        let built = StorageTreeBuilder.build(entries: entries, rootPath: rootPath, rootName: rootName)
        return StorageReport(
            root: built.root,
            largestFiles: built.largest,
            totalBytes: built.root.size,
            fileCount: built.files,
            folderCount: built.folders,
            fromIndex: fromIndex,
            indexedAt: indexedAt,
            wasCancelled: wasCancelled
        )
    }
}
