import Foundation
import UniformTypeIdentifiers

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

    var kind: String {
        if isDirectory { return "Folder" }
        let ext = (name as NSString).pathExtension
        if !ext.isEmpty, let utType = UTType(filenameExtension: ext),
           let description = utType.localizedDescription {
            return description
        }
        return ext.isEmpty ? "Document" : ext.uppercased()
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

    /// Maximum per-file upload size accepted by this provider, in bytes.
    /// Returning nil means "no documented limit" — uploads will still be
    /// attempted, and the server may reject them after the bytes have been
    /// transferred. Providers should override this when they have a known
    /// hard cap so the upload path can reject oversized files locally
    /// (pre-flight) instead of wasting bandwidth.
    var maxUploadFileSize: Int64? { get async }

    func listDirectory(at path: String) async throws -> [CloudFileItem]
    func downloadFile(remotePath: String, to localURL: URL) async throws
    func uploadFile(from localURL: URL, to remotePath: String) async throws

    /// Download with byte-level progress. Default implementation forwards to the
    /// non-progress variant; providers that can stream should override to emit deltas.
    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws

    /// Upload with byte-level progress. Default implementation forwards to the
    /// non-progress variant; providers that can stream should override to emit deltas.
    func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws

    func deleteItem(at path: String) async throws
    func createDirectory(at path: String) async throws
    func renameItem(at path: String, to newName: String) async throws

    /// Server-side move on the same account: relocate `path` to `newPath`
    /// (full destination path, including the new filename). Default impl
    /// throws `.notImplemented` so callers can fall back to download+upload.
    func moveItem(at path: String, toPath newPath: String) async throws

    /// Server-side copy on the same account. Same conventions as `moveItem`.
    func copyItem(at path: String, toPath newPath: String) async throws

    func getFileMetadata(at path: String) async throws -> CloudFileItem
    func folderSize(at path: String) async throws -> Int64

    /// Search for files matching the query. Returns nil if the provider does not support search.
    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]?
}

extension CloudProvider {
    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]? {
        nil
    }

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        try await uploadFile(from: localURL, to: remotePath)
    }

    var maxUploadFileSize: Int64? {
        get async { nil }
    }

    func moveItem(at path: String, toPath newPath: String) async throws {
        throw CloudProviderError.notImplemented
    }

    func copyItem(at path: String, toPath newPath: String) async throws {
        throw CloudProviderError.notImplemented
    }
}
