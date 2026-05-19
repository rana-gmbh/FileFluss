#if os(macOS)
#if os(macOS)
import Foundation
import os

private let iCloudLog = Logger(subsystem: "com.rana.FileFluss", category: "iCloud")

/// iCloud Drive provider. Unlike every other `CloudProvider`, the
/// "remote" backing store is a local folder
/// (`~/Library/Mobile Documents/com~apple~CloudDocs`) that macOS keeps
/// in sync with iCloud. We just navigate it and let the system handle
/// upload, conflict resolution, and eviction. The only iCloud-specific
/// work is materialising evicted files on demand via
/// `FileManager.startDownloadingUbiquitousItem(at:)` before reading.
public final class ICloudProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .iCloud

    /// Root of iCloud Drive's user-visible files. We don't use
    /// `FileManager.url(forUbiquityContainerIdentifier:)` because that
    /// requires the iCloud entitlement — and we only need the public
    /// CloudDocs path, which any app with file access can read.
    public static let rootURL: URL = FileManager.default
        .homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Mobile Documents/com~apple~CloudDocs", isDirectory: true)

    /// Polling cadence used while waiting for an evicted file to finish
    /// downloading. The system delivers metadata updates through
    /// `NSMetadataQuery`, but for the simple download-then-copy case
    /// polling the resource value is enough and avoids the live-query
    /// plumbing.
    private static let downloadPollInterval: TimeInterval = 0.25

    /// Hard cap on how long we'll wait for `startDownloadingUbiquitousItem`
    /// to finish. iCloud handles huge files fine, but a misbehaving network
    /// shouldn't pin a transfer indefinitely.
    private static let downloadTimeout: TimeInterval = 60 * 30

    public init() {}

    // MARK: - Authentication

    public var isAuthenticated: Bool {
        get async {
            FileManager.default.ubiquityIdentityToken != nil
                && FileManager.default.fileExists(atPath: Self.rootURL.path)
        }
    }

    public func authenticate() async throws {
        guard FileManager.default.ubiquityIdentityToken != nil else {
            throw CloudProviderError.commandFailed(
                "You're not signed into iCloud on this Mac. Open System Settings → Apple Account → iCloud → iCloud Drive and turn it on."
            )
        }
        guard FileManager.default.fileExists(atPath: Self.rootURL.path) else {
            throw CloudProviderError.commandFailed(
                "iCloud Drive is enabled, but the CloudDocs folder isn't available yet. Wait a moment for the system to set it up and try again."
            )
        }
        iCloudLog.info("[iCloud] Authenticated, root=\(Self.rootURL.path)")
    }

    public func disconnect() async throws {}

    public func userDisplayName() async throws -> String {
        // No reliable public API to read the iCloud account name without
        // entitlements. The display name on the account row is set by
        // SyncViewModel using a fixed "iCloud Drive" label.
        "iCloud Drive"
    }

    // MARK: - Listing

    public func listDirectory(at path: String) async throws -> [CloudFileItem] {
        let dir = url(forRemotePath: path)
        guard FileManager.default.fileExists(atPath: dir.path) else {
            throw CloudProviderError.notFound(path)
        }

        let keys: Set<URLResourceKey> = [
            .isDirectoryKey,
            .fileSizeKey,
            .totalFileSizeKey,
            .contentModificationDateKey,
            .ubiquitousItemDownloadingStatusKey,
            .ubiquitousItemIsDownloadingKey,
        ]
        let urls = try FileManager.default.contentsOfDirectory(
            at: dir,
            includingPropertiesForKeys: Array(keys),
            options: [.skipsHiddenFiles, .skipsSubdirectoryDescendants]
        )

        var items: [CloudFileItem] = []
        items.reserveCapacity(urls.count)
        for child in urls {
            let resolvedName = displayName(for: child)
            let childRemotePath = joinedRemotePath(parent: path, name: resolvedName)
            items.append(makeItem(at: child, remotePath: childRemotePath, displayName: resolvedName))
        }
        return items
    }

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        let fileURL = url(forRemotePath: path)
        guard FileManager.default.fileExists(atPath: fileURL.path) else {
            throw CloudProviderError.notFound(path)
        }
        return makeItem(at: fileURL, remotePath: path, displayName: fileURL.lastPathComponent)
    }

    public func folderSize(at path: String) async throws -> Int64 {
        let dir = url(forRemotePath: path)
        guard let enumerator = FileManager.default.enumerator(
            at: dir,
            includingPropertiesForKeys: [.fileSizeKey, .totalFileSizeKey, .isDirectoryKey],
            options: [.skipsHiddenFiles]
        ) else { return 0 }
        var total: Int64 = 0
        while let next = enumerator.nextObject() {
            guard let url = next as? URL else { continue }
            let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .fileSizeKey, .totalFileSizeKey])
            guard values?.isDirectory == false else { continue }
            // `.totalFileSizeKey` is populated even for evicted iCloud
            // items where `.fileSizeKey` reports 0; prefer it.
            if let total64 = values?.totalFileSize { total += Int64(total64) }
            else if let size = values?.fileSize { total += Int64(size) }
        }
        return total
    }

    public func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]? {
        // Could be implemented with NSMetadataQuery against
        // NSMetadataQueryUbiquitousDocumentsScope. Out of scope for v1.
        return nil
    }

    // No storageQuota override: Apple doesn't expose the iCloud account
    // quota through any public API without the iCloud entitlement. The
    // local disk's capacity (.volumeTotalCapacityKey) would be misleading
    // because iCloud Drive's used bytes ≠ Mac's used bytes — files can
    // be evicted from local storage while still consuming iCloud quota.
    // Falling through to the default nil impl keeps the status bar
    // quota segment hidden for iCloud accounts.

    // MARK: - File operations

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let source = url(forRemotePath: remotePath)
        try await materialise(source)
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.copyItem(at: source, to: localURL)
        if let onBytes,
           let size = try? source.resourceValues(forKeys: [.fileSizeKey]).fileSize {
            onBytes(Int64(size))
        }
    }

    public func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let dest = url(forRemotePath: remotePath)
        try ensureParentDirectory(for: dest)
        if FileManager.default.fileExists(atPath: dest.path) {
            try FileManager.default.removeItem(at: dest)
        }
        try FileManager.default.copyItem(at: localURL, to: dest)
        if let onBytes,
           let size = try? localURL.resourceValues(forKeys: [.fileSizeKey]).fileSize {
            onBytes(Int64(size))
        }
    }

    public func deleteItem(at path: String) async throws {
        let target = url(forRemotePath: path)
        guard FileManager.default.fileExists(atPath: target.path) else {
            throw CloudProviderError.notFound(path)
        }
        try FileManager.default.removeItem(at: target)
    }

    public func createDirectory(at path: String) async throws {
        let target = url(forRemotePath: path)
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
    }

    public func renameItem(at path: String, to newName: String) async throws {
        let source = url(forRemotePath: path)
        let dest = source.deletingLastPathComponent().appendingPathComponent(newName)
        try FileManager.default.moveItem(at: source, to: dest)
    }

    public func moveItem(at path: String, toPath newPath: String) async throws {
        let source = url(forRemotePath: path)
        let dest = url(forRemotePath: newPath)
        try ensureParentDirectory(for: dest)
        try FileManager.default.moveItem(at: source, to: dest)
    }

    public func copyItem(at path: String, toPath newPath: String) async throws {
        let source = url(forRemotePath: path)
        let dest = url(forRemotePath: newPath)
        try ensureParentDirectory(for: dest)
        try FileManager.default.copyItem(at: source, to: dest)
    }

    public func setModificationDate(at remotePath: String, to date: Date) async throws {
        let target = url(forRemotePath: remotePath)
        try FileManager.default.setAttributes([.modificationDate: date], ofItemAtPath: target.path)
    }

    // MARK: - Path helpers

    /// Builds an absolute file URL beneath the CloudDocs root from a
    /// provider-style remote path (`/foo/bar.txt`).
    public func url(forRemotePath remotePath: String) -> URL {
        var trimmed = remotePath
        while trimmed.hasPrefix("/") { trimmed.removeFirst() }
        if trimmed.isEmpty { return Self.rootURL }
        return Self.rootURL.appendingPathComponent(trimmed)
    }

    private func joinedRemotePath(parent: String, name: String) -> String {
        let base = parent.hasSuffix("/") ? String(parent.dropLast()) : parent
        let prefix = base.isEmpty ? "" : base
        return "\(prefix)/\(name)"
    }

    private func ensureParentDirectory(for url: URL) throws {
        let parent = url.deletingLastPathComponent()
        guard !FileManager.default.fileExists(atPath: parent.path) else { return }
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true)
    }

    // MARK: - Item construction

    private func makeItem(at url: URL, remotePath: String, displayName: String) -> CloudFileItem {
        let values = try? url.resourceValues(forKeys: [
            .isDirectoryKey,
            .fileSizeKey,
            .totalFileSizeKey,
            .contentModificationDateKey,
            .ubiquitousItemDownloadingStatusKey,
            .ubiquitousItemIsDownloadingKey,
        ])
        let isDir = values?.isDirectory ?? false
        let size: Int64 = {
            if isDir { return 0 }
            if let total = values?.totalFileSize { return Int64(total) }
            if let local = values?.fileSize { return Int64(local) }
            return 0
        }()
        let mod = values?.contentModificationDate ?? .distantPast
        let status = downloadStatus(from: values, isDir: isDir)
        return CloudFileItem(
            id: remotePath,
            name: displayName,
            path: remotePath,
            isDirectory: isDir,
            size: size,
            modificationDate: mod,
            checksum: nil,
            downloadStatus: status
        )
    }

    /// macOS hides evicted files behind a sibling `.foo.icloud` placeholder
    /// on older systems, but `contentsOfDirectory` already presents the
    /// original name on current macOS. Strip the dotted-prefix /
    /// `.icloud` extension just in case so callers always see "foo.pdf"
    /// regardless of physical filename.
    private func displayName(for url: URL) -> String {
        let raw = url.lastPathComponent
        guard raw.hasSuffix(".icloud") else { return raw }
        var stripped = raw
        stripped.removeLast(".icloud".count)
        if stripped.hasPrefix(".") { stripped.removeFirst() }
        return stripped.isEmpty ? raw : stripped
    }

    private func downloadStatus(
        from values: URLResourceValues?,
        isDir: Bool
    ) -> CloudDownloadStatus {
        guard !isDir else { return .local }
        if values?.ubiquitousItemIsDownloading == true { return .downloading }
        switch values?.ubiquitousItemDownloadingStatus {
        case .some(.current), .some(.downloaded):
            return .local
        case .some(.notDownloaded):
            return .evicted
        case .none:
            return .local
        default:
            return .local
        }
    }

    // MARK: - Materialisation

    /// Triggers iCloud to download a file if it's evicted, then waits
    /// until the local copy is current. Safe to call on already-local
    /// files — returns immediately.
    private func materialise(_ url: URL) async throws {
        if isMaterialised(url) { return }
        try FileManager.default.startDownloadingUbiquitousItem(at: url)

        let deadline = Date().addingTimeInterval(Self.downloadTimeout)
        while Date() < deadline {
            if Task.isCancelled { throw CancellationError() }
            if isMaterialised(url) { return }
            try await Task.sleep(nanoseconds: UInt64(Self.downloadPollInterval * 1_000_000_000))
        }
        throw CloudProviderError.commandFailed(
            "Timed out waiting for iCloud to download \(url.lastPathComponent)."
        )
    }

    private func isMaterialised(_ url: URL) -> Bool {
        let values = try? url.resourceValues(forKeys: [.ubiquitousItemDownloadingStatusKey])
        switch values?.ubiquitousItemDownloadingStatus {
        case .some(.current), .some(.downloaded):
            return true
        default:
            return false
        }
    }
}
#endif
#endif
