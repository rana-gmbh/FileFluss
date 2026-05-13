import Foundation
import os

private let seafileLog = Logger(subsystem: "com.rana.FileFluss", category: "seafile")

/// Persisted credentials for a Seafile account. The auth token is long-lived
/// (Seafile's `/api2/auth-token/` returns a token that doesn't expire on a
/// fixed clock — it's only invalidated by the server admin or a password
/// change). The username is stored alongside purely so we can render a
/// helpful display name; only `token` matters for API calls.
struct SeafileCredentials: Codable, Sendable {
    let serverURL: String
    let username: String
    let token: String
    /// When true, the URLSession used to talk to this server is configured
    /// with a trust delegate that accepts any certificate the server
    /// presents (limited to that exact host). Required for self-hosted
    /// Seafile boxes with a self-signed or LAN-only certificate. Backwards-
    /// compatible default is `false` so accounts saved before this option
    /// existed keep their original strict behaviour.
    var allowSelfSignedCertificate: Bool = false

    private enum CodingKeys: String, CodingKey {
        case serverURL, username, token, allowSelfSignedCertificate
    }

    init(serverURL: String, username: String, token: String, allowSelfSignedCertificate: Bool = false) {
        self.serverURL = serverURL
        self.username = username
        self.token = token
        self.allowSelfSignedCertificate = allowSelfSignedCertificate
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        serverURL = try c.decode(String.self, forKey: .serverURL)
        username = try c.decode(String.self, forKey: .username)
        token = try c.decode(String.self, forKey: .token)
        allowSelfSignedCertificate = (try? c.decode(Bool.self, forKey: .allowSelfSignedCertificate)) ?? false
    }
}

/// Wire model for `/api2/repos/` entries.
private struct SeafileLibrary: Decodable, Sendable {
    let id: String
    let name: String
    let owner: String?
    let encrypted: Bool?
    let mtime: Int64?
    let size: Int64?
}

/// Wire model for `/api2/repos/{id}/dir/` entries.
private struct SeafileDirEntry: Decodable, Sendable {
    let type: String          // "file" or "dir"
    let id: String            // SHA1 of file content / dir tree hash
    let name: String
    let size: Int64?
    let mtime: Int64?
    let modifier_email: String?
}

