import Foundation
import UniformTypeIdentifiers

struct FileItem: Identifiable, Hashable, Sendable {
    let id: String
    let url: URL
    let name: String
    let isDirectory: Bool
    let size: Int64
    let modificationDate: Date
    let creationDate: Date
    let isHidden: Bool
    let isSymlink: Bool
    let contentType: UTType?

    init(url: URL) {
        let keys: Set<URLResourceKey> = [
            .nameKey, .isDirectoryKey, .fileSizeKey, .totalFileSizeKey,
            .contentModificationDateKey, .creationDateKey, .isHiddenKey,
            .isSymbolicLinkKey, .contentTypeKey
        ]
        let values = (try? url.resourceValues(forKeys: keys)) ?? URLResourceValues()
        self.init(url: url, resourceValues: values)
    }

    init(url: URL, resourceValues: URLResourceValues) {
        self.id = url.path()
        self.url = url
        self.name = resourceValues.name ?? url.lastPathComponent
        self.isDirectory = resourceValues.isDirectory ?? false
        self.size = Int64(resourceValues.totalFileSize ?? resourceValues.fileSize ?? 0)
        self.modificationDate = resourceValues.contentModificationDate ?? .distantPast
        self.creationDate = resourceValues.creationDate ?? .distantPast
        self.isHidden = resourceValues.isHidden ?? false
        self.isSymlink = resourceValues.isSymbolicLink ?? false
        self.contentType = resourceValues.contentType
    }

    var icon: String {
        if isDirectory {
            return "folder.fill"
        }
        guard let contentType else { return "doc" }
        if contentType.conforms(to: .image) { return "photo" }
        if contentType.conforms(to: .movie) { return "film" }
        if contentType.conforms(to: .audio) { return "music.note" }
        if contentType.conforms(to: .pdf) { return "doc.richtext" }
        if contentType.conforms(to: .sourceCode) { return "chevron.left.forwardslash.chevron.right" }
        if contentType.conforms(to: .archive) { return "doc.zipper" }
        if contentType.conforms(to: .text) { return "doc.text" }
        return "doc"
    }

    var formattedSize: String {
        if isDirectory { return "--" }
        return ByteCountFormatter.string(fromByteCount: size, countStyle: .file)
    }

    var formattedDate: String {
        Self.dateFormatter.string(from: modificationDate)
    }

    var formattedCreationDate: String {
        if creationDate == .distantPast { return "--" }
        return Self.dateFormatter.string(from: creationDate)
    }

    var kind: String {
        if isDirectory { return "Folder" }
        if let contentType, let description = contentType.localizedDescription {
            return description
        }
        let ext = url.pathExtension
        return ext.isEmpty ? "Document" : ext.uppercased()
    }

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()
}
