import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

/// A cloud account in memory, with switches for the failures that matter:
/// the tests below are all about what's left when something goes wrong.
private final class FakeProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .webDAV
    private let lock = NSLock()
    private var files: [String: Data] = [:]
    private var folders: Set<String> = ["/"]
    private var failingUploads: Set<String> = []
    private var failingDownloads: Set<String> = []
    private var failingDeletes: Set<String> = []
    private var extraListing: [String: [CloudFileItem]] = [:]

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }

    func put(_ path: String, _ text: String) { locked { files[path] = Data(text.utf8) } }
    func addFolder(_ path: String) { _ = locked { folders.insert(path) } }
    func failUploads(named name: String) { _ = locked { failingUploads.insert(name) } }
    func failDownload(of path: String) { _ = locked { failingDownloads.insert(path) } }
    func failDelete(of path: String) { _ = locked { failingDeletes.insert(path) } }
    func addListing(_ item: CloudFileItem, in folder: String) { locked { extraListing[folder, default: []].append(item) } }
    func text(_ path: String) -> String? { locked { files[path].map { String(decoding: $0, as: UTF8.self) } } }
    var allPaths: [String] { locked { (Array(files.keys) + folders.filter { $0 != "/" }).sorted() } }

    var isAuthenticated: Bool { get async { true } }
    func authenticate() async throws {}
    func disconnect() async throws {}

    func listDirectory(at path: String) async throws -> [CloudFileItem] {
        locked {
            let prefix = path == "/" ? "/" : path + "/"
            func isChild(_ candidate: String) -> Bool {
                candidate.hasPrefix(prefix) && !candidate.dropFirst(prefix.count).contains("/") && candidate != prefix
            }
            let fileItems = files.keys.filter(isChild).map { item($0, isDirectory: false, size: Int64(files[$0]!.count)) }
            let folderItems = folders.filter(isChild).map { item($0, isDirectory: true, size: 0) }
            return fileItems + folderItems + (extraListing[path] ?? [])
        }
    }

    private func item(_ path: String, isDirectory: Bool, size: Int64) -> CloudFileItem {
        CloudFileItem(id: path, name: (path as NSString).lastPathComponent, path: path,
                      isDirectory: isDirectory, size: size,
                      modificationDate: Date(timeIntervalSince1970: 1_000), checksum: nil)
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        let data: Data? = locked { failingDownloads.contains(remotePath) ? nil : files[remotePath] }
        guard let data else { throw CloudProviderError.serverError(503) }
        try data.write(to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        let name = localURL.lastPathComponent
        // Half an upload arrives, then the connection drops.
        if locked({ failingUploads.contains(name) }) {
            locked { files[remotePath] = Data("partial".utf8) }
            throw CloudProviderError.serverError(502)
        }
        let data = try Data(contentsOf: localURL)
        locked { files[remotePath] = data }
    }

    func deleteItem(at path: String) async throws {
        if locked({ failingDeletes.contains(path) }) { throw CloudProviderError.unauthorized }
        locked {
            let prefix = path + "/"
            files = files.filter { $0.key != path && !$0.key.hasPrefix(prefix) }
            folders = folders.filter { $0 != path && !$0.hasPrefix(prefix) }
        }
    }

    func createDirectory(at path: String) async throws { addFolder(path) }

    func renameItem(at path: String, to newName: String) async throws {
        let target = ((path as NSString).deletingLastPathComponent as NSString).appendingPathComponent(newName)
        locked {
            if let data = files.removeValue(forKey: path) { files[target] = data }
            let prefix = path + "/"
            for (key, value) in files where key.hasPrefix(prefix) {
                files.removeValue(forKey: key)
                files[target + "/" + key.dropFirst(prefix.count)] = value
            }
            if folders.remove(path) != nil { folders.insert(target) }
        }
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        guard let size = locked({ files[path]?.count }) else { throw CloudProviderError.notFound(path) }
        return item(path, isDirectory: false, size: Int64(size))
    }

    func folderSize(at path: String) async throws -> Int64 { 0 }
}

