import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

/// The panel's side of archive support, driven through the view model the
/// way the UI drives it: double-click to go in, navigate around, navigate
/// out, unpack a selection, and compress one.
@Suite("Archive browsing")
@MainActor
struct ArchiveBrowsingTests {

    /// A temporary folder holding `Sample/` — with a nested subfolder — and
    /// `Sample.zip` made from it.
    private func makeFixture() throws -> (root: URL, archive: URL) {
        let base = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussBrowse-\(UUID().uuidString)", isDirectory: true)
        let sample = base.appendingPathComponent("Sample/Docs", isDirectory: true)
        try FileManager.default.createDirectory(at: sample, withIntermediateDirectories: true)
        // `/var` is a symlink to `/private/var`, and a directory listing
        // comes back resolved — so the fixture resolves too, or every URL
        // the panel reports looks different from the one the test built.
        let root = base.resolvingSymlinksInPath()
        try "top\n".write(
            to: base.appendingPathComponent("Sample/readme.txt"), atomically: true, encoding: .utf8
        )
        try "nested\n".write(
            to: sample.appendingPathComponent("report.txt"), atomically: true, encoding: .utf8
        )

        let zip = Process()
        zip.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
        zip.arguments = ["-q", "-r", "Sample.zip", "Sample"]
        zip.currentDirectoryURL = root
        try zip.run()
        zip.waitUntilExit()
        return (root, root.appendingPathComponent("Sample.zip"))
    }

    private func cleanUp(_ root: URL) {
        try? FileManager.default.removeItem(at: root)
    }

    /// A panel pointed at `directory`.
    ///
    /// The initialiser starts a listing of the home folder that nothing can
    /// be awaited on; the navigation below supersedes it, however the two
    /// happen to be scheduled. If that ever regresses, these rows turn into
    /// the home folder's and every test here fails at once.
    private func panel(at directory: URL) async -> FileManagerViewModel {
        let vm = FileManagerViewModel()
        await vm.loadDirectory(at: directory)
        await Task.yield()
        return vm
    }

    @Test("Double-clicking an archive opens it in the panel")
    func opensAnArchive() async throws {
        let (root, archive) = try makeFixture()
        defer { cleanUp(root) }

        let vm = await panel(at: root)
        let item = try #require(vm.items.first { $0.url.lastPathComponent == "Sample.zip" })
        await vm.openItem(item)

        #expect(vm.isBrowsingArchive)
        #expect(vm.currentDirectory.path.hasSuffix("Sample.zip"))
        #expect(vm.items.map(\.name) == ["Sample"])
        #expect(vm.items[0].isDirectory)
        // The folder's row carries what unpacking it would cost, which the
        // panel can't get by walking a disk.
        #expect(vm.items[0].size == 11)
    }

