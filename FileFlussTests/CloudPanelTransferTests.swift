import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

// MARK: - Names a Mac stores as one

@Suite("Siblings that are one name on this Mac keep a name each")
struct LocalNameClaimsTests {

    private let nfc = "Caf\u{E9}.txt"
    private let nfd = "Cafe\u{301}.txt"

    @Test("README.md and Readme.md: the first in ordinal order keeps its name")
    func caseClash() {
        #expect(LocalNameClaims.localNames(for: ["Readme.md", "README.md"], caseSensitive: false)
            == ["Readme (2).md", "README.md"])
    }

    @Test("On a case-sensitive volume both keep their names")
    func caseSensitiveVolume() {
        #expect(LocalNameClaims.localNames(for: ["Readme.md", "README.md"], caseSensitive: true)
            == ["Readme.md", "README.md"])
    }

    @Test("The NFC and NFD spellings of one name clash on every Mac volume")
    func unicodeForms() {
        let names = LocalNameClaims.localNames(for: [nfd, nfc], caseSensitive: true)
        // NFC (U+00E9) sorts after NFD's plain "e", so NFD keeps the name.
        #expect(Array(names[0].unicodeScalars) == Array(nfd.unicodeScalars))
        #expect(names[1] == "Caf\u{E9} (2).txt")
    }

    @Test("A real 'name (2)' among the siblings pushes the clash on to (3)")
    func takenCounter() {
        #expect(LocalNameClaims.localNames(for: ["README.md", "Readme (2).md", "Readme.md"], caseSensitive: false)
            == ["README.md", "Readme (2).md", "Readme (3).md"])
    }

    @Test("Two files of exactly one name, as Google Drive allows")
    func duplicates() {
        #expect(LocalNameClaims.localNames(for: ["Report.pdf", "Report.pdf"], caseSensitive: true)
            == ["Report.pdf", "Report (2).pdf"])
    }

    @Test("Names without clashes are left alone")
    func noClash() {
        #expect(LocalNameClaims.localNames(for: ["a.txt", "b", ".env"], caseSensitive: false) == ["a.txt", "b", ".env"])
    }
}

// MARK: - Downloads

@MainActor
@Suite("A cloud folder download loses none of its files")
struct CloudFolderDownloadTests {

    @Test("A folder holding README.md and Readme.md comes down with both")
    func caseClashInsideFolder() async throws {
        let provider = FakeProvider()
        provider.addFolder("/docs")
        provider.put("/docs/README.md", "upper")
        provider.put("/docs/Readme.md", "lower")
        let destination = try temporaryFolder()
        try #require(!LocalNameClaims.isCaseSensitive(destination), "the test needs a volume that ignores case")

        try await withAccount(provider) { accountId in
            let vm = CloudFileManagerViewModel(accountId: accountId)
            let folder = CloudFileItem(id: "/docs", name: "docs", path: "/docs", isDirectory: true, size: 0, modificationDate: .distantPast, checksum: nil)
            let progress = TransferProgress(operation: "Moving", totalItems: 1)
            let downloaded = await vm.downloadItems([folder], to: destination, progress: progress, skipConflictCheck: true)
            #expect(downloaded == ["docs"])
        }
        let docs = destination.appendingPathComponent("docs")
        let names = try FileManager.default.contentsOfDirectory(atPath: docs.path).sorted()
        #expect(names == ["README.md", "Readme (2).md"])
        #expect(read(docs.appendingPathComponent("README.md")) == "upper")
        #expect(read(docs.appendingPathComponent("Readme (2).md")) == "lower")
    }

    @Test("Staged for another cloud, each top-level item is found where it landed")
    func stagedLocations() async throws {
        let provider = FakeProvider()
        provider.put("/README.md", "upper")
        provider.put("/Readme.md", "lower")
        let staging = try temporaryFolder()

        try await withAccount(provider) { accountId in
            let vm = CloudFileManagerViewModel(accountId: accountId)
            let upper = CloudFileItem(id: "/README.md", name: "README.md", path: "/README.md", isDirectory: false, size: 5, modificationDate: .distantPast, checksum: nil)
            let lower = CloudFileItem(id: "/Readme.md", name: "Readme.md", path: "/Readme.md", isDirectory: false, size: 5, modificationDate: .distantPast, checksum: nil)
            let progress = TransferProgress(operation: "Copying", totalItems: 2)
            progress.isCloudToCloud = true
            let landed = await vm.downloadItemsLocating([lower, upper], to: staging, progress: progress, skipConflictCheck: true)
            let upperURL = try #require(landed[upper.id]?.url)
            let lowerURL = try #require(landed[lower.id]?.url)
            #expect(read(upperURL) == "upper")
            #expect(read(lowerURL) == "lower")
            #expect(landed[upper.id]?.renamedForClash == false)
            #expect(landed[lower.id]?.renamedForClash == true)
        }
    }

