import Foundation
import os

private let synologyC2Log = Logger(subsystem: "com.rana.FileFluss", category: "synologyC2")

/// Synology C2 Object Storage is S3-compatible — it speaks the same SigV4
/// protocol but at `<region>.s3.synologyc2.net` (e.g. `eu-001.s3.synologyc2.net`)
/// instead of the AWS endpoint. We reuse the existing `S3APIClient` and
/// just point it at the C2 host via the `endpointHost` field of
/// `S3Credentials`.
public final class SynologyC2Provider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .synologyC2

    private var apiClient: S3APIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    /// Synology C2 caps a single PUT at 5 GiB, same as AWS — multipart
    /// upload would lift it but isn't implemented yet.
    public var maxUploadFileSize: Int64? {
        get async { 5 * 1024 * 1024 * 1024 }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "synologyC2.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    public func authenticate(
        accessKeyId: String,
        secretAccessKey: String,
        region: String,
        rootPath: String? = nil
    ) async throws {
        // Accept either a region code (`eu-005`), a bare hostname, or a
        // full URL — whatever the user copied out of the C2 console.
        let endpointHost = SynologyC2Provider.endpoint(forRegion: region)
        let signingRegion = SynologyC2Provider.region(fromEndpoint: endpointHost)
        let displayName = "Synology C2 (\(signingRegion))"
        let creds = S3Credentials(
            accessKeyId: accessKeyId,
            secretAccessKey: secretAccessKey,
            region: signingRegion,
            displayName: displayName,
            endpointHost: endpointHost,
            rootPath: rootPath
        )
        let probe = S3APIClient(credentials: creds)
        // Probes the configured bucket when there is one, and the bucket
        // list otherwise — a key scoped to one bucket can't read the list.
        try await probe.verifyAccess(rootPath: rootPath)

        self.apiClient = probe
        try KeychainService.save(key: keychainKey, value: creds)
        synologyC2Log.info("[Synology C2] Authenticated for region \(region)")
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

    // MARK: - Region helpers

    /// Normalises whatever the user typed into a `host:port`-style endpoint
    /// that SigV4 signing can sit on top of. Accepts a full URL
    /// (`https://eu-005.s3.synologyc2.net`), a bare hostname, or a
    /// short region code (`eu-005` → `eu-005.s3.synologyc2.net`).
    public static func endpoint(forRegion region: String) -> String {
        let trimmed = region.trimmingCharacters(in: .whitespacesAndNewlines)
        // Strip scheme + trailing slashes if the user pasted a URL.
        if let url = URL(string: trimmed), let host = url.host {
            return host
        }
        // Already a hostname?
        if trimmed.contains(".") { return trimmed }
        return "\(trimmed).s3.synologyc2.net"
    }

    /// Pulls the SigV4 region scope out of the endpoint hostname.
    /// `eu-005.s3.synologyc2.net` → `eu-005`. Falls back to whatever the
    /// caller passed in if the host doesn't follow that shape.
    public static func region(fromEndpoint endpoint: String) -> String {
        let host = endpoint.hasPrefix("http") ? (URL(string: endpoint)?.host ?? endpoint) : endpoint
        let head = host.split(separator: ".").first.map(String.init) ?? host
        return head
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
