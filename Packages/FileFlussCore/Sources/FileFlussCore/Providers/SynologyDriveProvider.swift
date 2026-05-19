import Foundation
import os

private let synologyDriveProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "synologyDriveProvider")

public final class SynologyDriveProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .synologyDrive

    private var apiClient: SynologyDriveAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    /// Synology DSM accepts a single uploaded file in one POST. There's
    /// no documented hard cap, but the form-data path streams from disk
    /// so we don't need to pre-flight a size check.
    public var maxUploadFileSize: Int64? {
        get async { nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "synologyDrive.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    public func authenticate(
        serverURL: String,
        username: String,
        password: String,
        otp: String? = nil,
        allowSelfSignedCertificate: Bool
    ) async throws {
        let credentials = try await SynologyDriveAPIClient.authenticate(
            serverURL: serverURL,
            username: username,
            password: password,
            otp: otp,
            allowSelfSignedCertificate: allowSelfSignedCertificate
        )
        self.apiClient = SynologyDriveAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        synologyDriveProviderLog.info("[Synology] Authenticated as \(credentials.displayName)")
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
        let destFolder = (newPath as NSString).deletingLastPathComponent
        let destName = (newPath as NSString).lastPathComponent
        let sourceName = (path as NSString).lastPathComponent

        // Synology's CopyMove always lands the source under destFolder
        // using the *source* filename. If the user wants a different
        // filename at the destination, do an in-place rename after.
        try await client.copyMove(path: path, toFolderPath: destFolder, removeSrc: true)
        if destName != sourceName {
            let intermediate = destFolder.hasSuffix("/") ? destFolder + sourceName : "\(destFolder)/\(sourceName)"
            try? await client.renameItem(at: intermediate, to: destName)
        }
    }

    public func copyItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let destFolder = (newPath as NSString).deletingLastPathComponent
        let destName = (newPath as NSString).lastPathComponent
        let sourceName = (path as NSString).lastPathComponent

        try await client.copyMove(path: path, toFolderPath: destFolder, removeSrc: false)
        if destName != sourceName {
            let intermediate = destFolder.hasSuffix("/") ? destFolder + sourceName : "\(destFolder)/\(sourceName)"
            try? await client.renameItem(at: intermediate, to: destName)
        }
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

    // No storageQuota override: the DSM `SYNO.Core.Quota` API requires
    // admin permissions or DSM-version-specific endpoints that vary
    // across appliances. Falling through to the default nil impl keeps
    // the status bar quota segment hidden for this provider until we
    // can write a probe that works across DSM 6/7 with both
    // admin and standard accounts.

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: SynologyDriveCredentials.self) {
            apiClient = SynologyDriveAPIClient(credentials: creds)
        }
    }
}
