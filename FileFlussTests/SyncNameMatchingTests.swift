import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

/// Names the destination treats as one: README.md and readme.md on APFS,
/// NFC and NFD spellings of Café anywhere. Matching them exactly made Mirror
/// write a file and then delete it, and made two source files fight over
/// one destination name.
@Suite("Sync matches names as the destination does")
struct SyncNameMatchingTests {

    private func file(_ path: String, mod: TimeInterval = 1_000, size: Int64 = 1) -> SyncEntry {
        SyncEntry(relativePath: path, isDirectory: false, size: size, modificationDate: Date(timeIntervalSince1970: mod))
    }

    private func folder(_ path: String) -> SyncEntry {
        SyncEntry(relativePath: path, isDirectory: true, size: 0, modificationDate: Date(timeIntervalSince1970: 1_000))
    }

    private func plan(_ source: [SyncEntry], over destination: [SyncEntry], mode: SyncMode = .mirror,
                      ignoreCase: Bool = true) async -> SyncPlan {
        await SyncPlanner().plan(sourceEntries: source, destEntries: destination, mode: mode, direction: .leftToRight,
                                 sourceIsCloud: true, destIsCloud: false, destinationIgnoresCase: ignoreCase)
    }

    @Test("A name that differs only in case is the same file: replaced in place, never deleted")
    func caseDifferenceIsOneFile() async {
        let changed = await plan([file("README.md", mod: 9_000)], over: [file("readme.md")])
        #expect(changed.operations == [.replace(relativePath: "README.md", bytes: 1, destRelativePath: "readme.md")])
        #expect(changed.filesToDelete == 0)

        let same = await plan([file("README.md")], over: [file("readme.md")])
        #expect(same.operations.isEmpty)
    }

