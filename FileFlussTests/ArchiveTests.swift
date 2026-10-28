import Testing
import Foundation
@testable import FileFluss

/// Covers the archive layer: what counts as an archive, how an entry list
/// becomes a navigable tree, where an entry is allowed to land on disk, and
/// a round trip through the real tools — a zip written by `/usr/bin/zip` and
/// a tar.gz written by `/usr/bin/tar`, both read back through libarchive.
@Suite("Archives")
struct ArchiveTests {

    // MARK: - Recognising archives

    @Test("Extensions map to formats, longest suffix first")
    func formatDetection() {
        #expect(ArchiveFormat.detect(fileName: "backup.zip") == .zip)
        #expect(ArchiveFormat.detect(fileName: "BACKUP.ZIP") == .zip)
        #expect(ArchiveFormat.detect(fileName: "comic.cbz") == .zip)
        #expect(ArchiveFormat.detect(fileName: "movie.rar") == .rar)
        #expect(ArchiveFormat.detect(fileName: "stuff.7z") == .sevenZip)
        #expect(ArchiveFormat.detect(fileName: "src.tar") == .tar)
        // `.tar.gz` has to beat `.gz`, or a tarball would be read as one
        // nameless compressed stream.
        #expect(ArchiveFormat.detect(fileName: "photos.tar.gz") == .tarGzip)
        #expect(ArchiveFormat.detect(fileName: "photos.tgz") == .tarGzip)
        #expect(ArchiveFormat.detect(fileName: "logs.tar.xz") == .tarXZ)
        #expect(ArchiveFormat.detect(fileName: "dump.sql.gz") == .gzip)
    }

    @Test("Documents that merely happen to be zips are left alone")
    func containerFormatsAreNotArchives() {
        // Double-clicking any of these has to open its own app. Browsing
        // inside a .docx is not what anyone meant.
        for name in ["report.docx", "deck.pptx", "book.epub", "app.ipa", "lib.jar"] {
            #expect(ArchiveFormat.detect(fileName: name) == nil, "\(name)")
        }
    }

    @Test("A hidden file named for an extension is not an archive")
    func bareExtensionIsNotAnArchive() {
        #expect(ArchiveFormat.detect(fileName: ".zip") == nil)
        #expect(ArchiveFormat.detect(fileName: ".7z") == nil)
        #expect(ArchiveFormat.detect(fileName: "notes.txt") == nil)
    }

    @Test("Extracted contents are named after the archive, minus its suffix")
    func baseNames() {
        #expect(ArchiveFormat.baseName(ofArchive: "photos.tar.gz") == "photos")
        #expect(ArchiveFormat.baseName(ofArchive: "backup.zip") == "backup")
        #expect(ArchiveFormat.baseName(ofArchive: "dump.sql.gz") == "dump.sql")
        #expect(ArchiveFormat.baseName(ofArchive: "noextension") == "noextension")
    }

    // MARK: - Entry paths

    @Test("Entry paths are normalised, and anything climbing out is refused")
    func pathNormalisation() {
        #expect(ArchiveEntry.normalise(path: "./a/b.txt") == "a/b.txt")
        #expect(ArchiveEntry.normalise(path: "a//b.txt") == "a/b.txt")
        #expect(ArchiveEntry.normalise(path: "dir/") == "dir")
        // tar -P stores absolute paths; dropping the root makes them ordinary.
        #expect(ArchiveEntry.normalise(path: "/etc/hosts") == "etc/hosts")
        // Zip slip: an entry that wants to climb out of the archive is never
        // something the user asked for.
        #expect(ArchiveEntry.normalise(path: "../../.ssh/authorized_keys") == nil)
        #expect(ArchiveEntry.normalise(path: "a/../../b") == nil)
        #expect(ArchiveEntry.normalise(path: "..") == nil)
        #expect(ArchiveEntry.normalise(path: "") == nil)
        #expect(ArchiveEntry.normalise(path: "/") == nil)
    }

    private func entry(
        _ path: String, dir: Bool = false, size: Int64 = 0
    ) -> ArchiveEntry {
        ArchiveEntry(
            path: path, isDirectory: dir, isSymlink: false, size: size,
            modificationDate: Date(timeIntervalSince1970: 1_700_000_000),
            isEncrypted: false
        )
    }

    // MARK: - The tree

