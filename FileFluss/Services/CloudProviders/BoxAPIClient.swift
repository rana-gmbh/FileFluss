import AppKit
import Foundation
import Network
import os
import Security
import CommonCrypto

private let boxLog = Logger(subsystem: "com.rana.FileFluss", category: "box")

struct BoxCredentials: Codable, Sendable {
    let accessToken: String
    let refreshToken: String
    let expiresAt: Date
    let userLogin: String
    let displayName: String
}

actor BoxAPIClient {
    /// OAuth 2.0 credentials registered for FileFluss.
    /// Box uses the confidential-client model — both id and secret are
    /// required for token exchange. Treat the secret as sensitive even
    /// though it ships in the binary (standard practice for desktop apps).
    static let clientId = "kv5fvna9auttv0d2su7ssies972hri96"
    static let clientSecret = "meEDcWLwAphA0UfMaoOfK6MoCztrT2lB"

    /// "Read and write all files and folders" — Box's full file-access
    /// scope. Empty `scope` parameter means the access granted is whatever
    /// the app's configuration allows, which is what we want.
    static let scopes = ""

    private static let authBaseURL = "https://account.box.com/api/oauth2/authorize"
    private static let tokenURL = "https://api.box.com/oauth2/token"
    private static let apiURL = "https://api.box.com/2.0"
    private static let uploadURL = "https://upload.box.com/api/2.0"

    /// Single-PUT cap; Box requires chunked upload (`/files/upload_sessions`)
    /// for anything larger. Not implemented in v1 — the upload pre-flight
    /// in the transfer paths rejects files above this size with a clear
    /// error.
    private static let singlePutMaxBytes: Int64 = 50 * 1024 * 1024

    private(set) var credentials: BoxCredentials
    private let session: URLSession

    /// Path → Box file/folder ID cache. Root is always "0". Walking the
    /// path lazily resolves intermediate folder IDs and caches them so
    /// subsequent operations on the same path are O(1).
    private var pathIdCache: [String: String] = ["/": "0"]

    /// When a token refresh is in flight, concurrent callers must await
    /// the same Task rather than firing parallel POSTs to /oauth2/token.
    /// Box rotates refresh tokens single-use, so a parallel refresh always
    /// fails with invalid_grant → .notAuthenticated. Actor isolation alone
    /// doesn't fix this because every `await` releases the actor.
    private var inflightRefresh: Task<BoxCredentials, Error>?

    init(credentials: BoxCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)
    }

    // MARK: - OAuth2 (Loopback Redirect)

    static func startOAuthFlow() async throws -> BoxCredentials {
        let expectedState = generateState()
        let (port, authCode) = try await listenForAuthCode(expectedState: expectedState)
        return try await exchangeCodeForTokens(code: authCode, redirectPort: port)
    }

    private static func listenForAuthCode(expectedState: String) async throws -> (UInt16, String) {
        let listener = try NWListener(using: .tcp, on: .any)
        let guard_ = BoxContinuationGuard<(UInt16, String)>()

        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<(UInt16, String), Error>) in
            guard_.setContinuation(continuation)

            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    guard let port = listener.port?.rawValue else { return }
                    var components = URLComponents(string: authBaseURL)!
                    components.queryItems = [
                        URLQueryItem(name: "response_type", value: "code"),
                        URLQueryItem(name: "client_id", value: clientId),
                        URLQueryItem(name: "redirect_uri", value: "http://localhost:\(port)"),
                        URLQueryItem(name: "state", value: expectedState),
                    ]
                    if let url = components.url {
                        DispatchQueue.main.async {
                            NSWorkspace.shared.open(url)
                        }
                    }
                case .failed(let error):
                    guard_.resume(throwing: CloudProviderError.networkError(error))
                default:
                    break
                }
            }

            listener.newConnectionHandler = { connection in
                connection.start(queue: .global())
                connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { data, _, _, _ in
                    guard let data, let requestString = String(data: data, encoding: .utf8) else {
                        connection.cancel()
                        return
                    }

                    boxLog.debug("[Box] OAuth callback received: \(requestString.prefix(300))")

                    guard let firstLine = requestString.components(separatedBy: "\r\n").first,
                          let urlPart = firstLine.split(separator: " ").dropFirst().first else {
                        connection.cancel()
                        return
                    }

                    let components = URLComponents(string: "http://localhost\(urlPart)")

                    if let errorParam = components?.queryItems?.first(where: { $0.name == "error" })?.value {
                        boxLog.error("[Box] OAuth error: \(errorParam)")
                        let errorHTML = "<!DOCTYPE html><html><body><h2>Authentication failed</h2><p>\(errorParam)</p><p>You can close this window.</p></body></html>"
                        let response = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: \(errorHTML.utf8.count)\r\nConnection: close\r\n\r\n\(errorHTML)"
                        connection.send(content: response.data(using: .utf8), completion: .contentProcessed { _ in
                            connection.cancel()
                        })
                        listener.cancel()
                        guard_.resume(throwing: CloudProviderError.unauthorized)
                        return
                    }

                    guard let code = components?.queryItems?.first(where: { $0.name == "code" })?.value else {
                        // Favicon, preflight, etc. — ignore and keep listening.
                        boxLog.debug("[Box] Ignoring non-auth request: \(String(urlPart).prefix(100))")
                        let emptyResponse = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        connection.send(content: emptyResponse.data(using: .utf8), completion: .contentProcessed { _ in
                            connection.cancel()
                        })
                        return
                    }

                    // CSRF defence: reject any callback whose `state` doesn't
                    // match the value we sent in the authorize URL.
                    let returnedState = components?.queryItems?.first(where: { $0.name == "state" })?.value
                    if returnedState != expectedState {
                        boxLog.error("[Box] OAuth state mismatch — rejecting callback")
                        let errorHTML = "<!DOCTYPE html><html><body><h2>Authentication failed</h2><p>Invalid state parameter. You can close this window.</p></body></html>"
                        let response = "HTTP/1.1 400 Bad Request\r\nContent-Type: text/html\r\nContent-Length: \(errorHTML.utf8.count)\r\nConnection: close\r\n\r\n\(errorHTML)"
                        connection.send(content: response.data(using: .utf8), completion: .contentProcessed { _ in
                            connection.cancel()
                        })
                        listener.cancel()
                        guard_.resume(throwing: CloudProviderError.unauthorized)
                        return
                    }

                    let successHTML = "<!DOCTYPE html><html><body><h2>Signed in to Box</h2><p>You can close this window and return to FileFluss.</p></body></html>"
                    let response = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: \(successHTML.utf8.count)\r\nConnection: close\r\n\r\n\(successHTML)"
                    connection.send(content: response.data(using: .utf8), completion: .contentProcessed { _ in
                        connection.cancel()
                    })

                    let port = listener.port?.rawValue ?? 0
                    listener.cancel()
                    guard_.resume(returning: (port, code))
                }
            }

            listener.start(queue: .global())

            DispatchQueue.global().asyncAfter(deadline: .now() + 300) {
                listener.cancel()
                guard_.resume(throwing: CloudProviderError.notAuthenticated)
            }
        }
    }

    /// Identify ourselves to Box's edge — the default URLSession UA gets
    /// blocked by their WAF with a generic HTML 403, before the request
    /// ever reaches the OAuth backend.
    private static let userAgent = "FileFluss/1.0 (macOS)"

    private static func exchangeCodeForTokens(code: String, redirectPort: UInt16) async throws -> BoxCredentials {
        let url = URL(string: tokenURL)!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")

        let encode = { (s: String) -> String in
            s.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? s
        }
        let body = [
            "grant_type=authorization_code",
            "code=\(encode(code))",
            "client_id=\(encode(clientId))",
            "client_secret=\(encode(clientSecret))",
            "redirect_uri=\(encode("http://localhost:\(redirectPort)"))",
        ].joined(separator: "&")
        request.httpBody = body.data(using: .utf8)

        boxLog.info("[Box] Token POST → \(tokenURL) bodyLen=\(body.utf8.count)")
        let (data, response) = try await URLSession.shared.data(for: request)
        let bodyStr = String(data: data, encoding: .utf8) ?? ""
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            boxLog.error("[Box] Token exchange failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            // Surface Box's own error_description so the user sees the
            // actual cause (typically "redirect_uri does not match" or
            // "invalid_client") instead of a misleading generic message.
            let detail = parseOAuthError(from: data) ?? bodyStr.prefix(200).description
            throw CloudProviderError.commandFailed("Box authentication failed: \(detail)")
        }

        let tokenResponse = try JSONDecoder().decode(BoxTokenResponse.self, from: data)
        let expiresAt = Date().addingTimeInterval(TimeInterval(tokenResponse.expires_in))
        let user = try await fetchUserInfo(accessToken: tokenResponse.access_token)
        boxLog.info("[Box] Authenticated as \(user.login)")
        return BoxCredentials(
            accessToken: tokenResponse.access_token,
            refreshToken: tokenResponse.refresh_token ?? "",
            expiresAt: expiresAt,
            userLogin: user.login,
            displayName: user.name
        )
    }

    private static func fetchUserInfo(accessToken: String) async throws -> BoxUser {
        let url = URL(string: "\(apiURL)/users/me")!
        var request = URLRequest(url: url)
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            return BoxUser(id: "", name: "Box user", login: "")
        }
        return try JSONDecoder().decode(BoxUser.self, from: data)
    }

    // MARK: - Token Refresh

    /// Refreshes credentials when within 60s of expiry. Box rotates refresh
    /// tokens — every successful refresh returns a new refresh token that
    /// invalidates the previous one, so we always persist the latest.
    func refreshTokenIfNeeded() async throws -> BoxCredentials {
        // If another caller is already refreshing, await its result instead
        // of firing a parallel /oauth2/token POST that Box would reject as
        // invalid_grant (the refresh token is single-use).
        if let inflight = inflightRefresh {
            return try await inflight.value
        }

        guard Date() >= credentials.expiresAt.addingTimeInterval(-60) else {
            return credentials
        }
        return try await startRefresh()
    }

    /// Force a refresh regardless of the cached `expiresAt`. Used after a
    /// 401 from an API call — Box may invalidate access tokens server-side
    /// (refresh-token rotation, forced logout, suspicious activity) before
    /// our local expiry kicks in, so the "is the token expiring soon?"
    /// guard isn't enough.
    func forceRefresh() async throws -> BoxCredentials {
        if let inflight = inflightRefresh {
            return try await inflight.value
        }
        return try await startRefresh()
    }

    private func startRefresh() async throws -> BoxCredentials {
        guard !credentials.refreshToken.isEmpty else {
            throw CloudProviderError.notAuthenticated
        }
        let task = Task<BoxCredentials, Error> { [self] in
            try await self.performTokenRefresh()
        }
        inflightRefresh = task
        do {
            let creds = try await task.value
            inflightRefresh = nil
            return creds
        } catch {
            inflightRefresh = nil
            throw error
        }
    }

    private func performTokenRefresh() async throws -> BoxCredentials {
        let url = URL(string: Self.tokenURL)!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
        let body = [
            "grant_type=refresh_token",
            "refresh_token=\(credentials.refreshToken)",
            "client_id=\(Self.clientId)",
            "client_secret=\(Self.clientSecret)",
        ].joined(separator: "&")
        request.httpBody = body.data(using: .utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            boxLog.error("[Box] Token refresh failed: HTTP \(http?.statusCode ?? 0)")
            throw CloudProviderError.notAuthenticated
        }
        let tokenResponse = try JSONDecoder().decode(BoxTokenResponse.self, from: data)
        let newCreds = BoxCredentials(
            accessToken: tokenResponse.access_token,
            refreshToken: tokenResponse.refresh_token ?? credentials.refreshToken,
            expiresAt: Date().addingTimeInterval(TimeInterval(tokenResponse.expires_in)),
            userLogin: credentials.userLogin,
            displayName: credentials.displayName
        )
        credentials = newCreds
        return newCreds
    }

    func userDisplayName() async throws -> String {
        credentials.displayName
    }

    // MARK: - File operations

    func listFolder(path: String) async throws -> [CloudFileItem] {
        let folderId = try await resolvePathToId(path)

        let fields = "id,type,name,size,modified_at,content_modified_at,sha1"
        var allEntries: [BoxItem] = []
        var offset = 0
        let limit = 1000

        repeat {
            let queryItems = [
                URLQueryItem(name: "fields", value: fields),
                URLQueryItem(name: "limit", value: "\(limit)"),
                URLQueryItem(name: "offset", value: "\(offset)"),
                URLQueryItem(name: "sort", value: "name"),
                URLQueryItem(name: "direction", value: "ASC"),
            ]
            let page: BoxFolderItems = try await apiRequest(.get, path: "/folders/\(folderId)/items", queryItems: queryItems)
            allEntries.append(contentsOf: page.entries)
            if page.entries.count < limit { break }
            offset += page.entries.count
            if let total = page.total_count, offset >= total { break }
        } while true

        for entry in allEntries {
            let childPath = joinedPath(parent: path, name: entry.name)
            pathIdCache[childPath] = entry.id
        }
        return allEntries.map { $0.toCloudFileItem(parentPath: path) }
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let fileId = try await resolvePathToId(remotePath)
        let creds = try await refreshTokenIfNeeded()

        let url = URL(string: "\(Self.apiURL)/files/\(fileId)/content")!
        var request = URLRequest(url: url)
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")

        // URLSession follows the 302 to dl.boxcloud.com automatically.
        // On redirect to a different host, URLSession strips the
        // Authorization header by default, which is what we want — the
        // signed URL embeds its own credentials.
        let (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let errorData = (try? Data(contentsOf: tempURL)) ?? Data()
            boxLog.error("[Box] Download failed: HTTP \(http?.statusCode ?? 0)")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0, responseBody: errorData)
        }
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let fileSize = (try? FileManager.default.attributesOfItem(atPath: localURL.path)[.size] as? Int64) ?? 0
        guard fileSize <= Self.singlePutMaxBytes else {
            throw CloudProviderError.fileTooLarge(fileBytes: fileSize, providerLimitBytes: Self.singlePutMaxBytes)
        }

        let parentPath = (remotePath as NSString).deletingLastPathComponent
        let fileName = (remotePath as NSString).lastPathComponent
        let parentId = try await resolvePathToId(parentPath)

        // Box separates "upload new file" (POST /files/content) from
        // "upload new version" (POST /files/{id}/content). Use the cache
        // to detect an existing file; if missing, attempt a new upload
        // and recover from a 409 by treating it as a version replacement.
        let existingId = pathIdCache[remotePath]
        let attrs = try? FileManager.default.attributesOfItem(atPath: localURL.path)
        let modDate = attrs?[.modificationDate] as? Date
        let createdDate = attrs?[.creationDate] as? Date
        let fileData = try Data(contentsOf: localURL)

        do {
            let uploaded = try await performMultipartUpload(
                fileData: fileData,
                fileName: fileName,
                parentId: parentId,
                existingId: existingId,
                modDate: modDate,
                createdDate: createdDate,
                onBytes: onBytes
            )
            pathIdCache[remotePath] = uploaded.id
        } catch CloudProviderError.serverError(409) {
            // Already exists — query for the file id and retry as a
            // version upload so we replace cleanly.
            guard let foundId = try? await findFileId(parentId: parentId, name: fileName) else {
                throw CloudProviderError.serverError(409)
            }
            let uploaded = try await performMultipartUpload(
                fileData: fileData,
                fileName: fileName,
                parentId: parentId,
                existingId: foundId,
                modDate: modDate,
                createdDate: createdDate,
                onBytes: onBytes
            )
            pathIdCache[remotePath] = uploaded.id
        }
    }

    private func performMultipartUpload(
        fileData: Data,
        fileName: String,
        parentId: String,
        existingId: String?,
        modDate: Date?,
        createdDate: Date?,
        onBytes: ByteProgressHandler?
    ) async throws -> BoxItem {
        let creds = try await refreshTokenIfNeeded()
        let boundary = "----FileFlussBoundary\(UUID().uuidString)"

        let urlString: String
        if let existingId {
            urlString = "\(Self.uploadURL)/files/\(existingId)/content"
        } else {
            urlString = "\(Self.uploadURL)/files/content"
        }
        guard let url = URL(string: urlString) else { throw CloudProviderError.invalidResponse }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")

        // Attributes JSON MUST come before the file part (Box returns 400
        // metadata_after_file_contents otherwise).
        let attributes = Self.buildUploadAttributes(
            name: fileName,
            parentId: parentId,
            isNewVersion: existingId != nil,
            contentModifiedAt: modDate,
            contentCreatedAt: createdDate
        )

        var body = Data()
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"attributes\"\r\n".data(using: .utf8)!)
        body.append("Content-Type: application/json\r\n\r\n".data(using: .utf8)!)
        body.append(attributes.data(using: .utf8)!)
        body.append("\r\n--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"file\"; filename=\"\(fileName)\"\r\n".data(using: .utf8)!)
        body.append("Content-Type: application/octet-stream\r\n\r\n".data(using: .utf8)!)
        body.append(fileData)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)

        let (data, response) = try await session.uploadReportingProgress(for: request, body: body, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
        if (200...299).contains(http.statusCode) {
            let parsed = try JSONDecoder().decode(BoxFolderItems.self, from: data)
            guard let first = parsed.entries.first else { throw CloudProviderError.invalidResponse }
            return first
        }
        let bodyStr = String(data: data, encoding: .utf8) ?? ""
        boxLog.error("[Box] Upload failed: HTTP \(http.statusCode): \(bodyStr.prefix(500))")
        throw Self.mapHTTPError(statusCode: http.statusCode, responseBody: data)
    }

    private static func buildUploadAttributes(
        name: String,
        parentId: String,
        isNewVersion: Bool,
        contentModifiedAt: Date?,
        contentCreatedAt: Date?
    ) -> String {
        var attrs: [String: Any] = [:]
        attrs["name"] = name
        if !isNewVersion {
            attrs["parent"] = ["id": parentId]
        }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        if let mod = contentModifiedAt {
            attrs["content_modified_at"] = formatter.string(from: mod)
        }
        if let created = contentCreatedAt {
            attrs["content_created_at"] = formatter.string(from: created)
        }
        if let data = try? JSONSerialization.data(withJSONObject: attrs),
           let str = String(data: data, encoding: .utf8) {
            return str
        }
        return "{}"
    }

    func deleteItem(at path: String) async throws {
        let entry = try await resolvePathToEntry(path)
        let suffix = entry.isFolder ? "/folders/\(entry.id)?recursive=true" : "/files/\(entry.id)"
        try await apiRequestVoid(.delete, path: suffix)
        pathIdCache.removeValue(forKey: path)
    }

    func createFolder(at path: String) async throws {
        // Box rejects duplicate folder names under the same parent with
        // 409, so short-circuit when the folder already exists.
        if (try? await resolvePathToId(path)) != nil { return }
        let parentPath = (path as NSString).deletingLastPathComponent
        let folderName = (path as NSString).lastPathComponent
        let parentId = try await resolvePathToId(parentPath)

        struct CreateFolderBody: Encodable {
            struct Parent: Encodable { let id: String }
            let name: String
            let parent: Parent
        }
        let body = CreateFolderBody(name: folderName, parent: .init(id: parentId))
        let created: BoxItem = try await apiRequest(.post, path: "/folders", body: body)
        pathIdCache[path] = created.id
    }

    func renameItem(at path: String, to newName: String) async throws {
        let entry = try await resolvePathToEntry(path)
        let suffix = entry.isFolder ? "/folders/\(entry.id)" : "/files/\(entry.id)"
        struct RenameBody: Encodable { let name: String }
        let _: BoxItem = try await apiRequest(.put, path: suffix, body: RenameBody(name: newName))

        pathIdCache.removeValue(forKey: path)
        let parentPath = (path as NSString).deletingLastPathComponent
        let newPath = joinedPath(parent: parentPath, name: newName)
        pathIdCache[newPath] = entry.id
    }

    /// Server-side move. Box accepts a parent change on `PUT /files/{id}`
    /// and `PUT /folders/{id}`; no download/re-upload needed.
    func moveItem(at path: String, toPath newPath: String) async throws {
        let entry = try await resolvePathToEntry(path)
        let newParentPath = (newPath as NSString).deletingLastPathComponent
        let newName = (newPath as NSString).lastPathComponent
        let newParentId = try await resolvePathToId(newParentPath)

        struct MoveBody: Encodable {
            struct Parent: Encodable { let id: String }
            let parent: Parent
            let name: String?
        }
        // Include the name in the PUT body — Box treats a move and a
        // rename as one atomic update when both fields are provided, so
        // a move-with-rename (drag-drop into a folder that already has
        // the same filename, after the conflict resolver picked a new
        // name) doesn't need a second round trip.
        let suffix = entry.isFolder ? "/folders/\(entry.id)" : "/files/\(entry.id)"
        let _: BoxItem = try await apiRequest(.put, path: suffix, body: MoveBody(parent: .init(id: newParentId), name: newName.isEmpty ? nil : newName))

        pathIdCache.removeValue(forKey: path)
        pathIdCache[newPath] = entry.id
    }

    /// Server-side copy. Folder copies use `POST /folders/{id}/copy` and
    /// recursively duplicate the tree on Box's side.
    func copyItem(at path: String, toPath newPath: String) async throws {
        let entry = try await resolvePathToEntry(path)
        let newParentPath = (newPath as NSString).deletingLastPathComponent
        let newName = (newPath as NSString).lastPathComponent
        let newParentId = try await resolvePathToId(newParentPath)

        struct CopyBody: Encodable {
            struct Parent: Encodable { let id: String }
            let parent: Parent
            let name: String?
        }
        let suffix = entry.isFolder ? "/folders/\(entry.id)/copy" : "/files/\(entry.id)/copy"
        let copied: BoxItem = try await apiRequest(.post, path: suffix, body: CopyBody(parent: .init(id: newParentId), name: newName.isEmpty ? nil : newName))
        pathIdCache[newPath] = copied.id
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        let entry = try await resolvePathToEntry(path)
        let parentPath = (path as NSString).deletingLastPathComponent
        let fields = "id,type,name,size,modified_at,content_modified_at,sha1"
        let suffix = entry.isFolder ? "/folders/\(entry.id)?fields=\(fields)" : "/files/\(entry.id)?fields=\(fields)"
        let item: BoxItem = try await apiRequest(.get, path: suffix)
        return item.toCloudFileItem(parentPath: parentPath)
    }

    func folderSize(at path: String) async throws -> Int64 {
        try await calculateFolderSizeRecursively(path: path)
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

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
        var queryItems = [
            URLQueryItem(name: "query", value: query),
            URLQueryItem(name: "limit", value: "200"),
            URLQueryItem(name: "fields", value: "id,type,name,size,modified_at,content_modified_at,sha1"),
        ]
        if let path, path != "/" {
            let folderId = try await resolvePathToId(path)
            queryItems.append(URLQueryItem(name: "ancestor_folder_ids", value: folderId))
        }
        let response: BoxFolderItems = try await apiRequest(.get, path: "/search", queryItems: queryItems)
        return response.entries.map { $0.toCloudFileItem(parentPath: "/") }
    }

    // MARK: - Path resolution

    private struct ResolvedEntry {
        let id: String
        let isFolder: Bool
    }

    private func resolvePathToId(_ path: String) async throws -> String {
        if let cached = pathIdCache[path] { return cached }
        let entry = try await resolvePathToEntry(path)
        return entry.id
    }

    /// Walks `path` component by component, querying the Box API for each
    /// child by name within its parent. We can't tell file vs folder from
    /// the cache alone, so the final lookup re-fetches the entry to learn
    /// its type. Intermediate components are always folders (otherwise the
    /// path is invalid).
    private func resolvePathToEntry(_ path: String) async throws -> ResolvedEntry {
        if path == "/" { return ResolvedEntry(id: "0", isFolder: true) }
        let components = path.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        guard !components.isEmpty else { return ResolvedEntry(id: "0", isFolder: true) }

        var currentId = "0"
        var currentPath = ""

        for (index, name) in components.enumerated() {
            currentPath += "/\(name)"
            let isLast = (index == components.count - 1)

            if let cached = pathIdCache[currentPath], !isLast {
                currentId = cached
                continue
            }

            let match = try await findEntry(parentId: currentId, name: name)
            guard let match else {
                throw CloudProviderError.notFound(currentPath)
            }
            pathIdCache[currentPath] = match.id
            if isLast { return match }
            guard match.isFolder else {
                throw CloudProviderError.notFound(currentPath)
            }
            currentId = match.id
        }
        return ResolvedEntry(id: currentId, isFolder: true)
    }

    /// Looks up a single child of `parentId` by exact name match.
    /// Box's `/folders/{id}/items` doesn't accept a name filter, so we
    /// page through up to 1000 items at a time and look locally. For most
    /// folders one page is enough.
    private func findEntry(parentId: String, name: String) async throws -> ResolvedEntry? {
        var offset = 0
        let limit = 1000
        while true {
            let queryItems = [
                URLQueryItem(name: "fields", value: "id,type,name"),
                URLQueryItem(name: "limit", value: "\(limit)"),
                URLQueryItem(name: "offset", value: "\(offset)"),
            ]
            let page: BoxFolderItems = try await apiRequest(.get, path: "/folders/\(parentId)/items", queryItems: queryItems)
            if let hit = page.entries.first(where: { $0.name == name }) {
                return ResolvedEntry(id: hit.id, isFolder: hit.isFolder)
            }
            if page.entries.count < limit { return nil }
            offset += page.entries.count
            if let total = page.total_count, offset >= total { return nil }
        }
    }

    /// Find a file (not folder) by exact name in a folder, returning its
    /// id. Used to recover from a 409 conflict on upload.
    private func findFileId(parentId: String, name: String) async throws -> String? {
        let entry = try await findEntry(parentId: parentId, name: name)
        guard let entry, !entry.isFolder else { return nil }
        return entry.id
    }

    private func joinedPath(parent: String, name: String) -> String {
        if parent == "/" { return "/\(name)" }
        return "\(parent)/\(name)"
    }

    // MARK: - HTTP helpers

    private enum HTTPMethod: String {
        case get = "GET"
        case post = "POST"
        case put = "PUT"
        case delete = "DELETE"
    }

    private func apiRequest<T: Decodable>(_ method: HTTPMethod, path: String, queryItems: [URLQueryItem] = [], body: (any Encodable)? = nil) async throws -> T {
        var components = URLComponents(string: "\(Self.apiURL)\(path)")!
        if !queryItems.isEmpty {
            components.queryItems = (components.queryItems ?? []) + queryItems
        }
        guard let url = components.url else { throw CloudProviderError.invalidResponse }
        let encodedBody: Data?
        if let body {
            encodedBody = try JSONEncoder().encode(body)
        } else {
            encodedBody = nil
        }

        func send(forceRefreshFirst: Bool) async throws -> (Data, HTTPURLResponse) {
            let creds = forceRefreshFirst ? try await forceRefresh() : try await refreshTokenIfNeeded()
            var request = URLRequest(url: url)
            request.httpMethod = method.rawValue
            request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
            if let encodedBody {
                request.setValue("application/json", forHTTPHeaderField: "Content-Type")
                request.httpBody = encodedBody
            }
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
            return (data, http)
        }

        var (data, http) = try await send(forceRefreshFirst: false)
        // 401 with a non-empty access token usually means Box invalidated
        // it server-side ahead of our local expiry. Force-refresh and retry
        // once before surfacing notAuthenticated to the user.
        if http.statusCode == 401 {
            boxLog.info("[Box] \(method.rawValue) \(path) → 401, force-refreshing and retrying once")
            (data, http) = try await send(forceRefreshFirst: true)
        }
        guard (200...299).contains(http.statusCode) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            boxLog.error("[Box] \(method.rawValue) \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http.statusCode, responseBody: data)
        }
        return try JSONDecoder().decode(T.self, from: data)
    }

    private func apiRequestVoid(_ method: HTTPMethod, path: String) async throws {
        guard let url = URL(string: "\(Self.apiURL)\(path)") else { throw CloudProviderError.invalidResponse }

        func send(forceRefreshFirst: Bool) async throws -> (Data, HTTPURLResponse) {
            let creds = forceRefreshFirst ? try await forceRefresh() : try await refreshTokenIfNeeded()
            var request = URLRequest(url: url)
            request.httpMethod = method.rawValue
            request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
            return (data, http)
        }

        var (data, http) = try await send(forceRefreshFirst: false)
        if http.statusCode == 401 {
            boxLog.info("[Box] \(method.rawValue) \(path) → 401, force-refreshing and retrying once")
            (data, http) = try await send(forceRefreshFirst: true)
        }
        guard (200...299).contains(http.statusCode) || http.statusCode == 204 else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            boxLog.error("[Box] \(method.rawValue) \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http.statusCode, responseBody: data)
        }
    }

    private static func mapHTTPError(statusCode: Int, responseBody: Data? = nil) -> CloudProviderError {
        let parsedMessage = parseErrorMessage(from: responseBody)
        switch statusCode {
        case 401: return .notAuthenticated
        case 403: return .unauthorized
        case 404: return .notFound(parsedMessage ?? "Resource not found")
        case 409: return .serverError(409)
        case 429: return .rateLimited
        case 507: return .quotaExceeded
        default: return .serverError(statusCode)
        }
    }

    /// Decodes Box's OAuth error envelope: `{ "error": "...", "error_description": "..." }`.
    /// Returns the human-readable description if available, else the error code.
    private static func parseOAuthError(from data: Data) -> String? {
        struct OAuthError: Decodable {
            let error: String?
            let error_description: String?
        }
        guard let parsed = try? JSONDecoder().decode(OAuthError.self, from: data) else { return nil }
        if let desc = parsed.error_description, !desc.isEmpty { return desc }
        return parsed.error
    }

    private static func parseErrorMessage(from data: Data?) -> String? {
        guard let data else { return nil }
        struct BoxError: Decodable {
            let type: String?
            let status: Int?
            let code: String?
            let message: String?
        }
        guard let parsed = try? JSONDecoder().decode(BoxError.self, from: data) else { return nil }
        return parsed.message ?? parsed.code
    }

    // MARK: - CSRF state

    /// Cryptographically random opaque value for the OAuth `state`
    /// parameter — used to bind the authorize request to its callback and
    /// reject auth codes the user didn't initiate.
    private static func generateState() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return Data(bytes).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