    @Test("A case-sensitive destination still keeps the two names apart")
    func caseSensitiveDestination() async {
        let made = await plan([file("README.md")], over: [file("readme.md")], ignoreCase: false)
        #expect(made.operations == [
            .add(relativePath: "README.md", isDirectory: false, bytes: 1),
            .delete(relativePath: "readme.md", isDirectory: false, bytes: 1),
        ])
    }

    @Test("Files go into the folder the destination has, under its spelling")
    func folderCaseDifference() async {
        let made = await plan([folder("Docs"), file("Docs/a.txt")], over: [folder("docs")])
        #expect(made.operations == [.add(relativePath: "Docs/a.txt", isDirectory: false, bytes: 1, destRelativePath: "docs/a.txt")])
    }

    @Test("NFC and NFD spellings match, and a replace keeps the destination's form")
    func unicodeForms() async {
        let nfd = "Cafe\u{301}.txt"
        let nfc = "Caf\u{E9}.txt"
        let same = await plan([file(nfd)], over: [file(nfc)])
        #expect(same.operations.isEmpty)

        let changed = await plan([file(nfd, mod: 9_000)], over: [file(nfc)])
        guard case .replace(_, _, let dest)? = changed.operations.first else {
            Issue.record("expected a replace, got \(changed.operations)")
            return
        }
        #expect(dest.map { Array($0.utf8) } == Array(nfc.utf8))
    }

    @Test("Two source names that are one at the destination both land, and a rerun has nothing to do")
    func sourceCollision() async {
        let source = [file("README.md"), file("Readme.md")]
        let first = await plan(source, over: [])
        #expect(first.operations == [
            .add(relativePath: "README.md", isDirectory: false, bytes: 1),
            .addRenamed(sourceRelativePath: "Readme.md", destRelativePath: "Readme (2).md", bytes: 1),
        ])

        let rerun = await plan(source, over: [file("README.md"), file("Readme (2).md")])
        #expect(rerun.operations.isEmpty)

        // A change to the renamed one goes to its copy, not the other file.
        let changed = await plan([file("README.md"), file("Readme.md", mod: 9_000)],
                                 over: [file("README.md"), file("Readme (2).md")])
        #expect(changed.operations == [.replace(relativePath: "Readme.md", bytes: 1, destRelativePath: "Readme (2).md")])
    }

    @Test("A renamed folder takes its contents along")
    func folderCollision() async {
        let made = await plan([folder("A"), folder("a"), file("A/x.txt"), file("a/y.txt")], over: [])
        #expect(made.operations == [
            .add(relativePath: "A", isDirectory: true, bytes: 0),
            .add(relativePath: "A/x.txt", isDirectory: false, bytes: 1),
            .add(relativePath: "a", isDirectory: true, bytes: 0, destRelativePath: "a (2)"),
            .add(relativePath: "a/y.txt", isDirectory: false, bytes: 1, destRelativePath: "a (2)/y.txt"),
        ])
    }

    // MARK: - A folder where the destination has a file

    @Test("Mirror replaces a file that stands where the source has a folder")
    func mirrorFolderOverFile() async {
        let made = await plan([folder("x"), file("x/child.txt")], over: [file("x", size: 4)])
        #expect(made.operations == [
            .delete(relativePath: "x", isDirectory: false, bytes: 4),
            .add(relativePath: "x", isDirectory: true, bytes: 0),
            .add(relativePath: "x/child.txt", isDirectory: false, bytes: 1),
        ])
        #expect(made.filesToDelete == 1)
        #expect(made.foldersToAdd == 1)
    }

    @Test("Modes that never delete leave such a folder and its contents out")
    func nonDeletingFolderOverFile() async {
        for mode in [SyncMode.newer, .additive] {
            let made = await plan([folder("x"), file("x/child.txt"), file("other.txt")], over: [file("x")], mode: mode)
            #expect(made.operations == [.add(relativePath: "other.txt", isDirectory: false, bytes: 1)])
            #expect(made.folderTypeClashes == 1)
        }
    }

    // MARK: - Running it

    @MainActor
    @Test("Mirror onto APFS where only the case differs leaves the file in place")
    func mirrorCaseDifferenceOnDisk() async throws {
        let source = try temporaryFolder()
        let destination = try temporaryFolder()
        guard try destinationFoldsCase(destination) else { return }
        try write("the new one", to: source.appendingPathComponent("README.md"))
        try write("old", to: destination.appendingPathComponent("readme.md"))

        let progress = try await mirror(.local(source), onto: .local(destination))
        #expect(progress.failureCount == 0)
        #expect(try FileManager.default.contentsOfDirectory(atPath: destination.path) == ["readme.md"])
        #expect(read(destination.appendingPathComponent("readme.md")) == "the new one")
    }

    @MainActor
    @Test("Two cloud files whose names fold together both arrive on APFS, and stay")
    func cloudCollisionOnDisk() async throws {
        let destination = try temporaryFolder()
        guard try destinationFoldsCase(destination) else { return }
        let provider = FakeProvider()
        provider.addFolder("/s")
        provider.put("/s/README.md", "upper")
        provider.put("/s/Readme.md", "mixed")

        try await withAccount(provider) { accountId in
            let source = SyncEndpoint.cloud(accountId: accountId, rootPath: "/s")
            let progress = try await mirror(source, onto: .local(destination))
            #expect(progress.failureCount == 0)
            #expect(read(destination.appendingPathComponent("README.md")) == "upper")
            #expect(read(destination.appendingPathComponent("Readme (2).md")) == "mixed")

            let planner = SyncPlanner()
            let rerun = await planner.plan(
                sourceEntries: try await planner.enumerate(source),
                destEntries: try await planner.enumerate(.local(destination)),
                mode: .mirror, direction: .leftToRight, sourceIsCloud: true, destIsCloud: false,
                destinationIgnoresCase: await planner.destinationIgnoresCase(.local(destination))
            )
            #expect(rerun.operations.isEmpty)
        }
    }

    @MainActor
    @Test("Mirror replaces a file with a folder on disk, and settles")
    func mirrorFolderOverFileOnDisk() async throws {
        let source = try temporaryFolder()
        let destination = try temporaryFolder()
        try write("child", to: source.appendingPathComponent("x/child.txt"))
        try write("file", to: destination.appendingPathComponent("x"))

        let progress = try await mirror(.local(source), onto: .local(destination))
        #expect(progress.failureCount == 0)
        #expect(read(destination.appendingPathComponent("x/child.txt")) == "child")
    }

    @MainActor
    @Test("Mirror deletes in a folder whose path has a space and an umlaut, and replaces without leftovers")
    func mirrorIntoSpacedUmlautFolder() async throws {
        let source = try temporaryFolder()
        let destination = try temporaryFolder().appendingPathComponent("Backup Übersicht", isDirectory: true)
        try write("same", to: source.appendingPathComponent("a.txt"))
        try write("the newer one", to: source.appendingPathComponent("b.txt"))
        try write("same", to: destination.appendingPathComponent("a.txt"))
        try write("older", to: destination.appendingPathComponent("b.txt"))
        try write("extra", to: destination.appendingPathComponent("extra.txt"))
        // b.txt differs in size, a.txt gets the same date on both sides.
        let date = Date(timeIntervalSince1970: 1_600_000_000)
        for url in [source.appendingPathComponent("a.txt"), destination.appendingPathComponent("a.txt")] {
            try FileManager.default.setAttributes([.modificationDate: date], ofItemAtPath: url.path)
        }

        let progress = try await mirror(.local(source), onto: .local(destination))
        #expect(progress.failureCount == 0)
        #expect(try FileManager.default.contentsOfDirectory(atPath: destination.path).sorted() == ["a.txt", "b.txt"])
        #expect(read(destination.appendingPathComponent("b.txt")) == "the newer one")
    }

    @MainActor
    @Test("A sync into a folder under /private works")
    func syncUnderPrivate() async throws {
        let source = try temporaryFolder()
        try write("hello", to: source.appendingPathComponent("sub/README.md"))
        // The temporary folder is /var/folders/…, really /private/var/folders/….
        let destination = URL(fileURLWithPath: "/private" + (try temporaryFolder()).standardizedFileURL.path, isDirectory: true)
        #expect(destination.path.hasPrefix("/private/"))

        let progress = try await mirror(.local(source), onto: .local(destination))
        #expect(progress.failureCount == 0)
        #expect(read(destination.appendingPathComponent("sub/README.md")) == "hello")
    }

    // MARK: - Sizes

    @Test("A local file's size is its data, not its resource fork")
    func resourceForkIsNotCounted() async throws {
        let folder = try temporaryFolder()
        let url = folder.appendingPathComponent("icon.txt")
        try write("hello", to: url)
        let fork = [UInt8](repeating: 7, count: 3_000)
        let result = url.withUnsafeFileSystemRepresentation { path in
            setxattr(path, "com.apple.ResourceFork", fork, fork.count, 0, 0)
        }
        try #require(result == 0)

        let entries = try await SyncPlanner().enumerate(.local(folder))
        #expect(entries.first { $0.relativePath == "icon.txt" }?.size == 5)
    }

    // MARK: - Helpers

    /// APFS as formatted by default; a case-sensitive volume has nothing to fold.
    private func destinationFoldsCase(_ url: URL) throws -> Bool {
        let values = try url.resourceValues(forKeys: [.volumeSupportsCaseSensitiveNamesKey])
        return values.volumeSupportsCaseSensitiveNames == false
    }

    @MainActor
    private func mirror(_ source: SyncEndpoint, onto destination: SyncEndpoint) async throws -> TransferProgress {
        let planner = SyncPlanner()
        let made = await planner.plan(
            sourceEntries: try await planner.enumerate(source),
            destEntries: try await planner.enumerate(destination),
            mode: .mirror, direction: .leftToRight,
            sourceIsCloud: source.isCloud, destIsCloud: destination.isCloud,
            destinationIgnoresCase: await planner.destinationIgnoresCase(destination)
        )
        let progress = TransferProgress(operation: "Mirroring", totalItems: made.operations.count)
        await SyncExecutor.execute(plan: made, source: source, destination: destination, progress: progress)
        return progress
    }
}