    @Test("Directories nobody declared are still navigable")
    func intermediateDirectoriesAreSynthesised() {
        // Most zips list files only, with no entry for the folders above them.
        let tree = ArchiveTree(rawEntries: [entry("a/b/c/deep.txt", size: 10)])

        #expect(tree.topLevel.map(\.name) == ["a"])
        #expect(tree.entry(at: "a")?.isDirectory == true)
        #expect(tree.entry(at: "a/b")?.isDirectory == true)
        #expect(tree.children(of: "a/b").map(\.name) == ["c"])
        #expect(tree.children(of: "a/b/c").map(\.name) == ["deep.txt"])
    }

    @Test("A folder lists its own children and no further")
    func childrenDoNotLeakGrandchildren() {
        let tree = ArchiveTree(rawEntries: [
            entry("docs", dir: true),
            entry("docs/readme.md", size: 5),
            entry("docs/img/logo.png", size: 7),
            entry("top.txt", size: 3),
        ])
        #expect(Set(tree.children(of: "docs").map(\.name)) == ["readme.md", "img"])
        #expect(Set(tree.topLevel.map(\.name)) == ["docs", "top.txt"])
    }

    @Test("A folder's size is what unpacking it would cost")
    func recursiveSizeRollsUp() {
        let tree = ArchiveTree(rawEntries: [
            entry("docs/readme.md", size: 100),
            entry("docs/img/logo.png", size: 250),
            entry("docs/img/icon.png", size: 50),
            entry("elsewhere.bin", size: 999),
        ])
        #expect(tree.recursiveSize(of: "docs") == 400)
        #expect(tree.recursiveSize(of: "docs/img") == 300)
        #expect(tree.recursiveSize(of: "nonexistent") == 0)
        #expect(tree.totalSize == 1399)
        #expect(tree.fileCount == 4)
    }

