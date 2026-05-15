import Foundation
import os

private let gmxCloudProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "gmxCloudProvider")

/// GMX Cloud (MediaCenter) — no public API; speaks WebDAV. This provider is a
/// preset around `WebDAVAPIClient` so users only have to enter their GMX email
/// and password.
public final class GMXCloudProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .gmxCloud

    /// Endpoint used by the GMX MediaCenter web UI and MailCheck apps. Subject
    /// to change by 1&1 / United Internet — if this stops working, try
    /// `https://webdav.mediacenter.gmx.net/`.
    static let serverURL = "https://webdav.mc.gmx.net/"

    private var apiClient: WebDAVAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    /// GMX MediaCenter rejects single-PUT uploads at the 4 GiB / 32-bit
    /// Content-Length boundary with HTTP 422. Pre-flight files larger than
    /// this so the upload doesn't get sent in the first place.
    public var maxUploadFileSize: Int64? {
        get async { 4_000_000_000 }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "gmxCloud.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    public func authenticate(email: String, password: String) async throws {
        let credentials = try await WebDAVAPIClient.authenticate(
            serverURL: Self.serverURL,
            username: email,
            password: password
        )
        self.apiClient = WebDAVAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        gmxCloudProviderLog.info("[GMXCloud] Authenticated as \(credentials.displayName)")
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

    public func copyItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.copyItem(at: path, toPath: newPath)
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
        if let creds = KeychainService.load(key: keychainKey, as: WebDAVCredentials.self) {
            apiClient = WebDAVAPIClient(credentials: creds)
        }
    }
}