private func temporaryFolder() throws -> URL {
    let url = FileManager.default.temporaryDirectory.appendingPathComponent("fileflusstests-\(UUID().uuidString)", isDirectory: true)
    try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

private func write(_ text: String, to url: URL) throws {
    try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    try Data(text.utf8).write(to: url)
}

private func read(_ url: URL) -> String? {
    (try? Data(contentsOf: url)).map { String(decoding: $0, as: UTF8.self) }
}

/// Registers a fake under a fresh account for the length of a test.
private func withAccount(_ provider: FakeProvider, isolation: isolated (any Actor)? = #isolation, _ body: (UUID) async throws -> Void) async throws {
    let accountId = UUID()
    await SyncEngine.shared.registerProvider(for: accountId, provider: provider)
    defer { Task { await SyncEngine.shared.removeProvider(for: accountId) } }
    try await body(accountId)
}

// MARK: - Replacing

@Suite("Replacing keeps the old copy until the new one is complete")
struct ReplacingTests {

    @Test("A cloud item is swapped only after the upload finished")
    func cloudSwap() async throws {
        let provider = FakeProvider()
        provider.put("/a.txt", "old")
        try await provider.replacingItem(at: "/a.txt") { temporaryPath in
            #expect(provider.text("/a.txt") == "old")
            provider.put(temporaryPath, "new")
        }
        #expect(provider.text("/a.txt") == "new")
        #expect(provider.allPaths == ["/a.txt"])
    }

    @Test("A failed upload leaves the old item and no leftovers")
    func cloudFailedWrite() async throws {
        let provider = FakeProvider()
        provider.put("/a.txt", "old")
        await #expect(throws: CloudProviderError.self) {
            try await provider.replacingItem(at: "/a.txt") { temporaryPath in
                provider.put(temporaryPath, "partial")
                throw CloudProviderError.serverError(502)
            }
        }
        #expect(provider.text("/a.txt") == "old")
        #expect(provider.allPaths == ["/a.txt"])
    }

    @Test("If the old item can't be removed, it stays and the new copy goes")
    func cloudFailedDelete() async throws {
        let provider = FakeProvider()
        provider.put("/a.txt", "old")
        provider.failDelete(of: "/a.txt")
        await #expect(throws: CloudProviderError.self) {
            try await provider.replacingItem(at: "/a.txt") { provider.put($0, "new") }
        }
        #expect(provider.text("/a.txt") == "old")
        #expect(provider.allPaths == ["/a.txt"])
    }

    @Test("The temporary name keeps the extension and stays a sensible length")
    func temporaryName() {
        let name = FileSystemService.temporaryReplacementName(for: "report.pdf")
        #expect(name.hasPrefix("report.filefluss-part-"))
        #expect(name.hasSuffix(".pdf"))
        #expect(!FileSystemService.temporaryReplacementName(for: "README").contains("."+"."))
        let long = String(repeating: "x", count: 250) + ".txt"
        #expect(FileSystemService.temporaryReplacementName(for: long).utf8.count <= 255)
    }

    @Test("A local copy replaces a file and keeps the source's date")
    func localCopy() async throws {
        let folder = try temporaryFolder()
        let source = folder.appendingPathComponent("source.txt")
        let target = folder.appendingPathComponent("target.txt")
        try write("new", to: source)
        try write("old", to: target)
        let date = Date(timeIntervalSince1970: 1_500_000_000)
        try FileManager.default.setAttributes([.modificationDate: date], ofItemAtPath: source.path)

        try await FileSystemService.shared.replaceItem(at: target, withCopyOf: source)

        #expect(read(target) == "new")
        #expect(read(source) == "new")
        let stamped = try FileManager.default.attributesOfItem(atPath: target.path)[.modificationDate] as? Date
        #expect(stamped == date)
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path).sorted() == ["source.txt", "target.txt"])
    }

    @Test("A local move replaces a whole folder")
    func localMoveFolder() async throws {
        let folder = try temporaryFolder()
        let source = folder.appendingPathComponent("from/stuff", isDirectory: true)
        let target = folder.appendingPathComponent("to/stuff", isDirectory: true)
        try write("new", to: source.appendingPathComponent("a.txt"))
        try write("old", to: target.appendingPathComponent("b.txt"))

        try await FileSystemService.shared.replaceItem(at: target, withMoved: source)

        #expect(read(target.appendingPathComponent("a.txt")) == "new")
        #expect(!FileManager.default.fileExists(atPath: target.appendingPathComponent("b.txt").path))
        #expect(!FileManager.default.fileExists(atPath: source.path))
    }

    @Test("A copy that can't happen leaves the destination alone")
    func localFailedCopy() async throws {
        let folder = try temporaryFolder()
        let target = folder.appendingPathComponent("target.txt")
        try write("old", to: target)
        await #expect(throws: (any Error).self) {
            try await FileSystemService.shared.replaceItem(at: target, withCopyOf: folder.appendingPathComponent("missing.txt"))
        }
        #expect(read(target) == "old")
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path) == ["target.txt"])
    }
}

