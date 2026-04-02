import Foundation

actor CloudSearcher {

    func search(
        query: String,
        provider: any CloudProvider,
        accountId: UUID,
        accountName: String,
        rootPath: String?
    ) -> AsyncStream<[SearchResultItem]> {
        AsyncStream { continuation in
            let task = Task {
                // 1. Yield cached index results immediately
                let cachedResults = await SearchIndex.shared.search(query: query, accountId: accountId)
                if !cachedResults.isEmpty {
                    let items = cachedResults.map { indexed in
                        SearchResultItem.cloud(indexed.item, accountId: accountId, accountName: accountName)
                    }
                    continuation.yield(items)
                }

                // 2. Try provider's native search API
                do {
                    if let apiResults = try await provider.searchFiles(query: query, path: rootPath) {
                        // Update index with fresh results
                        await SearchIndex.shared.upsertItems(apiResults, accountId: accountId)

                        let items = apiResults.map { item in
                            SearchResultItem.cloud(item, accountId: accountId, accountName: accountName)
                        }

                        // Deduplicate against cached results
                        if !cachedResults.isEmpty {
                            let cachedPaths = Set(cachedResults.map(\.item.path))
                            let newItems = items.filter { result in
                                if let cloudItem = result.cloudFileItem {
                                    return !cachedPaths.contains(cloudItem.path)
                                }
                                return true
                            }
                            if !newItems.isEmpty {
                                continuation.yield(newItems)
                            }
                        } else if !items.isEmpty {
                            continuation.yield(items)
                        }
                    }
                } catch {
                    // API search failed — cached results already yielded
                }

                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }
}
