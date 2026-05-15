import Foundation

public actor SyncEngine {
    public static let shared = SyncEngine()

    private var providers: [UUID: any CloudProvider] = [:]
    private var activeSyncs: Set<UUID> = []

    public func registerProvider(for accountId: UUID, provider: any CloudProvider) {
        providers[accountId] = provider
    }

    public func removeProvider(for accountId: UUID) {
        providers.removeValue(forKey: accountId)
    }

    public func provider(for accountId: UUID) -> (any CloudProvider)? {
        providers[accountId]
    }

    public func sync(rule: SyncRule) async throws {
        guard let provider = providers[rule.accountId] else {
            throw CloudProviderError.notAuthenticated
        }

        guard !activeSyncs.contains(rule.id) else { return }
        activeSyncs.insert(rule.id)
        defer { activeSyncs.remove(rule.id) }

        switch rule.direction {
        case .upload:
            try await syncUpload(rule: rule, provider: provider)
        case .download:
            try await syncDownload(rule: rule, provider: provider)
        case .bidirectional:
            try await syncBidirectional(rule: rule, provider: provider)
        }
    }

    private func syncUpload(rule: SyncRule, provider: any CloudProvider) async throws {
        let localItems = try await FileSystemService.shared.listDirectory(at: rule.localPath, showHidden: false)

        for item in localItems where !item.isDirectory {
            let remotePath = rule.remotePath + "/" + item.name
            try await provider.uploadFile(from: item.url, to: remotePath)
        }
    }

    private func syncDownload(rule: SyncRule, provider: any CloudProvider) async throws {
        let remoteItems = try await provider.listDirectory(at: rule.remotePath)

        for item in remoteItems where !item.isDirectory {
            let localURL = rule.localPath.appendingPathComponent(item.name)
            try await provider.downloadFile(remotePath: item.path, to: localURL)
        }
    }

    private func syncBidirectional(rule: SyncRule, provider: any CloudProvider) async throws {
        // TODO: Implement conflict detection and resolution
        try await syncUpload(rule: rule, provider: provider)
        try await syncDownload(rule: rule, provider: provider)
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
        #else
        case .iCloud, .sftp:
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
        }
    }
}
