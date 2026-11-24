import AppKit
import Foundation

/// Tracks external and network drives. Combines two sources of truth:
/// - **Live state**: the volumes currently mounted, observed via
///   `FileManager.mountedVolumeURLs` + `NSWorkspace` mount/unmount
///   notifications.
/// - **Persisted state**: drives the user has indexed, kept in UserDefaults
///   so an unmounted-but-indexed drive still shows up offline in the
///   sidebar and remains searchable.
///
/// The `drives` array merges both: a drive that's currently mounted appears
/// with a fresh mount path; a drive that's known-but-offline appears with
/// `lastMountPath` preserved and `isOnline = false` derived at read time.
///
/// Mounted volumes are enumerated and classified on a background thread and
/// the result is published to the main actor. Asking a volume for its
/// resource values can wait on the server behind it, and a dead SMB/NFS/AFP
/// share or an orphaned WebDAV mount used to hang the app's launch, or the
/// plugging-in of a USB stick, for as long as the server took to time out.
@MainActor
@Observable
final class DriveMonitor {
    static let shared = DriveMonitor()

    /// All known drives — currently mounted plus previously-indexed-but-now-offline.
    private(set) var drives: [Drive] = []

    /// Maps drive.id → its current mount URL when online. Absent ⇒ offline.
    private(set) var liveMountURLs: [String: URL] = [:]

    private static let driveStoreKey = "drives.known"

    /// Mount points to *exclude* from the drives list. The boot volume,
    /// VM/disk image overlays, and Time Machine snapshots are not useful
    /// to expose as user-managed drives.
    ///
    /// `/System/Volumes/` covers the parts of the startup disk macOS mounts
    /// for itself — the Data volume, Preboot, Recovery, VM — which is what
    /// keeps internal volumes safe to show now that they are shown at all.
    nonisolated private static let excludedPrefixes: [String] = [
        "/System/Volumes/",
        "/private/var/folders/"
    ]

    /// Mounts that are internal volumes by every API measure but aren't a
    /// place to put files: Time Machine's local snapshots, which macOS
    /// mounts under /Volumes with a generated name.
    nonisolated private static let excludedNamePrefixes: [String] = [
        "com.apple.TimeMachine",
        ".timemachine"
    ]

    private init() {
        loadFromDefaults()
        // On a fresh launch, drop any persisted entry that was never
        // indexed. Avoids carrying yesterday's one-off DMG into today's
        // sidebar. Nothing is known to be mounted yet, so this drops every
        // unindexed entry; the ones that are mounted come straight back
        // when the first scan, started below, is published.
        pruneUnindexedOfflineDrives()
        refreshMountedVolumes()

        let nc = NSWorkspace.shared.notificationCenter
        nc.addObserver(
            forName: NSWorkspace.didMountNotification,
            object: nil,
            queue: .main
        ) { [weak self] note in
            // Extract the URL synchronously on the notification thread —
            // Notification isn't Sendable, but `URL` is, so we marshal the
            // value across before hopping to the actor.
            let mountedURL = note.userInfo?[NSWorkspace.volumeURLUserInfoKey] as? URL
            MainActor.assumeIsolated {
                self?.handleMount(url: mountedURL)
            }
        }
        nc.addObserver(
            forName: NSWorkspace.didUnmountNotification,
            object: nil,
            queue: .main
        ) { [weak self] note in
            let unmountedURL = note.userInfo?[NSWorkspace.volumeURLUserInfoKey] as? URL
            MainActor.assumeIsolated {
                self?.handleUnmount(url: unmountedURL)
            }
        }
    }

    private func handleMount(url: URL?) {
        // A fresh enumeration is enough — the new volume will be picked
        // up by classify().
        refreshMountedVolumes()
    }

