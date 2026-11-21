import Testing
import Foundation
import AppKit
import FileFlussCore
@testable import FileFluss

/// A scratch folder that is removed when the test is done.
private final class Scratch {
    let url: URL

    init() {
        url = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussTagTest_\(UUID().uuidString)", isDirectory: true)
        try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    }

    deinit {
        try? FileManager.default.removeItem(at: url)
    }

    func file(_ name: String) -> URL {
        let file = url.appendingPathComponent(name)
        try! FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try! Data("x".utf8).write(to: file)
        return file
    }

    func folder(_ name: String) -> URL {
        let folder = url.appendingPathComponent(name, isDirectory: true)
        try! FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }
}

private let red = FileTag(name: "Red", color: .red)
private let blue = FileTag(name: "Blue", color: .blue)
private let green = FileTag(name: "Green", color: .green)
private let project = FileTag(name: "Projekt X", color: .purple)
private let plain = FileTag(name: "Steuer 2026", color: .none)

/// What the system itself reports for a file, read apart from FileFluss's
/// own reader: this is what Finder goes by.
private func systemTagNames(_ url: URL) -> [String] {
    var fresh = URL(fileURLWithPath: url.path)
    fresh.removeAllCachedResourceValues()
    return (try? fresh.resourceValues(forKeys: [.tagNamesKey]))?.tagNames ?? []
}

private func systemLabel(_ url: URL) -> Int {
    var fresh = URL(fileURLWithPath: url.path)
    fresh.removeAllCachedResourceValues()
    return (try? fresh.resourceValues(forKeys: [.labelNumberKey]))?.labelNumber ?? -1
}

// MARK: - Model

@Suite("Tag model")
struct TagModelTests {

    @Test("Finder's stored form is read and written back unchanged")
    func encoding() {
        #expect(FinderTags.decode("Red\n6") == red)
        #expect(FinderTags.decode("Projekt X\n3") == project)
        #expect(FinderTags.encode(red) == "Red\n6")
        #expect(FinderTags.encode(plain) == "Steuer 2026\n0")
    }

    @Test("A tag without a colour index, or with an unknown one, has no colour")
    func encodingWithoutColour() {
        #expect(FinderTags.decode("Steuer 2026") == plain)
        #expect(FinderTags.decode("Odd\n42") == FileTag(name: "Odd", color: .none))
        #expect(FinderTags.decode("") == nil)
        #expect(FinderTags.decode("\n6") == nil)
    }

    @Test("Colour indexes are Finder's")
    func colourIndexes() {
        #expect(TagColor.gray.rawValue == 1)
        #expect(TagColor.green.rawValue == 2)
        #expect(TagColor.purple.rawValue == 3)
        #expect(TagColor.blue.rawValue == 4)
        #expect(TagColor.yellow.rawValue == 5)
        #expect(TagColor.red.rawValue == 6)
        #expect(TagColor.orange.rawValue == 7)
        #expect(TagColor.menuOrder == [.red, .orange, .yellow, .green, .blue, .purple, .gray])
    }

    @Test("Dots leave out tags without a colour; a folder takes its last colour")
    func dotsAndTint() {
        let tags = [red, plain, blue]
        #expect(tags.dotColors == [.red, .blue])
        #expect(tags.folderTint == .blue)
        #expect([plain].folderTint == nil)
        #expect([FileTag]().dotColors.isEmpty)
    }

    @Test("Adding puts the tag last and never twice")
    func add() {
        #expect(TagChange.add(blue).applied(to: [red]) == [red, blue])
        #expect(TagChange.add(red).applied(to: [red, blue]) == [red, blue])
    }

    @Test("Removing goes by name and leaves the others alone")
    func remove() {
        #expect(TagChange.remove(name: "Red").applied(to: [red, project, blue]) == [project, blue])
        #expect(TagChange.remove(name: "Nope").applied(to: [red]) == [red])
        #expect(TagChange.removeAll.applied(to: [red, project]) == [])
    }

    @Test("The menu shows whether all, some or none of the selection has a tag")
    func menuPresence() {
        let rows = TagMenuModel.rows(
            standard: [red, blue, green],
            selection: [[red, blue], [red], [red]]
        )
        #expect(rows.map(\.tag) == [red, blue, green])
        #expect(rows.map(\.presence) == [.all, .some, .none])
    }

    @Test("Choosing a tag every item has removes it; otherwise it is added")
    func menuChange() {
        let rows = TagMenuModel.rows(standard: [red, blue, green], selection: [[red, blue], [red]])
        #expect(rows[0].change == .remove(name: "Red"))
        #expect(rows[1].change == .add(blue))
        #expect(rows[2].change == .add(green))
    }

