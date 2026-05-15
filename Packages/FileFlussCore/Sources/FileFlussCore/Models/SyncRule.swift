import Foundation

public enum SyncDirection: String, Codable, CaseIterable, Sendable {
    case upload
    case download
    case bidirectional

    public var displayName: String {
        switch self {
        case .upload: return "Upload Only"
        case .download: return "Download Only"
        case .bidirectional: return "Bidirectional"
        }
    }

    public var icon: String {
        switch self {
        case .upload: return "arrow.up.circle"
        case .download: return "arrow.down.circle"
        case .bidirectional: return "arrow.up.arrow.down.circle"
        }
    }
}

public enum SyncStatus: String, Codable, Sendable {
    case idle
    case syncing
    case paused
    case error

    public var displayName: String {
        switch self {
        case .idle: return "Up to date"
        case .syncing: return "Syncing..."
        case .paused: return "Paused"
        case .error: return "Error"
        }
    }
}

public struct SyncRule: Identifiable, Codable, Sendable {
    public let id: UUID
    public var localPath: URL
    public var remotePath: String
    public var accountId: UUID
    public var direction: SyncDirection
    public var isEnabled: Bool
    public var status: SyncStatus
    public var excludePatterns: [String]
    public var lastSyncDate: Date?
    public var errorMessage: String?

    public init(
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
