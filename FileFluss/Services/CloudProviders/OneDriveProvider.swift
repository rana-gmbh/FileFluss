import Foundation
import os

private let oneDriveProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "oneDriveProvider")

final class OneDriveProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .oneDrive

    private var apiClient: OneDriveAPIClient?
    private let keychainKey: String

    var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    init(accountId: UUID = UUID()) {
        self.keychainKey = "onedrive.\(accountId.uuidString)"
        restoreCredentials()
    }

    init(credentials: OneDriveCredentials) {
        self.keychainKey = "onedrive.\(credentials.userEmail)"
        self.apiClient = OneDriveAPIClient(credentials: credentials)
    }

    // MARK: - Authentication (Loopback OAuth + PKCE)

    /// Runs Microsoft's loopback OAuth flow and persists the resulting
    /// credentials under this account's keychain slot. Matches the pattern
    /// used by Google Drive, Dropbox, and Box so `SyncViewModel.reauthenticate`
    /// can drive all four through a single switch.
    func startOAuthFlow() async throws -> OneDriveCredentials {
        let credentials = try await OneDriveAPIClient.startOAuthFlow()
        self.apiClient = OneDriveAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        oneDriveProviderLog.info("[OneDrive] Authenticated as \(credentials.userEmail, privacy: .public)")
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

    func setModificationDate(at remotePath: String, to date: Date) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.setModificationDate(at: remotePath, to: date)
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.getFileMetadata(at: path)
    }

    func folderSize(at path: String) async throws -> Int64 {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.folderSize(at: path)
    }

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]? {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.searchFiles(query: query, path: path)
    }

    // MARK: - Token Refresh

    /// Refreshes credentials if expired and persists the updated tokens.
    func refreshIfNeeded() async throws {
        guard let client = apiClient else { return }
        let newCreds = try await client.refreshTokenIfNeeded()
        try? KeychainService.save(key: keychainKey, value: newCreds)
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: OneDriveCredentials.self) {
            apiClient = OneDriveAPIClient(credentials: creds)
            // Refresh token in background if needed
            Task {
                try? await refreshIfNeeded()
            }
        }
    }
}
