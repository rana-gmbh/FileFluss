import SwiftUI
import Combine

@Observable @MainActor
final class SyncViewModel {
    var accounts: [CloudAccount] = []
    var syncRules: [SyncRule] = []
    var isAddingAccount: Bool = false
    var isAddingRule: Bool = false
    var authError: String?

    // OneDrive device code flow state
    var oneDriveDeviceCode: OneDriveDeviceCode?
    var isPollingForOneDrive: Bool = false

    // Google Drive OAuth state
    var isAuthenticatingGoogleDrive: Bool = false

    // Dropbox OAuth state
    var isAuthenticatingDropbox: Bool = false

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

            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(userName))"
            }

            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addKDriveAccount(apiToken: String) async {
        let account = CloudAccount(providerType: .kDrive)
        let provider = KDriveProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(apiToken: apiToken)
            var connectedAccount = account

            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(userName))"
            }

            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addGoogleDriveAccount() async {
        let account = CloudAccount(providerType: .googleDrive)
        let provider = GoogleDriveProvider(accountId: account.id)
        authError = nil
        isAuthenticatingGoogleDrive = true

        do {
            let credentials = try await provider.startOAuthFlow()
            isAuthenticatingGoogleDrive = false

            var connectedAccount = account
            if !credentials.displayName.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(credentials.displayName))"
            }

            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            isAuthenticatingGoogleDrive = false
            authError = error.localizedDescription
        }
    }

    func addDropboxAccount() async {
        let account = CloudAccount(providerType: .dropbox)
        let provider = DropboxProvider(accountId: account.id)
        authError = nil
        isAuthenticatingDropbox = true

        do {
            let credentials = try await provider.startOAuthFlow()
            isAuthenticatingDropbox = false

            var connectedAccount = account
            if !credentials.displayName.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(credentials.displayName))"
            }

            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            isAuthenticatingDropbox = false
            authError = error.localizedDescription
        }
    }

    func addOneDriveAccount() async {
        let account = CloudAccount(providerType: .oneDrive)
        let provider = OneDriveProvider(accountId: account.id)
        authError = nil
        oneDriveDeviceCode = nil
        isPollingForOneDrive = false

        do {
            let deviceCode = try await provider.startDeviceCodeFlow()
            oneDriveDeviceCode = deviceCode
            isPollingForOneDrive = true

            try await provider.completeDeviceCodeFlow(deviceCode: deviceCode.deviceCode)
            isPollingForOneDrive = false
            oneDriveDeviceCode = nil

            var connectedAccount = account
            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(userName))"
            }

            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            isPollingForOneDrive = false
            oneDriveDeviceCode = nil
            authError = error.localizedDescription
        }
    }

    func addNextCloudAccount(serverURL: String, username: String, appPassword: String) async {
        let account = CloudAccount(providerType: .nextCloud)
        let provider = NextCloudProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(serverURL: serverURL, username: username, appPassword: appPassword)
            var connectedAccount = account

            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(userName))"
            }

            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addMegaAccount(email: String, password: String) async {
        let account = CloudAccount(providerType: .mega)
        let provider = MegaProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(email: email, password: password)
            var connectedAccount = account

            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(userName))"
            }

            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addSFTPAccount(host: String, port: Int, username: String, password: String) async {
        let account = CloudAccount(providerType: .sftp)
        let provider = SFTPProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(host: host, port: port, username: username, password: password)
            var connectedAccount = account

            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(userName))"
            }

            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addWordPressAccount(siteURL: String, username: String, appPassword: String) async {
        let account = CloudAccount(providerType: .wordpress)
        let provider = WordPressProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(siteURL: siteURL, username: username, appPassword: appPassword)
            var connectedAccount = account

            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(userName))"
            }

            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addWebDAVAccount(serverURL: String, username: String, password: String) async {
        let account = CloudAccount(providerType: .webDAV)
        let provider = WebDAVProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(serverURL: serverURL, username: username, password: password)
            var connectedAccount = account

            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(userName))"
            }

            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addKoofrAccount(email: String, appPassword: String) async {
        let account = CloudAccount(providerType: .koofr)
        let provider = KoofrProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(email: email, appPassword: appPassword)
            var connectedAccount = account

            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(userName))"
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

    func renameAccount(id: UUID, to newName: String) {
        if let idx = accounts.firstIndex(where: { $0.id == id }) {
            accounts[idx].displayName = newName
            saveAccounts()
        }
    }

    func reconnectSavedAccounts() async {
        for account in accounts {
            switch account.providerType {
            case .googleDrive:
                let provider = GoogleDriveProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .pCloud:
                let provider = PCloudProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .kDrive:
                let provider = KDriveProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .oneDrive:
                let provider = OneDriveProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .nextCloud:
                let provider = NextCloudProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .koofr:
                let provider = KoofrProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .dropbox:
                let provider = DropboxProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .mega:
                let provider = MegaProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .webDAV:
                let provider = WebDAVProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .sftp:
                let provider = SFTPProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .wordpress:
                let provider = WordPressProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            default:
                break
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

    func saveAccounts() {
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
