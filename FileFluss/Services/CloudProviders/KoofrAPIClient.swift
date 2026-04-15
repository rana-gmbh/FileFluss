import Foundation
import os

private let koofrLog = Logger(subsystem: "com.rana.FileFluss", category: "koofr")

struct KoofrCredentials: Codable, Sendable {
    let email: String
    let appPassword: String
    let primaryMountId: String
    let displayName: String
}

actor KoofrAPIClient {
    let credentials: KoofrCredentials
    private let session: URLSession
    private let baseURL = "https://app.koofr.net/api/v2"
    private let contentURL = "https://app.koofr.net/content/api/v2"

    init(credentials: KoofrCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)
    }

    private var authHeader: String {
        let cred = "\(credentials.email):\(credentials.appPassword)"
        let encoded = Data(cred.utf8).base64EncodedString()
        return "Basic \(encoded)"
    }

    private var mountId: String { credentials.primaryMountId }

    // MARK: - Authentication & User Info

    static func authenticate(email: String, appPassword: String) async throws -> KoofrCredentials {
        let baseURL = "https://app.koofr.net/api/v2"
        let cred = "\(email):\(appPassword)"
        let encoded = Data(cred.utf8).base64EncodedString()
        let auth = "Basic \(encoded)"

        // Verify credentials by fetching user info
        let userURL = URL(string: "\(baseURL)/user")!
        var userRequest = URLRequest(url: userURL)
        userRequest.setValue(auth, forHTTPHeaderField: "Authorization")

        let (userData, userResponse) = try await URLSession.shared.data(for: userRequest)
        guard let http = userResponse as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        if http.statusCode == 401 {
            throw CloudProviderError.invalidCredentials
        }
        guard (200...299).contains(http.statusCode) else {
            koofrLog.error("[Koofr] User info failed: HTTP \(http.statusCode)")
            throw CloudProviderError.serverError(http.statusCode)
        }

        let user = try JSONDecoder().decode(KoofrUserResponse.self, from: userData)
        let displayName: String
        if !user.firstName.isEmpty || !user.lastName.isEmpty {
            displayName = "\(user.firstName) \(user.lastName)".trimmingCharacters(in: .whitespaces)
        } else {
            displayName = user.email
        }

        // Fetch mounts to find primary
        let mountsURL = URL(string: "\(baseURL)/mounts")!
        var mountsRequest = URLRequest(url: mountsURL)
        mountsRequest.setValue(auth, forHTTPHeaderField: "Authorization")

        let (mountsData, mountsResponse) = try await URLSession.shared.data(for: mountsRequest)
        guard let mountsHttp = mountsResponse as? HTTPURLResponse, (200...299).contains(mountsHttp.statusCode) else {
            throw CloudProviderError.invalidResponse
        }

        let mountsResult = try JSONDecoder().decode(KoofrMountsResponse.self, from: mountsData)
        guard let primary = mountsResult.mounts.first(where: { $0.isPrimary }) ?? mountsResult.mounts.first else {
            throw CloudProviderError.notFound("No mount found")
        }

        koofrLog.info("[Koofr] Authenticated as \(displayName), mount: \(primary.id)")

        return KoofrCredentials(
            email: email,
            appPassword: appPassword,
            primaryMountId: primary.id,
            displayName: displayName
        )
    }

    func userDisplayName() -> String {
        credentials.displayName
    }

    // MARK: - File Operations

    func listFolder(path: String) async throws -> [CloudFileItem] {
        let encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? path
        let response: KoofrFilesResponse = try await request(
            .get,
            path: "/mounts/\(mountId)/files/list",
            queryString: "path=\(encodedPath)"
        )
        return response.files.map { $0.toCloudFileItem(parentPath: path) }
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let encodedPath = remotePath.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? remotePath
        let urlString = "\(contentURL)/mounts/\(mountId)/files/get?path=\(encodedPath)"
        guard let url = URL(string: urlString) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")

        let (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    func uploadFile(from localURL: URL, toFolder folderPath: String, fileName: String) async throws {
        try await uploadFile(from: localURL, toFolder: folderPath, fileName: fileName, onBytes: nil)
    }

    func uploadFile(from localURL: URL, toFolder folderPath: String, fileName: String, onBytes: ByteProgressHandler?) async throws {
        let encodedPath = folderPath.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? folderPath
        let encodedName = fileName.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? fileName
        let urlString = "\(contentURL)/mounts/\(mountId)/files/put?path=\(encodedPath)&filename=\(encodedName)&autorename=false&overwrite=true"
        guard let url = URL(string: urlString) else {
            throw CloudProviderError.invalidResponse
        }

        let fileData = try Data(contentsOf: localURL)
        let boundary = UUID().uuidString
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

        var body = Data()
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"file\"; filename=\"\(fileName)\"\r\n".data(using: .utf8)!)
        body.append("Content-Type: application/octet-stream\r\n\r\n".data(using: .utf8)!)
        body.append(fileData)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)

        let (responseData, response) = try await session.uploadReportingProgress(for: request, body: body, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: responseData, encoding: .utf8) ?? ""
            koofrLog.error("[Koofr] Upload failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    func deleteItem(at path: String) async throws {
        let encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? path
        try await requestVoid(.delete, path: "/mounts/\(mountId)/files/remove", queryString: "path=\(encodedPath)")
    }

    func createFolder(parentPath: String, name: String) async throws {
        let fullPath = parentPath == "/" ? "/\(name)" : "\(parentPath)/\(name)"
        if (try? await getFileInfo(at: fullPath)) != nil { return }

        let encodedPath = parentPath.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? parentPath
        struct CreateBody: Encodable { let name: String }
        try await requestVoidWithBody(
            .post,
            path: "/mounts/\(mountId)/files/folder",
            queryString: "path=\(encodedPath)",
            body: CreateBody(name: name)
        )
    }

    func renameItem(at path: String, to newName: String) async throws {
        let encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? path
        struct RenameBody: Encodable { let name: String }
        try await requestVoidWithBody(
            .put,
            path: "/mounts/\(mountId)/files/rename",
            queryString: "path=\(encodedPath)",
            body: RenameBody(name: newName)
        )
    }

    func getFileInfo(at path: String) async throws -> CloudFileItem {
        let encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? path
        let info: KoofrFileInfo = try await request(
            .get,
            path: "/mounts/\(mountId)/files/info",
            queryString: "path=\(encodedPath)"
        )
        let parentPath = (path as NSString).deletingLastPathComponent
        return info.toCloudFileItem(parentPath: parentPath)
    }

    func folderSize(path: String) async throws -> Int64 {
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

    // MARK: - HTTP

    private enum HTTPMethod: String {
        case get = "GET"
        case post = "POST"
        case put = "PUT"
        case delete = "DELETE"
    }

    private func request<T: Decodable>(_ method: HTTPMethod, path: String, queryString: String = "", body: (any Encodable)? = nil) async throws -> T {
        let urlString = queryString.isEmpty
            ? "\(baseURL)\(path)"
            : "\(baseURL)\(path)?\(queryString)"
        guard let url = URL(string: urlString) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = method.rawValue
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")

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
            koofrLog.error("[Koofr] \(method.rawValue) \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http.statusCode)
        }

        return try JSONDecoder().decode(T.self, from: data)
    }

    private func requestVoidWithBody(_ method: HTTPMethod, path: String, queryString: String = "", body: (any Encodable)? = nil) async throws {
        let urlString = queryString.isEmpty
            ? "\(baseURL)\(path)"
            : "\(baseURL)\(path)?\(queryString)"
        guard let url = URL(string: urlString) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = method.rawValue
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")

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
            koofrLog.error("[Koofr] \(method.rawValue) \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http.statusCode)
        }
    }

    private func requestVoid(_ method: HTTPMethod, path: String, queryString: String = "") async throws {
        let urlString = queryString.isEmpty
            ? "\(baseURL)\(path)"
            : "\(baseURL)\(path)?\(queryString)"
        guard let url = URL(string: urlString) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = method.rawValue
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        guard (200...299).contains(http.statusCode) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            koofrLog.error("[Koofr] \(method.rawValue) \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http.statusCode)
        }
    }

    private static func mapHTTPError(statusCode: Int) -> CloudProviderError {
        switch statusCode {
        case 401: return .invalidCredentials
        case 403: return .unauthorized
        case 404: return .notFound("Resource not found")
        case 409: return .serverError(409)
        case 429: return .rateLimited
        case 507: return .quotaExceeded
        default: return .serverError(statusCode)
        }
    }
}

// MARK: - Koofr API Response Types

private struct KoofrUserResponse: Decodable {
    let id: String
    let firstName: String
    let lastName: String
    let email: String
}

private struct KoofrMountsResponse: Decodable {
    let mounts: [KoofrMount]
}

private struct KoofrMount: Decodable {
    let id: String
    let name: String
    let isPrimary: Bool
}

struct KoofrFilesResponse: Decodable {
    let files: [KoofrFileInfo]
}

struct KoofrFileInfo: Decodable {
    let name: String
    let type: String // "file" or "dir"
    let modified: Int64  // milliseconds since epoch
    let size: Int64
    let contentType: String?
    let hash: String?

    var isDirectory: Bool { type == "dir" }

    func toCloudFileItem(parentPath: String) -> CloudFileItem {
        let itemPath: String
        if parentPath == "/" {
            itemPath = "/\(name)"
        } else {
            itemPath = "\(parentPath)/\(name)"
        }

        let modDate = Date(timeIntervalSince1970: TimeInterval(modified) / 1000.0)

        return CloudFileItem(
            id: isDirectory ? "d\(itemPath.hashValue)" : "f\(itemPath.hashValue)",
            name: name,
            path: itemPath,
            isDirectory: isDirectory,
            size: size,
            modificationDate: modDate,
            checksum: hash
        )
    }
}
