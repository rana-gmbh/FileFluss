import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

@Suite("FileManagerViewModel Tests")
@MainActor
struct FileManagerViewModelTests {

    @Test("Initial state points to home directory")
    func initialState() {
        let vm = FileManagerViewModel()
        let home = FileManager.default.homeDirectoryForCurrentUser
        #expect(vm.currentDirectory == home)
        #expect(vm.sortOrder == .name)
        #expect(vm.sortAscending)
        // Follows the "Show hidden files by default" setting.
        #expect(vm.showHiddenFiles == UserDefaults.standard.bool(forKey: "showHiddenFiles"))
        #expect(vm.searchText.isEmpty)
        #expect(!vm.canGoBack)
        #expect(!vm.canGoForward)
    }

    @Test("filteredItems memoization invalidates on every input change")
    func filteredItemsCacheInvalidation() {
        let vm = FileManagerViewModel()
        vm.items = [
            FileItem(url: URL(fileURLWithPath: "/tmp/banana.txt")),
            FileItem(url: URL(fileURLWithPath: "/tmp/apple.txt")),
        ]

        // Default: name ascending.
        #expect(vm.filteredItems.map(\.name) == ["apple.txt", "banana.txt"])
        // Repeat access returns the same (cached) ordering.
        #expect(vm.filteredItems.map(\.name) == ["apple.txt", "banana.txt"])

        // Flipping sort direction must invalidate the cache.
        vm.sortAscending = false
        #expect(vm.filteredItems.map(\.name) == ["banana.txt", "apple.txt"])

        // Search text must invalidate the cache.
        vm.searchText = "app"
        #expect(vm.filteredItems.map(\.name) == ["apple.txt"])

        // Clearing search restores the full (still descending) listing.
        vm.searchText = ""
        #expect(vm.filteredItems.map(\.name) == ["banana.txt", "apple.txt"])

        // Replacing items must invalidate the cache.
        vm.items = [FileItem(url: URL(fileURLWithPath: "/tmp/cherry.txt"))]
        #expect(vm.filteredItems.map(\.name) == ["cherry.txt"])
    }

    @Test("Load directory populates items")
    func loadDirectory() async {
        let vm = FileManagerViewModel()
        let tmpDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussVMTest_\(UUID())")
        try! FileManager.default.createDirectory(at: tmpDir, withIntermediateDirectories: true)
        try! Data("test".utf8).write(to: tmpDir.appendingPathComponent("a.txt"))
        try! Data("test".utf8).write(to: tmpDir.appendingPathComponent("b.txt"))
        defer { try? FileManager.default.removeItem(at: tmpDir) }

        await vm.loadDirectory(at: tmpDir)

        #expect(vm.currentDirectory == tmpDir)
        #expect(vm.items.count == 2)
        #expect(vm.error == nil)
    }

    @Test("Navigate to directory updates current directory and history")
    func navigation() async {
        let vm = FileManagerViewModel()
        let tmpDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussNavTest_\(UUID())")
        let subDir = tmpDir.appendingPathComponent("sub")
        try! FileManager.default.createDirectory(at: subDir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tmpDir) }

        await vm.navigateTo(tmpDir)
        #expect(vm.currentDirectory == tmpDir)