    @Test("Tags made in Finder are listed after the standard ones, once each")
    func menuCustomTags() {
        let rows = TagMenuModel.rows(standard: [red, blue], selection: [[project, red], [project, plain]])
        #expect(rows.map(\.tag) == [red, blue, project, plain])
        #expect(rows[2].presence == .all)
        #expect(rows[3].presence == .some)
        #expect(TagMenuModel.hasAnyTag([[], [plain]]))
        #expect(!TagMenuModel.hasAnyTag([[], []]))
    }
}

// MARK: - Finder tags on disk

@Suite("Finder tags")
struct FinderTagsTests {

    @Test("Tags written to a file are read back with their colours and order")
    func roundTrip() throws {
        let scratch = Scratch()
        let file = scratch.file("a.txt")
        try FinderTags.write([project, red, plain], to: file)
        #expect(FinderTags.read(at: file) == [project, red, plain])
    }

    @Test("A folder takes tags like a file")
    func folder() throws {
        let scratch = Scratch()
        let folder = scratch.folder("Photos")
        try FinderTags.write([green], to: folder)
        #expect(FinderTags.read(at: folder) == [green])
    }

    @Test("The system sees what FileFluss wrote, which is what Finder shows")
    func systemAgrees() throws {
        let scratch = Scratch()
        let file = scratch.file("a.txt")
        try FinderTags.write([red, blue], to: file)
        #expect(systemTagNames(file) == ["Red", "Blue"])
        // The legacy label follows the last coloured tag.
        #expect(systemLabel(file) == TagColor.blue.rawValue)
    }

    @Test("FileFluss sees a tag the system wrote, as Finder would write it")
    func readsSystemTags() throws {
        let scratch = Scratch()
        let file = scratch.file("a.txt")
        try (file as NSURL).setResourceValue(["Red"], forKey: .tagNamesKey)
        #expect(FinderTags.read(at: file) == [red])
    }

    @Test("Clearing leaves nothing behind for the system to show")
    func clear() throws {
        let scratch = Scratch()
        let file = scratch.file("a.txt")
        // The system's way of tagging also sets the legacy label; a file
        // cleared without resetting it would go on showing as red.
        try (file as NSURL).setResourceValue(["Red"], forKey: .tagNamesKey)
        try FinderTags.write([], to: file)
        #expect(FinderTags.read(at: file).isEmpty)
        #expect(systemTagNames(file).isEmpty)
        #expect(systemLabel(file) == 0)
    }

    @Test("Clearing a file that has no tags is not an error")
    func clearUntagged() throws {
        let scratch = Scratch()
        let file = scratch.file("a.txt")
        try FinderTags.write([], to: file)
        #expect(FinderTags.read(at: file).isEmpty)
    }

    @Test("A change is applied to what the file has, keeping its other tags")
    func apply() throws {
        let scratch = Scratch()
        let file = scratch.file("a.txt")
        try FinderTags.write([project], to: file)
        #expect(try FinderTags.apply(.add(red), to: file) == [project, red])
        #expect(try FinderTags.apply(.remove(name: "Projekt X"), to: file) == [red])
        #expect(FinderTags.read(at: file) == [red])
    }

    @Test("A file that can't be read simply has no tags")
    func missingFile() {
        #expect(FinderTags.read(at: URL(fileURLWithPath: "/nonexistent/\(UUID().uuidString)")).isEmpty)
    }

