import Foundation
import CryptoKit
import os

private let cacheLog = Logger(subsystem: "com.rana.FileFluss", category: "webdav-cache")

/// LRU disk-backed cache of downloaded WebDAV file bytes for one mount.
///
/// The mount's `WebDAVServer` checks this on every GET — when the cached
/// version of `<providerPath>` matches the server-reported `version`
/// (modificationDate + size), the cached file streams back to Finder
/// directly and the provider is never asked. On miss, the server downloads
/// to a temp file as usual, then hands the temp file off to `store(…)`
/// which moves it into the cache directory and registers it.
///
/// Keys are content-addressed: the on-disk filename is the SHA-256 of
/// `<providerPath>\0<version>`, which gives us a filesystem-safe filename
/// and lets a second cached version of the same file coexist with the old
/// one until eviction reclaims it.
public actor WebDAVReadCache {
    public let root: URL
    public private(set) var maxBytes: Int64

    private struct Entry {
        let key: String
        let providerPath: String
        let version: String
        let size: Int64
        var lastAccessed: Date
    }

    private var entries: [String: Entry] = [:]
    private var totalBytes: Int64 = 0

    public init(root: URL, maxBytes: Int64) {
        self.root = root
        self.maxBytes = max(0, maxBytes)
        try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        // No persisted index — we don't store the original (path, version)
        // pairs anywhere, so on init we drop any orphaned files from a
        // previous run. The index gets rebuilt at runtime as fresh
        // lookups land.
        if let contents = try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil) {
            for file in contents { try? FileManager.default.removeItem(at: file) }
        }
    }

    public func setMaxBytes(_ newValue: Int64) {
        maxBytes = max(0, newValue)
        evictIfNeeded()
    }

    /// Returns the cached file's URL when a (path, version) hit exists.
    /// Updates the entry's `lastAccessed` so the LRU stays accurate.
    public func lookup(providerPath: String, version: String) -> URL? {
        let key = Self.key(for: providerPath, version: version)
        guard var entry = entries[key] else { return nil }
        entry.lastAccessed = Date()
        entries[key] = entry
        return fileURL(forKey: key)
    }

    /// Moves the freshly-downloaded `tempURL` into the cache and registers
    /// it under (providerPath, version). Returns the cached URL the server
    /// should stream back to the client. If the move fails for any reason
    /// the original temp URL is returned and the cache stays untouched.
    public func store(providerPath: String, version: String, tempURL: URL) -> URL {
        guard maxBytes > 0 else { return tempURL }
        let key = Self.key(for: providerPath, version: version)
        let target = fileURL(forKey: key)
        // Replace any stale file at the target so a re-download succeeds.
        try? FileManager.default.removeItem(at: target)
        do {
            try FileManager.default.moveItem(at: tempURL, to: target)
        } catch {
            cacheLog.debug("[cache] move into cache failed: \(error.localizedDescription, privacy: .public)")
            return tempURL
        }
        let size = (try? FileManager.default.attributesOfItem(atPath: target.path))?[.size] as? Int64 ?? 0
        // If a previous entry under the same key existed, subtract it first
        // so totalBytes stays consistent.
        if let existing = entries[key] {
            totalBytes -= existing.size
        }
        let entry = Entry(key: key, providerPath: providerPath, version: version,
                          size: size, lastAccessed: Date())
        entries[key] = entry
        totalBytes += size
        evictIfNeeded()
        return target
    }

    /// Drops every cached version of `providerPath`. Called by the server
    /// when a PUT, MOVE, DELETE, or rename touches the path, so the next
    /// GET re-fetches the freshly-written bytes from the provider.
    public func invalidate(providerPath: String) {
        let matching = entries.values.filter { $0.providerPath == providerPath }
        for entry in matching { remove(key: entry.key) }
    }

    /// Wipes the whole cache from disk. Called when the mount is being
    /// torn down for good.
    public func clear() {
        entries.removeAll()
        totalBytes = 0
        clearOnDisk()
    }

    // MARK: - Internals

    private func remove(key: String) {
        guard let entry = entries.removeValue(forKey: key) else { return }
        totalBytes -= entry.size
        try? FileManager.default.removeItem(at: fileURL(forKey: key))
    }

    /// LRU eviction. Drop least-recently-accessed entries until we fit
    /// inside the budget. With many same-mtime files this can degenerate
    /// to "remove in insertion order" — acceptable for a first cut.
    private func evictIfNeeded() {
        guard totalBytes > maxBytes else { return }
        let sorted = entries.values.sorted { $0.lastAccessed < $1.lastAccessed }
        for entry in sorted {
            if totalBytes <= maxBytes { break }
            remove(key: entry.key)
        }
    }

    private func clearOnDisk() {
        if let contents = try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil) {
            for file in contents { try? FileManager.default.removeItem(at: file) }
        }
    }

    private func fileURL(forKey key: String) -> URL {
        root.appendingPathComponent(key)
    }

    private static func key(for providerPath: String, version: String) -> String {
        let raw = providerPath + "\u{0}" + version
        let digest = SHA256.hash(data: Data(raw.utf8))
        return digest.map { String(format: "%02x", $0) }.joined()
    }
}