    @Test("Folders inside an archive navigate like any other folder")
    func navigatesInsideAnArchive() async throws {
        let (root, archive) = try makeFixture()
        defer { cleanUp(root) }

        let vm = await panel(at: root)
        await vm.openItem(try #require(vm.items.first { $0.url.lastPathComponent == "Sample.zip" }))
        await vm.openItem(try #require(vm.items.first { $0.name == "Sample" }))

        #expect(vm.currentDirectory.path.hasSuffix("Sample.zip/Sample"))
        #expect(Set(vm.items.map(\.name)) == ["readme.txt", "Docs"])

        await vm.openItem(try #require(vm.items.first { $0.name == "Docs" }))
        #expect(vm.items.map(\.name) == ["report.txt"])
        #expect(vm.items[0].size == 7)
        // The breadcrumb and the history work off this path unchanged, which
        // is the whole reason entries carry one.
        #expect(vm.currentDirectory.path.hasSuffix("Sample.zip/Sample/Docs"))
    }

    @Test("Going up out of an archive lands back on the real folder")
    func navigatesOutOfAnArchive() async throws {
        let (root, _) = try makeFixture()
        defer { cleanUp(root) }

        let vm = await panel(at: root)
        await vm.openItem(try #require(vm.items.first { $0.url.lastPathComponent == "Sample.zip" }))
        await vm.openItem(try #require(vm.items.first { $0.name == "Sample" }))

        await vm.navigateUp()
        #expect(vm.isBrowsingArchive)
        #expect(vm.items.map(\.name) == ["Sample"])

        await vm.navigateUp()
        #expect(!vm.isBrowsingArchive)
        #expect(vm.currentDirectory.lastPathComponent == root.lastPathComponent)
        #expect(vm.items.contains { $0.url.lastPathComponent == "Sample.zip" })
    }

    @Test("Navigating somewhere else closes the archive")
    func navigatingAwayClosesTheArchive() async throws {
        let (root, _) = try makeFixture()
        defer { cleanUp(root) }

        let vm = await panel(at: root)
        await vm.openItem(try #require(vm.items.first { $0.url.lastPathComponent == "Sample.zip" }))
        #expect(vm.isBrowsingArchive)

        // A sidebar click, a favourite, Go to Folder — they all arrive here.
        await vm.navigateTo(FileManager.default.temporaryDirectory)
        #expect(!vm.isBrowsingArchive)
    }

    @Test("A file whose extension lies is not treated as an archive")
    func doesNotOpenANonArchive() async throws {
        let base = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussBrowse-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        defer { cleanUp(base) }
        let root = base.resolvingSymlinksInPath()
        let fake = root.appendingPathComponent("notreally.zip")
        try "plain text\n".write(to: fake, atomically: true, encoding: .utf8)

        let vm = await panel(at: root)
        let format = try #require(ArchiveFormat.detect(fake))
        // The panel asks before committing, so it can hand the file to its
        // own app instead of showing an empty archive. (`openItem` itself
        // would go on to call NSWorkspace, which a test shouldn't.)
        let opened = await vm.enterArchive(at: fake, format: format)
        #expect(!opened)
        #expect(!vm.isBrowsingArchive)
        #expect(vm.archiveError == nil)
    }

    @Test("Unpacking a selection gives back real files")
    func materialisesEntries() async throws {
        let (root, _) = try makeFixture()
        defer { cleanUp(root) }

        let vm = await panel(at: root)
        await vm.openItem(try #require(vm.items.first { $0.url.lastPathComponent == "Sample.zip" }))
        await vm.openItem(try #require(vm.items.first { $0.name == "Sample" }))

        let file = try #require(vm.items.first { $0.name == "readme.txt" })
        let staged = await vm.materialiseFromArchive([file])
        #expect(staged.count == 1)
        let url = try #require(staged.first)
        #expect(url.lastPathComponent == "readme.txt")
        #expect(try String(contentsOf: url, encoding: .utf8) == "top\n")

        // A selected folder comes out whole — this is what a drag onto the
        // other panel drops.
        let folder = try #require(vm.items.first { $0.name == "Docs" })
        let stagedFolder = try #require(await vm.materialiseFromArchive([folder]).first)
        #expect(try String(
            contentsOf: stagedFolder.appendingPathComponent("report.txt"), encoding: .utf8
        ) == "nested\n")
    }

    @Test("Extracting follows Finder's rule for where the contents go")
    func extractDestinationFollowsFinder() async throws {
        let (root, _) = try makeFixture()
        defer { cleanUp(root) }
        let destination = root.appendingPathComponent("Out", isDirectory: true)
        try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)

        let vm = await panel(at: root)
        let archiveItem = try #require(vm.items.first { $0.url.lastPathComponent == "Sample.zip" })

        // One top-level item in the archive, so that item lands directly —
        // no `Sample/Sample`.
        await vm.extractArchive(archiveItem, into: destination)
        #expect(vm.archiveError == nil, Comment(rawValue: vm.archiveError ?? ""))
        #expect(FileManager.default.fileExists(
            atPath: destination.appendingPathComponent("Sample/Docs/report.txt").path
        ), Comment(rawValue: (try? FileManager.default.contentsOfDirectory(atPath: destination.path).joined(separator: ",")) ?? "?"))

        // Again, and the name that is taken gets left alone.
        await vm.extractArchive(archiveItem, into: destination)
        let landed = try FileManager.default.contentsOfDirectory(atPath: destination.path).sorted()
        #expect(landed == ["Sample", "Sample 2"])
        #expect(vm.archiveError == nil)
    }

    @Test("An archive with several top-level items gets a folder of its own")
    func extractWrapsMultipleRoots() async throws {
        let (root, _) = try makeFixture()
        defer { cleanUp(root) }
        try "loose\n".write(
            to: root.appendingPathComponent("loose.txt"), atomically: true, encoding: .utf8
        )
        let zip = Process()
        zip.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
        zip.arguments = ["-q", "-r", "Two.zip", "Sample", "loose.txt"]
        zip.currentDirectoryURL = root
        try zip.run()
        zip.waitUntilExit()

        let destination = root.appendingPathComponent("Out", isDirectory: true)
        try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
        let vm = await panel(at: root)
        let archiveItem = try #require(vm.items.first { $0.url.lastPathComponent == "Two.zip" })

        await vm.extractArchive(archiveItem, into: destination)
        #expect(vm.archiveError == nil, Comment(rawValue: vm.archiveError ?? ""))
        let landed = try FileManager.default.contentsOfDirectory(atPath: destination.path).sorted()
        #expect(landed == ["Two"], Comment(rawValue: landed.joined(separator: ",")))
        #expect(FileManager.default.fileExists(
            atPath: destination.appendingPathComponent("Two/loose.txt").path
        ))
    }

    @Test("Compressing names the archive the way Finder does")
    func compressNaming() async throws {
        let (root, _) = try makeFixture()
        defer { cleanUp(root) }
        let vm = await panel(at: root)

        let folder = try #require(vm.items.first { $0.name == "Sample" })
        await vm.compress([folder])
        #expect(FileManager.default.fileExists(
            atPath: root.appendingPathComponent("Sample.zip 2").path
        ) || FileManager.default.fileExists(
            atPath: root.appendingPathComponent("Sample 2.zip").path
        ))
        #expect(vm.archiveError == nil)

