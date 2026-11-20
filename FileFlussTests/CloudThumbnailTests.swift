import Testing
import AppKit
import ImageIO
import UniformTypeIdentifiers
import FileFlussCore
@testable import FileFluss

private func cloudItem(
    _ name: String,
    path: String? = nil,
    size: Int64 = 1_000_000,
    isDirectory: Bool = false,
    status: CloudDownloadStatus = .local,
    role: CloudItemRole = .normal,
    symbol: String? = nil,
    modified: Date = Date(timeIntervalSinceReferenceDate: 700_000_000)
) -> CloudFileItem {
    CloudFileItem(
        id: path ?? "/\(name)",
        name: name,
        path: path ?? "/\(name)",
        isDirectory: isDirectory,
        size: size,
        modificationDate: modified,
        checksum: nil,
        downloadStatus: status,
        symbolIconOverride: symbol,
        role: role
    )
}

/// A solid-colour image of the given size, encoded as `type`.
private func imageData(width: Int, height: Int, type: UTType = .jpeg, orientation: Int? = nil) -> Data {
    let context = CGContext(
        data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
        space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
    )!
    context.setFillColor(CGColor(red: 0.2, green: 0.5, blue: 0.8, alpha: 1))
    context.fill(CGRect(x: 0, y: 0, width: width, height: height))
    let image = context.makeImage()!
    let output = NSMutableData()
    let destination = CGImageDestinationCreateWithData(output, type.identifier as CFString, 1, nil)!
    var properties: [CFString: Any] = [:]
    if let orientation { properties[kCGImagePropertyOrientation] = orientation }
    CGImageDestinationAddImage(destination, image, properties as CFDictionary)
    CGImageDestinationFinalize(destination)
    return output as Data
}

private func pixelSize(of data: Data) -> (width: Int, height: Int)? {
    guard let source = CGImageSourceCreateWithData(data as CFData, nil),
          let props = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
          let width = props[kCGImagePropertyPixelWidth] as? Int,
          let height = props[kCGImagePropertyPixelHeight] as? Int else { return nil }
    return (width, height)
}

/// Which cloud files the gallery asks a preview for, and which of those it
/// may download to draw one itself.
@Suite("Cloud thumbnails: which files")
struct CloudThumbnailEligibilityTests {

    @Test("photos, videos and PDFs get a preview; folders and other files keep their icon")
    func wantsThumbnail() {
        #expect(CloudThumbnailLoader.wantsThumbnail(for: cloudItem("IMG_0001.JPG")))
        #expect(CloudThumbnailLoader.wantsThumbnail(for: cloudItem("photo.heic")))
        #expect(CloudThumbnailLoader.wantsThumbnail(for: cloudItem("clip.mov")))
        #expect(CloudThumbnailLoader.wantsThumbnail(for: cloudItem("report.pdf")))

        #expect(!CloudThumbnailLoader.wantsThumbnail(for: cloudItem("Holiday.jpg", isDirectory: true)))
        #expect(!CloudThumbnailLoader.wantsThumbnail(for: cloudItem("notes.txt")))
        #expect(!CloudThumbnailLoader.wantsThumbnail(for: cloudItem("README")))
    }

    @Test("synthetic entries — shared drives, repositories — never ask")
    func syntheticEntries() {
        #expect(!CloudThumbnailLoader.wantsThumbnail(for: cloudItem("drive.png", symbol: "externaldrive")))
        for role in [CloudItemRole.virtualContainer, .driveRoot] {
            #expect(!CloudThumbnailLoader.wantsThumbnail(for: cloudItem("x.jpg", role: role)))
        }
    }

    @Test("small images and PDFs may be downloaded; videos and big files may not")
    func mayDownload() {
        #expect(CloudThumbnailLoader.mayDownloadForThumbnail(cloudItem("a.jpg", size: 3_000_000)))
        #expect(CloudThumbnailLoader.mayDownloadForThumbnail(cloudItem("a.pdf", size: 3_000_000)))
        #expect(!CloudThumbnailLoader.mayDownloadForThumbnail(cloudItem("a.mov", size: 3_000_000)))

        let limit = CloudThumbnailLoader.maxFallbackDownloadBytes
        #expect(CloudThumbnailLoader.mayDownloadForThumbnail(cloudItem("a.jpg", size: limit)))
        #expect(!CloudThumbnailLoader.mayDownloadForThumbnail(cloudItem("a.jpg", size: limit + 1)))
        // A size the service didn't report could be anything.
        #expect(!CloudThumbnailLoader.mayDownloadForThumbnail(cloudItem("a.jpg", size: 0)))
    }

