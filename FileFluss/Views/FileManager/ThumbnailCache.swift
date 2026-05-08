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

        let scale = NSScreen.main?.backingScaleFactor ?? 2
        let request = QLThumbnailGenerator.Request(
            fileAt: url,
            size: CGSize(width: pointSize, height: pointSize),
            scale: scale,
            representationTypes: .thumbnail
        )
        do {
            let rep = try await QLThumbnailGenerator.shared.generateBestRepresentation(for: request)
            let img = rep.nsImage
            cache[key] = img
            order.append(key)
            while order.count > maxEntries {
                cache.removeValue(forKey: order.removeFirst())
            }
            return img
        } catch {
            return nil
        }
    }
}
