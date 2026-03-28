import Testing
import Foundation
@testable import FileFluss

@Suite("SyncRule Tests")
struct SyncRuleTests {

    @Test("SyncRule initializes with defaults")
    func defaults() {
        let rule = SyncRule(
            localPath: URL(filePath: "/Users/test/Documents"),
            remotePath: "/Backup/Documents",
            accountId: UUID()
        )

        #expect(rule.direction == .bidirectional)
        #expect(rule.isEnabled)
        #expect(rule.status == .idle)
        #expect(rule.excludePatterns.isEmpty)
        #expect(rule.lastSyncDate == nil)
        #expect(rule.errorMessage == nil)
    }

    @Test("SyncDirection has correct display names and icons")
    func directionDisplayProperties() {
        for direction in SyncDirection.allCases {
            #expect(!direction.displayName.isEmpty)
            #expect(!direction.icon.isEmpty)
        }
    }

    @Test("SyncStatus has correct display names")
    func statusDisplayNames() {
        #expect(SyncStatus.idle.displayName == "Up to date")
        #expect(SyncStatus.syncing.displayName == "Syncing...")
        #expect(SyncStatus.paused.displayName == "Paused")
        #expect(SyncStatus.error.displayName == "Error")
    }

    @Test("SyncRule is Codable")
    func codable() throws {
        let original = SyncRule(
            localPath: URL(filePath: "/Users/test/Documents"),
            remotePath: "/Backup",
            accountId: UUID(),
            direction: .upload,
            isEnabled: false,
            excludePatterns: ["*.tmp", ".DS_Store"]
        )

        let data = try JSONEncoder().encode(original)
        let decoded = try JSONDecoder().decode(SyncRule.self, from: data)

        #expect(decoded.id == original.id)
        #expect(decoded.localPath == original.localPath)
        #expect(decoded.remotePath == original.remotePath)
        #expect(decoded.direction == .upload)
        #expect(!decoded.isEnabled)
        #expect(decoded.excludePatterns == ["*.tmp", ".DS_Store"])
    }
}
