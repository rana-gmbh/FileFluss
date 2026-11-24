import Foundation

/// Free and total space of each mounted drive, for the sidebar's tooltips.
///
/// The tooltip is built on every sidebar body evaluation, and asking a
/// volume for its capacity is a round trip to the server behind a network
/// drive. Reading it there stalled every redraw and froze the app on a share
/// whose server had gone. The tooltip now reads only this cache, which is
/// filled on a background thread when the mounted volumes change, when a
/// transfer finishes, and on a slow timer.
@MainActor @Observable
final class DriveCapacityCache {
    static let shared = DriveCapacityCache(refreshInterval: .seconds(60))

    struct Capacity: Sendable, Equatable {
        let free: Int64
        let total: Int64
    }

    private(set) var capacities: [String: Capacity] = [:]

    /// drive.id → mount URL, as last handed over by `DriveMonitor`.
    @ObservationIgnored private var mounts: [String: URL] = [:]
    /// Mounts whose capacity is being read. A drive behind a dead server is
    /// never asked again until its first answer is in, so the slow timer
    /// can't pile up blocked threads on it.
    @ObservationIgnored private var inFlight: Set<URL> = []
    @ObservationIgnored private var timerTask: Task<Void, Never>?
    @ObservationIgnored private let refreshInterval: Duration?
    @ObservationIgnored private let read: @Sendable (URL) -> Capacity?

    init(
        refreshInterval: Duration?,
        read: @escaping @Sendable (URL) -> Capacity? = { url in
            SpaceCheck.localVolumeCapacity(at: url).map { Capacity(free: $0.free, total: $0.total) }
        }
    ) {
        self.refreshInterval = refreshInterval
        self.read = read
    }

    func capacity(for driveId: String) -> Capacity? {
        capacities[driveId]
    }

    /// Takes the drives that are mounted now and reads each one's capacity
    /// in the background. Entries for drives no longer mounted are dropped
    /// at once. The returned task finishes when every read started here has
    /// been stored; callers other than tests ignore it.
    @discardableResult
    func refresh(mounts newMounts: [String: URL]) -> Task<Void, Never> {
        mounts = newMounts
        let kept = capacities.filter { newMounts[$0.key] != nil }
        if kept != capacities { capacities = kept }
        startTimerIfNeeded()

        let toRead = newMounts.filter { !inFlight.contains($0.value) }
        inFlight.formUnion(toRead.values)
        let read = self.read
        let cache = self
        return Task.detached(priority: .utility) {
            await withTaskGroup(of: Void.self) { group in
                for (driveId, url) in toRead {
                    group.addTask {
                        let value = read(url)
                        await cache.store(value, driveId: driveId, url: url)
                    }
                }
            }
        }
    }

    /// Reads every mounted drive again; used after a transfer has changed
    /// how much space is free.
    @discardableResult
    func refreshAll() -> Task<Void, Never> {
        refresh(mounts: mounts)
    }

    private func store(_ value: Capacity?, driveId: String, url: URL) {
        inFlight.remove(url)
        // The drive was unmounted, or remounted somewhere else, while it
        // was being read: the answer belongs to a mount that is gone.
        guard mounts[driveId] == url else { return }
        if capacities[driveId] != value { capacities[driveId] = value }
    }

    private func startTimerIfNeeded() {
        guard timerTask == nil, let interval = refreshInterval else { return }
        // The shared cache lives as long as the app, so a strong capture
        // costs nothing and keeps a weak reference out of a sendable closure.
        let cache = self
        timerTask = Task {
            while !Task.isCancelled {
                try? await Task.sleep(for: interval)
                if !cache.mounts.isEmpty { cache.refreshAll() }
            }
        }
    }
}
