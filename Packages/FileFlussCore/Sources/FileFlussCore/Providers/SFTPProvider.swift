#if os(macOS)
import Foundation
import os

private let sftpProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "sftpProvider")

public final class SFTPProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .sftp

    private var apiClient: SFTPAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "sftp.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    public func authenticate(
        host: String,
        port: Int,
        username: String,
        password: String? = nil,
        privateKey: String? = nil,
        passphrase: String? = nil,
        remotePath: String = "/"
    ) async throws {
        let credentials = try await SFTPAPIClient.authenticate(
            host: host,
            port: port,
            username: username,
            password: password,
            privateKey: privateKey,
            passphrase: passphrase,
            remotePath: remotePath
        )
        self.apiClient = SFTPAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        sftpProviderLog.info("[SFTP] Authenticated as \(credentials.username)@\(credentials.host) via \(credentials.authMethod.rawValue)")
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
        // Check if it's a directory first
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

    /// Server-side move: a rename, so moving a folder of videos within the
    /// account costs nothing. Without these two, SFTP fell through to the
    /// download-and-upload path for every intra-account move and copy
    /// (issue #62).
    public func moveItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.moveItem(at: path, toPath: newPath)
    }

    /// Server-side copy, where the server has the `copy-data` extension.
    /// Declines otherwise, and for directories, so the caller falls back.
    public func copyItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.copyItem(at: path, toPath: newPath)
    }

    /// `cp` copies one file; it has no recursive form. A folder copy is
    /// better served by the caller's own walk than by us emitting one
    /// command per file and getting the conflict handling wrong.
    public var canCopyDirectoriesServerSide: Bool {
        get async { false }
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
        if let creds = KeychainService.load(key: keychainKey, as: SFTPCredentials.self) {
            apiClient = SFTPAPIClient(credentials: creds)
        }
    }
}
#endif
