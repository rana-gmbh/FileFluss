import Foundation
import os

private let nasWebDAVLog = Logger(subsystem: "com.rana.FileFluss", category: "nasWebDAV")

/// NAS makers whose boxes FileFluss reaches through their built-in WebDAV
/// service. Neither UGOS Pro nor ADM publishes a file API for third-party
/// apps — WebDAV is the route both vendors document for that — so these
/// accounts are WebDAV underneath, with each maker's default ports and a
/// self-signed certificate allowed, the way their boxes ship.
public enum NASWebDAVBrand: Sendable {
    /// UGREEN NAS (UGOS Pro): Control Panel → File Service → WebDAV.
    case ugreen
    /// ASUSTOR NAS (ADM): Services → WebDAV.
    case asustor

    public var providerType: CloudProviderType {
        switch self {
        case .ugreen: return .ugreen
        case .asustor: return .asustor
        }
    }

    var keychainPrefix: String {
        switch self {
        case .ugreen: return "ugreen"
        case .asustor: return "asustor"
        }
    }

    /// The WebDAV ports the service listens on out of the box. The DSM-style
    /// web UI ports (9000/9443, 8000/8001) don't serve WebDAV, so a bare
    /// host name gets these.
    var defaultHTTPSPort: Int {
        switch self {
        case .ugreen: return 5006
        case .asustor: return 9802
        }
    }

    var defaultHTTPPort: Int {
        switch self {
        case .ugreen: return 5005
        case .asustor: return 9800
        }
    }
}

public final class NASWebDAVProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType
    public let brand: NASWebDAVBrand

    private var apiClient: WebDAVAPIClient?
    private let keychainKey: String

    public var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    /// Single-PUT uploads, like any WebDAV server: stay under the 32-bit
    /// Content-Length boundary some servers still trip over.
    public var maxUploadFileSize: Int64? {
        get async { 4_000_000_000 }
    }

    public init(brand: NASWebDAVBrand, accountId: UUID = UUID()) {
        self.brand = brand
        self.providerType = brand.providerType
        self.keychainKey = "\(brand.keychainPrefix).\(accountId.uuidString)"
        restoreCredentials()
    }

    // MARK: - Authentication

    public func authenticate(
        serverURL: String,
        username: String,
        password: String,
        allowSelfSignedCertificate: Bool
    ) async throws {
        let normalized = Self.normalizeServerURL(serverURL, brand: brand)
        let resolved: WebDAVCredentials
        do {
            resolved = try await WebDAVAPIClient.authenticate(
                serverURL: normalized,
                username: username,
                password: password,
                allowSelfSignedCertificate: allowSelfSignedCertificate
            )
        } catch CloudProviderError.serverError(let code) where code == 0 {
            // Nothing answered at all: the WebDAV service is off, the port is
            // wrong, or the certificate was refused. Say which knobs to check
            // rather than "server error 0".
            throw CloudProviderError.commandFailed(
                L10n.format("No WebDAV service answered at %@. Turn on WebDAV on the NAS, check the port, and allow a self-signed certificate if the NAS uses one.", normalized)
            )
        }
        let host = URLComponents(string: normalized)?.host ?? brand.keychainPrefix
        let credentials = WebDAVCredentials(
            serverURL: resolved.serverURL,
            username: resolved.username,
            password: resolved.password,
            displayName: "\(username)@\(host)",
            allowSelfSignedCertificate: resolved.allowSelfSignedCertificate
        )
        apiClient = WebDAVAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        nasWebDAVLog.info("[\(self.brand.keychainPrefix)] Authenticated as \(credentials.displayName)")
    }

    public func authenticate() async throws {
        throw CloudProviderError.notAuthenticated
    }

    public func disconnect() async throws {
        apiClient = nil
        try KeychainService.delete(key: keychainKey)
    }

    public func userDisplayName() async throws -> String {
        try await client().userDisplayName()
    }

    // MARK: - File operations

    public func listDirectory(at path: String) async throws -> [CloudFileItem] {
        try await client().listFolder(path: path)
    }

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        try await client().downloadFile(remotePath: remotePath, to: localURL, onBytes: onBytes)
    }

    public func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        try await client().uploadFile(from: localURL, to: remotePath, onBytes: onBytes)
    }

    public func deleteItem(at path: String) async throws {
        try await client().deleteItem(at: path)
    }

    public func createDirectory(at path: String) async throws {
        try await client().createFolder(at: path)
    }

    public func renameItem(at path: String, to newName: String) async throws {
        try await client().renameItem(at: path, to: newName)
    }

    public func moveItem(at path: String, toPath newPath: String) async throws {
        try await client().moveItem(at: path, toPath: newPath)
    }

    public func copyItem(at path: String, toPath newPath: String) async throws {
        try await client().copyItem(at: path, toPath: newPath)
    }

    public func setModificationDate(at remotePath: String, to date: Date) async throws {
        try await client().setModificationDate(at: remotePath, to: date)
    }

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        try await client().getFileInfo(at: path)
    }

    public func folderSize(at path: String) async throws -> Int64 {
        try await client().folderSize(path: path)
    }

    public func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]? {
        try await client().searchFiles(query: query, path: path)
    }

    public func storageQuota() async throws -> CloudStorageQuota? {
        guard let apiClient else { return nil }
        return try await apiClient.storageQuota()
    }

    // MARK: - Server address

    /// Adds `https://` when no scheme was typed and the maker's WebDAV port
    /// when no port was, so "192.168.1.20" reaches the WebDAV service rather
    /// than the web UI. An explicit port is kept as typed.
    public static func normalizeServerURL(_ raw: String, brand: NASWebDAVBrand) -> String {
        var s = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if !s.lowercased().hasPrefix("http://") && !s.lowercased().hasPrefix("https://") {
            s = "https://" + s
        }
        while s.hasSuffix("/") { s.removeLast() }
        guard var components = URLComponents(string: s), components.port == nil else { return s }
        components.port = components.scheme?.lowercased() == "http" ? brand.defaultHTTPPort : brand.defaultHTTPSPort
        return components.string ?? s
    }

    // MARK: - Private

    private func client() throws -> WebDAVAPIClient {
        guard let apiClient else { throw CloudProviderError.notAuthenticated }
        return apiClient
    }

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: WebDAVCredentials.self) {
            apiClient = WebDAVAPIClient(credentials: creds)
        }
    }
}
