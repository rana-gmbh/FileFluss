import Testing
import Foundation
@testable import FileFluss

/// Restoring a panel has to survive a world that moved on while the app was
/// closed: deleted folders, unplugged drives, removed cloud accounts
/// (issue #49). Opening onto an error would be worse than opening at home.
@Suite("Panel startup state")
@MainActor
struct PanelStartupStateTests {

    @Test("an existing folder is restorable")
    func existingFolderIsUsable() {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        #expect(PanelStartupState.isUsable(.local(path: home), connectedAccountIDs: []))
    }

    @Test("a folder that no longer exists is not restored")
    func missingFolderIsRejected() {
        let gone = "/Volumes/NoSuchDrive-\(UUID().uuidString)/Documents"
        #expect(!PanelStartupState.isUsable(.local(path: gone), connectedAccountIDs: []))
    }

    @Test("a file is not a folder to open")
    func fileIsRejected() throws {
        let file = FileManager.default.temporaryDirectory
            .appendingPathComponent("startup-test-\(UUID().uuidString).txt")
        try "x".write(to: file, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: file) }
        #expect(!PanelStartupState.isUsable(.local(path: file.path), connectedAccountIDs: []))
    }

    /// The stored form has to carry a remote path back unchanged — issue
    /// #60 was reported against SFTP, whose paths are plain POSIX ones and
    /// easy to assume are something cleverer. Tested through Codable rather
    /// than UserDefaults: a test has no business writing the app's own
    /// preferences.
    @Test("a remote path survives being stored and read back")
    func cloudLocationRoundTrips() throws {
        let account = UUID()
        let original = PanelLocation.cloud(accountId: account, path: "/var/www/html/wp-content")

        let data = try JSONEncoder().encode(original)
        let restored = try JSONDecoder().decode(PanelLocation.self, from: data)

        #expect(restored == original)
        if case .cloud(let id, let path) = restored {
            #expect(id == account)
            #expect(path == "/var/www/html/wp-content")
        } else {
            Issue.record("expected a cloud location")
        }
    }

    @Test("a cloud panel is restored only while its account is connected")
    func cloudNeedsAConnectedAccount() {
        let account = UUID()
        let location = PanelLocation.cloud(accountId: account, path: "/Photos")
        #expect(PanelStartupState.isUsable(location, connectedAccountIDs: [account]))
        // Signed out, or removed since the last session.
        #expect(!PanelStartupState.isUsable(location, connectedAccountIDs: []))
        #expect(!PanelStartupState.isUsable(location, connectedAccountIDs: [UUID()]))
    }

    @Test("locations survive a round trip through storage")
    func roundTrip() {
        let left = PanelLocation.local(path: "/Users/someone/Documents")
        let right = PanelLocation.cloud(accountId: UUID(), path: "/Team/Reports")
        PanelStartupState.setSpecificFolders(left: left, right: right)
        let restored = PanelStartupState.specificFolders()
        #expect(restored.left == left)
        #expect(restored.right == right)
    }
}
