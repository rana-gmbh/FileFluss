import SwiftUI

@Observable @MainActor
final class SyncViewModel {
    var accounts: [CloudAccount] = []
    var syncRules: [SyncRule] = []
    var isAddingAccount: Bool = false
    var isAddingRule: Bool = false
    var authError: String?

    // OneDrive OAuth state
    var isAuthenticatingOneDrive: Bool = false

    // Google Drive OAuth state
    var isAuthenticatingGoogleDrive: Bool = false

    // Dropbox OAuth state
    var isAuthenticatingDropbox: Bool = false

    // Box OAuth state
    var isAuthenticatingBox: Bool = false

    // NextCloud Login Flow v2 state (browser-based sign-in)
    var isAuthenticatingNextCloud: Bool = false

    private let syncEngine = SyncEngine.shared
    private static let accountsKey = "cloudAccounts"

    init() {
        loadAccounts()
    }

    func addPCloudAccount(email: String, password: String) async {
        await connectPCloud { provider in
            try await provider.authenticate(email: email, password: password)
        }
    }

    func addPCloudAccount(accessToken: String) async {
        await connectPCloud { provider in
            try await provider.authenticate(accessToken: accessToken)
        }
    }

    private func connectPCloud(_ authenticate: (PCloudProvider) async throws -> Void) async {
        let account = CloudAccount(providerType: .pCloud)
        let provider = PCloudProvider(accountId: account.id)
        authError = nil

        do {
            try await authenticate(provider)
            var connectedAccount = account

            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(userName))"
            }

            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch CloudProviderError.serverError(1022) {
            authError = "pCloud now requires OAuth for third-party apps, and new app registrations are disabled. Sign in at my.pcloud.com, copy the 'pcauth' cookie value, and paste it into the Access Token field."
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

    func addGMXCloudAccount(email: String, password: String) async {
        let account = CloudAccount(providerType: .gmxCloud)
        let provider = GMXCloudProvider(accountId: account.id)
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
        isAuthenticatingOneDrive = true

        do {
            let credentials = try await provider.startOAuthFlow()
            isAuthenticatingOneDrive = false

            var connectedAccount = account
            if !credentials.userEmail.isEmpty, credentials.userEmail != "Unknown" {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(credentials.userEmail))"
            }

            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            isAuthenticatingOneDrive = false
            authError = error.localizedDescription
        }
    }

    func addNextCloudAccountViaBrowser(serverURL: String) async {
        let account = CloudAccount(providerType: .nextCloud)
        let provider = NextCloudProvider(accountId: account.id)
        authError = nil
        isAuthenticatingNextCloud = true

        do {
            let credentials = try await provider.startLoginFlowV2(serverURL: serverURL)
            isAuthenticatingNextCloud = false

            var connectedAccount = account
            if !credentials.displayName.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(credentials.displayName))"
            }
            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            isAuthenticatingNextCloud = false
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

