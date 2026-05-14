import AppKit
import Foundation
import Network
import os
import Security
import CommonCrypto

private let oneDriveLog = Logger(subsystem: "com.rana.FileFluss", category: "oneDrive")

struct OneDriveCredentials: Codable, Sendable {
    let accessToken: String
    let refreshToken: String
    let expiresAt: Date
    let userEmail: String
}

actor OneDriveAPIClient {
    // FileFluss app registration in Microsoft Entra ID (Azure AD). Configured
    // as a public/Native client with `http://localhost` registered for the
    // "Mobile and desktop" platform — Microsoft accepts any port on that URI,
    // which is what the loopback PKCE flow below relies on.
    static let clientId = "23eeca59-6b12-4ea7-8999-8d07e2af558e"
    static let scopes = "https://graph.microsoft.com/Files.ReadWrite.All https://graph.microsoft.com/User.Read offline_access"

    private(set) var credentials: OneDriveCredentials
    private let session: URLSession
    private let graphURL = "https://graph.microsoft.com/v1.0"
    private let authURL = "https://login.microsoftonline.com/common/oauth2/v2.0"

    /// When a token refresh is in flight, concurrent callers must await
    /// the same Task rather than firing parallel POSTs. Actor isolation
    /// alone doesn't fix this because every `await` releases the actor.
    private var inflightRefresh: Task<OneDriveCredentials, Error>?

    init(credentials: OneDriveCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)
    }

    // MARK: - OAuth2 (Loopback Redirect with PKCE)

    /// Opens the user's browser at the Microsoft sign-in page and waits for
    /// the authorization code on a one-shot loopback HTTP server. Mirrors
    /// the Google/Dropbox/Box flow so OneDrive re-auth slots into the same
    /// path (`SyncViewModel.reauthenticate(accountId:)`).
    static func startOAuthFlow() async throws -> OneDriveCredentials {
        let codeVerifier = generateCodeVerifier()
        let codeChallenge = generateCodeChallenge(from: codeVerifier)
        let expectedState = generateState()

        let (port, authCode) = try await listenForAuthCode(codeChallenge: codeChallenge, expectedState: expectedState)
        return try await exchangeCodeForTokens(code: authCode, codeVerifier: codeVerifier, redirectPort: port)
    }

    private static func listenForAuthCode(codeChallenge: String, expectedState: String) async throws -> (UInt16, String) {
        let listener = try NWListener(using: .tcp, on: .any)
        let guard_ = OneDriveContinuationGuard<(UInt16, String)>()

        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<(UInt16, String), Error>) in
            guard_.setContinuation(continuation)

            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    guard let port = listener.port?.rawValue else { return }

                    var components = URLComponents(string: "https://login.microsoftonline.com/common/oauth2/v2.0/authorize")!
                    components.queryItems = [
                        URLQueryItem(name: "client_id", value: clientId),
                        URLQueryItem(name: "redirect_uri", value: "http://localhost:\(port)"),
                        URLQueryItem(name: "response_type", value: "code"),
                        URLQueryItem(name: "response_mode", value: "query"),
                        URLQueryItem(name: "scope", value: scopes),
                        URLQueryItem(name: "code_challenge", value: codeChallenge),
                        URLQueryItem(name: "code_challenge_method", value: "S256"),
                        URLQueryItem(name: "prompt", value: "select_account"),
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

                    oneDriveLog.debug("[OneDrive] OAuth callback received: \(requestString.prefix(300))")

                    guard let firstLine = requestString.components(separatedBy: "\r\n").first,
                          let urlPart = firstLine.split(separator: " ").dropFirst().first else {
                        connection.cancel()
                        return
                    }

                    let components = URLComponents(string: "http://localhost\(urlPart)")

                    if let errorParam = components?.queryItems?.first(where: { $0.name == "error" })?.value {
                        oneDriveLog.error("[OneDrive] OAuth error: \(errorParam)")
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
                        oneDriveLog.debug("[OneDrive] Ignoring non-auth request: \(String(urlPart).prefix(100))")
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
                        oneDriveLog.error("[OneDrive] OAuth state mismatch — rejecting callback")
                        let errorHTML = "<!DOCTYPE html><html><body><h2>Authentication failed</h2><p>Invalid state parameter. You can close this window.</p></body></html>"
                        let response = "HTTP/1.1 400 Bad Request\r\nContent-Type: text/html\r\nContent-Length: \(errorHTML.utf8.count)\r\nConnection: close\r\n\r\n\(errorHTML)"
                        connection.send(content: response.data(using: .utf8), completion: .contentProcessed { _ in
                            connection.cancel()
                        })
                        listener.cancel()
                        guard_.resume(throwing: CloudProviderError.unauthorized)
                        return
                    }

                    let successHTML = "<!DOCTYPE html><html><body><h2>Signed in to OneDrive</h2><p>You can close this window and return to FileFluss.</p></body></html>"
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

    private static func exchangeCodeForTokens(code: String, codeVerifier: String, redirectPort: UInt16) async throws -> OneDriveCredentials {
        let url = URL(string: "https://login.microsoftonline.com/common/oauth2/v2.0/token")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

        let encode = { (s: String) -> String in
            s.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? s
        }
        let bodyParams = [
            "client_id=\(encode(clientId))",
            "scope=\(encode(scopes))",
            "code=\(encode(code))",
            "redirect_uri=\(encode("http://localhost:\(redirectPort)"))",
            "grant_type=authorization_code",
            "code_verifier=\(encode(codeVerifier))",
        ].joined(separator: "&")
        request.httpBody = bodyParams.data(using: .utf8)

        let (data, response) = try await URLSession.shared.data(for: request)
        let bodyStr = String(data: data, encoding: .utf8) ?? ""
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            oneDriveLog.error("[OneDrive] Token exchange failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw CloudProviderError.invalidCredentials
        }

        let tokenResponse = try JSONDecoder().decode(TokenResponse.self, from: data)
        let expiresAt = Date().addingTimeInterval(TimeInterval(tokenResponse.expires_in))

        let email = try await fetchUserEmail(accessToken: tokenResponse.access_token)
        oneDriveLog.info("[OneDrive] Authenticated as \(email, privacy: .public)")

        return OneDriveCredentials(
            accessToken: tokenResponse.access_token,
            refreshToken: tokenResponse.refresh_token ?? "",
            expiresAt: expiresAt,
            userEmail: email
        )
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
        if let inflight = inflightRefresh {
            return try await inflight.value
        }
        guard Date() >= credentials.expiresAt.addingTimeInterval(-60) else {
            return credentials
        }
        return try await startRefresh()
    }

    /// Force a refresh regardless of the cached `expiresAt`. Used after a
    /// 401 from a Graph call — Microsoft can invalidate access tokens
    /// server-side before our local expiry kicks in.
    private func forceRefresh() async throws -> OneDriveCredentials {
        if let inflight = inflightRefresh {
            return try await inflight.value
        }
        return try await startRefresh()
    }

    private func startRefresh() async throws -> OneDriveCredentials {
        guard !credentials.refreshToken.isEmpty else {
            throw CloudProviderError.notAuthenticated
        }
        let task = Task<OneDriveCredentials, Error> { [self] in
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

    private func performTokenRefresh() async throws -> OneDriveCredentials {
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

        var response: GraphListResponse = try await graphRequest(.get, path: endpoint, queryItems: [
            URLQueryItem(name: "$top", value: "1000"),
            URLQueryItem(name: "$orderby", value: "name asc"),
        ])
        var all = response.value
        while let nextURL = response.nextLink {
            response = try await graphRequestAbsolute(url: nextURL)
            all.append(contentsOf: response.value)
        }

        return all.map { $0.toCloudFileItem(parentPath: path) }
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let encodedPath = remotePath.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? remotePath
        let endpoint = "/me/drive/root:\(encodedPath):/content"

        let url = URL(string: "\(graphURL)\(endpoint)")!
        var request = URLRequest(url: url)
        let creds = try await refreshTokenIfNeeded()
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")

        let (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...399).contains(http.statusCode) else {
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
        let fileData = try Data(contentsOf: localURL)
        let encodedPath = remotePath.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? remotePath
        let attrs = try? FileManager.default.attributesOfItem(atPath: localURL.path)
        let modDate = attrs?[.modificationDate] as? Date
        let createdDate = attrs?[.creationDate] as? Date

        // Files up to 4MB use simple upload; larger files use upload session
        if fileData.count <= 4_000_000 {
            try await simpleUpload(data: fileData, remotePath: encodedPath, onBytes: onBytes)
        } else {
            try await largeFileUpload(from: localURL, fileSize: fileData.count, remotePath: encodedPath, modDate: modDate, createdDate: createdDate, onBytes: onBytes)
        }

        // OneDrive has no upload-time parameter for simple uploads, so we PATCH
        // the fileSystemInfo afterwards. For large uploads we pass the info via
        // the upload session, but a follow-up PATCH is still a no-op if ignored.
        if modDate != nil || createdDate != nil {
            try? await patchFileSystemInfo(remotePath: encodedPath, modDate: modDate, createdDate: createdDate)
        }
    }

    private func patchFileSystemInfo(remotePath: String, modDate: Date?, createdDate: Date?) async throws {
        let endpoint = "/me/drive/root:\(remotePath):"
        let url = URL(string: "\(graphURL)\(endpoint)")!
        var request = URLRequest(url: url)
        request.httpMethod = "PATCH"
        let creds = try await refreshTokenIfNeeded()
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")

        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        var info: [String: String] = [:]
        if let modDate { info["lastModifiedDateTime"] = formatter.string(from: modDate) }
        if let createdDate { info["createdDateTime"] = formatter.string(from: createdDate) }
        request.httpBody = try JSONEncoder().encode(["fileSystemInfo": info])

        let (_, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else { return }
    }

    private func simpleUpload(data: Data, remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let endpoint = "/me/drive/root:\(remotePath):/content"
        let url = URL(string: "\(graphURL)\(endpoint)")!
        var request = URLRequest(url: url)
        request.httpMethod = "PUT"
        let creds = try await refreshTokenIfNeeded()
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")

        let (responseData, response) = try await session.uploadReportingProgress(for: request, body: data, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: responseData, encoding: .utf8) ?? ""
            oneDriveLog.error("[OneDrive] Upload failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    private func largeFileUpload(from localURL: URL, fileSize: Int, remotePath: String, modDate: Date?, createdDate: Date?, onBytes: ByteProgressHandler?) async throws {
        // Create upload session
        let endpoint = "/me/drive/root:\(remotePath):/createUploadSession"
        let url = URL(string: "\(graphURL)\(endpoint)")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        let creds = try await refreshTokenIfNeeded()
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")

        // Timestamps are applied by the caller via patchFileSystemInfo after
        // upload. Including fileSystemInfo in the session payload caused HTTP
        // 400 on some file types (e.g. large MOV uploads).
        struct SessionItem: Encodable {
            let conflictBehavior: String
            enum CodingKeys: String, CodingKey {
                case conflictBehavior = "@microsoft.graph.conflictBehavior"
            }
        }
        struct SessionBody: Encodable {
            let item: SessionItem
        }
        request.httpBody = try JSONEncoder().encode(SessionBody(item: SessionItem(conflictBehavior: "replace")))

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
        let encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? path
        let endpoint = "/me/drive/root:\(encodedPath):"
        try await graphRequestVoid(.delete, path: endpoint)
    }

    func createFolder(at path: String) async throws {
        if (try? await getFileMetadata(at: path)) != nil { return }

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

    func setModificationDate(at remotePath: String, to date: Date) async throws {
        // Microsoft Graph stores the client-visible mtime in
        // `fileSystemInfo.lastModifiedDateTime`. PATCHing the drive item
        // updates it without re-uploading content.
        let encodedPath = remotePath.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? remotePath
        let endpoint = "/me/drive/root:\(encodedPath):"

        struct FsInfo: Encodable { let lastModifiedDateTime: String }
        struct Body: Encodable { let fileSystemInfo: FsInfo }

        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        let body = Body(fileSystemInfo: FsInfo(lastModifiedDateTime: formatter.string(from: date)))

        let _: GraphDriveItem = try await graphRequest(.patch, path: endpoint, body: body)
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

    // MARK: - Search

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
        let endpoint: String
        if let path, path != "/" {
            let encodedPath = path.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? path
            endpoint = "/me/drive/root:\(encodedPath):/search(q='\(query)')"
        } else {
            endpoint = "/me/drive/root/search(q='\(query)')"
        }

        let response: GraphListResponse = try await graphRequest(.get, path: endpoint, queryItems: [
            URLQueryItem(name: "$top", value: "100"),
        ])

        return response.value.map { $0.toCloudFileItem(parentPath: "/") }
    }

    // MARK: - HTTP

    private enum HTTPMethod: String {
        case get = "GET"
        case post = "POST"
        case put = "PUT"
        case patch = "PATCH"
        case delete = "DELETE"
    }

    /// Follows an absolute URL (e.g. Graph's `@odata.nextLink`) with auth refresh.
    private func graphRequestAbsolute<T: Decodable>(url absoluteURL: String) async throws -> T {
        guard let url = URL(string: absoluteURL) else {
            throw CloudProviderError.invalidResponse
        }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        let creds = try await refreshTokenIfNeeded()
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }
        guard (200...299).contains(http.statusCode) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            oneDriveLog.error("[OneDrive] GET \(absoluteURL) → HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http.statusCode)
        }
        return try JSONDecoder().decode(T.self, from: data)
    }

    private func graphRequest<T: Decodable>(_ method: HTTPMethod, path: String, queryItems: [URLQueryItem] = [], body: (any Encodable)? = nil) async throws -> T {
        var components = URLComponents(string: "\(graphURL)\(path)")!
        if !queryItems.isEmpty {
            components.queryItems = (components.queryItems ?? []) + queryItems
        }

        guard let url = components.url else {
            throw CloudProviderError.invalidResponse
        }
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
            if let encodedBody {
                request.setValue("application/json", forHTTPHeaderField: "Content-Type")
                request.httpBody = encodedBody
            }
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
            return (data, http)
        }

        var (data, http) = try await send(forceRefreshFirst: false)
        if http.statusCode == 401 {
            oneDriveLog.info("[OneDrive] \(method.rawValue) \(path) → 401, force-refreshing and retrying once")
            (data, http) = try await send(forceRefreshFirst: true)
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

        func send(forceRefreshFirst: Bool) async throws -> (Data, HTTPURLResponse) {
            let creds = forceRefreshFirst ? try await forceRefresh() : try await refreshTokenIfNeeded()
            var request = URLRequest(url: url)
            request.httpMethod = method.rawValue
            request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
            return (data, http)
        }

        var (data, http) = try await send(forceRefreshFirst: false)
        if http.statusCode == 401 {
            oneDriveLog.info("[OneDrive] \(method.rawValue) \(path) → 401, force-refreshing and retrying once")
            (data, http) = try await send(forceRefreshFirst: true)
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

// MARK: - Thread-safe continuation wrapper

private final class OneDriveContinuationGuard<T: Sendable>: Sendable {
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

// MARK: - Microsoft Graph API Response Types

private struct TokenResponse: Decodable {
    let access_token: String
    let refresh_token: String?
    let expires_in: Int
    let token_type: String
}

struct GraphListResponse: Decodable {
    let value: [GraphDriveItem]
    let nextLink: String?

    private enum CodingKeys: String, CodingKey {
        case value
        case nextLink = "@odata.nextLink"
    }
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