    @Test("A selected folder's subtree is everything that has to come out")
    func subtreeCoversTheWholeFolder() {
        let tree = ArchiveTree(rawEntries: [
            entry("docs/readme.md", size: 1),
            entry("docs/img/logo.png", size: 1),
            entry("other/thing.txt", size: 1),
        ])
        #expect(Set(tree.subtree(at: "docs").map(\.path))
            == ["docs", "docs/readme.md", "docs/img", "docs/img/logo.png"])
        #expect(tree.subtree(at: "docs/readme.md").map(\.path) == ["docs/readme.md"])
        #expect(tree.subtree(at: "missing").isEmpty)
    }

    @Test("A name stored twice resolves to the entry that would win")
    func duplicateEntriesKeepTheLast() {
        // Zip allows the same name twice; extraction leaves the later one on
        // disk, so that is the one the listing has to show.
        let tree = ArchiveTree(rawEntries: [
            entry("dup.txt", size: 1),
            entry("dup.txt", size: 2),
        ])
        #expect(tree.entry(at: "dup.txt")?.size == 2)
        #expect(tree.topLevel.count == 1)
    }

    // MARK: - Where an entry may land

    @Test("The prefix a selection came from is dropped on the way out")
    func prefixStripping() throws {
        let root = URL(fileURLWithPath: "/tmp/dest")
        // Dragging `2026` out of `docs/reports` must produce `2026`, not
        // three nested folders.
        #expect(try ArchiveService.targetURL(
            for: "docs/reports/2026/jan.pdf", strippingPrefix: "docs/reports", in: root
        ).path == "/tmp/dest/2026/jan.pdf")
        #expect(try ArchiveService.targetURL(
            for: "docs/readme.md", strippingPrefix: "", in: root
        ).path == "/tmp/dest/docs/readme.md")
    }

    @Test("An entry cannot be written outside the destination")
    func containmentIsEnforced() {
        // The second of two independent defences. `normalise` already
        // refuses `..`, but the check that the joined path stayed inside the
        // destination doesn't depend on it having run.
        #expect(throws: ArchiveError.self) {
            try ArchiveService.targetURL(
                for: "../../escape.txt", strippingPrefix: "", in: URL(fileURLWithPath: "/tmp/dest")
            )
        }
    }

    @Test("zip's progress lines yield file names")
    func zipOutputParsing() {
        #expect(ArchiveService.addedFileName(from: "  adding: sub/file.txt (deflated 62%)")
            == "sub/file.txt")
        #expect(ArchiveService.addedFileName(from: "  adding: no stats") == "no stats")
        #expect(ArchiveService.addedFileName(from: "\tupdating: a.bin (stored 0%)") == "a.bin")
        #expect(ArchiveService.addedFileName(from: "zip warning: something") == nil)
        #expect(ArchiveService.addedFileName(from: "") == nil)
    }

    @Test("Archive names follow Finder, with the counter before a double extension")
    func creationNaming() {
        let none: (String) -> Bool = { _ in false }
        #expect(ArchiveCreationFormat.zip.archiveName(forItemNames: ["Docs"], isTaken: none) == "Docs.zip")
        #expect(ArchiveCreationFormat.sevenZip.archiveName(forItemNames: ["a", "b"], isTaken: none) == "Archive.7z")
        // "Archive 2.tar.gz", not the "Archive.tar 2.gz" that splitting on
        // the last dot would give.
        let taken: Set<String> = ["Archive.tar.gz", "Archive 2.tar.gz"]
        #expect(ArchiveCreationFormat.tarGzip.archiveName(
            forItemNames: ["a", "b"], isTaken: { taken.contains($0) }
        ) == "Archive 3.tar.gz")
    }

    @Test("bsdtar's and rar's progress lines yield file names")
    func otherToolOutputParsing() {
        #expect(ArchiveService.addedFileName(from: "a sample/sub/b.txt") == "sample/sub/b.txt")
        #expect(ArchiveService.addedFileName(from: "Adding    sample/a.txt                 OK ") == "sample/a.txt")
        #expect(ArchiveService.addedFileName(from: "Adding    big.bin    42%") == "big.bin")
    }

    @Test("Without RARLAB's tool, RAR is unavailable and says why")
    func rarNeedsItsTool() async throws {
        // Only meaningful on a Mac without the tool, which is the default.
        guard ArchiveCreationFormat.rarTool == nil else { return }
        #expect(!ArchiveCreationFormat.rar.isAvailable)
        try await withTemporaryDirectory { root in
            try "x".write(to: root.appendingPathComponent("x.txt"), atomically: true, encoding: .utf8)
            await #expect(throws: ArchiveError.self) {
                _ = try await ArchiveService.createArchive(
                    names: ["x.txt"], in: root, archiveName: "x.rar", format: .rar
                )
            }
        }
    }

    @Test("Every writable format reads back with the same entries",
          arguments: [ArchiveCreationFormat.zip, .sevenZip, .tarGzip, .tarBzip2, .tarXZ])
    func everyFormatRoundTrips(_ format: ArchiveCreationFormat) async throws {
        try await withTemporaryDirectory { root in
            _ = try makeSample(in: root)
            let names = Mutex()
            let archive = try await ArchiveService.createArchive(
                names: ["sample"], in: root,
                archiveName: "out.\(format.fileExtension)", format: format,
                onFileAdded: { names.add($0) }
            )
            let readFormat = try #require(ArchiveFormat.detect(archive))
            let tree = try await ArchiveService.list(archive, format: readFormat)
            #expect(tree.entry(at: "sample/sub/b.txt")?.size == 7)
            #expect(tree.entry(at: "sample/a.txt")?.size == 10)
            // Progress came through for each format's own output.
            #expect(names.count > 0)
        }
    }

    // MARK: - Round trips through the real tools

    /// A folder with a nested file, a sibling, and a symlink.
    private func makeSample(in root: URL) throws -> URL {
        let fm = FileManager.default
        let sample = root.appendingPathComponent("sample", isDirectory: true)
        try fm.createDirectory(at: sample.appendingPathComponent("sub"), withIntermediateDirectories: true)
        try "top level\n".write(to: sample.appendingPathComponent("a.txt"), atomically: true, encoding: .utf8)
        try "nested\n".write(to: sample.appendingPathComponent("sub/b.txt"), atomically: true, encoding: .utf8)
        try fm.createSymbolicLink(
            atPath: sample.appendingPathComponent("link.txt").path, withDestinationPath: "a.txt"
        )
        return sample
    }

    private func withTemporaryDirectory(_ body: (URL) async throws -> Void) async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussArchiveTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try await body(root)
    }

    @Test("A zip FileFluss wrote reads back with the structure it was given")
    func zipRoundTrip() async throws {
        try await withTemporaryDirectory { root in
            _ = try makeSample(in: root)
            let archive = try await ArchiveService.createZip(
                names: ["sample"], in: root, archiveName: "out.zip"
            )
            #expect(FileManager.default.fileExists(atPath: archive.path))

            let tree = try await ArchiveService.list(archive, format: .zip)
            #expect(tree.entry(at: "sample")?.isDirectory == true)
            #expect(tree.entry(at: "sample/sub/b.txt")?.size == 7)
            // `-y` stores the symlink rather than following it out of the
            // folder, so it arrives as a link and not as a second copy.
            #expect(tree.entry(at: "sample/link.txt")?.isSymlink == true)
            #expect(Set(tree.children(of: "sample").map(\.name)) == ["a.txt", "sub", "link.txt"])
        }
    }

    @Test("Compressing refuses to append to an archive that already exists")
    func createZipRefusesExistingArchive() async throws {
        try await withTemporaryDirectory { root in
            _ = try makeSample(in: root)
            _ = try await ArchiveService.createZip(names: ["sample"], in: root, archiveName: "out.zip")
            // zip would otherwise merge the new selection into the old file,
            // which looks like nothing happened until someone opens it.
            await #expect(throws: ArchiveError.self) {
                _ = try await ArchiveService.createZip(
                    names: ["sample"], in: root, archiveName: "out.zip"
                )
            }
        }
    }

    @Test("One nested entry can be pulled out on its own")
    func extractsASingleEntry() async throws {
        try await withTemporaryDirectory { root in
            _ = try makeSample(in: root)
            let archive = try await ArchiveService.createZip(
                names: ["sample"], in: root, archiveName: "out.zip"
            )
            let destination = root.appendingPathComponent("out", isDirectory: true)

            let written = try await ArchiveService.extract(
                entryPaths: ["sample/sub/b.txt"],
                from: archive, format: .zip,
                strippingPrefix: "sample/sub",
                into: destination,
                onBytes: nil
            )

            #expect(written.map(\.lastPathComponent) == ["b.txt"])
            let landed = destination.appendingPathComponent("b.txt")
            #expect(try String(contentsOf: landed, encoding: .utf8) == "nested\n")
            // Nothing else came along for the ride.
            let contents = try FileManager.default.contentsOfDirectory(atPath: destination.path)
            #expect(contents == ["b.txt"])
        }
    }

    @Test("A selected folder comes out as a folder, with its subtree")
    func extractsAFolder() async throws {
        try await withTemporaryDirectory { root in
            _ = try makeSample(in: root)
            let archive = try await ArchiveService.createZip(
                names: ["sample"], in: root, archiveName: "out.zip"
            )
            let tree = try await ArchiveService.list(archive, format: .zip)
            let destination = root.appendingPathComponent("out", isDirectory: true)

            let written = try await ArchiveService.extract(
                entryPaths: Set(tree.subtree(at: "sample/sub").map(\.path)),
                from: archive, format: .zip,
                strippingPrefix: "sample",
                into: destination,
                onBytes: nil
            )

            #expect(written.map(\.lastPathComponent) == ["sub"])
            #expect(try String(
                contentsOf: destination.appendingPathComponent("sub/b.txt"), encoding: .utf8
            ) == "nested\n")
        }
    }

    @Test("Bytes are reported as the entries are written")
    func reportsProgress() async throws {
        try await withTemporaryDirectory { root in
            let big = root.appendingPathComponent("big.bin")
            try Data(repeating: 0x41, count: 900_000).write(to: big)
            let archive = try await ArchiveService.createZip(
                names: ["big.bin"], in: root, archiveName: "out.zip"
            )
            let counter = ByteCounter()
            _ = try await ArchiveService.extract(
                entryPaths: ["big.bin"], from: archive, format: .zip,
                strippingPrefix: "", into: root.appendingPathComponent("out"),
                onBytes: { counter.add($0) }
            )
            #expect(counter.total == 900_000)
        }
    }

    @Test("A tar.gz reads through the same path as a zip")
    func tarGzipRoundTrip() async throws {
        try await withTemporaryDirectory { root in
            _ = try makeSample(in: root)
            // Written by tar rather than by FileFluss: zip is the only format
            // it creates, but every format it browses has to read.
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
            process.arguments = ["czf", "out.tar.gz", "sample"]
            process.currentDirectoryURL = root
            try process.run()
            process.waitUntilExit()
            #expect(process.terminationStatus == 0)

            let archive = root.appendingPathComponent("out.tar.gz")
            let tree = try await ArchiveService.list(archive, format: .tarGzip)
            #expect(tree.entry(at: "sample/a.txt")?.size == 10)
            #expect(tree.entry(at: "sample/sub")?.isDirectory == true)

            let destination = root.appendingPathComponent("out", isDirectory: true)
            _ = try await ArchiveService.extract(
                entryPaths: ["sample/a.txt"], from: archive, format: .tarGzip,
                strippingPrefix: "sample", into: destination, onBytes: nil
            )
            #expect(try String(
                contentsOf: destination.appendingPathComponent("a.txt"), encoding: .utf8
            ) == "top level\n")
        }
    }

    @Test("A file whose extension lies is reported as unreadable")
    func refusesNonArchives() async throws {
        try await withTemporaryDirectory { root in
            let fake = root.appendingPathComponent("notreally.zip")
            try "this is just text\n".write(to: fake, atomically: true, encoding: .utf8)
            // The panel relies on this to hand the file to its own app
            // instead of showing an empty archive.
            await #expect(throws: ArchiveError.self) {
                _ = try await ArchiveService.list(fake, format: .zip)
            }
        }
    }

    @Test("A tarball misnamed .gz still lists its own entries")
    func misnamedTarballKeepsItsEntryNames() async throws {
        try await withTemporaryDirectory { root in
            _ = try makeSample(in: root)
            let tar = Process()
            tar.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
            tar.arguments = ["czf", "backup.gz", "sample"]
            tar.currentDirectoryURL = root
            try tar.run()
            tar.waitUntilExit()

            // `.gz` says "one nameless stream", but the gzip filter plus the
            // tar reader finds a real entry list. Falling back to the
            // archive's own name here would collapse every row into one.
            let tree = try await ArchiveService.list(
                root.appendingPathComponent("backup.gz"), format: .gzip
            )
            #expect(tree.entry(at: "sample/sub/b.txt")?.size == 7)
            #expect(tree.topLevel.map(\.name) == ["sample"])
        }
    }

    @Test("A password-protected archive says so instead of failing late")
    func encryptedArchiveIsReported() async throws {
        try await withTemporaryDirectory { root in
            try "secret\n".write(
                to: root.appendingPathComponent("secret.txt"), atomically: true, encoding: .utf8
            )
            let zip = Process()
            zip.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
            zip.arguments = ["-q", "-P", "hunter2", "locked.zip", "secret.txt"]
            zip.currentDirectoryURL = root
            try zip.run()
            zip.waitUntilExit()
            #expect(zip.terminationStatus == 0)

            let archive = root.appendingPathComponent("locked.zip")
            // The entry list is readable even when the data isn't, so the
            // panel can open the archive and still explain why nothing will
            // come out of it.
            let tree = try await ArchiveService.list(archive, format: .zip)
            #expect(tree.entry(at: "secret.txt") != nil)
            #expect(tree.containsEncryptedEntries)

            await #expect(throws: ArchiveError.self) {
                try await ArchiveService.extractAll(
                    from: archive, format: .zip,
                    into: root.appendingPathComponent("out")
                )
            }
        }
    }

    @Test("A single compressed stream browses as one file named after it")
    func singleStreamArchive() async throws {
        try await withTemporaryDirectory { root in
            let source = root.appendingPathComponent("dump.sql")
            try "select 1;\n".write(to: source, atomically: true, encoding: .utf8)
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/gzip")
            process.arguments = ["dump.sql"]
            process.currentDirectoryURL = root
            try process.run()
            process.waitUntilExit()

            let archive = root.appendingPathComponent("dump.sql.gz")
            let tree = try await ArchiveService.list(archive, format: .gzip)
            // A .gz carries no entry list, so the one entry takes the
            // archive's own name without the suffix.
            #expect(tree.topLevel.map(\.name) == ["dump.sql"])
        }
    }
}

/// Collects byte deltas reported from the extractor's own thread.
private final class ByteCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Int64 = 0

    func add(_ delta: Int64) {
        lock.lock(); value += delta; lock.unlock()
    }

    var total: Int64 {
        lock.lock(); defer { lock.unlock() }
        return value
    }
}

/// Counts names reported from a helper tool's reader thread.
private final class Mutex: @unchecked Sendable {
    private let lock = NSLock()
    private var names: [String] = []
    func add(_ name: String) { lock.lock(); names.append(name); lock.unlock() }
    var count: Int { lock.lock(); defer { lock.unlock() }; return names.count }
}
