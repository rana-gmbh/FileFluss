import Foundation
import os

private let synologyC2Log = Logger(subsystem: "com.rana.FileFluss", category: "synologyC2")

/// Synology C2 Object Storage is S3-compatible — it speaks the same SigV4
/// protocol but at `<region>.s3.synologyc2.net` (e.g. `eu-001.s3.synologyc2.net`)
/// instead of the AWS endpoint. We reuse the existing `S3APIClient` and
/// just point it at the C2 host via the `endpointHost` field of
/// `S3Credentials`.
final class SynologyC2Provider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .synologyC2

    private var apiClient: S3APIClient?
    private let keychainKey: String

    var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    /// Synology C2 caps a single PUT at 5 GiB, same as AWS — multipart
    /// upload would lift it but isn't implemented yet.
    var maxUploadFileSize: Int64? {
        get async { 5 * 1024 * 1024 * 1024 }
    }

    init(accountId: UUID = UUID()) {
        self.keychainKey = "synologyC2.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    func authenticate(accessKeyId: String, secretAccessKey: String, region: String) async throws {
        let endpointHost = SynologyC2Provider.endpoint(forRegion: region)
        let displayName = "Synology C2 (\(region))"
        let creds = S3Credentials(
            accessKeyId: accessKeyId,
            secretAccessKey: secretAccessKey,
            region: region,
            displayName: displayName,
            endpointHost: endpointHost
        )
        let probe = S3APIClient(credentials: creds)
        // Validate by issuing ListBuckets — same shape AWS expects.
        _ = try await probe.listBuckets()

        self.apiClient = probe
        try KeychainService.save(key: keychainKey, value: creds)
        synologyC2Log.info("[Synology C2] Authenticated for region \(region)")
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

    // MARK: - Region helpers

    /// Maps a C2 region code (e.g. "eu-001") to the corresponding endpoint
    /// host. The user can also paste a fully-formed host directly in the
    /// add-account form for not-yet-listed regions.
    static func endpoint(forRegion region: String) -> String {
        let trimmed = region.trimmingCharacters(in: .whitespacesAndNewlines)
        // Already a hostname? Leave it as-is.
        if trimmed.contains(".") { return trimmed }
        return "\(trimmed).s3.synologyc2.net"
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: S3Credentials.self) {
            apiClient = S3APIClient(credentials: creds)
        }
    }
}

/// Static catalog of Synology C2 regions surfaced in the add-account picker.
enum SynologyC2RegionList {
    struct Region: Sendable {
        let code: String
        let displayName: String
    }

    static let allRegions: [Region] = [
        Region(code: "eu-001", displayName: "Europe (Frankfurt)"),
        Region(code: "us-001", displayName: "Americas (Seattle)"),
        Region(code: "tw-001", displayName: "Asia (Taipei)"),
    ]
}
