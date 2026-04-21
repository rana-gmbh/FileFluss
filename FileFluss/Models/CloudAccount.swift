import Foundation

enum CloudProviderType: String, Codable, CaseIterable, Identifiable {
    case pCloud
    case kDrive
    case oneDrive
    case googleDrive
    case nextCloud
    case iCloud
    case koofr
    case dropbox
    case mega
    case webDAV
    case sftp
    case wordpress
    case hiDrive
    case gmxCloud

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .pCloud: return "pCloud"
        case .kDrive: return "kDrive"
        case .oneDrive: return "OneDrive"
        case .googleDrive: return "Google Drive"
        case .nextCloud: return "NextCloud"
        case .iCloud: return "iCloud"
        case .koofr: return "Koofr"
        case .dropbox: return "Dropbox"
        case .mega: return "Mega"
        case .webDAV: return "WebDAV"
        case .sftp: return "SFTP"
        case .wordpress: return "WordPress"
        case .hiDrive: return "HiDrive"
        case .gmxCloud: return "GMX Cloud"
        }
    }

    var icon: String {
        switch self {
        case .pCloud: return "cloud"
        case .kDrive: return "externaldrive.badge.icloud"
        case .oneDrive: return "cloud.fill"
        case .googleDrive: return "externaldrive.connected.to.line.below"
        case .nextCloud: return "cloud.circle"
        case .iCloud: return "icloud"
        case .koofr: return "cloud.bolt"
        case .dropbox: return "drop"
        case .mega: return "cloud.bolt.fill"
        case .webDAV: return "externaldrive.badge.wifi"
        case .sftp: return "terminal"
        case .wordpress: return "w.square"
        case .hiDrive: return "externaldrive.fill.badge.person.crop"
        case .gmxCloud: return "envelope.badge.shield.half.filled"
        }
    }

    /// Asset catalog name for providers with official logos; nil falls back to SF Symbol `icon`.
    var logoAssetName: String? {
        switch self {
        case .pCloud: return "pCloudLogo"
        case .kDrive: return "kDriveLogo"
        case .oneDrive: return "OneDriveLogo"
        case .googleDrive: return "GoogleDriveLogo"
        case .koofr: return "KoofrLogo"
        case .nextCloud: return "NextCloudLogo"
        case .dropbox: return "DropboxLogo"
        case .mega: return "MegaLogo"
        case .webDAV: return "WebDAVLogo"
        case .sftp: return "SFTPLogo"
        case .wordpress: return "WordPressLogo"
        case .hiDrive: return "HiDriveLogo"
        case .gmxCloud: return "GMXCloudLogo"
        default: return nil
        }
    }
}

struct CloudAccount: Identifiable, Hashable, Codable {
    let id: UUID
    let providerType: CloudProviderType
    var displayName: String
    var isConnected: Bool
    var rootPath: String
    var lastSyncDate: Date?

    init(
        id: UUID = UUID(),
        providerType: CloudProviderType,
        displayName: String? = nil,
        isConnected: Bool = false,
        rootPath: String = "/",
        lastSyncDate: Date? = nil
    ) {
        self.id = id
        self.providerType = providerType
        self.displayName = displayName ?? providerType.displayName
        self.isConnected = isConnected
        self.rootPath = rootPath
        self.lastSyncDate = lastSyncDate
    }
}
