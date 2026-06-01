import Foundation

public enum CloudProviderType: String, Codable, CaseIterable, Identifiable, Sendable {
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
    case ftp
    case wordpress
    case gmxCloud
    case s3
    case synologyDrive
    case synologyC2
    case s3Compatible
    case box
    case seafile
    case filen
    case internxt
    case terabox
    case jottacloud
    case googleDrivePicker

    public var id: String { rawValue }

    public var displayName: String {
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
        case .ftp: return "FTP"
        case .wordpress: return "WordPress"
        case .gmxCloud: return "GMX Cloud"
        case .s3: return "AWS S3"
        case .synologyDrive: return "Synology Drive"
        case .synologyC2: return "Synology C2 Storage"
        case .s3Compatible: return "S3-Compatible Storage"
        case .box: return "Box"
        case .seafile: return "Seafile"
        case .filen: return "Filen"
        case .internxt: return "Internxt"
        case .terabox: return "TeraBox"
        case .jottacloud: return "Jottacloud"
        case .googleDrivePicker: return "Google Drive (Selected Folders)"
        }
    }

    public var icon: String {
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
        case .ftp: return "arrow.up.arrow.down.circle"
        case .wordpress: return "w.square"
        case .gmxCloud: return "envelope.badge.shield.half.filled"
        case .s3: return "server.rack"
        case .synologyDrive: return "externaldrive.fill"
        case .synologyC2: return "externaldrive.badge.timemachine"
        case .s3Compatible: return "externaldrive.connected.to.line.below.fill"
        case .box: return "shippingbox.fill"
        case .seafile: return "leaf.fill"
        case .filen: return "lock.shield"
        case .internxt: return "lock.icloud.fill"
        case .terabox: return "shippingbox.and.arrow.backward.fill"
        case .jottacloud: return "cloud.fill"
        case .googleDrivePicker: return "externaldrive.connected.to.line.below"
        }
    }

    /// Asset catalog name for providers with official logos; nil falls back to SF Symbol `icon`.
    public var logoAssetName: String? {
        switch self {
        case .pCloud: return "pCloudLogo"
        case .kDrive: return "kDriveLogo"
        case .oneDrive: return "OneDriveLogo"
        case .googleDrive: return "GoogleDriveLogo"
        case .koofr: return "KoofrLogo"
        case .iCloud: return "iCloudLogo"
        case .nextCloud: return "NextCloudLogo"
        case .dropbox: return "DropboxLogo"
        case .mega: return "MegaLogo"
        case .webDAV: return "WebDAVLogo"
        case .sftp: return "SFTPLogo"
        case .ftp: return "FTPLogo"
        case .wordpress: return "WordPressLogo"
        case .gmxCloud: return "GMXCloudLogo"
        case .s3: return "AmazonS3Logo"
        case .synologyDrive: return "SynologyDriveLogo"
        case .synologyC2: return "SynologyC2Logo"
        case .s3Compatible: return "S3CompatibleLogo"
        case .box: return "BoxLogo"
        case .seafile: return "SeafileLogo"
        case .filen: return "FilenLogo"
        case .internxt: return "InternxtLogo"
        case .terabox: return "TeraBoxLogo"
        case .jottacloud: return "JottacloudLogo"
        // Shares the Google Drive logo — it's the same service, different access model.
        case .googleDrivePicker: return "GoogleDriveLogo"
        }
    }
}

public struct CloudAccount: Identifiable, Hashable, Codable, Sendable {
    public let id: UUID
    public let providerType: CloudProviderType
    public var displayName: String
    public var isConnected: Bool
    public var rootPath: String
    public var lastSyncDate: Date?
    /// User-forced offline mode. When true, the panel reads from the local
    /// offline index (`cloud_files`) instead of talking to the provider —
    /// the user has explicitly said "treat this account as offline" even if
    /// the connection / credentials would otherwise work. Distinct from
    /// `isConnected`, which reflects whether the provider is currently
    /// registered in `SyncEngine`. An account in offline mode is treated
    /// the same as a disconnected one by the UI (offline source view,
    /// included in offline search, etc.).
    public var isOfflineMode: Bool

    private enum CodingKeys: String, CodingKey {
        case id, providerType, displayName, isConnected, rootPath, lastSyncDate, isOfflineMode
    }

    public init(
        id: UUID = UUID(),
        providerType: CloudProviderType,
        displayName: String? = nil,
        isConnected: Bool = false,
        rootPath: String = "/",
        lastSyncDate: Date? = nil,
        isOfflineMode: Bool = false
    ) {
        self.id = id
        self.providerType = providerType
        self.displayName = displayName ?? providerType.displayName
        self.isConnected = isConnected
        self.rootPath = rootPath
        self.lastSyncDate = lastSyncDate
        self.isOfflineMode = isOfflineMode
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(UUID.self, forKey: .id)
        providerType = try c.decode(CloudProviderType.self, forKey: .providerType)
        displayName = try c.decode(String.self, forKey: .displayName)
        isConnected = try c.decode(Bool.self, forKey: .isConnected)
        rootPath = try c.decode(String.self, forKey: .rootPath)
        lastSyncDate = try c.decodeIfPresent(Date.self, forKey: .lastSyncDate)
        // Default `false` when the key is missing — accounts persisted by
        // builds prior to this feature still decode cleanly.
        isOfflineMode = (try? c.decode(Bool.self, forKey: .isOfflineMode)) ?? false
    }
}