    @Test("Writing to a file that isn't there is reported")
    func writeFailure() {
        let missing = URL(fileURLWithPath: "/nonexistent/\(UUID().uuidString)")
        #expect(throws: FinderTags.WriteError.self) {
            try FinderTags.write([red], to: missing)
        }
    }

    @Test("Reading a folder's worth returns only the tagged files")
    func readAll() async throws {
        let scratch = Scratch()
        let a = scratch.file("a.txt"), b = scratch.file("b.txt"), c = scratch.file("c.txt")
        try FinderTags.write([red], to: a)
        try FinderTags.write([blue, green], to: c)
        let found = await FinderTags.readAll([a, b, c].map { (key: $0.lastPathComponent, path: $0.path) })
        #expect(found == ["a.txt": [red], "c.txt": [blue, green]])
    }

    @Test("A long list of tags is read in full")
    func manyTags() throws {
        let scratch = Scratch()
        let file = scratch.file("a.txt")
        let many = (0..<60).map { FileTag(name: "A rather long tag name number \($0)", color: .green) }
        try FinderTags.write(many, to: file)
        #expect(FinderTags.read(at: file) == many)
    }

    @Test("One change to several files reports the one that failed and does the rest")
    func applyAll() async throws {
        let scratch = Scratch()
        let good = scratch.file("good.txt")
        let missing = scratch.url.appendingPathComponent("gone.txt")
        let outcome = await FinderTags.applyAll(.add(red), to: [(key: "gone", url: missing), (key: "good", url: good)])
        #expect(outcome.tags["good"] == [red])
        #expect(outcome.tags["gone"] == [])
        #expect(outcome.failure?.name == "gone.txt")
        #expect(FinderTags.read(at: good) == [red])
    }

    @Test("A copy made on this Mac keeps its tags")
    func copyKeepsTags() async throws {
        let scratch = Scratch()
        let file = scratch.file("a.txt")
        try FinderTags.write([red, project], to: file)
        let copy = scratch.url.appendingPathComponent("b.txt")
        try await FileSystemService.shared.copyItem(from: file, to: copy)
        #expect(FinderTags.read(at: copy) == [red, project])
    }
}

// MARK: - The store for cloud accounts

@Suite("Tag store")
struct TagStoreTests {
    private let account = UUID()

