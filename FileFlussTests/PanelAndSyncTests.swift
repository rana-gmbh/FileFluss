import Testing
import Foundation
import AppKit
import FileFlussCore
@testable import FileFluss

@MainActor
@Suite("Sync runs what was planned")
struct SyncPlanTests {

    private func file(_ path: String, mod: TimeInterval = 1_000, size: Int64 = 1) -> SyncEntry {
        SyncEntry(relativePath: path, isDirectory: false, size: size, modificationDate: Date(timeIntervalSince1970: mod))
    }

    private func folder(_ path: String) -> SyncEntry {
        SyncEntry(relativePath: path, isDirectory: true, size: 0, modificationDate: Date(timeIntervalSince1970: 1_000))
    }

    private func plan(_ source: [SyncEntry], over destination: [SyncEntry], mode: SyncMode, direction: PlanDirection = .leftToRight, tolerance: TimeInterval = SyncPlanner.defaultDateTolerance) async -> SyncPlan {
        await SyncPlanner().plan(sourceEntries: source, destEntries: destination, mode: mode, direction: direction,
                                 sourceIsCloud: false, destIsCloud: true, dateTolerance: tolerance)
    }

    @Test("A plan only matches the direction and mode it was made for")
    func planMatches() async {
        let made = await plan([file("a")], over: [], mode: .mirror)
        #expect(made.matches(mode: .mirror, direction: .leftToRight))
        #expect(!made.matches(mode: .mirror, direction: .rightToLeft))
        #expect(!made.matches(mode: .additive, direction: .leftToRight))
    }

    @Test("Update Newer won't replace a folder with a file")
    func newerKeepsFolders() async {
        let made = await plan([file("x", mod: 9_000)], over: [folder("x"), file("x/keep.txt")], mode: .newer)
        #expect(made.operations.isEmpty)
        #expect(made.typeClashes == 1)
    }

    @Test("Minute-precision dates don't look like changes")
    func dateTolerance() async {
        let source = [file("a", mod: 1_045)]
        let destination = [file("a", mod: 1_000)]
        let exact = await plan(source, over: destination, mode: .mirror)
        #expect(exact.filesToReplace == 1)
        let coarse = await plan(source, over: destination, mode: .mirror, tolerance: SyncPlanner.minuteDateTolerance)
        #expect(coarse.operations.isEmpty)
    }

    @Test("A copy gets the source's date, so the next Mirror has nothing to do")
    func datesSurviveARoundTrip() async throws {
        let provider = FakeProvider()
        provider.addFolder("/backup")
        provider.put("/backup/a.txt", "cloud")
        let local = try temporaryFolder()

        try await withAccount(provider) { accountId in
            let source = SyncEndpoint.cloud(accountId: accountId, rootPath: "/backup")
            let destination = SyncEndpoint.local(local)
            let planner = SyncPlanner()
            let first = await planner.plan(
                sourceEntries: try await planner.enumerate(source),
                destEntries: try await planner.enumerate(destination),
                mode: .mirror, direction: .leftToRight, sourceIsCloud: true, destIsCloud: false
            )
            #expect(first.filesToAdd == 1)
            let progress = TransferProgress(operation: "Mirroring", totalItems: 1)
            await SyncExecutor.execute(plan: first, source: source, destination: destination, progress: progress)
            #expect(progress.failureCount == 0)

            let second = await planner.plan(
                sourceEntries: try await planner.enumerate(source),
                destEntries: try await planner.enumerate(destination),
                mode: .mirror, direction: .leftToRight, sourceIsCloud: true, destIsCloud: false
            )
            #expect(second.operations.isEmpty)
        }
    }

    @Test("An upload asks the provider to keep the source's date")
    func uploadStampsDate() async throws {
        let local = try temporaryFolder()
        try write("x", to: local.appendingPathComponent("a.txt"))
        let provider = FakeProvider()
        provider.addFolder("/backup")
        let date = Date(timeIntervalSince1970: 1_600_000_000)
        let made = SyncPlan(mode: .newer, direction: .leftToRight,
                            operations: [.add(relativePath: "a.txt", isDirectory: false, bytes: 1)],
                            filesToAdd: 1, filesToReplace: 0, filesToDelete: 0, foldersToAdd: 0, foldersToDelete: 0,
                            downloadBytes: 0, uploadBytes: 1, totalBytes: 1, netDestinationDelta: 1,
                            sourceEntryCount: 1, destinationEntryCount: 0,
                            sourceModificationDates: ["a.txt": date])
        try await withAccount(provider) { accountId in
            let progress = TransferProgress(operation: "Adding", totalItems: 1)
            await SyncExecutor.execute(plan: made, source: .local(local),
                                       destination: .cloud(accountId: accountId, rootPath: "/backup"),
                                       progress: progress)
        }
        #expect(provider.stampedDate(of: "/backup/a.txt") == date)
    }

