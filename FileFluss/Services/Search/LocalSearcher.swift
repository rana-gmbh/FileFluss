import Foundation

actor LocalSearcher {

    func search(query: String, rootURL: URL, recursive: Bool) -> AsyncStream<[SearchResultItem]> {
        if recursive {
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