    @Test("Tags set on a path are returned for it, and gone when cleared")
    func setAndGet() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.url)
        await store.setTags([red, blue], account: account, path: "/Docs/a.pdf")
        #expect(await store.tags(account: account, path: "/Docs/a.pdf") == [red, blue])
        #expect(await store.tags(account: account, path: "/Docs/b.pdf").isEmpty)
        await store.setTags([], account: account, path: "/Docs/a.pdf")
        #expect(await store.tags(account: account, path: "/Docs/a.pdf").isEmpty)
    }

    @Test("Accounts are kept apart")
    func accountsAreSeparate() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.url)
        let other = UUID()
        await store.setTags([red], account: account, path: "/a")
        #expect(await store.tags(account: other, path: "/a").isEmpty)
    }

    @Test("Tags survive a restart")
    func persistence() async {
        let scratch = Scratch()
        let first = TagStore(directory: scratch.url)
        await first.setTags([project, red], account: account, path: "/Docs/a.pdf")
        await first.flush()

        let second = TagStore(directory: scratch.url)
        #expect(await second.tags(account: account, path: "/Docs/a.pdf") == [project, red])
    }

    @Test("Changes are written without being asked, a moment later")
    func savesByItself() async throws {
        let scratch = Scratch()
        let first = TagStore(directory: scratch.url)
        await first.setTags([red], account: account, path: "/a")
        try await Task.sleep(for: .milliseconds(1200))

        let second = TagStore(directory: scratch.url)
        #expect(await second.tags(account: account, path: "/a") == [red])
    }

    @Test("A rename takes the item's tags and those of everything inside it")
    func moveSubtree() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.url)
        await store.setTags([red], account: account, path: "/Old")
        await store.setTags([blue], account: account, path: "/Old/inner/a.txt")
        await store.setTags([green], account: account, path: "/Older/untouched.txt")

        await store.moveSubtree(account: account, from: "/Old", to: "/New")

        #expect(await store.tags(account: account, path: "/New") == [red])
        #expect(await store.tags(account: account, path: "/New/inner/a.txt") == [blue])
        #expect(await store.tags(account: account, path: "/Old").isEmpty)
        #expect(await store.tags(account: account, path: "/Old/inner/a.txt").isEmpty)
        // A sibling whose name merely starts the same way is not inside it.
        #expect(await store.tags(account: account, path: "/Older/untouched.txt") == [green])
    }

    @Test("A move onto an item replaces that item's tags")
    func moveReplacesDestination() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.url)
        await store.setTags([red], account: account, path: "/a.txt")
        await store.setTags([blue], account: account, path: "/b.txt")
        await store.moveSubtree(account: account, from: "/a.txt", to: "/b.txt")
        #expect(await store.tags(account: account, path: "/b.txt") == [red])
        #expect(await store.tags(account: account, path: "/a.txt").isEmpty)
    }

    @Test("A copy leaves the original tagged, within an account and across two")
    func copySubtree() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.url)
        let other = UUID()
        await store.setTags([red], account: account, path: "/Folder")
        await store.setTags([blue], account: account, path: "/Folder/a.txt")

        await store.copySubtree(fromAccount: account, path: "/Folder", toAccount: account, to: "/Copy")
        await store.copySubtree(fromAccount: account, path: "/Folder", toAccount: other, to: "/Backup/Folder")

        #expect(await store.tags(account: account, path: "/Folder/a.txt") == [blue])
        #expect(await store.tags(account: account, path: "/Copy") == [red])
        #expect(await store.tags(account: account, path: "/Copy/a.txt") == [blue])
        #expect(await store.tags(account: other, path: "/Backup/Folder") == [red])
        #expect(await store.tags(account: other, path: "/Backup/Folder/a.txt") == [blue])
    }

    @Test("Deleting a folder forgets it and what was inside")
    func removeSubtree() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.url)
        await store.setTags([red], account: account, path: "/Folder")
        await store.setTags([blue], account: account, path: "/Folder/a.txt")
        await store.setTags([green], account: account, path: "/keep.txt")
        await store.removeSubtree(account: account, path: "/Folder")
        #expect(await store.tags(account: account, path: "/Folder").isEmpty)
        #expect(await store.tags(account: account, path: "/Folder/a.txt").isEmpty)
        #expect(await store.tags(account: account, path: "/keep.txt") == [green])
    }

    @Test("Removing an account removes its file")
    func removeAccount() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.url)
        await store.setTags([red], account: account, path: "/a")
        await store.flush()
        let file = scratch.url.appendingPathComponent("\(account.uuidString).json")
        #expect(FileManager.default.fileExists(atPath: file.path))

        await store.removeAccount(account)
        await store.flush()
        #expect(!FileManager.default.fileExists(atPath: file.path))
        #expect(await store.tags(account: account, path: "/a").isEmpty)
    }

    @Test("A listing returns the tags of its tagged items by path")
    func resolveByPath() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.url)
        await store.setTags([red], account: account, path: "/Docs/a.pdf")
        let found = await store.resolve(
            account: account,
            items: [(id: "x1", path: "/Docs/a.pdf"), (id: "x2", path: "/Docs/b.pdf")],
            stableIDs: false
        )
        #expect(found == ["/Docs/a.pdf": [red]])
    }

    @Test("Without lasting identifiers, a rename made elsewhere leaves the tag behind")
    func pathOnlyLosesRename() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.url)
        await store.setTags([red], account: account, path: "/a.pdf", itemID: nil)
        let found = await store.resolve(account: account, items: [(id: "whatever", path: "/renamed.pdf")], stableIDs: false)
        #expect(found.isEmpty)
    }

    @Test("With lasting identifiers, a tag follows an item renamed elsewhere")
    func followsRename() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.url)
        await store.setTags([red], account: account, path: "/a.pdf", itemID: "id-1")

        let found = await store.resolve(account: account, items: [(id: "id-1", path: "/renamed.pdf")], stableIDs: true)
        #expect(found == ["/renamed.pdf": [red]])
        // And the record now knows the new path.
        #expect(await store.tags(account: account, path: "/renamed.pdf") == [red])
        #expect(await store.tags(account: account, path: "/a.pdf").isEmpty)
    }

    @Test("A newly written item is recognised by its path and remembered by its identifier")
    func adoptsIdentifier() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.url)
        // As an upload leaves it: tagged, identifier not yet known.
        await store.setTags([blue], account: account, path: "/up.txt")
        _ = await store.resolve(account: account, items: [(id: "id-9", path: "/up.txt")], stableIDs: true)
        // Renamed elsewhere afterwards: found again by the identifier.
        let later = await store.resolve(account: account, items: [(id: "id-9", path: "/moved.txt")], stableIDs: true)
        #expect(later == ["/moved.txt": [blue]])
    }

    @Test("A file replaced in place keeps its tag although its identifier is new")
    func replacedInPlace() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.url)
        await store.setTags([red], account: account, path: "/report.pdf", itemID: "old-id")
        let found = await store.resolve(account: account, items: [(id: "new-id", path: "/report.pdf")], stableIDs: true)
        #expect(found == ["/report.pdf": [red]])
    }

    @Test("A new file at a renamed item's old path does not take over its tag")
    func newFileAtOldPath() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.url)
        await store.setTags([red], account: account, path: "/a.pdf", itemID: "id-1")
        // Elsewhere: a.pdf was renamed to b.pdf and a new a.pdf was made.
        let found = await store.resolve(
            account: account,
            items: [(id: "id-2", path: "/a.pdf"), (id: "id-1", path: "/b.pdf")],
            stableIDs: true
        )
        #expect(found == ["/b.pdf": [red]])
    }

    @Test("Two items that swapped names keep their own tags")
    func swappedNames() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.url)
        await store.setTags([red], account: account, path: "/a", itemID: "id-a")
        await store.setTags([blue], account: account, path: "/b", itemID: "id-b")
        let found = await store.resolve(
            account: account,
            items: [(id: "id-a", path: "/b"), (id: "id-b", path: "/a")],
            stableIDs: true
        )
        #expect(found == ["/b": [red], "/a": [blue]])
    }

    @Test("A damaged file is treated as no tags rather than a failure")
    func damagedFile() async throws {
        let scratch = Scratch()
        try Data("not json".utf8).write(to: scratch.url.appendingPathComponent("\(account.uuidString).json"))
        let store = TagStore(directory: scratch.url)
        #expect(await store.tags(account: account, path: "/a").isEmpty)
        await store.setTags([red], account: account, path: "/a")
        #expect(await store.tags(account: account, path: "/a") == [red])
    }
}

