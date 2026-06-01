import Foundation
import os

private let pickerProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "googleDrivePickerProvider")

/// "Google Drive (Selected Folders)" — Project B. Uses the non-sensitive
/// `drive.file` scope: the user grants access to specific folders through the
/// Google Picker, and those folders become the account's roots. Completely
/// separate from `GoogleDriveProvider` (full `drive`, Project A) so the
/// existing 100 users are untouched.
///
/// Browsing is always live (the API client holds no persistent cache), which
/// is the deliberate fix for the earlier "changes made outside FileFluss
/// weren't reflected" bugs.
public final class GoogleDrivePickerProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .googleDrivePicker

    private var apiClient: GoogleDrivePickerAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "googleDrivePicker.\(accountId.uuidString)"
        if let creds = KeychainService.load(key: keychainKey, as: GoogleDrivePickerCredentials.self) {
            self.apiClient = GoogleDrivePickerAPIClient(credentials: creds)
        }
    }

    // MARK: - Authentication

    /// Step 1 of the add flow: run OAuth and return credentials (with no roots
    /// yet). The caller then presents the Picker and calls `finishConnecting`.
    public static func startOAuth() async throws -> GoogleDrivePickerCredentials {
        try await GoogleDrivePickerAPIClient.startOAuthFlow()
    }

    /// Step 2: persist the credentials plus the folders the user picked, and
    /// activate the client. Returns the account display name.
    @discardableResult
    public func finishConnecting(credentials: GoogleDrivePickerCredentials, roots: [GoogleDrivePickedRoot]) throws -> String {
        var creds = credentials
        creds.roots = roots
        let client = GoogleDrivePickerAPIClient(credentials: creds)
        self.apiClient = client
        try KeychainService.save(key: keychainKey, value: creds)
        pickerProviderLog.info("[GDrivePicker] Connected \(creds.userEmail, privacy: .public) with \(roots.count) folder(s)")
        return creds.displayName
    }

    /// Add more picked folders to an already-connected account.
    public func addRoots(_ roots: [GoogleDrivePickedRoot]) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        await client.addRoots(roots)
        try await persistCredentials(from: client)
    }

    /// Current access token for handing to the Picker UI.
    public func pickerAccessToken() async throws -> String {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.validAccessToken()
    }

    public func authenticate() async throws {
        if apiClient == nil { throw CloudProviderError.notAuthenticated }
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
        let items = try await client.listDirectory(at: path)
        try? await persistCredentials(from: client)   // roots may have been pruned/renamed live
        return items
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
        try await client.createDirectory(at: path)
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
        return try await client.getFileMetadata(at: path)
    }

    public func folderSize(at path: String) async throws -> Int64 {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.folderSize(at: path)
    }

    public func setModificationDate(at remotePath: String, to date: Date) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.setModificationDate(at: remotePath, to: date)
    }

    public func storageQuota() async throws -> CloudStorageQuota? {
        guard let client = apiClient else { return nil }
        return try await client.storageQuota()
    }

    /// Pull the latest credentials snapshot out of the actor and persist it so
    /// rotated refresh tokens and live root pruning survive a relaunch.
    private func persistCredentials(from client: GoogleDrivePickerAPIClient) async throws {
        let creds = await client.credentials
        try KeychainService.save(key: keychainKey, value: creds)
    }
}
