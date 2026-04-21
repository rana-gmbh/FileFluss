import AppKit
import Foundation
import Network
import os

private let hiDriveLog = Logger(subsystem: "com.rana.FileFluss", category: "hiDrive")

struct HiDriveCredentials: Codable, Sendable {
    let accessToken: String
    let refreshToken: String
    let expiresAt: Date
    let userAlias: String       // HiDrive alias (maps to /users/<alias>)
    let displayName: String
}

actor HiDriveAPIClient {
    // Register an app at https://developer.hidrive.com/get-api-key/ and set
    // these to the issued credentials. Until they're filled in, the OAuth
    // flow will fail with an "invalid_client" error — this is expected.
    static let clientId = "HIDRIVE_CLIENT_ID"
    static let clientSecret = "HIDRIVE_CLIENT_SECRET"
    static let authBaseURL = "https://my.hidrive.com"
    static let apiBaseURL = "https://api.hidrive.strato.com/2.1"
    // Scope format is "<role>,<access>": user + read-write covers the
    // operations this app performs.
    static let scope = "user,rw"

    private(set) var credentials: HiDriveCredentials
    private let session: URLSession

    init(credentials: HiDriveCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)
    }

    // MARK: - OAuth2 (Authorization Code + Loopback Redirect)

    static func startOAuthFlow() async throws -> HiDriveCredentials {
        let (port, code) = try await listenForAuthCode()
        return try await exchangeCodeForTokens(code: code, redirectPort: port)
    }

    private static func listenForAuthCode() async throws -> (UInt16, String) {
        let listener = try NWListener(using: .tcp, on: .any)
        let guard_ = ContinuationGuard<(UInt16, String)>()

        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<(UInt16, String), Error>) in
            guard_.setContinuation(continuation)

            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    guard let port = listener.port?.rawValue else { return }

                    var components = URLComponents(string: "\(authBaseURL)/client/authorize")!
                    components.queryItems = [
                        URLQueryItem(name: "client_id", value: clientId),
                        URLQueryItem(name: "response_type", value: "code"),
                        URLQueryItem(name: "scope", value: scope),
                        URLQueryItem(name: "redirect_uri", value: "http://localhost:\(port)"),
                    ]

                    if let url = components.url {
                        DispatchQueue.main.async { NSWorkspace.shared.open(url) }
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

                    hiDriveLog.debug("[HiDrive] OAuth callback received: \(requestString.prefix(300))")

                    guard let firstLine = requestString.components(separatedBy: "\r\n").first,
                          let urlPart = firstLine.split(separator: " ").dropFirst().first else {
                        connection.cancel()
                        return
                    }

                    let components = URLComponents(string: "http://localhost\(urlPart)")

                    if let errorParam = components?.queryItems?.first(where: { $0.name == "error" })?.value {
                        hiDriveLog.error("[HiDrive] OAuth error: \(errorParam)")
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
                        let emptyResponse = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        connection.send(content: emptyResponse.data(using: .utf8), completion: .contentProcessed { _ in
                            connection.cancel()
                        })
                        return
                    }

                    let successHTML = "<!DOCTYPE html><html><body><h2>Signed in to HiDrive</h2><p>You can close this window and return to FileFluss.</p></body></html>"
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

    private static func exchangeCodeForTokens(code: String, redirectPort: UInt16) async throws -> HiDriveCredentials {
        let url = URL(string: "\(authBaseURL)/oauth2/token")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

        let encode = { (s: String) -> String in
            s.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? s
        }
        let body = [
            "grant_type=authorization_code",
            "client_id=\(encode(clientId))",
            "client_secret=\(encode(clientSecret))",
            "code=\(encode(code))",
            "redirect_uri=\(encode("http://localhost:\(redirectPort)"))",
        ].joined(separator: "&")
        request.httpBody = body.data(using: .utf8)

        let (data, response) = try await URLSession.shared.data(for: request)
        let bodyStr = String(data: data, encoding: .utf8) ?? ""
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            hiDriveLog.error("[HiDrive] Token exchange failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw CloudProviderError.invalidCredentials
        }

        let tokenResponse = try JSONDecoder().decode(HiDriveTokenResponse.self, from: data)
        let expiresAt = Date().addingTimeInterval(TimeInterval(tokenResponse.expires_in ?? 3600))

        let user = try await fetchUser(accessToken: tokenResponse.access_token)

        return HiDriveCredentials(
            accessToken: tokenResponse.access_token,
            refreshToken: tokenResponse.refresh_token ?? "",
            expiresAt: expiresAt,
            userAlias: user.alias,
            displayName: user.displayName
        )
    }

    private static func fetchUser(accessToken: String) async throws -> (alias: String, displayName: String) {
        let url = URL(string: "\(apiBaseURL)/user/me")!
        var request = URLRequest(url: url)
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.invalidCredentials
        }

        let user = try JSONDecoder().decode(HiDriveUser.self, from: data)
        let name = [user.first_name, user.last_name].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " ")
        let display = !name.isEmpty ? name : (user.email ?? user.alias)
        return (alias: user.alias, displayName: display)
    }

    // MARK: - Token Refresh

    func refreshTokenIfNeeded() async throws -> HiDriveCredentials {
        guard Date() >= credentials.expiresAt.addingTimeInterval(-60) else {
            return credentials
        }
        guard !credentials.refreshToken.isEmpty else {
            throw CloudProviderError.notAuthenticated
        }

        let url = URL(string: "\(Self.authBaseURL)/oauth2/token")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

        let encode = { (s: String) -> String in
            s.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? s
        }
        let body = [
            "grant_type=refresh_token",
            "client_id=\(encode(Self.clientId))",
            "client_secret=\(encode(Self.clientSecret))",
            "refresh_token=\(encode(credentials.refreshToken))",
        ].joined(separator: "&")
        request.httpBody = body.data(using: .utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            hiDriveLog.error("[HiDrive] Token refresh failed: HTTP \(http?.statusCode ?? 0)")
            throw CloudProviderError.notAuthenticated
        }

        let tokenResponse = try JSONDecoder().decode(HiDriveTokenResponse.self, from: data)
        let newCreds = HiDriveCredentials(
            accessToken: tokenResponse.access_token,
            refreshToken: tokenResponse.refresh_token ?? credentials.refreshToken,
            expiresAt: Date().addingTimeInterval(TimeInterval(tokenResponse.expires_in ?? 3600)),
            userAlias: credentials.userAlias,
            displayName: credentials.displayName
        )
        credentials = newCreds
        return newCreds
    }

    func userDisplayName() async throws -> String {
        credentials.displayName
    }

    // MARK: - Path Handling

    /// Converts a FileFluss-relative path ("/Documents/foo") to the absolute
    /// HiDrive path used in `path=` query params (`/users/<alias>/Documents/foo`).
    private func remotePath(_ path: String) -> String {
        let trimmed = path.hasPrefix("/") ? String(path.dropFirst()) : path
        if trimmed.isEmpty {
            return "/users/\(credentials.userAlias)"
        }
        return "/users/\(credentials.userAlias)/\(trimmed)"
    }

    // MARK: - File Operations

    func listFolder(path: String) async throws -> [CloudFileItem] {
        let remote = remotePath(path)
        var items: [HiDriveMember] = []
        var offset = 0
        let limit = 1000

        repeat {
            var comps = URLComponents(string: "\(Self.apiBaseURL)/dir")!
            comps.queryItems = [
                URLQueryItem(name: "path", value: remote),
                URLQueryItem(name: "members", value: "all"),
                URLQueryItem(name: "fields", value: "members,members.name,members.type,members.size,members.mtime,members.mhash,members.id"),
                URLQueryItem(name: "limit", value: "\(offset),\(limit)"),
            ]
            let response: HiDriveDirResponse = try await apiGET(url: comps.url!)
            let batch = response.members ?? []
            items.append(contentsOf: batch)
            if batch.count < limit { break }
            offset += batch.count
        } while true

        return items.map { $0.toCloudFileItem(parentPath: path) }
    }

    func downloadFile(remotePath remotePathArg: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePathArg, to: localURL, onBytes: nil)
    }

    func downloadFile(remotePath remotePathArg: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let remote = remotePath(remotePathArg)
        var comps = URLComponents(string: "\(Self.apiBaseURL)/file")!
        comps.queryItems = [URLQueryItem(name: "path", value: remote)]

        let creds = try await refreshTokenIfNeeded()
        var request = URLRequest(url: comps.url!)
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")

        let (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let errorData = (try? Data(contentsOf: tempURL)) ?? Data()
            let bodyStr = String(data: errorData, encoding: .utf8) ?? ""
            hiDriveLog.error("[HiDrive] Download failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePathArg: String) async throws {
        try await uploadFile(from: localURL, to: remotePathArg, onBytes: nil)
    }

    /// HiDrive uses:
    ///   - POST /file   → create new (multipart, with `dir` + `name`)
    ///   - PUT  /file   → overwrite existing at `path`
    /// Both accept an `mtime` query param (seconds since epoch) to preserve
    /// the original file's modification date.
    func uploadFile(from localURL: URL, to remotePathArg: String, onBytes: ByteProgressHandler?) async throws {
        let dir = (remotePathArg as NSString).deletingLastPathComponent
        let name = (remotePathArg as NSString).lastPathComponent
        let remoteDir = remotePath(dir)
        let remoteFile = remotePath(remotePathArg)

        let attrs = try? FileManager.default.attributesOfItem(atPath: localURL.path)
        let modDate = attrs?[.modificationDate] as? Date
        let data = try Data(contentsOf: localURL)

        // If the file already exists, PUT overwrites in place; otherwise POST
        // creates it. getFileMetadata is cheap (single HEAD-ish GET).
        let exists: Bool
        do {
            _ = try await getFileMetadata(at: remotePathArg)
            exists = true
        } catch {
            exists = false
        }

        let creds = try await refreshTokenIfNeeded()
        if exists {
            var comps = URLComponents(string: "\(Self.apiBaseURL)/file")!
            var items: [URLQueryItem] = [URLQueryItem(name: "path", value: remoteFile)]
            if let modDate {
                items.append(URLQueryItem(name: "mtime", value: "\(Int64(modDate.timeIntervalSince1970))"))
            }
            comps.queryItems = items

            var request = URLRequest(url: comps.url!)
            request.httpMethod = "PUT"
            request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
            request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")

            let (respData, response) = try await session.uploadReportingProgress(for: request, body: data, onBytes: onBytes)
            guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
                let http = response as? HTTPURLResponse
                let bodyStr = String(data: respData, encoding: .utf8) ?? ""
                hiDriveLog.error("[HiDrive] PUT upload failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
                throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
            }
        } else {
            var comps = URLComponents(string: "\(Self.apiBaseURL)/file")!
            var items: [URLQueryItem] = [
                URLQueryItem(name: "dir", value: remoteDir),
                URLQueryItem(name: "name", value: name),
                URLQueryItem(name: "on_exist", value: "overwrite"),
            ]
            if let modDate {
                items.append(URLQueryItem(name: "mtime", value: "\(Int64(modDate.timeIntervalSince1970))"))
            }
            comps.queryItems = items

            let boundary = UUID().uuidString
            var body = Data()
            body.append("--\(boundary)\r\n".data(using: .utf8)!)
            body.append("Content-Disposition: form-data; name=\"file\"; filename=\"\(name)\"\r\n".data(using: .utf8)!)
            body.append("Content-Type: application/octet-stream\r\n\r\n".data(using: .utf8)!)
            body.append(data)
            body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)

            var request = URLRequest(url: comps.url!)
            request.httpMethod = "POST"
            request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
            request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

            let (respData, response) = try await session.uploadReportingProgress(for: request, body: body, onBytes: onBytes)
            guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
                let http = response as? HTTPURLResponse
                let bodyStr = String(data: respData, encoding: .utf8) ?? ""
                hiDriveLog.error("[HiDrive] POST upload failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
                throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
            }
        }
    }

    func deleteItem(at path: String) async throws {
        let remote = remotePath(path)
        // Try /file first; if it's a directory, fall through to /dir.
        do {
            try await apiRequestVoid(method: "DELETE", path: "/file", queryItems: [
                URLQueryItem(name: "path", value: remote),
            ])
            return
        } catch CloudProviderError.notFound {
            // Might be a directory — retry on /dir.
        } catch CloudProviderError.serverError(let code) where code == 400 || code == 403 {
            // HiDrive returns 400/403 when /file is called on a directory.
        }

        try await apiRequestVoid(method: "DELETE", path: "/dir", queryItems: [
            URLQueryItem(name: "path", value: remote),
            URLQueryItem(name: "recursive", value: "true"),
        ])
    }

    func createFolder(at path: String) async throws {
        let remote = remotePath(path)
        do {
            try await apiRequestVoid(method: "POST", path: "/dir", queryItems: [
                URLQueryItem(name: "path", value: remote),
                URLQueryItem(name: "parent_mode", value: "create_parents"),
            ])
        } catch CloudProviderError.serverError(let code) where code == 409 {
            // Already exists — idempotent.
        }
    }

    func renameItem(at path: String, to newName: String) async throws {
        let srcRemote = remotePath(path)
        let parent = (path as NSString).deletingLastPathComponent
        let dstRelative = parent == "/" ? "/\(newName)" : "\(parent)/\(newName)"
        let dstRemote = remotePath(dstRelative)

        // Try file move; fall back to dir move.
        do {
            try await apiRequestVoid(method: "POST", path: "/file/rename", queryItems: [
                URLQueryItem(name: "src", value: srcRemote),
                URLQueryItem(name: "dst", value: dstRemote),
            ])
            return
        } catch {
            // fall through
        }
        try await apiRequestVoid(method: "POST", path: "/dir/rename", queryItems: [
            URLQueryItem(name: "src", value: srcRemote),
            URLQueryItem(name: "dst", value: dstRemote),
        ])
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        let remote = remotePath(path)
        var comps = URLComponents(string: "\(Self.apiBaseURL)/meta")!
        comps.queryItems = [
            URLQueryItem(name: "path", value: remote),
            URLQueryItem(name: "fields", value: "name,type,size,mtime,mhash,id"),
        ]
        let member: HiDriveMember = try await apiGET(url: comps.url!)
        let parentPath = (path as NSString).deletingLastPathComponent
        return member.toCloudFileItem(parentPath: parentPath == "" ? "/" : parentPath)
    }

    func folderSize(at path: String) async throws -> Int64 {
        let remote = remotePath(path)
        var comps = URLComponents(string: "\(Self.apiBaseURL)/meta")!
        comps.queryItems = [
            URLQueryItem(name: "path", value: remote),
            URLQueryItem(name: "fields", value: "size"),
        ]
        let member: HiDriveMember = try await apiGET(url: comps.url!)
        return member.size ?? 0
    }

    // MARK: - HTTP helpers

    private func apiGET<T: Decodable>(url: URL) async throws -> T {
        let creds = try await refreshTokenIfNeeded()
        var request = URLRequest(url: url)
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }
        guard (200...299).contains(http.statusCode) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            hiDriveLog.error("[HiDrive] GET \(url.path) → HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http.statusCode)
        }
        return try JSONDecoder().decode(T.self, from: data)
    }

    private func apiRequestVoid(method: String, path: String, queryItems: [URLQueryItem]) async throws {
        var comps = URLComponents(string: "\(Self.apiBaseURL)\(path)")!
        comps.queryItems = queryItems

        let creds = try await refreshTokenIfNeeded()
        var request = URLRequest(url: comps.url!)
        request.httpMethod = method
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }
        guard (200...299).contains(http.statusCode) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            hiDriveLog.error("[HiDrive] \(method) \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http.statusCode)
        }
    }

    private static func mapHTTPError(statusCode: Int) -> CloudProviderError {
        switch statusCode {
        case 401: return .notAuthenticated
        case 403: return .unauthorized
        case 404: return .notFound("Resource not found")
        case 409: return .serverError(409)
        case 429: return .rateLimited
        case 507: return .quotaExceeded
        default: return .serverError(statusCode)
        }
    }
}

