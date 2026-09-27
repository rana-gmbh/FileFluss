import Foundation
import os

private let googleDriveProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "googleDriveProvider")

public final class GoogleDriveProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .googleDrive

    private var apiClient: GoogleDriveAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "googledrive.\(accountId.uuidString)"
        restoreCredentials()
    }

    public init(credentials: GoogleDriveCredentials) {
        self.keychainKey = "googledrive.\(credentials.userEmail)"
        self.apiClient = GoogleDriveAPIClient(credentials: credentials)
    }

    // MARK: - Authentication (OAuth2 Loopback)

    /// Starts the OAuth2 flow: opens the browser for Google sign-in and waits for the redirect.
    public func startOAuthFlow() async throws -> GoogleDriveCredentials {
        let credentials = try await GoogleDriveAPIClient.startOAuthFlow()
        self.apiClient = GoogleDriveAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        googleDriveProviderLog.info("[Google Drive] Authenticated as \(credentials.displayName)")
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
        return try await client.userDisplayName()
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

    public func setModificationDate(at remotePath: String, to date: Date) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.setModificationDate(at: remotePath, to: date)
    }

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.getFileMetadata(at: path)
    }

    public func folderSize(at path: String) async throws -> Int64 {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.folderSize(at: path)
    }

    public func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]? {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.searchFiles(query: query, path: path)
    }

    public func storageQuota() async throws -> CloudStorageQuota? {
        guard let client = apiClient else { return nil }
        return try await client.storageQuota()
    }

    // MARK: - Share links

    /// Drive's "anyone with the link" permission carries no password and no
    /// expiry — those exist only on permissions granted to a named person —
    /// and there is no view-without-download mode either. So there is nothing
    /// an update could change: `canUpdate` stays false. Creating, reading back
    /// and revoking the permission all work; a Workspace admin can still
    /// forbid sharing, which surfaces as Google's own message.
    public var shareLinkCapabilities: ShareLinkCapabilities {
        ShareLinkCapabilities(
            canCreate: true,
            canQueryExisting: true,
            canUpdate: false,
            canRemove: true
        )
    }

    public func createShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.createShareLink(at: path, options: options)
    }

    public func existingShareLink(at path: String) async throws -> CloudShareLink? {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.existingShareLink(at: path)
    }

    /// Deliberately unimplemented. An `anyone` permission has no password, no
    /// expiry and no download toggle, so there is no setting to edit — and
    /// deleting plus re-creating the permission would produce the identical
    /// URL with the identical settings. `canUpdate` is false so the app never
    /// offers this.
    public func updateShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        throw CloudProviderError.notImplemented
    }

    public func removeShareLink(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.removeShareLink(at: path)
    }

    // MARK: - Token Refresh

    public func refreshIfNeeded() async throws {
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
