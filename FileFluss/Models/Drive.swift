import Foundation

/// Represents an external drive or network mount. Persisted across launches
/// so a drive that has been indexed but is currently unmounted still appears
/// in the sidebar (greyed out) and remains searchable.
struct Drive: Identifiable, Hashable, Codable {
    enum Kind: String, Codable, Hashable {
        case external   // USB / Thunderbolt / SD card — removable local volumes
        case network    // SMB / AFP / NFS / WebDAV mounts

        var displayName: String {
            switch self {
            case .external: return "External Drive"
            case .network: return "Network Drive"
            }
        }

        var sfSymbol: String {
            switch self {
            case .external: return "externaldrive.fill"
            case .network: return "server.rack"
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
