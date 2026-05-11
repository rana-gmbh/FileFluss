import Foundation
import SwiftUI

/// Manages the on-disk preview/download cache used by `CloudFileManagerViewModel`
/// (`FileFluss-cloud-<account-uuid>` directories under the system temp dir).
///
/// User preferences live in `@AppStorage`:
/// - `cacheAutoManage` (default true): on launch, prune files older than
///   `cacheAutoDeleteDays` and enforce `cacheMaxSizeMB`.
/// - `cacheMaxSizeMB` (default 1000): soft cap. When exceeded, the oldest
///   files (by modification date) are evicted until the total fits.
/// - `cacheAutoDeleteDays` (default 7): files older than this are removed
///   when auto-management runs.
@MainActor
@Observable
final class CacheManager {
    static let shared = CacheManager()

    /// Last measured cache size in bytes. `nil` until first refresh.
    var currentSize: Int64?
    var isCalculating = false
    var isClearing = false

    static let defaultMaxSizeMB: Int = 1000
    static let defaultAutoDeleteDays: Int = 7
    static let minSizeMB: Double = 100
    static let maxSizeMB: Double = 10_000

    private init() {}

    /// Cache directory roots managed by FileFluss. Currently the per-account
    /// preview/download caches; other transient temp files (sync staging,
    /// drag-out copies) clean themselves up in scope and aren't included.
    private func cacheDirectoryURLs() -> [URL] {
        let tempDir = FileManager.default.temporaryDirectory
        guard let contents = try? FileManager.default.contentsOfDirectory(
            at: tempDir,
            includingPropertiesForKeys: nil
        ) else { return [] }
        return contents.filter { $0.lastPathComponent.hasPrefix("FileFluss-cloud-") }
    }

    func refreshSize() async {
        isCalculating = true
        let urls = cacheDirectoryURLs()
        let size = await Task.detached(priority: .utility) {
            urls.reduce(Int64(0)) { $0 + (Self.totalSize(at: $1) ?? 0) }
        }.value
        currentSize = size
        isCalculating = false
    }

    func clearAll() async {
        isClearing = true
        let urls = cacheDirectoryURLs()
        await Task.detached(priority: .utility) {
            for url in urls {
                try? FileManager.default.removeItem(at: url)
                try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
            }
        }.value
        currentSize = 0
        isClearing = false
    }

    /// Runs the user's configured maintenance: prune by age, then enforce
    /// the size cap. Safe to call on launch — finishes off the main thread.
    func runAutoMaintenance(maxAgeDays: Int, maxSizeMB: Int) async {
        let urls = cacheDirectoryURLs()
        let cutoff = Date().addingTimeInterval(-Double(maxAgeDays) * 86_400)
        let maxBytes = Int64(maxSizeMB) * 1_024 * 1_024
        await Task.detached(priority: .utility) {
            for root in urls {
                Self.pruneOldFiles(in: root, olderThan: cutoff)
            }
            Self.enforceLimit(in: urls, maxBytes: maxBytes)
        }.value
        await refreshSize()
    }

    /// Enforces the size cap without pruning by age. Used when the user
    /// changes the slider and wants the new limit applied immediately.
    func enforceMaxSize(_ maxSizeMB: Int) async {
        let urls = cacheDirectoryURLs()
        let maxBytes = Int64(maxSizeMB) * 1_024 * 1_024
        await Task.detached(priority: .utility) {
            Self.enforceLimit(in: urls, maxBytes: maxBytes)
        }.value
        await refreshSize()
    }

    // MARK: - Off-main helpers

    nonisolated private static func totalSize(at root: URL) -> Int64? {
        guard let enumerator = FileManager.default.enumerator(
            at: root,
            includingPropertiesForKeys: [.fileSizeKey, .isDirectoryKey]
        ) else { return nil }
        var total: Int64 = 0
        for case let url as URL in enumerator {
            guard let values = try? url.resourceValues(forKeys: [.fileSizeKey, .isDirectoryKey]),
                  values.isDirectory != true else { continue }
            total += Int64(values.fileSize ?? 0)
        }
        return total
    }

    nonisolated private static func pruneOldFiles(in root: URL, olderThan cutoff: Date) {
        guard let enumerator = FileManager.default.enumerator(
            at: root,
            includingPropertiesForKeys: [.contentModificationDateKey, .isDirectoryKey]
        ) else { return }
        for case let url as URL in enumerator {
            guard let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .isDirectoryKey]),
                  values.isDirectory != true,
                  let mod = values.contentModificationDate,
                  mod < cutoff else { continue }
            try? FileManager.default.removeItem(at: url)
        }
    }

    nonisolated private static func enforceLimit(in roots: [URL], maxBytes: Int64) {
        guard maxBytes > 0 else { return }
        struct Entry { let url: URL; let size: Int64; let mod: Date }
        var entries: [Entry] = []
        var total: Int64 = 0
        for root in roots {
            guard let enumerator = FileManager.default.enumerator(
                at: root,
                includingPropertiesForKeys: [.fileSizeKey, .contentModificationDateKey, .isDirectoryKey]
            ) else { continue }
            for case let url as URL in enumerator {
                guard let values = try? url.resourceValues(forKeys: [
                    .fileSizeKey, .contentModificationDateKey, .isDirectoryKey
                ]), values.isDirectory != true else { continue }
                let size = Int64(values.fileSize ?? 0)
                let mod = values.contentModificationDate ?? .distantPast
                entries.append(.init(url: url, size: size, mod: mod))
                total += size
            }
        }
        guard total > maxBytes else { return }
        entries.sort { $0.mod < $1.mod } // oldest first
        var evicted: Int64 = 0
        let need = total - maxBytes
        for entry in entries {
            if evicted >= need { break }
            try? FileManager.default.removeItem(at: entry.url)
            evicted += entry.size
        }
    }
}