// MARK: - Sync

@MainActor
@Suite("Sync never gives up what it can't replace")
struct SyncExecutionTests {

    private func plan(_ operations: [SyncOperation], mode: SyncMode = .mirror) -> SyncPlan {
        SyncPlan(mode: mode, direction: .leftToRight, operations: operations,
                 filesToAdd: 0, filesToReplace: 0, filesToDelete: 0, foldersToAdd: 0, foldersToDelete: 0,
                 downloadBytes: 0, uploadBytes: 0, totalBytes: 0, netDestinationDelta: 0,
                 sourceEntryCount: 0, destinationEntryCount: 0)
    }

    @Test("A failed upload keeps the destination's version")
    func failedCloudReplace() async throws {
        let folder = try temporaryFolder()
        try write("new", to: folder.appendingPathComponent("a.txt"))
        let provider = FakeProvider()
        provider.put("/backup/a.txt", "old")
        provider.addFolder("/backup")
        provider.failUploads(named: "a.txt")

        try await withAccount(provider) { accountId in
            let progress = TransferProgress(operation: "Mirroring", totalItems: 1)
            await SyncExecutor.execute(
                plan: plan([.replace(relativePath: "a.txt", bytes: 3)]),
                source: .local(folder),
                destination: .cloud(accountId: accountId, rootPath: "/backup"),
                progress: progress
            )
            #expect(progress.failureCount == 1)
        }
        #expect(provider.text("/backup/a.txt") == "old")
        #expect(provider.allPaths == ["/backup", "/backup/a.txt"])
    }

    @Test("A successful replace lands under the real name, nested too")
    func cloudReplace() async throws {
        let folder = try temporaryFolder()
        try write("new", to: folder.appendingPathComponent("sub/a.txt"))
        let provider = FakeProvider()
        provider.addFolder("/backup")
        provider.addFolder("/backup/sub")
        provider.put("/backup/sub/a.txt", "old")

        try await withAccount(provider) { accountId in
            let progress = TransferProgress(operation: "Mirroring", totalItems: 1)
            await SyncExecutor.execute(
                plan: plan([.replace(relativePath: "sub/a.txt", bytes: 3)]),
                source: .local(folder),
                destination: .cloud(accountId: accountId, rootPath: "/backup"),
                progress: progress
            )
            #expect(progress.failureCount == 0)
        }
        #expect(provider.text("/backup/sub/a.txt") == "new")
        #expect(provider.allPaths == ["/backup", "/backup/sub", "/backup/sub/a.txt"])
    }

    @Test("A failed download keeps the local version and leaves nothing behind")
    func failedLocalReplace() async throws {
        let folder = try temporaryFolder()
        try write("old", to: folder.appendingPathComponent("a.txt"))
        let provider = FakeProvider()
        provider.addFolder("/backup")
        provider.put("/backup/a.txt", "new")
        provider.failDownload(of: "/backup/a.txt")

        try await withAccount(provider) { accountId in
            let progress = TransferProgress(operation: "Mirroring", totalItems: 1)
            await SyncExecutor.execute(
                plan: plan([.replace(relativePath: "a.txt", bytes: 3)]),
                source: .cloud(accountId: accountId, rootPath: "/backup"),
                destination: .local(folder),
                progress: progress
            )
            #expect(progress.failureCount == 1)
        }
        #expect(read(folder.appendingPathComponent("a.txt")) == "old")
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path) == ["a.txt"])
    }

    @Test("A Mirror delete that fails is reported as a failure")
    func failedMirrorDelete() async throws {
        let provider = FakeProvider()
        provider.addFolder("/backup")
        provider.put("/backup/extra.txt", "x")
        provider.failDelete(of: "/backup/extra.txt")
        let folder = try temporaryFolder()

        try await withAccount(provider) { accountId in
            let progress = TransferProgress(operation: "Mirroring", totalItems: 1)
            await SyncExecutor.execute(
                plan: plan([.delete(relativePath: "extra.txt", isDirectory: false, bytes: 1)]),
                source: .local(folder),
                destination: .cloud(accountId: accountId, rootPath: "/backup"),
                progress: progress
            )
            #expect(progress.failureCount == 1)
            #expect(progress.successCount == 0)
        }
    }

    @Test("A path that climbs out of the folder is refused at execution too")
    func unsafeLocalTarget() async throws {
        let folder = try temporaryFolder()
        let root = folder.appendingPathComponent("sync", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let provider = FakeProvider()
        provider.addFolder("/s")
        provider.put("/s/../evil.txt", "bad")

        try await withAccount(provider) { accountId in
            let progress = TransferProgress(operation: "Mirroring", totalItems: 1)
            await SyncExecutor.execute(
                plan: plan([.add(relativePath: "../evil.txt", isDirectory: false, bytes: 3)]),
                source: .cloud(accountId: accountId, rootPath: "/s"),
                destination: .local(root),
                progress: progress
            )
            #expect(progress.failureCount == 1)
        }
        #expect(!FileManager.default.fileExists(atPath: folder.appendingPathComponent("evil.txt").path))
    }
}

