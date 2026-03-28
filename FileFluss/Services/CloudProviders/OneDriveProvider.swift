import Foundation

final class OneDriveProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .oneDrive
    private var authenticated = false

    var isAuthenticated: Bool {
        get async { authenticated }
    }

    func authenticate() async throws {
        // TODO: Implement Microsoft Graph OAuth2 authentication
        authenticated = true
    }

    func disconnect() async throws {
        authenticated = false
    }

    func listDirectory(at path: String) async throws -> [CloudFileItem] {
        // TODO: Implement Microsoft Graph API - /me/drive/root/children
        return []
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        // TODO: Implement Microsoft Graph API - download
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        // TODO: Implement Microsoft Graph API - upload
    }

    func deleteItem(at path: String) async throws {
        // TODO: Implement Microsoft Graph API - delete
    }

    func createDirectory(at path: String) async throws {
        // TODO: Implement Microsoft Graph API - create folder
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        // TODO: Implement Microsoft Graph API - get item
        throw CloudProviderError.notImplemented
    }
}
