import Foundation

final class ICloudProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .iCloud
    private var authenticated = false

    var isAuthenticated: Bool {
        get async { authenticated }
    }

    func authenticate() async throws {
        // TODO: Implement iCloud Drive access via FileManager.default.url(forUbiquityContainerIdentifier:)
        authenticated = true
    }

    func disconnect() async throws {
        authenticated = false
    }

    func listDirectory(at path: String) async throws -> [CloudFileItem] {
        // TODO: Implement using NSMetadataQuery for iCloud Drive
        return []
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        // TODO: Implement using FileManager.startDownloadingUbiquitousItem(at:)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        // TODO: Implement by moving file to iCloud container
    }

    func deleteItem(at path: String) async throws {
        // TODO: Implement using FileManager.removeItem
    }

    func createDirectory(at path: String) async throws {
        // TODO: Implement using FileManager.createDirectory in iCloud container
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        // TODO: Implement using NSMetadataQuery
        throw CloudProviderError.notImplemented
    }

    func folderSize(at path: String) async throws -> Int64 {
        // TODO: Implement using NSMetadataQuery
        throw CloudProviderError.notImplemented
    }
}
