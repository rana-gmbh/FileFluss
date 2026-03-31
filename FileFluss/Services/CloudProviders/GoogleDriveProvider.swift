import Foundation
import os

private let googleDriveProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "googleDriveProvider")

final class GoogleDriveProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .googleDrive

    private var apiClient: GoogleDriveAPIClient?
    private let keychainKey: String

    var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    init(accountId: UUID = UUID()) {
        self.keychainKey = "googledrive.\(accountId.uuidString)"
        restoreCredentials()
    }

    init(credentials: GoogleDriveCredentials) {
        self.keychainKey = "googledrive.\(credentials.userEmail)"
        self.apiClient = GoogleDriveAPIClient(credentials: credentials)
    }

    // MARK: - Authentication (OAuth2 Loopback)

    /// Starts the OAuth2 flow: opens the browser for Google sign-in and waits for the redirect.
    func startOAuthFlow() async throws -> GoogleDriveCredentials {
        let credentials = try await GoogleDriveAPIClient.startOAuthFlow()
        self.apiClient = GoogleDriveAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        googleDriveProviderLog.info("[Google Drive] Authenticated as \(credentials.displayName)")
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
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.downloadFile(remotePath: remotePath, to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.uploadFile(from: localURL, to: remotePath)
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
        if let creds = KeychainService.load(key: keychainKey, as: GoogleDriveCredentials.self) {
            apiClient = GoogleDriveAPIClient(credentials: creds)
            Task {
                try? await refreshIfNeeded()
            }
        }
    }
}