    @Test("Nothing runs against a read-only destination")
    func readOnlyDestination() async throws {
        let provider = FakeProvider(type: .gopro)
        provider.addFolder("/DCIM")
        provider.put("/DCIM/clip.mp4", "footage")
        let local = try temporaryFolder()
        let made = SyncPlan(mode: .mirror, direction: .leftToRight,
                            operations: [.delete(relativePath: "clip.mp4", isDirectory: false, bytes: 7)],
                            filesToAdd: 0, filesToReplace: 0, filesToDelete: 1, foldersToAdd: 0, foldersToDelete: 0,
                            downloadBytes: 0, uploadBytes: 0, totalBytes: 0, netDestinationDelta: -7,
                            sourceEntryCount: 0, destinationEntryCount: 1)
        try await withAccount(provider) { accountId in
            let progress = TransferProgress(operation: "Mirroring", totalItems: 1)
            await SyncExecutor.execute(plan: made, source: .local(local),
                                       destination: .cloud(accountId: accountId, rootPath: "/DCIM"),
                                       progress: progress)
            #expect(progress.hasErrors)
            #expect(progress.isComplete)
        }
        #expect(provider.text("/DCIM/clip.mp4") == "footage")
    }

    @Test("A December file listed in January isn't dated in the future")
    func yearlessDates() throws {
        // A month from now, with no year, can only be last year.
        let future = try #require(Calendar.current.date(byAdding: .day, value: 30, to: Date()))
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "MMM dd"
        let row = "-rw-r--r-- 1 u g 12 \(formatter.string(from: future)) 10:00 a.txt"
        let parsed = try #require(SFTPAPIClient.parseListingLine(row))
        #expect(parsed.modDate < Date())
    }
}

/// Answers nothing, ever: a download that only ends if it's cancelled.
private final class HangingURLProtocol: URLProtocol, @unchecked Sendable {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {}
    override func stopLoading() {}
}

@Suite("Cancel stops work")
struct CancellationTests {

    @Test("A cancelled download ends straight away")
    func download() async throws {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [HangingURLProtocol.self]
        let session = URLSession(configuration: configuration)
        let url = try #require(URL(string: "https://example.invalid/big.bin"))

        let task = Task { try await session.downloadReportingProgress(for: URLRequest(url: url), onBytes: nil) }
        try await Task.sleep(for: .milliseconds(100))
        task.cancel()

        let outcome = await task.result
        guard case .failure = outcome else {
            Issue.record("Expected the download to fail")
            return
        }
    }

    @Test("A task cancelled before its download starts doesn't hang")
    func alreadyCancelled() async throws {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [HangingURLProtocol.self]
        let session = URLSession(configuration: configuration)
        let url = try #require(URL(string: "https://example.invalid/big.bin"))

        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return try await session.downloadReportingProgress(for: URLRequest(url: url), onBytes: nil)
        }
        guard case .failure = await task.result else {
            Issue.record("Expected the download to fail")
            return
        }
    }

    @Test("A running tool is stopped")
    func process() async throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sleep")
        process.arguments = ["30"]
        let cancellation = ProcessCancellation()
        let started = Date()
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            process.terminationHandler = { _ in continuation.resume() }
            try? process.run()
            cancellation.started(process)
            cancellation.cancel()
        }
        #expect(Date().timeIntervalSince(started) < 5)
    }

    @Test("A cancel that came first still stops the tool once it's running")
    func processCancelledEarly() async throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sleep")
        process.arguments = ["30"]
        let cancellation = ProcessCancellation()
        cancellation.cancel()
        let started = Date()
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            process.terminationHandler = { _ in continuation.resume() }
            try? process.run()
            cancellation.started(process)
        }
        #expect(Date().timeIntervalSince(started) < 5)
    }

    @Test("Sizing a folder stops when nobody wants the answer")
    func directorySize() async throws {
        let folder = try temporaryFolder()
        for index in 0..<2_000 {
            try write("x", to: folder.appendingPathComponent("f\(index).txt"))
        }
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return try await FileSystemService.shared.directorySize(at: folder)
        }
        guard case .failure = await task.result else {
            Issue.record("Expected the walk to stop")
            return
        }
    }

    @Test("Local work runs side by side")
    func concurrency() async throws {
        let folder = try temporaryFolder()
        try write("x", to: folder.appendingPathComponent("a.txt"))
        async let first = FileSystemService.shared.listDirectory(at: folder)
        async let second = FileSystemService.shared.itemExists(at: folder.appendingPathComponent("a.txt"))
        #expect(try await first.count == 1)
        #expect(await second)
    }
}

