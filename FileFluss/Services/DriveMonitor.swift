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
    private static let excludedPrefixes: [String] = [
        "/System/Volumes/",
        "/private/var/folders/"
    ]

    private init() {
        loadFromDefaults()
        refreshMountedVolumes()

        let nc = NSWorkspace.shared.notificationCenter
        nc.addObserver(
            forName: NSWorkspace.didMountNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.refreshMountedVolumes()
            }
        }
        nc.addObserver(
            forName: NSWorkspace.didUnmountNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.refreshMountedVolumes()
            }
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

    private func refreshMountedVolumes() {
        let keys: [URLResourceKey] = [
            .volumeIsRemovableKey,
            .volumeIsLocalKey,
            .volumeIsInternalKey,
            .volumeIsBrowsableKey,
            .volumeIsRootFileSystemKey,
            .volumeNameKey,
            .volumeUUIDStringKey,
            .volumeURLKey
        ]
        let mounted = FileManager.default.mountedVolumeURLs(
            includingResourceValuesForKeys: keys,
            options: [.skipHiddenVolumes]
        ) ?? []

        var nextLive: [String: URL] = [:]
        for url in mounted {
            guard let drive = classify(url, keys: keys) else { continue }
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
        saveToDefaults()
    }

    /// Inspects a mounted volume URL and returns a `Drive` if it's one we
    /// want to expose (external local, or network). Returns nil for the
    /// boot volume, internal disks, and excluded system mounts.
    private func classify(_ url: URL, keys: [URLResourceKey]) -> Drive? {
        let path = url.path
        if Self.excludedPrefixes.contains(where: { path.hasPrefix($0) }) {
            return nil
        }
        if path == "/" { return nil }

        guard let values = try? url.resourceValues(forKeys: Set(keys)) else { return nil }

        let isBrowsable = values.volumeIsBrowsable ?? true
        guard isBrowsable else { return nil }

        let isRootFS = values.volumeIsRootFileSystem ?? false
        if isRootFS { return nil }

        let isLocal = values.volumeIsLocal ?? true
        let isInternal = values.volumeIsInternal ?? false
        let name = values.volumeName ?? url.lastPathComponent

        // Classify:
        // - Non-local volume → network mount (SMB / AFP / NFS / WebDAV).
        // - Local + non-internal → external drive. We deliberately don't
        //   gate on `volumeIsRemovable` because many Thunderbolt/USB-C
        //   SSDs report `isRemovable == false` even though they are
        //   physically external.
        let kind: Drive.Kind
        if !isLocal {
            kind = .network
        } else if !isInternal {
            kind = .external
        } else {
            return nil
        }

        let uuid = values.volumeUUIDString
        let id: String
        switch kind {
        case .external:
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