// MARK: - Planning

@Suite("Mirror can't be misled about what exists")
struct SyncScanTests {

    private func file(_ path: String) -> SyncEntry {
        SyncEntry(relativePath: path, isDirectory: false, size: 1, modificationDate: Date(timeIntervalSince1970: 1_000))
    }

    private func mirror(_ source: [SyncEntry], over destination: [SyncEntry], sourceIsCloud: Bool = false, destIsCloud: Bool = true) async -> SyncPlan {
        await SyncPlanner().plan(sourceEntries: source, destEntries: destination, mode: .mirror,
                                 direction: .leftToRight, sourceIsCloud: sourceIsCloud, destIsCloud: destIsCloud)
    }

    @Test("Hidden files on the cloud survive a Mirror from this Mac")
    func hiddenFilesKept() async {
        let plan = await mirror([file("a.txt")], over: [file("a.txt"), file(".env"), file(".git/config")])
        #expect(plan.filesToDelete == 0)
    }

    @Test("Hidden cloud files aren't brought to this Mac either")
    func hiddenFilesNotCopied() async {
        let plan = await mirror([file(".env"), file("a.txt")], over: [], sourceIsCloud: true, destIsCloud: false)
        #expect(plan.filesToAdd == 1)
    }

    @Test("Between two clouds, hidden files are ordinary files")
    func cloudToCloudKeepsHiddenFiles() async {
        let plan = await mirror([file(".env")], over: [file(".old")], sourceIsCloud: true, destIsCloud: true)
        #expect(plan.filesToAdd == 1)
        #expect(plan.filesToDelete == 1)
    }

    @Test("An empty source can't wipe the destination")
    func emptySource() async {
        let plan = await mirror([], over: [file("a.txt")])
        #expect(plan.wouldEmptyDestination)
    }

    @Test("Deleting most of the destination asks again; a few deletions don't")
    func largeDeletion() async {
        let many = (0..<20).map { file("\($0).txt") }
        let large = await mirror(Array(many.prefix(5)), over: many)
        #expect(large.deletesMostOfDestination)
        let small = await mirror(Array(many.prefix(18)), over: many)
        #expect(!small.deletesMostOfDestination)
        #expect(!small.wouldEmptyDestination)
    }

    @Test("A subfolder that can't be read stops the scan")
    func unreadableFolder() async throws {
        let folder = try temporaryFolder()
        let locked = folder.appendingPathComponent("locked", isDirectory: true)
        try write("x", to: locked.appendingPathComponent("a.txt"))
        try write("y", to: folder.appendingPathComponent("b.txt"))
        try FileManager.default.setAttributes([.posixPermissions: 0], ofItemAtPath: locked.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: locked.path) }

