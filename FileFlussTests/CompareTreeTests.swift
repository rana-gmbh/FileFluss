import Testing
import Foundation
@testable import FileFluss

/// The compare window's tree: built off the main thread, with each folder's
/// counts taken once at build time, and shown as a flat list of the rows
/// whose folders are open.
@Suite("Compare tree")
struct CompareTreeTests {

    private static let date = Date(timeIntervalSince1970: 1_700_000_000)

    private func entry(_ path: String, dir: Bool = false, size: Int64 = 1, offset: TimeInterval = 0) -> SyncEntry {
        SyncEntry(relativePath: path, isDirectory: dir, size: size, modificationDate: Self.date.addingTimeInterval(offset))
    }

    /// A comparison with every status, nested folders, a folder that is in
    /// nobody's listing (only its files are), and names that sort
    /// differently by plain and by Finder order.
    private func sample() -> (left: [SyncEntry], right: [SyncEntry]) {
        let left = [
            entry("Docs", dir: true),
            entry("Docs/a.txt"),
            entry("Docs/b.txt", size: 5),
            entry("Docs/Old", dir: true),
            entry("Docs/Old/x.txt"),
            entry("file10.txt"),
            entry("file2.txt"),
            entry("Photos", dir: true),
            entry("Photos/same.jpg"),
            entry("Synthetic/deep/only-left.bin"),
            entry("dated.txt", offset: 0),
        ]
        let right = [
            entry("Docs", dir: true),
            entry("Docs/a.txt"),
            entry("Docs/b.txt", size: 9),
            entry("Docs/New", dir: true),
            entry("Docs/New/y.txt"),
            entry("file10.txt"),
            entry("Photos", dir: true),
            entry("Photos/same.jpg"),
            entry("Synthetic/deep/only-right.bin"),
            entry("dated.txt", offset: 3600),
        ]
        return (left, right)
    }

    private func tree(compareDates: Bool = false) -> CompareTree {
        let (l, r) = sample()
        let result = FolderComparison.compare(left: l, right: r, compareDates: compareDates)
        return CompareTreeBuilder.build(from: result.entries)
    }

    private func index(of path: String, in tree: CompareTree) -> Int {
        tree.nodes.firstIndex { $0.path == path }!
    }

    /// The counts as the old code worked them out: a full walk of the
    /// subtree, the node's own entry left out.
    private func walkedCounts(_ index: Int, in tree: CompareTree) -> CompareCounts {
        var counts = CompareCounts()
        func walk(_ i: Int) {
            if let e = tree.nodes[i].entry { counts.add(e.status) }
            for c in tree.nodes[i].children { walk(c) }
        }
        for c in tree.nodes[index].children { walk(c) }
        return counts
    }

    /// Visibility as the old code worked it out: the node's own entry
    /// matches, or a descendant is visible.
    private func walkedVisible(_ index: Int, in tree: CompareTree, filter: CompareFilter) -> Bool {
        if let e = tree.nodes[index].entry, filter.matches(e.status) { return true }
        return tree.nodes[index].children.contains { walkedVisible($0, in: tree, filter: filter) }
    }

    @Test("folders come first, names in Finder order, synthetic folders are made")
    func structure() {
        let t = tree()
        let top = t.topLevel.map { t.nodes[$0].name }
        #expect(top == ["Docs", "Photos", "Synthetic", "dated.txt", "file2.txt", "file10.txt"])

        let synthetic = t.nodes[index(of: "Synthetic", in: t)]
        #expect(synthetic.entry == nil)
        #expect(synthetic.isDirectory)
        let deep = t.nodes[index(of: "Synthetic/deep", in: t)]
        #expect(deep.children.map { t.nodes[$0].name } == ["only-left.bin", "only-right.bin"])

        let docs = t.nodes[index(of: "Docs", in: t)]
        #expect(docs.children.map { t.nodes[$0].name } == ["New", "Old", "a.txt", "b.txt"])
        // Every path appears once.
        #expect(Set(t.nodes.map(\.path)).count == t.nodes.count)
    }

