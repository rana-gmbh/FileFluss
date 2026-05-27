import Foundation
import os

private let teraboxProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "teraboxProvider")

/// TeraBox Open Platform provider. Authentication uses Device Code mode: the
/// app shows a QR the user scans in the TeraBox app, then polls for the token.
/// Note: the Open Platform sandboxes third-party apps to their own folder
/// (`/From: Other Applications/…`), so this provider only sees files created
/// through this integration — not the user's whole TeraBox drive.
public final class TeraBoxProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .terabox

    private var apiClient: TeraBoxAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "terabox.\(accountId.uuidString)"
        restoreCredentials()
    }

    public init(credentials: TeraBoxCredentials) {
        self.keychainKey = "terabox.\(credentials.userID)"
        self.apiClient = TeraBoxAPIClient(credentials: credentials)
    }

    // MARK: - Device-code authentication

    /// Step 1: fetch a device code + QR for the UI to display.
    public func beginDeviceLogin() async throws -> TeraBoxDeviceCode {
        try await TeraBoxAPIClient.requestDeviceCode()
    }

    /// Step 2: poll until the user authorizes, then persist the credentials.
    @discardableResult
    public func completeDeviceLogin(_ device: TeraBoxDeviceCode) async throws -> TeraBoxCredentials {
        let credentials = try await TeraBoxAPIClient.pollForToken(device)
        let client = TeraBoxAPIClient(credentials: credentials)
        self.apiClient = client
        try KeychainService.save(key: keychainKey, value: credentials)
        teraboxProviderLog.info("[TeraBox] Authenticated user \(credentials.userID)")
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
        let items = try await client.listFolder(path: path)
        await persist()
        return items
    }

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.downloadFile(remotePath: remotePath, to: localURL, onBytes: onBytes)
        await persist()
    }

    public func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.uploadFile(from: localURL, to: remotePath, onBytes: onBytes)
        await persist()
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

    /// Persist the (possibly refreshed) credentials so a single-use refresh
    /// token isn't lost between launches.
    private func persist() async {
        guard let client = apiClient else { return }
        try? KeychainService.save(key: keychainKey, value: await client.currentCredentials())
    }

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: TeraBoxCredentials.self) {
            apiClient = TeraBoxAPIClient(credentials: creds)
        }
    }
}
