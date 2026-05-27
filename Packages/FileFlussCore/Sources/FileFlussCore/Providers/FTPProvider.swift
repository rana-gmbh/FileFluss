#if os(macOS)
import Foundation
import os

private let ftpProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "ftpProvider")

/// Plain FTP / FTPS provider. Like SFTP it shells out to a system binary
/// (`/usr/bin/curl`) rather than depending on a third-party library, so it is
/// macOS-only.
public final class FTPProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .ftp

    private var apiClient: FTPAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "ftp.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    public func authenticate(
        host: String,
        port: Int,
        username: String,
        password: String,
        remotePath: String = "/",
        useTLS: Bool = false,
        allowInvalidCertificate: Bool = false
    ) async throws {
        let credentials = try await FTPAPIClient.authenticate(
            host: host,
            port: port,
            username: username,
            password: password,
            remotePath: remotePath,
            useTLS: useTLS,
            allowInvalidCertificate: allowInvalidCertificate
        )
        self.apiClient = FTPAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        ftpProviderLog.info("[FTP] Authenticated as \(credentials.username)@\(credentials.host)")
    }

    var remotePath: String {
        get async { apiClient?.credentials.remotePath ?? "/" }
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
        let info = try await client.getFileInfo(at: path)
        try await client.deleteItem(at: path, isDirectory: info.isDirectory)
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
        if let creds = KeychainService.load(key: keychainKey, as: FTPCredentials.self) {
            apiClient = FTPAPIClient(credentials: creds)
        }
    }
}
#endif
