import Foundation

/// Tree node built from the flat `[FolderCompareEntry]` so the compare
/// window can render folders as collapsible groups. Synthetic folder
/// nodes (with `entry == nil`) exist when a path component is in nobody's
/// compare entry but has matching descendants — e.g. an identical parent
/// folder containing differing files.
final class CompareTreeNode: Identifiable {
    let id = UUID()
    let path: String
    let name: String
    let isDirectory: Bool
    var entry: FolderCompareEntry?
    var children: [CompareTreeNode]

    init(path: String, name: String, isDirectory: Bool, entry: FolderCompareEntry? = nil, children: [CompareTreeNode] = []) {
        self.path = path
        self.name = name
        self.isDirectory = isDirectory
        self.entry = entry
        self.children = children
    }

    /// Aggregated counts of *entries* underneath this node, by status.
    /// Synthetic nodes contribute nothing themselves.
    func descendantCounts() -> (different: Int, onlyLeft: Int, onlyRight: Int, identical: Int) {
        var d = 0, l = 0, r = 0, s = 0
        func walk(_ n: CompareTreeNode) {
            if let e = n.entry {
                switch e.status {
                case .differs: d += 1
                case .onlyLeft: l += 1
                case .onlyRight: r += 1
                case .identical: s += 1
                }
            }
            for c in n.children { walk(c) }
        }
        for c in children { walk(c) }
        return (d, l, r, s)
    }
}

enum CompareTreeBuilder {
    /// Build a sorted tree from the flat compare result. Top-level
    /// children of `root` are entries directly under the compare roots.
    static func build(from entries: [FolderCompareEntry]) -> CompareTreeNode {
        let root = CompareTreeNode(path: "", name: "", isDirectory: true)
        let sorted = entries.sorted { $0.relativePath < $1.relativePath }

        for entry in sorted {
            let parts = entry.relativePath.split(separator: "/").map(String.init)
            guard !parts.isEmpty else { continue }

            var node = root
            var pathSoFar = ""
            for (i, p) in parts.enumerated() {
                let isLast = i == parts.count - 1
                pathSoFar = pathSoFar.isEmpty ? p : "\(pathSoFar)/\(p)"

                if let existing = node.children.first(where: { $0.name == p }) {
                    if isLast { existing.entry = entry }
                    node = existing
                } else {
                    let new = CompareTreeNode(
                        path: pathSoFar,
                        name: p,
                        isDirectory: isLast ? entry.isDirectory : true,
                        entry: isLast ? entry : nil
                    )
                    node.children.append(new)
                    node = new
                }
            }
        }

        sortRecursively(root)
        return root
    }

    private static func sortRecursively(_ node: CompareTreeNode) {
        node.children.sort { lhs, rhs in
            if lhs.isDirectory != rhs.isDirectory {
                return lhs.isDirectory && !rhs.isDirectory
            }
            return lhs.name.localizedStandardCompare(rhs.name) == .orderedAscending
        }
        for c in node.children { sortRecursively(c) }
    }
}