// MARK: - Transfers

@Suite("Tags across transfers")
struct TagTransferTests {
    private let account = UUID()

    @Test("An uploaded file's Finder tags become the cloud item's tags")
    func uploadFile() async throws {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.folder("store"))
        let file = scratch.file("a.txt")
        try FinderTags.write([red, project], to: file)

        await TagTransfer.uploaded(from: file, toAccount: account, providerType: .dropbox, remotePath: "/Docs/a.txt", store: store)
        #expect(await store.tags(account: account, path: "/Docs/a.txt") == [red, project])
    }

    @Test("An uploaded folder brings the tags of everything inside it")
    func uploadFolder() async throws {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.folder("store"))
        let folder = scratch.folder("Trip")
        try FinderTags.write([green], to: folder)
        try FinderTags.write([red], to: scratch.file("Trip/day1/photo.jpg"))
        _ = scratch.file("Trip/untagged.txt")

        await TagTransfer.uploaded(from: folder, toAccount: account, providerType: .pCloud, remotePath: "/Photos/Trip", store: store)
        #expect(await store.tags(account: account, path: "/Photos/Trip") == [green])
        #expect(await store.tags(account: account, path: "/Photos/Trip/day1/photo.jpg") == [red])
        #expect(await store.tags(account: account, path: "/Photos/Trip/untagged.txt").isEmpty)
    }

    @Test("An upload into the root uses root paths")
    func uploadToRoot() async throws {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.folder("store"))
        let folder = scratch.folder("Trip")
        try FinderTags.write([red], to: scratch.file("Trip/a.txt"))
        await TagTransfer.uploaded(from: folder, toAccount: account, providerType: .sftp, remotePath: "/Trip", store: store)
        #expect(await store.tags(account: account, path: "/Trip/a.txt") == [red])
    }

    @Test("An untagged upload leaves the cloud item's tag alone")
    func untaggedUploadKeepsTags() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.folder("store"))
        await store.setTags([blue], account: account, path: "/Docs/a.txt")
        let file = scratch.file("a.txt")

        await TagTransfer.uploaded(from: file, toAccount: account, providerType: .dropbox, remotePath: "/Docs/a.txt", store: store)
        #expect(await store.tags(account: account, path: "/Docs/a.txt") == [blue])
    }

    @Test("A download writes the cloud item's tags onto the file as Finder tags")
    func download() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.folder("store"))
        await store.setTags([red, project], account: account, path: "/Docs/a.txt")
        let file = scratch.file("a.txt")

        await TagTransfer.downloaded(fromAccount: account, providerType: .dropbox, remotePath: "/Docs/a.txt", to: file, store: store)
        #expect(FinderTags.read(at: file) == [red, project])
        #expect(systemTagNames(file) == ["Red", "Projekt X"])
    }

    @Test("A download of an untagged item leaves the file's own tags alone")
    func untaggedDownload() async throws {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.folder("store"))
        let file = scratch.file("a.txt")
        try FinderTags.write([green], to: file)
        await TagTransfer.downloaded(fromAccount: account, providerType: .dropbox, remotePath: "/a.txt", to: file, store: store)
        #expect(FinderTags.read(at: file) == [green])
    }

    @Test("From one account to another, by way of the staged file")
    func accountToAccount() async {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.folder("store"))
        let other = UUID()
        await store.setTags([red], account: account, path: "/a.txt")
        let staged = scratch.file("staged.tmp")

        await TagTransfer.downloaded(fromAccount: account, providerType: .dropbox, remotePath: "/a.txt", to: staged, store: store)
        await TagTransfer.uploaded(from: staged, toAccount: other, providerType: .box, remotePath: "/Inbox/a.txt", store: store)
        #expect(await store.tags(account: other, path: "/Inbox/a.txt") == [red])
        #expect(await store.tags(account: account, path: "/a.txt") == [red])
    }

    @Test("iCloud Drive keeps its tags in the files, so the store stays out of it")
    func iCloudIsLeftToFinder() async throws {
        let scratch = Scratch()
        let store = TagStore(directory: scratch.folder("store"))
        let file = scratch.file("a.txt")
        try FinderTags.write([red], to: file)
        await TagTransfer.uploaded(from: file, toAccount: account, providerType: .iCloud, remotePath: "/a.txt", store: store)
        #expect(await store.tags(account: account, path: "/a.txt").isEmpty)

        await store.setTags([blue], account: account, path: "/b.txt")
        let other = scratch.file("b.txt")
        await TagTransfer.downloaded(fromAccount: account, providerType: .iCloud, remotePath: "/b.txt", to: other, store: store)
        #expect(FinderTags.read(at: other).isEmpty)
    }

    @Test("Which accounts have identifiers that outlive a rename")
    func lastingIdentifiers() {
        #expect(CloudProviderType.dropbox.hasLastingItemIDs)
        #expect(CloudProviderType.googleDrive.hasLastingItemIDs)
        #expect(CloudProviderType.mega.hasLastingItemIDs)
        // Their identifiers are the path, or a hash of it that changes with
        // every launch.
        #expect(!CloudProviderType.sftp.hasLastingItemIDs)
        #expect(!CloudProviderType.webDAV.hasLastingItemIDs)
        #expect(!CloudProviderType.koofr.hasLastingItemIDs)
        #expect(!CloudProviderType.box.hasLastingItemIDs)
        #expect(CloudProviderType.iCloud.keepsTagsInFiles)
        #expect(!CloudProviderType.dropbox.keepsTagsInFiles)
    }
}

