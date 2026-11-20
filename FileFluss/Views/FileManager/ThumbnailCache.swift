import AppKit
@preconcurrency import QuickLookThumbnailing
import UniformTypeIdentifiers

/// What a thumbnail source made of one file.
enum ThumbnailOutcome: Sendable {
    /// Encoded image bytes, JPEG or PNG.
    case data(Data)
    /// There is no preview of this file, and asking again won't change that.
    case none
    /// Nothing this time — the network was down, the service busy. Not
    /// remembered, so the next time the tile shows it asks again.
    case unavailable
}

/// Caches thumbnail previews in memory: Quick Look's for local files, and
/// the cloud gallery's, which come from the service or from a small
/// download. Keyed by (identity, mtime, size, pixel size) so an edited file
/// invalidates automatically and the list's tiny icons never stand in for a
/// gallery tile.
@MainActor
final class ThumbnailCache {
    static let shared = ThumbnailCache()

    private struct Key: Hashable {
        /// A local file's path, or a cloud file's account-qualified path.
        let identity: String
        let mtime: TimeInterval
        let size: Int64
        /// Edge length asked for, in pixels.
        let pixels: Int
    }

    private enum Result {
        case image(NSImage)
        /// The file has no thumbnail; remembered.
        case refused
        /// No thumbnail this time; forgotten.
        case transient
    }

    /// One request and everyone waiting for it. Main-actor bound like the
    /// cache itself — a nested type doesn't inherit that — so the
    /// cancellation handler can hand it back to the main actor.
    @MainActor
    private final class Generation {
        var task: Task<Result, Never>?
        /// Withdraws the work behind `task` when nobody waits for it any more.
        var withdraw: (@MainActor () -> Void)?
        var waiters = 0
        var cancelledWaiters = 0
        var isCancelled = false
        var isFinished = false
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
    /// reuse re-asks for a thumbnail that was already refused, which is
    /// worst exactly where it hurts: scrolling a big folder.
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

    static var scale: CGFloat { NSScreen.main?.backingScaleFactor ?? 2 }

    static func pixels(for pointSize: CGFloat) -> Int {
        Int((pointSize * scale).rounded())
    }

    private static func key(identity: String, mtime: Date, size: Int64, pointSize: CGFloat) -> Key {
        Key(
            identity: identity,
            mtime: mtime.timeIntervalSinceReferenceDate,
            size: size,
            pixels: pixels(for: pointSize)
        )
    }

    // MARK: Local files (Quick Look)

    func cached(for url: URL, mtime: Date, size: Int64, pointSize: CGFloat = 18) -> NSImage? {
        cached(identity: url.path, mtime: mtime, size: size, pointSize: pointSize)
    }

    /// Generates (or finds) the thumbnail. When every caller waiting for a
    /// thumbnail has been cancelled — a gallery tile scrolled out of view —
    /// the Quick Look request is withdrawn, so the tiles that are on screen
    /// don't queue behind ones nobody can see any more.
    func thumbnail(for url: URL, mtime: Date, size: Int64, pointSize: CGFloat = 18) async -> NSImage? {
        let key = Self.key(identity: url.path, mtime: mtime, size: size, pointSize: pointSize)
        return await thumbnail(for: key) {
            let request = QLThumbnailGenerator.Request(
                fileAt: url,
                size: CGSize(width: pointSize, height: pointSize),
                scale: Self.scale,
                representationTypes: .thumbnail
            )
            let task = Task { () -> Result in
                let rep = try? await QLThumbnailGenerator.shared.generateBestRepresentation(for: request)
                return rep.map { .image($0.nsImage) } ?? .refused
            }
            return (task, { QLThumbnailGenerator.shared.cancel(request) })
        }
    }

    // MARK: Files without a local copy

    func cached(identity: String, mtime: Date, size: Int64, pointSize: CGFloat) -> NSImage? {
        cache[Self.key(identity: identity, mtime: mtime, size: size, pointSize: pointSize)]?.image
    }

    /// The thumbnail of a file Quick Look can't reach — a cloud file —
    /// made by `generate`, which gets the edge length in pixels. Shares the
    /// local thumbnails' memory budget, de-duplication and withdrawal: a
    /// tile scrolled out of view cancels the work behind it.
    func thumbnail(
        identity: String,
        mtime: Date,
        size: Int64,
        pointSize: CGFloat,
        generate: @escaping @Sendable (_ pixels: Int) async -> ThumbnailOutcome
    ) async -> NSImage? {
        let key = Self.key(identity: identity, mtime: mtime, size: size, pointSize: pointSize)
        let pixels = key.pixels
        return await thumbnail(for: key) {
            let task = Task { () -> Result in
                switch await generate(pixels) {
                case .data(let data):
                    return NSImage(data: data).map { .image($0) } ?? .refused
                case .none:
                    return .refused
                case .unavailable:
                    return .transient
                }
            }
            return (task, { task.cancel() })
        }
    }

    // MARK: Shared machinery

    private func thumbnail(
        for key: Key,
        start: () -> (Task<Result, Never>, @MainActor () -> Void)
    ) async -> NSImage? {
        if let cached = cache[key] { return cached.image }
        if failed.contains(key) { return nil }

        let generation: Generation
        if let existing = inFlight[key] {
            generation = existing
        } else {
            generation = Generation()
            let (task, withdraw) = start()
            generation.task = task
            generation.withdraw = withdraw
            inFlight[key] = generation
        }
        generation.waiters += 1
        let result = await withTaskCancellationHandler {
            await generation.task?.value ?? .transient
        } onCancel: {
            // `self` strongly: the cache lives as long as the app. A weak capture is a mutable
            // box, which Swift 6.3 — the compiler the release is built with — refuses to send
            // to the main actor ("sending 'self' risks causing data races").
            Task { @MainActor in self.waiterCancelled(generation, key: key) }
        }
        finish(generation, key: key, result: result)
        if case .image(let image) = result { return image }
        return nil
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
        generation.withdraw?()
    }

    private func finish(_ generation: Generation, key: Key, result: Result) {
        // Every waiter passes through here; the first does the bookkeeping.
        guard !generation.isFinished else { return }
        generation.isFinished = true
        if inFlight[key] === generation { inFlight[key] = nil }

        switch result {
        case .image(let image):
            store(image, for: key)
        case .refused:
            // A withdrawn request says nothing about the file.
            if !generation.isCancelled { failed.insert(key) }
        case .transient:
            break
        }
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
