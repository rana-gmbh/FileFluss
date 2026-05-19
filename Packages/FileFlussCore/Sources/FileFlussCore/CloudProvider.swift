import Foundation

/// Storage usage snapshot for a single cloud account. Some providers
/// (e.g. enterprise plans that haven't been provisioned with a quota)
/// report no upper bound — `totalBytes` is nil in that case and the
/// UI renders just the used figure.
public struct CloudStorageQuota: Sendable, Equatable, Codable {
    public let usedBytes: Int64
    public let totalBytes: Int64?
    public let fetchedAt: Date

    public init(usedBytes: Int64, totalBytes: Int64?, fetchedAt: Date = Date()) {
        self.usedBytes = usedBytes
        self.totalBytes = totalBytes
        self.fetchedAt = fetchedAt
    }
}

public protocol CloudProvider: Sendable {
    var providerType: CloudProviderType { get }

    func authenticate() async throws
    func disconnect() async throws
    var isAuthenticated: Bool { get async }

    /// Maximum per-file upload size accepted by this provider, in bytes.
    /// Returning nil means "no documented limit" — uploads will still be
    /// attempted, and the server may reject them after the bytes have been
    /// transferred. Providers should override this when they have a known
    /// hard cap so the upload path can reject oversized files locally
    /// (pre-flight) instead of wasting bandwidth.
    var maxUploadFileSize: Int64? { get async }

    func listDirectory(at path: String) async throws -> [CloudFileItem]
    func downloadFile(remotePath: String, to localURL: URL) async throws
    func uploadFile(from localURL: URL, to remotePath: String) async throws

    /// Download with byte-level progress. Default implementation forwards to the
    /// non-progress variant; providers that can stream should override to emit deltas.
    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws

    /// Upload with byte-level progress. Default implementation forwards to the
    /// non-progress variant; providers that can stream should override to emit deltas.
    func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws

    func deleteItem(at path: String) async throws
    func createDirectory(at path: String) async throws
    func renameItem(at path: String, to newName: String) async throws

    /// Server-side move on the same account: relocate `path` to `newPath`
    /// (full destination path, including the new filename). Default impl
    /// throws `.notImplemented` so callers can fall back to download+upload.
    func moveItem(at path: String, toPath newPath: String) async throws

    /// Server-side copy on the same account. Same conventions as `moveItem`.
    func copyItem(at path: String, toPath newPath: String) async throws

    func getFileMetadata(at path: String) async throws -> CloudFileItem
    func folderSize(at path: String) async throws -> Int64

    /// Search for files matching the query. Returns nil if the provider does not support search.
    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]?

    /// Sets the modification date on a remote file. Default implementation
    /// is a no-op so providers that don't expose this API still compile.
    /// Used by the transfer paths so a copy/move preserves the source file's
    /// mtime instead of stamping it with the upload time — matching Finder's
    /// behaviour across drives, local folders, and cloud accounts.
    func setModificationDate(at remotePath: String, to date: Date) async throws

    /// Account-wide storage usage. Returning nil means the provider has no
    /// quota API or hasn't been wired to surface one — the status bar then
    /// renders no quota line for that account. The default implementation
    /// returns nil so providers compile without touching their files.
    /// Implementations should NOT cache here; the view model owns caching.
    func storageQuota() async throws -> CloudStorageQuota?
}

extension CloudProvider {
    public func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]? {
        nil
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL)
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        try await uploadFile(from: localURL, to: remotePath)
    }

    public var maxUploadFileSize: Int64? {
        get async { nil }
    }

    public func moveItem(at path: String, toPath newPath: String) async throws {
        throw CloudProviderError.notImplemented
    }

    public func copyItem(at path: String, toPath newPath: String) async throws {
        throw CloudProviderError.notImplemented
    }

    /// Default no-op — providers without an API to set mtime simply skip
    /// the call. Each transfer path uses `try?` so an unsupported provider
    /// never blocks the operation; the file will land with the server's
    /// upload time, which is the existing pre-fix behaviour.
    public func setModificationDate(at remotePath: String, to date: Date) async throws {
        throw CloudProviderError.notImplemented
    }

    public func storageQuota() async throws -> CloudStorageQuota? {
        nil
    }
}