        // Several items become one Archive.zip, again like Finder.
        let several = vm.items.filter { $0.name == "Sample" || $0.name == "Sample.zip" }
        #expect(several.count == 2)
        await vm.compress(several)
        #expect(vm.archiveError == nil, Comment(rawValue: vm.archiveError ?? ""))
        #expect(FileManager.default.fileExists(
            atPath: root.appendingPathComponent("Archive.zip").path
        ), Comment(rawValue: (try? FileManager.default.contentsOfDirectory(atPath: root.path).joined(separator: ",")) ?? "?"))
    }

    @Test("Nothing inside an archive can be written to")
    func archiveIsReadOnly() async throws {
        let (root, _) = try makeFixture()
        defer { cleanUp(root) }
        let vm = await panel(at: root)
        await vm.openItem(try #require(vm.items.first { $0.url.lastPathComponent == "Sample.zip" }))

        // Compressing a selection would have to write into the folder on
        // screen, and the folder on screen is an archive.
        let before = vm.items
        await vm.compress(before)
        #expect(vm.items.map(\.name) == before.map(\.name))
        #expect(try FileManager.default.contentsOfDirectory(atPath: root.path)
            .contains("Sample.zip 2") == false)
    }

    // MARK: - Cloud panels

    /// A cloud panel already inside `Sample.zip`, stored on the account at
    /// `/Backups/Sample.zip`. Set up directly: opening it for real starts
    /// with a provider download, which a test has no account for.
    private func cloudPanel(inside archive: URL) async throws -> CloudFileManagerViewModel {
        let vm = CloudFileManagerViewModel(accountId: UUID())
        let tree = try await ArchiveService.list(archive, format: .zip)
        vm.archiveSession = ArchiveBrowseSession(archiveURL: archive, format: .zip, tree: tree)
        vm.archiveRemotePath = "/Backups/Sample.zip"
        await vm.loadDirectory(at: "/Backups/Sample.zip")
        return vm
    }

    @Test("A cloud archive browses like a folder, with its account path in front")
    func cloudArchiveNavigates() async throws {
        let (root, archive) = try makeFixture()
        defer { cleanUp(root) }
        let vm = try await cloudPanel(inside: archive)

        #expect(vm.isBrowsingArchive)
        #expect(vm.items.map(\.name) == ["Sample"])
        #expect(vm.items[0].path == "/Backups/Sample.zip/Sample")

        await vm.openItem(vm.items[0])
        #expect(vm.currentPath == "/Backups/Sample.zip/Sample")
        #expect(Set(vm.items.map(\.name)) == ["readme.txt", "Docs"])
        #expect(vm.items.first { $0.name == "Docs" }?.size == 7)
    }

    @Test("Going up out of a cloud archive closes it")
    func cloudArchiveCloses() async throws {
        let (root, archive) = try makeFixture()
        defer { cleanUp(root) }
        let vm = try await cloudPanel(inside: archive)

        await vm.openItem(vm.items[0])
        await vm.navigateUp()
        #expect(vm.isBrowsingArchive)
        #expect(vm.currentPath == "/Backups/Sample.zip")

        // One more and the panel is back on the account, where the listing
        // is the provider's job again.
        await vm.navigateUp()
        #expect(!vm.isBrowsingArchive)
        #expect(vm.currentPath == "/Backups")
    }

    @Test("Opening a file inside a cloud archive unpacks it instead of downloading it")
    func cloudArchiveEntryOpens() async throws {
        let (root, archive) = try makeFixture()
        defer { cleanUp(root) }
        let vm = try await cloudPanel(inside: archive)
        await vm.openItem(vm.items[0])

        // The same entry point Quick Look and double-click use.
        let readme = try #require(vm.items.first { $0.name == "readme.txt" })
        let url = try #require(await vm.downloadToTemp(readme))
        #expect(try String(contentsOf: url, encoding: .utf8) == "top\n")

        let docs = try #require(vm.items.first { $0.name == "Docs" })
        let folder = try #require(await vm.materialiseFromArchive([docs]).first)
        #expect(FileManager.default.fileExists(atPath: folder.appendingPathComponent("report.txt").path))
    }

    @Test("Unpacking names around what the destination already holds")
    func unpackAvoidsTakenNames() async throws {
        let (root, archive) = try makeFixture()
        defer { cleanUp(root) }
        let out = root.appendingPathComponent("Out", isDirectory: true)
        try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)

        // A cloud folder that already has "Sample": the name check is the
        // caller's, not the disk's.
        let landed = try await FileManagerViewModel.unpack(
            archive: archive, named: "Sample.zip", format: .zip, into: out,
            isTaken: { $0 == "Sample" }
        )
        #expect(landed.lastPathComponent == "Sample 2")
        #expect(FileManager.default.fileExists(atPath: landed.appendingPathComponent("Docs/report.txt").path))
        #expect(FileManagerViewModel.availableName("a.tar", isTaken: { $0 == "a.tar" }) == "a 2.tar")
    }
}
