import Foundation

final class PCloudProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .pCloud
    private var authenticated = false

    var isAuthenticated: Bool {
        get async { authenticated }
    }

    func authenticate() async throws {
        // TODO: Implement pCloud OAuth2 authentication
        authenticated = true
    }

    func disconnect() async throws {
        authenticated = false
    }

    func listDirectory(at path: String) async throws -> [CloudFileItem] {
        // TODO: Implement pCloud API - listfolder
        return []
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        // TODO: Implement pCloud API - downloadfile
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        // TODO: Implement pCloud API - uploadfile
    }

    func deleteItem(at path: String) async throws {
        // TODO: Implement pCloud API - deletefile/deletefolder
    }

    func createDirectory(at path: String) async throws {
        // TODO: Implement pCloud API - createfolder
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        // TODO: Implement pCloud API - stat
        throw CloudProviderError.notImplemented
    }
}
