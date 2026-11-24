import Foundation

/// Which entries the compare window shows.
enum CompareFilter: String, CaseIterable, Hashable, Sendable {
    case all = "All"
    case differences = "Differences"
    case onlyLeft = "Only Left"
    case onlyRight = "Only Right"
    case identical = "Identical"

    func matches(_ status: FolderCompareStatus) -> Bool {
        switch self {
        case .all: return true
        case .differences: return status != .identical
        case .onlyLeft: return status == .onlyLeft
        case .onlyRight: return status == .onlyRight
        case .identical: return status == .identical
        }
    }

    /// How many of the counted entries this filter keeps.
    func matchCount(_ counts: CompareCounts) -> Int {
        switch self {
        case .all: return counts.different + counts.onlyLeft + counts.onlyRight + counts.identical
        case .differences: return counts.different + counts.onlyLeft + counts.onlyRight
        case .onlyLeft: return counts.onlyLeft
        case .onlyRight: return counts.onlyRight
        case .identical: return counts.identical
        }
    }
}

/// Entries underneath a tree node, by status.
struct CompareCounts: Hashable, Sendable {
    var different = 0
    var onlyLeft = 0
    var onlyRight = 0
    var identical = 0

    mutating func add(_ status: FolderCompareStatus) {
        switch status {
        case .differs: different += 1
        case .onlyLeft: onlyLeft += 1
        case .onlyRight: onlyRight += 1
        case .identical: identical += 1
        }
    }

    mutating func add(_ other: CompareCounts) {
        different += other.different
        onlyLeft += other.onlyLeft
        onlyRight += other.onlyRight
        identical += other.identical
    }
}

/// The flat `[FolderCompareEntry]` arranged as a tree, so the compare window
/// can render folders as collapsible groups. Synthetic folder nodes (with
/// `entry == nil`) exist when a path component is in nobody's compare entry
/// but has matching descendants — e.g. an identical parent folder
/// containing differing files.
///
/// A plain value, so it can be built off the main thread and handed over:
/// building it for a big comparison took long enough to hang the window.
/// Nodes live in one array and refer to their children by index; the root
/// is `nodes[0]`.
struct CompareTree: Hashable, Sendable {
    struct Node: Hashable, Sendable {
        let path: String
        let name: String
        let isDirectory: Bool
        let entry: FolderCompareEntry?
        /// Indexes into `nodes`, sorted folders first, then by name.
        let children: [Int]
        /// Entries underneath this node, by status; the node's own entry is
        /// not included. Counted once when the tree is built: the folder
        /// rows show it, and walking the subtree for each row on every
        /// redraw was what made big folders slow to open.
        let counts: CompareCounts
    }

    let nodes: [Node]

    var topLevel: [Int] { nodes[0].children }

    /// A node is shown if its own entry matches the filter or it holds a
    /// matching entry somewhere underneath, so folders containing matches
    /// stay visible.
    func isVisible(_ index: Int, filter: CompareFilter) -> Bool {
        let node = nodes[index]
        if let entry = node.entry, filter.matches(entry.status) { return true }
        return filter.matchCount(node.counts) > 0
    }

    /// A folder can be opened when something underneath it is shown.
    func isExpandable(_ index: Int, filter: CompareFilter) -> Bool {
        let node = nodes[index]
        return node.isDirectory && filter.matchCount(node.counts) > 0
    }

    /// The rows the window shows, top to bottom: every visible node whose
    /// ancestors are all expanded, with its depth. The window renders this
    /// list lazily, so opening a folder of 20,000 entries builds only the
    /// rows on screen rather than all of them at once.
    func visibleRows(filter: CompareFilter, expanded: Set<String>) -> [CompareRow] {
        var rows: [CompareRow] = []
        func visit(_ index: Int, depth: Int) {
            let node = nodes[index]
            let expandable = isExpandable(index, filter: filter)
            let isExpanded = expandable && expanded.contains(node.path)
            rows.append(CompareRow(
                index: index,
                path: node.path,
                depth: depth,
                isExpandable: expandable,
                isExpanded: isExpanded
            ))
            guard isExpanded else { return }
            for child in node.children where isVisible(child, filter: filter) {
                visit(child, depth: depth + 1)
            }
        }
        for child in topLevel where isVisible(child, filter: filter) {
            visit(child, depth: 0)
        }
        return rows
    }
}

