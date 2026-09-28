import Foundation
import os

private let s3ProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "s3Provider")

public final class S3Provider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .s3

    private var apiClient: S3APIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    /// S3 caps a single PUT at 5 GiB. Larger files would need multipart
    /// upload — not yet implemented.
    public var maxUploadFileSize: Int64? {
        get async { 5 * 1024 * 1024 * 1024 }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "s3.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    /// `rootPath` is the optional bucket the user named. Passing it through
    /// means the connection check probes that bucket rather than the
    /// account's bucket list, which a bucket-scoped key may not read.
    public func authenticate(
        accessKeyId: String,
        secretAccessKey: String,
        region: String,
        rootPath: String? = nil
    ) async throws {
        let creds = try await S3APIClient.authenticate(
            accessKeyId: accessKeyId,
            secretAccessKey: secretAccessKey,
            region: region,
            rootPath: rootPath
        )
        self.apiClient = S3APIClient(credentials: creds)
        try KeychainService.save(key: keychainKey, value: creds)
        s3ProviderLog.info("[S3] Authenticated for region \(region)")
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

    // MARK: - Share links

    /// S3 has no server-side "share" concept: the link is a presigned GET
    /// URL signed locally with the account's own credentials. It therefore
    /// always expires (7 days maximum, and sooner with temporary
    /// credentials) and cannot carry a password.
    public var shareLinkCapabilities: ShareLinkCapabilities {
        ShareLinkCapabilities(
            canCreate: true,
            supportsPassword: false,
            supportsExpiry: true,
            supportsDownloadToggle: false,
            requiresExpiry: true,
            maximumExpiry: S3APIClient.maximumPresignedExpiry
        )
    }

    public func createShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        // The sheet always supplies an expiry for S3 (requiresExpiry), but
        // fall back to the maximum rather than failing if it somehow didn't.
        let seconds = options.expiry.map { $0.timeIntervalSinceNow } ?? S3APIClient.maximumPresignedExpiry
        let url = try await client.presignedDownloadURL(remotePath: path, expiresIn: seconds)
        return CloudShareLink(
            url: url,
            directDownloadURL: url,
            expiresAt: Date().addingTimeInterval(min(seconds, S3APIClient.maximumPresignedExpiry)),
            hasPassword: false,
            note: L10n.text("Anyone with this link can download the file until it expires. The link cannot be revoked early.")
        )
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: S3Credentials.self) {
            apiClient = S3APIClient(credentials: creds)
        }
    }
}
