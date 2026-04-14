import AppKit
import Foundation
import Network
import os
import CommonCrypto

private let dropboxLog = Logger(subsystem: "com.rana.FileFluss", category: "dropbox")

struct DropboxCredentials: Codable, Sendable {
    let accessToken: String
    let refreshToken: String
    let expiresAt: Date
    let accountId: String
    let displayName: String
}

actor DropboxAPIClient {
    // Dropbox App Key (PKCE flow — no client secret needed)
    static let appKey = "b5v4zgbnycbuimj"
    static let appSecret = "xcvb8frfc2jyzvj"

    private(set) var credentials: DropboxCredentials
    private let session: URLSession

    private static let rpcURL = "https://api.dropboxapi.com/2"
    private static let contentURL = "https://content.dropboxapi.com/2"

    init(credentials: DropboxCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)
    }

    // MARK: - OAuth2 (PKCE Loopback Redirect)

    static func startOAuthFlow() async throws -> DropboxCredentials {
        let codeVerifier = generateCodeVerifier()
        let codeChallenge = generateCodeChallenge(from: codeVerifier)

        let (port, authCode) = try await listenForAuthCode(codeVerifier: codeVerifier, codeChallenge: codeChallenge)

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

                    var components = URLComponents(string: "https://www.dropbox.com/oauth2/authorize")!
                    components.queryItems = [
                        URLQueryItem(name: "client_id", value: appKey),
                        URLQueryItem(name: "redirect_uri", value: "http://localhost:\(port)"),
                        URLQueryItem(name: "response_type", value: "code"),
                        URLQueryItem(name: "code_challenge", value: codeChallenge),
                        URLQueryItem(name: "code_challenge_method", value: "S256"),
                        URLQueryItem(name: "token_access_type", value: "offline"),
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

                    dropboxLog.debug("[Dropbox] OAuth callback received: \(requestString.prefix(300))")

                    guard let firstLine = requestString.components(separatedBy: "\r\n").first,
                          let urlPart = firstLine.split(separator: " ").dropFirst().first else {
                        connection.cancel()
                        return
                    }

                    let components = URLComponents(string: "http://localhost\(urlPart)")

                    if let errorParam = components?.queryItems?.first(where: { $0.name == "error" })?.value {
                        dropboxLog.error("[Dropbox] OAuth error: \(errorParam)")
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
                        dropboxLog.debug("[Dropbox] Ignoring non-auth request: \(String(urlPart).prefix(100))")
                        let emptyResponse = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        connection.send(content: emptyResponse.data(using: .utf8), completion: .contentProcessed { _ in
                            connection.cancel()
                        })
                        return
                    }

                    let successHTML = "<!DOCTYPE html><html><body><h2>Signed in to Dropbox</h2><p>You can close this window and return to FileFluss.</p></body></html>"
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

    private static func exchangeCodeForTokens(code: String, codeVerifier: String, redirectPort: UInt16) async throws -> DropboxCredentials {
        let url = URL(string: "https://api.dropboxapi.com/oauth2/token")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

        let encode = { (s: String) -> String in
            s.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? s
        }
        let bodyParams = [
            "code=\(encode(code))",
            "grant_type=authorization_code",
            "client_id=\(encode(appKey))",
            "client_secret=\(encode(appSecret))",
            "redirect_uri=\(encode("http://localhost:\(redirectPort)"))",
            "code_verifier=\(encode(codeVerifier))",
        ].joined(separator: "&")
        request.httpBody = bodyParams.data(using: .utf8)

        let (data, response) = try await URLSession.shared.data(for: request)
        let bodyStr = String(data: data, encoding: .utf8) ?? ""
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            dropboxLog.error("[Dropbox] Token exchange failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw CloudProviderError.invalidCredentials
        }

        dropboxLog.info("[Dropbox] Token exchange response: \(bodyStr.prefix(200))")

        let tokenResponse = try JSONDecoder().decode(DropboxTokenResponse.self, from: data)
        let expiresAt = Date().addingTimeInterval(TimeInterval(tokenResponse.expires_in))

        // Fetch user info
        let userInfo = try await fetchCurrentAccount(accessToken: tokenResponse.access_token)

        return DropboxCredentials(
            accessToken: tokenResponse.access_token,
            refreshToken: tokenResponse.refresh_token ?? "",
            expiresAt: expiresAt,
            accountId: userInfo.accountId,
            displayName: userInfo.displayName
        )
    }

    private static func fetchCurrentAccount(accessToken: String) async throws -> (accountId: String, displayName: String) {
        let url = URL(string: "\(rpcURL)/users/get_current_account")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        // Dropbox RPC endpoints require a null body or empty JSON
        request.httpBody = "null".data(using: .utf8)

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            return (accountId: "unknown", displayName: "Unknown")
        }

        let account = try JSONDecoder().decode(DropboxAccountInfo.self, from: data)
        return (accountId: account.account_id, displayName: account.name.display_name)
    }

    // MARK: - Token Refresh

    func refreshTokenIfNeeded() async throws -> DropboxCredentials {
        guard Date() >= credentials.expiresAt.addingTimeInterval(-60) else {
            return credentials
        }

        guard !credentials.refreshToken.isEmpty else {
            throw CloudProviderError.notAuthenticated
        }

        let url = URL(string: "https://api.dropboxapi.com/oauth2/token")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

        let body = [
            "grant_type=refresh_token",
            "refresh_token=\(credentials.refreshToken)",
            "client_id=\(Self.appKey)",
            "client_secret=\(Self.appSecret)",
        ].joined(separator: "&")
        request.httpBody = body.data(using: .utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            dropboxLog.error("[Dropbox] Token refresh failed: HTTP \(http?.statusCode ?? 0)")
            throw CloudProviderError.notAuthenticated
        }

        let tokenResponse = try JSONDecoder().decode(DropboxTokenResponse.self, from: data)
        let newCreds = DropboxCredentials(
            accessToken: tokenResponse.access_token,
            refreshToken: tokenResponse.refresh_token ?? credentials.refreshToken,
            expiresAt: Date().addingTimeInterval(TimeInterval(tokenResponse.expires_in)),
            accountId: credentials.accountId,
            displayName: credentials.displayName
        )
        credentials = newCreds
        return newCreds
    }

    func userDisplayName() async throws -> String {
        credentials.displayName
    }

    // MARK: - File Operations

    /// Dropbox uses path-based access. Root is "" (empty string).
    /// We normalize our "/" root to "" for Dropbox API calls.
    private func dropboxPath(_ path: String) -> String {
        let p = path == "/" ? "" : path
        return p
    }

    func listFolder(path: String) async throws -> [CloudFileItem] {
        let dbPath = dropboxPath(path)

        struct ListFolderRequest: Encodable {
            let path: String
            let recursive: Bool
            let include_deleted: Bool
            let limit: Int
        }

        let requestBody = ListFolderRequest(
            path: dbPath,
            recursive: false,
            include_deleted: false,
            limit: 2000
        )

        var allEntries: [DropboxEntry] = []

        let firstResponse: DropboxListFolderResponse = try await rpcRequest(
            path: "/files/list_folder",
            body: requestBody
        )
        allEntries.append(contentsOf: firstResponse.entries)

        var cursor = firstResponse.cursor
        var hasMore = firstResponse.has_more

        while hasMore {
            struct ContinueRequest: Encodable {
                let cursor: String
            }
            let nextResponse: DropboxListFolderResponse = try await rpcRequest(
                path: "/files/list_folder/continue",
                body: ContinueRequest(cursor: cursor)
            )
            allEntries.append(contentsOf: nextResponse.entries)
            cursor = nextResponse.cursor
            hasMore = nextResponse.has_more
        }

        return allEntries.compactMap { $0.toCloudFileItem() }
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let dbPath = dropboxPath(remotePath)

        struct DownloadArg: Encodable {
            let path: String
        }

        let arg = DownloadArg(path: dbPath)
        let argData = try JSONEncoder().encode(arg)
        let argString = Self.escapeNonASCII(String(data: argData, encoding: .utf8) ?? "")

        let creds = try await refreshTokenIfNeeded()
        let url = URL(string: "\(Self.contentURL)/files/download")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue(argString, forHTTPHeaderField: "Dropbox-API-Arg")

        var (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        if let http = response as? HTTPURLResponse, http.statusCode == 401 {
            dropboxLog.info("[Dropbox] Got 401 on download, refreshing token and retrying")
            let newCreds = try await forceRefreshToken()
            var retryRequest = URLRequest(url: url)
            retryRequest.httpMethod = "POST"
            retryRequest.setValue("Bearer \(newCreds.accessToken)", forHTTPHeaderField: "Authorization")
            retryRequest.setValue(argString, forHTTPHeaderField: "Dropbox-API-Arg")
            (tempURL, response) = try await session.downloadReportingProgress(for: retryRequest, onBytes: onBytes)
        }

        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let errorData = (try? Data(contentsOf: tempURL)) ?? Data()
            let bodyStr = String(data: errorData, encoding: .utf8) ?? ""
            dropboxLog.error("[Dropbox] Download failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0, responseBody: errorData)
        }
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let dbPath = dropboxPath(remotePath)
        let fileData = try Data(contentsOf: localURL)

        if fileData.count <= 150_000_000 {
            try await simpleUpload(data: fileData, path: dbPath, onBytes: onBytes)
        } else {
            try await sessionUpload(from: localURL, fileSize: fileData.count, path: dbPath, onBytes: onBytes)
        }
    }

    private func simpleUpload(data: Data, path: String, onBytes: ByteProgressHandler? = nil) async throws {
        struct UploadArg: Encodable {
            let path: String
            let mode: String
            let autorename: Bool
            let mute: Bool
        }

        let arg = UploadArg(path: path, mode: "add", autorename: false, mute: false)
        let argData = try JSONEncoder().encode(arg)
        let argString = Self.escapeNonASCII(String(data: argData, encoding: .utf8) ?? "")

        let creds = try await refreshTokenIfNeeded()
        let url = URL(string: "\(Self.contentURL)/files/upload")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.setValue(argString, forHTTPHeaderField: "Dropbox-API-Arg")

        var (responseData, response) = try await session.uploadReportingProgress(for: request, body: data, onBytes: onBytes)
        if let http = response as? HTTPURLResponse, http.statusCode == 401 {
            dropboxLog.info("[Dropbox] Got 401 on upload, refreshing token and retrying")
            let newCreds = try await forceRefreshToken()
            var retryRequest = URLRequest(url: url)
            retryRequest.httpMethod = "POST"
            retryRequest.setValue("Bearer \(newCreds.accessToken)", forHTTPHeaderField: "Authorization")
            retryRequest.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
            retryRequest.setValue(argString, forHTTPHeaderField: "Dropbox-API-Arg")
            (responseData, response) = try await session.uploadReportingProgress(for: retryRequest, body: data, onBytes: onBytes)
        }

        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: responseData, encoding: .utf8) ?? ""
            dropboxLog.error("[Dropbox] Upload failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0, responseBody: responseData)
        }
    }

    private func sessionUpload(from localURL: URL, fileSize: Int, path: String, onBytes: ByteProgressHandler? = nil) async throws {
        let chunkSize = 150_000_000 // 150MB
        let fileData = try Data(contentsOf: localURL)

        // Step 1: Start session
        struct StartArg: Encodable {
            let close: Bool
        }

        let startArgData = try JSONEncoder().encode(StartArg(close: false))
        let startArgString = Self.escapeNonASCII(String(data: startArgData, encoding: .utf8) ?? "")

        let creds = try await refreshTokenIfNeeded()
        let startURL = URL(string: "\(Self.contentURL)/files/upload_session/start")!
        var startRequest = URLRequest(url: startURL)
        startRequest.httpMethod = "POST"
        startRequest.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        startRequest.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        startRequest.setValue(startArgString, forHTTPHeaderField: "Dropbox-API-Arg")
        startRequest.httpBody = Data() // empty body for start

        let (startData, startResponse) = try await session.data(for: startRequest)
        guard let startHttp = startResponse as? HTTPURLResponse, (200...299).contains(startHttp.statusCode) else {
            let startHttp = startResponse as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: startHttp?.statusCode ?? 0, responseBody: startData)
        }

        struct SessionStartResult: Decodable {
            let session_id: String
        }
        let sessionResult = try JSONDecoder().decode(SessionStartResult.self, from: startData)
        let sessionId = sessionResult.session_id

        // Step 2: Append chunks
        var offset = 0
        while offset < fileSize {
            let end = min(offset + chunkSize, fileSize)
            let chunk = fileData[offset..<end]
            let isLast = end >= fileSize

            if isLast {
                // Step 3: Finish
                struct FinishArg: Encodable {
                    let cursor: SessionCursor
                    let commit: CommitInfo
                }
                struct SessionCursor: Encodable {
                    let session_id: String
                    let offset: Int
                }
                struct CommitInfo: Encodable {
                    let path: String
                    let mode: String
                    let autorename: Bool
                    let mute: Bool
                }

                let finishArg = FinishArg(
                    cursor: SessionCursor(session_id: sessionId, offset: offset),
                    commit: CommitInfo(path: path, mode: "add", autorename: false, mute: false)
                )
                let finishArgData = try JSONEncoder().encode(finishArg)
                let finishArgString = Self.escapeNonASCII(String(data: finishArgData, encoding: .utf8) ?? "")

                let finishCreds = try await refreshTokenIfNeeded()
                let finishURL = URL(string: "\(Self.contentURL)/files/upload_session/finish")!
                var finishRequest = URLRequest(url: finishURL)
                finishRequest.httpMethod = "POST"
                finishRequest.setValue("Bearer \(finishCreds.accessToken)", forHTTPHeaderField: "Authorization")
                finishRequest.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
                finishRequest.setValue(finishArgString, forHTTPHeaderField: "Dropbox-API-Arg")

                let (finishData, finishResponse) = try await session.uploadReportingProgress(for: finishRequest, body: Data(chunk), onBytes: onBytes)
                guard let finishHttp = finishResponse as? HTTPURLResponse, (200...299).contains(finishHttp.statusCode) else {
                    let finishHttp = finishResponse as? HTTPURLResponse
                    throw Self.mapHTTPError(statusCode: finishHttp?.statusCode ?? 0, responseBody: finishData)
                }
            } else {
                // Append
                struct AppendArg: Encodable {
                    let cursor: SessionCursor
                    let close: Bool
                }
                struct SessionCursor: Encodable {
                    let session_id: String
                    let offset: Int
                }

                let appendArg = AppendArg(
                    cursor: SessionCursor(session_id: sessionId, offset: offset),
                    close: false
                )
                let appendArgData = try JSONEncoder().encode(appendArg)
                let appendArgString = Self.escapeNonASCII(String(data: appendArgData, encoding: .utf8) ?? "")

                let appendCreds = try await refreshTokenIfNeeded()
                let appendURL = URL(string: "\(Self.contentURL)/files/upload_session/append_v2")!
                var appendRequest = URLRequest(url: appendURL)
                appendRequest.httpMethod = "POST"
                appendRequest.setValue("Bearer \(appendCreds.accessToken)", forHTTPHeaderField: "Authorization")
                appendRequest.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
                appendRequest.setValue(appendArgString, forHTTPHeaderField: "Dropbox-API-Arg")

                let (appendData, appendResponse) = try await session.uploadReportingProgress(for: appendRequest, body: Data(chunk), onBytes: onBytes)
                guard let appendHttp = appendResponse as? HTTPURLResponse, (200...299).contains(appendHttp.statusCode) else {
                    let appendHttp = appendResponse as? HTTPURLResponse
                    throw Self.mapHTTPError(statusCode: appendHttp?.statusCode ?? 0, responseBody: appendData)
                }
            }

            offset = end
        }
    }

    func deleteItem(at path: String) async throws {
        let dbPath = dropboxPath(path)

        struct DeleteArg: Encodable {
            let path: String
        }

        try await rpcRequestVoid(
            path: "/files/delete_v2",
            body: DeleteArg(path: dbPath)
        )
    }

    func createFolder(at path: String) async throws {
        let dbPath = dropboxPath(path)

        struct CreateFolderArg: Encodable {
            let path: String
            let autorename: Bool
        }

        try await rpcRequestVoid(
            path: "/files/create_folder_v2",
            body: CreateFolderArg(path: dbPath, autorename: false)
        )
    }

    func renameItem(at path: String, to newName: String) async throws {
        let dbPath = dropboxPath(path)
        let parentPath = (dbPath as NSString).deletingLastPathComponent
        let newPath = parentPath.isEmpty ? "/\(newName)" : "\(parentPath)/\(newName)"

        struct MoveArg: Encodable {
            let from_path: String
            let to_path: String
            let autorename: Bool
        }

        try await rpcRequestVoid(
            path: "/files/move_v2",
            body: MoveArg(from_path: dbPath, to_path: newPath, autorename: false)
        )
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        let dbPath = dropboxPath(path)

        struct MetadataArg: Encodable {
            let path: String
        }

        let entry: DropboxEntry = try await rpcRequest(
            path: "/files/get_metadata",
            body: MetadataArg(path: dbPath)
        )

        guard let item = entry.toCloudFileItem() else {
            throw CloudProviderError.invalidResponse
        }
        return item
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

    // MARK: - RPC Helper

    /// Force-refresh the token (ignoring expiry check) and update credentials.
    private func forceRefreshToken() async throws -> DropboxCredentials {
        guard !credentials.refreshToken.isEmpty else {
            throw CloudProviderError.notAuthenticated
        }

        let url = URL(string: "https://api.dropboxapi.com/oauth2/token")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

        let body = [
            "grant_type=refresh_token",
            "refresh_token=\(credentials.refreshToken)",
            "client_id=\(Self.appKey)",
            "client_secret=\(Self.appSecret)",
        ].joined(separator: "&")
        request.httpBody = body.data(using: .utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            dropboxLog.error("[Dropbox] Force token refresh failed: HTTP \(http?.statusCode ?? 0)")
            throw CloudProviderError.notAuthenticated
        }

        let tokenResponse = try JSONDecoder().decode(DropboxTokenResponse.self, from: data)
        let newCreds = DropboxCredentials(
            accessToken: tokenResponse.access_token,
            refreshToken: tokenResponse.refresh_token ?? credentials.refreshToken,
            expiresAt: Date().addingTimeInterval(TimeInterval(tokenResponse.expires_in)),
            accountId: credentials.accountId,
            displayName: credentials.displayName
        )
        credentials = newCreds
        return newCreds
    }

    // MARK: - Search

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
        struct SearchRequest: Encodable {
            let query: String
            let options: SearchOptions?
        }
        struct SearchOptions: Encodable {
            let path: String?
            let max_results: Int
        }
        struct SearchResponse: Decodable {
            let matches: [SearchMatch]
            let has_more: Bool
        }
        struct SearchMatch: Decodable {
            let metadata: SearchMatchMetadata
        }
        struct SearchMatchMetadata: Decodable {
            let metadata: DropboxEntry
        }

        let searchPath = path.flatMap { dropboxPath($0) }
        let requestBody = SearchRequest(
            query: query,
            options: SearchOptions(path: searchPath, max_results: 100)
        )

        let response: SearchResponse = try await rpcRequest(
            path: "/files/search_v2",
            body: requestBody
        )

        return response.matches.compactMap { $0.metadata.metadata.toCloudFileItem() }
    }

    private func rpcRequest<B: Encodable, T: Decodable>(path: String, body: B) async throws -> T {
        let creds = try await refreshTokenIfNeeded()
        let encodedBody = try JSONEncoder().encode(body)
        let url = URL(string: "\(Self.rpcURL)\(path)")!

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = encodedBody

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        // Retry once on 401 with a forced token refresh
        if http.statusCode == 401 {
            dropboxLog.info("[Dropbox] Got 401 on \(path), refreshing token and retrying")
            let newCreds = try await forceRefreshToken()
            var retryRequest = URLRequest(url: url)
            retryRequest.httpMethod = "POST"
            retryRequest.setValue("Bearer \(newCreds.accessToken)", forHTTPHeaderField: "Authorization")
            retryRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
            retryRequest.httpBody = encodedBody

            let (retryData, retryResponse) = try await session.data(for: retryRequest)
            guard let retryHttp = retryResponse as? HTTPURLResponse, (200...299).contains(retryHttp.statusCode) else {
                let retryHttp = retryResponse as? HTTPURLResponse
                let bodyStr = String(data: retryData, encoding: .utf8) ?? ""
                dropboxLog.error("[Dropbox] Retry POST \(path) → HTTP \(retryHttp?.statusCode ?? 0): \(bodyStr.prefix(1000))")
                throw Self.mapHTTPError(statusCode: retryHttp?.statusCode ?? 0, responseBody: retryData)
            }
            return try JSONDecoder().decode(T.self, from: retryData)
        }

        guard (200...299).contains(http.statusCode) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            dropboxLog.error("[Dropbox] POST \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(1000))")
            throw Self.mapHTTPError(statusCode: http.statusCode, responseBody: data)
        }

        return try JSONDecoder().decode(T.self, from: data)
    }

    private func rpcRequestVoid<B: Encodable>(path: String, body: B) async throws {
        let creds = try await refreshTokenIfNeeded()
        let encodedBody = try JSONEncoder().encode(body)
        let url = URL(string: "\(Self.rpcURL)\(path)")!

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = encodedBody

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        // Retry once on 401 with a forced token refresh
        if http.statusCode == 401 {
            dropboxLog.info("[Dropbox] Got 401 on \(path), refreshing token and retrying")
            let newCreds = try await forceRefreshToken()
            var retryRequest = URLRequest(url: url)
            retryRequest.httpMethod = "POST"
            retryRequest.setValue("Bearer \(newCreds.accessToken)", forHTTPHeaderField: "Authorization")
            retryRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
            retryRequest.httpBody = encodedBody

            let (retryData, retryResponse) = try await session.data(for: retryRequest)
            guard let retryHttp = retryResponse as? HTTPURLResponse, (200...299).contains(retryHttp.statusCode) else {
                let retryHttp = retryResponse as? HTTPURLResponse
                let bodyStr = String(data: retryData, encoding: .utf8) ?? ""
                dropboxLog.error("[Dropbox] Retry POST \(path) → HTTP \(retryHttp?.statusCode ?? 0): \(bodyStr.prefix(1000))")
                throw Self.mapHTTPError(statusCode: retryHttp?.statusCode ?? 0, responseBody: retryData)
            }
            return
        }

        guard (200...299).contains(http.statusCode) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            dropboxLog.error("[Dropbox] POST \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(1000))")
            throw Self.mapHTTPError(statusCode: http.statusCode, responseBody: data)
        }
    }

    // MARK: - Dropbox-API-Arg Header Encoding

    /// Dropbox requires non-ASCII characters in the Dropbox-API-Arg header to be
    /// escaped as \uXXXX sequences per their HTTP header encoding requirements.
    private static func escapeNonASCII(_ string: String) -> String {
        var result = ""
        for scalar in string.unicodeScalars {
            if scalar.value > 127 {
                result += String(format: "\\u%04x", scalar.value)
            } else {
                result.append(Character(scalar))
            }
        }
        return result
    }

    // MARK: - Error Mapping

    private static func mapHTTPError(statusCode: Int, responseBody: Data? = nil) -> CloudProviderError {
        switch statusCode {
        case 401: return .notAuthenticated
        case 403: return .unauthorized
        case 409:
            // Dropbox uses 409 for endpoint-specific errors (path not found, conflict, etc.)
            if let data = responseBody,
               let bodyStr = String(data: data, encoding: .utf8),
               bodyStr.contains("not_found") {
                return .notFound("Path not found")
            }
            return .serverError(409)
        case 429: return .rateLimited
        default: return .serverError(statusCode)
        }
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

// MARK: - Dropbox API Response Types

private struct DropboxTokenResponse: Decodable {
    let access_token: String
    let refresh_token: String?
    let expires_in: Int
    let token_type: String
    let account_id: String?
}

private struct DropboxAccountInfo: Decodable {
    let account_id: String
    let name: DropboxName

    struct DropboxName: Decodable {
        let display_name: String
    }
}

struct DropboxListFolderResponse: Decodable {
    let entries: [DropboxEntry]
    let cursor: String
    let has_more: Bool
}

struct DropboxEntry: Decodable {
    let tag: String
    let name: String
    let path_lower: String?
    let path_display: String?
    let id: String?
    let size: Int64?
    let server_modified: String?
    let content_hash: String?

    enum CodingKeys: String, CodingKey {
        case tag = ".tag"
        case name
        case path_lower
        case path_display
        case id
        case size
        case server_modified
        case content_hash
    }

    var isFolder: Bool { tag == "folder" }

    func toCloudFileItem() -> CloudFileItem? {
        guard tag == "file" || tag == "folder" else { return nil }
        let itemPath = path_display ?? path_lower ?? "/\(name)"

        let modDate: Date
        if let dateStr = server_modified {
            let formatter = ISO8601DateFormatter()
            formatter.formatOptions = [.withInternetDateTime]
            modDate = formatter.date(from: dateStr) ?? Date.distantPast
        } else {
            modDate = Date.distantPast
        }

        return CloudFileItem(
            id: id ?? (isFolder ? "d_\(name)" : "f_\(name)"),
            name: name,
            path: itemPath,
            isDirectory: isFolder,
            size: size ?? 0,
            modificationDate: modDate,
            checksum: content_hash
        )
    }
}

