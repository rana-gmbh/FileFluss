import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

/// Debug-only: the test accounts a developer sets up once and keeps.
@Suite("Test account snapshot")
@MainActor
struct TestAccountSnapshotTests {

    private let a = CloudAccount(providerType: .box, displayName: "Box")
    private let b = CloudAccount(providerType: .dropbox, displayName: "Dropbox")

    /// The installed release app shares the preferences, and removing test
    /// accounts there removed them from Debug as well.
    @Test("Debug builds keep their own account list")
    func ownListKey() {
        #expect(SyncViewModel.accountsKey != SyncViewModel.sharedAccountsKey)
    }

    @Test("The first Debug launch starts from the shared list")
    func seededFromSharedList() throws {
        let defaults = SyncViewModel.defaults
        let savedOwn = defaults.data(forKey: SyncViewModel.accountsKey)
        let savedShared = defaults.data(forKey: SyncViewModel.sharedAccountsKey)
        defer {
            defaults.set(savedOwn, forKey: SyncViewModel.accountsKey)
            defaults.set(savedShared, forKey: SyncViewModel.sharedAccountsKey)
        }
        defaults.removeObject(forKey: SyncViewModel.accountsKey)
        defaults.set(try JSONEncoder().encode([a]), forKey: SyncViewModel.sharedAccountsKey)

        let vm = SyncViewModel()

        #expect(vm.accounts.map(\.id) == [a.id])
        #expect(defaults.data(forKey: SyncViewModel.accountsKey) != nil)
    }

    @Test("A first save holds exactly what's here")
    func firstSave() {
        let snapshot = TestAccountSnapshot.merged(
            current: [a], credentials: ["box.\(a.id.uuidString)": Data([1])], into: nil, at: Date())

        #expect(snapshot.accounts.map(\.id) == [a.id])
        #expect(snapshot.credentials.count == 1)
    }

    /// Refreshing after a version test must not drop an account that was
    /// removed by accident: that's what the saved set is there to bring back.
    @Test("A refresh updates what's here and keeps what isn't")
    func refreshKeepsMissing() {
        let earlier = TestAccountSnapshot.Snapshot(
            savedAt: .distantPast, accounts: [a, b],
            credentials: ["box.\(a.id.uuidString)": Data([1]), "dropbox.\(b.id.uuidString)": Data([2])])

        let snapshot = TestAccountSnapshot.merged(
            current: [a], credentials: ["box.\(a.id.uuidString)": Data([9])], into: earlier, at: Date())

        #expect(Set(snapshot.accounts.map(\.id)) == [a.id, b.id])
        #expect(snapshot.credentials["box.\(a.id.uuidString)"] == Data([9]))
        #expect(snapshot.credentials["dropbox.\(b.id.uuidString)"] == Data([2]))
    }

    @Test("Credentials of an account that's gone from both are dropped")
    func orphanCredentialsDropped() {
        let stray = UUID()
        let earlier = TestAccountSnapshot.Snapshot(
            savedAt: .distantPast, accounts: [a],
            credentials: ["mega.\(stray.uuidString)": Data([3])])

        let snapshot = TestAccountSnapshot.merged(current: [a], credentials: [:], into: earlier, at: Date())

        #expect(snapshot.credentials.isEmpty)
    }
}