    @Test("a photo iCloud moved off this Mac is not brought back for a preview")
    func evictedStaysInTheCloud() {
        #expect(!CloudThumbnailLoader.mayDownloadForThumbnail(cloudItem("a.jpg", status: .evicted)))
    }

    @Test("offline and cancelled count as 'try again', other failures don't")
    func transientErrors() {
        #expect(CloudThumbnailLoader.isTransient(URLError(.notConnectedToInternet)))
        #expect(CloudThumbnailLoader.isTransient(URLError(.timedOut)))
        #expect(CloudThumbnailLoader.isTransient(CancellationError()))
        #expect(!CloudThumbnailLoader.isTransient(URLError(.badServerResponse)))
        #expect(!CloudThumbnailLoader.isTransient(CloudProviderError.notImplemented))
    }
}

/// Previews are scaled to the tile and stored small.
@Suite("Cloud thumbnails: scaling")
struct ThumbnailImageCodingTests {

    @Test("a large photo comes back no bigger than the tile")
    func scalesDown() throws {
        let data = imageData(width: 4000, height: 3000)
        let scaled = try #require(ThumbnailImageCoding.scaled(data, maxPixelSize: 288))
        let size = try #require(pixelSize(of: scaled))
        #expect(max(size.width, size.height) == 288)
        #expect(size.width > size.height)
        #expect(scaled.count < data.count)
    }

    @Test("a photo taken upright is shown upright")
    func appliesOrientation() throws {
        // EXIF orientation 6: stored landscape, meant to be seen portrait.
        let data = imageData(width: 400, height: 300, orientation: 6)
        let scaled = try #require(ThumbnailImageCoding.scaled(data, maxPixelSize: 200))
        let size = try #require(pixelSize(of: scaled))
        #expect(size.height > size.width)
    }

    @Test("bytes that aren't an image give nothing rather than a broken tile")
    func rejectsGarbage() {
        #expect(ThumbnailImageCoding.scaled(Data("<html>nope</html>".utf8), maxPixelSize: 200) == nil)
    }
}

/// Only a few previews are fetched at once, and a tile that scrolled away
/// gives up its place in the queue.
@Suite("Cloud thumbnails: fetch limit")
struct ThumbnailGateTests {

    @Test("no more than the limit run at once; a release lets the next one in")
    func limitsConcurrency() async {
        let gate = ThumbnailGate(limit: 2)
        #expect(await gate.acquire())
        #expect(await gate.acquire())

        let third = Task { await gate.acquire() }
        while await gate.waitingCount == 0 { await Task.yield() }
        #expect(await gate.runningCount == 2)

        await gate.release()
        #expect(await third.value)
        #expect(await gate.runningCount == 2)
        await gate.release()
        await gate.release()
        #expect(await gate.runningCount == 0)
    }

    @Test("a cancelled waiter leaves the queue without taking a slot")
    func cancelledWaiterLeaves() async {
        let gate = ThumbnailGate(limit: 1)
        #expect(await gate.acquire())

        let waiter = Task { await gate.acquire() }
        while await gate.waitingCount == 0 { await Task.yield() }
        waiter.cancel()
        #expect(await waiter.value == false)
        #expect(await gate.waitingCount == 0)

        await gate.release()
        #expect(await gate.runningCount == 0)
    }

    @Test("an already-cancelled task doesn't queue at all")
    func cancelledBeforeAsking() async {
        let gate = ThumbnailGate(limit: 1)
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return await gate.acquire()
        }
        #expect(await task.value == false)
        #expect(await gate.runningCount == 0)
    }
}

/// Previews seen once are kept on disk, one file per version of a file.
@Suite("Cloud thumbnails: disk cache")
struct CloudThumbnailDiskCacheTests {

