import Foundation

enum CloudProviderType: String, Codable, CaseIterable, Identifiable {
    case pCloud
    case oneDrive
    case googleDrive
    case nextCloud
    case iCloud

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .pCloud: return "pCloud"
        case .oneDrive: return "OneDrive"
        case .googleDrive: return "Google Drive"
        case .nextCloud: return "NextCloud"
        case .iCloud: return "iCloud"
        }
    }

    var icon: String {
        switch self {
        case .pCloud: return "cloud"
        case .oneDrive: return "cloud.fill"
        case .googleDrive: return "externaldrive.connected.to.line.below"
        case .nextCloud: return "cloud.circle"
        case .iCloud: return "icloud"
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
