import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

@Suite("Saved data survives what this version can't read")
struct StoredListTests {

    private let stored = Data("""
    [
      {"id": "11111111-1111-1111-1111-111111111111", "providerType": "dropbox", "displayName": "Dropbox",
       "isConnected": true, "rootPath": "/"},
      {"id": "22222222-2222-2222-2222-222222222222", "providerType": "aProviderFromTheFuture", "displayName": "New",
       "isConnected": true, "rootPath": "/", "somethingNew": {"nested": [1, 2]}}
    ]
    """.utf8)

    @Test("An unknown account doesn't lose the ones around it")
    func unknownElement() throws {
        let decoded = try #require(StoredList.decode(stored, as: CloudAccount.self))
        #expect(decoded.items.map(\.displayName) == ["Dropbox"])
        #expect(decoded.unreadable.count == 1)
    }

    @Test("Saving keeps what couldn't be read, untouched")
    func unknownElementSurvivesASave() throws {
        let decoded = try #require(StoredList.decode(stored, as: CloudAccount.self))
        var accounts = decoded.items
        accounts.append(CloudAccount(providerType: .box))

        let saved = try StoredList.encode(accounts, unreadable: decoded.unreadable)

        let reread = try #require(StoredList.decode(saved, as: CloudAccount.self))
        #expect(reread.items.map(\.providerType) == [.dropbox, .box])
        #expect(reread.unreadable.count == 1)
        let raw = String(decoding: reread.unreadable[0], as: UTF8.self)
        #expect(raw.contains("aProviderFromTheFuture"))
        #expect(raw.contains("somethingNew"))
    }

    @Test("Dates come through a save unchanged")
    func dates() throws {
        let date = Date(timeIntervalSince1970: 1_700_000_000.25)
        let account = CloudAccount(providerType: .box, lastSyncDate: date)
        let saved = try StoredList.encode([account], unreadable: [Data(#"{"x":1}"#.utf8)])
        let reread = try #require(StoredList.decode(saved, as: CloudAccount.self))
        #expect(reread.items.first?.lastSyncDate == date)
    }

    @Test("Data that isn't a list is reported, not treated as empty")
    func notAList() {
        #expect(StoredList.decode(Data("garbage".utf8), as: CloudAccount.self) == nil)
    }

    @Test("A damaged search index is replaced, not left broken")
    func corruptIndex() async throws {
        let path = try temporaryFolder().appendingPathComponent("index.db").path
        try Data(repeating: 0x7A, count: 8_192).write(to: URL(fileURLWithPath: path))

        let index = SearchIndex(path: path)
        try await index.open()
        let item = CloudFileItem(id: "a", name: "a.txt", path: "/a.txt", isDirectory: false, size: 1,
                                 modificationDate: Date(), checksum: nil)
        try await index.upsertItems([item], accountId: UUID())
    }
}

/// Sends half of what it promised, then ends as if nothing was wrong.
private final class ShortBodyURLProtocol: URLProtocol, @unchecked Sendable {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        guard let url = request.url,
              let response = HTTPURLResponse(url: url, statusCode: 200, httpVersion: "HTTP/1.1",
                                             headerFields: ["Content-Length": "100"]) else { return }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(repeating: 1, count: 50))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

/// Sends exactly what it promised.
private final class FullBodyURLProtocol: URLProtocol, @unchecked Sendable {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        guard let url = request.url,
              let response = HTTPURLResponse(url: url, statusCode: 200, httpVersion: "HTTP/1.1",
                                             headerFields: ["Content-Length": "50"]) else { return }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(repeating: 1, count: 50))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

@Suite("Downloads land whole or not at all")
struct DownloadSafetyTests {

    private func session(_ protocolClass: AnyClass) -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [protocolClass]
        return URLSession(configuration: configuration)
    }

    private let url = URL(string: "https://example.invalid/file.bin")!

    @Test("A body shorter than promised is a failure")
    func truncated() async {
        await #expect(throws: (any Error).self) {
            _ = try await session(ShortBodyURLProtocol.self).downloadReportingProgress(for: URLRequest(url: url), onBytes: nil)
        }
    }

    @Test("A complete body is fine")
    func complete() async throws {
        let (file, _) = try await session(FullBodyURLProtocol.self).downloadReportingProgress(for: URLRequest(url: url), onBytes: nil)
        #expect(try FileChunkReader.size(of: file) == 50)
        try? FileManager.default.removeItem(at: file)
    }

    @Test("A finished download replaces the old file")
    func place() throws {
        let folder = try temporaryFolder()
        let temporary = folder.appendingPathComponent("incoming")
        let target = folder.appendingPathComponent("a.txt")
        try write("new", to: temporary)
        try write("old", to: target)

        try FileSystemService.placeDownload(from: temporary, at: target)

        #expect(read(target) == "new")
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path) == ["a.txt"])
    }

    @Test("Nothing is lost when a download can't be placed")
    func placeFails() throws {
        let folder = try temporaryFolder()
        let target = folder.appendingPathComponent("a.txt")
        try write("old", to: target)

        #expect(throws: (any Error).self) {
            try FileSystemService.placeDownload(from: folder.appendingPathComponent("missing"), at: target)
        }
        #expect(read(target) == "old")
    }

    @Test("Chunks read back what's in the file")
    func chunks() throws {
        let file = try temporaryFolder().appendingPathComponent("data.bin")
        try Data("0123456789".utf8).write(to: file)
        let reader = try FileChunkReader(url: file)
        #expect(try reader.read(offset: 7, length: 3) == Data("789".utf8))
        #expect(try reader.read(offset: 0, length: 4) == Data("0123".utf8))
        #expect(try FileChunkReader.size(of: file) == 10)
    }

    @Test("A file that shrank is reported, not read past")
    func shrunkFile() throws {
        let file = try temporaryFolder().appendingPathComponent("data.bin")
        try Data("0123".utf8).write(to: file)
        let reader = try FileChunkReader(url: file)
        #expect(throws: CloudProviderError.self) {
            _ = try reader.read(offset: 2, length: 10)
        }
    }
}

