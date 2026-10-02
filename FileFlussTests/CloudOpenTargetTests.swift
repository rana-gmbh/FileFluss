import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

/// Where a panel goes when its sidebar selection becomes a cloud account.
///
/// Issue #60: with "the folders from last time", an SFTP panel came back at
/// `/` instead of the folder it was left in. Restoring set the selection and
/// navigated to the saved folder, while the selection change sent the same
/// panel to the account's root — two navigations, and the last one to start
/// decided.
@Suite("Cloud open target")
@MainActor
struct CloudOpenTargetTests {

    private func account(rootPath: String = "/") -> CloudAccount {
        CloudAccount(providerType: .sftp, displayName: "Server", isConnected: true, rootPath: rootPath)
    }

    @Test("A panel pointed at a folder opens that folder, not the root")
    func honoursPendingPath() {
        let acc = account()
        let pending = AppState.PendingCloudOpen(accountId: acc.id, path: "/var/www/project")

        #expect(AppState.cloudOpenTarget(for: acc, pending: pending) == "/var/www/project")
    }

    @Test("A click with nothing pending opens the account root")
    func plainSelectionOpensRoot() {
        #expect(AppState.cloudOpenTarget(for: account(), pending: nil) == "/")
    }

    /// An account whose root is a bucket path or a configured remote
    /// directory still opens there.
    @Test("The account's own root is respected")
    func respectsAccountRoot() {
        let acc = CloudAccount(providerType: .s3, displayName: "Bucket", rootPath: "/my-bucket/data")

        #expect(AppState.cloudOpenTarget(for: acc, pending: nil) == "/my-bucket/data")
    }

    @Test("An empty account root means the root")
    func emptyRootMeansSlash() {
        #expect(AppState.cloudOpenTarget(for: account(rootPath: ""), pending: nil) == "/")
    }

    /// Both panels can be restored at once, and each announces its own
    /// target: one panel's intent must never steer the other's account.
    @Test("A target meant for another account is ignored")
    func ignoresAnotherAccountsTarget() {
        let acc = account()
        let other = AppState.PendingCloudOpen(accountId: UUID(), path: "/somewhere/else")

        #expect(AppState.cloudOpenTarget(for: acc, pending: other) == "/")
    }

    @Test("An empty path is not a destination")
    func emptyPendingPathFallsBack() {
        let acc = account(rootPath: "/home/bernd")
        let pending = AppState.PendingCloudOpen(accountId: acc.id, path: "")

        #expect(AppState.cloudOpenTarget(for: acc, pending: pending) == "/home/bernd")
    }
}