        await #expect(throws: SyncPlannerError.self) {
            _ = try await SyncPlanner().enumerate(.local(folder))
        }
    }

    @Test("A folder that isn't there is an error, not an empty source")
    func missingRoot() async throws {
        let missing = try temporaryFolder().appendingPathComponent("ejected", isDirectory: true)
        await #expect(throws: SyncPlannerError.self) {
            _ = try await SyncPlanner().enumerate(.local(missing))
        }
    }

    /// /tmp is a symlink to /private/tmp, and a prefix check against the
    /// wrong spelling used to drop every entry without a word.
    @Test("A folder reached through a symlink is still scanned")
    func symlinkedRoot() async throws {
        let folder = try temporaryFolder()
        try write("x", to: folder.appendingPathComponent("real/a.txt"))
        let link = folder.appendingPathComponent("link")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: folder.appendingPathComponent("real"))
        let entries = try await SyncPlanner().enumerate(.local(link))
        #expect(entries.map(\.relativePath) == ["a.txt"])
    }

    @Test("A listing with a path that climbs out stops the plan")
    func unsafeCloudPath() async throws {
        let provider = FakeProvider()
        provider.addFolder("/s")
        provider.addListing(
            CloudFileItem(id: "x", name: "evil.plist", path: "/s/../../Library/LaunchAgents/evil.plist",
                          isDirectory: false, size: 1, modificationDate: .distantPast, checksum: nil),
            in: "/s"
        )
        try await withAccount(provider) { accountId in
            await #expect(throws: SyncPlannerError.self) {
                _ = try await SyncPlanner().enumerate(.cloud(accountId: accountId, rootPath: "/s"))
            }
        }
    }
}

// MARK: - Moves

@MainActor
@Suite("A move only removes what arrived")
struct MoveTests {

    @Test("An item that failed anywhere doesn't count as arrived")
    func succeededNames() {
        let progress = TransferProgress(operation: "Moving", totalItems: 3)
        progress.recordFailure("folder", error: "one child failed")
        progress.recordSuccess("folder")
        progress.recordSuccess("done.txt")
        progress.recordSkip("skipped.txt")
        #expect(progress.succeededNames == ["done.txt"])
    }

    @Test("A folder that downloaded only in part isn't reported as downloaded")
    func partialFolder() async throws {
        let provider = FakeProvider()
        provider.addFolder("/docs")
        provider.put("/docs/a.txt", "a")
        provider.put("/docs/b.txt", "b")
        provider.failDownload(of: "/docs/b.txt")
        provider.put("/fine.txt", "fine")
        let staging = try temporaryFolder()

        try await withAccount(provider) { accountId in
            let vm = CloudFileManagerViewModel(accountId: accountId)
            let progress = TransferProgress(operation: "Moving", totalItems: 2)
            let folder = CloudFileItem(id: "/docs", name: "docs", path: "/docs", isDirectory: true, size: 0, modificationDate: .distantPast, checksum: nil)
            let file = CloudFileItem(id: "/fine.txt", name: "fine.txt", path: "/fine.txt", isDirectory: false, size: 4, modificationDate: .distantPast, checksum: nil)
            let downloaded = await vm.downloadItems([folder, file], to: staging, progress: progress, skipConflictCheck: true)
            #expect(downloaded == ["fine.txt"])
            #expect(progress.succeededNames == ["fine.txt"])
        }
    }

    @Test("A listing that can't be read stops an upload instead of overwriting")
    func unreadableDestination() async throws {
        let provider = FailingListProvider()
        let folder = try temporaryFolder()
        let file = folder.appendingPathComponent("a.txt")
        try write("x", to: file)

        let accountId = UUID()
        await SyncEngine.shared.registerProvider(for: accountId, provider: provider)
        let vm = CloudFileManagerViewModel(accountId: accountId)
        let progress = TransferProgress(operation: "Moving", totalItems: 1)
        await vm.uploadFiles(from: [file], toPath: "/elsewhere", progress: progress)
        await SyncEngine.shared.removeProvider(for: accountId)

        #expect(progress.failureCount == 1)
        #expect(progress.succeededNames.isEmpty)
        #expect(provider.uploads == 0)
    }

    @Test("Moving /A/foo/foo to /A can't delete the folder it's in")
    func ancestorReplace() async throws {
        let root = try temporaryFolder()
        let outer = root.appendingPathComponent("foo", isDirectory: true)
        let inner = outer.appendingPathComponent("foo", isDirectory: true)
        try write("keep", to: inner.appendingPathComponent("a.txt"))
        try write("sibling", to: outer.appendingPathComponent("b.txt"))

        let vm = FileManagerViewModel()
        let progress = TransferProgress(operation: "Moving", totalItems: 1)
        await vm.performMove(items: [FileItem(url: inner)], to: root, progress: progress)

        #expect(progress.failureCount == 1)
        #expect(vm.pendingConflict == nil)
        #expect(read(inner.appendingPathComponent("a.txt")) == "keep")
        #expect(read(outer.appendingPathComponent("b.txt")) == "sibling")
    }

