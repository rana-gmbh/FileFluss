import Foundation

/// One entry in a storage analysis: a file, or a folder with everything
/// beneath it rolled up.
///
/// Folder sizes are always computed from the files underneath, never taken
/// from what a provider reports for a directory — most report 0, some
/// report a stale or capped figure, and a size view that quietly
/// under-reports is worse than none.
@MainActor
final class StorageNode: Identifiable {
    let id: String
    let name: String
    let path: String
    let isDirectory: Bool
    /// Rolled-up bytes for a folder; the file's own size for a file.
    private(set) var size: Int64
    private(set) var fileCount: Int
    private(set) var children: [StorageNode]
    let modificationDate: Date?

    init(
        name: String,
        path: String,
        isDirectory: Bool,
        size: Int64 = 0,
        fileCount: Int = 0,
        modificationDate: Date? = nil,
        children: [StorageNode] = []
    ) {
        self.id = path
        self.name = name
        self.path = path
        self.isDirectory = isDirectory
        self.size = size
        self.fileCount = fileCount
        self.modificationDate = modificationDate
        self.children = children
    }

    /// This node's share of its parent, for the size bars.
    func fraction(of total: Int64) -> Double {
        guard total > 0 else { return 0 }
        return min(1, Double(size) / Double(total))
    }

    fileprivate func add(_ child: StorageNode) {
        children.append(child)
    }

    /// Depth-first roll-up: a folder's size is the sum of its children, and
    /// children are ordered biggest first, which is the order the user
    /// wants to read them in.
    fileprivate func rollUp() {
        guard isDirectory else {
            fileCount = 1
            return
        }
        var total: Int64 = 0
        var files = 0
        for child in children {
            child.rollUp()
            total += child.size
            files += child.fileCount
        }
        size = total
        fileCount = files
        children.sort { lhs, rhs in
            lhs.size == rhs.size ? lhs.name.localizedStandardCompare(rhs.name) == .orderedAscending : lhs.size > rhs.size
        }
    }
}

/// A finished (or partial) analysis.
@MainActor
struct StorageReport {
    let root: StorageNode
    /// The biggest individual files, already sorted, so "what single file is
    /// huge" is answered without drilling into the tree.
    let largestFiles: [StorageNode]
    let totalBytes: Int64
    let fileCount: Int
    let folderCount: Int
    /// True when this came from the existing offline index rather than a
    /// fresh walk — the figures are as old as the last index run.
    let fromIndex: Bool
    let indexedAt: Date?
    /// Set when the scan was stopped early; the totals are then a lower bound.
    let wasCancelled: Bool
}

/// Builds the tree from a flat list of entries. Accepts them in any order
/// and tolerates missing intermediate folders, which is normal: a provider
/// listing yields files whose parent folders were never listed themselves.
@MainActor
enum StorageTreeBuilder {

    struct Entry {
        let path: String
        let name: String
        let isDirectory: Bool
        let size: Int64
        let modificationDate: Date?
    }

    static func build(
        entries: [Entry],
        rootPath: String,
        rootName: String,
        largestFileLimit: Int = 250
    ) -> (root: StorageNode, largest: [StorageNode], files: Int, folders: Int) {
        let normalizedRoot = normalize(rootPath)
        let root = StorageNode(
            name: rootName,
            path: normalizedRoot.isEmpty ? "/" : normalizedRoot,
            isDirectory: true
        )

        var folders: [String: StorageNode] = [root.path: root]
        var fileNodes: [StorageNode] = []

        // Folders first, so a file never has to invent a parent that the
        // listing is about to provide properly.
        let sorted = entries.sorted { lhs, rhs in
            lhs.isDirectory == rhs.isDirectory ? lhs.path.count < rhs.path.count : lhs.isDirectory
        }

        for entry in sorted {
            let path = normalize(entry.path)
            guard path != root.path else { continue }
            let parentPath = parent(of: path, root: root.path)

            if entry.isDirectory {
                guard folders[path] == nil else { continue }
                let node = StorageNode(
                    name: entry.name,
                    path: path,
                    isDirectory: true,
                    modificationDate: entry.modificationDate
                )
                folders[path] = node
                ensureFolder(parentPath, in: &folders, root: root).add(node)
            } else {
                let node = StorageNode(
                    name: entry.name,
                    path: path,
                    isDirectory: false,
                    size: entry.size,
                    fileCount: 1,
                    modificationDate: entry.modificationDate
                )
                ensureFolder(parentPath, in: &folders, root: root).add(node)
                fileNodes.append(node)
            }
        }

        root.rollUp()

        let largest = fileNodes
            .sorted { $0.size > $1.size }
            .prefix(largestFileLimit)
        return (root, Array(largest), fileNodes.count, max(0, folders.count - 1))
    }

    /// Returns the folder node for `path`, creating placeholders for any
    /// missing level. A provider that lists files without their parent
    /// folders would otherwise lose entire branches.
    private static func ensureFolder(
        _ path: String,
        in folders: inout [String: StorageNode],
        root: StorageNode
    ) -> StorageNode {
        if let existing = folders[path] { return existing }
        let name = (path as NSString).lastPathComponent
        let node = StorageNode(name: name.isEmpty ? path : name, path: path, isDirectory: true)
        folders[path] = node
        let parentPath = parent(of: path, root: root.path)
        // Guard against a malformed path walking past the root forever.
        if parentPath == path {
            root.add(node)
        } else {
            ensureFolder(parentPath, in: &folders, root: root).add(node)
        }
        return node
    }

    private static func parent(of path: String, root: String) -> String {
        let parent = (path as NSString).deletingLastPathComponent
        if parent.isEmpty || parent == "." { return root }
        if parent == "/" { return root == "/" ? "/" : root }
        return parent.count < root.count ? root : parent
    }

    private static func normalize(_ path: String) -> String {
        var trimmed = path
        while trimmed.count > 1 && trimmed.hasSuffix("/") { trimmed.removeLast() }
        return trimmed
    }
}