        await vm.navigateTo(subDir)
        #expect(vm.currentDirectory == subDir)
        #expect(vm.canGoBack)
    }

    @Test("Navigate back and forward through history")
    func backForward() async {
        let vm = FileManagerViewModel()
        let base = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussHistTest_\(UUID())")
        let dirA = base.appendingPathComponent("A")
        let dirB = base.appendingPathComponent("B")
        try! FileManager.default.createDirectory(at: dirA, withIntermediateDirectories: true)
        try! FileManager.default.createDirectory(at: dirB, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: base) }

        await vm.navigateTo(dirA)
        await vm.navigateTo(dirB)
        #expect(vm.currentDirectory == dirB)

        await vm.navigateBack()
        #expect(vm.currentDirectory == dirA)
        #expect(vm.canGoForward)

        await vm.navigateForward()
        #expect(vm.currentDirectory == dirB)
    }

    @Test("Navigate up goes to parent directory")
    func navigateUp() async {
        let vm = FileManagerViewModel()
        let base = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussUpTest_\(UUID())")
        let sub = base.appendingPathComponent("child")
        try! FileManager.default.createDirectory(at: sub, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: base) }

        await vm.navigateTo(sub)
        #expect(vm.currentDirectory == sub)

        await vm.navigateUp()
        #expect(vm.currentDirectory.standardizedFileURL == base.standardizedFileURL)
    }

    @Test("Search filters items by name")
    func searchFilter() async {
        let vm = FileManagerViewModel()
        let tmpDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussSearchTest_\(UUID())")
        try! FileManager.default.createDirectory(at: tmpDir, withIntermediateDirectories: true)
        try! Data().write(to: tmpDir.appendingPathComponent("report.pdf"))
        try! Data().write(to: tmpDir.appendingPathComponent("photo.jpg"))
        try! Data().write(to: tmpDir.appendingPathComponent("notes.txt"))
        defer { try? FileManager.default.removeItem(at: tmpDir) }

        await vm.loadDirectory(at: tmpDir)
        #expect(vm.filteredItems.count == 3)

        vm.searchText = "report"
        #expect(vm.filteredItems.count == 1)
        #expect(vm.filteredItems[0].name == "report.pdf")

        vm.searchText = ""
        #expect(vm.filteredItems.count == 3)
    }

    @Test("Sort order affects item ordering")
    func sorting() async {
        let vm = FileManagerViewModel()
        let tmpDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussSortTest_\(UUID())")
        try! FileManager.default.createDirectory(at: tmpDir, withIntermediateDirectories: true)
        try! Data(repeating: 0, count: 100).write(to: tmpDir.appendingPathComponent("big.txt"))
        try! Data(repeating: 0, count: 1).write(to: tmpDir.appendingPathComponent("small.txt"))
        defer { try? FileManager.default.removeItem(at: tmpDir) }

        await vm.loadDirectory(at: tmpDir)

        vm.sortOrder = .size
        vm.sortAscending = true
        #expect(vm.filteredItems.first?.name == "small.txt")

        vm.sortAscending = false
        #expect(vm.filteredItems.first?.name == "big.txt")
    }

    @Test("Create new folder and verify it appears")
    func createFolder() async {
        let vm = FileManagerViewModel()
        let tmpDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussCreateTest_\(UUID())")
        try! FileManager.default.createDirectory(at: tmpDir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tmpDir) }

        await vm.loadDirectory(at: tmpDir)
        #expect(vm.items.isEmpty)

        await vm.createNewFolder(named: "NewFolder")

        #expect(vm.items.count == 1)
        #expect(vm.items[0].name == "NewFolder")
        #expect(vm.items[0].isDirectory)
    }

    @Test("Delete selected items removes them")
    func deleteItems() async {
        let vm = FileManagerViewModel()
        let tmpDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussDeleteTest_\(UUID())")
        try! FileManager.default.createDirectory(at: tmpDir, withIntermediateDirectories: true)
        let fileURL = tmpDir.appendingPathComponent("deleteme.txt")
        try! Data("bye".utf8).write(to: fileURL)
        defer { try? FileManager.default.removeItem(at: tmpDir) }

        await vm.loadDirectory(at: tmpDir)
        #expect(vm.items.count == 1)

        vm.selectedItemIDs = Set([vm.items[0].id])
        await vm.deleteSelectedItems()

        #expect(vm.items.isEmpty)
        #expect(!FileManager.default.fileExists(atPath: fileURL.path()))
    }

    @Test("A folder is never copied or moved into itself or a folder inside it")
    func copyIntoOwnSubfolder() async throws {
        let root = try temporaryFolder()
        defer { try? FileManager.default.removeItem(at: root) }
        let a = root.appendingPathComponent("A", isDirectory: true)
        let b = a.appendingPathComponent("B", isDirectory: true)
        try write("x", to: a.appendingPathComponent("file.txt"))
        try FileManager.default.createDirectory(at: b, withIntermediateDirectories: true)
        let item = FileItem(url: a)

        // The last one names A/B through /private, as a panel may show it.
        for target in [b, a, URL(fileURLWithPath: "/private" + b.standardizedFileURL.path, isDirectory: true)] {
            let vm = FileManagerViewModel()
            let copy = TransferProgress(operation: "Copying", totalItems: 1)
            await vm.performCopy(items: [item], to: target, progress: copy)
            #expect(copy.failureCount == 1)
            #expect(vm.error != nil)

            let move = TransferProgress(operation: "Moving", totalItems: 1)
            await vm.performMove(items: [item], to: target, progress: move)
            #expect(move.failureCount == 1)
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: b.path).isEmpty)
        #expect(try FileManager.default.contentsOfDirectory(atPath: a.path).sorted() == ["B", "file.txt"])
    }

    @Test("A folder can still be copied beside itself and into a sibling")
    func copyBesideItself() {
        let parent = URL(fileURLWithPath: "/Users/someone/Docs", isDirectory: true)
        let a = parent.appendingPathComponent("A", isDirectory: true)
        #expect(FileManagerViewModel.isSameOrInside(a, of: a))
        #expect(FileManagerViewModel.isSameOrInside(a.appendingPathComponent("B/C"), of: a))
        #expect(!FileManagerViewModel.isSameOrInside(parent, of: a))
        #expect(!FileManagerViewModel.isSameOrInside(parent.appendingPathComponent("AB"), of: a))
    }

    @Test("Loading nonexistent directory sets error")
    func loadBadDirectory() async {
        let vm = FileManagerViewModel()
        let bad = URL(filePath: "/nonexistent_path_\(UUID())")

        await vm.loadDirectory(at: bad)

        #expect(vm.error != nil)
    }
}
