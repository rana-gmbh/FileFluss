import Foundation

/// A Google Drive folder that the user has explicitly granted FileFluss access to
/// via the Google Picker. Under the `drive.file` OAuth scope, the app can only
/// see files the user has picked (or that the app itself has created), and
/// each picked folder cascades access to its entire subtree.
struct PickedDriveFolder: Codable, Sendable, Hashable, Identifiable {
    /// Google Drive file ID (not prefixed with "d" or "f" like CloudFileItem.id).
    let id: String
    /// Display name at the time the folder was picked. May drift if renamed in Drive.
    var name: String
    /// Timestamp of when the folder was added to the picked list.
    let addedAt: Date

    init(id: String, name: String, addedAt: Date = Date()) {
        self.id = id
        self.name = name
        self.addedAt = addedAt
    }
}

/// Per-account persistence of picked folders. Stored in UserDefaults under
/// "googledrive.picked.<accountId>" as a JSON-encoded array.
enum PickedDriveFolderStore {
    private static func key(for accountId: UUID) -> String {
        "googledrive.picked.\(accountId.uuidString)"
    }

    static func load(accountId: UUID) -> [PickedDriveFolder] {
        guard let data = UserDefaults.standard.data(forKey: key(for: accountId)),
              let folders = try? JSONDecoder().decode([PickedDriveFolder].self, from: data) else {
            return []
        }
        return folders
    }

    static func save(_ folders: [PickedDriveFolder], accountId: UUID) {
        if let data = try? JSONEncoder().encode(folders) {
            UserDefaults.standard.set(data, forKey: key(for: accountId))
        }
    }

    static func clear(accountId: UUID) {
        UserDefaults.standard.removeObject(forKey: key(for: accountId))
    }
}
