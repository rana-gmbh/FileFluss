import Foundation

final class NextCloudProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .nextCloud
    private var authenticated = false
    private var serverURL: URL?

    var isAuthenticated: Bool {
        get async { authenticated }
    }

    func authenticate() async throws {
        // TODO: Implement NextCloud Login Flow v2 authentication
        authenticated = true
    }

    func disconnect() async throws {
        authenticated = false
        serverURL = nil
    }

    func listDirectory(at path: String) async throws -> [CloudFileItem] {
        // TODO: Implement WebDAV PROPFIND
        return []
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        // TODO: Implement WebDAV GET
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        // TODO: Implement WebDAV PUT
    }

    func deleteItem(at path: String) async throws {
        // TODO: Implement WebDAV DELETE
    }

    func createDirectory(at path: String) async throws {
        // TODO: Implement WebDAV MKCOL
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        // TODO: Implement WebDAV PROPFIND (single file)
        throw CloudProviderError.notImplemented
    }
}