/// One line of the compare window.
struct CompareRow: Identifiable, Hashable, Sendable {
    /// Index of the node in `CompareTree.nodes`.
    let index: Int
    /// The node's relative path, unique within a comparison.
    let path: String
    let depth: Int
    let isExpandable: Bool
    let isExpanded: Bool

    var id: String { path }
}

enum CompareTreeBuilder {
    /// Build a sorted tree from the flat compare result. Top-level
    /// children of the root are entries directly under the compare roots.
    static func build(from entries: [FolderCompareEntry]) -> CompareTree {
        var paths: [String] = [""]
        var names: [String] = [""]
        var isDirectory: [Bool] = [true]
        var nodeEntries: [FolderCompareEntry?] = [nil]
        var parents: [Int] = [-1]
        var children: [[Int]] = [[]]
        // Children by name, so building the tree doesn't rescan a folder's
        // siblings for every path component. A directory holding 10k files
        // otherwise costs ~50M string comparisons to build.
        var childByName: [[String: Int]] = [[:]]

        let sorted = entries.sorted { $0.relativePath < $1.relativePath }
        for entry in sorted {
            let parts = entry.relativePath.split(separator: "/").map(String.init)
            guard !parts.isEmpty else { continue }

            var node = 0
            var pathSoFar = ""
            for (i, part) in parts.enumerated() {
                let isLast = i == parts.count - 1
                pathSoFar = pathSoFar.isEmpty ? part : "\(pathSoFar)/\(part)"

                if let existing = childByName[node][part] {
                    if isLast { nodeEntries[existing] = entry }
                    node = existing
                } else {
                    let new = paths.count
                    paths.append(pathSoFar)
                    names.append(part)
                    isDirectory.append(isLast ? entry.isDirectory : true)
                    nodeEntries.append(isLast ? entry : nil)
                    parents.append(node)
                    children.append([])
                    childByName.append([:])
                    children[node].append(new)
                    childByName[node][part] = new
                    node = new
                }
            }
        }

        for index in children.indices where children[index].count > 1 {
            children[index].sort { lhs, rhs in
                if isDirectory[lhs] != isDirectory[rhs] {
                    return isDirectory[lhs] && !isDirectory[rhs]
                }
                return names[lhs].localizedStandardCompare(names[rhs]) == .orderedAscending
            }
        }

        // A node is always created after its parent, so walking the indexes
        // backwards finishes every node's counts before they are added to
        // its parent's.
        var counts = [CompareCounts](repeating: CompareCounts(), count: paths.count)
        for index in stride(from: paths.count - 1, through: 1, by: -1) {
            let parent = parents[index]
            if let entry = nodeEntries[index] { counts[parent].add(entry.status) }
            counts[parent].add(counts[index])
        }

        let nodes = paths.indices.map { index in
            CompareTree.Node(
                path: paths[index],
                name: names[index],
                isDirectory: isDirectory[index],
                entry: nodeEntries[index],
                children: children[index],
                counts: counts[index]
            )
        }
        return CompareTree(nodes: nodes)
    }
}

/// A finished comparison: the flat result and the tree the window shows.
struct FolderCompareOutcome: Sendable {
    let result: FolderComparisonResult
    let tree: CompareTree

    /// Compares the two listings and builds the tree on a background
    /// thread. Both are linear or worse in the number of entries and hung
    /// the compare window when they ran on main, after "Reading folders…"
    /// and again at every "Compare Date" toggle. Returns nil when the
    /// calling task is cancelled before the work is done.
    static func make(
        left: [SyncEntry],
        right: [SyncEntry],
        compareDates: Bool
    ) async -> FolderCompareOutcome? {
        let work = Task.detached(priority: .userInitiated) { () -> FolderCompareOutcome? in
            let result = FolderComparison.compare(left: left, right: right, compareDates: compareDates)
            if Task.isCancelled { return nil }
            let tree = CompareTreeBuilder.build(from: result.entries)
            if Task.isCancelled { return nil }
            return FolderCompareOutcome(result: result, tree: tree)
        }
        return await withTaskCancellationHandler {
            await work.value
        } onCancel: {
            work.cancel()
        }
    }
}