    private func scratchDirectory() -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("CloudThumbnailTests-\(UUID().uuidString)", isDirectory: true)
    }

    @Test("same file, same name; another file, version or account, another name")
    func cacheFileIdentity() async throws {
        let loader = CloudThumbnailLoader(directory: scratchDirectory())
        let account = UUID()
        let item = cloudItem("a.jpg")
        let first = try #require(await loader.cacheFile(accountId: account, item: item, pixels: 288))
        #expect(await loader.cacheFile(accountId: account, item: item, pixels: 288) == first)

        let others: [URL?] = [
            await loader.cacheFile(accountId: account, item: cloudItem("b.jpg"), pixels: 288),
            await loader.cacheFile(accountId: account, item: cloudItem("a.jpg", size: 2), pixels: 288),
            await loader.cacheFile(accountId: account, item: cloudItem("a.jpg", modified: .now), pixels: 288),
            await loader.cacheFile(accountId: UUID(), item: item, pixels: 288),
            await loader.cacheFile(accountId: account, item: item, pixels: 192),
        ]
        for other in others {
            #expect(other != nil && other != first)
        }
    }

    @Test("nothing of the remote name reaches the file system")
    func hashedNames() async throws {
        let directory = scratchDirectory()
        let loader = CloudThumbnailLoader(directory: directory)
        let file = try #require(await loader.cacheFile(
            accountId: UUID(), item: cloudItem("../../evil.jpg", path: "/x/../../evil.jpg"), pixels: 288
        ))
        #expect(file.deletingLastPathComponent() == directory)
        #expect(!file.lastPathComponent.contains("evil"))
    }

    @Test("pruning drops the oldest previews first, down to three quarters of the limit")
    func prunesOldestFirst() async throws {
        let directory = scratchDirectory()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let chunk = Data(repeating: 7, count: 1000)
        var files: [URL] = []
        for index in 0..<10 {
            let file = directory.appendingPathComponent("\(index).thumb")
            try chunk.write(to: file)
            try FileManager.default.setAttributes(
                [.modificationDate: Date(timeIntervalSinceNow: Double(index - 100))],
                ofItemAtPath: file.path
            )
            files.append(file)
        }
        let loader = CloudThumbnailLoader(directory: directory)
        await loader.prune(directory, keepingAtMost: 8000)

        let remaining = files.filter { FileManager.default.fileExists(atPath: $0.path) }
        #expect(remaining.count == 6)
        #expect(remaining == Array(files.suffix(6)))
    }
}

/// The memory cache treats cloud previews like local ones, except that a
/// failure caused by the network is not remembered.
@Suite("Cloud thumbnails: memory cache")
@MainActor
struct CloudThumbnailMemoryCacheTests {

    private final class Counter: @unchecked Sendable {
        private let lock = NSLock()
        private var value = 0
        func next() -> Int { lock.withLock { value += 1; return value } }
        var count: Int { lock.withLock { value } }
    }

    private let date = Date(timeIntervalSinceReferenceDate: 700_000_000)

    @Test("a preview is made once and then served from memory")
    func servesFromMemory() async {
        let identity = "cloud:test-\(UUID().uuidString)/a.jpg"
        let calls = Counter()
        let data = imageData(width: 50, height: 40)
        for _ in 0..<2 {
            let image = await ThumbnailCache.shared.thumbnail(identity: identity, mtime: date, size: 1, pointSize: 96) { _ in
                _ = calls.next()
                return .data(data)
            }
            #expect(image != nil)
        }
        #expect(calls.count == 1)
        #expect(ThumbnailCache.shared.cached(identity: identity, mtime: date, size: 1, pointSize: 96) != nil)
    }

    @Test("a file without a preview is asked once; one that failed offline is asked again")
    func remembersOnlyRealRefusals() async {
        let refused = "cloud:test-\(UUID().uuidString)/none.jpg"
        let offline = "cloud:test-\(UUID().uuidString)/offline.jpg"
        let refusedCalls = Counter()
        let offlineCalls = Counter()
        for _ in 0..<2 {
            _ = await ThumbnailCache.shared.thumbnail(identity: refused, mtime: date, size: 1, pointSize: 96) { _ in
                _ = refusedCalls.next()
                return .none
            }
            _ = await ThumbnailCache.shared.thumbnail(identity: offline, mtime: date, size: 1, pointSize: 96) { _ in
                _ = offlineCalls.next()
                return .unavailable
            }
        }
        #expect(refusedCalls.count == 1)
        #expect(offlineCalls.count == 2)
    }

    @Test("the source is asked for the tile's size in pixels")
    func passesPixelSize() async {
        let identity = "cloud:test-\(UUID().uuidString)/size.jpg"
        let asked = Counter()
        let expected = ThumbnailCache.pixels(for: 144)
        _ = await ThumbnailCache.shared.thumbnail(identity: identity, mtime: date, size: 1, pointSize: 144) { requested in
            // Records the request in the counter only when the size is right.
            if requested == expected { _ = asked.next() }
            return .data(imageData(width: requested, height: requested))
        }
        #expect(asked.count == 1)
        #expect(expected == Int((144 * ThumbnailCache.scale).rounded()))
    }
}