// MARK: - Continuation guard (OAuth loopback)

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

// MARK: - HiDrive API Response Types

private struct HiDriveTokenResponse: Decodable {
    let access_token: String
    let refresh_token: String?
    let token_type: String?
    let expires_in: Int?
    let scope: String?
    let userid: String?
    let alias: String?
}

private struct HiDriveUser: Decodable {
    let alias: String
    let email: String?
    let first_name: String?
    let last_name: String?
}

struct HiDriveDirResponse: Decodable {
    let members: [HiDriveMember]?
    let name: String?
    let type: String?
    let id: String?
}

struct HiDriveMember: Decodable {
    let name: String
    let type: String?          // "file" | "dir"
    let size: Int64?
    let mtime: Double?         // Unix seconds (may be integer or float)
    let mhash: String?
    let id: String?

    var isDirectory: Bool { type == "dir" }

    func toCloudFileItem(parentPath: String) -> CloudFileItem {
        let itemPath: String
        if parentPath == "/" {
            itemPath = "/\(name)"
        } else {
            itemPath = "\(parentPath)/\(name)"
        }
        let modDate: Date
        if let mtime {
            modDate = Date(timeIntervalSince1970: mtime)
        } else {
            modDate = .distantPast
        }
        return CloudFileItem(
            id: (isDirectory ? "d" : "f") + (id ?? itemPath),
            name: name,
            path: itemPath,
            isDirectory: isDirectory,
            size: size ?? 0,
            modificationDate: modDate,
            checksum: mhash
        )
    }
}
