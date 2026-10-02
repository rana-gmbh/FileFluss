import Testing
import Foundation
import NetFS
@testable import FileFluss

/// The options FileFluss hands NetFS when it mounts a cloud account in
/// Finder. The spelling is the whole feature: these are untyped dictionary
/// keys, so a wrong one is accepted silently and simply does nothing.
@Suite("Loopback mount options")
struct LoopbackMountOptionsTests {

    private var options: [String: Any] {
        LoopbackMountService.mountOpenOptions() as? [String: Any] ?? [:]
    }

    /// Issue #59: every mount raised macOS's "Unsecured Connection" alert,
    /// because the no-UI request was written as `"NoUI": true` — the value
    /// used as a key, with a boolean. NetFS ignored it. The key is
    /// `kNAUIOptionKey` ("UIOption") and "NoUI" is `kNAUIOptionNoUI`, its
    /// value.
    @Test("NetFS is asked to show no dialogs, in the spelling it reads")
    func suppressesNetFSDialogs() {
        #expect(options["UIOption"] as? String == "NoUI")
        // The mistake that shipped: the value standing in for the key.
        #expect(options["NoUI"] == nil)
    }

    @Test("A loopback mount is allowed")
    func allowsLoopback() {
        #expect(options["AllowLoopback"] as? Bool == true)
    }

    /// Guest is what makes suppressing the UI safe: the loopback server
    /// binds to 127.0.0.1 and asks for no credentials, so there is nothing
    /// NetFS would need to prompt for.
    @Test("The mount goes in as a guest, ignoring saved server preferences")
    func mountsAsGuest() {
        #expect(options[kNetFSUseGuestKey as String] as? Bool == true)
        #expect(options[kNetFSNoUserPreferencesKey as String] as? Bool == true)
    }

    /// Broken on Tahoe for WebDAV — it fails any mount path we propose, so
    /// NetFS has to pick the mount point itself.
    @Test("No mount directory is proposed")
    func proposesNoMountDirectory() {
        #expect(options[kNetFSMountAtMountDirKey as String] == nil)
    }

    @Test("An error NetFS can no longer ask about is explained")
    func explainsSilentFailures() {
        let auth = LoopbackMountService.netfsErrorMessage(status: EAUTH)
        #expect(auth.contains("credentials"))

        let noShares = LoopbackMountService.netfsErrorMessage(
            status: LoopbackMountService.ENETFSNOSHARESAVAIL
        )
        #expect(noShares.contains("nothing to mount"))

        // Ordinary errno values still read as themselves.
        #expect(LoopbackMountService.netfsErrorMessage(status: ENOENT).contains("errno 2"))
    }
}
