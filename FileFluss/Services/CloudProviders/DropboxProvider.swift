import Foundation
import os

private let dropboxProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "dropboxProvider")

final class DropboxProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .dropbox

    private var apiClient: DropboxAPIClient?
    private let keychainKey: String

    var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    init(accountId: UUID = UUID()) {
        self.keychainKey = "dropbox.\(accountId.uuidString)"
        restoreCredentials()
    }

    init(credentials: DropboxCredentials) {
        self.keychainKey = "dropbox.\(credentials.accountId)"
        self.apiClient = DropboxAPIClient(credentials: credentials)
    }

    // MARK: - Authentication (OAuth2 PKCE Loopback)

    func startOAuthFlow() async throws -> DropboxCredentials {
        let credentials = try await DropboxAPIClient.startOAuthFlow()
        self.apiClient = DropboxAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        dropboxProviderLog.info("[Dropbox] Authenticated as \(credentials.displayName)")
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

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]? {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.searchFiles(query: query, path: path)
    }

    // MARK: - Token Refresh

    func refreshIfNeeded() async throws {
        guard let client = apiClient else { return }
        let newCreds = try await client.refreshTokenIfNeeded()
        try? KeychainService.save(key: keychainKey, value: newCreds)
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: DropboxCredentials.self) {
            apiClient = DropboxAPIClient(credentials: creds)
            Task {
                try? await refreshIfNeeded()
            }
        }
    }
}
