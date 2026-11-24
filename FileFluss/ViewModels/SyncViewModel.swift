import SwiftUI
import AppKit
import FileFlussCore

@Observable @MainActor
final class SyncViewModel {
    var accounts: [CloudAccount] = []
    var isAddingAccount: Bool = false
    var authError: String?

    // TeraBox device-code (QR) login state. The view shows `teraboxQRImageData`
    // while the user scans, then we poll for the token.
    var teraboxQRImageData: Data?
    var isAuthenticatingTeraBox: Bool = false

    // OneDrive OAuth state
    var isAuthenticatingOneDrive: Bool = false

    // Google Drive OAuth state
    var isAuthenticatingGoogleDrive: Bool = false

    // Dropbox OAuth state
    var isAuthenticatingDropbox: Bool = false

    // Box OAuth state
    var isAuthenticatingBox: Bool = false

    // pCloud OAuth state
    var isAuthenticatingPCloud: Bool = false

    // Drime OAuth state
    var isAuthenticatingDrime: Bool = false

    // NextCloud Login Flow v2 state (browser-based sign-in)
    var isAuthenticatingNextCloud: Bool = false

    private let syncEngine = SyncEngine.shared
    #if DEBUG
    /// Debug builds keep their own list. They share the bundle ID, and so
    /// the preferences, of the installed release app, which showed every
    /// test account without its credentials (Debug keeps those in its own
    /// folder, not the Keychain). Removing them there removed them here too,
    /// and the whole set had to be added again for the next version test.
    static let accountsKey = "debugCloudAccounts"
    static let sharedAccountsKey = "cloudAccounts"
    #else
    static let accountsKey = "cloudAccounts"
    #endif

    /// True when a stored account list existed but couldn't be decoded. In
    /// that state `accounts` is empty through no fault of the user, and
    /// writing that emptiness back would destroy the real list — so saving
    /// is suppressed until the user actually changes something.
    private var accountsLoadFailed = false

    /// Where accounts are persisted. The unit tests run *inside the app
    /// bundle*, so they share its preferences domain: a test that builds a
    /// `SyncViewModel` and triggers a save would otherwise overwrite the
    /// developer's real, hard-won list of connected accounts. Under XCTest
    /// we therefore persist to a scratch suite instead.
    static var defaults: UserDefaults {
        guard ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil
                || ProcessInfo.processInfo.environment["XCTestSessionIdentifier"] != nil
                || NSClassFromString("XCTestCase") != nil else {
            return .standard
        }
        return UserDefaults(suiteName: "com.rana-gmbh.FileFluss.tests") ?? .standard
    }

    init() {
        loadAccounts()
    }

    /// Per-account quota cache. In-memory only — fresh on every app
    /// launch, refreshed in the background while a panel is open. A
    /// short TTL avoids hammering the cloud APIs while still reflecting
    /// recent uploads/deletes (each successful write also invalidates).
    private var quotaCache: [UUID: CloudStorageQuota] = [:]
    private static let quotaTTL: TimeInterval = 120

    /// Returns the storage quota for an account, fetching when stale.
    /// On network failure returns whatever value is currently cached
    /// (or nil) so the status bar can keep showing the last-known
    /// figure rather than flickering empty.
    func storageQuota(for accountId: UUID) async -> CloudStorageQuota? {
        if let cached = quotaCache[accountId],
           Date().timeIntervalSince(cached.fetchedAt) < Self.quotaTTL {
            return cached
        }
        guard let provider = await syncEngine.provider(for: accountId) else {
            return quotaCache[accountId]
        }
        do {
            if let fresh = try await provider.storageQuota() {
                quotaCache[accountId] = fresh
                return fresh
            }
        } catch {
            // Best-effort — leave the cached entry alone so a transient
            // failure doesn't blank the status bar.
        }
        return quotaCache[accountId]
    }

    /// Drops the cached entry so the next storageQuota(for:) call
    /// hits the API. Use after a successful upload/delete/createFolder
    /// from this account so the bar updates promptly.
    func invalidateQuota(for accountId: UUID) {
        quotaCache.removeValue(forKey: accountId)
    }

    /// Clears every browser-OAuth pending flag and any leftover auth
    /// error. Called when the user aborts a pending sign-in (closes the
    /// browser tab and hits Cancel in the Add Account sheet, or just
    /// dismisses the sheet outright). Without this, the flags stay set
    /// for the rest of the session and the Add Account dialog hides
    /// the Connect button permanently — see issue #24.
    func cancelPendingBrowserAuth() {
        isAuthenticatingGoogleDrive = false
        isAuthenticatingDropbox = false
        isAuthenticatingOneDrive = false
        isAuthenticatingNextCloud = false
        isAuthenticatingBox = false
        isAuthenticatingDrime = false
        isAuthenticatingPCloud = false
        authError = nil
    }

