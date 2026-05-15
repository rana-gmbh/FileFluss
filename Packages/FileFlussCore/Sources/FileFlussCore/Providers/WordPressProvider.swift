import Foundation
import os

private let wpProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "wordpressProvider")

public final class WordPressProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .wordpress

    private var apiClient: WordPressAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "wordpress.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    public func authenticate(siteURL: String, username: String, appPassword: String) async throws {
        let credentials = try await WordPressAPIClient.authenticate(
            siteURL: siteURL,
            username: username,
            password: appPassword
        )
        self.apiClient = WordPressAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        wpProviderLog.info("[WordPress] Authenticated at \(siteURL)")
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
        // WordPress media library uses auto-generated date-based folders
        // Creating arbitrary directories is not supported
        throw CloudProviderError.serverError(405)
    }

    public func renameItem(at path: String, to newName: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.renameItem(at: path, to: newName)
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

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: WordPressCredentials.self) {
            apiClient = WordPressAPIClient(credentials: creds)
        }
    }
}
