import Testing
import Foundation
@testable import FileFluss

/// The sidebar's drive tooltips read capacities from a cache that is filled
/// in the background, so a sidebar redraw never waits on a volume.
@Suite("Drive capacity cache")
@MainActor
struct DriveCapacityCacheTests {

    /// Counts reads and can hold them until released, from any thread.
    final class Reader: @unchecked Sendable {
        private let lock = NSLock()
        private var _calls = 0
        private let gate: DispatchSemaphore?

        init(holding: Bool = false) {
            gate = holding ? DispatchSemaphore(value: 0) : nil
        }

        var calls: Int { lock.withLock { _calls } }

        func release() { gate?.signal() }

        func read(_ url: URL) -> DriveCapacityCache.Capacity? {
            lock.withLock { _calls += 1 }
            _ = gate?.wait(timeout: .now() + 5)
            // A different, recognisable answer per mount.
            let total = Int64(url.lastPathComponent.count) * 1_000
            return .init(free: total / 2, total: total)
        }
    }

    @Test("a refresh reads each mounted drive in the background and caches it")
    func fillsCache() async {
        let reader = Reader()
        let cache = DriveCapacityCache(refreshInterval: nil, read: { reader.read($0) })
        #expect(cache.capacity(for: "a") == nil)

        await cache.refresh(mounts: [
            "a": URL(fileURLWithPath: "/Volumes/AAAA"),
            "b": URL(fileURLWithPath: "/Volumes/BB")
        ]).value

        #expect(cache.capacity(for: "a") == .init(free: 2_000, total: 4_000))
        #expect(cache.capacity(for: "b") == .init(free: 1_000, total: 2_000))
        #expect(reader.calls == 2)
    }

    @Test("a drive that is no longer mounted loses its entry at once")
    func dropsUnmounted() async {
        let reader = Reader()
        let cache = DriveCapacityCache(refreshInterval: nil, read: { reader.read($0) })
        await cache.refresh(mounts: [
            "a": URL(fileURLWithPath: "/Volumes/AAAA"),
            "b": URL(fileURLWithPath: "/Volumes/BB")
        ]).value

        let task = cache.refresh(mounts: ["a": URL(fileURLWithPath: "/Volumes/AAAA")])
        // Synchronously, before any read has come back.
        #expect(cache.capacity(for: "b") == nil)
        await task.value
        #expect(cache.capacity(for: "a") != nil)
    }

    @Test("an answer for a mount that has since moved is thrown away")
    func discardsStaleMount() async {
        let reader = Reader()
        let cache = DriveCapacityCache(refreshInterval: nil, read: { reader.read($0) })
        let first = cache.refresh(mounts: ["a": URL(fileURLWithPath: "/Volumes/AAAA")])
        let second = cache.refresh(mounts: ["a": URL(fileURLWithPath: "/Volumes/AAAA 1")])
        await first.value
        await second.value
        // Whichever read finished last, the cache describes the current mount.
        #expect(cache.capacity(for: "a") == .init(free: 3_000, total: 6_000))
    }

    @Test("a slow drive is not asked again while its first read is pending")
    func noPileUpOnSlowDrive() async {
        let reader = Reader(holding: true)
        let cache = DriveCapacityCache(refreshInterval: nil, read: { reader.read($0) })
        let mounts = ["a": URL(fileURLWithPath: "/Volumes/AAAA")]
        let first = cache.refresh(mounts: mounts)
        let second = cache.refresh(mounts: mounts)
        let third = cache.refreshAll()
        await second.value
        await third.value
        reader.release()
        await first.value
        #expect(reader.calls == 1)
        #expect(cache.capacity(for: "a") != nil)
    }

    @Test("the real reader reports the capacity of a mounted volume")
    func realReader() async {
        let cache = DriveCapacityCache(refreshInterval: nil)
        await cache.refresh(mounts: ["tmp": FileManager.default.temporaryDirectory]).value
        let capacity = cache.capacity(for: "tmp")
        #expect(capacity != nil)
        if let capacity { #expect(capacity.total >= capacity.free) }
    }

    @Test("the volume scan runs off the main thread and never lists the startup volume")
    func scanOffMain() async {
        let volumes = await Task.detached { DriveMonitor.scanMountedVolumes() }.value
        #expect(!volumes.contains { $0.url.path == "/" })
    }
}
