import Foundation
import os

private let oneDriveProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "oneDriveProvider")

final class OneDriveProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .oneDrive

    private var apiClient: OneDriveAPIClient?
    private let keychainKey: String

    var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    init(accountId: UUID = UUID()) {
        self.keychainKey = "onedrive.\(accountId.uuidString)"
        restoreCredentials()
    }

    init(credentials: OneDriveCredentials) {
        self.keychainKey = "onedrive.\(credentials.userEmail)"
        self.apiClient = OneDriveAPIClient(credentials: credentials)
    }

    // MARK: - Authentication (Device Code Flow)

    /// Step 1: Request a device code for the user to enter at the Microsoft login page.
    func startDeviceCodeFlow() async throws -> OneDriveDeviceCode {
        return try await OneDriveAPIClient.requestDeviceCode()
    }

    /// Step 2: Poll Microsoft until the user completes sign-in. Saves credentials on success.
    func completeDeviceCodeFlow(deviceCode: String) async throws {
        let credentials = try await OneDriveAPIClient.pollForToken(deviceCode: deviceCode)
        self.apiClient = OneDriveAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        oneDriveProviderLog.info("[OneDrive] Authenticated as \(credentials.userEmail)")
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
        return try await client.userDisplayName()
    }

    // MARK: - File Operations

    func listDirectory(at path: String) async throws -> [CloudFileItem] {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.listFolder(path: path)
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.downloadFile(remotePath: remotePath, to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.uploadFile(from: localURL, to: remotePath)
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

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.getFileMetadata(at: path)
    }

    func folderSize(at path: String) async throws -> Int64 {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.folderSize(at: path)
    }

    // MARK: - Token Refresh

    /// Refreshes credentials if expired and persists the updated tokens.
    func refreshIfNeeded() async throws {
        guard let client = apiClient else { return }
        let newCreds = try await client.refreshTokenIfNeeded()
        try? KeychainService.save(key: keychainKey, value: newCreds)
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: OneDriveCredentials.self) {
            apiClient = OneDriveAPIClient(credentials: creds)
            // Refresh token in background if needed
            Task {
                try? await refreshIfNeeded()
            }
        }
    }
}
