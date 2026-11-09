import Testing
import Foundation
@testable import FileFlussCore

/// Moving and copying within one SFTP account, which used to download the
/// file and upload it again (issue #62).
@Suite("SFTP server-side transfers")
struct SFTPServerSideTransferTests {

    // MARK: The commands

    @Test("A move is a rename")
    func renameCommand() {
        #expect(
            SFTPAPIClient.renameCommand(from: "/home/me/clip.mp4", to: "/home/me/videos/clip.mp4")
            == #"rename "/home/me/clip.mp4" "/home/me/videos/clip.mp4""#
        )
    }

    @Test("A copy is cp")
    func copyCommand() {
        #expect(
            SFTPAPIClient.copyCommand(from: "/a/x.txt", to: "/b/x.txt")
            == #"cp "/a/x.txt" "/b/x.txt""#
        )
    }

    /// These strings are handed to another program, so a path carrying a
    /// quote, a backslash or a `$` has to come out the other side as the
    /// same path — and those are the paths nobody tries by hand.
    @Test("Paths that mean something to a shell are escaped")
    func escapesAwkwardPaths() {
        #expect(SFTPAPIClient.shellEscape("/tmp/plain.txt") == #""/tmp/plain.txt""#)
        #expect(SFTPAPIClient.shellEscape("/tmp/with space.txt") == #""/tmp/with space.txt""#)
        #expect(SFTPAPIClient.shellEscape(#"/tmp/qu"ote.txt"#) == #""/tmp/qu\"ote.txt""#)
        #expect(SFTPAPIClient.shellEscape("/tmp/$HOME.txt") == #""/tmp/\$HOME.txt""#)
        #expect(SFTPAPIClient.shellEscape("/tmp/back`tick.txt") == #""/tmp/back\`tick.txt""#)
        #expect(SFTPAPIClient.shellEscape(#"/tmp/back\slash.txt"#) == #""/tmp/back\\slash.txt""#)
    }

    @Test("An escaped path still appears inside the command")
    func commandsUseTheEscapedForm() {
        let command = SFTPAPIClient.renameCommand(from: #"/a/"odd" name.txt"#, to: "/b/plain.txt")

        #expect(command.hasPrefix("rename "))
        #expect(command.contains(#"\"odd\" name.txt"#))
    }

    // MARK: What a refusal means

    /// A server without the copy-data extension — OpenSSH before 9.0, or
    /// something that isn't OpenSSH — can't copy server-side at all. That
    /// has to read as "not supported", so the caller falls back to moving
    /// the bytes through, rather than as a failed file.
    @Test("A server without copy-data reads as unsupported")
    func unsupportedCopyIsRecognised() {
        #expect(SFTPAPIClient.isUnsupportedCopy(
            CloudProviderError.commandFailed("Server does not support copy-data extension")
        ))
        #expect(SFTPAPIClient.isUnsupportedCopy(
            CloudProviderError.commandFailed("Invalid command.")
        ))
        #expect(SFTPAPIClient.isRefusedServerSideOperation(
            CloudProviderError.commandFailed("server does not support copy-data extension")
        ))
    }

    /// The reporter's own caveat: a rename across filesystems on the
    /// server can't work, and copying the bytes through is the only way.
    @Test("A cross-device rename falls back rather than failing")
    func crossDeviceRenameFallsBack() {
        #expect(SFTPAPIClient.isRefusedServerSideOperation(
            CloudProviderError.commandFailed("rename: Invalid cross-device link")
        ))
        #expect(SFTPAPIClient.isRefusedServerSideOperation(CloudProviderError.notImplemented))
    }

    /// An ordinary failure must stay a failure: falling back on a denied
    /// permission would download and re-upload a file the server was never
    /// going to accept, and report success for the copy.
    @Test("An ordinary failure is not a reason to fall back")
    func realFailuresAreNotFallbacks() {
        #expect(!SFTPAPIClient.isRefusedServerSideOperation(
            CloudProviderError.commandFailed("rename: Permission denied")
        ))
        #expect(!SFTPAPIClient.isRefusedServerSideOperation(
            CloudProviderError.notFound("/no/such/file")
        ))
        #expect(!SFTPAPIClient.isUnsupportedCopy(
            CloudProviderError.commandFailed("cp: Permission denied")
        ))
    }
}
