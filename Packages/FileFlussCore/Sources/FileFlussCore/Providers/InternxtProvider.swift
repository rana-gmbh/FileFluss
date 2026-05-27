import Foundation
import os

private let internxtProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "internxtProvider")

/// Internxt provider. End-to-end encrypted: login derives the account mnemonic
/// locally, and file content is encrypted/decrypted client-side via the Network
/// service. This first pass covers auth + browse + download + folder/file
/// mutations; encrypted upload lands in a follow-up.
public final class InternxtProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .internxt

    private var apiClient: InternxtAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "internxt.\(accountId.uuidString)"
        restoreCredentials()
    }

    public init(credentials: InternxtCredentials) {
        self.keychainKey = "internxt.\(credentials.email)"
        self.apiClient = InternxtAPIClient(credentials: credentials)
    }

    // MARK: - Authentication

    @discardableResult
    public func authenticate(email: String, password: String, twoFactorCode: String) async throws -> InternxtCredentials {
        let credentials = try await InternxtAPIClient.login(email: email, password: password, twoFactorCode: twoFactorCode)
        self.apiClient = InternxtAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        internxtProviderLog.info("[Internxt] Authenticated as \(credentials.email, privacy: .public)")
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

    public func moveItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.moveItem(at: path, toPath: newPath)
    }

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let parent = (path as NSString).deletingLastPathComponent
        let name = (path as NSString).lastPathComponent
        let entries = try await client.listFolder(path: parent.isEmpty ? "/" : parent)
        if let match = entries.first(where: { $0.name == name }) { return match }
        throw CloudProviderError.notFound(path)
    }

    public func folderSize(at path: String) async throws -> Int64 {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let entries = try await client.listFolder(path: path)
        var total: Int64 = 0
        for entry in entries {
            if entry.isDirectory {
                total += try await folderSize(at: entry.path)
            } else {
                total += entry.size
            }
        }
        return total
    }

    public func storageQuota() async throws -> CloudStorageQuota? {
        guard let client = apiClient else { return nil }
        return try await client.storageQuota()
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: InternxtCredentials.self) {
            apiClient = InternxtAPIClient(credentials: creds)
        }
    }
}
