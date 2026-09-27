import Foundation
import Network
import os
import Security
import CommonCrypto

private let dropboxLog = Logger(subsystem: "com.rana.FileFluss", category: "dropbox")

public struct DropboxCredentials: Codable, Sendable {
    public let accessToken: String
    public let refreshToken: String
    public let expiresAt: Date
    public let accountId: String
    public let displayName: String
}

public actor DropboxAPIClient {
    // Dropbox App Key (PKCE flow — no client secret needed)
    static let appKey = "b5v4zgbnycbuimj"
    static let appSecret = "xcvb8frfc2jyzvj"

    private(set) var credentials: DropboxCredentials
    private let session: URLSession

    /// When a token refresh is in flight, concurrent callers must await
    /// the same Task rather than firing parallel POSTs to /oauth2/token.
    /// Dropbox rotates refresh tokens — a parallel refresh races and one
    /// caller comes back with invalid_grant → notAuthenticated. Actor
    /// isolation alone doesn't fix this because every `await` releases
    /// the actor.
    private var inflightRefresh: Task<DropboxCredentials, Error>?

    private static let rpcURL = "https://api.dropboxapi.com/2"
    private static let contentURL = "https://content.dropboxapi.com/2"

    /// Space-separated OAuth scopes requested at authorize time. These
    /// must all be enabled in the Dropbox app console (Permissions tab) —
    /// requesting a scope the app isn't allowed to grant fails the whole
    /// authorization. `account_info.read` covers the storage quota
    /// (/users/get_space_usage) and account name lookup; `files.metadata.read`
    /// covers listing/search/get_metadata; `files.content.read` covers
    /// downloads; `files.content.write` covers upload/delete/move/copy/
    /// create_folder; `sharing.write` covers creating public share links
    /// (/sharing/create_shared_link_with_settings) and `sharing.read` the
    /// lookup of a link that already exists (/sharing/list_shared_links) —
    /// the console grants the two together, ticking `sharing.write` selects
    /// `sharing.read` as well and it can't be unticked, so requesting both
    /// costs nothing. We deliberately do NOT request `files.metadata.write`
    /// (only needed for the file-properties/templates API, which we don't
    /// use) or `account_info.write` (we never modify the account).
    ///
    /// Accounts linked before `sharing.write` joined this list still hold a
    /// token without it. The sharing call then fails with HTTP 401 and a
    /// `missing_scope` tag — the same failure shape as the `account_info.read`
    /// case noted on `storageQuota` — which `createShareLink` maps to
    /// `.unauthorized` so the panel's re-auth banner asks the user to sign in
    /// again and pick the new scope up.
    static let oauthScopes = "account_info.read files.metadata.read files.content.read files.content.write sharing.write sharing.read"

    public init(credentials: DropboxCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)
    }

    // MARK: - OAuth2 (PKCE)

    public static func startOAuthFlow() async throws -> DropboxCredentials {
        let codeVerifier = generateCodeVerifier()
        let codeChallenge = generateCodeChallenge(from: codeVerifier)
        let expectedState = generateState()

        let result = try await OAuthSession.authenticate(
            callbackURLScheme: oauthCallbackScheme
        ) { redirectURI in
            var components = URLComponents(string: "https://www.dropbox.com/oauth2/authorize")!
            components.queryItems = [
                URLQueryItem(name: "client_id", value: appKey),
                URLQueryItem(name: "redirect_uri", value: redirectURI),
                URLQueryItem(name: "response_type", value: "code"),
                URLQueryItem(name: "code_challenge", value: codeChallenge),
                URLQueryItem(name: "code_challenge_method", value: "S256"),
                URLQueryItem(name: "token_access_type", value: "offline"),
                // Request scopes explicitly so the grant is deterministic
                // rather than depending on the app-console default set.
                // `account_info.read` is what powers /users/get_space_usage
                // and /users/get_current_account — without it the storage
                // quota silently fails (the call 401s and we return nil).
                URLQueryItem(name: "scope", value: Self.oauthScopes),
                URLQueryItem(name: "state", value: expectedState),
            ]
            return components.url!
        }

        let callbackParams = URLComponents(url: result.callbackURL, resolvingAgainstBaseURL: false)?.queryItems
        let returnedState = callbackParams?.first(where: { $0.name == "state" })?.value
        guard returnedState == expectedState else {
            dropboxLog.error("[Dropbox] OAuth state mismatch — rejecting callback")
            throw CloudProviderError.unauthorized
        }
        if let errorParam = callbackParams?.first(where: { $0.name == "error" })?.value {
            dropboxLog.error("[Dropbox] OAuth error: \(errorParam)")
            throw CloudProviderError.unauthorized
        }
        guard let code = callbackParams?.first(where: { $0.name == "code" })?.value else {
            throw CloudProviderError.invalidResponse
        }
        return try await exchangeCodeForTokens(code: code, codeVerifier: codeVerifier, redirectURI: result.redirectURI)
    }

    /// URL scheme the iOS host registers in Info.plist so
    /// ASWebAuthenticationSession's redirect lands back in the app.
    /// Ignored by the macOS loopback authenticator.
    public static let oauthCallbackScheme = "filefluss-oauth"

    private static func exchangeCodeForTokens(code: String, codeVerifier: String, redirectURI: String) async throws -> DropboxCredentials {
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
            "redirect_uri=\(encode(redirectURI))",
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

    /// Soft variant used during initial sign-in: never throws on an API
    /// error, falling back to a placeholder so the account is still added
    /// even if the name lookup hiccups.
    private static func fetchCurrentAccount(accessToken: String) async throws -> (accountId: String, displayName: String) {
        do {
            return try await fetchCurrentAccountStrict(accessToken: accessToken)
        } catch {
            return (accountId: "unknown", displayName: "Unknown")
        }
    }

    /// Throwing variant used by the launch-time name refresh, where we
    /// must distinguish "lookup failed" (keep the old name) from a real
    /// fresh value — so a transient error never overwrites a good name
    /// with "Unknown".
    private static func fetchCurrentAccountStrict(accessToken: String) async throws -> (accountId: String, displayName: String) {
        let url = URL(string: "\(rpcURL)/users/get_current_account")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        // Dropbox RPC endpoints require a null body or empty JSON. The
        // Content-Type header is mandatory — without it URLSession sends
        // application/x-www-form-urlencoded, which Dropbox rejects with a
        // 400 "Bad HTTP Content-Type header", leaving the account name
        // stuck at "Unknown".
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = "null".data(using: .utf8)

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let status = (response as? HTTPURLResponse)?.statusCode ?? -1
            throw CloudProviderError.serverError(status)
        }

        let account = try JSONDecoder().decode(DropboxAccountInfo.self, from: data)
        return (accountId: account.account_id, displayName: account.name.display_name)
    }

    /// Re-fetches the live account display name and updates the cached
    /// credentials. Returns the updated credentials so the caller can
    /// persist them, or `nil` when nothing changed / the lookup failed.
    /// Lets accounts linked before the Content-Type fix self-heal their
    /// "Unknown" name on the next launch without a manual re-link.
    public func refreshDisplayName() async throws -> DropboxCredentials? {
        let creds = try await refreshTokenIfNeeded()
        let (accountId, displayName) = try await Self.fetchCurrentAccountStrict(accessToken: creds.accessToken)
        let trimmed = displayName.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, trimmed != "Unknown", trimmed != credentials.displayName else {
            return nil
        }
        let updated = DropboxCredentials(
            accessToken: creds.accessToken,
            refreshToken: creds.refreshToken,
            expiresAt: creds.expiresAt,
            accountId: accountId.isEmpty ? creds.accountId : accountId,
            displayName: trimmed
        )
        credentials = updated
        return updated
    }

    // MARK: - Token Refresh

    public func refreshTokenIfNeeded() async throws -> DropboxCredentials {
        if let inflight = inflightRefresh {
            return try await inflight.value
        }
        guard Date() >= credentials.expiresAt.addingTimeInterval(-60) else {
            return credentials
        }
        return try await startRefresh()
    }

    public func userDisplayName() async throws -> String {
        credentials.displayName
    }

    /// Dropbox `/users/get_space_usage`. The `allocation` block tells us
    /// whether the account has a hard cap (individual plans) or is
    /// shared/team (we still surface the team allocation, since that's
    /// what the user will hit). `allocated == 0` happens on
    /// Dropbox Business plans without an enforced quota; we treat that
    /// as "no total" rather than "0 bytes total" so the status bar
    /// renders just the used figure.
    public func storageQuota() async throws -> CloudStorageQuota? {
        struct SpaceUsage: Decodable {
            let used: Int64
            let allocation: Allocation
            struct Allocation: Decodable {
                let allocated: Int64?
            }
        }
        let creds = try await refreshTokenIfNeeded()
        let url = URL(string: "\(Self.rpcURL)/users/get_space_usage")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        // get_space_usage requires a null body, not an empty {}. The
        // Content-Type header is mandatory — without it URLSession sends
        // application/x-www-form-urlencoded and Dropbox 400s, which is why
        // the storage quota never appeared (issue: quota not showing).
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = "null".data(using: .utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            // Surface *why* the quota is missing instead of silently
            // returning nil. The usual culprit is a token granted without
            // the `account_info.read` scope (HTTP 401, body tag
            // `missing_scope`); the user must re-link the account once the
            // scope is requested. Logged at error level so it lands in the
            // support log.
            let status = (response as? HTTPURLResponse)?.statusCode ?? -1
            let body = String(data: data, encoding: .utf8)?.prefix(300) ?? ""
            dropboxLog.error("[Dropbox] get_space_usage failed (HTTP \(status)): \(body, privacy: .public)")
            return nil
        }
        let usage = try JSONDecoder().decode(SpaceUsage.self, from: data)
        let total = (usage.allocation.allocated ?? 0) > 0 ? usage.allocation.allocated : nil
        return CloudStorageQuota(usedBytes: usage.used, totalBytes: total)
    }

    // MARK: - File Operations

    /// Dropbox uses path-based access. Root is "" (empty string).
    /// We normalize our "/" root to "" for Dropbox API calls.
    private func dropboxPath(_ path: String) -> String {
        let p = path == "/" ? "" : path
        return p
    }

    public func listFolder(path: String) async throws -> [CloudFileItem] {
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

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
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

    public func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let dbPath = dropboxPath(remotePath)
        let fileData = try Data(contentsOf: localURL)
        let modDate = (try? FileManager.default.attributesOfItem(atPath: localURL.path)[.modificationDate]) as? Date

        if fileData.count <= 150_000_000 {
            try await simpleUpload(data: fileData, path: dbPath, clientModified: modDate, onBytes: onBytes)
        } else {
            try await sessionUpload(from: localURL, fileSize: fileData.count, path: dbPath, clientModified: modDate, onBytes: onBytes)
        }
    }

    private func simpleUpload(data: Data, path: String, clientModified: Date?, onBytes: ByteProgressHandler? = nil) async throws {
        struct UploadArg: Encodable {
            let path: String
            let mode: String
            let autorename: Bool
            let mute: Bool
            let client_modified: String?
        }

        let arg = UploadArg(path: path, mode: "add", autorename: false, mute: false, client_modified: Self.dropboxDateString(clientModified))
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

    private static func dropboxDateString(_ date: Date?) -> String? {
        guard let date else { return nil }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        return formatter.string(from: date)
    }

    private func sessionUpload(from localURL: URL, fileSize: Int, path: String, clientModified: Date?, onBytes: ByteProgressHandler? = nil) async throws {
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
                    let client_modified: String?
                }

                let finishArg = FinishArg(
                    cursor: SessionCursor(session_id: sessionId, offset: offset),
                    commit: CommitInfo(path: path, mode: "add", autorename: false, mute: false, client_modified: Self.dropboxDateString(clientModified))
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

    public func deleteItem(at path: String) async throws {
        let dbPath = dropboxPath(path)

        struct DeleteArg: Encodable {
            let path: String
        }

        try await rpcRequestVoid(
            path: "/files/delete_v2",
            body: DeleteArg(path: dbPath)
        )
    }

    public func createFolder(at path: String) async throws {
        if (try? await getFileMetadata(at: path)) != nil { return }

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

    public func renameItem(at path: String, to newName: String) async throws {
        let dbPath = dropboxPath(path)
        let parentPath = (dbPath as NSString).deletingLastPathComponent
        let newPath = parentPath.isEmpty ? "/\(newName)" : "\(parentPath)/\(newName)"
        try await moveItem(at: path, toPath: newPath)
    }

    public func moveItem(at path: String, toPath newPath: String) async throws {
        struct MoveArg: Encodable {
            let from_path: String
            let to_path: String
            let autorename: Bool
        }
        try await rpcRequestVoid(
            path: "/files/move_v2",
            body: MoveArg(from_path: dropboxPath(path), to_path: dropboxPath(newPath), autorename: false)
        )
    }

    public func copyItem(at path: String, toPath newPath: String) async throws {
        struct CopyArg: Encodable {
            let from_path: String
            let to_path: String
            let autorename: Bool
        }
        try await rpcRequestVoid(
            path: "/files/copy_v2",
            body: CopyArg(from_path: dropboxPath(path), to_path: dropboxPath(newPath), autorename: false)
        )
    }

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
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

    public func folderSize(at path: String) async throws -> Int64 {
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

    /// Creates a public link for `path` — or hands back the one that already
    /// exists.
    ///
    /// Dropbox reports "this file is already shared" as an *error*
    /// (`shared_link_already_exists`) rather than returning the link, so the
    /// failure body has to be inspected: it normally carries the existing
    /// link's metadata inline, and when it doesn't we ask
    /// /sharing/list_shared_links for it.
    ///
    /// `require_password` and `expires` are Professional/Business features. A
    /// free account gets a `settings_error`, whose `error_summary` we surface
    /// verbatim — only Dropbox can explain which plan the user would need.
    public func createShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        struct Settings: Encodable {
            let require_password: Bool?
            let link_password: String?
            let expires: String?
            let allow_download: Bool
            let access: String
        }
        struct CreateRequest: Encodable {
            let path: String
            let settings: Settings
        }

        let dbPath = dropboxPath(path)
        let password = (options.password?.isEmpty == false) ? options.password : nil
        let requestBody = CreateRequest(
            path: dbPath,
            settings: Settings(
                // Omitted rather than sent as false: on a free account any
                // password field at all turns the call into a settings_error.
                require_password: password != nil ? true : nil,
                link_password: password,
                expires: options.expiry.map(Self.dropboxTimestamp),
                allow_download: options.allowDownload,
                // "viewer" is view+comment. The edit access levels need a
                // shared *folder*, which a public file link isn't.
                access: "viewer"
            )
        )

        let (data, http) = try await rpcRaw(
            path: "/sharing/create_shared_link_with_settings",
            encodedBody: try JSONEncoder().encode(requestBody)
        )
        if (200...299).contains(http.statusCode) {
            let metadata = try JSONDecoder().decode(DropboxSharedLinkMetadata.self, from: data)
            return try Self.shareLink(from: metadata, requestedPassword: password != nil)
        }

        // A token minted before `sharing.write` was requested (see oauthScopes):
        // no refresh can fix it, the user has to re-link, and `.unauthorized`
        // is what drives the panel's re-auth banner.
        if Self.isMissingScope(statusCode: http.statusCode, body: data) {
            dropboxLog.error("[Dropbox] create_shared_link_with_settings rejected: token lacks the sharing.write scope")
            throw CloudProviderError.unauthorized
        }

        let envelope = try? JSONDecoder().decode(DropboxShareErrorEnvelope.self, from: data)

        // Already shared: the error payload *is* the link we wanted.
        if let alreadyExists = envelope?.error?.shared_link_already_exists {
            if let metadata = alreadyExists.metadata {
                return try Self.shareLink(
                    from: metadata,
                    requestedPassword: false,
                    note: Self.existingLinkNote(options: options)
                )
            }
            // Dropbox can answer `.tag: "unverified"` with no metadata at all.
            // The link exists, we just weren't told which one — look it up.
            // Best-effort: on a token predating the `sharing.read` scope this
            // lookup fails, and that must not replace the real situation
            // ("already shared") with a scope error.
            if let existing = try? await firstDirectSharedLink(path: dbPath) {
                return try Self.shareLink(
                    from: existing,
                    requestedPassword: false,
                    note: Self.existingLinkNote(options: options)
                )
            }
            throw CloudProviderError.commandFailed(
                L10n.text("This file already has a public link. Open it in Dropbox to copy that link.")
            )
        }

        if let summary = envelope?.error_summary, !summary.isEmpty {
            throw CloudProviderError.commandFailed(summary)
        }
        // A malformed request comes back as HTTP 400 with a *plain-text*
        // explanation ("Error in call to API function …"), not the JSON
        // envelope above. Passing it through beats reporting a bare 400.
        if let text = String(data: data, encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty {
            throw CloudProviderError.commandFailed(String(text.prefix(300)))
        }
        throw Self.mapHTTPError(statusCode: http.statusCode, responseBody: data)
    }

    /// Existing links for one path. `direct_only` drops links that were
    /// inherited from a shared parent folder, leaving only links to the file
    /// itself — which is what `shared_link_already_exists` refers to.
    ///
    /// Gated on `sharing.read`, which `oauthScopes` requests. The caller
    /// still treats this as best-effort: an account linked before that scope
    /// was requested fails here with `missing_scope`, and that must not
    /// replace the real situation ("already shared") with a scope error.
    private func firstDirectSharedLink(path: String) async throws -> DropboxSharedLinkMetadata? {
        struct ListRequest: Encodable {
            let path: String
            let direct_only: Bool
        }
        struct ListResponse: Decodable {
            let links: [DropboxSharedLinkMetadata]
        }
        let response: ListResponse = try await rpcRequest(
            path: "/sharing/list_shared_links",
            body: ListRequest(path: path, direct_only: true)
        )
        return response.links.first
    }

    private static func shareLink(
        from metadata: DropboxSharedLinkMetadata,
        requestedPassword: Bool,
        note: String? = nil
    ) throws -> CloudShareLink {
        guard let url = URL(string: metadata.url) else { throw CloudProviderError.invalidResponse }
        // Honour what the server reports, falling back to what we asked for
        // when it leaves the field out.
        let allowsDownload = metadata.link_permissions?.allow_download ?? true
        return CloudShareLink(
            url: url,
            directDownloadURL: allowsDownload ? Self.directDownloadURL(from: metadata.url) : nil,
            expiresAt: metadata.expires.flatMap { Self.dropboxDate(from: $0) },
            hasPassword: metadata.link_permissions?.require_password ?? requestedPassword,
            note: note
        )
    }

    /// Dropbox hands back a preview link ending in `?dl=0`; flipping the
    /// parameter to `dl=1` streams the bytes. Rebuilding the query rather
    /// than string-replacing also covers a link that arrives with `dl=1`
    /// already, or with no `dl` at all (then it's appended with the right
    /// `?`/`&` separator).
    private static func directDownloadURL(from raw: String) -> URL? {
        guard var components = URLComponents(string: raw) else { return nil }
        var items = components.queryItems ?? []
        if let index = items.firstIndex(where: { $0.name == "dl" }) {
            items[index].value = "1"
        } else {
            items.append(URLQueryItem(name: "dl", value: "1"))
        }
        components.queryItems = items
        return components.url
    }

    /// The already-exists path returns the link Dropbox already had, with its
    /// own settings — nothing we asked for was applied, so say so when the
    /// user actually asked for something.
    private static func existingLinkNote(options: ShareLinkOptions) -> String? {
        let askedForSettings = !(options.password ?? "").isEmpty || options.expiry != nil || !options.allowDownload
        guard askedForSettings else { return nil }
        return L10n.text("This file already had a public link, so the requested settings were not applied.")
    }

    /// Whole-second UTC ("2026-01-01T12:00:00Z"). Dropbox rejects a
    /// fractional-seconds timestamp as a malformed datetime.
    private static func dropboxTimestamp(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        return formatter.string(from: date)
    }

    private static func dropboxDate(from raw: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        return formatter.date(from: raw)
    }

    /// Dropbox answers a scope-less token with HTTP 401 and a `missing_scope`
    /// tag naming the scope it wanted. Matched on the raw body in the same
    /// spirit as the `not_found` check in `mapHTTPError` — the 401 envelope is
    /// plain text as often as JSON.
    private static func isMissingScope(statusCode: Int, body: Data) -> Bool {
        guard statusCode == 401, let text = String(data: body, encoding: .utf8) else { return false }
        return text.contains("missing_scope")
    }

    // MARK: - RPC Helper

    /// Force a refresh regardless of the cached `expiresAt`. Used after a
    /// 401 from an API call — servers can invalidate access tokens before
    /// our local expiry kicks in, so the "is the token expiring soon?"
    /// guard isn't enough.
    private func forceRefreshToken() async throws -> DropboxCredentials {
        if let inflight = inflightRefresh {
            return try await inflight.value
        }
        return try await startRefresh()
    }

    private func startRefresh() async throws -> DropboxCredentials {
        guard !credentials.refreshToken.isEmpty else {
            throw CloudProviderError.notAuthenticated
        }
        let task = Task<DropboxCredentials, Error> { [self] in
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

    private func performTokenRefresh() async throws -> DropboxCredentials {
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

    // MARK: - Search

    public func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
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

    /// Sends an RPC POST and returns the response as-is, error statuses
    /// included. `rpcRequest`/`rpcRequestVoid` add the usual status check on
    /// top; the sharing path needs the raw body, because Dropbox reports
    /// "link already exists" as an error whose payload is the link we want.
    ///
    /// Keeping the token handling here means there is still exactly one auth
    /// path: refresh-if-stale, and one forced refresh + retry on a 401.
    private func rpcRaw(path: String, encodedBody: Data) async throws -> (Data, HTTPURLResponse) {
        let creds = try await refreshTokenIfNeeded()
        let url = URL(string: "\(Self.rpcURL)\(path)")!

        func send(accessToken: String) async throws -> (Data, HTTPURLResponse) {
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = encodedBody

            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                throw CloudProviderError.invalidResponse
            }
            return (data, http)
        }

        var (data, http) = try await send(accessToken: creds.accessToken)

        // Retry once on 401 with a forced token refresh
        if http.statusCode == 401 {
            dropboxLog.info("[Dropbox] Got 401 on \(path), refreshing token and retrying")
            let newCreds = try await forceRefreshToken()
            (data, http) = try await send(accessToken: newCreds.accessToken)
        }

        if !(200...299).contains(http.statusCode) {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            dropboxLog.error("[Dropbox] POST \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(1000))")
        }

        return (data, http)
    }

    private func rpcRequest<B: Encodable, T: Decodable>(path: String, body: B) async throws -> T {
        let (data, http) = try await rpcRaw(path: path, encodedBody: try JSONEncoder().encode(body))
        guard (200...299).contains(http.statusCode) else {
            throw Self.mapHTTPError(statusCode: http.statusCode, responseBody: data)
        }
        return try JSONDecoder().decode(T.self, from: data)
    }

    private func rpcRequestVoid<B: Encodable>(path: String, body: B) async throws {
        let (data, http) = try await rpcRaw(path: path, encodedBody: try JSONEncoder().encode(body))
        guard (200...299).contains(http.statusCode) else {
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

private final class ContinuationGuard<T: Sendable>: Sendable {
    private let state = OSAllocatedUnfairLock(initialState: State())

    private struct State {
        var continuation: CheckedContinuation<T, Error>?
        var resumed = false
    }

    public func setContinuation(_ continuation: CheckedContinuation<T, Error>) {
        state.withLock { $0.continuation = continuation }
    }

    public func resume(returning value: T) {
        state.withLock { state in
            guard !state.resumed, let cont = state.continuation else { return }
            state.resumed = true
            state.continuation = nil
            cont.resume(returning: value)
        }
    }

    public func resume(throwing error: Error) {
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

/// The part of Dropbox's `SharedLinkMetadata` we use. Returned both by
/// /sharing/create_shared_link_with_settings and, for a link that already
/// existed, nested inside that call's error body.
private struct DropboxSharedLinkMetadata: Decodable {
    /// Preview link, ending in `?dl=0`.
    let url: String
    /// ISO8601 UTC; present only on a link that is set to expire.
    let expires: String?
    let link_permissions: LinkPermissions?

    struct LinkPermissions: Decodable {
        let require_password: Bool?
        let allow_download: Bool?
    }
}

/// Error envelope of /sharing/create_shared_link_with_settings. The
/// `shared_link_already_exists` member isn't really a failure: its payload
/// carries the metadata of the link the file already has.
private struct DropboxShareErrorEnvelope: Decodable {
    /// Dropbox's own summary, e.g. `settings_error/not_authorized/` for a
    /// password or expiry on a free plan. Surfaced to the user verbatim.
    let error_summary: String?
    let error: ErrorDetail?

    struct ErrorDetail: Decodable {
        let tag: String?
        let shared_link_already_exists: AlreadyExists?

        enum CodingKeys: String, CodingKey {
            case tag = ".tag"
            case shared_link_already_exists
        }

        /// `.tag` is "metadata" (with the link) or "unverified" (without).
        struct AlreadyExists: Decodable {
            let metadata: DropboxSharedLinkMetadata?
        }
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

    public func toCloudFileItem() -> CloudFileItem? {
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

