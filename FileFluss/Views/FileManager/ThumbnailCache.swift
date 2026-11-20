import AppKit
@preconcurrency import QuickLookThumbnailing
import UniformTypeIdentifiers

/// Caches Quick Look thumbnail previews for local files. Keyed by
/// (path, mtime, size, pixel size) so an edited file invalidates
/// automatically and the list's tiny icons never stand in for a gallery
/// tile. Used in the file-list cells and the gallery tiles to render real
/// previews instead of generic type icons.
@MainActor
final class ThumbnailCache {
    static let shared = ThumbnailCache()

    private struct Key: Hashable {
        let path: String
        let mtime: TimeInterval
        let size: Int64
        /// Edge length asked of Quick Look, in pixels.
        let pixels: Int
    }

    /// One Quick Look request and everyone waiting for it. Main-actor bound
    /// like the cache itself — a nested type doesn't inherit that — so the
    /// cancellation handler can hand it back to the main actor.
    @MainActor
    private final class Generation {
        let request: QLThumbnailGenerator.Request
        var task: Task<NSImage?, Never>?
        var waiters = 0
        var cancelledWaiters = 0
        var isCancelled = false
        var isFinished = false

        init(request: QLThumbnailGenerator.Request) {
            self.request = request
        }
    }

    private var cache: [Key: (image: NSImage, cost: Int)] = [:]
    private var order: [Key] = []
    private var totalCost = 0
    private let maxEntries = 2048
    /// Budget in pixels. A list icon is a few hundred of them, a gallery
    /// tile tens of thousands — counting entries alone would let a gallery
    /// of large photos hold hundreds of megabytes.
    private let maxCost = 24_000_000
    /// In-flight requests, so a row that scrolls out and back in awaits the
    /// existing generation instead of queuing a second one.
    private var inFlight: [Key: Generation] = [:]
    /// Files whose thumbnail generation failed. Without this, every cell
    /// reuse re-asks Quick Look for a thumbnail it already refused, which
    /// is worst exactly where it hurts: scrolling a big folder.
    private var failed: Set<Key> = []

    /// Returns true when a content thumbnail is worth generating instead
    /// of using the generic NSWorkspace icon in the list, where it is drawn
    /// at icon size and only images and PDFs gain from it.
    static func shouldUseThumbnail(for type: UTType?) -> Bool {
        guard let type else { return false }
        return type.conforms(to: .image) || type.conforms(to: .pdf)
    }

    /// The gallery has room for a real preview of nearly anything Quick
    /// Look can draw — movies, documents, text. A file it can't draw is
    /// remembered as such, so asking costs one refusal per file.
    static func shouldUseGalleryThumbnail(for type: UTType?) -> Bool {
        guard let type else { return false }
        return !type.conforms(to: .directory) && !type.conforms(to: .package)
    }

    private static var scale: CGFloat { NSScreen.main?.backingScaleFactor ?? 2 }

    private static func key(for url: URL, mtime: Date, size: Int64, pointSize: CGFloat) -> Key {
        Key(
            path: url.path,
            mtime: mtime.timeIntervalSinceReferenceDate,
            size: size,
            pixels: Int((pointSize * scale).rounded())
        )
    }

    func cached(for url: URL, mtime: Date, size: Int64, pointSize: CGFloat = 18) -> NSImage? {
        cache[Self.key(for: url, mtime: mtime, size: size, pointSize: pointSize)]?.image
    }

    /// Generates (or finds) the thumbnail. When every caller waiting for a
    /// thumbnail has been cancelled — a gallery tile scrolled out of view —
    /// the Quick Look request is withdrawn, so the tiles that are on screen
    /// don't queue behind ones nobody can see any more.
    func thumbnail(for url: URL, mtime: Date, size: Int64, pointSize: CGFloat = 18) async -> NSImage? {
        let key = Self.key(for: url, mtime: mtime, size: size, pointSize: pointSize)
        if let cached = cache[key] { return cached.image }
        if failed.contains(key) { return nil }

        let generation = inFlight[key] ?? start(key, url: url, pointSize: pointSize)
        generation.waiters += 1
        let image = await withTaskCancellationHandler {
            await generation.task?.value ?? nil
        } onCancel: {
            // `self` strongly: the cache lives as long as the app. A weak capture is a mutable
            // box, which Swift 6.3 — the compiler the release is built with — refuses to send
            // to the main actor ("sending 'self' risks causing data races").
            Task { @MainActor in self.waiterCancelled(generation, key: key) }
        }
        finish(generation, key: key, image: image)
        return image
    }

    private func start(_ key: Key, url: URL, pointSize: CGFloat) -> Generation {
        let request = QLThumbnailGenerator.Request(
            fileAt: url,
            size: CGSize(width: pointSize, height: pointSize),
            scale: Self.scale,
            representationTypes: .thumbnail
        )
        let generation = Generation(request: request)
        generation.task = Task { () -> NSImage? in
            let rep = try? await QLThumbnailGenerator.shared.generateBestRepresentation(for: request)
            return rep?.nsImage
        }
        inFlight[key] = generation
        return generation
    }

    private func waiterCancelled(_ generation: Generation, key: Key) {
        guard !generation.isFinished, !generation.isCancelled else { return }
        generation.cancelledWaiters += 1
        guard generation.cancelledWaiters >= generation.waiters else { return }
        generation.isCancelled = true
        // Out of the table at once: a tile that comes back into view while
        // the withdrawn request winds down starts a fresh one rather than
        // inheriting a cancellation.
        if inFlight[key] === generation { inFlight[key] = nil }
        QLThumbnailGenerator.shared.cancel(generation.request)
    }

    private func finish(_ generation: Generation, key: Key, image: NSImage?) {
        // Every waiter passes through here; the first does the bookkeeping.
        guard !generation.isFinished else { return }
        generation.isFinished = true
        if inFlight[key] === generation { inFlight[key] = nil }

        guard let image else {
            // A withdrawn request says nothing about the file.
            if !generation.isCancelled { failed.insert(key) }
            return
        }
        store(image, for: key)
    }

    private func store(_ image: NSImage, for key: Key) {
        let cost: Int = {
            if let rep = image.representations.first, rep.pixelsWide > 0, rep.pixelsHigh > 0 {
                return rep.pixelsWide * rep.pixelsHigh
            }
            return key.pixels * key.pixels
        }()
        if let previous = cache[key] {
            totalCost -= previous.cost
        } else {
            order.append(key)
        }
        cache[key] = (image, cost)
        totalCost += cost
        while (order.count > maxEntries || totalCost > maxCost), !order.isEmpty {
            let evicted = order.removeFirst()
            if let entry = cache.removeValue(forKey: evicted) {
                totalCost -= entry.cost
            }
        }
    }
}