    /// The listing has no MIME type, so a Google item without an MD5 is
    /// taken for a Workspace document. Everything else has one.
    @Test("Only a Google Workspace document may land under another name")
    func exportExtensionsOnlyForWorkspaceDocuments() {
        let folder = CloudFileItem(id: "d1", name: "Report", path: "/Report", isDirectory: true, size: 0, modificationDate: .distantPast, checksum: nil)
        let binary = CloudFileItem(id: "f1", name: "Report", path: "/Report", isDirectory: false, size: 3, modificationDate: .distantPast, checksum: "abc")
        let document = CloudFileItem(id: "f2", name: "Report", path: "/Report", isDirectory: false, size: 0, modificationDate: .distantPast, checksum: nil)
        #expect(CloudFileManagerViewModel.exportExtensions(for: folder, providerType: .googleDrive).isEmpty)
        #expect(CloudFileManagerViewModel.exportExtensions(for: binary, providerType: .googleDrive).isEmpty)
        #expect(CloudFileManagerViewModel.exportExtensions(for: document, providerType: .webDAV).isEmpty)
        #expect(CloudFileManagerViewModel.exportExtensions(for: document, providerType: .googleDrive) == ["docx", "xlsx", "pptx", "pdf"])
        #expect(CloudFileManagerViewModel.exportExtensions(for: document, providerType: .googleDrivePicker).contains("pdf"))
    }

    @Test("Only the item's own earlier export counts as that item")
    func exportMatch() {
        let directory = URL(fileURLWithPath: "/tmp/dl", isDirectory: true)
        let extensions = CloudFileManagerViewModel.googleExportExtensions
        #expect(CloudFileManagerViewModel.isExport(directory.appendingPathComponent("Doc.pdf"), ofLocalName: "Doc", in: directory, extensions: extensions))
        #expect(!CloudFileManagerViewModel.isExport(directory.appendingPathComponent("Doc.pdf"), ofLocalName: "Doc", in: directory, extensions: []))
        #expect(!CloudFileManagerViewModel.isExport(directory.appendingPathComponent("Doc"), ofLocalName: "Doc", in: directory, extensions: extensions))
        #expect(!CloudFileManagerViewModel.isExport(directory.appendingPathComponent("Other.pdf"), ofLocalName: "Doc", in: directory, extensions: extensions))
    }

    /// Replace on a Google folder called `Report` used to delete the
    /// unrelated `Report.pdf` beside it — for good.
    @Test("Downloading a Google folder named Report leaves Report.pdf alone", arguments: [false, true])
    func unrelatedExportNameSurvives(skipConflictCheck: Bool) async throws {
        let provider = FakeProvider(type: .googleDrive)
        provider.addFolder("/Report")
        provider.put("/Report/inside.txt", "inside")
        let destination = try temporaryFolder()
        let unrelated = destination.appendingPathComponent("Report.pdf")
        try write("my own pdf", to: unrelated)

        try await withAccount(provider) { accountId in
            let vm = CloudFileManagerViewModel(accountId: accountId, providerType: .googleDrive)
            let folder = CloudFileItem(id: "dReport", name: "Report", path: "/Report", isDirectory: true, size: 0, modificationDate: .distantPast, checksum: nil)
            // Before the fix this asked about Report.pdf; whoever answers
            // "Replace" then loses it.
            let answerer = Task { @MainActor in
                while !Task.isCancelled {
                    if ConflictPrompter.shared.current?.conflict.source.name == "Report" {
                        ConflictPrompter.shared.resolve(ConflictResolution(choice: .replace, applyToAll: false))
                    }
                    try? await Task.sleep(for: .milliseconds(10))
                }
            }
            defer { answerer.cancel() }
            let downloaded = await vm.downloadItems([folder], to: destination, skipConflictCheck: skipConflictCheck)
            #expect(downloaded == ["Report"])
        }
        #expect(read(unrelated) == "my own pdf")
        #expect(read(destination.appendingPathComponent("Report/inside.txt")) == "inside")
    }
}

// MARK: - Quarantine

@MainActor
@Suite("Cloud files opened from FileFluss are marked as downloaded")
struct CloudOpenQuarantineTests {

