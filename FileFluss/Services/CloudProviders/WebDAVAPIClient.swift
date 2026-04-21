import Foundation
import os

private let webDAVLog = Logger(subsystem: "com.rana.FileFluss", category: "webDAV")

struct WebDAVCredentials: Codable, Sendable {
    let serverURL: String
    let username: String
    let password: String
    let displayName: String
}

/// URLSession delegate that preserves the Authorization header on same-host redirects.
/// WebDAV servers often redirect (e.g. /path → /path/) and URLSession strips auth headers by default.
private final class WebDAVSessionDelegate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        var redirected = request
        if let originalHost = task.originalRequest?.url?.host,
           let redirectHost = request.url?.host,
           originalHost == redirectHost,
           let auth = task.originalRequest?.value(forHTTPHeaderField: "Authorization") {
            redirected.setValue(auth, forHTTPHeaderField: "Authorization")
        }
        completionHandler(redirected)
    }
}

actor WebDAVAPIClient {
    let credentials: WebDAVCredentials
    private let session: URLSession
    private let sessionDelegate = WebDAVSessionDelegate()
    let davBaseURL: String

    init(credentials: WebDAVCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config, delegate: sessionDelegate, delegateQueue: nil)

        var base = credentials.serverURL
        if base.hasSuffix("/") { base = String(base.dropLast()) }
        self.davBaseURL = base
    }

    private var authHeader: String {
        let cred = "\(credentials.username):\(credentials.password)"
        let encoded = Data(cred.utf8).base64EncodedString()
        return "Basic \(encoded)"
    }

    // MARK: - Authentication

    static func authenticate(serverURL: String, username: String, password: String) async throws -> WebDAVCredentials {
        var base = serverURL
        if base.hasSuffix("/") { base = String(base.dropLast()) }

        let cred = "\(username):\(password)"
        let encoded = Data(cred.utf8).base64EncodedString()
        let authDelegate = WebDAVSessionDelegate()
        let authSession = URLSession(configuration: .default, delegate: authDelegate, delegateQueue: nil)

        // Try the user-provided URL first, then common WebDAV sub-paths
        let candidates = [base, "\(base)/webdav", "\(base)/remote.php/dav/files/\(username)", "\(base)/dav"]

        var workingBase: String?
        var lastStatusCode = 0

        for candidate in candidates {
            guard let url = URL(string: "\(candidate)/") else { continue }

            var request = URLRequest(url: url)
            request.httpMethod = "PROPFIND"
            request.setValue("Basic \(encoded)", forHTTPHeaderField: "Authorization")
            request.setValue("0", forHTTPHeaderField: "Depth")
            request.setValue("application/xml", forHTTPHeaderField: "Content-Type")
            request.httpBody = propfindBodyXML.data(using: .utf8)

            do {
                let (_, response) = try await authSession.data(for: request)
                guard let http = response as? HTTPURLResponse else { continue }
                lastStatusCode = http.statusCode

                if http.statusCode == 401 {
                    throw CloudProviderError.invalidCredentials
                }

                if http.statusCode == 207 {
                    // Verify this endpoint supports write operations via OPTIONS
                    var optionsReq = URLRequest(url: url)
                    optionsReq.httpMethod = "OPTIONS"
                    optionsReq.setValue("Basic \(encoded)", forHTTPHeaderField: "Authorization")
                    if let (_, optResp) = try? await authSession.data(for: optionsReq),
                       let optHTTP = optResp as? HTTPURLResponse,
                       let allow = optHTTP.value(forHTTPHeaderField: "Allow") ?? optHTTP.value(forHTTPHeaderField: "allow"),
                       allow.contains("MKCOL") {
                        workingBase = candidate
                        break
                    }
                    // If OPTIONS didn't confirm MKCOL, still record as fallback
                    if workingBase == nil {
                        workingBase = candidate
                    }
                }
            } catch let error as CloudProviderError {
                throw error
            } catch {
                continue
            }
        }

        guard let resolvedBase = workingBase else {
            if lastStatusCode == 401 {
                throw CloudProviderError.invalidCredentials
            }
            throw CloudProviderError.serverError(lastStatusCode)
        }
        base = resolvedBase

        webDAVLog.info("[WebDAV] Authenticated as \(username)")
        return WebDAVCredentials(
            serverURL: base,
            username: username,
            password: password,
            displayName: username
        )
    }

    func userDisplayName() -> String {
        credentials.displayName
    }

    // MARK: - File Operations

    func listFolder(path: String) async throws -> [CloudFileItem] {
        let davPath = buildDAVPath(path)
        guard let url = URL(string: davPath) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "PROPFIND"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")
        request.setValue("1", forHTTPHeaderField: "Depth")
        request.setValue("application/xml", forHTTPHeaderField: "Content-Type")
        request.httpBody = Self.propfindBodyXML.data(using: .utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        guard http.statusCode == 207 else {
            webDAVLog.error("[WebDAV] PROPFIND \(path) → HTTP \(http.statusCode)")
            throw Self.mapHTTPError(statusCode: http.statusCode)
        }

        let items = WebDAVResponseParser.parse(data: data, basePath: davBaseURL, requestPath: path)
        // PROPFIND depth 1 includes the folder itself as first entry — skip it
        return items.dropFirst().map { $0 }
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let davPath = buildDAVPath(remotePath)
        guard let url = URL(string: davPath) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")

        let (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let davPath = buildDAVPath(remotePath)
        guard let url = URL(string: davPath) else {
            throw CloudProviderError.invalidResponse
        }

        let fileData = try Data(contentsOf: localURL)
        var request = URLRequest(url: url)
        request.httpMethod = "PUT"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")

        // Many WebDAV servers (Nextcloud, ownCloud, Seafile, …) honor the
        // X-OC-Mtime header to preserve the client's modification time.
        // Servers that don't support it simply ignore it.
        if let modDate = (try? FileManager.default.attributesOfItem(atPath: localURL.path)[.modificationDate]) as? Date {
            request.setValue("\(Int64(modDate.timeIntervalSince1970))", forHTTPHeaderField: "X-OC-Mtime")
        }

        let (data, response) = try await session.uploadReportingProgress(for: request, body: fileData, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            webDAVLog.error("[WebDAV] Upload failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    func deleteItem(at path: String) async throws {
        let davPath = buildDAVPath(path)
        guard let url = URL(string: davPath) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "DELETE"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) || http.statusCode == 204 else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            webDAVLog.error("[WebDAV] DELETE \(path) → HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    func createFolder(at path: String) async throws {
        let davPath = buildDAVPath(path)

        // Try MKCOL with and without trailing slash — some servers require one or the other
        for candidate in [davPath, davPath + "/"] {
            guard let url = URL(string: candidate) else { continue }

            var request = URLRequest(url: url)
            request.httpMethod = "MKCOL"
            request.setValue(authHeader, forHTTPHeaderField: "Authorization")
            request.setValue("0", forHTTPHeaderField: "Content-Length")

            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                throw CloudProviderError.invalidResponse
            }
            if (200...299).contains(http.statusCode) {
                return
            }
            if http.statusCode == 405 || http.statusCode == 301 {
                continue
            }
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            webDAVLog.error("[WebDAV] MKCOL \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http.statusCode)
        }
        // If both variants returned 405, the folder likely already exists
    }

    func renameItem(at path: String, to newName: String) async throws {
        let parentPath = (path as NSString).deletingLastPathComponent
        let destinationPath: String
        if parentPath == "/" {
            destinationPath = "/\(newName)"
        } else {
            destinationPath = "\(parentPath)/\(newName)"
        }

        let sourceDavPath = buildDAVPath(path)
        let destDavPath = buildDAVPath(destinationPath)

        guard let url = URL(string: sourceDavPath) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "MOVE"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")
        request.setValue(destDavPath, forHTTPHeaderField: "Destination")
        request.setValue("F", forHTTPHeaderField: "Overwrite")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) || http.statusCode == 201 else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            webDAVLog.error("[WebDAV] MOVE \(path) → HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    func getFileInfo(at path: String) async throws -> CloudFileItem {
        let davPath = buildDAVPath(path)
        guard let url = URL(string: davPath) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "PROPFIND"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")
        request.setValue("0", forHTTPHeaderField: "Depth")
        request.setValue("application/xml", forHTTPHeaderField: "Content-Type")
        request.httpBody = Self.propfindBodyXML.data(using: .utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 207 else {
            let http = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }

        let items = WebDAVResponseParser.parse(data: data, basePath: davBaseURL, requestPath: path)
        guard let item = items.first else {
            throw CloudProviderError.notFound(path)
        }
        return item
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

    // MARK: - Search (client-side filtering via recursive PROPFIND)

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
        let searchPath = path ?? "/"
        let allItems = try await listAllRecursively(path: searchPath)
        let lowered = query.lowercased()
        return allItems.filter { $0.name.lowercased().contains(lowered) }
    }

    private func listAllRecursively(path: String) async throws -> [CloudFileItem] {
        let items = try await listFolder(path: path)
        var result = items
        for item in items where item.isDirectory {
            let children = try await listAllRecursively(path: item.path)
            result.append(contentsOf: children)
        }
        return result
    }

    // MARK: - Private

    private func buildDAVPath(_ path: String) -> String {
        let cleanPath = path.hasPrefix("/") ? String(path.dropFirst()) : path
        let encoded = cleanPath
            .split(separator: "/", omittingEmptySubsequences: true)
            .map { $0.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? String($0) }
            .joined(separator: "/")
        if encoded.isEmpty {
            return "\(davBaseURL)/"
        }
        return "\(davBaseURL)/\(encoded)"
    }

    private static let propfindBodyXML = """
        <?xml version="1.0" encoding="UTF-8"?>
        <d:propfind xmlns:d="DAV:">
            <d:prop>
                <d:getlastmodified/>
                <d:getcontentlength/>
                <d:getcontenttype/>
                <d:resourcetype/>
                <d:displayname/>
            </d:prop>
        </d:propfind>
        """

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
