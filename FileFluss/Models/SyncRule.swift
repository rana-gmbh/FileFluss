import Foundation

enum SyncDirection: String, Codable, CaseIterable {
    case upload
    case download
    case bidirectional

    var displayName: String {
        switch self {
        case .upload: return "Upload Only"
        case .download: return "Download Only"
        case .bidirectional: return "Bidirectional"
        }
    }

    var icon: String {
        switch self {
        case .upload: return "arrow.up.circle"
        case .download: return "arrow.down.circle"
        case .bidirectional: return "arrow.up.arrow.down.circle"
        }
    }
}

enum SyncStatus: String, Codable {
    case idle
    case syncing
    case paused
    case error

    var displayName: String {
        switch self {
        case .idle: return "Up to date"
        case .syncing: return "Syncing..."
        case .paused: return "Paused"
        case .error: return "Error"
        }
    }
}

struct SyncRule: Identifiable, Codable {
    let id: UUID
    var localPath: URL
    var remotePath: String
    var accountId: UUID
    var direction: SyncDirection
    var isEnabled: Bool
    var status: SyncStatus
    var excludePatterns: [String]
    var lastSyncDate: Date?
    var errorMessage: String?

    init(
        id: UUID = UUID(),
        localPath: URL,
        remotePath: String,
        accountId: UUID,
        direction: SyncDirection = .bidirectional,
        isEnabled: Bool = true,
        status: SyncStatus = .idle,
        excludePatterns: [String] = [],
        lastSyncDate: Date? = nil,
        errorMessage: String? = nil
    ) {
        self.id = id
        self.localPath = localPath
        self.remotePath = remotePath
        self.accountId = accountId
        self.direction = direction
        self.isEnabled = isEnabled
        self.status = status
        self.excludePatterns = excludePatterns
        self.lastSyncDate = lastSyncDate
        self.errorMessage = errorMessage
    }
}
