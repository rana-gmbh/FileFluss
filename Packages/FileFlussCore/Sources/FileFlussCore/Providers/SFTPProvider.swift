#if os(macOS)
import Foundation
import os

private let sftpProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "sftpProvider")

public final class SFTPProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .sftp

    private var apiClient: SFTPAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "sftp.\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    /// Connects with what the user entered and stores it.
    ///
    /// The server's host key is checked as OpenSSH would, but asked about in
    /// the app, as on Windows: a server the account has no key for yet shows
    /// its fingerprint, and a key other than the pinned one shows both. Each
    /// time FileFluss connects only on a yes, and the probe that learns the
    /// key ends before any password or key is sent.
    public func authenticate(
        host: String,
        port: Int,
        username: String,
        password: String? = nil,
        privateKey: String? = nil,
        passphrase: String? = nil,
        remotePath: String = "/",
        confirmTrust: @escaping @Sendable (SFTPTrustQuestion) async -> Bool
    ) async throws {
        // The pinned key carries over while the account points at the same
        // server. One stored before keys were pinned takes over what
        // `~/.ssh/known_hosts` trusts, which is where OpenSSH recorded it.
        var expected: SFTPHostKey?
        if let stored = KeychainService.load(key: keychainKey, as: SFTPCredentials.self),
           stored.host.caseInsensitiveCompare(host) == .orderedSame,
           stored.port == port {
            if let pinned = stored.hostKey {
                expected = pinned
            } else {
                expected = await SFTPHostKeys.fromUserKnownHosts(host: host, port: port)
            }
        }

        let pinned: SFTPHostKey
        if let expected {
            pinned = expected
        } else {
            let presented = try await SFTPHostKeys.probe(host: host, port: port, username: username)
            guard await confirmTrust(.firstConnection(host: host, fingerprint: presented.fingerprint)) else {
                throw SFTPHostKeyNotTrusted()
            }
            pinned = presented
        }

        func connect(_ key: SFTPHostKey) async throws -> SFTPCredentials {
            try await SFTPAPIClient.authenticate(
                host: host,
                port: port,
                username: username,
                password: password,
                privateKey: privateKey,
                passphrase: passphrase,
                remotePath: remotePath,
                hostKey: key
            )
        }

        let credentials: SFTPCredentials
        do {
            credentials = try await connect(pinned)
        } catch is SFTPHostKeyMismatch {
            let presented = try await SFTPHostKeys.probe(host: host, port: port, username: username)
            guard await confirmTrust(.changed(host: host, previous: pinned.fingerprint, now: presented.fingerprint)) else {
                throw SFTPHostKeyMismatch()
            }
            // Pin exactly the key the user just saw, not whatever comes next.
            credentials = try await connect(presented)
        }

        self.apiClient = makeClient(credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        sftpProviderLog.info("[SFTP] Authenticated as \(credentials.username)@\(credentials.host) via \(credentials.authMethod.rawValue)")
    }

    var remotePath: String {
        get async { apiClient?.credentials.remotePath ?? "/" }
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
        // Check if it's a directory first
        let info = try await client.getFileInfo(at: path)
        try await client.deleteItem(at: path, isDirectory: info.isDirectory)
    }

    public func createDirectory(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.createFolder(at: path)
    }

    public func renameItem(at path: String, to newName: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.renameItem(at: path, to: newName)
    }

    /// Server-side move: a rename, so moving a folder of videos within the
    /// account costs nothing. Without these two, SFTP fell through to the
    /// download-and-upload path for every intra-account move and copy
    /// (issue #62).
    public func moveItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.moveItem(at: path, toPath: newPath)
    }

    /// Server-side copy, where the server has the `copy-data` extension.
    /// Declines otherwise, and for directories, so the caller falls back.
    public func copyItem(at path: String, toPath newPath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.copyItem(at: path, toPath: newPath)
    }

    /// `cp` copies one file; it has no recursive form. A folder copy is
    /// better served by the caller's own walk than by us emitting one
    /// command per file and getting the conflict handling wrong.
    public var canCopyDirectoriesServerSide: Bool {
        get async { false }
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

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: SFTPCredentials.self) {
            apiClient = makeClient(creds)
        }
    }

    /// A client that saves the account when it takes over a host key, which
    /// an account stored before keys were pinned does on first use.
    private func makeClient(_ credentials: SFTPCredentials) -> SFTPAPIClient {
        let key = keychainKey
        return SFTPAPIClient(credentials: credentials, onHostKeyPinned: { updated in
            try? KeychainService.save(key: key, value: updated)
        })
    }
}
#endif
