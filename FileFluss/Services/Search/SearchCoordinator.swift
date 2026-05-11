import Foundation

actor SearchCoordinator {
    static let shared = SearchCoordinator()

    private let localSearcher = LocalSearcher()
    private let cloudSearcher = CloudSearcher()

    struct SearchRequest: Sendable {
        let query: String
        let scope: SearchScope
        let localDirectory: URL?
        let cloudAccountId: UUID?
        let cloudPath: String?
        let allAccounts: [(id: UUID, name: String)]
        /// Extra local roots to scan in parallel with `localDirectory` when
        /// scope is `.allSources` — typically mounted external/network
        /// drives so they're included in a "search everywhere" query
        /// without requiring the user to navigate to them first. Each
        /// entry pairs the root URL with a display name used to group its
        /// results in the UI.
        let extraLocalRoots: [(url: URL, name: String)]
        /// When true, also surface matches from the offline index for
        /// disconnected cloud accounts and unmounted indexed drives.
        let includeOffline: Bool
        /// Currently online sources keyed by id — used to determine which
        /// indexed sources are *offline* and therefore eligible to appear in
        /// offline results. `offlineCloudAccounts` holds disconnected ones
        /// from `allAccounts`. `offlineDrives` carries (id, name) for any
        /// drive that has an index but isn't currently mounted.
        let offlineCloudAccounts: [(id: UUID, name: String)]
        let offlineDrives: [(id: String, name: String)]
    }

    func search(request: SearchRequest) -> AsyncStream<SearchResultBatch> {
        AsyncStream { continuation in
            let task = Task {
                await withTaskGroup(of: Void.self) { group in
                    var sourcesTotal = 0
                    var sourcesCompleted = 0

                    // Local search
                    if let localDir = request.localDirectory,
                       request.scope != .currentSource || request.cloudAccountId == nil {
                        sourcesTotal += 1
                        group.addTask { [localSearcher] in
                            let recursive = request.scope != .currentFolder
                            let stream = await localSearcher.search(
                                query: request.query,
                                rootURL: localDir,
                                recursive: recursive
                            )
                            for await batch in stream {
                                guard !Task.isCancelled else { return }
                                continuation.yield(SearchResultBatch(
                                    source: "Local",
                                    items: batch,
                                    isComplete: false
                                ))
                            }
                            continuation.yield(SearchResultBatch(
                                source: "Local",
                                items: [],
                                isComplete: true
                            ))
                        }
                    }

                    // Extra local roots (mounted external/network drives) — only
                    // applies to "all sources" scope, so we don't fan out when
                    // the user explicitly scoped to the active panel.
                    if request.scope == .allSources {
                        for entry in request.extraLocalRoots {
                            sourcesTotal += 1
                            let rootURL = entry.url
                            let sourceName = entry.name
                            group.addTask { [localSearcher] in
                                // Use direct recursive enumeration rather
                                // than Spotlight: many external drives have
                                // indexing disabled, and the underlying
                                // NSMetadataQuery is single-instance so
                                // running multiple in parallel doesn't work.
                                let stream = await localSearcher.recursiveEnumerate(
                                    query: request.query,
                                    rootURL: rootURL
                                )
                                for await batch in stream {
                                    guard !Task.isCancelled else { return }
                                    continuation.yield(SearchResultBatch(
                                        source: sourceName,
                                        items: batch,
                                        isComplete: false
                                    ))
                                }
                                continuation.yield(SearchResultBatch(
                                    source: sourceName,
                                    items: [],
                                    isComplete: true
                                ))
                            }
                        }
                    }

                    // Cloud search
                    let accountsToSearch: [(id: UUID, name: String)]
                    switch request.scope {
                    case .currentFolder:
                        // Current folder filtering is done in-memory by SearchViewModel
                        accountsToSearch = []
                    case .currentSource:
                        if let accountId = request.cloudAccountId,
                           let account = request.allAccounts.first(where: { $0.id == accountId }) {
                            accountsToSearch = [account]
                        } else {
                            accountsToSearch = []
                        }
                    case .allSources:
                        accountsToSearch = request.allAccounts
                    }

                    for account in accountsToSearch {
                        sourcesTotal += 1
                        group.addTask { [cloudSearcher] in
                            guard let provider = await SyncEngine.shared.provider(for: account.id) else { return }
                            let rootPath = (request.cloudAccountId == account.id) ? request.cloudPath : nil
                            let stream = await cloudSearcher.search(
                                query: request.query,
                                provider: provider,
                                accountId: account.id,
                                accountName: account.name,
                                rootPath: rootPath
                            )
                            for await batch in stream {
                                guard !Task.isCancelled else { return }
                                continuation.yield(SearchResultBatch(
                                    source: account.name,
                                    items: batch,
                                    isComplete: false
                                ))
                            }
                            continuation.yield(SearchResultBatch(
                                source: account.name,
                                items: [],
                                isComplete: true
                            ))
                        }
                    }

                    // Offline indexed results (drives + disconnected accounts)
                    if request.includeOffline {
                        let query = request.query
                        let offlineCloud = request.offlineCloudAccounts
                        let offlineDrives = request.offlineDrives
                        if !offlineCloud.isEmpty || !offlineDrives.isEmpty {
                            group.addTask {
                                let driveIds = offlineDrives.map(\.id)
                                let driveResults = await SearchIndex.shared.searchIndexed(
                                    query: query, sourceIds: driveIds, limit: 300
                                )
                                let driveNames = Dictionary(uniqueKeysWithValues: offlineDrives.map { ($0.id, $0.name) })
                                let driveBatch: [SearchResultItem] = driveResults.map { f in
                                    .offline(
                                        name: f.name,
                                        path: f.path,
                                        isDirectory: f.isDirectory,
                                        size: f.size,
                                        modificationDate: f.modificationDate,
                                        sourceId: f.sourceId,
                                        sourceName: driveNames[f.sourceId] ?? "Offline Drive",
                                        sourceKind: "drive"
                                    )
                                }
                                if !driveBatch.isEmpty {
                                    continuation.yield(SearchResultBatch(
                                        source: "Offline Drives",
                                        items: driveBatch,
                                        isComplete: false
                                    ))
                                }

                                let cloudIds = offlineCloud.map(\.id)
                                let cloudResults = await SearchIndex.shared.searchCloudCached(
                                    query: query, accountIds: cloudIds, limit: 300
                                )
                                let cloudNames = Dictionary(uniqueKeysWithValues: offlineCloud.map { ($0.id, $0.name) })
                                let cloudBatch: [SearchResultItem] = cloudResults.map { r in
                                    .offline(
                                        name: r.item.name,
                                        path: r.item.path,
                                        isDirectory: r.item.isDirectory,
                                        size: r.item.size,
                                        modificationDate: r.item.modificationDate,
                                        sourceId: r.accountId.uuidString,
                                        sourceName: cloudNames[r.accountId] ?? "Offline Account",
                                        sourceKind: "cloud"
                                    )
                                }
                                if !cloudBatch.isEmpty {
                                    continuation.yield(SearchResultBatch(
                                        source: "Offline Cloud Accounts",
                                        items: cloudBatch,
                                        isComplete: false
                                    ))
                                }
                                continuation.yield(SearchResultBatch(
                                    source: "Offline",
                                    items: [],
                                    isComplete: true
                                ))
                            }
                        }
                    }

                    // Wait for all to finish
                    for await _ in group {}
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }
}
