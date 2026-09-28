import AppKit
@preconcurrency import QuickLookThumbnailing
import UniformTypeIdentifiers

/// Caches Quick Look thumbnail previews for local files. Keyed by
/// (path, mtime, size) so an edited file invalidates automatically.
/// Used in the file-list cells to render real image / PDF previews
/// instead of generic type icons.
@MainActor
final class ThumbnailCache {
    static let shared = ThumbnailCache()

    private struct Key: Hashable {
        let path: String
        let mtime: TimeInterval
        let size: Int64
    }

    private var cache: [Key: NSImage] = [:]
    private var order: [Key] = []
    private let maxEntries = 512
    /// In-flight requests, so a row that scrolls out and back in awaits the
    /// existing generation instead of queuing a second one.
    private var inFlight: [Key: Task<NSImage?, Never>] = [:]
    /// Files whose thumbnail generation failed. Without this, every cell
    /// reuse re-asks Quick Look for a thumbnail it already refused, which
    /// is worst exactly where it hurts: scrolling a big folder.
    private var failed: Set<Key> = []

    /// Returns true when a content thumbnail is worth generating instead
    /// of using the generic NSWorkspace icon.
    static func shouldUseThumbnail(for type: UTType?) -> Bool {
        guard let type else { return false }
        return type.conforms(to: .image) || type.conforms(to: .pdf)
    }

    func cached(for url: URL, mtime: Date, size: Int64) -> NSImage? {
        cache[Key(path: url.path, mtime: mtime.timeIntervalSinceReferenceDate, size: size)]
    }

    func thumbnail(for url: URL, mtime: Date, size: Int64, pointSize: CGFloat = 18) async -> NSImage? {
        let key = Key(path: url.path, mtime: mtime.timeIntervalSinceReferenceDate, size: size)
        if let cached = cache[key] { return cached }
        if failed.contains(key) { return nil }
        if let running = inFlight[key] { return await running.value }

        let scale = NSScreen.main?.backingScaleFactor ?? 2
        let task = Task { () -> NSImage? in
            let request = QLThumbnailGenerator.Request(
                fileAt: url,
                size: CGSize(width: pointSize, height: pointSize),
                scale: scale,
                representationTypes: .thumbnail
            )
            let rep = try? await QLThumbnailGenerator.shared.generateBestRepresentation(for: request)
            return rep?.nsImage
        }
        inFlight[key] = task
        let image = await task.value
        inFlight[key] = nil

        guard let image else {
            failed.insert(key)
            return nil
        }
        cache[key] = image
        order.append(key)
        while order.count > maxEntries {
            cache.removeValue(forKey: order.removeFirst())
        }
        return image
    }
}