// MARK: - Thread-safe continuation wrapper

private final class BoxContinuationGuard<T: Sendable>: Sendable {
    private let state = OSAllocatedUnfairLock(initialState: State())

    private struct State {
        var continuation: CheckedContinuation<T, Error>?
        var resumed = false
    }

    func setContinuation(_ continuation: CheckedContinuation<T, Error>) {
        state.withLock { $0.continuation = continuation }
    }

    func resume(returning value: T) {
        state.withLock { state in
            guard !state.resumed, let cont = state.continuation else { return }
            state.resumed = true
            state.continuation = nil
            cont.resume(returning: value)
        }
    }

    func resume(throwing error: Error) {
        state.withLock { state in
            guard !state.resumed, let cont = state.continuation else { return }
            state.resumed = true
            state.continuation = nil
            cont.resume(throwing: error)
        }
    }
}

// MARK: - Box API response types

private struct BoxTokenResponse: Decodable {
    let access_token: String
    let refresh_token: String?
    let expires_in: Int
    let token_type: String?
}

private struct BoxUser: Decodable {
    let id: String
    let name: String
    let login: String
}

struct BoxItem: Decodable {
    let id: String
    let type: String
    let name: String
    let size: Int64?
    let modified_at: String?
    let content_modified_at: String?
    let sha1: String?

    var isFolder: Bool { type == "folder" }

    func toCloudFileItem(parentPath: String) -> CloudFileItem {
        let itemPath: String
        if parentPath == "/" { itemPath = "/\(name)" }
        else { itemPath = "\(parentPath)/\(name)" }

        let modDate: Date = {
            // Prefer the user-set content_modified_at (matches what other
            // providers expose). Fall back to server modified_at, then
            // distantPast for items without timestamps.
            let formatter = ISO8601DateFormatter()
            formatter.formatOptions = [.withInternetDateTime]
            if let s = content_modified_at, let d = formatter.date(from: s) { return d }
            if let s = modified_at, let d = formatter.date(from: s) { return d }
            return .distantPast
        }()

        return CloudFileItem(
            id: itemPath,
            name: name,
            path: itemPath,
            isDirectory: isFolder,
            size: size ?? 0,
            modificationDate: modDate,
            checksum: sha1
        )
    }
}

struct BoxFolderItems: Decodable {
    let entries: [BoxItem]
    let total_count: Int?
    let limit: Int?
    let offset: Int?
}
