import Foundation
import os

private let googleDriveProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "googleDriveProvider")

final class GoogleDriveProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .googleDrive

    private var apiClient: GoogleDriveAPIClient?
    private let keychainKey: String
    let accountId: UUID

    var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    init(accountId: UUID = UUID()) {
        self.accountId = accountId
        self.keychainKey = "googledrive.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication (OAuth2 Loopback)

    /// Starts the OAuth2 flow: opens the browser for Google sign-in and waits for the redirect.
    func startOAuthFlow() async throws -> GoogleDriveCredentials {
        let credentials = try await GoogleDriveAPIClient.startOAuthFlow()
        self.apiClient = GoogleDriveAPIClient(accountId: accountId, credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        googleDriveProviderLog.info("[Google Drive] Authenticated as \(credentials.displayName)")
        return credentials
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

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]? {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.searchFiles(query: query, path: path)
    }

    // MARK: - Token Refresh

    func refreshIfNeeded() async throws {
        guard let client = apiClient else { return }
        let newCreds = try await client.refreshTokenIfNeeded()
        try? KeychainService.save(key: keychainKey, value: newCreds)
    }

    // MARK: - Picked folders (drive.file model)

    func pickedFolders() async -> [PickedDriveFolder] {
        guard let client = apiClient else { return [] }
        return await client.currentPickedFolders()
    }

    func addPickedFolders(_ folders: [PickedDriveFolder]) async {
        guard let client = apiClient else { return }
        for folder in folders {
            await client.registerPickedFolder(id: folder.id, name: folder.name)
        }
    }

    func removePickedFolder(id: String) async {
        guard let client = apiClient else { return }
        await client.removePickedFolder(id: id)
    }

    /// Present the Google Picker for the user to choose folders, then persist
    /// the results. Must be called from the main actor because it instantiates
    /// AppKit/WebKit UI. Returns the newly added picked folders (may be empty
    /// if the user cancelled).
    @MainActor
    func presentFolderPicker(preselect existing: [PickedDriveFolder] = []) async throws -> [PickedDriveFolder] {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        // Make sure the token is fresh — Picker JS rejects expired access tokens.
        let creds = try await client.refreshTokenIfNeeded()
        try? KeychainService.save(key: keychainKey, value: creds)

        let picker = GoogleDrivePickerWindow(
            accessToken: creds.accessToken,
            apiKey: GoogleDriveAPIClient.pickerApiKey,
            preselectFileIds: existing.map(\.id)
        )
        let picked = try await picker.present()

        for folder in picked {
            await client.registerPickedFolder(id: folder.id, name: folder.name)
        }
        return picked
    }

    /// Migration path for users upgrading from the pre-drive.file build. If the
    /// picked-folder list is empty and we have legacy broad-scope credentials,
    /// auto-populate the picked list from the top-level folders currently visible
    /// in the user's Drive root. Silently no-ops if the query fails (e.g. the
    /// stored token only has `drive.file` scope and returns nothing useful).
    func migrateFromLegacyRootIfNeeded() async {
        guard let client = apiClient else { return }
        let existing = await client.currentPickedFolders()
        guard existing.isEmpty else { return }

        do {
            let roots = try await client.listLegacyRootFolders()
            guard !roots.isEmpty else { return }
            for folder in roots {
                await client.registerPickedFolder(id: folder.id, name: folder.name)
            }
            googleDriveProviderLog.info("[Google Drive] Auto-migrated \(roots.count) legacy root folders to picked list")
        } catch {
            googleDriveProviderLog.debug("[Google Drive] Legacy migration skipped: \(error.localizedDescription)")
        }
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: GoogleDriveCredentials.self) {
            apiClient = GoogleDriveAPIClient(accountId: accountId, credentials: creds)
            Task {
                try? await refreshIfNeeded()
                await migrateFromLegacyRootIfNeeded()
            }
        }
    }
}
