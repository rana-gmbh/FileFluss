import Foundation
import FileFlussCore
import os

private let s3ProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "s3Provider")

final class S3Provider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .s3

    private var apiClient: S3APIClient?
    private let keychainKey: String

    var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    /// S3 caps a single PUT at 5 GiB. Larger files would need multipart
    /// upload — not yet implemented.
    var maxUploadFileSize: Int64? {
        get async { 5 * 1024 * 1024 * 1024 }
    }

    init(accountId: UUID = UUID()) {
        self.keychainKey = "s3.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    func authenticate(accessKeyId: String, secretAccessKey: String, region: String) async throws {
        let creds = try await S3APIClient.authenticate(
            accessKeyId: accessKeyId,
            secretAccessKey: secretAccessKey,
            region: region
        )
        self.apiClient = S3APIClient(credentials: creds)
        try KeychainService.save(key: keychainKey, value: creds)
        s3ProviderLog.info("[S3] Authenticated for region \(region)")
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
        return await client.userDisplayName()
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

    func moveItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.moveItem(at: path, toPath: newPath)
    }

    func copyItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.copyItem(at: path, toPath: newPath)
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.getFileInfo(at: path)
    }

    func folderSize(at path: String) async throws -> Int64 {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.folderSize(path: path)
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: S3Credentials.self) {
            apiClient = S3APIClient(credentials: creds)
        }
    }
}
