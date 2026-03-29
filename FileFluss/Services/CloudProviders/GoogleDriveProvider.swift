import Foundation

final class GoogleDriveProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .googleDrive
    private var authenticated = false

    var isAuthenticated: Bool {
        get async { authenticated }
    }

    func authenticate() async throws {
        // TODO: Implement Google OAuth2 authentication
        authenticated = true
    }

    func disconnect() async throws {
        authenticated = false
    }

    func listDirectory(at path: String) async throws -> [CloudFileItem] {
        // TODO: Implement Google Drive API v3 - files.list
        return []
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        // TODO: Implement Google Drive API v3 - files.get with alt=media
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        // TODO: Implement Google Drive API v3 - files.create
    }

    func deleteItem(at path: String) async throws {
        // TODO: Implement Google Drive API v3 - files.delete
    }

    func createDirectory(at path: String) async throws {
        // TODO: Implement Google Drive API v3 - files.create (folder)
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        // TODO: Implement Google Drive API v3 - files.get
        throw CloudProviderError.notImplemented
    }

    func folderSize(at path: String) async throws -> Int64 {
        // TODO: Implement Google Drive API v3
        throw CloudProviderError.notImplemented
    }
}
