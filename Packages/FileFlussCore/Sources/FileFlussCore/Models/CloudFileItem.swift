import Foundation
import UniformTypeIdentifiers

/// Whether the file is fully present locally or held remotely (only
/// meaningful for providers that have a partial-local concept — today
/// that's iCloud Drive's evicted files).
public enum CloudDownloadStatus: Hashable, Sendable {
    case local
    case evicted
    case downloading
}

public struct CloudFileItem: Identifiable, Hashable, Sendable {
    public let id: String
    public let name: String
    public let path: String
    public let isDirectory: Bool
    public let size: Int64
    public let modificationDate: Date
    public let checksum: String?
    public let downloadStatus: CloudDownloadStatus

    public init(
        id: String,
        name: String,
        path: String,
        isDirectory: Bool,
        size: Int64,
        modificationDate: Date,
        checksum: String?,
        downloadStatus: CloudDownloadStatus = .local
    ) {
        self.id = id
        self.name = name
        self.path = path
        self.isDirectory = isDirectory
        self.size = size
        self.modificationDate = modificationDate
        self.checksum = checksum
        self.downloadStatus = downloadStatus
    }

    public var icon: String {
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

    public var formattedSize: String {
        if isDirectory { return "--" }
        return ByteCountFormatter.string(fromByteCount: size, countStyle: .file)
    }

    public var formattedDate: String {
        if modificationDate == .distantPast { return "--" }
        return Self.dateFormatter.string(from: modificationDate)
    }

    public var kind: String {
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
