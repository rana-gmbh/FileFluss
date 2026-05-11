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
    /// A match found in the offline search index. `sourceId` identifies the
    /// originating drive or cloud account; `sourceKind` is `external`,
    /// `network`, or `cloud`. Clicking one opens an `OfflineSourceView`.
    case offline(name: String, path: String, isDirectory: Bool, size: Int64,
                 modificationDate: Date, sourceId: String, sourceName: String,
                 sourceKind: String)

    var id: String {
        switch self {
        case .local(let item): return "local:\(item.id)"
        case .cloud(let item, let accountId, _): return "cloud:\(accountId.uuidString):\(item.id)"
        case .offline(_, let path, _, _, _, let sid, _, _): return "offline:\(sid):\(path)"
        }
    }

    var name: String {
        switch self {
        case .local(let item): return item.name
        case .cloud(let item, _, _): return item.name
        case .offline(let name, _, _, _, _, _, _, _): return name
        }
    }

    var isDirectory: Bool {
        switch self {
        case .local(let item): return item.isDirectory
        case .cloud(let item, _, _): return item.isDirectory
        case .offline(_, _, let isDir, _, _, _, _, _): return isDir
        }
    }

    var size: Int64 {
        switch self {
        case .local(let item): return item.size
        case .cloud(let item, _, _): return item.size
        case .offline(_, _, _, let s, _, _, _, _): return s
        }
    }

    var modificationDate: Date {
        switch self {
        case .local(let item): return item.modificationDate
        case .cloud(let item, _, _): return item.modificationDate
        case .offline(_, _, _, _, let d, _, _, _): return d
        }
    }

    var icon: String {
        switch self {
        case .local(let item): return item.icon
        case .cloud(let item, _, _): return item.icon
        case .offline(let name, _, let isDir, _, _, _, _, _):
            if isDir { return "folder.fill" }
            let ext = (name as NSString).pathExtension.lowercased()
            switch ext {
            case "jpg", "jpeg", "png", "gif", "heic", "webp", "tiff": return "photo"
            case "mp4", "mov", "avi", "mkv": return "film"
            case "mp3", "aac", "wav", "flac", "m4a": return "music.note"
            case "pdf": return "doc.richtext"
            case "zip", "tar", "gz", "rar", "7z": return "doc.zipper"
            case "txt", "md", "rtf": return "doc.text"
            default: return "doc"
            }
        }
    }

    var formattedSize: String {
        switch self {
        case .local(let item): return item.formattedSize
        case .cloud(let item, _, _): return item.formattedSize
        case .offline(_, _, let isDir, let s, _, _, _, _):
            return isDir ? "--" : ByteCountFormatter.string(fromByteCount: s, countStyle: .file)
        }
    }

    var formattedDate: String {
        switch self {
        case .local(let item): return item.formattedDate
        case .cloud(let item, _, _): return item.formattedDate
        case .offline(_, _, _, _, let d, _, _, _):
            if d == .distantPast { return "--" }
            return Self.offlineDateFormatter.string(from: d)
        }
    }

    private static let offlineDateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()

    var locationDescription: String {
        switch self {
        case .local(let item):
            let parent = item.url.deletingLastPathComponent().path()
            return parent
        case .cloud(let item, _, let accountName):
            let parent = (item.path as NSString).deletingLastPathComponent
            return "\(accountName): \(parent)"
        case .offline(_, let path, _, _, _, _, let sourceName, _):
            let parent = (path as NSString).deletingLastPathComponent
            return "\(sourceName) (offline): \(parent)"
        }
    }

    var isOffline: Bool {
        if case .offline = self { return true }
        return false
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

    /// For offline matches, the source identifier + parent path so the UI
    /// can open an `OfflineSourceView` at the file's containing folder.
    var offlineNavigation: (sourceId: String, parentPath: String)? {
        if case .offline(_, let path, _, _, _, let sid, _, _) = self {
            let parent = (path as NSString).deletingLastPathComponent
            return (sid, parent.isEmpty ? "/" : parent)
        }
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