    private func resolveWhenAsked(_ vm: CloudFileManagerViewModel, _ choice: ConflictChoice) async {
        while vm.pendingConflict == nil { await Task.yield() }
        vm.resolveConflict(ConflictResolution(choice: choice, applyToAll: false))
    }

    @Test("Keep Both on a download keeps both")
    func keepBothDownload() async throws {
        let provider = FakeProvider()
        provider.put("/a.txt", "cloud")
        let folder = try temporaryFolder()
        try write("local", to: folder.appendingPathComponent("a.txt"))

        try await withAccount(provider) { accountId in
            let vm = CloudFileManagerViewModel(accountId: accountId)
            let item = CloudFileItem(id: "/a.txt", name: "a.txt", path: "/a.txt", isDirectory: false, size: 5, modificationDate: .distantPast, checksum: nil)
            async let download = vm.downloadItems([item], to: folder)
            await resolveWhenAsked(vm, .keepBoth)
            _ = await download
        }
        let names = try FileManager.default.contentsOfDirectory(atPath: folder.path)
        #expect(names.count == 2)
        #expect(read(folder.appendingPathComponent("a.txt")) == "local")
    }

    @Test("A failed download doesn't cost the file it was replacing")
    func failedReplaceDownload() async throws {
        let provider = FakeProvider()
        provider.put("/a.txt", "cloud")
        provider.failDownload(of: "/a.txt")
        let folder = try temporaryFolder()
        try write("local", to: folder.appendingPathComponent("a.txt"))

        try await withAccount(provider) { accountId in
            let vm = CloudFileManagerViewModel(accountId: accountId)
            let item = CloudFileItem(id: "/a.txt", name: "a.txt", path: "/a.txt", isDirectory: false, size: 5, modificationDate: .distantPast, checksum: nil)
            async let download = vm.downloadItems([item], to: folder)
            await resolveWhenAsked(vm, .replace)
            let downloaded = await download
            #expect(downloaded.isEmpty)
        }
        #expect(read(folder.appendingPathComponent("a.txt")) == "local")
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path) == ["a.txt"])
    }

    @Test("A download that replaces lands over the old file")
    func replaceDownload() async throws {
        let provider = FakeProvider()
        provider.put("/a.txt", "cloud")
        let folder = try temporaryFolder()
        try write("local", to: folder.appendingPathComponent("a.txt"))

        try await withAccount(provider) { accountId in
            let vm = CloudFileManagerViewModel(accountId: accountId)
            let item = CloudFileItem(id: "/a.txt", name: "a.txt", path: "/a.txt", isDirectory: false, size: 5, modificationDate: .distantPast, checksum: nil)
            async let download = vm.downloadItems([item], to: folder)
            await resolveWhenAsked(vm, .replace)
            #expect(await download == ["a.txt"])
        }
        #expect(read(folder.appendingPathComponent("a.txt")) == "cloud")
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path) == ["a.txt"])
    }
}

/// An account whose folders can't be listed but whose uploads would work,
/// which is what makes silently carrying on dangerous.
private final class FailingListProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .webDAV
    private let lock = NSLock()
    private var uploadCount = 0
    var uploads: Int { lock.withLock { uploadCount } }
    var isAuthenticated: Bool { get async { true } }
    func authenticate() async throws {}
    func disconnect() async throws {}
    func listDirectory(at path: String) async throws -> [CloudFileItem] { throw CloudProviderError.serverError(503) }
    func downloadFile(remotePath: String, to localURL: URL) async throws {}
    func uploadFile(from localURL: URL, to remotePath: String) async throws { lock.withLock { uploadCount += 1 } }
    func deleteItem(at path: String) async throws {}
    func createDirectory(at path: String) async throws {}
    func renameItem(at path: String, to newName: String) async throws {}
    func getFileMetadata(at path: String) async throws -> CloudFileItem { throw CloudProviderError.notFound(path) }
    func folderSize(at path: String) async throws -> Int64 { 0 }
}

// MARK: - Paths

@Suite("Paths from outside stay inside")
struct PathSafetyTests {

