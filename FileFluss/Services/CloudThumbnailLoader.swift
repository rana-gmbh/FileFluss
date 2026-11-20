import Foundation
import CryptoKit
import ImageIO
@preconcurrency import QuickLookThumbnailing
import UniformTypeIdentifiers
import FileFlussCore

/// Thumbnails for the cloud gallery. A cloud file has no local copy for
/// Quick Look, so a preview comes from one of two places:
///
/// 1. the service itself, through `CloudProvider.thumbnail(for:maxPixelSize:)`
///    — a few kilobytes, and the only way videos get a preview;
/// 2. failing that, a download of the file, drawn by Quick Look — only for
///    images and PDFs small enough that the download is cheap.
///
/// Either way the result is scaled to the tile and kept on disk, so a folder
/// seen once shows its previews at once the next time, without the network.
/// Only a few files are fetched at a time, and a tile scrolled out of view
/// withdraws its request (`ThumbnailCache` cancels the task awaiting it).
actor CloudThumbnailLoader {
    static let shared = CloudThumbnailLoader()

    /// The largest file downloaded just to draw its preview. Above it the
    /// tile keeps the type's icon unless the service makes previews.
    static let maxFallbackDownloadBytes: Int64 = 20 * 1024 * 1024

    /// What the disk cache may grow to before the oldest previews go.
    static let maxDiskCacheBytes: Int64 = 300 * 1024 * 1024

    private let directory: URL?
    private let gate = ThumbnailGate(limit: 4)
    private var didPrune = false

    init(directory: URL? = CloudThumbnailLoader.defaultDirectory) {
        self.directory = directory
    }

    static var defaultDirectory: URL? {
        guard let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first else {
            return nil
        }
        let bundle = Bundle.main.bundleIdentifier ?? "FileFluss"
        return caches
            .appendingPathComponent(bundle, isDirectory: true)
            .appendingPathComponent("CloudThumbnails", isDirectory: true)
    }

    // MARK: Which files

    /// Whether a tile should ask for a preview at all: files the service
    /// or Quick Look could draw, and nothing synthetic.
    static func wantsThumbnail(for item: CloudFileItem) -> Bool {
        guard !item.isDirectory, item.role == .normal, item.symbolIconOverride == nil,
              let type = contentType(of: item) else { return false }
        return type.conforms(to: .image) || type.conforms(to: .movie) || type.conforms(to: .pdf)
    }

    /// Whether a file may be downloaded to draw its preview when the service
    /// has none: a small image or PDF that is really there. A file iCloud
    /// has moved off this Mac stays where it is; fetching it for a preview
    /// would bring back every photo the user let go.
    static func mayDownloadForThumbnail(_ item: CloudFileItem) -> Bool {
        guard let type = contentType(of: item),
              type.conforms(to: .image) || type.conforms(to: .pdf) else { return false }
        if case .evicted = item.downloadStatus { return false }
        return item.size > 0 && item.size <= maxFallbackDownloadBytes
    }

    static func contentType(of item: CloudFileItem) -> UTType? {
        let ext = (item.name as NSString).pathExtension
        guard !ext.isEmpty else { return nil }
        return UTType(filenameExtension: ext)
    }

    // MARK: Loading

    func thumbnail(accountId: UUID, item: CloudFileItem, pixels: Int) async -> ThumbnailOutcome {
        let file = cacheFile(accountId: accountId, item: item, pixels: pixels)
        if let file, let data = try? Data(contentsOf: file) {
            return .data(data)
        }
        guard let provider = await SyncEngine.shared.provider(for: accountId) else {
            return .unavailable
        }
        guard await gate.acquire() else { return .unavailable }
        let outcome = await fetch(item, from: provider, pixels: pixels)
        await gate.release()

        if case .data(let data) = outcome, let file {
            store(data, at: file)
        }
        return outcome
    }

    private func fetch(_ item: CloudFileItem, from provider: any CloudProvider, pixels: Int) async -> ThumbnailOutcome {
        // iCloud Drive's files are already on this Mac — unless iCloud moved
        // them off it, and then they stay in the cloud. Quick Look draws the
        // ones that are here in place, videos included, without a copy.
        if let iCloud = provider as? ICloudProvider {
            guard case .local = item.downloadStatus else { return .unavailable }
            return await drawWithQuickLook(iCloud.url(forRemotePath: item.path), pixels: pixels)
        }
        do {
            if let data = try await provider.thumbnail(for: item, maxPixelSize: pixels),
               let scaled = ThumbnailImageCoding.scaled(data, maxPixelSize: pixels) {
                return .data(scaled)
            }
        } catch {
            if Self.isTransient(error) { return .unavailable }
            // Any other refusal — a type the service can't preview, a file
            // too big for its previewer — leaves the download to try.
        }
        guard Self.mayDownloadForThumbnail(item) else { return .none }
        return await drawFromDownload(item, provider: provider, pixels: pixels)
    }

    private func drawFromDownload(_ item: CloudFileItem, provider: any CloudProvider, pixels: Int) async -> ThumbnailOutcome {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussThumbnails", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        // The name keeps its extension — Quick Look picks its renderer by
        // it — but nothing else of the untrusted remote name.
        let ext = (item.name as NSString).pathExtension
        let local = folder.appendingPathComponent(ext.isEmpty ? "file" : "file.\(ext)")
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try await provider.downloadFile(remotePath: item.path, to: local)
            try Task.checkCancellation()
        } catch {
            return Self.isTransient(error) ? .unavailable : .none
        }
        return await drawWithQuickLook(local, pixels: pixels)
    }

    private func drawWithQuickLook(_ url: URL, pixels: Int) async -> ThumbnailOutcome {
        let request = QLThumbnailGenerator.Request(
            fileAt: url,
            size: CGSize(width: pixels, height: pixels),
            scale: 1,
            representationTypes: .thumbnail
        )
        do {
            let rep = try await withTaskCancellationHandler {
                try await QLThumbnailGenerator.shared.generateBestRepresentation(for: request)
            } onCancel: {
                QLThumbnailGenerator.shared.cancel(request)
            }
            guard let data = ThumbnailImageCoding.encoded(rep.cgImage) else { return .none }
            return .data(data)
        } catch {
            return Task.isCancelled ? .unavailable : .none
        }
    }

    /// Errors that say nothing about the file: try again next time.
    static func isTransient(_ error: any Error) -> Bool {
        if error is CancellationError { return true }
        if let urlError = error as? URLError {
            switch urlError.code {
            case .cancelled, .timedOut, .notConnectedToInternet, .networkConnectionLost,
                 .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed, .internationalRoamingOff,
                 .dataNotAllowed:
                return true
            default:
                return false
            }
        }
        return (error as NSError).code == NSUserCancelledError
    }

    // MARK: Disk cache

    /// One file per (account, path, version, size). The name is a hash, so
    /// nothing of a remote path reaches the file system.
    func cacheFile(accountId: UUID, item: CloudFileItem, pixels: Int) -> URL? {
        guard let directory else { return nil }
        let identity = [
            accountId.uuidString,
            item.path,
            String(item.modificationDate.timeIntervalSinceReferenceDate),
            String(item.size),
            String(pixels),
        ].joined(separator: "\u{1F}")
        let digest = SHA256.hash(data: Data(identity.utf8))
        let name = digest.map { String(format: "%02x", $0) }.joined()
        return directory.appendingPathComponent(name).appendingPathExtension("thumb")
    }

    private func store(_ data: Data, at file: URL) {
        let folder = file.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try? data.write(to: file, options: .atomic)
        if !didPrune {
            didPrune = true
            prune(folder, keepingAtMost: Self.maxDiskCacheBytes)
        }
    }

    /// Drops the oldest previews until the cache fits. Once per launch: the
    /// cache only grows by a few kilobytes per preview.
    func prune(_ folder: URL, keepingAtMost limit: Int64) {
        let keys: [URLResourceKey] = [.fileSizeKey, .contentModificationDateKey]
        guard let files = try? FileManager.default.contentsOfDirectory(
            at: folder, includingPropertiesForKeys: keys, options: .skipsHiddenFiles
        ) else { return }
        var entries = files.compactMap { url -> (url: URL, size: Int64, date: Date)? in
            guard let values = try? url.resourceValues(forKeys: Set(keys)) else { return nil }
            return (url, Int64(values.fileSize ?? 0), values.contentModificationDate ?? .distantPast)
        }
        var total = entries.reduce(Int64(0)) { $0 + $1.size }
        guard total > limit else { return }
        entries.sort { $0.date < $1.date }
        // Down to three quarters, so the next launch doesn't prune again at once.
        let target = limit / 4 * 3
        for entry in entries where total > target {
            try? FileManager.default.removeItem(at: entry.url)
            total -= entry.size
        }
    }
}

