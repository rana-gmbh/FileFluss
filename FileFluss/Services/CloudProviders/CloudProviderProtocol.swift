import Foundation

struct CloudFileItem: Identifiable {
    let id: String
    let name: String
    let path: String
    let isDirectory: Bool
    let size: Int64
    let modificationDate: Date
    let checksum: String?
}

protocol CloudProvider: Sendable {
    var providerType: CloudProviderType { get }

    func authenticate() async throws
    func disconnect() async throws
    var isAuthenticated: Bool { get async }

    func listDirectory(at path: String) async throws -> [CloudFileItem]
    func downloadFile(remotePath: String, to localURL: URL) async throws
    func uploadFile(from localURL: URL, to remotePath: String) async throws
    func deleteItem(at path: String) async throws
    func createDirectory(at path: String) async throws
    func getFileMetadata(at path: String) async throws -> CloudFileItem
}
