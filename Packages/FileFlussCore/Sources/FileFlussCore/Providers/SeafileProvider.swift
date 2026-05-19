import Foundation
import os

private let seafileProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "seafileProvider")

public final class SeafileProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .seafile

    private var apiClient: SeafileAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "seafile.\(accountId.uuidString)"
        restoreCredentials()
    }

    public init(credentials: SeafileCredentials) {
        self.keychainKey = "seafile.\(credentials.username)@\(credentials.serverURL)"
        self.apiClient = SeafileAPIClient(credentials: credentials)
    }

    // MARK: - Authentication

    /// Exchange the user's email + password (+ optional 2FA OTP) for a
    /// long-lived Seafile API token, then persist the token in the keychain.
    /// The password is discarded after this call.
    public func authenticate(
        serverURL: String,
        username: String,
        password: String,
        otp: String?,
        allowSelfSignedCertificate: Bool
    ) async throws -> SeafileCredentials {
        let credentials = try await SeafileAPIClient.obtainToken(
            serverURL: serverURL,
            username: username,
            password: password,
            otp: otp,
            allowSelfSignedCertificate: allowSelfSignedCertificate
        )
        self.apiClient = SeafileAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        seafileProviderLog.info("[Seafile] Authenticated as \(credentials.username, privacy: .public)")
        return credentials
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

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: SeafileCredentials.self) {
            apiClient = SeafileAPIClient(credentials: creds)
        }
    }
}