    func addPCloudAccount() async {
        let account = CloudAccount(providerType: .pCloud)
        let provider = PCloudProvider(accountId: account.id)
        authError = nil
        isAuthenticatingPCloud = true
        defer { isAuthenticatingPCloud = false }

        do {
            try await provider.startOAuthFlow()
            var connectedAccount = account
            if let userName = try? await provider.userDisplayName(), !userName.isEmpty {
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

    /// Probe an API token before committing it as an account. The UI uses
    /// this to drive a drive picker — kDrive's "organization" plan exposes
    /// a personal drive plus one or more shared workspaces, each a separate
    /// drive in Infomaniak's model.
    func discoverKDriveDrives(apiToken: String) async throws -> KDriveProvider.Discovery {
        try await KDriveProvider.discoverDrives(apiToken: apiToken)
    }

    /// `driveId == nil` adds the first drive (original behaviour). Pass an
    /// explicit id to add a specific workspace. `driveName` is folded into
    /// the displayName when present so the user can tell `kDrive (me)` apart
    /// from `kDrive (me · Common Documents)` in the account list.
    func addKDriveAccount(apiToken: String, driveId: Int? = nil, driveName: String? = nil) async {
        let account = CloudAccount(providerType: .kDrive)
        let provider = KDriveProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(apiToken: apiToken, driveId: driveId)
            var connectedAccount = account

            let userName = try? await provider.userDisplayName()
            let userPart: String
            if let userName, !userName.isEmpty {
                userPart = userName
            } else {
                userPart = ""
            }
            let label: String
            switch (userPart.isEmpty, driveName?.isEmpty ?? true) {
            case (false, false): label = "\(userPart) · \(driveName!)"
            case (false, true): label = userPart
            case (true, false): label = driveName!
            case (true, true): label = ""
            }
            if !label.isEmpty {
                connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(label))"
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

    func addMegaAccount(email: String, password: String, mfaCode: String? = nil) async {
        let account = CloudAccount(providerType: .mega)
        let provider = MegaProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(email: email, password: password, mfaCode: mfaCode)
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
                remotePath: resolvedRemotePath,
                confirmTrust: { question in await Self.confirmSFTPTrust(question) }
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

    func addFTPAccount(
        host: String,
        port: Int,
        username: String,
        password: String,
        remotePath: String = "/",
        useTLS: Bool = false,
        allowInvalidCertificate: Bool = false
    ) async {
        let resolvedRemotePath = remotePath.isEmpty ? "/" : remotePath
        var account = CloudAccount(providerType: .ftp)
        account.rootPath = resolvedRemotePath
        let provider = FTPProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(
                host: host,
                port: port,
                username: username,
                password: password,
                remotePath: resolvedRemotePath,
                useTLS: useTLS,
                allowInvalidCertificate: allowInvalidCertificate
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

    func addSeafileAccount(
        serverURL: String,
        username: String,
        password: String,
        otp: String?,
        allowSelfSignedCertificate: Bool
    ) async {
        let account = CloudAccount(providerType: .seafile)
        let provider = SeafileProvider(accountId: account.id)
        authError = nil

        do {
            let credentials = try await provider.authenticate(
                serverURL: serverURL,
                username: username,
                password: password,
                otp: otp,
                allowSelfSignedCertificate: allowSelfSignedCertificate
            )

            var connectedAccount = account
            connectedAccount.displayName = "Seafile (\(credentials.username))"
            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addFilenAccount(email: String, password: String, twoFactorCode: String) async {
        let account = CloudAccount(providerType: .filen)
        let provider = FilenProvider(accountId: account.id)
        authError = nil
        do {
            let credentials = try await provider.authenticate(
                email: email,
                password: password,
                twoFactorCode: twoFactorCode
            )
            var connectedAccount = account
            connectedAccount.displayName = "Filen (\(credentials.email))"
            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addInternxtAccount(email: String, password: String, twoFactorCode: String) async {
        let account = CloudAccount(providerType: .internxt)
        let provider = InternxtProvider(accountId: account.id)
        authError = nil
        do {
            let credentials = try await provider.authenticate(
                email: email,
                password: password,
                twoFactorCode: twoFactorCode
            )
            var connectedAccount = account
            connectedAccount.displayName = "Internxt (\(credentials.email))"
            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    /// Finalize a "Google Drive (Selected Folders)" account after OAuth + the
    /// Picker. `credentials` come from the OAuth step; `roots` are the folders
    /// the user picked. (Project B — drive.file.)
    func addGoogleDrivePickerAccount(credentials: GoogleDrivePickerCredentials, roots: [GoogleDrivePickedRoot]) async {
        let account = CloudAccount(providerType: .googleDrivePicker)
        let provider = GoogleDrivePickerProvider(accountId: account.id)
        authError = nil
        do {
            let displayName = try provider.finishConnecting(credentials: credentials, roots: roots)
            var connectedAccount = account
            connectedAccount.displayName = "Google Drive (\(displayName))"
            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addJottacloudAccount(personalToken: String) async {
        let account = CloudAccount(providerType: .jottacloud)
        let provider = JottacloudProvider(accountId: account.id)
        authError = nil
        do {
            let username = try await provider.authenticate(personalToken: personalToken)
            var connectedAccount = account
            connectedAccount.displayName = "Jottacloud (\(username))"
            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addGitHubAccount(token: String) async {
        let account = CloudAccount(providerType: .github)
        let provider = GitHubProvider(accountId: account.id)
        authError = nil
        do {
            let login = try await provider.authenticate(token: token)
            var connectedAccount = account
            connectedAccount.displayName = "GitHub (\(login))"
            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addDrimeAccount(personalToken: String) async {
        await connectDrime { try await $0.authenticate(personalToken: personalToken) }
    }

    func addDrimeAccountViaBrowser() async {
        isAuthenticatingDrime = true
        defer { isAuthenticatingDrime = false }
        await connectDrime { try await $0.startOAuthFlow() }
    }

    private func connectDrime(_ signIn: (DrimeProvider) async throws -> DrimeCredentials) async {
        let account = CloudAccount(providerType: .drime)
        let provider = DrimeProvider(accountId: account.id)
        authError = nil
        do {
            let credentials = try await signIn(provider)
            var connectedAccount = account
            let name = credentials.email.isEmpty ? credentials.displayName : credentials.email
            if !name.isEmpty {
                connectedAccount.displayName = "Drime (\(name))"
            }
            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    /// TeraBox device-code login: fetch a QR, surface it for the user to scan
    /// in the TeraBox app, then poll for the token and register the account.
    func connectTeraBox() async {
        authError = nil
        teraboxQRImageData = nil
        isAuthenticatingTeraBox = true
        defer { isAuthenticatingTeraBox = false; teraboxQRImageData = nil }

        let account = CloudAccount(providerType: .terabox)
        let provider = TeraBoxProvider(accountId: account.id)
        do {
            let device = try await provider.beginDeviceLogin()
            teraboxQRImageData = device.qrImageData
            let credentials = try await provider.completeDeviceLogin(device)
            var connectedAccount = account
            connectedAccount.displayName = "TeraBox (\(credentials.userID))"
            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch is CancellationError {
            // The sheet was closed; nothing went wrong.
        } catch {
            authError = error.localizedDescription
        }
    }

    func addICloudAccount() async {
        authError = nil
        // Disallow more than one iCloud account — they all back the same
        // CloudDocs folder, so duplicates would only confuse the sidebar.
        if accounts.contains(where: { $0.providerType == .iCloud }) {
            authError = L10n.text("iCloud Drive is already added.")
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
        displayName: String?,
        rootPath: String? = nil
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
                displayName: displayName,
                rootPath: Self.normalizeS3RootPath(rootPath)
            )

            let normalizedRoot = Self.normalizeS3RootPath(rootPath)
            if let normalizedRoot {
                _ = try await provider.listDirectory(at: normalizedRoot)
            }

            var connectedAccount = account
            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = userName
            }
            if let normalizedRoot {
                connectedAccount.rootPath = normalizedRoot
                connectedAccount.displayName = "\(connectedAccount.displayName) — \(normalizedRoot.dropFirst())"
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

    func addQNAPAccount(
        serverURL: String,
        username: String,
        password: String,
        securityCode: String?,
        allowSelfSignedCertificate: Bool
    ) async {
        let account = CloudAccount(providerType: .qnap)
        let provider = QNAPProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(
                serverURL: serverURL,
                username: username,
                password: password,
                securityCode: securityCode,
                allowSelfSignedCertificate: allowSelfSignedCertificate
            )
            await finishAddingNASAccount(account, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    /// UGREEN and ASUSTOR NAS accounts, both reached over the box's WebDAV
    /// service.
    func addNASWebDAVAccount(
        brand: NASWebDAVBrand,
        serverURL: String,
        username: String,
        password: String,
        allowSelfSignedCertificate: Bool
    ) async {
        let account = CloudAccount(providerType: brand.providerType)
        let provider = NASWebDAVProvider(brand: brand, accountId: account.id)
        authError = nil

        do {
            try await provider.authenticate(
                serverURL: serverURL,
                username: username,
                password: password,
                allowSelfSignedCertificate: allowSelfSignedCertificate
            )
            await finishAddingNASAccount(account, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    /// Names the account "<Maker> (user@host)", the way Synology accounts
    /// are named, then registers it.
    private func finishAddingNASAccount(_ account: CloudAccount, provider: any CloudProvider) async {
        var connectedAccount = account
        if let userName = try? await nasUserDisplayName(provider), !userName.isEmpty {
            connectedAccount.displayName = "\(connectedAccount.providerType.displayName) (\(userName))"
        }
        connectedAccount.isConnected = true
        accounts.append(connectedAccount)
        await syncEngine.registerProvider(for: account.id, provider: provider)
        saveAccounts()
    }

    private func nasUserDisplayName(_ provider: any CloudProvider) async throws -> String? {
        switch provider {
        case let qnap as QNAPProvider: return try await qnap.userDisplayName()
        case let nas as NASWebDAVProvider: return try await nas.userDisplayName()
        default: return nil
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

    func addS3Account(
        accessKeyId: String,
        secretAccessKey: String,
        region: String,
        rootPath: String? = nil
    ) async {
        let account = CloudAccount(providerType: .s3)
        let provider = S3Provider(accountId: account.id)
        authError = nil

        do {
            // The path goes in first: with a bucket named, the connection
            // check probes that bucket instead of the account's bucket list,
            // which a key scoped to one bucket is not allowed to read.
            let normalizedRoot = Self.normalizeS3RootPath(rootPath)
            try await provider.authenticate(
                accessKeyId: accessKeyId,
                secretAccessKey: secretAccessKey,
                region: region,
                rootPath: normalizedRoot
            )

            if let normalizedRoot {
                // Also list it once, so a typo in the folder part surfaces
                // now rather than as an empty panel later.
                _ = try await provider.listDirectory(at: normalizedRoot)
            }

            var connectedAccount = account

            let userName = try? await provider.userDisplayName()
            if let userName, !userName.isEmpty {
                connectedAccount.displayName = userName
            }
            if let normalizedRoot {
                connectedAccount.rootPath = normalizedRoot
                connectedAccount.displayName = "\(connectedAccount.displayName) — \(normalizedRoot.dropFirst())"
            }

            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    /// Trims whitespace and slashes off the user-supplied bucket/prefix
    /// path and re-prefixes a single `/`. Returns nil for empty input so
    /// the caller leaves the account's default rootPath alone.
    static func normalizeS3RootPath(_ raw: String?) -> String? {
        guard let raw else { return nil }
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return trimmed.isEmpty ? nil : "/\(trimmed)"
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

    /// Discovers reachable GoPro cameras (USB / Wi-Fi AP / Bonjour). Surfaced
    /// by the Add-account "Scan" button; returns confirmed cameras only.
    func scanForGoProCameras() async -> [GoProCamera] {
        await GoProDiscovery.scan()
    }

    func addGoProAccount(camera: GoProCamera) async {
        let account = CloudAccount(providerType: .gopro)
        let provider = GoProProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.connect(camera: camera)
            var connectedAccount = account
            connectedAccount.displayName = camera.name
            connectedAccount.isConnected = true
            accounts.append(connectedAccount)
            await syncEngine.registerProvider(for: account.id, provider: provider)
            saveAccounts()
        } catch {
            authError = error.localizedDescription
        }
    }

    func addGoProCOHNAccount(ipAddress: String, username: String, password: String) async {
        let account = CloudAccount(providerType: .gopro)
        let provider = GoProProvider(accountId: account.id)
        authError = nil

        do {
            try await provider.connectCOHN(ipAddress: ipAddress, username: username, password: password, cameraName: nil)
            var connectedAccount = account
            connectedAccount.displayName = (try? await provider.userDisplayName()) ?? L10n.text("GoPro Camera")
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

    func removeAccount(_ account: CloudAccount) async {
        accounts.removeAll { $0.id == account.id }
        await syncEngine.removeProvider(for: account.id)
        saveAccounts()
        // Drop search-index rows so the account no longer surfaces under
        // Settings → Index Status as an orphan with a stale name.
        await SearchIndex.shared.dropIndexedCloudSource(accountId: account.id)
        // And the tags FileFluss kept for its items.
        await TagStore.shared.removeAccount(account.id)
    }

    /// Flip the user-controlled offline-mode flag. When on, the sidebar
    /// row routes its panel to `OfflineSourceView` and the search popup
    /// treats this account as offline — even if the live provider would
    /// otherwise be reachable. Persists immediately so the choice
    /// survives a relaunch.
    func setOfflineMode(_ enabled: Bool, accountId: UUID) {
        guard let idx = accounts.firstIndex(where: { $0.id == accountId }) else { return }
        guard accounts[idx].isOfflineMode != enabled else { return }
        accounts[idx].isOfflineMode = enabled
        saveAccounts()
    }

    func renameAccount(id: UUID, to newName: String) {
        if let idx = accounts.firstIndex(where: { $0.id == id }) {
            accounts[idx].displayName = newName
            saveAccounts()
            // Keep Settings → Index Status in sync — `indexed_sources`
            // stores a snapshot of the display name captured at indexing
            // time, so rename it directly instead of waiting for the next
            // full crawl to overwrite it.
            Task.detached {
                await SearchIndex.shared.renameIndexedSource(sourceId: id.uuidString, to: newName)
            }
        }
    }

    /// How an account whose sign-in stopped working gets it back. Decides
    /// what the panel's Reconnect button does.
    enum ReconnectMethod: Equatable {
        /// The provider's own sign-in page, in the browser — nothing to type
        /// in FileFluss.
        case browser
        /// The account's credentials sheet, filled in with whatever is
        /// still saved: a password, an app password, a token, a QR code.
        case credentials
        /// The Mac's own iCloud sign-in, in System Settings.
        case systemSettings
        /// Nothing FileFluss can ask for again: the account has to be
        /// removed and added afresh (a folder pick, a paired camera).
        case addAgain
    }

    func reconnectMethod(for type: CloudProviderType) -> ReconnectMethod {
        switch type {
        case .googleDrive, .dropbox, .box, .oneDrive, .pCloud:
            return .browser
        case .drime:
            return DrimeAPIClient.isOAuthAvailable ? .browser : .credentials
        case .iCloud:
            return .systemSettings
        case .googleDrivePicker, .gopro:
            return .addAgain
        case .nextCloud, .koofr, .mega, .webDAV, .sftp, .ftp, .wordpress, .gmxCloud, .s3,
             .synologyDrive, .qnap, .ugreen, .asustor, .synologyC2, .s3Compatible, .seafile,
             .filen, .internxt, .terabox, .jottacloud, .github, .kDrive:
            return .credentials
        }
    }

    /// Re-runs the browser sign-in for an existing account, reusing the same
    /// `accountId` so the stored credentials slot is overwritten in the
    /// keychain. Only handles providers whose `reconnectMethod(for:)` is
    /// `.browser`; the rest go through the credentials sheet.
    func reauthenticate(accountId: UUID) async {
        guard let account = accountFor(id: accountId) else { return }
        switch account.providerType {
        case .googleDrive: await updateGoogleDriveAccount(accountId: accountId)
        case .dropbox: await updateDropboxAccount(accountId: accountId)
        case .box: await updateBoxAccount(accountId: accountId)
        case .oneDrive: await updateOneDriveAccount(accountId: accountId)
        case .pCloud: await updatePCloudAccount(accountId: accountId)
        case .drime where DrimeAPIClient.isOAuthAvailable:
            await updateDrimeAccountViaBrowser(accountId: accountId)
        default: return
        }
    }

    /// Reads the sign-in saved for one account again and puts it back to
    /// work, for an account that has no provider this session.
    ///
    /// That happens when the saved credentials couldn't be read at launch —
    /// the keychain was still locked, or its access prompt was dismissed
    /// after an update — and nothing ever tried again, so the account stayed
    /// "not connected" until the next relaunch although nothing about it had
    /// changed. True when the account has a provider afterwards.
    func restoreSavedSignIn(accountId: UUID) async -> Bool {
        if await syncEngine.provider(for: accountId) != nil { return true }
        guard let account = accountFor(id: accountId),
              let provider = await storedProvider(for: account) else { return false }
        await syncEngine.registerProvider(for: accountId, provider: provider)
        if let idx = accounts.firstIndex(where: { $0.id == accountId }), !accounts[idx].isConnected {
            accounts[idx].isConnected = true
            saveAccounts()
        }
        // Credentials to try count as signed in, like at launch; the panel
        // flags the account again if the provider turns them down.
        markSignInWorks(accountId: accountId)
        return true
    }

    /// A provider built from the sign-in saved for `account`, or nil when
    /// nothing usable is saved — no credentials, or none that could be read.
    private func storedProvider(for account: CloudAccount) async -> (any CloudProvider)? {
        let id = account.id
        let provider: any CloudProvider
        switch account.providerType {
        case .googleDrive: provider = GoogleDriveProvider(accountId: id)
        case .pCloud: provider = PCloudProvider(accountId: id)
        case .kDrive: provider = KDriveProvider(accountId: id)
        case .oneDrive: provider = OneDriveProvider(accountId: id)
        case .nextCloud: provider = NextCloudProvider(accountId: id)
        case .koofr: provider = KoofrProvider(accountId: id)
        case .dropbox: provider = DropboxProvider(accountId: id)
        case .mega: provider = MegaProvider(accountId: id)
        case .webDAV: provider = WebDAVProvider(accountId: id)
        case .sftp: provider = SFTPProvider(accountId: id)
        case .ftp: provider = FTPProvider(accountId: id)
        case .wordpress: provider = WordPressProvider(accountId: id)
        case .gmxCloud: provider = GMXCloudProvider(accountId: id)
        case .s3: provider = S3Provider(accountId: id)
        case .synologyDrive: provider = SynologyDriveProvider(accountId: id)
        case .qnap: provider = QNAPProvider(accountId: id)
        case .ugreen: provider = NASWebDAVProvider(brand: .ugreen, accountId: id)
        case .asustor: provider = NASWebDAVProvider(brand: .asustor, accountId: id)
        case .synologyC2: provider = SynologyC2Provider(accountId: id)
        case .s3Compatible: provider = S3CompatibleProvider(accountId: id)
        case .iCloud: provider = ICloudProvider()
        case .box: provider = BoxProvider(accountId: id)
        case .seafile: provider = SeafileProvider(accountId: id)
        case .filen: provider = FilenProvider(accountId: id)
        case .internxt: provider = InternxtProvider(accountId: id)
        case .terabox: provider = TeraBoxProvider(accountId: id)
        case .jottacloud: provider = JottacloudProvider(accountId: id)
        case .googleDrivePicker: provider = GoogleDrivePickerProvider(accountId: id)
        case .github: provider = GitHubProvider(accountId: id)
        case .drime: provider = DrimeProvider(accountId: id)
        // A paired camera may be off or unplugged now; it's registered
        // anyway so the account is present. The IP is re-resolved lazily on
        // first use, and an unreachable camera surfaces as offline.
        case .gopro: provider = GoProProvider(accountId: id)
        }
        return await provider.isAuthenticated ? provider : nil
    }

    /// Accounts saved as disconnected by an expired sign-in, in versions that
    /// recorded it that way. With credentials to try, they're live again, and
    /// the panel shows Sign In if those credentials still don't work.
    private func restoreDisconnectedAccounts() async {
        var changed = false
        for account in accounts where !account.isConnected {
            guard await syncEngine.provider(for: account.id) != nil,
                  let index = accounts.firstIndex(where: { $0.id == account.id }) else { continue }
            accounts[index].isConnected = true
            changed = true
        }
        if changed { saveAccounts() }
    }

    func reconnectSavedAccounts() async {
        defer { Task { await restoreDisconnectedAccounts() } }
        for account in accounts {
            guard let provider = await storedProvider(for: account) else {
                // Grey dot and a Reconnect item in the sidebar from launch,
                // rather than a green dot until a panel tries the account.
                markNeedsReAuthentication(accountId: account.id)
                continue
            }
            await syncEngine.registerProvider(for: account.id, provider: provider)
            // Self-heal a stuck "Dropbox (Unknown)" name from accounts
            // linked before the get_current_account Content-Type fix.
            if let dropbox = provider as? DropboxProvider,
               let freshName = await dropbox.refreshDisplayName(),
               let idx = accounts.firstIndex(where: { $0.id == account.id }) {
                accounts[idx].displayName = "\(account.providerType.displayName) (\(freshName))"
                saveAccounts()
            }
        }
    }

    /// Accounts whose sign-in stopped working this session.
    ///
    /// Apart from `isConnected`, which decides whether a panel shows the live
    /// account or an offline snapshot. An expired sign-in used to clear that
    /// flag, which took the panel, and the Sign In button with it, away, and
    /// was saved, so the account stayed stuck after a relaunch.
    private(set) var accountsNeedingSignIn: Set<UUID> = []

    /// What the status dot shows.
    func isSignedIn(_ account: CloudAccount) -> Bool {
        account.isConnected && !accountsNeedingSignIn.contains(account.id)
    }

    /// Records that an account's credentials stopped working, so the
    /// sidebar stops showing it as connected.
    ///
    /// Without this the dot stays green while the panel says "sign in
    /// required" — the app contradicting itself in two places at once
    /// (issue #46).
    func markNeedsReAuthentication(accountId: UUID) {
        // Writing the set notifies observers even when nothing changes, and
        // every sidebar row reads it.
        guard !accountsNeedingSignIn.contains(accountId) else { return }
        accountsNeedingSignIn.insert(accountId)
    }

    func markSignInWorks(accountId: UUID) {
        guard accountsNeedingSignIn.contains(accountId) else { return }
        accountsNeedingSignIn.remove(accountId)
    }

    func accountFor(id: UUID) -> CloudAccount? {
        accounts.first { $0.id == id }
    }

    /// Returns the live `CloudProvider` instance registered with the
    /// SyncEngine for an account, or nil if the account isn't currently
    /// connected. Used by the Finder-mount UI to hand the right provider to
    /// `LoopbackMountService`.
    func providerFor(accountId: UUID) async -> (any CloudProvider)? {
        await syncEngine.provider(for: accountId)
    }

    // MARK: - Account editing (re-authenticate existing accountId)
    //
    // Each method below mirrors the corresponding addXxxAccount, but
    // re-uses an existing accountId so the keychain entry is overwritten
    // in place, the SyncEngine provider is replaced for the same id, and
    // sync rules / panel state / favourites referencing the account all
    // survive. On failure, authError is surfaced and the existing
    // connection is left untouched.

    private func finalizeUpdate(
        accountId: UUID,
        provider: any CloudProvider,
        derivedDisplayName: String? = nil
    ) async {
        await syncEngine.registerProvider(for: accountId, provider: provider)
        if let idx = accounts.firstIndex(where: { $0.id == accountId }) {
            accounts[idx].isConnected = true
            if let derivedDisplayName, !derivedDisplayName.isEmpty {
                accounts[idx].displayName = derivedDisplayName
            }
            saveAccounts()
        }
        markSignInWorks(accountId: accountId)
    }

    func updateDropboxAccount(accountId: UUID) async {
        authError = nil
        do {
            let provider = DropboxProvider(accountId: accountId)
            _ = try await provider.startOAuthFlow()
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateGoogleDriveAccount(accountId: UUID) async {
        authError = nil
        do {
            let provider = GoogleDriveProvider(accountId: accountId)
            _ = try await provider.startOAuthFlow()
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateOneDriveAccount(accountId: UUID) async {
        authError = nil
        do {
            let provider = OneDriveProvider(accountId: accountId)
            _ = try await provider.startOAuthFlow()
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateBoxAccount(accountId: UUID) async {
        authError = nil
        do {
            let provider = BoxProvider(accountId: accountId)
            _ = try await provider.startOAuthFlow()
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateNextCloudAccountViaBrowser(accountId: UUID, serverURL: String) async {
        authError = nil
        isAuthenticatingNextCloud = true
        defer { isAuthenticatingNextCloud = false }
        do {
            let provider = NextCloudProvider(accountId: accountId)
            _ = try await provider.startLoginFlowV2(serverURL: serverURL)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateNextCloudAccount(accountId: UUID, serverURL: String, username: String, appPassword: String) async {
        authError = nil
        do {
            let provider = NextCloudProvider(accountId: accountId)
            try await provider.authenticate(serverURL: serverURL, username: username, appPassword: appPassword)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateKoofrAccount(accountId: UUID, email: String, appPassword: String) async {
        authError = nil
        do {
            let provider = KoofrProvider(accountId: accountId)
            try await provider.authenticate(email: email, appPassword: appPassword)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateMegaAccount(accountId: UUID, email: String, password: String, mfaCode: String?) async {
        authError = nil
        do {
            let provider = MegaProvider(accountId: accountId)
            try await provider.authenticate(email: email, password: password, mfaCode: mfaCode)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateGMXCloudAccount(accountId: UUID, email: String, password: String) async {
        authError = nil
        do {
            let provider = GMXCloudProvider(accountId: accountId)
            try await provider.authenticate(email: email, password: password)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateFilenAccount(accountId: UUID, email: String, password: String, twoFactorCode: String) async {
        authError = nil
        do {
            let provider = FilenProvider(accountId: accountId)
            _ = try await provider.authenticate(email: email, password: password, twoFactorCode: twoFactorCode)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateInternxtAccount(accountId: UUID, email: String, password: String, twoFactorCode: String) async {
        authError = nil
        do {
            let provider = InternxtProvider(accountId: accountId)
            _ = try await provider.authenticate(email: email, password: password, twoFactorCode: twoFactorCode)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateSeafileAccount(accountId: UUID, serverURL: String, username: String, password: String, otp: String?, allowSelfSignedCertificate: Bool) async {
        authError = nil
        do {
            let provider = SeafileProvider(accountId: accountId)
            _ = try await provider.authenticate(
                serverURL: serverURL,
                username: username,
                password: password,
                otp: otp,
                allowSelfSignedCertificate: allowSelfSignedCertificate
            )
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateWebDAVAccount(accountId: UUID, serverURL: String, username: String, password: String) async {
        authError = nil
        do {
            let provider = WebDAVProvider(accountId: accountId)
            try await provider.authenticate(serverURL: serverURL, username: username, password: password)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateSynologyDriveAccount(accountId: UUID, serverURL: String, username: String, password: String, otp: String?, allowSelfSignedCertificate: Bool) async {
        authError = nil
        do {
            let provider = SynologyDriveProvider(accountId: accountId)
            try await provider.authenticate(
                serverURL: serverURL,
                username: username,
                password: password,
                otp: otp,
                allowSelfSignedCertificate: allowSelfSignedCertificate
            )
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateQNAPAccount(accountId: UUID, serverURL: String, username: String, password: String, securityCode: String?, allowSelfSignedCertificate: Bool) async {
        authError = nil
        do {
            let provider = QNAPProvider(accountId: accountId)
            try await provider.authenticate(
                serverURL: serverURL,
                username: username,
                password: password,
                securityCode: securityCode,
                allowSelfSignedCertificate: allowSelfSignedCertificate
            )
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateNASWebDAVAccount(accountId: UUID, brand: NASWebDAVBrand, serverURL: String, username: String, password: String, allowSelfSignedCertificate: Bool) async {
        authError = nil
        do {
            let provider = NASWebDAVProvider(brand: brand, accountId: accountId)
            try await provider.authenticate(
                serverURL: serverURL,
                username: username,
                password: password,
                allowSelfSignedCertificate: allowSelfSignedCertificate
            )
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateWordPressAccount(accountId: UUID, siteURL: String, username: String, appPassword: String) async {
        authError = nil
        do {
            let provider = WordPressProvider(accountId: accountId)
            try await provider.authenticate(siteURL: siteURL, username: username, appPassword: appPassword)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updatePCloudAccount(accountId: UUID) async {
        authError = nil
        isAuthenticatingPCloud = true
        defer { isAuthenticatingPCloud = false }
        do {
            let provider = PCloudProvider(accountId: accountId)
            try await provider.startOAuthFlow()
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateDrimeAccountViaBrowser(accountId: UUID) async {
        authError = nil
        isAuthenticatingDrime = true
        defer { isAuthenticatingDrime = false }
        do {
            let provider = DrimeProvider(accountId: accountId)
            _ = try await provider.startOAuthFlow()
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateDrimeAccount(accountId: UUID, personalToken: String) async {
        authError = nil
        do {
            let provider = DrimeProvider(accountId: accountId)
            _ = try await provider.authenticate(personalToken: personalToken)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateJottacloudAccount(accountId: UUID, personalToken: String) async {
        authError = nil
        do {
            let provider = JottacloudProvider(accountId: accountId)
            _ = try await provider.authenticate(personalToken: personalToken)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateGitHubAccount(accountId: UUID, token: String) async {
        authError = nil
        do {
            let provider = GitHubProvider(accountId: accountId)
            _ = try await provider.authenticate(token: token)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    /// TeraBox's QR sign-in again, for the same account: shows a fresh code
    /// in `teraboxQRImageData` and waits for the TeraBox app to approve it.
    func updateTeraBoxAccount(accountId: UUID) async {
        authError = nil
        teraboxQRImageData = nil
        isAuthenticatingTeraBox = true
        defer { isAuthenticatingTeraBox = false; teraboxQRImageData = nil }
        do {
            let provider = TeraBoxProvider(accountId: accountId)
            let device = try await provider.beginDeviceLogin()
            teraboxQRImageData = device.qrImageData
            try await provider.completeDeviceLogin(device)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch is CancellationError {
            // The sheet was closed; nothing went wrong.
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateKDriveAccount(accountId: UUID, apiToken: String) async {
        authError = nil
        do {
            // Preserve the existing driveId — the user can't change the
            // workspace from Edit; if they want a different drive they
            // add a second account.
            let existingDriveId = (KeychainService.load(
                key: "kdrive.\(accountId.uuidString)",
                as: KDriveCredentials.self
            ))?.driveId
            let provider = KDriveProvider(accountId: accountId)
            try await provider.authenticate(apiToken: apiToken, driveId: existingDriveId)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateS3Account(accountId: UUID, accessKeyId: String, secretAccessKey: String, region: String, rootPath: String?) async {
        authError = nil
        do {
            let provider = S3Provider(accountId: accountId)
            let normalizedRoot = Self.normalizeS3RootPath(rootPath)
            try await provider.authenticate(
                accessKeyId: accessKeyId,
                secretAccessKey: secretAccessKey,
                region: region,
                rootPath: normalizedRoot
            )
            if let normalizedRoot {
                _ = try await provider.listDirectory(at: normalizedRoot)
            }
            await syncEngine.registerProvider(for: accountId, provider: provider)
            if let idx = accounts.firstIndex(where: { $0.id == accountId }) {
                accounts[idx].isConnected = true
                accounts[idx].rootPath = normalizedRoot ?? "/"
                saveAccounts()
            }
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateS3CompatibleAccount(accountId: UUID, accessKeyId: String, secretAccessKey: String, endpoint: String, region: String, displayName: String?, rootPath: String?) async {
        authError = nil
        do {
            let provider = S3CompatibleProvider(accountId: accountId)
            try await provider.authenticate(
                accessKeyId: accessKeyId,
                secretAccessKey: secretAccessKey,
                endpoint: endpoint,
                region: region,
                displayName: displayName
            )
            let normalizedRoot = Self.normalizeS3RootPath(rootPath)
            if let normalizedRoot {
                _ = try await provider.listDirectory(at: normalizedRoot)
            }
            await syncEngine.registerProvider(for: accountId, provider: provider)
            if let idx = accounts.firstIndex(where: { $0.id == accountId }) {
                accounts[idx].isConnected = true
                accounts[idx].rootPath = normalizedRoot ?? "/"
                saveAccounts()
            }
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateSynologyC2Account(accountId: UUID, accessKeyId: String, secretAccessKey: String, region: String) async {
        authError = nil
        do {
            let provider = SynologyC2Provider(accountId: accountId)
            try await provider.authenticate(
                accessKeyId: accessKeyId,
                secretAccessKey: secretAccessKey,
                region: region
            )
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    /// Asks before FileFluss trusts an SFTP server's host key: the first
    /// time it connects to a server, and when the server's key has changed.
    /// The Windows app asks the same.
    static func confirmSFTPTrust(_ question: SFTPTrustQuestion) -> Bool {
        let alert = NSAlert()
        switch question {
        case let .firstConnection(host, fingerprint):
            alert.messageText = L10n.text("Trust this server?")
            alert.informativeText = L10n.format(
                "This is the first connection to %@. Check that this SSH host key fingerprint is the server's — your provider or administrator can confirm it:\n\n%@\n\nTrust this key and connect?",
                host, fingerprint
            )
            alert.alertStyle = .warning
        case let .changed(host, previous, now):
            alert.messageText = L10n.text("Host key changed")
            alert.informativeText = L10n.format(
                "The SSH host key of %@ has changed since you last connected. This happens when the server was reinstalled — or when someone intercepts the connection.\n\nPreviously: %@\nNow: %@\n\nTrust the new key?",
                host, previous, now
            )
            alert.alertStyle = .critical
        }
        alert.addButton(withTitle: L10n.text("Connect"))
        alert.addButton(withTitle: L10n.text("Cancel"))
        return alert.runModal() == .alertFirstButtonReturn
    }

    func updateSFTPAccount(
        accountId: UUID,
        host: String,
        port: Int,
        username: String,
        password: String? = nil,
        privateKey: String? = nil,
        passphrase: String? = nil,
        remotePath: String
    ) async {
        authError = nil
        do {
            let resolvedRemote = remotePath.isEmpty ? "/" : remotePath
            let provider = SFTPProvider(accountId: accountId)
            if let privateKey, !privateKey.isEmpty {
                try await provider.authenticate(
                    host: host,
                    port: port,
                    username: username,
                    privateKey: privateKey,
                    passphrase: passphrase,
                    remotePath: resolvedRemote,
                    confirmTrust: { question in await Self.confirmSFTPTrust(question) }
                )
            } else {
                try await provider.authenticate(
                    host: host,
                    port: port,
                    username: username,
                    password: password ?? "",
                    remotePath: resolvedRemote,
                    confirmTrust: { question in await Self.confirmSFTPTrust(question) }
                )
            }
            await syncEngine.registerProvider(for: accountId, provider: provider)
            if let idx = accounts.firstIndex(where: { $0.id == accountId }) {
                accounts[idx].isConnected = true
                accounts[idx].rootPath = resolvedRemote
                saveAccounts()
            }
        } catch {
            authError = error.localizedDescription
        }
    }

    func updateFTPAccount(
        accountId: UUID,
        host: String,
        port: Int,
        username: String,
        password: String,
        remotePath: String,
        useTLS: Bool,
        allowInvalidCertificate: Bool
    ) async {
        authError = nil
        do {
            let resolvedRemote = remotePath.isEmpty ? "/" : remotePath
            let provider = FTPProvider(accountId: accountId)
            try await provider.authenticate(
                host: host,
                port: port,
                username: username,
                password: password,
                remotePath: resolvedRemote,
                useTLS: useTLS,
                allowInvalidCertificate: allowInvalidCertificate
            )
            await syncEngine.registerProvider(for: accountId, provider: provider)
            if let idx = accounts.firstIndex(where: { $0.id == accountId }) {
                accounts[idx].isConnected = true
                accounts[idx].rootPath = resolvedRemote
                saveAccounts()
            }
        } catch {
            authError = error.localizedDescription
        }
    }

    // MARK: - Persistence

    func saveAccounts() {
        // Never let a failed load turn into a wipe.
        if accountsLoadFailed && accounts.isEmpty {
            NSLog("[FileFluss] saveAccounts skipped: stored accounts couldn't be decoded, refusing to overwrite them")
            return
        }
        do {
            let data = try StoredList.encode(accounts, unreadable: unreadableAccounts)
            Self.defaults.set(data, forKey: Self.accountsKey)
        } catch {
            // A silent failure here would lose accounts the user just added —
            // surface it in the log so support has something to work with.
            NSLog("[FileFluss] saveAccounts failed: \(error.localizedDescription)")
        }
    }

    #if DEBUG
    /// Adds the accounts from a test-account snapshot that aren't in the
    /// list, then connects everything whose credentials are in place.
    /// Accounts already here are left as they are. Returns how many were added.
    @discardableResult
    func restoreTestAccounts(_ restored: [CloudAccount]) async -> Int {
        let known = Set(accounts.map(\.id))
        let added = restored.filter { !known.contains($0.id) }
        accounts += added
        saveAccounts()
        await reconnectSavedAccounts()
        return added.count
    }
    #endif

    /// Accounts in the store that this version can't read: one added in a
    /// newer version, usually. Kept so that saving doesn't delete them.
    private var unreadableAccounts: [Data] = []

    private func loadAccounts() {
        #if DEBUG
        // First launch with a separate list: start from the shared one, so
        // the accounts already set up for testing carry over.
        if Self.defaults.data(forKey: Self.accountsKey) == nil,
           let shared = Self.defaults.data(forKey: Self.sharedAccountsKey) {
            Self.defaults.set(shared, forKey: Self.accountsKey)
        }
        #endif
        guard let data = Self.defaults.data(forKey: Self.accountsKey) else { return }
        guard let saved = StoredList.decode(data, as: CloudAccount.self) else {
            // Stored data exists but doesn't decode — a truncated write, say.
            // Flag it so a later save can't replace the stored list with an
            // empty one.
            accountsLoadFailed = true
            NSLog("[FileFluss] loadAccounts: stored account list could not be decoded — it will not be overwritten")
            return
        }
        accounts = saved.items
        unreadableAccounts = saved.unreadable
        if !saved.unreadable.isEmpty {
            NSLog("[FileFluss] loadAccounts: keeping \(saved.unreadable.count) account(s) this version can't read")
        }
    }
}
