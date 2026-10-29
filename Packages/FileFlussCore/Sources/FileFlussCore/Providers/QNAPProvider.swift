import Foundation
import os

private let qnapProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "qnapProvider")

public final class QNAPProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .qnap

    private var apiClient: QNAPAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "qnap.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    /// `securityCode` is the 6-digit code of a 2-step account. QTS answers
    /// that login with a token that spares later logins the code.
    public func authenticate(
        serverURL: String,
        username: String,
        password: String,
        securityCode: String? = nil,
        allowSelfSignedCertificate: Bool
    ) async throws {
        // Tokens this account already has carry over to an edit of the same
        // NAS and user, so changing a setting doesn't demand a fresh code.
        // They belong to that one account and never go to another host.
        let stored = KeychainService.load(key: keychainKey, as: QNAPCredentials.self)
        let previous = stored.flatMap { existing -> QNAPCredentials? in
            guard existing.username == username,
                  existing.serverURL == QNAPAPIClient.normalizeServerURL(serverURL)
            else { return nil }
            return existing
        }

        let client = try await QNAPAPIClient.authenticatedClient(
            serverURL: serverURL,
            username: username,
            password: password,
            securityCode: securityCode,
            allowSelfSignedCertificate: allowSelfSignedCertificate,
            previous: previous
        )
        let credentials = await client.credentials
        // Keep the client that logged in: it holds the session, and the next
        // login can't repeat a 2-step code.
        apiClient = client
        try KeychainService.save(key: keychainKey, value: credentials)
        qnapProviderLog.info("[QNAP] Authenticated as \(credentials.displayName)")
    }

    public func authenticate() async throws {
        throw CloudProviderError.notAuthenticated
    }

    public func disconnect() async throws {
        if let apiClient { await apiClient.logout() }
        apiClient = nil
        try KeychainService.delete(key: keychainKey)
    }

    public func userDisplayName() async throws -> String {
        try await client().userDisplayName()
    }

    // MARK: - File operations

    public func listDirectory(at path: String) async throws -> [CloudFileItem] {
        try await withPersistedTokens { try await $0.listFolder(path: path) }
    }

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        try await withPersistedTokens { try await $0.downloadFile(remotePath: remotePath, to: localURL, onBytes: onBytes) }
    }

    public func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        try await withPersistedTokens { try await $0.uploadFile(from: localURL, to: remotePath, onBytes: onBytes) }
    }

    public func deleteItem(at path: String) async throws {
        try await withPersistedTokens { try await $0.deleteItem(at: path) }
    }

    public func createDirectory(at path: String) async throws {
        try await withPersistedTokens { try await $0.createFolder(at: path) }
    }

    public func renameItem(at path: String, to newName: String) async throws {
        try await withPersistedTokens { try await $0.renameItem(at: path, to: newName) }
    }

    public func moveItem(at path: String, toPath newPath: String) async throws {
        try await copyOrMove(from: path, to: newPath, move: true)
    }

    public func copyItem(at path: String, toPath newPath: String) async throws {
        try await copyOrMove(from: path, to: newPath, move: false)
    }

    /// File Station copies and moves keep the source name, so a different
    /// destination name is a rename afterwards.
    private func copyOrMove(from path: String, to newPath: String, move: Bool) async throws {
        let destFolder = (newPath as NSString).deletingLastPathComponent
        let destName = (newPath as NSString).lastPathComponent
        let sourceName = (path as NSString).lastPathComponent
        try await withPersistedTokens { client in
            try await client.copyMove(path: path, toFolderPath: destFolder, move: move)
            if destName != sourceName {
                try await client.renameItem(at: QNAPAPIClient.join(destFolder.isEmpty ? "/" : destFolder, sourceName), to: destName)
            }
        }
    }

    public func setModificationDate(at remotePath: String, to date: Date) async throws {
        try await withPersistedTokens { try await $0.setModificationDate(at: remotePath, to: date) }
    }

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        try await withPersistedTokens { try await $0.getFileInfo(at: path) }
    }

    public func folderSize(at path: String) async throws -> Int64 {
        try await withPersistedTokens { try await $0.folderSize(path: path) }
    }

    public func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]? {
        try await withPersistedTokens { try await $0.searchFiles(query: query, path: path) }
    }

    // MARK: - Share links

    /// File Station links take an access code and an expiry and can be
    /// looked up and deleted. Editing one in place needs settings this
    /// account can't read back reliably, so a change means a new link.
    public var shareLinkCapabilities: ShareLinkCapabilities {
        ShareLinkCapabilities(
            canCreate: true,
            canQueryExisting: true,
            canUpdate: false,
            canRemove: true,
            supportsPassword: true,
            supportsExpiry: true,
            supportsDownloadToggle: false
        )
    }

    public func createShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        try await withPersistedTokens { try await $0.createShareLink(at: path, options: options) }
    }

    public func existingShareLink(at path: String) async throws -> CloudShareLink? {
        try await withPersistedTokens { try await $0.existingShareLink(at: path) }
    }

    public func removeShareLink(at path: String) async throws {
        try await withPersistedTokens { try await $0.removeShareLink(at: path) }
    }

    // MARK: - Private

    private func client() throws -> QNAPAPIClient {
        guard let apiClient else { throw CloudProviderError.notAuthenticated }
        return apiClient
    }

    /// Runs `work` and then saves the credentials if a re-login along the
    /// way replaced a token, so the next launch uses the current one.
    private func withPersistedTokens<T>(_ work: (QNAPAPIClient) async throws -> T) async throws -> T {
        let client = try client()
        let before = await client.credentials
        do {
            let result = try await work(client)
            await persistTokenChanges(of: client, since: before)
            return result
        } catch {
            await persistTokenChanges(of: client, since: before)
            throw error
        }
    }

    private func persistTokenChanges(of client: QNAPAPIClient, since before: QNAPCredentials) async {
        let after = await client.credentials
        guard after.qtoken != before.qtoken || after.vtoken != before.vtoken else { return }
        try? KeychainService.save(key: keychainKey, value: after)
    }

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: QNAPCredentials.self) {
            apiClient = QNAPAPIClient(credentials: creds)
        }
    }
}
