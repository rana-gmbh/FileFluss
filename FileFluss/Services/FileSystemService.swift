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
        do {
            try Foundation.FileManager.default.createDirectory(
                at: url,
                withIntermediateDirectories: true
            )
            SupportLogger.shared.log("createDirectory: \(url.path)", category: "fs")
        } catch {
            SupportLogger.shared.log("createDirectory FAILED: \(url.path) — \(error.localizedDescription)", category: "fs", level: .error)
            throw error
        }
    }

    func deleteItem(at url: URL) throws {
        do {
            try Foundation.FileManager.default.removeItem(at: url)
            SupportLogger.shared.log("deleteItem: \(url.path)", category: "fs")
        } catch {
            SupportLogger.shared.log("deleteItem FAILED: \(url.path) — \(error.localizedDescription)", category: "fs", level: .error)
            throw error
        }
    }

    func trashItem(at url: URL) throws {
        do {
            try Foundation.FileManager.default.trashItem(at: url, resultingItemURL: nil)
            SupportLogger.shared.log("trashItem: \(url.path)", category: "fs")
        } catch {
            SupportLogger.shared.log("trashItem FAILED: \(url.path) — \(error.localizedDescription)", category: "fs", level: .error)
            throw error
        }
    }

    func moveItem(from source: URL, to destination: URL) throws {
        do {
            try Foundation.FileManager.default.moveItem(at: source, to: destination)
            SupportLogger.shared.log("moveItem: \(source.path) → \(destination.path)", category: "fs")
        } catch {
            SupportLogger.shared.log("moveItem FAILED: \(source.path) → \(destination.path) — \(error.localizedDescription)", category: "fs", level: .error)
            throw error
        }
    }

    func copyItem(from source: URL, to destination: URL) throws {
        do {
            try Foundation.FileManager.default.copyItem(at: source, to: destination)
            SupportLogger.shared.log("copyItem: \(source.path) → \(destination.path)", category: "fs")
        } catch {
            SupportLogger.shared.log("copyItem FAILED: \(source.path) → \(destination.path) — \(error.localizedDescription)", category: "fs", level: .error)
            throw error
        }
    }

    func itemExists(at url: URL) -> Bool {
        Foundation.FileManager.default.fileExists(atPath: url.path())
    }

    func directorySize(at url: URL) throws -> Int64 {
        let fm = Foundation.FileManager.default
        let enumerator = fm.enumerator(
            at: url,
            includingPropertiesForKeys: [.fileSizeKey, .isDirectoryKey],
            options: [.skipsHiddenFiles]
        )
        var totalSize: Int64 = 0
        while let fileURL = enumerator?.nextObject() as? URL {
            let values = try fileURL.resourceValues(forKeys: [.fileSizeKey, .isDirectoryKey])
            if values.isDirectory != true {
                totalSize += Int64(values.fileSize ?? 0)
            }
        }
        return totalSize
    }
}
