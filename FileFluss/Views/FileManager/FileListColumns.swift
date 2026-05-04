import Foundation
import AppKit

enum FileListColumnID: String, CaseIterable {
    case dateModified
    case dateCreated
    case size
    case kind

    var title: String {
        switch self {
        case .dateModified: return "Date Modified"
        case .dateCreated: return "Date Created"
        case .size: return "Size"
        case .kind: return "Kind"
        }
    }

    var sortKey: String {
        switch self {
        case .dateModified: return "date"
        case .dateCreated: return "dateCreated"
        case .size: return "size"
        case .kind: return "kind"
        }
    }

    static func from(sortKey: String) -> FileListColumnID? {
        FileListColumnID.allCases.first { $0.sortKey == sortKey }
    }
}

enum FileListColumnPrefs {
    private static let localKey = "FileList.local.visibleColumns"
    private static let cloudKey = "FileList.cloud.visibleColumns"
    private static let defaultIDs: Set<FileListColumnID> = [.dateModified, .size]

    /// Cloud panels exclude Date Created — `CloudFileItem` does not carry a
    /// creation timestamp because most providers either don't expose one or
    /// expose it inconsistently.
    static func availableColumns(forCloud: Bool) -> [FileListColumnID] {
        forCloud
            ? [.dateModified, .size, .kind]
            : [.dateModified, .dateCreated, .size, .kind]
    }

    static func visibleColumns(forCloud: Bool) -> Set<FileListColumnID> {
        let key = forCloud ? cloudKey : localKey
        let available = Set(availableColumns(forCloud: forCloud))
        if let stored = UserDefaults.standard.array(forKey: key) as? [String] {
            return Set(stored.compactMap(FileListColumnID.init(rawValue:))).intersection(available)
        }
        return defaultIDs.intersection(available)
    }

    static func setVisibleColumns(_ ids: Set<FileListColumnID>, forCloud: Bool) {
        let key = forCloud ? cloudKey : localKey
        UserDefaults.standard.set(ids.map(\.rawValue), forKey: key)
        NotificationCenter.default.post(name: .fileListColumnsChanged, object: forCloud)
    }

    static func toggle(_ id: FileListColumnID, forCloud: Bool) {
        var current = visibleColumns(forCloud: forCloud)
        if current.contains(id) {
            current.remove(id)
        } else {
            current.insert(id)
        }
        setVisibleColumns(current, forCloud: forCloud)
    }
}

extension Notification.Name {
    static let fileListColumnsChanged = Notification.Name("fileListColumnsChanged")
}
