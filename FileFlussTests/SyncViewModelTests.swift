import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

@Suite("SyncViewModel Tests")
@MainActor
struct SyncViewModelTests {

    @Test("Account lookup by ID")
    func accountLookup() async {
        let vm = SyncViewModel()
        let account = CloudAccount(providerType: .nextCloud, displayName: "My NC")
        vm.accounts.append(account)

        #expect(vm.accountFor(id: account.id)?.displayName == "My NC")
        #expect(vm.accountFor(id: UUID()) == nil)
    }
}
