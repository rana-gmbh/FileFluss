import Foundation
import os

private let oneDriveLog = Logger(subsystem: "com.rana.FileFluss", category: "oneDrive")

struct OneDriveCredentials: Codable, Sendable {
    let accessToken: String
    let refreshToken: String
    let expiresAt: Date
    let userEmail: String
}

struct OneDriveDeviceCode: Sendable {
    let deviceCode: String
    let userCode: String
    let verificationUri: String
    let expiresIn: Int
    let interval: Int
    let message: String
}

actor OneDriveAPIClient {
    static let clientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e"
    static let scopes = "https://graph.microsoft.com/Files.ReadWrite.All https://graph.microsoft.com/User.Read offline_access"

    private(set) var credentials: OneDriveCredentials
    private let session: URLSession
    private let graphURL = "https://graph.microsoft.com/v1.0"
    private let authURL = "https://login.microsoftonline.com/common/oauth2/v2.0"

    init(credentials: OneDriveCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)
    }

    // MARK: - Authentication

    static func requestDeviceCode() async throws -> OneDriveDeviceCode {
        let url = URL(string: "https://login.microsoftonline.com/common/oauth2/v2.0/devicecode")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

        let body = "client_id=\(clientId)&scope=\(scopes.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? scopes)"
        request.httpBody = body.data(using: .utf8)

        let (data, response) = try await URLSession.shared.data(for: request)
        let bodyStr = String(data: data, encoding: .utf8) ?? ""
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            oneDriveLog.error("[OneDrive] Device code request failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(1000))")
            throw CloudProviderError.serverError(http?.statusCode ?? 0)
        }
        oneDriveLog.info("[OneDrive] Device code response: \(bodyStr.prefix(500))")

        struct DeviceCodeResponse: Decodable {
            let device_code: String
            let user_code: String
            let verification_uri: String
            let expires_in: Int
            let interval: Int
            let message: String
        }

        let parsed = try JSONDecoder().decode(DeviceCodeResponse.self, from: data)
        return OneDriveDeviceCode(
            deviceCode: parsed.device_code,
            userCode: parsed.user_code,
            verificationUri: parsed.verification_uri,
            expiresIn: parsed.expires_in,
            interval: parsed.interval,
            message: parsed.message
        )
    }

    static func pollForToken(deviceCode: String) async throws -> OneDriveCredentials {
        let url = URL(string: "https://login.microsoftonline.com/common/oauth2/v2.0/token")!
        let body = "grant_type=urn:ietf:params:oauth:grant-type:device_code&client_id=\(clientId)&device_code=\(deviceCode)"

        let startTime = Date()
        let timeout: TimeInterval = 900 // 15 minutes max

        while Date().timeIntervalSince(startTime) < timeout {
            try await Task.sleep(nanoseconds: 5_000_000_000) // 5 seconds

            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
            request.httpBody = body.data(using: .utf8)

            let (data, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                throw CloudProviderError.invalidResponse
            }

            if (200...299).contains(http.statusCode) {
                let tokenResponse = try JSONDecoder().decode(TokenResponse.self, from: data)
                let expiresAt = Date().addingTimeInterval(TimeInterval(tokenResponse.expires_in))

                // Fetch user email
                let email = try await fetchUserEmail(accessToken: tokenResponse.access_token)

                return OneDriveCredentials(
                    accessToken: tokenResponse.access_token,
                    refreshToken: tokenResponse.refresh_token ?? "",
                    expiresAt: expiresAt,
                    userEmail: email
                )
            }

            // Check for pending/error states
            struct ErrorResponse: Decodable {
                let error: String
                let error_description: String?
            }

            if let errorResponse = try? JSONDecoder().decode(ErrorResponse.self, from: data) {
                switch errorResponse.error {
                case "authorization_pending":
                    continue
                case "slow_down":
                    try await Task.sleep(nanoseconds: 5_000_000_000)
                    continue
                case "authorization_declined":
                    throw CloudProviderError.unauthorized
                case "expired_token":
                    throw CloudProviderError.notAuthenticated
                default:
                    oneDriveLog.error("[OneDrive] Token poll error: \(errorResponse.error)")
                    throw CloudProviderError.invalidResponse
                }
            }
        }

        throw CloudProviderError.notAuthenticated
    }

    private static func fetchUserEmail(accessToken: String) async throws -> String {
        let url = URL(string: "https://graph.microsoft.com/v1.0/me")!
        var request = URLRequest(url: url)
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            return "Unknown"
        }

        struct UserResponse: Decodable {
            let displayName: String?
            let mail: String?
            let userPrincipalName: String?
        }

        let user = try JSONDecoder().decode(UserResponse.self, from: data)
        return user.displayName ?? user.mail ?? user.userPrincipalName ?? "Unknown"
    }

    func refreshTokenIfNeeded() async throws -> OneDriveCredentials {
        guard Date() >= credentials.expiresAt.addingTimeInterval(-60) else {
            return credentials
        }

        guard !credentials.refreshToken.isEmpty else {
            throw CloudProviderError.notAuthenticated
        }

        let url = URL(string: "\(authURL)/token")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

        let body = "client_id=\(Self.clientId)&grant_type=refresh_token&refresh_token=\(credentials.refreshToken)&scope=\(Self.scopes.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? Self.scopes)"
        request.httpBody = body.data(using: .utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            oneDriveLog.error("[OneDrive] Token refresh failed: HTTP \(http?.statusCode ?? 0)")
            throw CloudProviderError.notAuthenticated
        }

        let tokenResponse = try JSONDecoder().decode(TokenResponse.self, from: data)
        let newCreds = OneDriveCredentials(
            accessToken: tokenResponse.access_token,
            refreshToken: tokenResponse.refresh_token ?? credentials.refreshToken,
            expiresAt: Date().addingTimeInterval(TimeInterval(tokenResponse.expires_in)),
            userEmail: credentials.userEmail
        )
        credentials = newCreds
        return newCreds
    }

    // MARK: - File Operations

    func listFolder(path: String) async throws -> [CloudFileItem] {
        let endpoint: String
        if path == "/" || path.isEmpty {
            endpoint = "/me/drive/root/children"
        } else {
            let encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? path
            endpoint = "/me/drive/root:\(encodedPath):/children"
        }

        let response: GraphListResponse = try await graphRequest(.get, path: endpoint, queryItems: [
            URLQueryItem(name: "$top", value: "1000"),
            URLQueryItem(name: "$orderby", value: "name asc"),
        ])

        return response.value.map { $0.toCloudFileItem(parentPath: path) }
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        let encodedPath = remotePath.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? remotePath
        let endpoint = "/me/drive/root:\(encodedPath):/content"

        let url = URL(string: "\(graphURL)\(endpoint)")!
        var request = URLRequest(url: url)
        let creds = try await refreshTokenIfNeeded()
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...399).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
        try data.write(to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        let fileData = try Data(contentsOf: localURL)
        let encodedPath = remotePath.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? remotePath

        // Files up to 4MB use simple upload; larger files use upload session
        if fileData.count <= 4_000_000 {
            try await simpleUpload(data: fileData, remotePath: encodedPath)
        } else {
            try await largeFileUpload(from: localURL, fileSize: fileData.count, remotePath: encodedPath)
        }
    }

    private func simpleUpload(data: Data, remotePath: String) async throws {
        let endpoint = "/me/drive/root:\(remotePath):/content"
        let url = URL(string: "\(graphURL)\(endpoint)")!
        var request = URLRequest(url: url)
        request.httpMethod = "PUT"
        let creds = try await refreshTokenIfNeeded()
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.httpBody = data

        let (responseData, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: responseData, encoding: .utf8) ?? ""
            oneDriveLog.error("[OneDrive] Upload failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    private func largeFileUpload(from localURL: URL, fileSize: Int, remotePath: String) async throws {
        // Create upload session
        let endpoint = "/me/drive/root:\(remotePath):/createUploadSession"
        let url = URL(string: "\(graphURL)\(endpoint)")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        let creds = try await refreshTokenIfNeeded()
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(["item": ["@microsoft.graph.conflictBehavior": "replace"]])

        let (sessionData, sessionResponse) = try await session.data(for: request)
        guard let http = sessionResponse as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = sessionResponse as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }

        struct UploadSession: Decodable {
            let uploadUrl: String
        }
        let uploadSession = try JSONDecoder().decode(UploadSession.self, from: sessionData)
        guard let uploadURL = URL(string: uploadSession.uploadUrl) else {
            throw CloudProviderError.invalidResponse
        }

        // Upload in 10MB chunks
        let chunkSize = 10 * 1024 * 1024
        let fileData = try Data(contentsOf: localURL)
        var offset = 0

        while offset < fileSize {
            let end = min(offset + chunkSize, fileSize)
            let chunk = fileData[offset..<end]

            var chunkRequest = URLRequest(url: uploadURL)
            chunkRequest.httpMethod = "PUT"
            chunkRequest.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
            chunkRequest.setValue("bytes \(offset)-\(end - 1)/\(fileSize)", forHTTPHeaderField: "Content-Range")
            chunkRequest.setValue("\(chunk.count)", forHTTPHeaderField: "Content-Length")
            chunkRequest.httpBody = chunk

            let (_, chunkResponse) = try await session.data(for: chunkRequest)
            guard let chunkHttp = chunkResponse as? HTTPURLResponse,
                  (200...299).contains(chunkHttp.statusCode) || chunkHttp.statusCode == 308 else {
                let chunkHttp = chunkResponse as? HTTPURLResponse
                throw Self.mapHTTPError(statusCode: chunkHttp?.statusCode ?? 0)
            }

            offset = end
        }
    }

    func deleteItem(at path: String) async throws {
        let encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? path
        let endpoint = "/me/drive/root:\(encodedPath):"
        try await graphRequestVoid(.delete, path: endpoint)
    }

    func createFolder(at path: String) async throws {
        let parentPath = (path as NSString).deletingLastPathComponent
        let folderName = (path as NSString).lastPathComponent

        let endpoint: String
        if parentPath == "/" || parentPath.isEmpty {
            endpoint = "/me/drive/root/children"
        } else {
            let encodedParent = parentPath.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? parentPath
            endpoint = "/me/drive/root:\(encodedParent):/children"
        }

        struct CreateFolderBody: Encodable {
            let name: String
            let folder: FolderFacet
            // swiftlint:disable:next nesting
            struct FolderFacet: Encodable {}
            enum CodingKeys: String, CodingKey {
                case name, folder
                case conflictBehavior = "@microsoft.graph.conflictBehavior"
            }
            let conflictBehavior = "fail"
            func encode(to encoder: Encoder) throws {
                var container = encoder.container(keyedBy: CodingKeys.self)
                try container.encode(name, forKey: .name)
                try container.encode(folder, forKey: .folder)
                try container.encode(conflictBehavior, forKey: .conflictBehavior)
            }
        }

        let body = CreateFolderBody(name: folderName, folder: .init())
        let _: GraphDriveItem = try await graphRequest(.post, path: endpoint, body: body)
    }

    func renameItem(at path: String, to newName: String) async throws {
        let encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? path
        let endpoint = "/me/drive/root:\(encodedPath):"

        struct RenameBody: Encodable {
            let name: String
        }

        let _: GraphDriveItem = try await graphRequest(.patch, path: endpoint, body: RenameBody(name: newName))
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        let encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? path
        let endpoint = "/me/drive/root:\(encodedPath):"
        let item: GraphDriveItem = try await graphRequest(.get, path: endpoint)
        let parentPath = (path as NSString).deletingLastPathComponent
        return item.toCloudFileItem(parentPath: parentPath)
    }

    func folderSize(at path: String) async throws -> Int64 {
        // Get the folder item which includes a size property for the subtree
        let encodedPath: String
        if path == "/" || path.isEmpty {
            let item: GraphDriveItem = try await graphRequest(.get, path: "/me/drive/root")
            return item.size ?? 0
        } else {
            encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? path
            let item: GraphDriveItem = try await graphRequest(.get, path: "/me/drive/root:\(encodedPath):")
            if let size = item.size, size > 0 {
                return size
            }
        }

        // Fallback: calculate recursively
        return try await calculateFolderSizeRecursively(path: path)
    }

    private func calculateFolderSizeRecursively(path: String) async throws -> Int64 {
        let items = try await listFolder(path: path)
        var total: Int64 = 0
        for item in items {
            if item.isDirectory {
                total += try await calculateFolderSizeRecursively(path: item.path)
            } else {
                total += item.size
            }
        }
        return total
    }

    func userDisplayName() async throws -> String {
        let creds = try await refreshTokenIfNeeded()
        return creds.userEmail
    }

    // MARK: - HTTP

    private enum HTTPMethod: String {
        case get = "GET"
        case post = "POST"
        case put = "PUT"
        case patch = "PATCH"
        case delete = "DELETE"
    }

    private func graphRequest<T: Decodable>(_ method: HTTPMethod, path: String, queryItems: [URLQueryItem] = [], body: (any Encodable)? = nil) async throws -> T {
        var components = URLComponents(string: "\(graphURL)\(path)")!
        if !queryItems.isEmpty {
            components.queryItems = (components.queryItems ?? []) + queryItems
        }

        guard let url = components.url else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = method.rawValue
        let creds = try await refreshTokenIfNeeded()
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")

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
            oneDriveLog.error("[OneDrive] \(method.rawValue) \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http.statusCode)
        }

        return try JSONDecoder().decode(T.self, from: data)
    }

    private func graphRequestVoid(_ method: HTTPMethod, path: String) async throws {
        var components = URLComponents(string: "\(graphURL)\(path)")!
        guard let url = components.url else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = method.rawValue
        let creds = try await refreshTokenIfNeeded()
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        // 204 No Content is expected for DELETE
        guard (200...299).contains(http.statusCode) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            oneDriveLog.error("[OneDrive] \(method.rawValue) \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http.statusCode)
        }
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

// MARK: - Microsoft Graph API Response Types

private struct TokenResponse: Decodable {
    let access_token: String
    let refresh_token: String?
    let expires_in: Int
    let token_type: String
}

struct GraphListResponse: Decodable {
    let value: [GraphDriveItem]
}

struct GraphDriveItem: Decodable {
    let id: String
    let name: String
    let size: Int64?
    let lastModifiedDateTime: String?
    let folder: GraphFolder?
    let file: GraphFile?

    struct GraphFolder: Decodable {
        let childCount: Int?
    }

    struct GraphFile: Decodable {
        let mimeType: String?
        let hashes: GraphHashes?
    }

    struct GraphHashes: Decodable {
        let sha1Hash: String?
        let quickXorHash: String?
    }

    var isDirectory: Bool { folder != nil }

    func toCloudFileItem(parentPath: String) -> CloudFileItem {
        let itemPath: String
        if parentPath == "/" {
            itemPath = "/\(name)"
        } else {
            itemPath = "\(parentPath)/\(name)"
        }

        let modDate: Date
        if let dateStr = lastModifiedDateTime {
            let formatter = ISO8601DateFormatter()
            formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            modDate = formatter.date(from: dateStr) ?? (ISO8601DateFormatter().date(from: dateStr) ?? Date.distantPast)
        } else {
            modDate = Date.distantPast
        }

        return CloudFileItem(
            id: isDirectory ? "d\(id)" : "f\(id)",
            name: name,
            path: itemPath,
            isDirectory: isDirectory,
            size: size ?? 0,
            modificationDate: modDate,
            checksum: file?.hashes?.sha1Hash ?? file?.hashes?.quickXorHash
        )
    }
}