    /// Optimistic unmount handling: directly drop the entry pointing at the
    /// disappearing URL so the UI flips to offline immediately, then run a
    /// full refresh shortly after. Without the direct removal we relied on
    /// `mountedVolumeURLs()` already excluding the just-unmounted drive,
    /// which isn't always true — the system can return the stale URL for
    /// a moment after the notification fires, so the next `liveMountURLs =
    /// nextLive` assignment re-adds it and the row keeps showing online.
    private func handleUnmount(url: URL?) {
        if let url {
            suppressedURLPaths.insert(url.path)
            if let staleId = liveMountURLs.first(where: { $0.value.path == url.path })?.key {
                liveMountURLs.removeValue(forKey: staleId)
            }
        }
        refreshMountedVolumes()
        // Belt-and-suspenders: schedule another refresh + clear the
        // suppression after the kernel catches up. Cheap, and guarantees
        // we end in a consistent state even if the first refresh saw
        // stale data.
        let suppressed = url?.path
        Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(800))
            if let suppressed { self.suppressedURLPaths.remove(suppressed) }
            self.refreshMountedVolumes()
        }
    }

    // MARK: - Public

    /// True if the drive is currently mounted and accessible.
    func isOnline(_ drive: Drive) -> Bool {
        liveMountURLs[drive.id] != nil
    }

    /// Returns the live mount URL for a drive, if mounted.
    func mountURL(for driveId: String) -> URL? {
        liveMountURLs[driveId]
    }

    /// Persist updated metadata after an indexing run. Inserts if missing.
    func upsert(_ drive: Drive) {
        if let idx = drives.firstIndex(where: { $0.id == drive.id }) {
            drives[idx] = drive
        } else {
            drives.append(drive)
        }
        saveToDefaults()
    }

    /// Remove a drive from the persisted list. Use when the user wants to
    /// stop tracking a drive entirely (clears its index in the caller).
    func forget(driveId: String) {
        drives.removeAll { $0.id == driveId }
        saveToDefaults()
    }

    // MARK: - Mount handling

    /// Volume URLs we've optimistically removed from `liveMountURLs` after
    /// an unmount notification. The next `refreshMountedVolumes()` call
    /// must NOT re-add these even if `mountedVolumeURLs()` is still
    /// returning them — that's the kernel-lag race the bug exposes.
    /// Entries clear themselves after a short delay, so a genuinely
    /// re-mounted drive can come back online.
    private var suppressedURLPaths: Set<String> = []

    /// Bumped by every scan that starts. A scan's result is published only
    /// if it is newer than the last one published, so a scan that was held
    /// up by a slow volume can never overwrite what a later one found.
    @ObservationIgnored private var scanGeneration = 0
    @ObservationIgnored private var publishedGeneration = 0

    /// Starts a background scan of the mounted volumes; the result is
    /// merged into `drives` and `liveMountURLs` on the main actor when it
    /// arrives. Returns at once, so nothing here waits on a volume.
    private func refreshMountedVolumes() {
        scanGeneration += 1
        let generation = scanGeneration
        // The monitor is a process-wide singleton, so a strong capture
        // costs nothing and keeps a weak reference out of a sendable closure.
        let monitor = self
        Task.detached(priority: .userInitiated) {
            let volumes = Self.scanMountedVolumes()
            await monitor.publish(volumes, generation: generation)
        }
    }

    /// A volume the scan decided to show, with the URL it is mounted at.
    struct MountedVolume: Sendable {
        let url: URL
        let drive: Drive
    }

    nonisolated private static let volumeKeys: [URLResourceKey] = [
        .volumeIsRemovableKey,
        .volumeIsLocalKey,
        .volumeIsInternalKey,
        .volumeIsBrowsableKey,
        .volumeIsRootFileSystemKey,
        .volumeNameKey,
        .volumeUUIDStringKey,
        .volumeURLKey
    ]

    /// Enumerates and classifies the mounted volumes. Blocking file-system
    /// work, so it must never run on the main thread.
    nonisolated static func scanMountedVolumes() -> [MountedVolume] {
        let keys = volumeKeys
        let mounted = FileManager.default.mountedVolumeURLs(
            includingResourceValuesForKeys: keys,
            options: [.skipHiddenVolumes]
        ) ?? []
        return mounted.compactMap { url in
            classify(url, keys: keys).map { MountedVolume(url: url, drive: $0) }
        }
    }

    /// Merges a finished scan into the published state.
    private func publish(_ volumes: [MountedVolume], generation: Int) {
        guard generation > publishedGeneration else { return }
        publishedGeneration = generation

        var nextLive: [String: URL] = [:]
        for volume in volumes {
            let url = volume.url
            if suppressedURLPaths.contains(url.path) {
                // The kernel still reports this URL even though we just
                // saw the unmount notification. Don't treat it as online.
                continue
            }
            let drive = volume.drive
            nextLive[drive.id] = url

            // Merge into persisted list: keep lastIndexed etc. if present.
            if let idx = drives.firstIndex(where: { $0.id == drive.id }) {
                drives[idx].displayName = drive.displayName
                drives[idx].lastMountPath = url.path
            } else {
                var d = drive
                d.lastMountPath = url.path
                drives.append(d)
            }
        }
        liveMountURLs = nextLive
        // Every transition can leave behind a never-indexed entry whose
        // volume just unmounted. Prune those so the sidebar stays tight.
        pruneUnindexedOfflineDrives()
        saveToDefaults()
        DriveCapacityCache.shared.refresh(mounts: liveMountURLs)
    }

    /// Drop every drive that (a) isn't currently mounted and (b) has
    /// never been indexed by the user. Indexed-but-offline drives stay,
    /// because their cached index is still searchable.
    private func pruneUnindexedOfflineDrives() {
        let before = drives.count
        drives.removeAll { drive in
            drive.lastIndexed == nil && liveMountURLs[drive.id] == nil
        }
        if drives.count != before { saveToDefaults() }
    }

    /// Inspects a mounted volume URL and returns a `Drive` if it's one we
    /// want to expose: an external drive, a network mount, or another
    /// volume on a built-in disk. Returns nil for the startup volume, the
    /// system's own mounts, and Time Machine snapshots.
    /// What a mounted volume is, from the facts the file system reports
    /// about it. Nil means "don't show this".
    ///
    /// Pure and `static` so it can be tested: this is the rule that decided
    /// a user's second internal partition didn't exist, and it was
    /// previously reachable only by plugging real disks into a real Mac.
    nonisolated static func kind(
        mountPath: String,
        name: String,
        isBrowsable: Bool,
        isRootFileSystem: Bool,
        isLocal: Bool,
        isInternal: Bool
    ) -> Drive.Kind? {
        if excludedPrefixes.contains(where: { mountPath.hasPrefix($0) }) { return nil }
        // The startup volume, by either measure.
        if mountPath == "/" || isRootFileSystem { return nil }
        guard isBrowsable else { return nil }
        // Time Machine mounts its local snapshots under /Volumes with a
        // generated name, and they look like ordinary internal volumes.
        if excludedNamePrefixes.contains(where: { name.hasPrefix($0) }) { return nil }

        // - Non-local volume → network mount (SMB / AFP / NFS / WebDAV).
        // - Local + non-internal → external drive. We deliberately don't
        //   gate on `volumeIsRemovable` because many Thunderbolt/USB-C
        //   SSDs report `isRemovable == false` even though they are
        //   physically external.
        // - Local + internal → another volume on a built-in disk: a second
        //   partition, or a second APFS volume in the same container. The
        //   startup volume never gets this far, and the system's own
        //   volumes are excluded by mount path, so what is left is a place
        //   the user keeps their own files — and until now had no way to
        //   open from the sidebar.
        if !isLocal { return .network }
        return isInternal ? .internalVolume : .external
    }

    nonisolated private static func classify(_ url: URL, keys: [URLResourceKey]) -> Drive? {
        guard let values = try? url.resourceValues(forKeys: Set(keys)) else { return nil }

        let name = values.volumeName ?? url.lastPathComponent
        guard let kind = Self.kind(
            mountPath: url.path,
            name: name,
            isBrowsable: values.volumeIsBrowsable ?? true,
            isRootFileSystem: values.volumeIsRootFileSystem ?? false,
            isLocal: values.volumeIsLocal ?? true,
            isInternal: values.volumeIsInternal ?? false
        ) else { return nil }

        let uuid = values.volumeUUIDString
        let id: String
        switch kind {
        case .external, .internalVolume:
            // Prefer volume UUID; fall back to a stable derivation of the name
            // for filesystems that don't expose one (e.g. exFAT on some setups).
            id = uuid.map { "vol:\($0)" } ?? "vol-name:\(name)"
        case .network:
            // Network volumes rarely have stable UUIDs; key on the mount URL
            // last component (the share name) plus host if we can read it.
            id = "net:\(url.path)"
        }
        return Drive(
            id: id,
            displayName: name,
            kind: kind,
            lastMountPath: url.path
        )
    }

    // MARK: - Persistence

    private func loadFromDefaults() {
        guard let data = UserDefaults.standard.data(forKey: Self.driveStoreKey),
              let decoded = try? JSONDecoder().decode([Drive].self, from: data) else {
            return
        }
        drives = decoded
    }

    private func saveToDefaults() {
        guard let data = try? JSONEncoder().encode(drives) else { return }
        UserDefaults.standard.set(data, forKey: Self.driveStoreKey)
    }
}