    @Test("counts cached at build time equal a full walk of each subtree")
    func cachedCountsMatchWalk() {
        for compareDates in [false, true] {
            let t = tree(compareDates: compareDates)
            for i in t.nodes.indices {
                #expect(t.nodes[i].counts == walkedCounts(i, in: t), "node \(t.nodes[i].path)")
            }
        }
        let t = tree()
        #expect(t.nodes[index(of: "Docs", in: t)].counts
                == CompareCounts(different: 1, onlyLeft: 2, onlyRight: 2, identical: 1))
    }

    @Test("counts match a walk on a large generated comparison")
    func cachedCountsMatchWalkLarge() {
        var left: [SyncEntry] = []
        var right: [SyncEntry] = []
        for folder in 0..<20 {
            left.append(entry("f\(folder)", dir: true))
            right.append(entry("f\(folder)", dir: true))
            for file in 0..<200 {
                let path = "f\(folder)/sub\(file % 7)/file\(file).dat"
                switch (folder + file) % 4 {
                case 0: left.append(entry(path)); right.append(entry(path))
                case 1: left.append(entry(path))
                case 2: right.append(entry(path))
                default: left.append(entry(path, size: 1)); right.append(entry(path, size: 2))
                }
            }
        }
        let result = FolderComparison.compare(left: left, right: right)
        let t = CompareTreeBuilder.build(from: result.entries)
        for i in t.nodes.indices {
            #expect(t.nodes[i].counts == walkedCounts(i, in: t))
        }
        let root = t.nodes[0].counts
        #expect(root.identical == result.identicalCount)
        #expect(root.onlyLeft == result.onlyLeftCount)
        #expect(root.onlyRight == result.onlyRightCount)
        #expect(root.different == result.differsCount)
    }

    @Test("visibility from cached counts equals the subtree walk, for every filter")
    func visibilityMatchesWalk() {
        let t = tree(compareDates: true)
        for filter in CompareFilter.allCases {
            for i in t.nodes.indices.dropFirst() {
                #expect(t.isVisible(i, filter: filter) == walkedVisible(i, in: t, filter: filter),
                        "\(filter) \(t.nodes[i].path)")
            }
        }
    }

    @Test("only the rows of open folders are listed, in tree order with depth")
    func visibleRows() {
        let t = tree()

        let closed = t.visibleRows(filter: .all, expanded: [])
        #expect(closed.map(\.path) == ["Docs", "Photos", "Synthetic", "dated.txt", "file2.txt", "file10.txt"])
        #expect(closed.allSatisfy { $0.depth == 0 && !$0.isExpanded })

        let docsOpen = t.visibleRows(filter: .all, expanded: ["Docs"])
        #expect(docsOpen.map(\.path) == [
            "Docs", "Docs/New", "Docs/Old", "Docs/a.txt", "Docs/b.txt",
            "Photos", "Synthetic", "dated.txt", "file2.txt", "file10.txt",
        ])
        #expect(docsOpen[0].isExpanded)
        #expect(docsOpen[1].depth == 1)

        // A folder inside a closed one stays hidden, however it was left.
        let innerOnly = t.visibleRows(filter: .all, expanded: ["Synthetic/deep"])
        #expect(innerOnly.map(\.path) == closed.map(\.path))

        let nested = t.visibleRows(filter: .all, expanded: ["Synthetic", "Synthetic/deep"])
        let paths = nested.map(\.path)
        let deepIndex = paths.firstIndex(of: "Synthetic/deep")!
        #expect(paths[deepIndex + 1] == "Synthetic/deep/only-left.bin")
        #expect(nested[deepIndex + 1].depth == 2)
    }

    @Test("filters hide rows and folders with nothing to show")
    func filteredRows() {
        let t = tree()
        let rows = t.visibleRows(filter: .differences, expanded: ["Docs", "Photos"])
        // Photos holds only an identical file, so it is gone; the identical
        // Docs/a.txt is hidden inside the open Docs.
        #expect(rows.map(\.path) == ["Docs", "Docs/New", "Docs/Old", "Docs/b.txt", "Synthetic", "file2.txt"])

        let identical = t.visibleRows(filter: .identical, expanded: ["Docs"])
        #expect(identical.map(\.path) == ["Docs", "Docs/a.txt", "Photos", "dated.txt", "file10.txt"])
        // Docs/a.txt is a file: not a folder that can be opened.
        #expect(identical.first { $0.path == "Docs/a.txt" }?.isExpandable == false)
    }

    @Test("the background build gives the same result and tree as building in place")
    func backgroundBuildMatches() async throws {
        let (l, r) = sample()
        for compareDates in [false, true] {
            let outcome = try #require(await FolderCompareOutcome.make(left: l, right: r, compareDates: compareDates))
            let direct = FolderComparison.compare(left: l, right: r, compareDates: compareDates)
            #expect(outcome.result.entries == direct.entries)
            #expect(outcome.result.differsCount == direct.differsCount)
            #expect(outcome.tree == CompareTreeBuilder.build(from: direct.entries))
        }
    }

    @Test("Compare Date changes the outcome")
    func compareDatesChangesOutcome() async throws {
        let (l, r) = sample()
        let without = try #require(await FolderCompareOutcome.make(left: l, right: r, compareDates: false))
        let with = try #require(await FolderCompareOutcome.make(left: l, right: r, compareDates: true))
        #expect(with.result.differsCount == without.result.differsCount + 1)
        #expect(with.tree.nodes.first { $0.path == "dated.txt" }?.entry?.dateDiffersOnly == true)
    }
}