    @Test("Traversal is found wherever it hides")
    func traversal() {
        #expect(SafeLocalPath.hasTraversal("../a"))
        #expect(SafeLocalPath.hasTraversal("/a/../b"))
        #expect(SafeLocalPath.hasTraversal("a/.."))
        #expect(SafeLocalPath.hasTraversal("a\\..\\b"))
        #expect(SafeLocalPath.hasTraversal("./a"))
        #expect(!SafeLocalPath.hasTraversal("/a/b.txt"))
        #expect(!SafeLocalPath.hasTraversal("a..b/c..d"))
        #expect(!SafeLocalPath.hasTraversal("/.hidden/.env"))
    }

    @Test("A relative path keeps its folders and can't leave the root")
    func relativeDestination() throws {
        let root = URL(fileURLWithPath: "/tmp/sync", isDirectory: true)
        let url = try SafeLocalPath.destination(forRelativePath: "sub/a.txt", in: root)
        #expect(url.path == "/tmp/sync/sub/a.txt")
        #expect(throws: CloudProviderError.self) {
            _ = try SafeLocalPath.destination(forRelativePath: "../../Library/LaunchAgents/x.plist", in: root)
        }
    }

    @Test("The Finder mount refuses to climb out, in the path or the destination")
    func mountRequests() {
        #expect(WebDAVServer.escapesMount(path: "/../../Library/LaunchAgents/evil.plist", destination: nil))
        #expect(WebDAVServer.escapesMount(path: "/a.txt", destination: "/../../.ssh/id_rsa"))
        #expect(!WebDAVServer.escapesMount(path: "/Documents/a.txt", destination: "/Documents/b.txt"))
        #expect(!WebDAVServer.escapesMount(path: "/._a.txt", destination: nil))
    }

    @Test("An iCloud path can't reach outside CloudDocs")
    func iCloudPaths() {
        let provider = ICloudProvider()
        let root = ICloudProvider.rootURL.path
        #expect(provider.url(forRemotePath: "/../../.ssh/id_rsa").standardizedFileURL.path.hasPrefix(root))
        #expect(provider.url(forRemotePath: "/a/b.txt").path == root + "/a/b.txt")
        #expect(provider.url(forRemotePath: "/").path == ICloudProvider.rootURL.path)
    }
}

// MARK: - Listings

@Suite("A listing that can't be read isn't an empty folder")
struct ListingSafetyTests {

    private let webDAVBase = "https://dav.example.com"

    @Test("Broken WebDAV XML is an error, even after some good rows")
    func brokenWebDAV() {
        let truncated = Data("""
        <?xml version="1.0"?><d:multistatus xmlns:d="DAV:"><d:response><d:href>/a.txt</d:href>
        """.utf8)
        #expect(throws: CloudProviderError.self) {
            _ = try WebDAVResponseParser.children(data: truncated, basePath: webDAVBase, requestPath: "/")
        }
    }

    @Test("Broken Jottacloud XML is an error")
    func brokenJottacloud() {
        #expect(throws: CloudProviderError.self) {
            _ = try JottacloudXMLParser.parseListing(data: Data("<folder><files><file name=".utf8), basePath: "/")
        }
    }

    @Test("A row that isn't understood fails an SFTP listing")
    func unknownSFTPRow() {
        let output = """
        sftp> ls -la /data
        -rw-r--r-- 1 u g 12 Jan  5 10:00 /data/good.txt
        -rw-r--r-- odd columns nobody expected here
        """
        #expect(throws: CloudProviderError.self) {
            try SFTPAPIClient.validateListing(output: output, itemCount: 1, everyLineIsARow: false)
        }
    }

    @Test("Real empty folders and normal listings still pass")
    func normalListings() throws {
        try SFTPAPIClient.validateListing(output: "sftp> ls -la /data\n", itemCount: 0, everyLineIsARow: false)
        try SFTPAPIClient.validateListing(output: "", itemCount: 0, everyLineIsARow: true)
        try SFTPAPIClient.validateListing(output: "total 0\n", itemCount: 0, everyLineIsARow: true)
        let dots = """
        drwxr-xr-x 2 u g 4096 Jan  5 10:00 .
        drwxr-xr-x 9 u g 4096 Jan  5 10:00 ..
        """
        try SFTPAPIClient.validateListing(output: dots, itemCount: 0, everyLineIsARow: true)
        let withDots = """
        sftp> ls -la /data
        drwxr-xr-x 2 u g 4096 Jan  5 10:00 /data/.
        drwxr-xr-x 9 u g 4096 Jan  5 10:00 /data/..
        -rw-r--r-- 1 u g 12 Jan  5 10:00 /data/good.txt
        """
        try SFTPAPIClient.validateListing(output: withDots, itemCount: 1, everyLineIsARow: false)
    }

    @Test("Device rows don't fail a listing")
    func deviceRows() throws {
        let output = """
        crw-rw---- 1 root tty 4, 64 Jan  5 10:00 ttyS0
        -rw-r--r-- 1 u g 12 Jan  5 10:00 good.txt
        """
        try SFTPAPIClient.validateListing(output: output, itemCount: 1, everyLineIsARow: true)
    }

    @Test("A Windows-style FTP listing isn't mistaken for an empty folder")
    func windowsFTP() {
        let output = """
        10-28-26  09:30PM       <DIR>          Photos
        10-28-26  09:31PM              1024 notes.txt
        """
        #expect(throws: CloudProviderError.self) {
            try SFTPAPIClient.validateListing(output: output, itemCount: 0, everyLineIsARow: true)
        }
    }

    @Test("A listing rejected by a real parse pass counts every row")
    func parsedRowsAreFine() throws {
        let row = "-rw-r--r-- 1 u g 12 Jan  5 10:00 good.txt"
        #expect(SFTPAPIClient.parseListingLine(row) != nil)
        try SFTPAPIClient.validateListing(output: row, itemCount: 1, everyLineIsARow: true)
    }
}