@Suite("Login details go only where they belong")
struct CredentialScopeTests {

    private func url(_ string: String) -> URL? { URL(string: string) }

    @Test("Same place keeps them, anywhere else doesn't")
    func scope() {
        let site = url("https://example.com/blog")
        #expect(CredentialScope.allows(url("https://example.com/wp-content/a.jpg"), for: site))
        #expect(CredentialScope.allows(url("https://EXAMPLE.com:443/a.jpg"), for: site))
        #expect(!CredentialScope.allows(url("https://cdn.example.net/a.jpg"), for: site))
        #expect(!CredentialScope.allows(url("https://www.example.com/a.jpg"), for: site))
        #expect(!CredentialScope.allows(url("https://example.com:8443/a.jpg"), for: site))
    }

    @Test("A move to plain HTTP loses them, an upgrade to HTTPS doesn't")
    func downgrade() {
        #expect(!CredentialScope.allows(url("http://example.com/a"), for: url("https://example.com/")))
        #expect(CredentialScope.allows(url("https://example.com/a"), for: url("http://example.com/")))
        #expect(!CredentialScope.allows(url("https://example.com:8443/a"), for: url("http://example.com:8080/")))
    }

    @Test("Only a password that would cross a public network is flagged")
    func plainHTTP() {
        #expect(CredentialScope.exposesPassword(address: "http://nas.example.com:5005"))
        #expect(CredentialScope.exposesPassword(address: "http://8.8.8.8"))
        #expect(!CredentialScope.exposesPassword(address: "https://nas.example.com"))
        #expect(!CredentialScope.exposesPassword(address: "http://192.168.1.20:5005"))
        #expect(!CredentialScope.exposesPassword(address: "http://10.0.0.5"))
        #expect(!CredentialScope.exposesPassword(address: "http://172.20.1.1"))
        #expect(!CredentialScope.exposesPassword(address: "http://nas.local"))
        #expect(!CredentialScope.exposesPassword(address: "http://localhost:8080"))
        #expect(!CredentialScope.exposesPassword(address: ""))
    }
}

@Suite("Zip extraction")
struct ZipSafetyTests {

    @Test("A zip with a link pointing outside is refused before ditto sees it")
    func escapingLink() async throws {
        let folder = try temporaryFolder()
        try FileManager.default.createSymbolicLink(
            at: folder.appendingPathComponent("link"),
            withDestinationURL: URL(fileURLWithPath: "/etc/hosts")
        )
        let archive = try await ArchiveService.createArchive(names: ["link"], in: folder, archiveName: "out.zip", format: .zip)
        let target = folder.appendingPathComponent("unpacked", isDirectory: true)
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)

        await #expect(throws: ArchiveError.self) {
            try await ArchiveService.extractAll(from: archive, format: .zip, into: target)
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: target.path).isEmpty)
    }

    @Test("An ordinary zip still unpacks")
    func ordinaryZip() async throws {
        let folder = try temporaryFolder()
        try write("x", to: folder.appendingPathComponent("a.txt"))
        let archive = try await ArchiveService.createArchive(names: ["a.txt"], in: folder, archiveName: "out.zip", format: .zip)
        let target = folder.appendingPathComponent("unpacked", isDirectory: true)
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
        try await ArchiveService.extractAll(from: archive, format: .zip, into: target)
        #expect(read(target.appendingPathComponent("a.txt")) == "x")
    }
}

@MainActor
@Suite("Panels")
struct PanelBehaviourTests {

    private func opened(_ folder: URL) async -> FileManagerViewModel {
        let vm = FileManagerViewModel()
        await vm.loadDirectory(at: folder)
        return vm
    }

    @Test("A refresh keeps the selection, less what's gone")
    func refreshKeepsSelection() async throws {
        let folder = try temporaryFolder()
        try write("a", to: folder.appendingPathComponent("a.txt"))
        try write("b", to: folder.appendingPathComponent("b.txt"))
        let vm = await opened(folder)
        vm.selectedItemIDs = Set(vm.items.map(\.id))
        try FileManager.default.removeItem(at: folder.appendingPathComponent("b.txt"))

        await vm.refresh()

        #expect(vm.selectedItems.map(\.name) == ["a.txt"])
        #expect(vm.selectedItemIDs.count == 1)
    }