@MainActor
@Suite("The conflict question always gets answered", .serialized)
struct ConflictPrompterTests {

    private func conflict(_ name: String) -> PendingConflict {
        let info = ConflictFileInfo(name: name, date: .distantPast, size: 1, fileExtension: "txt", localURL: nil)
        return PendingConflict(source: info, destination: info, remainingConflicts: 0)
    }

    /// Waits for a question about `name`. The prompter is shared, and other
    /// suites running alongside ask questions of their own.
    private func question(about names: Set<String>) async -> ConflictPrompter.Question {
        while true {
            if let question = ConflictPrompter.shared.current, names.contains(question.conflict.source.name) {
                return question
            }
            await Task.yield()
        }
    }

    @Test("Two transfers are asked in turn, and each gets its own answer")
    func queue() async {
        let prompter = ConflictPrompter.shared
        async let first = prompter.ask(conflict("queue-first"))
        async let second = prompter.ask(conflict("queue-second"))

        let asked = await question(about: ["queue-first", "queue-second"])
        prompter.resolve(ConflictResolution(choice: .replace, applyToAll: false))
        let remaining = Set(["queue-first", "queue-second"]).subtracting([asked.conflict.source.name])
        _ = await question(about: remaining)
        prompter.resolve(ConflictResolution(choice: .skip, applyToAll: false))

        let answers = await [first.choice, second.choice]
        #expect(answers.contains(.replace))
        #expect(answers.contains(.skip))
    }

    @Test("Cancelling a transfer ends its question")
    func cancelWhileAsking() async {
        let prompter = ConflictPrompter.shared
        let task = Task { await prompter.ask(conflict("cancelled")) }
        _ = await question(about: ["cancelled"])
        task.cancel()
        #expect(await task.value.choice == .stop)
        #expect(prompter.current?.conflict.source.name != "cancelled")
    }

