import Foundation

/// Represents an external drive or network mount. Persisted across launches
/// so a drive that has been indexed but is currently unmounted still appears
/// in the sidebar (greyed out) and remains searchable.
struct Drive: Identifiable, Hashable, Codable {
    enum Kind: String, Codable, Hashable {
        case external   // USB / Thunderbolt / SD card — removable local volumes
        case network    // SMB / AFP / NFS / WebDAV mounts
        /// A second partition or APFS volume on a built-in disk. Reported by
        /// a user who had one and no way to reach it: the Drives section was
        /// written as "external and network", so every internal volume that
        /// wasn't the startup disk fell through and was shown nowhere.
        case internalVolume

        var displayName: String {
            switch self {
            case .external: return "External Drive"
            case .network: return "Network Drive"
            case .internalVolume: return "Internal Volume"
            }
        }

        var sfSymbol: String {
            switch self {
            case .external: return "externaldrive.fill"
            case .network: return "server.rack"
            // Distinct from the external drive's icon: the user needs to
            // see at a glance that this one lives inside the Mac.
            case .internalVolume: return "internaldrive.fill"
            }
        }
    }

    /// Stable identifier across mount/unmount. Volume UUID for external
    /// drives that report one; otherwise a hash of the mount URL.
    let id: String

    var displayName: String
    let kind: Kind

    /// Last known mount path. Set when the drive is online; we keep the
    /// last seen value when offline so we can still show "was at /Volumes/Foo".
    var lastMountPath: String?

    /// Last time the user successfully indexed this drive. nil = never.
    var lastIndexed: Date?

    /// File and byte counts from the most recent index run.
    var totalFiles: Int = 0
    var totalBytes: Int64 = 0
}