    @Test("A copy fetched to open or preview carries the quarantine mark")
    func downloadToTempMarks() async throws {
        let provider = FakeProvider()
        provider.put("/Shared/run.command", "#!/bin/sh\necho hi\n")

        try await withAccount(provider) { accountId in
            let vm = CloudFileManagerViewModel(accountId: accountId)
            let item = CloudFileItem(id: "/Shared/run.command", name: "run.command", path: "/Shared/run.command", isDirectory: false, size: 18, modificationDate: Date(timeIntervalSince1970: 1_000), checksum: nil)
            let url = try #require(await vm.downloadToTemp(item))
            // The account's own preview cache folder.
            defer { try? FileManager.default.removeItem(at: url.deletingLastPathComponent().deletingLastPathComponent()) }
            #expect(Quarantine.isMarked(url))

            // A copy cached unmarked, by an earlier version, is marked when
            // it is handed out again.
            // (Fresh URLs throughout: a URL caches the values it has read.)
            removexattr(url.path, "com.apple.quarantine", 0)
            #expect(!Quarantine.isMarked(URL(fileURLWithPath: url.path)))
            let again = try #require(await vm.downloadToTemp(item))
            #expect(again.path == url.path)
            #expect(Quarantine.isMarked(URL(fileURLWithPath: again.path)))
        }
    }

    @Test("Entries unpacked from a cloud archive are marked, even when the archive isn't")
    func cloudArchiveEntriesMarked() async throws {
        let base = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussQuarantine-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: base) }
        let root = base.resolvingSymlinksInPath()
        try write("#!/bin/sh\n", to: root.appendingPathComponent("Kit/run.command"))
        let archive = try await ArchiveService.createZip(names: ["Kit"], in: root, archiveName: "Kit.zip")
        #expect(!Quarantine.isMarked(archive))
        let tree = try await ArchiveService.list(archive, format: .zip)

        let local = ArchiveBrowseSession(archiveURL: archive, format: .zip, tree: tree)
        let fromDisk = try await local.materialise(innerPaths: ["Kit/run.command"])
        #expect(fromDisk.count == 1)
        #expect(!Quarantine.isMarked(fromDisk[0]), "an archive of the user's own stays as it is")
        local.close()

        let cloud = ArchiveBrowseSession(archiveURL: archive, format: .zip, tree: tree, isUntrusted: true)
        let fromCloud = try await cloud.materialise(innerPaths: ["Kit/run.command"])
        #expect(fromCloud.count == 1)
        #expect(Quarantine.isMarked(fromCloud[0]))
        cloud.close()
    }
}

// MARK: - Uploads

@MainActor
@Suite("Uploading a local folder")
struct CloudFolderUploadTests {

    @Test("Sources are inspected off the main actor with the same answers")
    func inspection() async throws {
        let folder = try temporaryFolder()
        try write("12345", to: folder.appendingPathComponent("tree/a.txt"))
        try write("123", to: folder.appendingPathComponent("tree/sub/b.txt"))
        try write("1", to: folder.appendingPathComponent("single.txt"))
        let tree = folder.appendingPathComponent("tree")
        let single = folder.appendingPathComponent("single.txt")

        let inspected = CloudFileManagerViewModel.inspectUploadSources([tree, single, folder.appendingPathComponent("gone")], measuringBytes: true)
        #expect(inspected.folderNames == ["tree"])
        #expect(inspected.bytes == 9)

        let file = try #require(try await CloudFileManagerViewModel.inspectLocalUploadItem(single))
        #expect(!file.isDirectory)
        #expect(file.size == 1)
        #expect(file.modificationDate != nil)
        let directory = try #require(try await CloudFileManagerViewModel.inspectLocalUploadItem(tree))
        #expect(directory.isDirectory)
        #expect(Set(directory.children.map(\.lastPathComponent)) == ["a.txt", "sub"])
        #expect(try await CloudFileManagerViewModel.inspectLocalUploadItem(folder.appendingPathComponent("gone")) == nil)
    }

    @Test("A nested folder uploads whole, and the bar learns its size")
    func nestedUpload() async throws {
        let provider = FakeProvider()
        let folder = try temporaryFolder()
        try write("12345", to: folder.appendingPathComponent("tree/a.txt"))
        try write("123", to: folder.appendingPathComponent("tree/sub/b.txt"))

        try await withAccount(provider) { accountId in
            let vm = CloudFileManagerViewModel(accountId: accountId)
            let progress = TransferProgress(operation: "Uploading", totalItems: 1)
            await vm.uploadFiles(from: [folder.appendingPathComponent("tree")], toPath: "/", progress: progress, skipConflictCheck: true)
            #expect(progress.succeededNames == ["tree"])
            // The size is measured alongside the upload and may report a
            // moment later; the folder name always does.
            for _ in 0..<200 where progress.transferredFolderNames.isEmpty {
                try? await Task.sleep(for: .milliseconds(10))
            }
            #expect(progress.transferredFolderNames == ["tree"])
        }
        #expect(provider.text("/tree/a.txt") == "12345")
        #expect(provider.text("/tree/sub/b.txt") == "123")
    }
}