    @Test("A cancelled transfer isn't asked at all")
    func alreadyCancelled() async {
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return await ConflictPrompter.shared.ask(conflict("late"))
        }
        #expect(await task.value.choice == .stop)
        #expect(ConflictPrompter.shared.current?.conflict.source.name != "late")
    }

    @Test("A cloud file pasted over a local one reaches the user")
    func noViewNeeded() async throws {
        let provider = FakeProvider()
        provider.put("/pasted.txt", "cloud")
        let folder = try temporaryFolder()
        try write("local", to: folder.appendingPathComponent("pasted.txt"))

        try await withAccount(provider) { accountId in
            let vm = CloudFileManagerViewModel(accountId: accountId)
            let item = CloudFileItem(id: "/pasted.txt", name: "pasted.txt", path: "/pasted.txt", isDirectory: false, size: 5, modificationDate: .distantPast, checksum: nil)
            async let download = vm.downloadItems([item], to: folder)
            _ = await question(about: ["pasted.txt"])
            ConflictPrompter.shared.resolve(ConflictResolution(choice: .skip, applyToAll: false))
            #expect(await download.isEmpty)
        }
        #expect(read(folder.appendingPathComponent("pasted.txt")) == "local")
    }
}

@MainActor
@Suite("Cloud clipboard items")
struct CloudClipboardTests {

    private func clipboardItem(_ path: String) -> CloudFileItem {
        CloudFileItem(id: path, name: (path as NSString).lastPathComponent, path: path, isDirectory: false,
                      size: 0, modificationDate: Date(), checksum: nil)
    }

    @Test("A folder on the clipboard is recognised as one")
    func folders() async throws {
        let provider = FakeProvider()
        provider.addFolder("/docs")
        provider.put("/docs/a.txt", "a")
        provider.put("/b.txt", "bb")

        try await withAccount(provider) { accountId in
            let transfer = TransferProgress(operation: "Moving", totalItems: 2)
            let state = AppState()
            let resolved = await state.resolvedCloudItems(
                [clipboardItem("/docs"), clipboardItem("/b.txt")], accountId: accountId, transfer: transfer
            )
            #expect(resolved?.map(\.isDirectory) == [true, false])
            #expect(resolved?.last?.size == 2)
        }
    }

    @Test("An item that's gone is a failure, and the rest carry on")
    func missingItem() async throws {
        let provider = FakeProvider()
        provider.put("/b.txt", "b")
        try await withAccount(provider) { accountId in
            let transfer = TransferProgress(operation: "Moving", totalItems: 2)
            let resolved = await AppState().resolvedCloudItems(
                [clipboardItem("/gone.txt"), clipboardItem("/b.txt")], accountId: accountId, transfer: transfer
            )
            #expect(resolved?.map(\.name) == ["b.txt"])
            #expect(transfer.failureCount == 1)
        }
    }
}

@Suite("Gatekeeper sees what came from the internet")
struct QuarantineTests {

    @Test("A marked file reads as marked")
    func mark() throws {
        let file = try temporaryFolder().appendingPathComponent("a.txt")
        try write("x", to: file)
        #expect(!Quarantine.isMarked(file))
        Quarantine.mark(file)
        #expect(Quarantine.isMarked(file))
    }

    @MainActor
    @Test("A cloud download is marked")
    func download() async throws {
        let provider = FakeProvider()
        provider.put("/setup.pkg", "x")
        let folder = try temporaryFolder()
        try await withAccount(provider) { accountId in
            let vm = CloudFileManagerViewModel(accountId: accountId)
            let item = CloudFileItem(id: "/setup.pkg", name: "setup.pkg", path: "/setup.pkg", isDirectory: false, size: 1, modificationDate: .distantPast, checksum: nil)
            await vm.downloadItems([item], to: folder, skipConflictCheck: true)
        }
        #expect(Quarantine.isMarked(folder.appendingPathComponent("setup.pkg")))
    }

