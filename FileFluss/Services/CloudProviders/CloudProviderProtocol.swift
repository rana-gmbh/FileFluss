import Foundation

struct CloudFileItem: Identifiable, Hashable, Sendable {
    let id: String
    let name: String
    let path: String
    let isDirectory: Bool
    let size: Int64
    let modificationDate: Date
    let checksum: String?

    var icon: String {
        if isDirectory { return "folder.fill" }
        let ext = (name as NSString).pathExtension.lowercased()
        switch ext {
        case "jpg", "jpeg", "png", "gif", "heic", "webp", "tiff": return "photo"
        case "mp4", "mov", "avi", "mkv": return "film"
        case "mp3", "aac", "wav", "flac", "m4a": return "music.note"
        case "pdf": return "doc.richtext"
        case "zip", "tar", "gz", "rar", "7z": return "doc.zipper"
        case "swift", "py", "js", "ts", "html", "css", "json", "xml":
            return "chevron.left.forwardslash.chevron.right"
        case "txt", "md", "rtf": return "doc.text"
        default: return "doc"
        }
    }

    var formattedSize: String {
        if isDirectory { return "--" }
        return ByteCountFormatter.string(fromByteCount: size, countStyle: .file)
    }

    var formattedDate: String {
        if modificationDate == .distantPast { return "--" }
        return Self.dateFormatter.string(from: modificationDate)
    }

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()
}

protocol CloudProvider: Sendable {
    var providerType: CloudProviderType { get }

    func authenticate() async throws
    func disconnect() async throws
    var isAuthenticated: Bool { get async }

    func listDirectory(at path: String) async throws -> [CloudFileItem]
    func downloadFile(remotePath: String, to localURL: URL) async throws
    func uploadFile(from localURL: URL, to remotePath: String) async throws
    func deleteItem(at path: String) async throws
    func createDirectory(at path: String) async throws
    func renameItem(at path: String, to newName: String) async throws
    func getFileMetadata(at path: String) async throws -> CloudFileItem
    func folderSize(at path: String) async throws -> Int64

    /// Search for files matching the query. Returns nil if the provider does not support search.
    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]?
}

extension CloudProvider {
    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]? {
        nil
    }
}
