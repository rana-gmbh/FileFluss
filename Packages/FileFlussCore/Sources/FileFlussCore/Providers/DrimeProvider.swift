import Foundation
import os

private let drimeProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "drimeProvider")

public final class DrimeProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .drime

    private var apiClient: DrimeAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "drime.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    public func startOAuthFlow() async throws -> DrimeCredentials {
        try await connect(with: DrimeAPIClient.startOAuthFlow())
    }

    public func authenticate(personalToken: String) async throws -> DrimeCredentials {
        try await connect(with: DrimeAPIClient.validatePersonalToken(personalToken))
    }

    private func connect(with credentials: DrimeCredentials) throws -> DrimeCredentials {
        try KeychainService.save(key: keychainKey, value: credentials)
        let client = DrimeAPIClient(credentials: credentials)
        apiClient = client
        persistCredentialChanges(from: client)
        drimeProviderLog.info("[Drime] Connected (\(credentials.kind.rawValue))")
        return credentials
    }

    public func authenticate() async throws {
        throw CloudProviderError.notAuthenticated
    }

    public func disconnect() async throws {
        if let apiClient { await apiClient.revoke() }
        apiClient = nil
        try KeychainService.delete(key: keychainKey)
    }

    public func userDisplayName() async throws -> String {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return await client.userDisplayName()
    }

    // MARK: - File operations

    public func listDirectory(at path: String) async throws -> [CloudFileItem] {
        try await client().listFolder(path: path)
    }

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        try await client().downloadFile(remotePath: remotePath, to: localURL, onBytes: onBytes)
    }

    public func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        try await client().uploadFile(from: localURL, to: remotePath, onBytes: onBytes)
    }

    public func deleteItem(at path: String) async throws {
        try await client().deleteItem(at: path)
    }

    public func createDirectory(at path: String) async throws {
        try await client().createFolder(at: path)
    }

    public func renameItem(at path: String, to newName: String) async throws {
        try await client().renameItem(at: path, to: newName)
    }

    public func moveItem(at path: String, toPath newPath: String) async throws {
        try await client().moveItem(at: path, toPath: newPath)
    }

    public func copyItem(at path: String, toPath newPath: String) async throws {
        try await client().copyItem(at: path, toPath: newPath)
    }

    public func setModificationDate(at remotePath: String, to date: Date) async throws {
        try await client().setModificationDate(at: remotePath, to: date)
    }

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        try await client().getFileMetadata(at: path)
    }

    public func folderSize(at path: String) async throws -> Int64 {
        try await client().folderSize(at: path)
    }

    public func storageQuota() async throws -> CloudStorageQuota? {
        guard let client = apiClient else { return nil }
        return try await client.storageQuota()
    }

    // MARK: - Share links

    /// Drime's links take a password, an expiry and a download switch, can
    /// be read back and edited in place, and deleted.
    public var shareLinkCapabilities: ShareLinkCapabilities {
        ShareLinkCapabilities(
            canCreate: true,
            canQueryExisting: true,
            canUpdate: true,
            canRemove: true,
            supportsPassword: true,
            supportsExpiry: true,
            supportsDownloadToggle: true
        )
    }

    public func createShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        try await client().createShareLink(at: path, options: options)
    }

    public func existingShareLink(at path: String) async throws -> CloudShareLink? {
        try await client().existingShareLink(at: path)
    }

    public func updateShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        try await client().updateShareLink(at: path, options: options)
    }

    public func removeShareLink(at path: String) async throws {
        try await client().removeShareLink(at: path)
    }

    // MARK: - Private

    private func client() throws -> DrimeAPIClient {
        guard let apiClient else { throw CloudProviderError.notAuthenticated }
        return apiClient
    }

    /// Drime rotates the refresh token on every refresh and revokes the
    /// whole grant if an old one is ever presented again, so each new one
    /// goes to the keychain the moment it exists.
    private func persistCredentialChanges(from client: DrimeAPIClient) {
        let key = keychainKey
        Task {
            await client.setCredentialsDidChange { creds in
                try? KeychainService.save(key: key, value: creds)
            }
        }
    }

    private func restoreCredentials() {
        guard let creds = KeychainService.load(key: keychainKey, as: DrimeCredentials.self) else { return }
        let client = DrimeAPIClient(credentials: creds)
        apiClient = client
        persistCredentialChanges(from: client)
    }
}