actor SeafileAPIClient {
    private(set) var credentials: SeafileCredentials
    private let session: URLSession

    /// Cache mapping library display name → repo id. Built lazily from
    /// `listLibraries()` so a path like `/<library-name>/sub/path` can be
    /// resolved without an extra round trip per navigation step.
    private var libraryIdByName: [String: String] = [:]
    private var lastLibraryListAt: Date = .distantPast

    init(credentials: SeafileCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 600
        if credentials.allowSelfSignedCertificate,
           let host = Self.host(from: credentials.serverURL) {
            self.session = URLSession(
                configuration: config,
                delegate: SeafileTrustingDelegate(host: host),
                delegateQueue: nil
            )
        } else {
            self.session = URLSession(configuration: config)
        }
    }

    private static func host(from urlString: String) -> String? {
        URL(string: urlString)?.host
    }

    /// Seafile's `/upload-link/` and `/file/` endpoints return a URL whose
    /// scheme/host/port reflect the server's configured `FILE_SERVER_ROOT`
    /// setting — which is frequently misaligned with how the user actually
    /// reaches the server (e.g. server admin set FILE_SERVER_ROOT to https
    /// while the box only listens on http, or vice versa, or the configured
    /// hostname isn't reachable from the client network).
    ///
    /// Trust the user's typed Server URL instead: replace scheme + host +
    /// port with what they entered, keeping the path + query (which contain
    /// the session token Seafile actually validates). This makes uploads
    /// reach the same machine the listing/auth calls already work against.
    private func rewriteToServer(_ raw: String) -> URL? {
        var trimmed = raw
        if trimmed.hasPrefix("\""), trimmed.hasSuffix("\"") {
            trimmed = String(trimmed.dropFirst().dropLast())
        }
        guard let original = URLComponents(string: trimmed),
              let server = URLComponents(string: credentials.serverURL)
        else { return URL(string: trimmed) }

        var rewritten = original
        rewritten.scheme = server.scheme
        rewritten.host = server.host
        rewritten.port = server.port
        return rewritten.url
    }

    // MARK: - Authentication

    /// Exchange username + password (+ optional 2FA token) for a long-lived
    /// auth token via `POST /api2/auth-token/`. Returns the token together
    /// with the trimmed server URL — never the password.
    static func obtainToken(
        serverURL: String,
        username: String,
        password: String,
        otp: String?,
        allowSelfSignedCertificate: Bool
    ) async throws -> SeafileCredentials {
        let normalizedServer = normalizeServerURL(serverURL)
        guard let url = URL(string: "\(normalizedServer)/api2/auth-token/") else {
            throw CloudProviderError.commandFailed("Server URL is not a valid URL.")
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        if let otp, !otp.isEmpty {
            request.setValue(otp, forHTTPHeaderField: "X-SEAFILE-OTP")
        }

        let encode: (String) -> String = { $0.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? $0 }
        let body = "username=\(encode(username))&password=\(encode(password))"
        request.httpBody = body.data(using: .utf8)

        // Match the live session's trust behaviour for the auth call too,
        // otherwise a self-hosted server with a self-signed certificate would
        // fail the very first request before the user ever sees the file
        // listing. Restricted to the entered host so we don't grant trust
        // beyond what the user explicitly authorised.
        let authSession: URLSession
        if allowSelfSignedCertificate, let host = URL(string: normalizedServer)?.host {
            authSession = URLSession(
                configuration: .default,
                delegate: SeafileTrustingDelegate(host: host),
                delegateQueue: nil
            )
        } else {
            authSession = URLSession.shared
        }

        let (data, response) = try await authSession.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        // Two-factor required: server returns 403 with a header we can
        // surface to the user so the add-account form can re-prompt for the
        // OTP without making them retype the password.
        if http.statusCode == 403,
           http.value(forHTTPHeaderField: "X-SEAFILE-OTP") == "required" {
            throw CloudProviderError.twoFactorRequired
        }

        guard (200...299).contains(http.statusCode) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            seafileLog.error("[Seafile] auth-token failed: HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            throw CloudProviderError.invalidCredentials
        }

        struct TokenResponse: Decodable { let token: String }
        let parsed = try JSONDecoder().decode(TokenResponse.self, from: data)
        return SeafileCredentials(
            serverURL: normalizedServer,
            username: username,
            token: parsed.token,
            allowSelfSignedCertificate: allowSelfSignedCertificate
        )
    }

    /// Verify the stored token still works by hitting `/api2/server-info/`
    /// (a lightweight read-only endpoint that requires auth). The cloud
    /// panel's "Sign In Again" flow runs this before listing.
    func verifyToken() async throws {
        var request = try authenticatedRequest(path: "/api2/server-info/")
        request.httpMethod = "GET"
        let (_, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.notAuthenticated
        }
    }

    func userDisplayName() async -> String {
        credentials.username
    }

    // MARK: - URL helpers

    /// Drop trailing slash + protocol-default any missing scheme so the
    /// rest of the file can concatenate paths without worrying about edge
    /// cases. `example.com` becomes `https://example.com`.
    private static func normalizeServerURL(_ raw: String) -> String {
        var s = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if s.hasSuffix("/") { s.removeLast() }
        if !s.lowercased().hasPrefix("http://") && !s.lowercased().hasPrefix("https://") {
            s = "https://\(s)"
        }
        return s
    }

    private func authenticatedRequest(path: String) throws -> URLRequest {
        guard let url = URL(string: "\(credentials.serverURL)\(path)") else {
            throw CloudProviderError.invalidResponse
        }
        var request = URLRequest(url: url)
        request.setValue("Token \(credentials.token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        return request
    }

    // MARK: - Path model
    //
    // The Seafile API is library-scoped: a path always lives under a specific
    // repo-id. The rest of FileFluss expects a single `/` root per cloud
    // account, so the API client maps:
    //
    //   /                         → list of libraries (rendered as folders)
    //   /<library-name>           → root of that library
    //   /<library-name>/foo/bar   → /foo/bar inside that library
    //
    // `resolveLibrary(forPath:)` returns `(repoId, pathInsideRepo)` for any
    // panel-level path. When two libraries share a name, the on-demand
    // collision suffix `<name> (<id-prefix>)` keeps them distinguishable.

    private func refreshLibraryCacheIfStale() async throws -> [SeafileLibrary] {
        // 30-second TTL is enough to debounce navigation in the same folder
        // tree while still picking up newly-created libraries quickly. Listing
        // libraries is cheap server-side but we shouldn't hammer it.
        if Date().timeIntervalSince(lastLibraryListAt) < 30 && !libraryIdByName.isEmpty {
            return try await fetchLibraries(forceCacheUpdate: false)
        }
        return try await fetchLibraries(forceCacheUpdate: true)
    }

    private func fetchLibraries(forceCacheUpdate: Bool) async throws -> [SeafileLibrary] {
        var request = try authenticatedRequest(path: "/api2/repos/")
        request.httpMethod = "GET"
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
        let libs = try JSONDecoder().decode([SeafileLibrary].self, from: data)
        if forceCacheUpdate {
            libraryIdByName = Self.buildNameIndex(libraries: libs)
            lastLibraryListAt = Date()
        }
        return libs
    }

    /// Build a {displayName → id} index. Collisions get an `(id-prefix)`
    /// suffix so each library is addressable by a unique folder name in the
    /// panel UI. The same disambiguation is applied by `displayName(for:)`
    /// when we render the listing.
    private static func buildNameIndex(libraries: [SeafileLibrary]) -> [String: String] {
        var counts: [String: Int] = [:]
        for lib in libraries { counts[lib.name, default: 0] += 1 }
        var index: [String: String] = [:]
        for lib in libraries {
            let key = (counts[lib.name] ?? 0) > 1
                ? "\(lib.name) (\(lib.id.prefix(6)))"
                : lib.name
            index[key] = lib.id
        }
        return index
    }

    private static func displayName(for lib: SeafileLibrary, allCountsByName counts: [String: Int]) -> String {
        if (counts[lib.name] ?? 0) > 1 {
            return "\(lib.name) (\(lib.id.prefix(6)))"
        }
        return lib.name
    }

    /// Resolve a panel-level path into the underlying (repoId, in-repo path).
    /// Returns nil for the root path so callers know to render the library
    /// list instead of issuing a `/dir/` call against a specific repo.
    private func resolveLibrary(forPath path: String) async throws -> (repoId: String, innerPath: String)? {
        let trimmed = path.hasPrefix("/") ? String(path.dropFirst()) : path
        if trimmed.isEmpty { return nil }

        let firstSlash = trimmed.firstIndex(of: "/")
        let libraryKey = firstSlash.map { String(trimmed[..<$0]) } ?? trimmed
        let inner = firstSlash.map { String(trimmed[trimmed.index(after: $0)...]) } ?? ""

        if let id = libraryIdByName[libraryKey] {
            return (id, "/" + inner)
        }
        // Fall through: refresh the cache and try again. Handles the case
        // where the library was created on the server after the last cache
        // refresh.
        _ = try await refreshLibraryCacheIfStale()
        if let id = libraryIdByName[libraryKey] {
            return (id, "/" + inner)
        }
        throw CloudProviderError.notFound("Library \(libraryKey)")
    }

    // MARK: - Directory listing

    func listFolder(path: String) async throws -> [CloudFileItem] {
        if let resolved = try await resolveLibrary(forPath: path) {
            return try await listLibraryPath(repoId: resolved.repoId, innerPath: resolved.innerPath, parentPath: path)
        }
        // Root: list libraries as top-level folders.
        let libs = try await refreshLibraryCacheIfStale()
        let counts = libs.reduce(into: [String: Int]()) { $0[$1.name, default: 0] += 1 }
        return libs.map { lib in
            let name = Self.displayName(for: lib, allCountsByName: counts)
            return CloudFileItem(
                id: "lib:\(lib.id)",
                name: name,
                path: "/\(name)",
                isDirectory: true,
                size: lib.size ?? 0,
                modificationDate: lib.mtime.map { Date(timeIntervalSince1970: TimeInterval($0)) } ?? .distantPast,
                checksum: nil
            )
        }
    }

    private func listLibraryPath(repoId: String, innerPath: String, parentPath: String) async throws -> [CloudFileItem] {
        let encodedPath = innerPath.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? innerPath
        var request = try authenticatedRequest(path: "/api2/repos/\(repoId)/dir/?p=\(encodedPath)")
        request.httpMethod = "GET"
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0, responseBody: data)
        }
        let entries = try JSONDecoder().decode([SeafileDirEntry].self, from: data)
        let parentBase = parentPath.hasSuffix("/") ? String(parentPath.dropLast()) : parentPath
        return entries.map { entry in
            let isDir = entry.type == "dir"
            let itemPath = parentBase.isEmpty ? "/\(entry.name)" : "\(parentBase)/\(entry.name)"
            return CloudFileItem(
                id: "\(isDir ? "d" : "f"):\(repoId):\(itemPath)",
                name: entry.name,
                path: itemPath,
                isDirectory: isDir,
                size: entry.size ?? 0,
                modificationDate: entry.mtime.map { Date(timeIntervalSince1970: TimeInterval($0)) } ?? .distantPast,
                checksum: isDir ? nil : entry.id
            )
        }
    }

    // MARK: - File operations

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        guard let resolved = try await resolveLibrary(forPath: remotePath) else {
            throw CloudProviderError.notFound(remotePath)
        }
        let encodedInner = resolved.innerPath.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? resolved.innerPath

        // Step 1: ask Seafile for a short-lived signed download URL.
        var linkRequest = try authenticatedRequest(path: "/api2/repos/\(resolved.repoId)/file/?p=\(encodedInner)")
        linkRequest.httpMethod = "GET"
        let (linkData, linkResponse) = try await session.data(for: linkRequest)
        guard let http = linkResponse as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = linkResponse as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0, responseBody: linkData)
        }
        // Response is the URL itself (JSON-encoded string, double-quoted).
        let raw = String(data: linkData, encoding: .utf8) ?? ""
        guard let downloadURL = rewriteToServer(raw) else {
            throw CloudProviderError.invalidResponse
        }

        // Step 2: stream the bytes to disk via the shared progress-reporting
        // delegate so the transfer footer updates as the file arrives.
        let getRequest = URLRequest(url: downloadURL)
        let (tempURL, response) = try await session.downloadReportingProgress(for: getRequest, onBytes: onBytes)
        guard let http2 = response as? HTTPURLResponse, (200...299).contains(http2.statusCode) else {
            let http2 = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http2?.statusCode ?? 0)
        }
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        guard let resolved = try await resolveLibrary(forPath: remotePath) else {
            throw CloudProviderError.commandFailed("Cannot upload directly to the Seafile library root — pick a library first.")
        }
        let parentInner = (resolved.innerPath as NSString).deletingLastPathComponent
        let fileName = (resolved.innerPath as NSString).lastPathComponent
        let parentEncoded = (parentInner.isEmpty ? "/" : parentInner).addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? "/"

        // Step 1: get a one-shot upload URL.
        var linkRequest = try authenticatedRequest(path: "/api2/repos/\(resolved.repoId)/upload-link/?p=\(parentEncoded)")
        linkRequest.httpMethod = "GET"
        let (linkData, linkResponse) = try await session.data(for: linkRequest)
        guard let http = linkResponse as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = linkResponse as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0, responseBody: linkData)
        }
        let raw = String(data: linkData, encoding: .utf8) ?? ""
        guard let uploadURL = rewriteToServer(raw) else {
            throw CloudProviderError.invalidResponse
        }

        // Step 2: POST the file as multipart/form-data. Seafile expects:
        //   - parent_dir         the in-repo directory path (form field)
        //   - replace=1          for overwrite semantics (we always replace
        //                         since FileFluss conflict-resolution has
        //                         already decided to do so by the time we
        //                         land here)
        //   - file               the file bytes
        let boundary = "Boundary-\(UUID().uuidString)"
        var body = Data()
        let lineBreak = "\r\n"

        func appendField(name: String, value: String) {
            body.append("--\(boundary)\(lineBreak)".data(using: .utf8)!)
            body.append("Content-Disposition: form-data; name=\"\(name)\"\(lineBreak)\(lineBreak)".data(using: .utf8)!)
            body.append("\(value)\(lineBreak)".data(using: .utf8)!)
        }

        appendField(name: "parent_dir", value: parentInner.isEmpty ? "/" : parentInner)
        appendField(name: "replace", value: "1")

        body.append("--\(boundary)\(lineBreak)".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"file\"; filename=\"\(fileName)\"\(lineBreak)".data(using: .utf8)!)
        body.append("Content-Type: application/octet-stream\(lineBreak)\(lineBreak)".data(using: .utf8)!)
        let fileData = try Data(contentsOf: localURL)
        body.append(fileData)
        body.append(lineBreak.data(using: .utf8)!)
        body.append("--\(boundary)--\(lineBreak)".data(using: .utf8)!)

        var uploadRequest = URLRequest(url: uploadURL)
        uploadRequest.httpMethod = "POST"
        uploadRequest.setValue("Token \(credentials.token)", forHTTPHeaderField: "Authorization")
        uploadRequest.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

        let (respData, response) = try await session.uploadReportingProgress(for: uploadRequest, body: body, onBytes: onBytes)
        guard let http2 = response as? HTTPURLResponse, (200...299).contains(http2.statusCode) else {
            let http2 = response as? HTTPURLResponse
            seafileLog.error("[Seafile] Upload failed: HTTP \(http2?.statusCode ?? 0): \(String(data: respData, encoding: .utf8)?.prefix(500) ?? "")")
            throw Self.mapHTTPError(statusCode: http2?.statusCode ?? 0, responseBody: respData)
        }
    }

    func deleteItem(at path: String) async throws {
        guard let resolved = try await resolveLibrary(forPath: path) else {
            // Deleting a whole library is destructive enough that we leave
            // it to the Seafile web UI rather than expose it accidentally
            // through the panel's Delete key.
            throw CloudProviderError.commandFailed("Cannot delete a Seafile library from FileFluss. Use the Seafile web UI.")
        }
        let encodedInner = resolved.innerPath.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? resolved.innerPath
        let isDir = await isDirectory(repoId: resolved.repoId, innerPath: resolved.innerPath)
        let endpoint = isDir
            ? "/api2/repos/\(resolved.repoId)/dir/?p=\(encodedInner)"
            : "/api2/repos/\(resolved.repoId)/file/?p=\(encodedInner)"
        var request = try authenticatedRequest(path: endpoint)
        request.httpMethod = "DELETE"
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0, responseBody: data)
        }
    }

    func createFolder(at path: String) async throws {
        guard let resolved = try await resolveLibrary(forPath: path) else {
            throw CloudProviderError.commandFailed("Create a library in the Seafile web UI before adding folders.")
        }
        let encodedInner = resolved.innerPath.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? resolved.innerPath
        var request = try authenticatedRequest(path: "/api2/repos/\(resolved.repoId)/dir/?p=\(encodedInner)")
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.httpBody = "operation=mkdir".data(using: .utf8)
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0, responseBody: data)
        }
    }

    func renameItem(at path: String, to newName: String) async throws {
        guard let resolved = try await resolveLibrary(forPath: path), !resolved.innerPath.isEmpty, resolved.innerPath != "/" else {
            throw CloudProviderError.commandFailed("Rename a Seafile library from its web UI.")
        }
        let encodedInner = resolved.innerPath.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? resolved.innerPath
        let isDir = await isDirectory(repoId: resolved.repoId, innerPath: resolved.innerPath)
        let endpoint = isDir
            ? "/api2/repos/\(resolved.repoId)/dir/?p=\(encodedInner)"
            : "/api2/repos/\(resolved.repoId)/file/?p=\(encodedInner)"
        var request = try authenticatedRequest(path: endpoint)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        let encode: (String) -> String = { $0.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? $0 }
        let body = isDir
            ? "operation=rename&newname=\(encode(newName))"
            : "operation=rename&newname=\(encode(newName))"
        request.httpBody = body.data(using: .utf8)
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0, responseBody: data)
        }
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        guard let resolved = try await resolveLibrary(forPath: path) else {
            throw CloudProviderError.commandFailed("Library root has no file metadata.")
        }
        let encodedInner = resolved.innerPath.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? resolved.innerPath

        // Probe as file first; on 404 try as directory. Seafile has no
        // single "stat" endpoint that returns both file/dir info — these are
        // separate API surfaces, so the cheapest correct thing is to ask the
        // file endpoint first since uploads/downloads are file-only paths.
        var request = try authenticatedRequest(path: "/api2/repos/\(resolved.repoId)/file/detail/?p=\(encodedInner)")
        request.httpMethod = "GET"
        let (data, response) = try await session.data(for: request)
        if let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) {
            struct FileDetail: Decodable {
                let id: String
                let mtime: Int64
                let type: String
                let name: String
                let size: Int64
            }
            let detail = try JSONDecoder().decode(FileDetail.self, from: data)
            return CloudFileItem(
                id: "f:\(resolved.repoId):\(path)",
                name: detail.name,
                path: path,
                isDirectory: false,
                size: detail.size,
                modificationDate: Date(timeIntervalSince1970: TimeInterval(detail.mtime)),
                checksum: detail.id
            )
        }
        let parentInner = (resolved.innerPath as NSString).deletingLastPathComponent
        let entries = try await listLibraryPath(repoId: resolved.repoId, innerPath: parentInner.isEmpty ? "/" : parentInner, parentPath: (path as NSString).deletingLastPathComponent)
        let name = (path as NSString).lastPathComponent
        if let match = entries.first(where: { $0.name == name }) { return match }
        throw CloudProviderError.notFound(path)
    }

    func folderSize(at path: String) async throws -> Int64 {
        let entries = try await listFolder(path: path)
        var total: Int64 = 0
        for entry in entries {
            if entry.isDirectory {
                total += try await folderSize(at: entry.path)
            } else {
                total += entry.size
            }
        }
        return total
    }

    private func isDirectory(repoId: String, innerPath: String) async -> Bool {
        let parent = (innerPath as NSString).deletingLastPathComponent
        let name = (innerPath as NSString).lastPathComponent
        let encoded = (parent.isEmpty ? "/" : parent).addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? "/"
        guard var request = try? authenticatedRequest(path: "/api2/repos/\(repoId)/dir/?p=\(encoded)") else { return false }
        request.httpMethod = "GET"
        guard let (data, response) = try? await session.data(for: request),
              let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode),
              let entries = try? JSONDecoder().decode([SeafileDirEntry].self, from: data)
        else { return false }
        return entries.first(where: { $0.name == name })?.type == "dir"
    }

    // MARK: - Errors

    // MARK: - Self-signed cert support

    /// Trusts the certificate presented by the configured Seafile host even
    /// when it doesn't chain to a system-trusted CA. Used only when the user
    /// opts in via the "Allow self-signed certificate" checkbox. Scope is
    /// restricted to the exact host so other connections still go through
    /// the system trust store.
    final class SeafileTrustingDelegate: NSObject, URLSessionDelegate, URLSessionTaskDelegate, URLSessionDownloadDelegate, @unchecked Sendable {
        let host: String
        init(host: String) { self.host = host }

        func urlSession(
            _ session: URLSession,
            didReceive challenge: URLAuthenticationChallenge,
            completionHandler: @escaping @Sendable (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
        ) {
            guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
                  challenge.protectionSpace.host == host,
                  let trust = challenge.protectionSpace.serverTrust
            else {
                completionHandler(.performDefaultHandling, nil)
                return
            }
            completionHandler(.useCredential, URLCredential(trust: trust))
        }

        func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {}
    }

    // MARK: - Errors

    private static func mapHTTPError(statusCode: Int, responseBody: Data? = nil) -> CloudProviderError {
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