/// Lets a fixed number of thumbnail fetches run at once. A waiter whose
/// task is cancelled — its tile scrolled away — leaves the queue at once.
actor ThumbnailGate {
    private let limit: Int
    private var running = 0
    private var waiters: [(id: UUID, continuation: CheckedContinuation<Bool, Never>)] = []

    init(limit: Int) {
        self.limit = limit
    }

    /// True once the caller may run; false if it was cancelled first. Only
    /// a true result must be balanced with `release()`.
    func acquire() async -> Bool {
        if Task.isCancelled { return false }
        if running < limit {
            running += 1
            return true
        }
        let id = UUID()
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                waiters.append((id, continuation))
            }
        } onCancel: {
            Task { await self.cancelWaiter(id) }
        }
    }

    func release() {
        if waiters.isEmpty {
            running = max(0, running - 1)
        } else {
            // The slot passes straight to the next waiter.
            waiters.removeFirst().continuation.resume(returning: true)
        }
    }

    private func cancelWaiter(_ id: UUID) {
        guard let index = waiters.firstIndex(where: { $0.id == id }) else { return }
        waiters.remove(at: index).continuation.resume(returning: false)
    }

    var runningCount: Int { running }
    var waitingCount: Int { waiters.count }
}

/// Scaling and encoding previews with ImageIO, off the main thread.
enum ThumbnailImageCoding {
    /// The image in `data` scaled so its longer edge is at most
    /// `maxPixelSize`, upright, re-encoded. Nil if `data` isn't an image.
    static func scaled(_ data: Data, maxPixelSize: Int) -> Data? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: max(1, maxPixelSize),
            kCGImageSourceShouldCacheImmediately: true,
        ]
        guard let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else {
            return nil
        }
        return encoded(image)
    }

    /// JPEG for opaque images, PNG where there is transparency to keep.
    static func encoded(_ image: CGImage) -> Data? {
        let hasAlpha: Bool = {
            switch image.alphaInfo {
            case .none, .noneSkipFirst, .noneSkipLast: return false
            default: return true
            }
        }()
        let type = (hasAlpha ? UTType.png : UTType.jpeg).identifier as CFString
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(output, type, 1, nil) else { return nil }
        let properties: [CFString: Any] = hasAlpha ? [:] : [kCGImageDestinationLossyCompressionQuality: 0.85]
        CGImageDestinationAddImage(destination, image, properties as CFDictionary)
        guard CGImageDestinationFinalize(destination) else { return nil }
        return output as Data
    }
}
