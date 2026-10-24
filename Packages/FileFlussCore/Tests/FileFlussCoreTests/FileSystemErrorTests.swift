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
}
