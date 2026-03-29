import SwiftUI
import Combine

@Observable @MainActor
final class SyncViewModel {
    var accounts: [CloudAccount] = []
    var syncRules: [SyncRule] = []
    var isAddingAccount: Bool = false
    var isAddingRule: Bool = false
    var authError: String?

    private let syncEngine = SyncEngine.shared
    private static let accountsKey = "cloudAccounts"

    init() {
        loadAccounts()
    }

    func addPCloudAccount(email: String, password: String) async {
        let account = CloudAccount(providerType: .pCloud)
        let provider = PCloudProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(email: email, password: password)
            var connectedAccount = account

            let displayName = try? await provider.userDisplayName()
            if let displayName, !displayName.isEmpty {
                connectedAccount.displayName = displayName
            }

            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addAccount(type: CloudProviderType) async {
        let account = CloudAccount(providerType: type)
        let provider = await syncEngine.createProvider(for: type)
        authError = nil

        do {
            try await provider.authenticate()
            var connectedAccount = account
            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func removeAccount(_ account: CloudAccount) async {
        accounts.removeAll { $0.id == account.id }
        syncRules.removeAll { $0.accountId == account.id }
        await syncEngine.removeProvider(for: account.id)
        saveAccounts()
    }

    func reconnectSavedAccounts() async {
        for account in accounts where account.providerType == .pCloud {
            let provider = PCloudProvider(accountId: account.id)
            if await provider.isAuthenticated {
                await syncEngine.registerProvider(for: account.id, provider: provider)
            }
        }
    }

    func addSyncRule(localPath: URL, remotePath: String, accountId: UUID, direction: SyncDirection) {
        let rule = SyncRule(
            localPath: localPath,
            remotePath: remotePath,
            accountId: accountId,
            direction: direction
        )
        syncRules.append(rule)
    }

    func removeSyncRule(_ rule: SyncRule) {
        syncRules.removeAll { $0.id == rule.id }
    }

    func toggleRule(_ rule: SyncRule) {
        guard let index = syncRules.firstIndex(where: { $0.id == rule.id }) else { return }
        syncRules[index].isEnabled.toggle()
    }

    func syncNow(rule: SyncRule) async {
        guard let index = syncRules.firstIndex(where: { $0.id == rule.id }) else { return }
        syncRules[index].status = .syncing

        do {
            try await syncEngine.sync(rule: rule)
            syncRules[index].status = .idle
            syncRules[index].lastSyncDate = Date()
            syncRules[index].errorMessage = nil
        } catch {
            syncRules[index].status = .error
            syncRules[index].errorMessage = error.localizedDescription
        }
    }

    func syncAll() async {
        let enabledRules = syncRules.filter(\.isEnabled)
        for rule in enabledRules {
            await syncNow(rule: rule)
        }
    }

    func accountFor(id: UUID) -> CloudAccount? {
        accounts.first { $0.id == id }
    }

    // MARK: - Persistence

    private func saveAccounts() {
        if let data = try? JSONEncoder().encode(accounts) {
            UserDefaults.standard.set(data, forKey: Self.accountsKey)
        }
    }

    private func loadAccounts() {
        guard let data = UserDefaults.standard.data(forKey: Self.accountsKey),
              let saved = try? JSONDecoder().decode([CloudAccount].self, from: data) else {
            return
        }
        accounts = saved
    }
}
