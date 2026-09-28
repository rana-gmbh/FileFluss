import Foundation

public actor SyncEngine {
    public static let shared = SyncEngine()

    private var providers: [UUID: any CloudProvider] = [:]

    public func registerProvider(for accountId: UUID, provider: any CloudProvider) {
        providers[accountId] = provider
    }

    public func removeProvider(for accountId: UUID) {
        providers.removeValue(forKey: accountId)
    }

    public func provider(for accountId: UUID) -> (any CloudProvider)? {
        providers[accountId]
    }

    public func createProvider(for type: CloudProviderType) -> any CloudProvider {
        switch type {
        case .pCloud: return PCloudProvider()
        case .kDrive: return KDriveProvider()
        case .oneDrive: return OneDriveProvider()
        case .googleDrive: return GoogleDriveProvider()
        case .nextCloud: return NextCloudProvider()
        case .koofr: return KoofrProvider()
        case .dropbox: return DropboxProvider()
        case .mega: return MegaProvider()
        case .webDAV: return WebDAVProvider()
        case .wordpress: return WordPressProvider()
        #if os(macOS)
        // ICloudProvider relies on FileManager.homeDirectoryForCurrentUser
        // and the user-visible CloudDocs folder, neither of which exist on
        // iOS — Files-app picker handles iCloud Drive there instead.
        // SFTPProvider shells out to /usr/bin/ssh via Foundation.Process,
        // which iOS doesn't expose. Both providers are macOS-only.
        case .iCloud: return ICloudProvider()
        case .sftp: return SFTPProvider()
        case .ftp: return FTPProvider()
        #else
        case .iCloud, .sftp, .ftp:
            fatalError("Provider \(type) is macOS-only and shouldn't reach createProvider on iOS")
        #endif
        case .gmxCloud: return GMXCloudProvider()
        case .s3: return S3Provider()
        case .synologyDrive: return SynologyDriveProvider()
        case .synologyC2: return SynologyC2Provider()
        case .s3Compatible: return S3CompatibleProvider()
        case .box: return BoxProvider()
        case .seafile: return SeafileProvider()
        case .filen: return FilenProvider()
        case .internxt: return InternxtProvider()
        case .terabox: return TeraBoxProvider()
        case .jottacloud: return JottacloudProvider()
        case .googleDrivePicker: return GoogleDrivePickerProvider()
        case .gopro: return GoProProvider()
        }
    }
}