// MARK: - Tools

@Suite("Command-line tools")
struct ToolSafetyTests {

    /// A process that writes far more than a pipe holds and then exits only
    /// finishes if something reads while it runs.
    @Test("Large output doesn't hang a process")
    func largeOutput() async throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = ["-c", "head -c 500000 /dev/zero | tr '\\0' 'x'"]
        let pipe = Pipe()
        process.standardOutput = pipe
        let drain = PipeDrain(pipe)
        let count: Int = try await withCheckedThrowingContinuation { continuation in
            process.terminationHandler = { _ in continuation.resume(returning: drain.collected().count) }
            do { try process.run() } catch { continuation.resume(throwing: error) }
        }
        #expect(count == 500_000)
    }

    /// `zip` reads `-TT` as "run this command to test the archive".
    @Test("A file name can't become a zip option")
    func zipOptionInjection() async throws {
        let folder = try temporaryFolder()
        let marker = folder.appendingPathComponent("pwned")
        let evil = "-TTtouch pwned #"
        try write("x", to: folder.appendingPathComponent(evil))
        try write("x", to: folder.appendingPathComponent("-T"))
        try write("y", to: folder.appendingPathComponent("plain.txt"))

        let archive = try await ArchiveService.createArchive(
            names: [evil, "-T", "plain.txt"], in: folder, archiveName: "out.zip", format: .zip
        )
        #expect(FileManager.default.fileExists(atPath: archive.path))
        #expect(!FileManager.default.fileExists(atPath: marker.path))

        let tree = try await ArchiveService.list(archive, format: .zip)
        #expect(Set(tree.entries.map(\.path)) == [evil, "-T", "plain.txt"])
    }

    @Test("Unpacking a tarball keeps tools runnable, without setuid")
    func executableBit() async throws {
        let folder = try temporaryFolder()
        let source = folder.appendingPathComponent("src", isDirectory: true)
        try write("#!/bin/sh\n", to: source.appendingPathComponent("tool"))
        try write("text", to: source.appendingPathComponent("notes.txt"))
        let tool = source.appendingPathComponent("tool")
        try FileManager.default.setAttributes([.posixPermissions: 0o4755], ofItemAtPath: tool.path)

        let archive = try await ArchiveService.createArchive(
            names: ["tool", "notes.txt"], in: source, archiveName: "out.tar.gz", format: .tarGzip
        )
        let target = folder.appendingPathComponent("unpacked", isDirectory: true)
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
        try await ArchiveService.extractAll(from: archive, format: .tarGzip, into: target)

        let unpacked = target.appendingPathComponent("tool")
        #expect(FileManager.default.isExecutableFile(atPath: unpacked.path))
        #expect(!FileManager.default.isExecutableFile(atPath: target.appendingPathComponent("notes.txt").path))
        let mode = try FileManager.default.attributesOfItem(atPath: unpacked.path)[.posixPermissions] as? Int
        #expect(mode == 0o755)
    }
}
