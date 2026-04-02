import SwiftUI

@Observable @MainActor
final class SearchViewModel {
    var searchText: String = ""
    var results: [SearchResultItem] = []
    var status: SearchStatus = .idle
    var sourceFilter: SourceFilter = .all

    private var searchTask: Task<Void, Never>?

    enum SourceFilter: Hashable {
        case all
        case local
        case cloud(UUID)
    }

    var filteredResults: [SearchResultItem] {
        switch sourceFilter {
        case .all:
            return results
        case .local:
            return results.filter { $0.localFileItem != nil }
        case .cloud(let accountId):
            return results.filter { $0.cloudAccountId == accountId }
        }
    }

    /// Groups results by source for display
    var groupedResults: [(source: String, icon: String?, providerType: CloudProviderType?, items: [SearchResultItem])] {
        var localItems: [SearchResultItem] = []
        var cloudGroups: [UUID: (name: String, providerType: CloudProviderType?, items: [SearchResultItem])] = [:]

        for item in filteredResults {
            switch item {
            case .local:
                localItems.append(item)
            case .cloud(_, let accountId, let accountName):
                var group = cloudGroups[accountId] ?? (name: accountName, providerType: nil, items: [])
                group.items.append(item)
                cloudGroups[accountId] = group
            }
        }

        var groups: [(source: String, icon: String?, providerType: CloudProviderType?, items: [SearchResultItem])] = []
        if !localItems.isEmpty {
            groups.append((source: "Local Files", icon: "externaldrive.fill", providerType: nil, items: localItems))
        }
        for (_, group) in cloudGroups.sorted(by: { $0.value.name < $1.value.name }) {
            groups.append((source: group.name, icon: nil, providerType: group.providerType, items: group.items))
        }
        return groups
    }

    /// Available filter options based on current results
    var availableSources: [(label: String, filter: SourceFilter, providerType: CloudProviderType?)] {
        var sources: [(label: String, filter: SourceFilter, providerType: CloudProviderType?)] = []
        sources.append(("All", .all, nil))

        let hasLocal = results.contains { $0.localFileItem != nil }
        if hasLocal {
            sources.append(("Local", .local, nil))
        }

        var seenAccounts = Set<UUID>()
        for item in results {
            if case .cloud(_, let accountId, let accountName) = item, !seenAccounts.contains(accountId) {
                seenAccounts.insert(accountId)
                sources.append((accountName, .cloud(accountId), nil))
            }
        }
        return sources
    }

    func performSearch(
        localDirectory: URL?,
        allAccounts: [(id: UUID, name: String)]
    ) {
        searchTask?.cancel()

        guard !searchText.isEmpty else {
            results = []
            status = .idle
            return
        }

        status = .searching(sourcesCompleted: 0, sourcesTotal: 0)

        searchTask = Task {
            try? await Task.sleep(for: .milliseconds(200))
            guard !Task.isCancelled else { return }

            results = []

            let request = SearchCoordinator.SearchRequest(
                query: searchText,
                scope: .allSources,
                localDirectory: localDirectory,
                cloudAccountId: nil,
                cloudPath: nil,
                allAccounts: allAccounts
            )

            let stream = await SearchCoordinator.shared.search(request: request)
            var completedSources = 0
            var totalSources = 0
            var seenSourceNames = Set<String>()

            for await batch in stream {
                guard !Task.isCancelled else { return }
                if batch.isComplete {
                    completedSources += 1
                } else {
                    if !batch.items.isEmpty && !seenSourceNames.contains(batch.source) {
                        totalSources += 1
                        seenSourceNames.insert(batch.source)
                    }
                    let existingIds = Set(results.map(\.id))
                    let newItems = batch.items.filter { !existingIds.contains($0.id) }
                    results.append(contentsOf: newItems)
                }
                status = .searching(
                    sourcesCompleted: completedSources,
                    sourcesTotal: max(totalSources, completedSources)
                )
            }
            status = .complete(resultCount: results.count)
        }
    }

    func clear() {
        searchTask?.cancel()
        searchTask = nil
        searchText = ""
        results = []
        status = .idle
        sourceFilter = .all
    }
}
