import Foundation
import os

private let goProProviderLog = Logger(subsystem: "com.rana.FileFluss", category: "goProProvider")

/// Persisted connection details for a paired GoPro camera. The IP is dynamic
/// (USB DHCP / Wi-Fi AP), so only the *last-known* address is stored as a hint
/// — it's always re-confirmed (or re-discovered) before use.
public struct GoProConnection: Codable, Sendable {
    public var cameraName: String
    public var mode: GoProConnectionMode
    public var lastKnownIP: String?

    public init(cameraName: String, mode: GoProConnectionMode, lastKnownIP: String?) {
        self.cameraName = cameraName
        self.mode = mode
        self.lastKnownIP = lastKnownIP
    }
}

/// Read-only `CloudProvider` for GoPro cameras over the Open GoPro HTTP API.
///
/// Browsing, downloading and deleting media are supported; the camera is an
/// import source, so every write operation throws `.notImplemented`. The IP is
/// resolved lazily (and re-resolved on a dropped connection) via
/// `GoProDiscovery`, so an account survives the camera sleeping, being
/// unplugged, or getting a new DHCP lease.
public final class GoProProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .gopro

    private let keychainKey: String
    private var connection: GoProConnection?
    private var client: GoProAPIClient?
    private var keepAliveTask: Task<Void, Never>?

    public var isAuthenticated: Bool {
        get async { connection != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "gopro.\(accountId.uuidString)"
        if let saved = KeychainService.load(key: keychainKey, as: GoProConnection.self) {
            self.connection = saved
        }
    }

    // MARK: - Connection

    /// Connects to a freshly discovered camera and persists it as the account's
    /// connection. Confirms reachability before saving.
    public func connect(camera: GoProCamera) async throws {
        let client = GoProAPIClient(ipAddress: camera.ipAddress, port: GoProDiscovery.port)
        if camera.mode == .wiredUSB {
            await client.enableWiredControl()
        }
        guard await client.ping() else {
            throw CloudProviderError.networkError(URLError(.cannotConnectToHost))
        }
        let conn = GoProConnection(
            cameraName: camera.name,
            mode: camera.mode,
            lastKnownIP: camera.ipAddress
        )
        self.client = client
        self.connection = conn
        try KeychainService.save(key: keychainKey, value: conn)
        startKeepAlive()
        goProProviderLog.info("[GoPro] connected to \(camera.name) at \(camera.ipAddress)")
    }

    public func authenticate() async throws {
        // GoPro accounts are established via `connect(camera:)` from the
        // add-account discovery flow, not a credential prompt.
        throw CloudProviderError.notAuthenticated
    }

    public func disconnect() async throws {
        keepAliveTask?.cancel()
        keepAliveTask = nil
        client = nil
        connection = nil
        try KeychainService.delete(key: keychainKey)
    }

    public func userDisplayName() async throws -> String {
        connection?.cameraName ?? "GoPro Camera"
    }

    /// Lazily (re)builds a live API client, re-resolving the camera's IP if the
    /// last one stopped answering. Throws if the camera can't be reached.
    private func liveClient() async throws -> GoProAPIClient {
        guard let connection else { throw CloudProviderError.notAuthenticated }
        if let client, await client.ping() { return client }

        guard let ip = await GoProDiscovery.resolve(
            lastKnownIP: connection.lastKnownIP,
            mode: connection.mode
        ) else {
            throw CloudProviderError.networkError(URLError(.cannotConnectToHost))
        }

        let fresh = GoProAPIClient(ipAddress: ip, port: GoProDiscovery.port)
        if connection.mode == .wiredUSB { await fresh.enableWiredControl() }
        self.client = fresh

        // Persist the refreshed address as the next hint.
        if ip != connection.lastKnownIP {
            var updated = connection
            updated.lastKnownIP = ip
            self.connection = updated
            try? KeychainService.save(key: keychainKey, value: updated)
        }
        startKeepAlive()
        return fresh
    }

    private func startKeepAlive() {
        keepAliveTask?.cancel()
        keepAliveTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(30))
                guard let client = self?.client else { return }
                await client.keepAlive()
            }
        }
    }

    // MARK: - Read operations

    public func listDirectory(at path: String) async throws -> [CloudFileItem] {
        try await liveClient().listDirectory(at: path)
    }

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        try await liveClient().downloadFile(remotePath: remotePath, to: localURL, onBytes: onBytes)
    }

    public func deleteItem(at path: String) async throws {
        try await liveClient().deleteItem(at: path)
    }

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        try await liveClient().getFileInfo(at: path)
    }

    public func folderSize(at path: String) async throws -> Int64 {
        try await liveClient().folderSize(at: path)
    }

    /// Raw thumbnail JPEG bytes for a media item, or nil. Not part of
    /// `CloudProvider`; callers that know they hold a GoPro provider can use it.
    public func thumbnailData(at path: String) async -> Data? {
        guard let client = try? await liveClient() else { return nil }
        return await client.thumbnail(at: path)
    }

    // MARK: - Write operations (unsupported — camera is an import source)

    public func uploadFile(from localURL: URL, to remotePath: String) async throws {
        throw CloudProviderError.notImplemented
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        throw CloudProviderError.notImplemented
    }

    public func createDirectory(at path: String) async throws {
        throw CloudProviderError.notImplemented
    }

    public func renameItem(at path: String, to newName: String) async throws {
        throw CloudProviderError.notImplemented
    }
}
