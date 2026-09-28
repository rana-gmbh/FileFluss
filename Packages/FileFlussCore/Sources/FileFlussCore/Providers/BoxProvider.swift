import Foundation
import os

private let boxProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "boxProvider")

public final class BoxProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .box

    private var apiClient: BoxAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    /// Single-PUT cap. Box requires the chunked upload session API for
    /// anything larger, which isn't implemented yet — the upload path
    /// rejects oversized files pre-flight with a clear message.
    public var maxUploadFileSize: Int64? {
        get async { 50 * 1024 * 1024 }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "box.\(accountId.uuidString)"
        restoreCredentials()
    }

    /// Unused in this app, and a hazard: it stores under a different key
    /// (`box.<login>`) than `init(accountId:)` (`box.<uuid>`), so an account
    /// created through here is invisible to the normal path and asks for
    /// sign-in forever. Kept only because this package is shared with the
    /// iOS app.
    @available(*, deprecated, message: "Use init(accountId:) — this stores credentials under a key the rest of the app never reads.")
    public init(credentials: BoxCredentials) {
        self.keychainKey = "box.\(credentials.userLogin)"
        self.apiClient = BoxAPIClient(credentials: credentials)
    }

    /// Persists every credential change the client makes — above all the
    /// rotated refresh token, which Box issues on each refresh and which is
    /// worthless if it only ever lives in memory (issue #46).
    private func persistCredentialChanges(from client: BoxAPIClient) {
        let key = keychainKey
        Task {
            await client.setCredentialsDidChange { creds in
                try? KeychainService.save(key: key, value: creds)
            }
        }
    }

    // MARK: - Authentication

    public func startOAuthFlow() async throws -> BoxCredentials {
        let credentials = try await BoxAPIClient.startOAuthFlow()
        let client = BoxAPIClient(credentials: credentials)
        self.apiClient = client
        persistCredentialChanges(from: client)
        try KeychainService.save(key: keychainKey, value: credentials)
        boxProviderLog.info("[Box] Authenticated as \(credentials.userLogin)")
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

    /// Server-side move. Box accepts a parent change on `PUT /files/{id}`
    /// and `PUT /folders/{id}`, so cross-folder moves on the same account
    /// don't go through download/re-upload.
    public func moveItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.moveItem(at: path, toPath: newPath)
    }

    /// Server-side copy via `POST /files/{id}/copy` (or the folder
    /// equivalent for trees).
    public func copyItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.copyItem(at: path, toPath: newPath)
    }

    /// Box's public API exposes no way to set `content_modified_at` on an
    /// already-uploaded file (the field is missing from `PUT /files/{id}`).
    /// mtime preservation still works for new uploads because we set the
    /// timestamp inside the upload's multipart attributes JSON; this
    /// method is only invoked for standalone "stamp this existing file"
    /// flows, where there's nothing we can do. Transfer paths already
    /// call us via `try?`, so reporting `.notImplemented` is safe.
    public func setModificationDate(at remotePath: String, to date: Date) async throws {
        throw CloudProviderError.notImplemented
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

    /// Box supports the full set on `PUT /files/{id}` — including reading the
    /// current link back, editing it in place (the PUT is an upsert) and
    /// withdrawing it by nulling the field. These are only the
    /// API's *capabilities*: password and expiry need a paid plan, and an
    /// enterprise admin can restrict or disable public links — in which
    /// case creation either fails with Box's own message or comes back
    /// downgraded (see `shareDowngradeNote`).
    public var shareLinkCapabilities: ShareLinkCapabilities {
        ShareLinkCapabilities(
            canCreate: true,
            canQueryExisting: true,
            canUpdate: true,
            canRemove: true,
            supportsPassword: true,
            supportsExpiry: true,
            supportsDownloadToggle: true,
            passwordRequiresPaidPlan: true,
            expiryRequiresPaidPlan: true
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

    public func updateShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.updateShareLink(at: path, options: options)
    }

    public func removeShareLink(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.removeShareLink(at: path)
    }

    // MARK: - Token refresh

    public func refreshIfNeeded() async throws {
        guard let client = apiClient else { return }
        let newCreds = try await client.refreshTokenIfNeeded()
        try? KeychainService.save(key: keychainKey, value: newCreds)
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: BoxCredentials.self) {
            let client = BoxAPIClient(credentials: creds)
            apiClient = client
            persistCredentialChanges(from: client)
            Task { try? await refreshIfNeeded() }
        }
    }
}