    func addSFTPAccount(
        host: String,
        port: Int,
        username: String,
        password: String? = nil,
        privateKey: String? = nil,
        passphrase: String? = nil,
        remotePath: String = "/"
    ) async {
        let resolvedRemotePath = remotePath.isEmpty ? "/" : remotePath
        var account = CloudAccount(providerType: .sftp)
        account.rootPath = resolvedRemotePath
        let provider = SFTPProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(
                host: host,
                port: port,
                username: username,
                password: password,
                privateKey: privateKey,
                passphrase: passphrase,
                remotePath: resolvedRemotePath
            )
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

    func addBoxAccount() async {
        let account = CloudAccount(providerType: .box)
        let provider = BoxProvider(accountId: account.id)
        authError = nil
        isAuthenticatingBox = true

        do {
            let credentials = try await provider.startOAuthFlow()
            isAuthenticatingBox = false

            var connectedAccount = account
            if !credentials.displayName.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(credentials.displayName))"
            }
            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            isAuthenticatingBox = false
            authError = error.localizedDescription
        }
    }

    func addICloudAccount() async {
        authError = nil
        // Disallow more than one iCloud account — they all back the same
        // CloudDocs folder, so duplicates would only confuse the sidebar.
        if accounts.contains(where: { $0.providerType == .iCloud }) {
            authError = "iCloud Drive is already added."
            return
        }
        let provider = ICloudProvider()
        do {
            try await provider.authenticate()
            var account = CloudAccount(providerType: .iCloud)
            account.displayName = "iCloud Drive"
            account.isConnected = true
            accounts.append(account)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addS3CompatibleAccount(
        accessKeyId: String,
        secretAccessKey: String,
        endpoint: String,
        region: String,
        displayName: String?
    ) async {
        let account = CloudAccount(providerType: .s3Compatible)
        let provider = S3CompatibleProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(
                accessKeyId: accessKeyId,
                secretAccessKey: secretAccessKey,
                endpoint: endpoint,
                region: region,
                displayName: displayName
            )
            var connectedAccount = account
            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = userName
            }
            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addSynologyC2Account(accessKeyId: String, secretAccessKey: String, region: String) async {
        let account = CloudAccount(providerType: .synologyC2)
        let provider = SynologyC2Provider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(
                accessKeyId: accessKeyId,
                secretAccessKey: secretAccessKey,
                region: region
            )
            var connectedAccount = account
            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = userName
            }
            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addSynologyDriveAccount(
        serverURL: String,
        username: String,
        password: String,
        otp: String?,
        allowSelfSignedCertificate: Bool
    ) async {
        let account = CloudAccount(providerType: .synologyDrive)
        let provider = SynologyDriveProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(
                serverURL: serverURL,
                username: username,
                password: password,
                otp: otp,
                allowSelfSignedCertificate: allowSelfSignedCertificate
            )
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

    func addS3Account(accessKeyId: String, secretAccessKey: String, region: String) async {
        let account = CloudAccount(providerType: .s3)
        let provider = S3Provider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(
                accessKeyId: accessKeyId,
                secretAccessKey: secretAccessKey,
                region: region
            )
            var connectedAccount = account

            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = userName
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

    /// Returns true when the provider's auth flow can be re-run from inside
    /// the panel — currently the loopback-OAuth providers (Google, Dropbox,
    /// Box). Anything else needs the Settings → Cloud Accounts sheet because
    /// it relies on credentials the user must type in.
    func providerSupportsInlineReauth(_ type: CloudProviderType) -> Bool {
        switch type {
        case .googleDrive, .dropbox, .box, .oneDrive: return true
        default: return false
        }
    }

    /// Re-runs the OAuth flow for an existing account, reusing the same
    /// `accountId` so the stored credentials slot is overwritten in the
    /// keychain. Only handles providers reported by
    /// `providerSupportsInlineReauth(_:)`; for the rest the caller should
    /// open Settings → Cloud Accounts instead.
    func reauthenticate(accountId: UUID) async {
        guard let account = accounts.first(where: { $0.id == accountId }) else { return }
        authError = nil
        do {
            switch account.providerType {
            case .googleDrive:
                let provider = GoogleDriveProvider(accountId: accountId)
                _ = try await provider.startOAuthFlow()
                await syncEngine.registerProvider(for: accountId, provider: provider)
            case .dropbox:
                let provider = DropboxProvider(accountId: accountId)
                _ = try await provider.startOAuthFlow()
                await syncEngine.registerProvider(for: accountId, provider: provider)
            case .box:
                let provider = BoxProvider(accountId: accountId)
                _ = try await provider.startOAuthFlow()
                await syncEngine.registerProvider(for: accountId, provider: provider)
            case .oneDrive:
                let provider = OneDriveProvider(accountId: accountId)
                _ = try await provider.startOAuthFlow()
                await syncEngine.registerProvider(for: accountId, provider: provider)
            default:
                return
            }
            if let idx = accounts.firstIndex(where: { $0.id == accountId }) {
                accounts[idx].isConnected = true
                saveAccounts()
            }
        } catch {
            authError = error.localizedDescription
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
            case .gmxCloud:
                let provider = GMXCloudProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .s3:
                let provider = S3Provider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .synologyDrive:
                let provider = SynologyDriveProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .synologyC2:
                let provider = SynologyC2Provider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .s3Compatible:
                let provider = S3CompatibleProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .iCloud:
                let provider = ICloudProvider()
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .box:
                let provider = BoxProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
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