    @Test("Going up lands on the folder just left")
    func goingUpSelectsChild() async throws {
        let parent = try temporaryFolder()
        let child = parent.appendingPathComponent("child", isDirectory: true)
        try write("x", to: child.appendingPathComponent("a.txt"))
        let vm = await opened(child)

        await vm.loadDirectory(at: parent)

        #expect(vm.selectedItems.map(\.name) == ["child"])
    }

    @Test("Going into a folder starts with nothing selected")
    func goingDownSelectsNothing() async throws {
        let parent = try temporaryFolder()
        let child = parent.appendingPathComponent("child", isDirectory: true)
        try write("x", to: child.appendingPathComponent("a.txt"))
        let vm = await opened(parent)
        vm.selectedItemIDs = Set(vm.items.map(\.id))

        await vm.loadDirectory(at: child)

        #expect(vm.selectedItemIDs.isEmpty)
    }

    @Test("A failure survives the refresh that follows it")
    func errorSurvivesRefresh() async throws {
        let folder = try temporaryFolder()
        let vm = await opened(folder)
        vm.error = "Failed to rename"

        await vm.refresh()
        #expect(vm.error == "Failed to rename")

        await vm.loadDirectory(at: folder.appendingPathComponent("missing"))
        #expect(vm.error != nil)
        #expect(vm.error != "Failed to rename")
    }

    @Test("A refused folder is reported")
    func unreadableFolder() async throws {
        let folder = try temporaryFolder()
        let vm = await opened(folder)
        await vm.loadDirectory(at: folder.appendingPathComponent("does-not-exist"))
        #expect(vm.error != nil)
    }

    @Test("A failed cloud action keeps the file list up")
    func cloudActionError() async throws {
        let provider = FakeProvider()
        provider.put("/a.txt", "x")
        provider.failDelete(of: "/a.txt")
        try await withAccount(provider) { accountId in
            let vm = CloudFileManagerViewModel(accountId: accountId)
            await vm.loadDirectory()
            let item = try #require(vm.items.first)
            await vm.deleteItems([item])
            #expect(vm.actionError != nil)
            #expect(vm.error == nil)
            #expect(vm.items.count == 1)
        }
    }

    @Test("The setting for hidden files is what a new panel starts with")
    func hiddenFilesDefault() {
        let key = "showHiddenFiles"
        let before = UserDefaults.standard.object(forKey: key)
        defer { UserDefaults.standard.set(before, forKey: key) }

        UserDefaults.standard.set(true, forKey: key)
        #expect(FileManagerViewModel().showHiddenFiles)
        #expect(CloudFileManagerViewModel(accountId: UUID()).showHiddenFiles)
        UserDefaults.standard.set(false, forKey: key)
        #expect(!FileManagerViewModel().showHiddenFiles)
    }

    @Test("No preset binds one key to two commands")
    func noDuplicateShortcuts() {
        let manager = KeyboardShortcutManager.shared
        for preset in KeyboardShortcutManager.Preset.allCases {
            let bindings = manager.defaults(for: preset)
            let grouped = Dictionary(grouping: bindings, by: { $0.value })
            let clashes = grouped.filter { $0.value.count > 1 }.map { $0.value.map(\.key.rawValue).sorted() }
            #expect(clashes.isEmpty, "\(preset): \(clashes)")
        }
    }

    @Test("Quick Look follows the shortcut that's set")
    func quickLookShortcut() throws {
        func keyDown(_ code: UInt16, _ text: String) throws -> NSEvent {
            try #require(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                                          windowNumber: 0, context: nil, characters: text,
                                          charactersIgnoringModifiers: text, isARepeat: false, keyCode: code))
        }
        let manager = KeyboardShortcutManager.shared
        let saved = manager.binding(for: .quickLook)
        defer { manager.setBinding(saved, for: .quickLook) }

        manager.setBinding(.init(key: "f3", modifiers: []), for: .quickLook)
        #expect(manager.isQuickLook(try keyDown(99, "\u{F706}")))
        #expect(!manager.isQuickLook(try keyDown(49, " ")))
    }

    @Test("Starting and finishing a transfer doesn't get stuck")
    func transferActivity() {
        let progress = TransferProgress(operation: "Copying", totalItems: 1)
        progress.isComplete = true
        progress.isComplete = true
        #expect(progress.isComplete)
    }
}