// MARK: - Panels

@Suite("Tags in the panels")
@MainActor
struct TagPanelTests {

    /// Waits for something the view model does in the background.
    private func eventually(_ condition: @MainActor () -> Bool) async -> Bool {
        for _ in 0..<200 {
            if condition() { return true }
            try? await Task.sleep(for: .milliseconds(20))
        }
        return condition()
    }

    @Test("A folder's tags are on screen shortly after its listing")
    func listingLoadsTags() async throws {
        let scratch = Scratch()
        let tagged = scratch.file("tagged.txt")
        _ = scratch.file("plain.txt")
        try FinderTags.write([red, blue], to: tagged)

        let vm = FileManagerViewModel()
        await vm.loadDirectory(at: scratch.url)
        #expect(await eventually { !vm.tags.isEmpty })
        let item = try #require(vm.items.first { $0.name == "tagged.txt" })
        #expect(vm.tags[item.id] == [red, blue])
        #expect(vm.tags.count == 1)
    }

    @Test("Tagging from the menu writes a Finder tag on the file")
    func changeTagsWritesFinderTag() async throws {
        let scratch = Scratch()
        let file = scratch.file("a.txt")
        let vm = FileManagerViewModel()
        await vm.loadDirectory(at: scratch.url)
        let item = try #require(vm.items.first)

        vm.changeTags(of: [item], .add(red))
        // On screen at once, on disk a moment later.
        #expect(vm.tags[item.id] == [red])
        #expect(await eventually { FinderTags.read(at: file) == [red] })
        #expect(systemTagNames(file) == ["Red"])

        vm.changeTags(of: [item], .remove(name: "Red"))
        #expect(vm.tags[item.id] == nil)
        #expect(await eventually { FinderTags.read(at: file).isEmpty })
    }

    @Test("A tag given outside FileFluss shows after the tags are read again")
    func picksUpFinderChange() async throws {
        let scratch = Scratch()
        let file = scratch.file("a.txt")
        let vm = FileManagerViewModel()
        await vm.loadDirectory(at: scratch.url)
        let item = try #require(vm.items.first)
        #expect(vm.tags[item.id] == nil)

        // As Finder would.
        try (file as NSURL).setResourceValue(["Green"], forKey: .tagNamesKey)
        vm.reloadTags()
        #expect(await eventually { vm.tags[item.id] == [green] })
    }

    @Test("Another folder's tags are dropped when the panel moves on")
    func navigationClearsTags() async throws {
        let scratch = Scratch()
        try FinderTags.write([red], to: scratch.file("one/a.txt"))
        _ = scratch.folder("two")
        let vm = FileManagerViewModel()
        await vm.loadDirectory(at: scratch.url.appendingPathComponent("one"))
        #expect(await eventually { !vm.tags.isEmpty })
        await vm.loadDirectory(at: scratch.url.appendingPathComponent("two"))
        #expect(await eventually { vm.tags.isEmpty })
    }

