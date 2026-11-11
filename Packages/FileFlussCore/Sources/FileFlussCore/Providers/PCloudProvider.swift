import Foundation

public final class PCloudProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .pCloud

    private var apiClient: PCloudAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "pcloud.\(accountId.uuidString)"
        restoreCredentials()
    }

    public init(credentials: PCloudCredentials) {
        self.keychainKey = "pcloud.\(credentials.userId)"
        self.apiClient = PCloudAPIClient(credentials: credentials)
    }

    // MARK: - Authentication

    /// Signs in through pCloud's own page in the browser (OAuth 2.0 code
    /// flow). pCloud stopped issuing tokens to third-party apps through
    /// password login, which left only copying a cookie out of the browser.
    public func startOAuthFlow() async throws {
        let credentials = try await PCloudAPIClient.startOAuthFlow()
        self.apiClient = PCloudAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
    }

    public func authenticate() async throws {
        throw CloudProviderError.notAuthenticated
    }

    public func disconnect() async throws {
        apiClient = nil
        try KeychainService.delete(key: keychainKey)
    }

    public func userDisplayName() async throws -> String {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let info = try await client.userInfo()
        return info.email
    }

    // MARK: - File Operations

    public func listDirectory(at path: String) async throws -> [CloudFileItem] {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.listFolder(path: path)
    }

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.downloadFile(remotePath: remotePath, to: localURL, onBytes: onBytes)
    }

    public func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let folderPath = (remotePath as NSString).deletingLastPathComponent
        let fileName = (remotePath as NSString).lastPathComponent
        try await client.uploadFile(from: localURL, toFolder: folderPath, fileName: fileName, onBytes: onBytes)
    }

    public func deleteItem(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        do {
            try await client.deleteFile(path: path)
        } catch CloudProviderError.notFound {
            try await client.deleteFolder(path: path)
        }
    }

    public func createDirectory(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.createFolder(path: path)
    }

    public func renameItem(at path: String, to newName: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let metadata = try await client.stat(path: path)
        if metadata.isDirectory {
            try await client.renameFolder(path: path, toName: newName)
        } else {
            try await client.renameFile(path: path, toName: newName)
        }
    }

    public func moveItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let metadata = try await client.stat(path: path)
        if metadata.isDirectory {
            try await client.renameFolder(path: path, toPath: newPath)
        } else {
            try await client.renameFile(path: path, toPath: newPath)
        }
    }

    public func setModificationDate(at remotePath: String, to date: Date) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.setModificationDate(at: remotePath, to: date)
    }

    public func copyItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let metadata = try await client.stat(path: path)
        if metadata.isDirectory {
            try await client.copyFolder(path: path, toPath: newPath)
        } else {
            try await client.copyFile(path: path, toPath: newPath)
        }
    }

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.stat(path: path)
    }

    public func folderSize(at path: String) async throws -> Int64 {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.folderSize(path: path)
    }

    public func storageQuota() async throws -> CloudStorageQuota? {
        guard let client = apiClient else { return nil }
        let info = try await client.userInfo()
        return CloudStorageQuota(
            usedBytes: info.usedQuota,
            totalBytes: info.quota > 0 ? info.quota : nil
        )
    }

    // MARK: - Share links

    /// pCloud creates public links on any plan, but `linkpassword` and
    /// `expire` are Premium-only — the server rejects them on a free
    /// account, and `createShareLink` surfaces its message verbatim.
    /// There's no API switch for view-without-download.
    ///
    /// `listpublinks` enumerates the account's links so an existing one can be
    /// found, and `changepublink` / `deletepublink` edit and withdraw it in
    /// place — the link's `code`, and therefore its URL, survives an update.
    public var shareLinkCapabilities: ShareLinkCapabilities {
        ShareLinkCapabilities(
            canCreate: true,
            canQueryExisting: true,
            canUpdate: true,
            canRemove: true,
            supportsPassword: true,
            supportsExpiry: true,
            supportsDownloadToggle: false,
            passwordRequiresPaidPlan: true,
            expiryRequiresPaidPlan: true
        )
    }

    public func createShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.createShareLink(at: path, options: options)
    }

    public func existingShareLink(at path: String) async throws -> CloudShareLink? {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.existingShareLink(at: path)
    }

    public func updateShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.updateShareLink(at: path, options: options)
    }

    public func removeShareLink(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.removeShareLink(at: path)
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: PCloudCredentials.self) {
            apiClient = PCloudAPIClient(credentials: creds)
        }
    }
}
