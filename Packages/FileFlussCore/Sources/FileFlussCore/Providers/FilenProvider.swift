import Foundation
import os

private let filenProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "filenProvider")

/// First-pass Filen provider: covers the parts a user can verify end-to-end
/// without risking data loss (auth + browse + folder navigation). Mutating
/// operations (upload, delete, rename, mkdir) throw `notImplemented` for now
/// — they need their own crypto plumbing (chunked AES-256-GCM upload,
/// encrypted-metadata creation) and we want to land the read-only path first
/// so any login / metadata-decrypt issues surface against a real account
/// before we start writing into it.
public final class FilenProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .filen

    private var apiClient: FilenAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "filen.\(accountId.uuidString)"
        restoreCredentials()
    }

    public init(credentials: FilenCredentials) {
        self.keychainKey = "filen.\(credentials.email)"
        self.apiClient = FilenAPIClient(credentials: credentials)
    }

    // MARK: - Authentication

    /// Run the v2 PBKDF2-login dance and stash the resulting bearer token +
    /// master keys + root folder UUID in the keychain. The password is held
    /// only on the stack during this call.
    public func authenticate(email: String, password: String, twoFactorCode: String) async throws -> FilenCredentials {
        let credentials = try await FilenAPIClient.login(
            email: email,
            password: password,
            twoFactorCode: twoFactorCode
        )
        self.apiClient = FilenAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        filenProviderLog.info("[Filen] Authenticated as \(credentials.email, privacy: .public)")
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
        return await client.credentials.email
    }

    // MARK: - File ops (read-only first pass)

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
        try await client.trashItem(at: path)
    }

    public func createDirectory(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.createFolder(at: path)
    }

    public func renameItem(at path: String, to newName: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.renameItem(at: path, to: newName)
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
        if let creds = KeychainService.load(key: keychainKey, as: FilenCredentials.self) {
            apiClient = FilenAPIClient(credentials: creds)
        }
    }
}
