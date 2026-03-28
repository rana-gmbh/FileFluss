import Foundation
import Combine

actor FileSystemService {
    static let shared = FileSystemService()

    private static let resourceKeys: Set<URLResourceKey> = [
        .nameKey, .isDirectoryKey, .fileSizeKey, .totalFileSizeKey,
        .contentModificationDateKey, .creationDateKey, .isHiddenKey,
        .isSymbolicLinkKey, .contentTypeKey
    ]

    func listDirectory(at url: URL, showHidden: Bool = false) throws -> [FileItem] {
        let contents = try Foundation.FileManager.default.contentsOfDirectory(
            at: url,
            includingPropertiesForKeys: Array(Self.resourceKeys),
            options: showHidden ? [] : [.skipsHiddenFiles]
        )

        return try contents.map { itemURL in
            let values = try itemURL.resourceValues(forKeys: Self.resourceKeys)
            return FileItem(url: itemURL, resourceValues: values)
        }
    }

    func createDirectory(at url: URL) throws {
        try Foundation.FileManager.default.createDirectory(
            at: url,
            withIntermediateDirectories: true
        )
    }

    func deleteItem(at url: URL) throws {
        try Foundation.FileManager.default.removeItem(at: url)
    }

    func moveItem(from source: URL, to destination: URL) throws {
        try Foundation.FileManager.default.moveItem(at: source, to: destination)
    }

    func copyItem(from source: URL, to destination: URL) throws {
        try Foundation.FileManager.default.copyItem(at: source, to: destination)
    }

    func itemExists(at url: URL) -> Bool {
        Foundation.FileManager.default.fileExists(atPath: url.path())
    }
}
