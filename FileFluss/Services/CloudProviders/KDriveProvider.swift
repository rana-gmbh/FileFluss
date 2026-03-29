import Foundation
import os

private let kDriveLog = Logger(subsystem: "com.rana.FileFluss", category: "kDrive")

final class KDriveProvider: CloudProvider, @unchecked Sendable {
    let providerType: CloudProviderType = .kDrive

    static func log(_ msg: String) {
        kDriveLog.info("\(msg)")
        let logFile = FileManager.default.temporaryDirectory.appendingPathComponent("filefluss_kdrive.log")
        let line = "\(Date()): \(msg)\n"
        if let handle = try? FileHandle(forWritingTo: logFile) {
            handle.seekToEndOfFile()
            handle.write(line.data(using: .utf8)!)
            handle.closeFile()
        } else {
            try? line.write(to: logFile, atomically: true, encoding: .utf8)
        }
    }

    private var apiClient: KDriveAPIClient?
    private let keychainKey: String

    var isAuthenticated: Bool {
        get async { apiClient != nil }
    }

    init(accountId: UUID = UUID()) {
        self.keychainKey = "kdrive.\(accountId.uuidString)"
        restoreCredentials()
    }

    init(credentials: KDriveCredentials) {
        self.keychainKey = "kdrive.\(credentials.driveId)"
        self.apiClient = KDriveAPIClient(credentials: credentials)
    }

    // MARK: - Authentication

    func authenticate(apiToken: String) async throws {
        // Get user profile to find account ID
        let profile = try await fetchProfile(token: apiToken)
        KDriveProvider.log("[kDrive] Profile: \(profile.email), accountId=\(profile.accountId)")

        // Fetch drives using account ID
        let drives: [KDriveDriveListItem]
        do {
            drives = try await fetchDrives(token: apiToken, accountId: profile.accountId)
        } catch {
            KDriveProvider.log("[kDrive] fetchDrives failed: \(error)")
            throw error
        }
        guard let firstDrive = drives.first else {
            throw CloudProviderError.notFound("No kDrive found for this account")
        }
        KDriveProvider.log("[kDrive] Using drive: id=\(firstDrive.id) name=\(firstDrive.name)")

        let email = profile.displayName.isEmpty ? profile.email : profile.displayName

        let credentials = KDriveCredentials(
            apiToken: apiToken,
            driveId: firstDrive.id,
            userEmail: email
        )
        self.apiClient = KDriveAPIClient(credentials: credentials)

        // Resolve and cache root folder ID (default to 1 if lookup fails)
        let rootId = (try? await apiClient!.fetchRootFileId()) ?? 1
        await apiClient!.setRootId(rootId)

        try KeychainService.save(key: keychainKey, value: credentials)
    }

    func authenticate() async throws {
        throw CloudProviderError.notAuthenticated
    }

    func disconnect() async throws {
        apiClient = nil
        try KeychainService.delete(key: keychainKey)
    }

    func userDisplayName() async throws -> String {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.userInfo()
    }

    // MARK: - File Operations

    func listDirectory(at path: String) async throws -> [CloudFileItem] {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let items = try await client.listFolder(path: path)
        // Cache all returned items' paths
        for item in items {
            let fileIdStr = item.id.replacingOccurrences(of: "d", with: "").replacingOccurrences(of: "f", with: "")
            if let fileId = Int(fileIdStr) {
                await client.cachePath(item.path, fileId: fileId)
            }
        }
        return items
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.downloadFile(remotePath: remotePath, to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let folderPath = (remotePath as NSString).deletingLastPathComponent
        let fileName = (remotePath as NSString).lastPathComponent
        let folderId = try await client.resolvePathToId(folderPath)
        try await client.uploadFile(from: localURL, toFolderId: folderId, fileName: fileName)
    }

    func deleteItem(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.deleteFile(path: path)
    }

    func createDirectory(at path: String) async throws {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        try await client.createFolder(path: path)
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        let fileId = try await client.resolvePathToId(path)
        let metadata = try await client.stat(fileId: fileId)
        let parentPath = (path as NSString).deletingLastPathComponent
        return metadata.toCloudFileItem(parentPath: parentPath)
    }

    func folderSize(at path: String) async throws -> Int64 {
        guard let client = apiClient else { throw CloudProviderError.notAuthenticated }
        return try await client.folderSize(path: path)
    }

    // MARK: - Private

    private func restoreCredentials() {
        if let creds = KeychainService.load(key: keychainKey, as: KDriveCredentials.self) {
            apiClient = KDriveAPIClient(credentials: creds)
            Task {
                if let rootId = try? await apiClient?.fetchRootFileId() {
                    await apiClient?.setRootId(rootId)
                }
            }
        }
    }

    struct UserProfile {
        let email: String
        let displayName: String
        let accountId: Int
    }

    private func fetchProfile(token: String) async throws -> UserProfile {
        guard let url = URL(string: "https://api.infomaniak.com/1/profile") else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await URLSession.shared.data(for: request)
        let bodyStr = String(data: data, encoding: .utf8) ?? ""
        KDriveProvider.log("[kDrive] /1/profile → HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0): \(bodyStr.prefix(500))")

        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            if (response as? HTTPURLResponse)?.statusCode == 401 {
                throw CloudProviderError.invalidCredentials
            }
            throw CloudProviderError.invalidResponse
        }

        struct ProfileResponse: Decodable {
            let result: String
            let data: ProfileData
        }
        struct ProfileData: Decodable {
            let id: Int?
            let email: String?
            let display_name: String?
            let current_account_id: Int?
        }

        let parsed = try JSONDecoder().decode(ProfileResponse.self, from: data)
        let accountId = parsed.data.current_account_id ?? parsed.data.id ?? 0
        return UserProfile(
            email: parsed.data.email ?? "Unknown",
            displayName: parsed.data.display_name ?? "",
            accountId: accountId
        )
    }

    private func fetchDrives(token: String, accountId: Int) async throws -> [KDriveDriveListItem] {
        let urlString = "https://api.infomaniak.com/2/drive?account_id=\(accountId)"
        guard let url = URL(string: urlString) else { throw CloudProviderError.invalidResponse }

        var request = URLRequest(url: url)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }

        let bodyStr = String(data: data, encoding: .utf8) ?? ""
        KDriveProvider.log("[kDrive] /2/drive?account_id=\(accountId) → HTTP \(http.statusCode): \(bodyStr.prefix(500))")

        if http.statusCode == 401 { throw CloudProviderError.invalidCredentials }
        guard (200...299).contains(http.statusCode) else {
            throw CloudProviderError.serverError(http.statusCode)
        }

        struct DriveListResponse: Decodable {
            let result: String
            let data: [KDriveDriveListItem]
        }

        let parsed = try JSONDecoder().decode(DriveListResponse.self, from: data)
        return parsed.data
    }
}