    @Test("A tag that can't be written is reported and taken off the screen again")
    func failedChangeIsReported() async throws {
        let scratch = Scratch()
        let file = scratch.file("a.txt")
        let vm = FileManagerViewModel()
        await vm.loadDirectory(at: scratch.url)
        let item = try #require(vm.items.first)
        try FileManager.default.removeItem(at: file)

        vm.changeTags(of: [item], .add(red))
        #expect(await eventually { vm.tagError != nil })
        #expect(await eventually { vm.tags[item.id] == nil })
    }

    @Test("The dots take the room of up to three, overlapping")
    func dotsWidth() {
        #expect(TagDotsView.width(for: 0) == 0)
        #expect(TagDotsView.width(for: 1) == TagDotsView.diameter)
        #expect(TagDotsView.width(for: 2) == TagDotsView.diameter + TagDotsView.step)
        #expect(TagDotsView.width(for: 7) == TagDotsView.diameter + 2 * TagDotsView.step)

        let dots = TagDotsView()
        #expect(dots.isHidden)
        dots.colors = [.red, .blue]
        #expect(!dots.isHidden)
        #expect(dots.intrinsicContentSize.width == TagDotsView.diameter + TagDotsView.step)
    }

    @Test("The standard tags are the seven colours, in Finder's order, each with a name")
    func standardTags() {
        let standard = SystemTagNames.standard
        #expect(standard.map(\.color) == TagColor.menuOrder)
        #expect(standard.allSatisfy { !$0.name.isEmpty })
        #expect(Set(standard.map(\.name)).count == 7)
    }

    @Test("The Tags menu marks what the selection has and offers to clear it")
    func menu() throws {
        let standard = SystemTagNames.standard
        let first = try #require(standard.first)
        let target = NSObject()
        let item = TagMenu.item(
            selection: [(id: "a", tags: [first, project]), (id: "b", tags: [project])],
            target: target,
            action: #selector(NSObject.copy)
        )
        let rows = try #require(item.submenu?.items)
        let titled = rows.filter { !$0.isSeparatorItem }
        // Seven colours, the Finder-made tag, and "Remove All Tags".
        #expect(titled.count == 9)
        #expect(titled[0].title == first.name)
        #expect(titled[0].state == .mixed)
        #expect(titled[1].state == .off)
        #expect(titled[7].title == "Projekt X")
        #expect(titled[7].state == .on)

        let mixed = try #require(titled[0].representedObject as? TagMenuPayload)
        #expect(mixed.change == .add(first))
        #expect(mixed.itemIDs == ["a", "b"])
        let shared = try #require(titled[7].representedObject as? TagMenuPayload)
        #expect(shared.change == .remove(name: "Projekt X"))
        let clear = try #require(titled[8].representedObject as? TagMenuPayload)
        #expect(clear.change == .removeAll)
    }

    @Test("An untagged selection gets no \"Remove All Tags\"")
    func menuWithoutTags() throws {
        let item = TagMenu.item(selection: [(id: "a", tags: [])], target: NSObject(), action: #selector(NSObject.copy))
        let rows = try #require(item.submenu?.items).filter { !$0.isSeparatorItem }
        #expect(rows.count == 7)
        #expect(rows.allSatisfy { $0.state == .off })
    }

    /// The average colour of an icon's opaque pixels.
    private func averageColour(of image: NSImage) throws -> (red: CGFloat, green: CGFloat, blue: CGFloat) {
        let rep = try #require(image.representations.first as? NSBitmapImageRep)
        var red: CGFloat = 0, green: CGFloat = 0, blue: CGFloat = 0, count: CGFloat = 0
        for y in 0..<rep.pixelsHigh {
            for x in 0..<rep.pixelsWide {
                guard let colour = rep.colorAt(x: x, y: y), colour.alphaComponent > 0.5 else { continue }
                red += colour.redComponent
                green += colour.greenComponent
                blue += colour.blueComponent
                count += 1
            }
        }
        try #require(count > 0)
        return (red / count, green / count, blue / count)
    }

    @Test("A tagged folder is drawn in its tag's colour, in the same shape")
    func folderTint() throws {
        let plain = FileTypeIcon.folderIcon()
        let red = FileTypeIcon.folderIcon(tint: .red)
        let green = FileTypeIcon.folderIcon(tint: .green)
        #expect(FileTypeIcon.folderIcon(tint: TagColor.none) === plain)
        // Cached, like every other icon.
        #expect(FileTypeIcon.folderIcon(tint: .red) === red)

        let plainColour = try averageColour(of: plain)
        let redColour = try averageColour(of: red)
        let greenColour = try averageColour(of: green)
        #expect(plainColour.blue > plainColour.red)
        #expect(redColour.red > redColour.blue)
        #expect(redColour.red > redColour.green)
        #expect(greenColour.green > greenColour.red)
        #expect(greenColour.green > greenColour.blue)

        // The tint recolours the folder; it must not fill the square.
        let plainRep = try #require(plain.representations.first as? NSBitmapImageRep)
        let redRep = try #require(red.representations.first as? NSBitmapImageRep)
        #expect(plainRep.colorAt(x: 0, y: 0)?.alphaComponent == redRep.colorAt(x: 0, y: 0)?.alphaComponent)
    }
}

// MARK: - Tags given in Finder

/// Finder on macOS 27 writes every tag with colour index 1 (grey) and keeps
/// the real colour in its iCloud tag store; it tells a standard tag's colour
/// by its name. FileFluss has to do the same, or a tag given in Finder shows
/// as a grey dot.
@Suite("Tags given in Finder", .serialized)
@MainActor
struct FinderWrittenTagTests {

