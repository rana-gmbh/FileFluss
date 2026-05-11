import Foundation

actor LocalSearcher {

    func search(query: String, rootURL: URL, recursive: Bool) -> AsyncStream<[SearchResultItem]> {
        if recursive {
            // Spotlight is reliable for the user's home tree but flaky on
            // mounted external/network volumes — even when `mdutil` reports
            // indexing as enabled, results frequently come back empty. Use
            // a direct recursive walk for /Volumes paths so the user always
            // finds their files; keep Spotlight for the indexed system tree.
            if rootURL.path.hasPrefix("/Volumes/") {
                return recursiveEnumerate(query: query, rootURL: rootURL)
            }
            return spotlightSearch(query: query, rootURL: rootURL)
        } else {
            return currentFolderSearch(query: query, rootURL: rootURL)
        }
    }

    private func currentFolderSearch(query: String, rootURL: URL) -> AsyncStream<[SearchResultItem]> {
        AsyncStream { continuation in
            let task = Task.detached(priority: .userInitiated) {
                let results = Self.enumerateFolder(query: query, rootURL: rootURL)
                if !results.isEmpty {
                    continuation.yield(results)
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    private nonisolated static func enumerateFolder(query: String, rootURL: URL) -> [SearchResultItem] {
        let fm = FileManager.default
        let keys: Set<URLResourceKey> = [
            .nameKey, .isDirectoryKey, .fileSizeKey, .totalFileSizeKey,
            .contentModificationDateKey, .creationDateKey, .isHiddenKey,
            .isSymbolicLinkKey, .contentTypeKey
        ]
        guard let enumerator = fm.enumerator(
            at: rootURL,
            includingPropertiesForKeys: Array(keys),
            options: [.skipsSubdirectoryDescendants]
        ) else {
            return []
        }

        let lowerQuery = query.lowercased()
        var batch: [SearchResultItem] = []
        while let url = enumerator.nextObject() as? URL {
            let values = (try? url.resourceValues(forKeys: keys)) ?? URLResourceValues()
            let name = values.name ?? url.lastPathComponent
            if name.lowercased().contains(lowerQuery) {
                let item = FileItem(url: url, resourceValues: values)
                batch.append(.local(item))
            }
        }
        return batch
    }

    /// Recursive name-match for `/Volumes` and other un-indexed roots.
    /// Streaming behaviour matters here: huge drives can take minutes to
    /// walk fully, so each match is yielded as soon as it's found rather
    /// than buffered. The enumerator runs WITHOUT prefetching resource
    /// values — that's a per-file metadata read that adds enormous overhead
    /// on slow external drives, and we only need the URL to test the name.
    func recursiveEnumerate(query: String, rootURL: URL) -> AsyncStream<[SearchResultItem]> {
        AsyncStream { continuation in
            let task = Task.detached(priority: .userInitiated) {
                let fm = FileManager.default
                let rootPath = rootURL.path
                NSLog("[FileFluss] recursiveEnumerate start root=\(rootPath) query=\(query)")

                // Quick reachability probe so the empty case is logged
                // (TCC denials, filesystem errors etc surface here).
                var isDir: ObjCBool = false
                if !fm.fileExists(atPath: rootPath, isDirectory: &isDir) || !isDir.boolValue {
                    NSLog("[FileFluss] recursiveEnumerate aborted: root not reachable as directory")
                    continuation.finish()
                    return
                }

                guard let enumerator = fm.enumerator(
                    at: rootURL,
                    includingPropertiesForKeys: nil,
                    options: [.skipsHiddenFiles],
                    errorHandler: { url, error in
                        NSLog("[FileFluss] enumerator error at \(url.path): \(error.localizedDescription)")
                        return true
                    }
                ) else {
                    NSLog("[FileFluss] recursiveEnumerate aborted: enumerator(at:) returned nil")
                    continuation.finish()
                    return
                }

                let lowerQuery = query.lowercased()
                var visited = 0
                var matched = 0
                while let url = enumerator.nextObject() as? URL {
                    if Task.isCancelled {
                        NSLog("[FileFluss] recursiveEnumerate cancelled at visited=\(visited)")
                        break
                    }
                    visited += 1
                    let name = url.lastPathComponent
                    if name.lowercased().contains(lowerQuery) {
                        // Fetch resource values lazily for matches only —
                        // the walk hot path stays as cheap as possible.
                        let keys: Set<URLResourceKey> = [
                            .nameKey, .isDirectoryKey, .fileSizeKey, .totalFileSizeKey,
                            .contentModificationDateKey, .creationDateKey, .isHiddenKey,
                            .isSymbolicLinkKey, .contentTypeKey
                        ]
                        let values = (try? url.resourceValues(forKeys: keys)) ?? URLResourceValues()
                        let item: SearchResultItem = .local(FileItem(url: url, resourceValues: values))
                        // Stream each match immediately so the UI shows
                        // progress on slow drives.
                        continuation.yield([item])
                        matched += 1
                    }
                }
                NSLog("[FileFluss] recursiveEnumerate finished root=\(rootPath) visited=\(visited) matched=\(matched)")
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    private func spotlightSearch(query: String, rootURL: URL) -> AsyncStream<[SearchResultItem]> {
        AsyncStream { continuation in
            Task { @MainActor in
                let helper = SpotlightSearchHelper(query: query, rootURL: rootURL, continuation: continuation)
                helper.start()
            }
        }
    }
}

/// Bridges NSMetadataQuery (requires RunLoop) to AsyncStream.
@MainActor
private final class SpotlightSearchHelper {
    private let mdQuery = NSMetadataQuery()
    private let continuation: AsyncStream<[SearchResultItem]>.Continuation
    private var gatheringObserver: Any?
    private var finishedObserver: Any?

    static var current: SpotlightSearchHelper?

    init(query: String, rootURL: URL, continuation: AsyncStream<[SearchResultItem]>.Continuation) {
        self.continuation = continuation

        mdQuery.searchScopes = [rootURL]
        mdQuery.predicate = NSPredicate(format: "kMDItemFSName CONTAINS[cd] %@", query)
        mdQuery.sortDescriptors = [NSSortDescriptor(key: kMDItemFSName as String, ascending: true)]
    }

    func start() {
        SpotlightSearchHelper.current?.stop()
        SpotlightSearchHelper.current = self

        gatheringObserver = NotificationCenter.default.addObserver(
            forName: .NSMetadataQueryGatheringProgress,
            object: mdQuery,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.processResults() }
        }

        finishedObserver = NotificationCenter.default.addObserver(
            forName: .NSMetadataQueryDidFinishGathering,
            object: mdQuery,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.processResults()
                self?.stop()
            }
        }

        mdQuery.start()
    }

    private func processResults() {
        mdQuery.disableUpdates()
        defer { mdQuery.enableUpdates() }

        var batch: [SearchResultItem] = []
        for i in 0..<mdQuery.resultCount {
            guard let result = mdQuery.result(at: i) as? NSMetadataItem,
                  let path = result.value(forAttribute: kMDItemPath as String) as? String else { continue }
            let url = URL(filePath: path)
            let item = FileItem(url: url)
            batch.append(.local(item))
        }
        if !batch.isEmpty {
            continuation.yield(batch)
        }
    }

    private func stop() {
        mdQuery.stop()
        if let obs = gatheringObserver { NotificationCenter.default.removeObserver(obs) }
        if let obs = finishedObserver { NotificationCenter.default.removeObserver(obs) }
        gatheringObserver = nil
        finishedObserver = nil
        continuation.finish()
        if SpotlightSearchHelper.current === self {
            SpotlightSearchHelper.current = nil
        }
    }
}
