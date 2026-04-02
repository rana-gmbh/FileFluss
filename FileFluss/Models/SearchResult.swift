import Foundation

enum SearchScope: Hashable, CaseIterable, Sendable {
    case currentFolder
    case currentSource
    case allSources

    var label: String {
        switch self {
        case .currentFolder: return "Current Folder"
        case .currentSource: return "This Source"
        case .allSources: return "All Sources"
        }
    }
}

enum SearchResultItem: Identifiable, Hashable, Sendable {
    case local(FileItem)
    case cloud(CloudFileItem, accountId: UUID, accountName: String)

    var id: String {
        switch self {
        case .local(let item): return "local:\(item.id)"
        case .cloud(let item, let accountId, _): return "cloud:\(accountId.uuidString):\(item.id)"
        }
    }

    var name: String {
        switch self {
        case .local(let item): return item.name
        case .cloud(let item, _, _): return item.name
        }
    }

    var isDirectory: Bool {
        switch self {
        case .local(let item): return item.isDirectory
        case .cloud(let item, _, _): return item.isDirectory
        }
    }

    var size: Int64 {
        switch self {
        case .local(let item): return item.size
        case .cloud(let item, _, _): return item.size
        }
    }

    var modificationDate: Date {
        switch self {
        case .local(let item): return item.modificationDate
        case .cloud(let item, _, _): return item.modificationDate
        }
    }

    var icon: String {
        switch self {
        case .local(let item): return item.icon
        case .cloud(let item, _, _): return item.icon
        }
    }

    var formattedSize: String {
        switch self {
        case .local(let item): return item.formattedSize
        case .cloud(let item, _, _): return item.formattedSize
        }
    }

    var formattedDate: String {
        switch self {
        case .local(let item): return item.formattedDate
        case .cloud(let item, _, _): return item.formattedDate
        }
    }

    var locationDescription: String {
        switch self {
        case .local(let item):
            let parent = item.url.deletingLastPathComponent().path()
            return parent
        case .cloud(let item, _, let accountName):
            let parent = (item.path as NSString).deletingLastPathComponent
            return "\(accountName): \(parent)"
        }
    }

    var localFileItem: FileItem? {
        if case .local(let item) = self { return item }
        return nil
    }

    var cloudFileItem: CloudFileItem? {
        if case .cloud(let item, _, _) = self { return item }
        return nil
    }

    var cloudAccountId: UUID? {
        if case .cloud(_, let accountId, _) = self { return accountId }
        return nil
    }
}

enum SearchStatus: Equatable {
    case idle
    case searching(sourcesCompleted: Int, sourcesTotal: Int)
    case complete(resultCount: Int)
}

struct SearchResultBatch: Sendable {
    let source: String
    let items: [SearchResultItem]
    let isComplete: Bool
}
