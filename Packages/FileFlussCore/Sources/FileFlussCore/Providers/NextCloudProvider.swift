import Foundation
import os

private let nextCloudProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "nextCloudProvider")

public final class NextCloudProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .nextCloud

    private var apiClient: NextCloudAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "nextcloud.\(accountId.uuidString)"
        restoreCredentials()
    }

    public init(credentials: NextCloudCredentials) {
        self.keychainKey = "nextcloud.\(credentials.username)"
        self.apiClient = NextCloudAPIClient(credentials: credentials)
    }

    // MARK: - Authentication

    public func authenticate(serverURL: String, username: String, appPassword: String) async throws {
        let credentials = try await NextCloudAPIClient.authenticate(
            serverURL: serverURL,
            username: username,
            appPassword: appPassword
        )
        self.apiClient = NextCloudAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        nextCloudProviderLog.info("[NextCloud] Authenticated as \(credentials.displayName)")
    }

    /// Browser-based sign-in using Nextcloud's Login Flow v2. Returns the
    /// credentials so the caller can display the user's real name on the
    /// account row.
    public func startLoginFlowV2(serverURL: String) async throws -> NextCloudCredentials {
        let credentials = try await NextCloudAPIClient.startLoginFlowV2(serverURL: serverURL)
        self.apiClient = NextCloudAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        nextCloudProviderLog.info("[NextCloud] Login Flow v2 succeeded for \(credentials.displayName)")
        return credentials
    }

    /// Result of `prepareLoginFlowV2(serverURL:)`: the login URL and a
    /// closure that polls until the user completes the browser handshake,
    /// then writes the resulting credentials into this provider's
    /// keychain entry. iOS callers present the loginURL in an
    /// SFSafariViewController and await `completeLogin()` in parallel.
    public struct PreparedLoginFlowV2: Sendable {
        public let loginURL: URL
        public let completeLogin: @Sendable () async throws -> NextCloudCredentials
    }

    /// iOS-friendly split of `startLoginFlowV2`: returns the browser URL
    /// up-front so the host can present its own browser surface
    /// (SFSafariViewController), then drive the polling closure to
    /// completion.
    public func prepareLoginFlowV2(serverURL: String) async throws -> PreparedLoginFlowV2 {
        let flow = try await NextCloudAPIClient.prepareLoginFlowV2(serverURL: serverURL)
        let keychainKey = self.keychainKey
        let completeLogin: @Sendable () async throws -> NextCloudCredentials = { [weak self] in
            let credentials = try await flow.poll()
            self?.apiClient = NextCloudAPIClient(credentials: credentials)
            try KeychainService.save(key: keychainKey, value: credentials)
            nextCloudProviderLog.info("[NextCloud] Login Flow v2 succeeded for \(credentials.displayName)")
            return credentials
        }
        return PreparedLoginFlowV2(loginURL: flow.loginURL, completeLogin: completeLogin)
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
        return await client.userDisplayName()
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
        try await client.uploadFile(from: localURL, to: remotePath, onBytes: onBytes)
    }

    public func deleteItem(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.deleteItem(at: path)
    }

    public func createDirectory(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.createFolder(at: path)
    }

    public func renameItem(at path: String, to newName: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.renameItem(at: path, to: newName)
    }

    public func moveItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.moveItem(at: path, toPath: newPath)
    }

    public func copyItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.copyItem(at: path, toPath: newPath)
    }

    public func setModificationDate(at remotePath: String, to date: Date) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.setModificationDate(at: remotePath, to: date)
    }

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.getFileInfo(at: path)
    }

    public func folderSize(at path: String) async throws -> Int64 {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.folderSize(path: path)
    }

    public func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]? {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.searchFiles(query: query, path: path)
    }

    public func storageQuota() async throws -> CloudStorageQuota? {
        guard let client = apiClient else { return nil }
        return try await client.storageQuota()
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: NextCloudCredentials.self) {
            apiClient = NextCloudAPIClient(credentials: creds)
        }
    }
}
