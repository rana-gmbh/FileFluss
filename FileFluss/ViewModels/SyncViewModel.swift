import SwiftUI
import FileFlussCore

@Observable @MainActor
final class SyncViewModel {
    var accounts: [CloudAccount] = []
    var syncRules: [SyncRule] = []
    var isAddingAccount: Bool = false
    var isAddingRule: Bool = false
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

    // NextCloud Login Flow v2 state (browser-based sign-in)
    var isAuthenticatingNextCloud: Bool = false

    private let syncEngine = SyncEngine.shared
    private static let accountsKey = "cloudAccounts"

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
        authError = nil
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
        } catch {
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
                displayName: displayName
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
            try await provider.authenticate(
                accessKeyId: accessKeyId,
                secretAccessKey: secretAccessKey,
                region: region
            )

            let normalizedRoot = Self.normalizeS3RootPath(rootPath)
            if let normalizedRoot {
                // Probe the chosen bucket/prefix once so a typo or missing
                // ListBucket permission surfaces as a clear connect-time
                // error instead of producing an account that silently
                // opens to an empty folder.
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
            connectedAccount.displayName = (try? await provider.userDisplayName()) ?? "GoPro Camera"
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
        // Drop search-index rows so the account no longer surfaces under
        // Settings → Index Status as an orphan with a stale name.
        await SearchIndex.shared.dropIndexedCloudSource(accountId: account.id)
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
                    // Self-heal a stuck "Dropbox (Unknown)" name from accounts
                    // linked before the get_current_account Content-Type fix.
                    if let freshName = await provider.refreshDisplayName(),
                       let idx = accounts.firstIndex(where: { $0.id == account.id }) {
                        accounts[idx].displayName = "\(account.providerType.displayName) (\(freshName))"
                        saveAccounts()
                    }
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
            case .ftp:
                let provider = FTPProvider(accountId: account.id)
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
            case .seafile:
                let provider = SeafileProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .filen:
                let provider = FilenProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .internxt:
                let provider = InternxtProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .terabox:
                let provider = TeraBoxProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .jottacloud:
                let provider = JottacloudProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .googleDrivePicker:
                let provider = GoogleDrivePickerProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    await syncEngine.registerProvider(for: account.id, provider: provider)
                }
            case .gopro:
                let provider = GoProProvider(accountId: account.id)
                if await provider.isAuthenticated {
                    // The camera may be off/unplugged now; register anyway so
                    // the account is present. The IP is re-resolved lazily on
                    // first use, and unreachable cameras surface as offline.
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

    func updatePCloudAccount(accountId: UUID, accessToken: String) async {
        authError = nil
        do {
            let provider = PCloudProvider(accountId: accountId)
            try await provider.authenticate(accessToken: accessToken)
            await finalizeUpdate(accountId: accountId, provider: provider)
        } catch {
            authError = error.localizedDescription
        }
    }

    func updatePCloudAccount(accountId: UUID, email: String, password: String) async {
        authError = nil
        do {
            let provider = PCloudProvider(accountId: accountId)
            try await provider.authenticate(email: email, password: password)
            await finalizeUpdate(accountId: accountId, provider: provider)
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
            try await provider.authenticate(
                accessKeyId: accessKeyId,
                secretAccessKey: secretAccessKey,
                region: region
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
                    remotePath: resolvedRemote
                )
            } else {
                try await provider.authenticate(
                    host: host,
                    port: port,
                    username: username,
                    password: password ?? "",
                    remotePath: resolvedRemote
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
        do {
            let data = try JSONEncoder().encode(accounts)
            UserDefaults.standard.set(data, forKey: Self.accountsKey)
        } catch {
            // A silent failure here would lose accounts the user just added —
            // surface it in the log so support has something to work with.
            NSLog("[FileFluss] saveAccounts failed: \(error.localizedDescription)")
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
