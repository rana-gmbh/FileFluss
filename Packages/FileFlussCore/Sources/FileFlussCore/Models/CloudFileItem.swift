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

/// What kind of navigational node an item is. Most items are `.normal`
/// files/folders; the others are synthetic entries (today: Google Drive's
/// "Shared drives" tree) that the UI must treat differently — e.g. you can
/// open a shared drive but not rename, delete, or move it.
public enum CloudItemRole: String, Hashable, Sendable, Codable {
    case normal
    /// A synthetic grouping node such as "Shared drives". Navigable, but not
    /// a real folder: no copy/move/rename/delete, and nothing can be created
    /// inside it directly.
    case virtualContainer
    /// A shared drive's root — a real folder you can browse and copy out of,
    /// but the drive itself can't be renamed, deleted, or moved.
    case driveRoot
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
    /// Optional SF Symbol name that overrides the default folder/file icon.
    /// Used for synthetic entries — e.g. Google Drive's "Shared drives"
    /// container and each shared drive — which should read as drives rather
    /// than ordinary folders.
    public let symbolIconOverride: String?
    /// Navigational role — drives which context-menu actions are valid.
    public let role: CloudItemRole

    public init(
        id: String,
        name: String,
        path: String,
        isDirectory: Bool,
        size: Int64,
        modificationDate: Date,
        checksum: String?,
        downloadStatus: CloudDownloadStatus = .local,
        symbolIconOverride: String? = nil,
        role: CloudItemRole = .normal
    ) {
        self.id = id
        self.name = name
        self.path = path
        self.isDirectory = isDirectory
        self.size = size
        self.modificationDate = modificationDate
        self.checksum = checksum
        self.downloadStatus = downloadStatus
        self.symbolIconOverride = symbolIconOverride
        self.role = role
    }

    public var icon: String {
        if let symbolIconOverride { return symbolIconOverride }
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
