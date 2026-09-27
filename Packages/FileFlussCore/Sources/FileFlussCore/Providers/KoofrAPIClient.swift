import Foundation
import os

private let koofrLog = Logger(subsystem: "com.rana.FileFluss", category: "koofr")

public struct KoofrCredentials: Codable, Sendable {
    let email: String
    let appPassword: String
    let primaryMountId: String
    let displayName: String
}

public actor KoofrAPIClient {
    let credentials: KoofrCredentials
    private let session: URLSession
    private let baseURL = "https://app.koofr.net/api/v2"
    private let contentURL = "https://app.koofr.net/content/api/v2"

    public init(credentials: KoofrCredentials) {
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

    public static func authenticate(email: String, appPassword: String) async throws -> KoofrCredentials {
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

    public func userDisplayName() -> String {
        credentials.displayName
    }

    /// Koofr `/mounts/{mountId}` carries `spaceTotal` and `spaceUsed`
    /// for the primary mount. Free Koofr accounts get a configured
    /// quota; tier upgrades raise it. spaceTotal == 0 (rare —
    /// happens on disabled mounts) returns nil.
    public func storageQuota() async throws -> CloudStorageQuota? {
        struct MountDetail: Decodable {
            let spaceTotal: Int64?
            let spaceUsed: Int64?
        }
        let mount: MountDetail = try await request(
            .get,
            path: "/mounts/\(mountId)"
        )
        let total = (mount.spaceTotal ?? 0) > 0 ? mount.spaceTotal : nil
        return CloudStorageQuota(usedBytes: mount.spaceUsed ?? 0, totalBytes: total)
    }

    // MARK: - File Operations

    public func listFolder(path: String) async throws -> [CloudFileItem] {
        let encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? path
        let response: KoofrFilesResponse = try await request(
            .get,
            path: "/mounts/\(mountId)/files/list",
            queryString: "path=\(encodedPath)"
        )
        return response.files.map { $0.toCloudFileItem(parentPath: path) }
    }

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
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

    public func uploadFile(from localURL: URL, toFolder folderPath: String, fileName: String) async throws {
        try await uploadFile(from: localURL, toFolder: folderPath, fileName: fileName, onBytes: nil)
    }

    public func uploadFile(from localURL: URL, toFolder folderPath: String, fileName: String, onBytes: ByteProgressHandler?) async throws {
        let encodedPath = folderPath.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? folderPath
        let encodedName = fileName.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? fileName
        var urlString = "\(contentURL)/mounts/\(mountId)/files/put?path=\(encodedPath)&filename=\(encodedName)&autorename=false&overwrite=true"

        // Koofr's put endpoint takes `modified` in milliseconds since the
        // Unix epoch to preserve the client's modification time.
        if let modDate = (try? FileManager.default.attributesOfItem(atPath: localURL.path)[.modificationDate]) as? Date {
            urlString += "&modified=\(Int64(modDate.timeIntervalSince1970 * 1000))"
        }

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

    public func deleteItem(at path: String) async throws {
        let encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? path
        try await requestVoid(.delete, path: "/mounts/\(mountId)/files/remove", queryString: "path=\(encodedPath)")
    }

    public func createFolder(parentPath: String, name: String) async throws {
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

    public func renameItem(at path: String, to newName: String) async throws {
        let encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? path
        struct RenameBody: Encodable { let name: String }
        try await requestVoidWithBody(
            .put,
            path: "/mounts/\(mountId)/files/rename",
            queryString: "path=\(encodedPath)",
            body: RenameBody(name: newName)
        )
    }

    public func getFileInfo(at path: String) async throws -> CloudFileItem {
        let encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? path
        let info: KoofrFileInfo = try await request(
            .get,
            path: "/mounts/\(mountId)/files/info",
            queryString: "path=\(encodedPath)"
        )
        let parentPath = (path as NSString).deletingLastPathComponent
        return info.toCloudFileItem(parentPath: parentPath)
    }

    public func folderSize(path: String) async throws -> Int64 {
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

    // MARK: - Share links
    //
    // Koofr's published API reference doesn't document link sharing at all.
    // The endpoints below are the ones rclone's "koofr" backend has shipped
    // for years (`/mounts/{mountId}/links`), so they're treated as observed
    // behaviour rather than a contract.
    //
    //   POST   /mounts/{mountId}/links        {"path":"/full/path"}  → link
    //   GET    /mounts/{mountId}/links                               → all links
    //   DELETE /mounts/{mountId}/links/{id}                          → unshare
    //
    // There is no create-time password, expiry or download toggle: the body
    // carries the path and nothing else. `PUT …/links/{id}` appears to exist
    // but rclone doesn't use it and its fields are unverified, so updating a
    // link is deliberately left unimplemented (`canUpdate` is false).

    /// One entry of the account's link list, and the body `POST …/links`
    /// answers with. Everything but `id`/`path`/`url` is optional: the shape
    /// is undocumented, and a missing field must not sink the whole lookup.
    private struct KoofrLink: Decodable {
        let id: String
        let path: String
        let url: String
        let shortUrl: String?
        let hasPassword: Bool?
        /// Milliseconds since the Unix epoch, as everywhere else in this API.
        /// Absent or 0 means the link never expires.
        let validTo: Int64?
    }

    /// `GET …/links` answers `{"links":[…]}`. A bare array is accepted too,
    /// since the endpoint is undocumented; anything else throws rather than
    /// decoding to an empty list, which would masquerade as "not shared".
    private struct KoofrLinksResponse: Decodable {
        let links: [KoofrLink]

        private enum CodingKeys: String, CodingKey { case links }

        init(from decoder: Decoder) throws {
            if let keyed = try? decoder.container(keyedBy: CodingKeys.self) {
                links = try keyed.decode([KoofrLink].self, forKey: .links)
            } else {
                links = try [KoofrLink](from: decoder)
            }
        }
    }

    /// Creates a public link for `path`.
    ///
    /// `options` is accepted and ignored — the create body has no password,
    /// expiry or download field, and `shareLinkCapabilities` advertises none
    /// of them, so the UI never offers those controls in the first place.
    /// Sharing can still be refused account-side, so the request surfaces
    /// Koofr's own message.
    public func createShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        struct CreateBody: Encodable { let path: String }
        let link: KoofrLink = try await request(
            .post,
            path: "/mounts/\(mountId)/links",
            body: CreateBody(path: path),
            surfaceServerMessage: true
        )
        return try await shareLink(from: link)
    }

    /// The public link this path already has, or nil when it has none.
    ///
    /// Koofr has no per-path link lookup, so the account's whole link list is
    /// fetched and matched on `path`. No match is the "not shared" answer and
    /// returns nil; an HTTP failure still throws, so a refused or broken
    /// request never reads as "this file isn't shared".
    public func existingShareLink(at path: String) async throws -> CloudShareLink? {
        guard let link = try await findLink(at: path) else { return nil }
        return try await shareLink(from: link)
    }

    /// Withdraws the public link. Deliberately not an error when the path has
    /// no link: the caller's goal — "this file is not shared" — already holds.
    public func removeShareLink(at path: String) async throws {
        guard let link = try await findLink(at: path) else {
            koofrLog.info("[Koofr] removeShareLink: no link on that path, nothing to withdraw")
            return
        }
        try await requestVoid(
            .delete,
            path: "/mounts/\(mountId)/links/\(link.id)",
            surfaceServerMessage: true
        )
    }

    private func findLink(at path: String) async throws -> KoofrLink? {
        let response: KoofrLinksResponse = try await request(
            .get,
            path: "/mounts/\(mountId)/links",
            surfaceServerMessage: true
        )
        let wanted = Self.normalizedLinkPath(path)
        return response.links.first { Self.normalizedLinkPath($0.path) == wanted }
    }

    private func shareLink(from link: KoofrLink) async throws -> CloudShareLink {
        // `shortUrl` is the same landing page behind a shorter host; prefer the
        // canonical `url` and only fall back when the primary one is unusable.
        guard let landing = URL(string: link.url) ?? link.shortUrl.flatMap({ URL(string: $0) }) else {
            throw CloudProviderError.invalidResponse
        }
        let expiry = link.validTo.flatMap {
            $0 > 0 ? Date(timeIntervalSince1970: TimeInterval($0) / 1000.0) : nil
        }
        return CloudShareLink(
            url: landing,
            directDownloadURL: await directContentURL(for: link),
            expiresAt: expiry,
            // Nothing here can set a password, but a link made in Koofr's web
            // UI can carry one — report what the server says, not what we asked.
            hasPassword: link.hasPassword ?? false
        )
    }

    /// Koofr's direct-content URL for a link: the landing URL with `/links`
    /// swapped for `/content/links` and the file-fetch suffix appended. The
    /// `path` parameter has to be exactly `%2F` — any other value answers 404.
    ///
    /// Per rclone's backend this serves the file itself *only* for an item
    /// sitting in the mount root. Deeper in the tree Koofr answers with a ZIP
    /// containing the single member, and a folder link is a ZIP by definition —
    /// so both get no direct URL at all. Handing someone a ZIP when they
    /// expect the file is worse than handing them the landing page.
    private func directContentURL(for link: KoofrLink) async -> URL? {
        let path = Self.normalizedLinkPath(link.path)
        let parent = (path as NSString).deletingLastPathComponent
        guard parent.isEmpty || parent == "/" else { return nil }
        // One extra request, and only for root-level items: everything deeper
        // has already returned above. Anything we can't confirm to be a file
        // stays nil rather than risking the ZIP.
        guard let info = try? await getFileInfo(at: path), !info.isDirectory else { return nil }
        guard let range = link.url.range(of: "/links") else { return nil }
        let content = link.url.replacingCharacters(in: range, with: "/content/links")
        return URL(string: content + "/files/get?path=%2F")
    }

    /// Link paths come back exactly as they were submitted, so they're compared
    /// normalised: a trailing slash or a missing leading one would otherwise
    /// hide an existing link.
    private static func normalizedLinkPath(_ path: String) -> String {
        var p = path.hasPrefix("/") ? path : "/\(path)"
        while p.count > 1 && p.hasSuffix("/") { p.removeLast() }
        return p
    }

    /// Koofr's failure bodies are JSON, and the shape is undocumented: the
    /// human-readable text has been seen both at the top level
    /// (`{"error":"…"}`) and nested (`{"error":{"message":"…"}}`). Both are
    /// tried; anything else falls back to the coarse status mapping.
    private static func serverMessage(from data: Data) -> String? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return nil
        }
        if let message = object["message"] as? String, !message.isEmpty { return message }
        if let error = object["error"] as? String, !error.isEmpty { return error }
        if let error = object["error"] as? [String: Any] {
            if let message = error["message"] as? String, !message.isEmpty { return message }
            if let code = error["code"] as? String, !code.isEmpty { return code }
        }
        return nil
    }

    // MARK: - HTTP

    private enum HTTPMethod: String {
        case get = "GET"
        case post = "POST"
        case put = "PUT"
        case delete = "DELETE"
    }

    /// - Parameter surfaceServerMessage: when true, a failure whose body
    ///   carries a human-readable message throws `.commandFailed` with that
    ///   text instead of the coarse mapping below. Used by the sharing path,
    ///   where the only useful explanation ("link sharing isn't available on
    ///   this plan") is server-side. Off by default so existing callers keep
    ///   their established error semantics.
    private func request<T: Decodable>(_ method: HTTPMethod, path: String, queryString: String = "", body: (any Encodable)? = nil, surfaceServerMessage: Bool = false) async throws -> T {
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
            if surfaceServerMessage, let message = Self.serverMessage(from: data) {
                throw CloudProviderError.commandFailed(message)
            }
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

    /// - Parameter surfaceServerMessage: see `request(_:path:queryString:body:surfaceServerMessage:)`.
    private func requestVoid(_ method: HTTPMethod, path: String, queryString: String = "", surfaceServerMessage: Bool = false) async throws {
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
            if surfaceServerMessage, let message = Self.serverMessage(from: data) {
                throw CloudProviderError.commandFailed(message)
            }
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

    public func toCloudFileItem(parentPath: String) -> CloudFileItem {
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