    @Test("a standard tag takes the colour of its name, not the grey index Finder stores")
    func standardNamesWin() {
        #expect(FinderTags.decode("Green\n1") == FileTag(name: "Green", color: .green))
        #expect(FinderTags.decode("Red\n1") == FileTag(name: "Red", color: .red))
        #expect(FinderTags.decode("Blue\n1") == FileTag(name: "Blue", color: .blue))
        #expect(FinderTags.decode("Orange\n1") == FileTag(name: "Orange", color: .orange))
        // Without any index too.
        #expect(FinderTags.decode("Yellow") == FileTag(name: "Yellow", color: .yellow))
        // Grey stays grey.
        #expect(FinderTags.decode("Gray\n1") == FileTag(name: "Gray", color: .gray))
    }

    @Test("tags given on a Mac in another language get their colour too")
    func otherLanguages() {
        #expect(FinderTags.decode("Rot\n1")?.color == .red)
        #expect(FinderTags.decode("Gelb\n1")?.color == .yellow)
        #expect(FinderTags.decode("Grün\n1")?.color == .green)
        #expect(FinderTags.decode("Lila\n1")?.color == .purple)
        #expect(FinderTags.decode("绿色\n1")?.color == .green)
        #expect(FinderTags.decode("綠色\n1")?.color == .green)
        #expect(FinderTags.decode("Vert\n1")?.color == .green)
    }

    @Test("case and Unicode form don't matter")
    func normalisation() {
        #expect(FinderTags.decode("green\n1")?.color == .green)
        // "Grün" with the umlaut as a separate combining mark.
        #expect(FinderTags.decode("Gru\u{0308}n\n1")?.color == .green)
    }

    @Test("a tag of the user's own keeps the colour stored with it")
    func ownTagsKeepTheirIndex() {
        #expect(FinderTags.decode("money\n1") == FileTag(name: "money", color: .gray))
        #expect(FinderTags.decode("Projekt X\n3") == project)
        #expect(FinderTags.decode("Steuer 2026") == plain)
    }

    @Test("a standard tag the user renamed gets its colour from the system's names")
    func renamedStandardTag() {
        defer { FinderTags.registerSystemNames(SystemTagNames.standard) }
        #expect(FinderTags.decode("Wichtig\n1")?.color == .gray)

        FinderTags.registerSystemNames([FileTag(name: "Wichtig", color: .red), FileTag(name: "Green", color: .green)])

        #expect(FinderTags.decode("Wichtig\n1")?.color == .red)
        // The built-in names still count.
        #expect(FinderTags.decode("Blue\n1")?.color == .blue)
    }

    @Test("a file tagged the way Finder does it reads with its colours")
    func fileTaggedByFinder() throws {
        let scratch = Scratch()
        let file = scratch.file("Antwortschreiben.pdf")
        let raw = try PropertyListSerialization.data(fromPropertyList: ["Green\n1", "Red\n1", "money\n1"], format: .binary, options: 0)
        let status = raw.withUnsafeBytes { setxattr(file.path, "com.apple.metadata:_kMDItemUserTags", $0.baseAddress, raw.count, 0, 0) }
        try #require(status == 0)

        let tags = FinderTags.read(at: file)

        #expect(tags == [green, red, FileTag(name: "money", color: .gray)])
        #expect(tags.dotColors == [.green, .red, .gray])
    }
}
