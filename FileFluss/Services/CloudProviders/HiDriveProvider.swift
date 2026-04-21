import Foundation
import os

private let hiDriveProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "hiDriveProvider")

final class HiDriveProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .hiDrive

    private var apiClient: HiDriveAPIClient?
    private let keychainKey: String

    var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    init(accountId: UUID = UUID()) {
        self.keychainKey = "hiDrive.\(accountId.uuidString)"
        restoreCredentials()
    }

    init(credentials: HiDriveCredentials, accountId: UUID) {
        self.keychainKey = "hiDrive.\(accountId.uuidString)"
        self.apiClient = HiDriveAPIClient(credentials: credentials)
    }

    // MARK: - Authentication (OAuth2 Authorization Code + Loopback)

    func startOAuthFlow() async throws -> HiDriveCredentials {
        let credentials = try await HiDriveAPIClient.startOAuthFlow()
        self.apiClient = HiDriveAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        hiDriveProviderLog.info("[HiDrive] Authenticated as \(credentials.displayName)")
        return credentials
    }

    func authenticate() async throws {
        throw CloudProviderError.notAuthenticated
    }

    func disconnect() async throws {
        apiClient = nil
        try KeychainService.delete(key: keychainKey)
    }

    func userDisplayName() async throws -> String {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.userDisplayName()
    }

    // MARK: - File Operations

    func listDirectory(at path: String) async throws -> [CloudFileItem] {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.listFolder(path: path)
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.downloadFile(remotePath: remotePath, to: localURL, onBytes: onBytes)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.uploadFile(from: localURL, to: remotePath, onBytes: onBytes)
    }

    func deleteItem(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.deleteItem(at: path)
    }

    func createDirectory(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.createFolder(at: path)
    }

    func renameItem(at path: String, to newName: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.renameItem(at: path, to: newName)
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.getFileMetadata(at: path)
    }

    func folderSize(at path: String) async throws -> Int64 {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.folderSize(at: path)
    }

    // MARK: - Token Refresh

    func refreshIfNeeded() async throws {
        guard let client = apiClient else { return }
        let newCreds = try await client.refreshTokenIfNeeded()
        try? KeychainService.save(key: keychainKey, value: newCreds)
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: HiDriveCredentials.self) {
            apiClient = HiDriveAPIClient(credentials: creds)
            Task { try? await refreshIfNeeded() }
        }
    }
}
