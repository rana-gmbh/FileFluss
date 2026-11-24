import Testing
import Foundation
@testable import FileFlussCore

/// Telling apart the file-system failures that have a remedy from the ones
/// that are just failures.
@Suite("File system errors")
struct FileSystemErrorTests {

    /// A customer's support log showed three of these in a row, reported to
    /// him as "failed" with nothing he could do about it. macOS withholding
    /// a protected folder has a remedy, and the app has to recognise it to
    /// offer one.
    @Test("A refusal by macOS is recognised as a permission problem")
    func cocoaPermissionError() {
        let error = NSError(domain: NSCocoaErrorDomain, code: NSFileWriteNoPermissionError)

        #expect(FileSystemService.isPermissionError(error))
        #expect(!FileSystemService.isNoTrashOnVolumeError(error))
    }

    @Test("A POSIX refusal counts too")
    func posixPermissionErrors() {
        #expect(FileSystemService.isPermissionError(
            NSError(domain: NSPOSIXErrorDomain, code: Int(EACCES))
        ))
        #expect(FileSystemService.isPermissionError(
            NSError(domain: NSPOSIXErrorDomain, code: Int(EPERM))
        ))
    }

    /// Cocoa usually hands back its own error with the real one inside.
    @Test("A wrapped refusal is found inside")
    func wrappedPermissionError() {
        let inner = NSError(domain: NSPOSIXErrorDomain, code: Int(EACCES))
        let outer = NSError(
            domain: NSCocoaErrorDomain,
            code: NSFileWriteUnknownError,
            userInfo: [NSUnderlyingErrorKey: inner]
        )

        #expect(FileSystemService.isPermissionError(outer))
    }

    /// A volume without a Trash is a different problem with a different
    /// answer — deleting outright — and must not be confused with this one.
    @Test("A volume without a Trash is not a permission problem")
    func noTrashIsItsOwnCase() {
        let error = CocoaError(.featureUnsupported)

        #expect(FileSystemService.isNoTrashOnVolumeError(error))
        #expect(!FileSystemService.isPermissionError(error))
    }

    @Test("An ordinary failure is neither")
    func unrelatedError() {
        let error = NSError(domain: NSCocoaErrorDomain, code: NSFileNoSuchFileError)

        #expect(!FileSystemService.isPermissionError(error))
        #expect(!FileSystemService.isNoTrashOnVolumeError(error))
    }

    // MARK: - Why the Trash was refused

    private let me: uid_t = 501
    private func folder(_ path: String, owner: uid_t = 501, writable: Bool = true) -> FileSystemService.FolderFacts {
        .init(path: path, ownerUID: owner, isWritable: writable)
    }

    /// The customer who could delete a file but not trash it, with Full
    /// Disk Access granted: the Trash folder is the one thing that differs.
    @Test("A Trash folder owned by someone else is named as the cause")
    func trashOwnedByRoot() {
        let verdict = FileSystemService.classifyTrashRefusal(
            trash: folder("/Users/m/.Trash", owner: 0, writable: false),
            parent: folder("/Users/m/Documents"), userID: me)

        #expect(verdict == .trashFolderUnwritable(trashPath: "/Users/m/.Trash", ownerUID: 0))
    }

    @Test("A Trash folder that's ours but shut is named too")
    func trashNotWritable() {
        let verdict = FileSystemService.classifyTrashRefusal(
            trash: folder("/Users/m/.Trash", writable: false),
            parent: folder("/Users/m/Documents"), userID: me)

        #expect(verdict == .trashFolderUnwritable(trashPath: "/Users/m/.Trash", ownerUID: me))
    }

    @Test("A folder the user can't change is told apart")
    func parentNotWritable() {
        let verdict = FileSystemService.classifyTrashRefusal(
            trash: folder("/Users/m/.Trash"),
            parent: folder("/Library/Shared", owner: 0, writable: false), userID: me)

        #expect(verdict == .folderNotWritable(folderPath: "/Library/Shared"))
    }

    @Test("With nothing visibly wrong the cause stays unknown")
    func nothingVisible() {
        #expect(FileSystemService.classifyTrashRefusal(
            trash: folder("/Users/m/.Trash"), parent: folder("/Users/m/Documents"), userID: me) == .unknown)
        #expect(FileSystemService.classifyTrashRefusal(trash: nil, parent: nil, userID: me) == .unknown)
    }

    /// A customer's report: a file on a Desktop synced with iCloud. Its Trash
    /// couldn't even be looked up, so nothing was "visibly wrong".
    @Test("An item in iCloud Drive is named as such")
    func iCloudDriveItem() {
        #expect(FileSystemService.classifyTrashRefusal(
            trash: nil, parent: folder("/Users/m/Desktop"), userID: me, inICloudDrive: true) == .iCloudDrive)
        // Its own Trash folder doesn't matter for an iCloud Drive item…
        #expect(FileSystemService.classifyTrashRefusal(
            trash: folder("/Users/m/.Trash", owner: 0, writable: false),
            parent: folder("/Users/m/Desktop"), userID: me, inICloudDrive: true) == .iCloudDrive)
        // …but a folder the user can't change still can't give the item up.
        #expect(FileSystemService.classifyTrashRefusal(
            trash: nil, parent: folder("/Users/m/Desktop/Shared", owner: 0, writable: false),
            userID: me, inICloudDrive: true) == .folderNotWritable(folderPath: "/Users/m/Desktop/Shared"))
    }

    @Test("iCloud Drive is recognised by its file provider, wherever it is")
    func recognisesICloudDrive() {
        let domains = [
            "/Users/m/Desktop": "com.apple.CloudDocs.iCloudDriveFileProvider/79B89A7C",
            "/Users/m/Dropbox": "com.getdropbox.dropbox.fileprovider/abc",
        ]
        func isICloud(_ path: String) -> Bool {
            FileSystemService.isInICloudDrive(URL(fileURLWithPath: path), home: "/Users/m") { domains[$0] }
        }
        #expect(isICloud("/Users/m/Desktop/Scan.pdf"))
        #expect(isICloud("/Users/m/Desktop/Scans/a.pdf"))
        #expect(isICloud("/Users/m/Library/Mobile Documents/com~apple~CloudDocs/a.txt"))
        #expect(!isICloud("/Users/m/Downloads/a.pdf"))
        #expect(!isICloud("/Users/m/Dropbox/a.pdf"))
        #expect(!isICloud("/Volumes/USB/a.pdf"))
    }

    /// macOS doesn't let anyone set the file provider attribute itself, so
    /// the reader is tried on an attribute of our own.
    @Test("Extended attributes are read from a real folder")
    func readsExtendedAttribute() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        #expect(FileSystemService.fileProviderDomain(ofPath: dir.path) == nil)

        let name = "com.rana.filefluss.test"
        let value = Array("com.apple.CloudDocs.iCloudDriveFileProvider/X".utf8)
        #expect(setxattr(dir.path, name, value, value.count, 0, 0) == 0)
        #expect(FileSystemService.extendedAttribute(name, ofPath: dir.path) == "com.apple.CloudDocs.iCloudDriveFileProvider/X")
        #expect(FileSystemService.extendedAttribute(name, ofPath: dir.appendingPathComponent("missing").path) == nil)
    }

    @Test("Facts about a real folder are read without listing it")
    func readsRealFolder() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }

        let facts = try #require(FileSystemService.FolderFacts.read(dir))
        #expect(facts.ownerUID == getuid())
        #expect(facts.isWritable)
        #expect(FileSystemService.FolderFacts.read(dir.appendingPathComponent("missing")) == nil)
    }

    /// The command is pasted into a shell, so a path with a quote in it
    /// must not break out of its quoting.
    @Test("The repair command quotes the path safely")
    func repairCommandQuoting() {
        #expect(FileSystemService.trashRepairCommand(trashPath: "/Users/m/.Trash")
                == #"sudo chown "$(id -u)" '/Users/m/.Trash' && sudo chmod u+rwx '/Users/m/.Trash'"#)
        #expect(FileSystemService.trashRepairCommand(trashPath: "/Volumes/Bob's/.Trashes/501")
                .contains(#"'/Volumes/Bob'\''s/.Trashes/501'"#))
    }

    @Test("The log names every error in the chain")
    func errorChain() {
        let inner = NSError(domain: NSPOSIXErrorDomain, code: Int(EACCES))
        let outer = NSError(domain: NSCocoaErrorDomain, code: NSFileWriteNoPermissionError,
                            userInfo: [NSUnderlyingErrorKey: inner])

        #expect(FileSystemService.errorChain(outer) == "NSCocoaErrorDomain 513 ← NSPOSIXErrorDomain 13")
    }
}