    @Test("Unpacking keeps the mark, and doesn't invent one")
    func extraction() async throws {
        let folder = try temporaryFolder()
        try write("x", to: folder.appendingPathComponent("a.txt"))
        let archive = try await ArchiveService.createArchive(names: ["a.txt"], in: folder, archiveName: "out.tar.gz", format: .tarGzip)

        let clean = folder.appendingPathComponent("clean", isDirectory: true)
        try FileManager.default.createDirectory(at: clean, withIntermediateDirectories: true)
        try await ArchiveService.extractAll(from: archive, format: .tarGzip, into: clean)
        #expect(!Quarantine.isMarked(clean.appendingPathComponent("a.txt")))

        Quarantine.mark(archive)
        let marked = folder.appendingPathComponent("marked", isDirectory: true)
        try FileManager.default.createDirectory(at: marked, withIntermediateDirectories: true)
        try await ArchiveService.extractAll(from: archive, format: .tarGzip, into: marked)
        #expect(Quarantine.isMarked(marked.appendingPathComponent("a.txt")))
    }
}

@MainActor
@Suite("Account status")
struct AccountStatusTests {

    @Test("An expired sign-in keeps the panel live and shows on the dot")
    func expiredSignIn() {
        let manager = SyncViewModel()
        let account = CloudAccount(providerType: .box, isConnected: true)
        manager.accounts.append(account)
        defer { manager.accounts.removeAll { $0.id == account.id } }

        manager.markNeedsReAuthentication(accountId: account.id)
        let stored = manager.accounts.first { $0.id == account.id }
        #expect(stored?.isConnected == true)
        #expect(manager.isSignedIn(account) == false)

        manager.markSignInWorks(accountId: account.id)
        #expect(manager.isSignedIn(account))
    }
}

@MainActor
@Suite("Panels follow the folder")
struct FolderWatchingTests {

    private func waitUntil(_ condition: @MainActor () -> Bool) async -> Bool {
        for _ in 0..<80 {
            if condition() { return true }
            try? await Task.sleep(for: .milliseconds(50))
        }
        return condition()
    }

    @Test("The watcher reports a new file")
    func watcher() async throws {
        let folder = try temporaryFolder()
        let changes = Counter()
        let watcher = FolderWatcher(url: folder) { changes.increment() }
        #expect(watcher != nil)
        try write("x", to: folder.appendingPathComponent("a.txt"))
        #expect(await waitUntil { changes.value > 0 })
        _ = watcher
    }

    @Test("A folder that can't be watched is no error")
    func missingFolder() throws {
        let missing = try temporaryFolder().appendingPathComponent("nope")
        #expect(FolderWatcher(url: missing) {} == nil)
    }

    @Test("A file added from outside appears by itself")
    func fileAppears() async throws {
        let folder = try temporaryFolder()
        let vm = FileManagerViewModel()
        await vm.loadDirectory(at: folder)
        #expect(vm.items.isEmpty)

        try write("x", to: folder.appendingPathComponent("from-finder.txt"))

        #expect(await waitUntil { vm.items.map(\.name) == ["from-finder.txt"] })
    }

    @Test("A folder removed from outside sends the panel to its parent")
    func folderRemoved() async throws {
        let parent = try temporaryFolder()
        let child = parent.appendingPathComponent("child", isDirectory: true)
        try FileManager.default.createDirectory(at: child, withIntermediateDirectories: true)
        let vm = FileManagerViewModel()
        await vm.loadDirectory(at: child)

        try FileManager.default.removeItem(at: child)

        #expect(await waitUntil { vm.currentDirectory.standardizedFileURL == parent.standardizedFileURL })
        #expect(vm.error == nil)
    }
}

private final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    func increment() { lock.withLock { count += 1 } }
    var value: Int { lock.withLock { count } }
}
