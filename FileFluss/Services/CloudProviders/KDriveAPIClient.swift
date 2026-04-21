import Foundation

struct KDriveCredentials: Codable, Sendable {
    let apiToken: String
    let driveId: Int
    let userEmail: String
}

actor KDriveAPIClient {
    let credentials: KDriveCredentials
    private let session: URLSession
    private let baseURL = "https://api.infomaniak.com"

    // Cache path → fileId mapping for navigation
    private var pathToId: [String: Int] = ["/": 0] // root placeholder, set after init

    init(credentials: KDriveCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)
        // Root folder ID is typically the drive's root
        pathToId["/"] = credentials.driveId > 0 ? 0 : 0
    }

    // MARK: - Drive Info

    func fetchRootFileId() async throws -> Int {
        // Try to get root file ID from drive info
        if let response: KDriveResponse<KDriveDriveInfo> = try? await request(.get, path: "/2/drive/\(credentials.driveId)") {
            return response.data.rootFileId
        }
        // Fallback: try listing files at root (ID 1 is common default)
        // Verify by listing — if it works, 1 is the root
        let _: KDriveResponse<[KDriveFileMetadata]> = try await request(.get, path: "/2/drive/\(credentials.driveId)/files/1/files")
        return 1
    }

    func setRootId(_ id: Int) {
        pathToId["/"] = id
    }

    // MARK: - Folder Operations

    func listFolder(fileId: Int) async throws -> [KDriveFileMetadata] {
        // kDrive's default per_page on this endpoint is small (10), so a single
        // unpaginated call silently truncates folders with more entries. Loop
        // until a page returns fewer items than per_page.
        let perPage = 500
        var page = 1
        var allItems: [KDriveFileMetadata] = []
        while true {
            let response: KDriveResponse<[KDriveFileMetadata]> = try await request(
                .get,
                path: "/2/drive/\(credentials.driveId)/files/\(fileId)/files",
                queryItems: [
                    URLQueryItem(name: "order_by", value: "name"),
                    URLQueryItem(name: "order", value: "asc"),
                    URLQueryItem(name: "per_page", value: "\(perPage)"),
                    URLQueryItem(name: "page", value: "\(page)"),
                ]
            )
            allItems.append(contentsOf: response.data)
            if response.data.count < perPage { break }
            page += 1
        }
        return allItems
    }

    func listFolder(path: String) async throws -> [CloudFileItem] {
        let fileId = try await resolvePathToId(path)
        let items = try await listFolder(fileId: fileId)
        return items.map { $0.toCloudFileItem(parentPath: path) }
    }

    func createFolder(parentId: Int, name: String) async throws {
        let url = URL(string: "\(baseURL)/2/drive/\(credentials.driveId)/files/\(parentId)/directory")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(["name": name])

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] POST directory → HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    func createFolder(path: String) async throws {
        if (try? await resolvePathToId(path)) != nil { return }

        let parentPath = (path as NSString).deletingLastPathComponent
        let folderName = (path as NSString).lastPathComponent
        let parentId = try await resolvePathToId(parentPath)
        try await createFolder(parentId: parentId, name: folderName)
        // Cache the new folder's path → refresh parent to get the ID
        let items = try await listFolder(fileId: parentId)
        if let created = items.first(where: { $0.name == folderName }) {
            cachePath(path, fileId: created.id)
        }
    }

    // MARK: - File Operations

    func downloadFile(fileId: Int, to localURL: URL, onBytes: ByteProgressHandler? = nil) async throws {
        let url = URL(string: "\(baseURL)/2/drive/\(credentials.driveId)/files/\(fileId)/download")!
        var request = URLRequest(url: url)
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")

        let (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let fileId = try await resolvePathToId(remotePath)
        try await downloadFile(fileId: fileId, to: localURL, onBytes: onBytes)
    }

    func uploadFile(from localURL: URL, toFolderId folderId: Int, fileName: String, onBytes: ByteProgressHandler? = nil) async throws {
        let fileData = try Data(contentsOf: localURL)

        var queryItems: [URLQueryItem] = [
            URLQueryItem(name: "directory_id", value: "\(folderId)"),
            URLQueryItem(name: "file_name", value: fileName),
            URLQueryItem(name: "total_size", value: "\(fileData.count)"),
            URLQueryItem(name: "conflict", value: "version"),
        ]

        // Preserve the local file's timestamps on the cloud side so sync
        // diffs stay stable and Finder-style drags don't reset mod dates.
        let attrs = try? FileManager.default.attributesOfItem(atPath: localURL.path)
        if let modDate = attrs?[.modificationDate] as? Date {
            queryItems.append(URLQueryItem(name: "last_modified_at", value: "\(Int64(modDate.timeIntervalSince1970))"))
        }
        if let createdDate = attrs?[.creationDate] as? Date {
            queryItems.append(URLQueryItem(name: "created_at", value: "\(Int64(createdDate.timeIntervalSince1970))"))
        }

        var components = URLComponents(string: "\(baseURL)/3/drive/\(credentials.driveId)/upload")!
        components.queryItems = queryItems

        guard let url = components.url else {
            throw CloudProviderError.invalidResponse
        }

        // Infomaniak's upload service is on a separate subsystem from the
        // Drive metadata API. A freshly-created folder is visible to listing
        // immediately but the upload endpoint occasionally 404s for ~1s
        // afterwards. Retry a small number of times before giving up.
        var attempt = 0
        while true {
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")
            request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")

            KDriveProvider.log("[kDrive API] POST upload → folderId=\(folderId), fileName=\(fileName), size=\(fileData.count), attempt=\(attempt + 1)")
            let (data, response) = try await session.uploadReportingProgress(for: request, body: fileData, onBytes: onBytes)
            let http = response as? HTTPURLResponse
            let status = http?.statusCode ?? 0
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] POST upload → HTTP \(status): \(bodyStr.prefix(500))")
            if let http, (200...299).contains(http.statusCode) { return }
            if status == 404 && attempt < 3 {
                attempt += 1
                try? await Task.sleep(nanoseconds: UInt64(attempt * 500_000_000))
                continue
            }
            throw Self.mapHTTPError(statusCode: status)
        }
    }

    func deleteFile(fileId: Int) async throws {
        let url = URL(string: "\(baseURL)/2/drive/\(credentials.driveId)/files/\(fileId)")!
        var request = URLRequest(url: url)
        request.httpMethod = "DELETE"
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] DELETE files/\(fileId) → HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    func deleteFile(path: String) async throws {
        let fileId = try await resolvePathToId(path)
        try await deleteFile(fileId: fileId)
        pathToId.removeValue(forKey: path)
    }

    func renameFile(fileId: Int, to newName: String) async throws {
        let url = URL(string: "\(baseURL)/2/drive/\(credentials.driveId)/files/\(fileId)/rename")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(["name": newName])

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] POST rename → HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    func renameFile(path: String, to newName: String) async throws {
        let fileId = try await resolvePathToId(path)
        try await renameFile(fileId: fileId, to: newName)
        // Update cache: remove old path, add new path
        pathToId.removeValue(forKey: path)
        let parentPath = (path as NSString).deletingLastPathComponent
        let newPath = parentPath == "/" ? "/\(newName)" : "\(parentPath)/\(newName)"
        pathToId[newPath] = fileId
    }

    func stat(fileId: Int) async throws -> KDriveFileMetadata {
        let response: KDriveResponse<KDriveFileMetadata> = try await request(
            .get,
            path: "/2/drive/\(credentials.driveId)/files/\(fileId)"
        )
        return response.data
    }

    func folderSize(path: String) async throws -> Int64 {
        let fileId = try await resolvePathToId(path)
        return try await calculateFolderSizeRecursively(fileId: fileId)
    }

    private func calculateFolderSizeRecursively(fileId: Int) async throws -> Int64 {
        let items = try await listFolder(fileId: fileId)
        var total: Int64 = 0
        for item in items {
            if item.isFolder {
                total += try await calculateFolderSizeRecursively(fileId: item.id)
            } else {
                total += item.size ?? 0
            }
        }
        return total
    }

    // MARK: - User Info

    func userInfo() async throws -> String {
        return credentials.userEmail
    }

    // MARK: - Path Resolution

    func resolvePathToId(_ path: String) async throws -> Int {
        if let cached = pathToId[path] {
            return cached
        }

        // Resolve component by component
        let components = path.split(separator: "/", omittingEmptySubsequences: true)
        var currentId = pathToId["/"]!
        var currentPath = ""

        for component in components {
            currentPath += "/\(component)"
            if let cached = pathToId[currentPath] {
                currentId = cached
                continue
            }
            let items = try await listFolder(fileId: currentId)
            guard let match = items.first(where: { $0.name == String(component) }) else {
                throw CloudProviderError.notFound(String(component))
            }
            currentId = match.id
            pathToId[currentPath] = currentId
        }
        return currentId
    }

    func cachePath(_ path: String, fileId: Int) {
        pathToId[path] = fileId
    }

    // MARK: - HTTP

    private enum HTTPMethod: String {
        case get = "GET"
        case post = "POST"
        case put = "PUT"
        case delete = "DELETE"
    }

    private func request<T: Decodable>(_ method: HTTPMethod, path: String, queryItems: [URLQueryItem] = [], body: (any Encodable)? = nil) async throws -> T {
        var components = URLComponents(string: "\(baseURL)\(path)")!
        if !queryItems.isEmpty {
            components.queryItems = (components.queryItems ?? []) + queryItems
        }

        guard let url = components.url else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = method.rawValue
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")

        if let body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONEncoder().encode(body)
        }

        let (data, response) = try await session.data(for: request)

        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        guard (200...299).contains(http.statusCode) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] \(method.rawValue) \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http.statusCode)
        }

        return try JSONDecoder().decode(T.self, from: data)
    }

    private static func mapHTTPError(statusCode: Int) -> CloudProviderError {
        switch statusCode {
        case 401: return .notAuthenticated
        case 403: return .unauthorized
        case 404: return .notFound("Resource not found")
        case 429: return .rateLimited
        case 507: return .quotaExceeded
        default: return .serverError(statusCode)
        }
    }
}

