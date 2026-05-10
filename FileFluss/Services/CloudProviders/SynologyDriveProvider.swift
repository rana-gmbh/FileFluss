import Foundation
import os

private let synologyDriveProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "synologyDriveProvider")

final class SynologyDriveProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .synologyDrive

    private var apiClient: SynologyDriveAPIClient?
    private let keychainKey: String

    var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    /// Synology DSM accepts a single uploaded file in one POST. There's
    /// no documented hard cap, but the form-data path streams from disk
    /// so we don't need to pre-flight a size check.
    var maxUploadFileSize: Int64? {
        get async { nil }
    }

    init(accountId: UUID = UUID()) {
        self.keychainKey = "synologyDrive.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    func authenticate(
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

    // MARK: - File Operations

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

    func copyItem(at path: String, toPath newPath: String) async throws {
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

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.getFileInfo(at: path)
    }

    func folderSize(at path: String) async throws -> Int64 {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.folderSize(path: path)
    }

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]? {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.searchFiles(query: query, path: path)
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: SynologyDriveCredentials.self) {
            apiClient = SynologyDriveAPIClient(credentials: creds)
        }
    }
}
