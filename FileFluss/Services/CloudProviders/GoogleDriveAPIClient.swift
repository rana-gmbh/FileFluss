import AppKit
import Foundation
import Network
import os

private let googleLog = Logger(subsystem: "com.rana.FileFluss", category: "googleDrive")

struct GoogleDriveCredentials: Codable, Sendable {
    let accessToken: String
    let refreshToken: String
    let expiresAt: Date
    let userEmail: String
    let displayName: String
}

struct GoogleDriveDeviceAuth: Sendable {
    let authURL: URL
    let port: UInt16
}

actor GoogleDriveAPIClient {
    static let clientId = "682536313816-j07jrk2kbff3sljb16vfal5soqs8vte9.apps.googleusercontent.com"
    static let clientSecret = "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"
    static let scopes = "https://www.googleapis.com/auth/drive https://www.googleapis.com/auth/userinfo.profile https://www.googleapis.com/auth/userinfo.email"

    private(set) var credentials: GoogleDriveCredentials
    private let session: URLSession
    private let apiURL = "https://www.googleapis.com/drive/v3"
    private let uploadURL = "https://www.googleapis.com/upload/drive/v3"

    // Cache path → (file ID, mimeType) lookups to avoid repeated resolution
    private var pathIdCache: [String: CachedFile] = ["/" : CachedFile(id: "root", mimeType: "application/vnd.google-apps.folder")]

    struct CachedFile {
        let id: String
        let mimeType: String
    }

    private static let googleWorkspaceMimeTypes: Set<String> = [
        "application/vnd.google-apps.document",
        "application/vnd.google-apps.spreadsheet",
        "application/vnd.google-apps.presentation",
        "application/vnd.google-apps.drawing",
    ]

    private static let exportMimeTypes: [String: String] = [
        "application/vnd.google-apps.document": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.google-apps.spreadsheet": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.google-apps.presentation": "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.google-apps.drawing": "application/pdf",
    ]

    private static let exportExtensions: [String: String] = [
        "application/vnd.google-apps.document": "docx",
        "application/vnd.google-apps.spreadsheet": "xlsx",
        "application/vnd.google-apps.presentation": "pptx",
        "application/vnd.google-apps.drawing": "pdf",
    ]

    init(credentials: GoogleDriveCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)
    }

    // MARK: - OAuth2 (Loopback Redirect with PKCE)

    /// Start the OAuth flow: opens the user's browser for Google sign-in.
    /// Returns the authorization code via a loopback HTTP server.
    static func startOAuthFlow() async throws -> GoogleDriveCredentials {
        let codeVerifier = generateCodeVerifier()
        let codeChallenge = generateCodeChallenge(from: codeVerifier)

        let (port, authCode) = try await listenForAuthCode(codeVerifier: codeVerifier, codeChallenge: codeChallenge)

        // Exchange auth code for tokens
        let credentials = try await exchangeCodeForTokens(code: authCode, codeVerifier: codeVerifier, redirectPort: port)
        return credentials
    }

    private static func listenForAuthCode(codeVerifier: String, codeChallenge: String) async throws -> (UInt16, String) {
        let listener = try NWListener(using: .tcp, on: .any)
        let guard_ = ContinuationGuard<(UInt16, String)>()

        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<(UInt16, String), Error>) in
            guard_.setContinuation(continuation)

            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    guard let port = listener.port?.rawValue else { return }

                    var components = URLComponents(string: "https://accounts.google.com/o/oauth2/v2/auth")!
                    components.queryItems = [
                        URLQueryItem(name: "client_id", value: clientId),
                        URLQueryItem(name: "redirect_uri", value: "http://127.0.0.1:\(port)"),
                        URLQueryItem(name: "response_type", value: "code"),
                        URLQueryItem(name: "scope", value: scopes),
                        URLQueryItem(name: "code_challenge", value: codeChallenge),
                        URLQueryItem(name: "code_challenge_method", value: "S256"),
                        URLQueryItem(name: "access_type", value: "offline"),
                        URLQueryItem(name: "prompt", value: "consent"),
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

                    googleLog.debug("[Google] OAuth callback received: \(requestString.prefix(300))")

                    // Parse the HTTP request line to extract query parameters
                    guard let firstLine = requestString.components(separatedBy: "\r\n").first,
                          let urlPart = firstLine.split(separator: " ").dropFirst().first else {
                        // Not a valid HTTP request — ignore and close this connection
                        connection.cancel()
                        return
                    }

                    let components = URLComponents(string: "http://localhost\(urlPart)")

                    // Check for an explicit error from Google
                    if let errorParam = components?.queryItems?.first(where: { $0.name == "error" })?.value {
                        googleLog.error("[Google] OAuth error: \(errorParam)")
                        let errorHTML = "<!DOCTYPE html><html><body><h2>Authentication failed</h2><p>\(errorParam)</p><p>You can close this window.</p></body></html>"
                        let response = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: \(errorHTML.utf8.count)\r\nConnection: close\r\n\r\n\(errorHTML)"
                        connection.send(content: response.data(using: .utf8), completion: .contentProcessed { _ in
                            connection.cancel()
                        })
                        listener.cancel()
                        guard_.resume(throwing: CloudProviderError.unauthorized)
                        return
                    }

                    // Try to extract the authorization code
                    guard let code = components?.queryItems?.first(where: { $0.name == "code" })?.value else {
                        // No code and no error — likely a favicon or preflight request. Ignore it.
                        googleLog.debug("[Google] Ignoring non-auth request: \(String(urlPart).prefix(100))")
                        let emptyResponse = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        connection.send(content: emptyResponse.data(using: .utf8), completion: .contentProcessed { _ in
                            connection.cancel()
                        })
                        return
                    }

                    // Success — got the auth code
                    let successHTML = "<!DOCTYPE html><html><body><h2>Signed in to Google Drive</h2><p>You can close this window and return to FileFluss.</p></body></html>"
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

    private static func exchangeCodeForTokens(code: String, codeVerifier: String, redirectPort: UInt16) async throws -> GoogleDriveCredentials {
        let url = URL(string: "https://oauth2.googleapis.com/token")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

        // URL-encode all values to handle special characters (e.g. "/" in auth code)
        let encode = { (s: String) -> String in
            s.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? s
        }
        let bodyParams = [
            "code=\(encode(code))",
            "client_id=\(encode(clientId))",
            "client_secret=\(encode(clientSecret))",
            "redirect_uri=\(encode("http://127.0.0.1:\(redirectPort)"))",
            "grant_type=authorization_code",
            "code_verifier=\(encode(codeVerifier))",
        ].joined(separator: "&")
        request.httpBody = bodyParams.data(using: .utf8)

        let (data, response) = try await URLSession.shared.data(for: request)
        let bodyStr = String(data: data, encoding: .utf8) ?? ""
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            googleLog.error("[Google] Token exchange failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw CloudProviderError.invalidCredentials
        }

        googleLog.info("[Google] Token exchange response: \(bodyStr.prefix(200))")

        let tokenResponse = try JSONDecoder().decode(GoogleTokenResponse.self, from: data)
        let expiresAt = Date().addingTimeInterval(TimeInterval(tokenResponse.expires_in))

        googleLog.info("[Google] Token expires in \(tokenResponse.expires_in)s, scope: \(tokenResponse.scope ?? "none")")

        // Verify the token works by fetching user info
        let userInfo = try await fetchUserInfo(accessToken: tokenResponse.access_token)

        googleLog.info("[Google] Authenticated as \(userInfo.name ?? userInfo.email)")

        return GoogleDriveCredentials(
            accessToken: tokenResponse.access_token,
            refreshToken: tokenResponse.refresh_token ?? "",
            expiresAt: expiresAt,
            userEmail: userInfo.email,
            displayName: userInfo.name ?? userInfo.email
        )
    }

    private static func fetchUserInfo(accessToken: String) async throws -> GoogleUserInfo {
        let url = URL(string: "https://www.googleapis.com/oauth2/v2/userinfo")!
        var request = URLRequest(url: url)
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            return GoogleUserInfo(email: "Unknown", name: nil)
        }

        return try JSONDecoder().decode(GoogleUserInfo.self, from: data)
    }

    // MARK: - Token Refresh

    func refreshTokenIfNeeded() async throws -> GoogleDriveCredentials {
        guard Date() >= credentials.expiresAt.addingTimeInterval(-60) else {
            return credentials
        }

        guard !credentials.refreshToken.isEmpty else {
            throw CloudProviderError.notAuthenticated
        }

        let url = URL(string: "https://oauth2.googleapis.com/token")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

        let body = [
            "client_id=\(Self.clientId)",
            "client_secret=\(Self.clientSecret)",
            "refresh_token=\(credentials.refreshToken)",
            "grant_type=refresh_token",
        ].joined(separator: "&")
        request.httpBody = body.data(using: .utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            googleLog.error("[Google] Token refresh failed: HTTP \(http?.statusCode ?? 0)")
            throw CloudProviderError.notAuthenticated
        }

        let tokenResponse = try JSONDecoder().decode(GoogleTokenResponse.self, from: data)
        let newCreds = GoogleDriveCredentials(
            accessToken: tokenResponse.access_token,
            refreshToken: tokenResponse.refresh_token ?? credentials.refreshToken,
            expiresAt: Date().addingTimeInterval(TimeInterval(tokenResponse.expires_in)),
            userEmail: credentials.userEmail,
            displayName: credentials.displayName
        )
        credentials = newCreds
        return newCreds
    }

    func userDisplayName() async throws -> String {
        credentials.displayName
    }

    // MARK: - File Operations

    func listFolder(path: String) async throws -> [CloudFileItem] {
        let folderId = (try await resolvePathToCachedFile(path)).id

        var allItems: [GoogleDriveFile] = []
        var pageToken: String?

        repeat {
            var queryItems = [
                URLQueryItem(name: "q", value: "'\(folderId)' in parents and trashed = false"),
                URLQueryItem(name: "fields", value: "nextPageToken,files(id,name,mimeType,size,modifiedTime,md5Checksum)"),
                URLQueryItem(name: "pageSize", value: "1000"),
                URLQueryItem(name: "orderBy", value: "folder,name"),
            ]
            if let pageToken {
                queryItems.append(URLQueryItem(name: "pageToken", value: pageToken))
            }

            let response: GoogleFileListResponse = try await apiRequest(.get, path: "/files", queryItems: queryItems)
            allItems.append(contentsOf: response.files)
            pageToken = response.nextPageToken
        } while pageToken != nil

        // Cache IDs and mimeTypes for resolved items
        for file in allItems {
            let itemPath = path == "/" ? "/\(file.name)" : "\(path)/\(file.name)"
            pathIdCache[itemPath] = CachedFile(id: file.id, mimeType: file.mimeType)
        }

        return allItems.map { $0.toCloudFileItem(parentPath: path) }
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let cached = try await resolvePathToCachedFile(remotePath)
        let creds = try await refreshTokenIfNeeded()

        let url: URL
        var actualLocalURL = localURL
        if let exportMime = Self.exportMimeTypes[cached.mimeType] {
            // Google Workspace file — use export endpoint
            let encodedMime = exportMime.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? exportMime
            url = URL(string: "\(apiURL)/files/\(cached.id)/export?mimeType=\(encodedMime)")!
            // Append correct file extension for the exported format
            if let ext = Self.exportExtensions[cached.mimeType] {
                actualLocalURL = localURL.appendingPathExtension(ext)
            }
            googleLog.debug("[Google] Exporting workspace file as \(exportMime) → .\(Self.exportExtensions[cached.mimeType] ?? "?")")
        } else {
            // Regular file — use alt=media
            url = URL(string: "\(apiURL)/files/\(cached.id)?alt=media")!
        }

        var request = URLRequest(url: url)
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")

        let (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let errorData = (try? Data(contentsOf: tempURL)) ?? Data()
            let bodyStr = String(data: errorData, encoding: .utf8) ?? ""
            googleLog.error("[Google] Download failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0, responseBody: errorData)
        }
        try? FileManager.default.removeItem(at: actualLocalURL)
        try FileManager.default.moveItem(at: tempURL, to: actualLocalURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let parentPath = (remotePath as NSString).deletingLastPathComponent
        let fileName = (remotePath as NSString).lastPathComponent
        let parentId = (try await resolvePathToCachedFile(parentPath)).id

        let fileData = try Data(contentsOf: localURL)

        if fileData.count <= 5_000_000 {
            try await simpleUpload(data: fileData, fileName: fileName, parentId: parentId, onBytes: onBytes)
        } else {
            try await resumableUpload(from: localURL, fileSize: fileData.count, fileName: fileName, parentId: parentId, onBytes: onBytes)
        }
    }

    private func simpleUpload(data: Data, fileName: String, parentId: String, onBytes: ByteProgressHandler?) async throws {
        let creds = try await refreshTokenIfNeeded()
        let boundary = UUID().uuidString

        guard let url = URL(string: "\(uploadURL)/files?uploadType=multipart") else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("multipart/related; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

        let metadata = "{\"name\": \"\(fileName)\", \"parents\": [\"\(parentId)\"]}"
        var body = Data()
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Type: application/json; charset=UTF-8\r\n\r\n".data(using: .utf8)!)
        body.append(metadata.data(using: .utf8)!)
        body.append("\r\n--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Type: application/octet-stream\r\n\r\n".data(using: .utf8)!)
        body.append(data)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)

        let (responseData, response) = try await session.uploadReportingProgress(for: request, body: body, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: responseData, encoding: .utf8) ?? ""
            googleLog.error("[Google] Upload failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    private func resumableUpload(from localURL: URL, fileSize: Int, fileName: String, parentId: String, onBytes: ByteProgressHandler?) async throws {
        let creds = try await refreshTokenIfNeeded()

        // Step 1: Initiate resumable upload
        guard let initURL = URL(string: "\(uploadURL)/files?uploadType=resumable") else {
            throw CloudProviderError.invalidResponse
        }

        var initRequest = URLRequest(url: initURL)
        initRequest.httpMethod = "POST"
        initRequest.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        initRequest.setValue("application/json; charset=UTF-8", forHTTPHeaderField: "Content-Type")
        initRequest.setValue("application/octet-stream", forHTTPHeaderField: "X-Upload-Content-Type")
        initRequest.setValue("\(fileSize)", forHTTPHeaderField: "X-Upload-Content-Length")

        let metadata = "{\"name\": \"\(fileName)\", \"parents\": [\"\(parentId)\"]}"
        initRequest.httpBody = metadata.data(using: .utf8)

        let (_, initResponse) = try await session.data(for: initRequest)
        guard let initHttp = initResponse as? HTTPURLResponse, (200...299).contains(initHttp.statusCode),
              let uploadURLString = initHttp.value(forHTTPHeaderField: "Location"),
              let uploadURL = URL(string: uploadURLString) else {
            let initHttp = initResponse as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: initHttp?.statusCode ?? 0)
        }

        // Step 2: Upload in 10MB chunks
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

            let (_, chunkResponse) = try await session.uploadReportingProgress(for: chunkRequest, body: Data(chunk), onBytes: onBytes)
            guard let chunkHttp = chunkResponse as? HTTPURLResponse,
                  (200...299).contains(chunkHttp.statusCode) || chunkHttp.statusCode == 308 else {
                let chunkHttp = chunkResponse as? HTTPURLResponse
                throw Self.mapHTTPError(statusCode: chunkHttp?.statusCode ?? 0)
            }

            offset = end
        }
    }

    func deleteItem(at path: String) async throws {
        let cached = try await resolvePathToCachedFile(path)
        try await apiRequestVoid(.delete, path: "/files/\(cached.id)")
        pathIdCache.removeValue(forKey: path)
    }

    func createFolder(at path: String) async throws {
        // Idempotent: Google Drive allows duplicate folder names under the same
        // parent, so a naive create multiplies folders on each retry. Short-
        // circuit when the folder already exists at this path.
        if (try? await resolvePathToCachedFile(path)) != nil { return }

        let parentPath = (path as NSString).deletingLastPathComponent
        let folderName = (path as NSString).lastPathComponent
        let parentCached = try await resolvePathToCachedFile(parentPath)
        let parentId = parentCached.id

        struct CreateFolderBody: Encodable {
            let name: String
            let mimeType: String
            let parents: [String]
        }

        let body = CreateFolderBody(
            name: folderName,
            mimeType: "application/vnd.google-apps.folder",
            parents: [parentId]
        )

        let created: GoogleDriveFile = try await apiRequest(.post, path: "/files", body: body)
        pathIdCache[path] = CachedFile(id: created.id, mimeType: created.mimeType)
    }

    func renameItem(at path: String, to newName: String) async throws {
        let cached = try await resolvePathToCachedFile(path)
        let fileId = cached.id

        struct RenameBody: Encodable {
            let name: String
        }

        let _: GoogleDriveFile = try await apiRequest(.patch, path: "/files/\(fileId)", body: RenameBody(name: newName))

        // Update cache
        pathIdCache.removeValue(forKey: path)
        let parentPath = (path as NSString).deletingLastPathComponent
        let newPath = parentPath == "/" ? "/\(newName)" : "\(parentPath)/\(newName)"
        pathIdCache[newPath] = CachedFile(id: fileId, mimeType: cached.mimeType)
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        let fileId = (try await resolvePathToCachedFile(path)).id

        var components = URLComponents(string: "\(apiURL)/files/\(fileId)")!
        components.queryItems = [
            URLQueryItem(name: "fields", value: "id,name,mimeType,size,modifiedTime,md5Checksum"),
        ]

        guard let url = components.url else {
            throw CloudProviderError.invalidResponse
        }

        let creds = try await refreshTokenIfNeeded()
        var request = URLRequest(url: url)
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }

        let file = try JSONDecoder().decode(GoogleDriveFile.self, from: data)
        let parentPath = (path as NSString).deletingLastPathComponent
        return file.toCloudFileItem(parentPath: parentPath)
    }

    func folderSize(at path: String) async throws -> Int64 {
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

    // MARK: - Path Resolution

    /// Google Drive uses file IDs, not paths. This resolves a POSIX-style path to a cached file entry
    /// by walking each path component and querying for it by name within the parent.
    private func resolvePathToCachedFile(_ path: String) async throws -> CachedFile {
        if let cached = pathIdCache[path] {
            return cached
        }

        let components = path.split(separator: "/", omittingEmptySubsequences: true)
        if components.isEmpty {
            return pathIdCache["/"]!
        }

        var currentCached = pathIdCache["/"]!
        var currentPath = ""

        for component in components {
            let name = String(component)
            currentPath += "/\(name)"

            if let cached = pathIdCache[currentPath] {
                currentCached = cached
                continue
            }

            let escapedName = name.replacingOccurrences(of: "'", with: "\\'")
            let query = "'\(currentCached.id)' in parents and name = '\(escapedName)' and trashed = false"

            let response: GoogleFileListResponse = try await apiRequest(.get, path: "/files", queryItems: [
                URLQueryItem(name: "q", value: query),
                URLQueryItem(name: "fields", value: "files(id,name,mimeType)"),
                URLQueryItem(name: "pageSize", value: "1"),
            ])

            guard let file = response.files.first else {
                throw CloudProviderError.notFound(currentPath)
            }

            let cached = CachedFile(id: file.id, mimeType: file.mimeType)
            pathIdCache[currentPath] = cached
            currentCached = cached
        }

        return currentCached
    }

    // MARK: - Search

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
        let escapedQuery = query.replacingOccurrences(of: "'", with: "\\'")
        var q = "name contains '\(escapedQuery)' and trashed = false"

        // Scope to a specific folder if path is provided
        if let path, path != "/" {
            let cached = try await resolvePathToCachedFile(path)
            q = "'\(cached.id)' in parents and name contains '\(escapedQuery)' and trashed = false"
        }

        var allItems: [GoogleDriveFile] = []
        var pageToken: String?

        repeat {
            var queryItems = [
                URLQueryItem(name: "q", value: q),
                URLQueryItem(name: "fields", value: "nextPageToken,files(id,name,mimeType,size,modifiedTime,md5Checksum,parents)"),
                URLQueryItem(name: "pageSize", value: "100"),
            ]
            if let pageToken {
                queryItems.append(URLQueryItem(name: "pageToken", value: pageToken))
            }
            let response: GoogleFileListResponse = try await apiRequest(.get, path: "/files", queryItems: queryItems)
            allItems.append(contentsOf: response.files)
            pageToken = response.nextPageToken
        } while pageToken != nil && allItems.count < 500

        return allItems.map { $0.toCloudFileItem(parentPath: "/") }
    }

    // MARK: - HTTP Helpers

    private enum HTTPMethod: String {
        case get = "GET"
        case post = "POST"
        case patch = "PATCH"
        case delete = "DELETE"
    }

    private func apiRequest<T: Decodable>(_ method: HTTPMethod, path: String, queryItems: [URLQueryItem] = [], body: (any Encodable)? = nil) async throws -> T {
        var components = URLComponents(string: "\(apiURL)\(path)")!
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
            googleLog.error("[Google] \(method.rawValue) \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(1000))")
            throw Self.mapHTTPError(statusCode: http.statusCode, responseBody: data)
        }

        return try JSONDecoder().decode(T.self, from: data)
    }

    private func apiRequestVoid(_ method: HTTPMethod, path: String) async throws {
        guard let url = URL(string: "\(apiURL)\(path)") else {
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

        guard (200...299).contains(http.statusCode) || http.statusCode == 204 else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            googleLog.error("[Google] \(method.rawValue) \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(1000))")
            throw Self.mapHTTPError(statusCode: http.statusCode, responseBody: data)
        }
    }

    private static func mapHTTPError(statusCode: Int, responseBody: Data? = nil) -> CloudProviderError {
        // Try to extract Google's error message for better diagnostics
        let googleMessage = parseGoogleErrorMessage(from: responseBody)

        switch statusCode {
        case 401: return .notAuthenticated
        case 403:
            // Pass through Google's actual error (e.g. "Drive API not enabled", "Insufficient permissions")
            if googleMessage != nil {
                return .serverError(403)
            }
            return .unauthorized
        case 404: return .notFound(googleMessage ?? "Resource not found")
        case 429: return .rateLimited
        case 507: return .quotaExceeded
        default: return .serverError(statusCode)
        }
    }

    private static func parseGoogleErrorMessage(from data: Data?) -> String? {
        guard let data else { return nil }
        struct GoogleError: Decodable {
            struct ErrorDetail: Decodable {
                let message: String
                let status: String?
                let code: Int?
            }
            let error: ErrorDetail
        }
        guard let parsed = try? JSONDecoder().decode(GoogleError.self, from: data) else { return nil }
        return parsed.error.message
    }

    // MARK: - PKCE

    private static func generateCodeVerifier() -> String {
        let chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~"
        return String((0..<64).map { _ in chars.randomElement()! })
    }

    private static func generateCodeChallenge(from verifier: String) -> String {
        let data = Data(verifier.utf8)
        var hash = [UInt8](repeating: 0, count: 32)
        data.withUnsafeBytes { buffer in
            _ = CC_SHA256(buffer.baseAddress, CC_LONG(data.count), &hash)
        }
        return Data(hash)
            .base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

// Use CommonCrypto for SHA256
import CommonCrypto

// MARK: - Thread-safe continuation wrapper

private final class ContinuationGuard<T: Sendable>: Sendable {
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

// MARK: - Google API Response Types

private struct GoogleTokenResponse: Decodable {
    let access_token: String
    let refresh_token: String?
    let expires_in: Int
    let token_type: String
    let scope: String?
}

private struct GoogleUserInfo: Decodable {
    let email: String
    let name: String?
}

struct GoogleFileListResponse: Decodable {
    let files: [GoogleDriveFile]
    let nextPageToken: String?
}

struct GoogleDriveFile: Decodable {
    let id: String
    let name: String
    let mimeType: String
    let size: String?
    let modifiedTime: String?
    let md5Checksum: String?

    var isDirectory: Bool {
        mimeType == "application/vnd.google-apps.folder"
    }

    func toCloudFileItem(parentPath: String) -> CloudFileItem {
        let itemPath: String
        if parentPath == "/" {
            itemPath = "/\(name)"
        } else {
            itemPath = "\(parentPath)/\(name)"
        }

        let modDate: Date
        if let dateStr = modifiedTime {
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
            size: Int64(size ?? "0") ?? 0,
            modificationDate: modDate,
            checksum: md5Checksum
        )
    }
}