// MARK: - kDrive API Response Types

struct KDriveResponse<T: Decodable>: Decodable {
    let result: String
    let data: T
}

struct KDriveDriveInfo: Decodable {
    let id: Int
    let name: String

    private enum CodingKeys: String, CodingKey {
        case id, name
        case rootFileId = "root_file_id"
    }

    let rootFileId: Int

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(Int.self, forKey: .id)
        name = try container.decode(String.self, forKey: .name)
        // Root file ID might be nested or at top level
        rootFileId = (try? container.decode(Int.self, forKey: .rootFileId)) ?? 1
    }
}

struct KDriveFolderSize: Decodable {
    let size: Int64
}

struct KDriveFileMetadata: Decodable {
    let id: Int
    let name: String
    let type: String // "file" or "dir"
    let size: Int64?
    let lastModifiedAt: Int?
    let createdAt: Int?

    private enum CodingKeys: String, CodingKey {
        case id, name, type, size
        case lastModifiedAt = "last_modified_at"
        case createdAt = "created_at"
    }

    var isFolder: Bool { type == "dir" }

    func toCloudFileItem(parentPath: String) -> CloudFileItem {
        let itemPath: String
        if parentPath == "/" {
            itemPath = "/\(name)"
        } else {
            itemPath = "\(parentPath)/\(name)"
        }

        let modDate: Date
        if let ts = lastModifiedAt {
            modDate = Date(timeIntervalSince1970: TimeInterval(ts))
        } else {
            modDate = Date.distantPast
        }

        return CloudFileItem(
            id: "\(type == "dir" ? "d" : "f")\(id)",
            name: name,
            path: itemPath,
            isDirectory: isFolder,
            size: size ?? 0,
            modificationDate: modDate,
            checksum: nil
        )
    }
}

struct KDriveDriveListItem: Decodable {
    let id: Int
    let name: String
}
