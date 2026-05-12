import Foundation
import os

private let s3CompatLog = Logger(subsystem: "com.rana.FileFluss", category: "s3Compatible")

/// Generic S3-compatible storage — Hetzner Object Storage, MinIO,
/// Wasabi, Backblaze B2 (S3 API), Cloudflare R2, Linode, DigitalOcean
/// Spaces, etc. Reuses the existing `S3APIClient` and just lets the
/// user supply both the endpoint host and the SigV4 region scope.
final class S3CompatibleProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .s3Compatible

    private var apiClient: S3APIClient?
    private let keychainKey: String

    var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    /// Single-PUT cap aligned with AWS / Synology C2. Multipart is not
    /// implemented yet, so files larger than this are rejected
    /// pre-flight by the upload path.
    var maxUploadFileSize: Int64? {
        get async { 5 * 1024 * 1024 * 1024 }
    }

    init(accountId: UUID = UUID()) {
        self.keychainKey = "s3Compatible.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    /// `endpoint` accepts a bare hostname (`fsn1.your-objectstorage.com`),
    /// a full URL (`https://fsn1.your-objectstorage.com`), or a hostname
    /// with port. `region` is whatever the provider wants in the SigV4
    /// credential scope — Hetzner uses the location code (`fsn1`, `nbg1`,
    /// `hel1`), Cloudflare R2 uses `auto`, MinIO usually `us-east-1`.
    func authenticate(
        accessKeyId: String,
        secretAccessKey: String,
        endpoint: String,
        region: String,
        displayName: String?
    ) async throws {
        let host = S3CompatibleProvider.host(fromEndpoint: endpoint)
        let resolvedRegion = region.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? S3CompatibleProvider.regionGuess(fromHost: host)
            : region.trimmingCharacters(in: .whitespacesAndNewlines)
        let trimmedName = displayName?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let name = trimmedName.isEmpty ? "S3 (\(host))" : trimmedName

        let creds = S3Credentials(
            accessKeyId: accessKeyId,
            secretAccessKey: secretAccessKey,
            region: resolvedRegion,
            displayName: name,
            endpointHost: host
        )
        let probe = S3APIClient(credentials: creds)
        _ = try await probe.listBuckets()

        self.apiClient = probe
        try KeychainService.save(key: keychainKey, value: creds)
        s3CompatLog.info("[S3-compat] Authenticated against \(host) (region \(resolvedRegion))")
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

    // MARK: - File operations

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

    // MARK: - Endpoint helpers

    /// Extracts a bare hostname from whatever the user typed.
    static func host(fromEndpoint endpoint: String) -> String {
        let trimmed = endpoint.trimmingCharacters(in: .whitespacesAndNewlines)
        if let url = URL(string: trimmed), let host = url.host { return host }
        // Drop a stray scheme that URL couldn't parse (uncommon).
        var s = trimmed
        for scheme in ["https://", "http://"] {
            if s.lowercased().hasPrefix(scheme) {
                s = String(s.dropFirst(scheme.count))
                break
            }
        }
        if s.hasSuffix("/") { s.removeLast() }
        return s
    }

    /// Best-effort guess of the SigV4 region when the user leaves the
    /// region field blank — pulls the first subdomain off the host,
    /// which matches Hetzner's `fsn1.your-objectstorage.com` pattern
    /// and Synology C2's `eu-005.s3.synologyc2.net`. Falls back to
    /// `us-east-1` (the convention used by MinIO + Backblaze) when we
    /// can't tell.
    static func regionGuess(fromHost host: String) -> String {
        let head = host.split(separator: ".").first.map(String.init) ?? ""
        return head.isEmpty ? "us-east-1" : head
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: S3Credentials.self) {
            apiClient = S3APIClient(credentials: creds)
        }
    }
}
