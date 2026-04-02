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

                    // Wait for all to finish
                    for await _ in group {}
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }
}
