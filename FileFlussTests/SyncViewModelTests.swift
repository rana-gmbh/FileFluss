import Testing
import Foundation
@testable import FileFluss

@Suite("SyncViewModel Tests")
@MainActor
struct SyncViewModelTests {

    @Test("Add and remove sync rules")
    func syncRuleCRUD() {
        let vm = SyncViewModel()
        let accountId = UUID()

        vm.addSyncRule(
            localPath: URL(filePath: "/Users/test/Documents"),
            remotePath: "/Backup",
            accountId: accountId,
            direction: .upload
        )

        #expect(vm.syncRules.count == 1)
        #expect(vm.syncRules[0].direction == .upload)
        #expect(vm.syncRules[0].localPath.path().hasSuffix("Documents"))

        let rule = vm.syncRules[0]
        vm.removeSyncRule(rule)
        #expect(vm.syncRules.isEmpty)
    }

    @Test("Toggle rule enables/disables")
    func toggleRule() {
        let vm = SyncViewModel()
        vm.addSyncRule(
            localPath: URL(filePath: "/tmp"),
            remotePath: "/",
            accountId: UUID(),
            direction: .bidirectional
        )

        #expect(vm.syncRules[0].isEnabled)

        vm.toggleRule(vm.syncRules[0])
        #expect(!vm.syncRules[0].isEnabled)

        vm.toggleRule(vm.syncRules[0])
        #expect(vm.syncRules[0].isEnabled)
    }

    @Test("Account lookup by ID")
    func accountLookup() async {
        let vm = SyncViewModel()
        let account = CloudAccount(providerType: .nextCloud, displayName: "My NC")
        vm.accounts.append(account)

        #expect(vm.accountFor(id: account.id)?.displayName == "My NC")
        #expect(vm.accountFor(id: UUID()) == nil)
    }

    @Test("Remove account also removes associated sync rules")
    func removeAccountCascades() async {
        let vm = SyncViewModel()
        let account = CloudAccount(providerType: .pCloud, isConnected: true)
        vm.accounts.append(account)

        vm.addSyncRule(
            localPath: URL(filePath: "/tmp/a"),
            remotePath: "/a",
            accountId: account.id,
            direction: .upload
        )
        vm.addSyncRule(
            localPath: URL(filePath: "/tmp/b"),
            remotePath: "/b",
            accountId: account.id,
            direction: .download
        )
        vm.addSyncRule(
            localPath: URL(filePath: "/tmp/c"),
            remotePath: "/c",
            accountId: UUID(), // different account
            direction: .bidirectional
        )

        #expect(vm.syncRules.count == 3)

        await vm.removeAccount(account)

        #expect(vm.accounts.isEmpty)
        #expect(vm.syncRules.count == 1)
        #expect(vm.syncRules[0].remotePath == "/c")
    }

    @Test("Multiple rules can coexist")
    func multipleRules() {
        let vm = SyncViewModel()
        let id = UUID()

        for i in 0..<5 {
            vm.addSyncRule(
                localPath: URL(filePath: "/tmp/folder\(i)"),
                remotePath: "/remote\(i)",
                accountId: id,
                direction: SyncDirection.allCases[i % 3]
            )
        }

        #expect(vm.syncRules.count == 5)
        let paths = Set(vm.syncRules.map(\.remotePath))
        #expect(paths.count == 5)
    }
}
