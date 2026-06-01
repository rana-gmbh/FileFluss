import Foundation
import os

private let jottaProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "jottacloudProvider")

/// Jottacloud provider. Talks to Jottacloud's undocumented HTTP API (the same
/// one rclone uses) over the device "Jotta" / mountpoint "Archive" root.
/// Authentication is a one-time Personal Login Token paste, exchanged for an
/// OAuth refresh token that's persisted in the keychain. The rotated refresh
/// token is written back on every refresh (see `JottacloudAPIClient`).
public final class JottacloudProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .jottacloud

    private var apiClient: JottacloudAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async {
            guard let client = apiClient else { return false }
            return await client.isAuthenticated
        }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "jottacloud.\(accountId.uuidString)"
        if let creds = KeychainService.load(key: keychainKey, as: JottacloudCredentials.self) {
            self.apiClient = JottacloudProvider.makeClient(credentials: creds, keychainKey: keychainKey)
        }
    }

    /// Build a client whose token-rotation callback persists the refreshed
    /// credentials back to the same keychain entry.
    private static func makeClient(credentials: JottacloudCredentials, keychainKey: String) -> JottacloudAPIClient {
        JottacloudAPIClient(credentials: credentials) { updated in
            try? KeychainService.save(key: keychainKey, value: updated)
        }
    }

    // MARK: - Authentication

    /// Exchange a pasted Personal Login Token for OAuth credentials and store
    /// them. Returns the resolved username for the account display name.
    @discardableResult
    public func authenticate(personalToken: String) async throws -> String {
        let credentials = try await JottacloudAPIClient.login(personalToken: personalToken)
        let client = JottacloudProvider.makeClient(credentials: credentials, keychainKey: keychainKey)
        self.apiClient = client
        try KeychainService.save(key: keychainKey, value: credentials)
        jottaProviderLog.info("[Jottacloud] Connected account \(credentials.username, privacy: .public)")
        return credentials.username
    }

    public func authenticate() async throws {
        if apiClient == nil { throw CloudProviderError.notAuthenticated }
    }

    public func disconnect() async throws {
        apiClient = nil
        try KeychainService.delete(key: keychainKey)
    }

    public func userDisplayName() async throws -> String {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return await client.credentials.username
    }

    // MARK: - File operations

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

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.getFileMetadata(at: path)
    }

    public func folderSize(at path: String) async throws -> Int64 {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.folderSize(at: path)
    }

    public func storageQuota() async throws -> CloudStorageQuota? {
        guard let client = apiClient else { return nil }
        return try await client.storageQuota()
    }
}
