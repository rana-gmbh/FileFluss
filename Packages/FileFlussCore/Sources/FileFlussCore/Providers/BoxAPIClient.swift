import Foundation
import Network
import os
import Security
import CommonCrypto
import CryptoKit

private let boxLog = Logger(subsystem: "com.rana.FileFluss", category: "box")

public struct BoxCredentials: Codable, Sendable {
    public let accessToken: String
    public let refreshToken: String
    public let expiresAt: Date
    public let userLogin: String
    public let displayName: String

    public init(
        accessToken: String,
        refreshToken: String,
        expiresAt: Date,
        userLogin: String,
        displayName: String
    ) {
        self.accessToken = accessToken
        self.refreshToken = refreshToken
        self.expiresAt = expiresAt
        self.userLogin = userLogin
        self.displayName = displayName
    }
}

public actor BoxAPIClient {
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

    /// Single-request cap. Anything larger goes through the chunked upload
    /// session API (`/files/upload_sessions`).
    private static let singlePutMaxBytes: Int64 = 50 * 1024 * 1024

    /// Box's documented ceiling for a chunked upload. The account's own
    /// per-file limit (250 MB on free plans) is enforced server-side when
    /// the session is created and surfaces as `.fileTooLarge`.
    static let maxChunkedUploadBytes: Int64 = 50 * 1024 * 1024 * 1024

    private(set) var credentials: BoxCredentials
    /// Called whenever the credentials change, so the new ones reach the
    /// keychain immediately.
    ///
    /// This is not optional housekeeping for Box: every refresh returns a
    /// NEW refresh token and invalidates the previous one. Keeping the new
    /// token only in memory means the stored one is already dead — the next
    /// launch asks the user to sign in again, every time (issue #46).
    /// Dropbox survives the same structure only because its refresh tokens
    /// are not rotated.
    private var credentialsDidChange: (@Sendable (BoxCredentials) -> Void)?

    public func setCredentialsDidChange(_ handler: @escaping @Sendable (BoxCredentials) -> Void) {
        credentialsDidChange = handler
    }
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

    public init(credentials: BoxCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)
    }

    // MARK: - OAuth2

    public static func startOAuthFlow() async throws -> BoxCredentials {
        let expectedState = generateState()
        let result = try await OAuthSession.authenticate(
            callbackURLScheme: oauthCallbackScheme
        ) { redirectURI in
            var components = URLComponents(string: authBaseURL)!
            components.queryItems = [
                URLQueryItem(name: "response_type", value: "code"),
                URLQueryItem(name: "client_id", value: clientId),
                URLQueryItem(name: "redirect_uri", value: redirectURI),
                URLQueryItem(name: "state", value: expectedState),
            ]
            return components.url!
        }

        // CSRF defence — the OAuth server must round-trip the same `state`
        // value we sent in the authorize URL.
        let callbackParams = URLComponents(url: result.callbackURL, resolvingAgainstBaseURL: false)?.queryItems
        let returnedState = callbackParams?.first(where: { $0.name == "state" })?.value
        guard returnedState == expectedState else {
            boxLog.error("[Box] OAuth state mismatch — rejecting callback")
            throw CloudProviderError.unauthorized
        }
        if let errorParam = callbackParams?.first(where: { $0.name == "error" })?.value {
            boxLog.error("[Box] OAuth error: \(errorParam)")
            throw CloudProviderError.unauthorized
        }
        guard let code = callbackParams?.first(where: { $0.name == "code" })?.value else {
            throw CloudProviderError.invalidResponse
        }
        return try await exchangeCodeForTokens(code: code, redirectURI: result.redirectURI)
    }

    /// URL scheme the iOS host registers in Info.plist so
    /// ASWebAuthenticationSession's redirect lands back in the app.
    /// Ignored by the macOS loopback authenticator.
    public static let oauthCallbackScheme = "filefluss-oauth"

    /// Identify ourselves to Box's edge — the default URLSession UA gets
    /// blocked by their WAF with a generic HTML 403, before the request
    /// ever reaches the OAuth backend.
    private static let userAgent = "FileFluss/1.0 (macOS)"

    private static func exchangeCodeForTokens(code: String, redirectURI: String) async throws -> BoxCredentials {
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
            "redirect_uri=\(encode(redirectURI))",
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
    public func refreshTokenIfNeeded() async throws -> BoxCredentials {
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
    public func forceRefresh() async throws -> BoxCredentials {
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
        credentialsDidChange?(newCreds)
        return newCreds
    }

    public func userDisplayName() async throws -> String {
        credentials.displayName
    }

    /// Box `/users/me?fields=space_amount,space_used`. `space_amount`
    /// is the user's total quota in bytes; -1 means "no quota assigned"
    /// (typical for enterprise accounts), which we surface as `nil`.
    public func storageQuota() async throws -> CloudStorageQuota? {
        struct Me: Decodable {
            let space_amount: Int64?
            let space_used: Int64?
        }
        let creds = try await refreshTokenIfNeeded()
        let url = URL(string: "\(Self.apiURL)/users/me?fields=space_amount,space_used")!
        var request = URLRequest(url: url)
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            return nil
        }
        let me = try JSONDecoder().decode(Me.self, from: data)
        let total = (me.space_amount ?? -1) > 0 ? me.space_amount : nil
        return CloudStorageQuota(usedBytes: me.space_used ?? 0, totalBytes: total)
    }

    // MARK: - Share links

    /// The `shared_link` sub-object Box returns for an item. We only ever
    /// ask for this one field (`?fields=shared_link`), so the envelope
    /// stays this small.
    private struct BoxSharedLinkEnvelope: Decodable {
        struct SharedLink: Decodable {
            let url: String?
            /// Direct-download URL. Always null for folders, and null for
            /// files whose effective permission is preview-only.
            let download_url: String?
            let is_password_enabled: Bool?
            /// RFC3339, or absent when the link never expires.
            let unshared_at: String?
            /// What the link *actually* grants after enterprise policy is
            /// applied: "open", "company" or "collaborators".
            let effective_access: String?
            /// "can_download" or "can_preview" after policy is applied.
            let effective_permission: String?
        }
        let shared_link: SharedLink?
    }

    /// Creates the public shared link on an existing item.
    ///
    /// `?fields=shared_link` is mandatory: without it Box answers 200 with
    /// the plain item representation and no link in it at all.
    ///
    /// A password is only accepted alongside `access: "open"` and must be
    /// at least 8 characters with a digit, an uppercase letter or a
    /// non-alphanumeric; `unshared_at` is a paid-plan feature. Box rejects
    /// both cases with its own explanation, which we pass through verbatim
    /// rather than flattening into a generic error.
    public func createShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        try await putSharedLink(at: path, options: options)
    }

    /// Box's sharing PUT is an upsert: the very same call that creates a
    /// link rewrites the settings of one that already exists, and the URL
    /// is preserved. So updating is creating with the new settings — no
    /// separate endpoint, and the recipient's link keeps working.
    public func updateShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        try await putSharedLink(at: path, options: options)
    }

    /// Reads back the item's current `shared_link`.
    ///
    /// Box always answers 200 with the item representation; `shared_link` is
    /// `null` when the item isn't shared, which is the only "not shared"
    /// signal — a 404 means the *item* is gone and stays an error, as does a
    /// 403 from an admin-restricted account.
    public func existingShareLink(at path: String) async throws -> CloudShareLink? {
        let entry = try await resolvePathToEntry(path)
        let collection = entry.isFolder ? "folders" : "files"
        let envelope: BoxSharedLinkEnvelope = try await apiRequest(
            .get,
            path: "/\(collection)/\(entry.id)",
            queryItems: [URLQueryItem(name: "fields", value: "shared_link")]
        )
        guard let link = envelope.shared_link else { return nil }
        // Nothing was requested here, so the downgrade note is computed
        // against the defaults: it still reports an admin-narrowed scope or a
        // preview-only link, which is exactly what the user needs to know.
        return Self.shareLink(from: link, requested: .default)
    }

    /// Withdraws the link by nulling `shared_link` on the item. Box needs an
    /// explicit JSON `null` here (an omitted key is a no-op), hence the
    /// hand-rolled encoder.
    public func removeShareLink(at path: String) async throws {
        struct ClearSharedLink: Encodable {
            enum CodingKeys: String, CodingKey { case shared_link }
            func encode(to encoder: Encoder) throws {
                var container = encoder.container(keyedBy: CodingKeys.self)
                try container.encodeNil(forKey: .shared_link)
            }
        }
        let entry = try await resolvePathToEntry(path)
        let collection = entry.isFolder ? "folders" : "files"
        // Reading the field back confirms the link really is gone rather
        // than trusting a bare 200.
        let envelope: BoxSharedLinkEnvelope = try await apiRequest(
            .put,
            path: "/\(collection)/\(entry.id)",
            queryItems: [URLQueryItem(name: "fields", value: "shared_link")],
            body: ClearSharedLink(),
            surfaceServerMessage: true
        )
        if envelope.shared_link != nil {
            boxLog.error("[Box] Unshare of /\(collection)/\(entry.id) left a shared_link in place")
            throw CloudProviderError.commandFailed(L10n.text("Box kept the public link in place — check whether your administrator manages sharing for this item."))
        }
    }

    private func putSharedLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        let entry = try await resolvePathToEntry(path)
        let collection = entry.isFolder ? "folders" : "files"
        guard let url = URL(string: "\(Self.apiURL)/\(collection)/\(entry.id)?fields=shared_link") else {
            throw CloudProviderError.invalidResponse
        }

        struct SharedLinkBody: Encodable {
            struct Permissions: Encodable {
                let can_download: Bool
                let can_preview: Bool
            }
            struct Link: Encodable {
                let access: String
                let password: String?
                let unshared_at: String?
                let permissions: Permissions
            }
            let shared_link: Link
        }

        var unsharedAt: String?
        if let expiry = options.expiry {
            let formatter = ISO8601DateFormatter()
            formatter.formatOptions = [.withInternetDateTime]
            formatter.timeZone = TimeZone(secondsFromGMT: 0)
            unsharedAt = formatter.string(from: expiry)
        }
        let password = (options.password?.isEmpty == false) ? options.password : nil
        let body = SharedLinkBody(shared_link: SharedLinkBody.Link(
            access: "open",
            password: password,
            unshared_at: unsharedAt,
            // Preview stays on even when downloads are off: that's the
            // point of a view-only link.
            permissions: .init(can_download: options.allowDownload, can_preview: true)
        ))
        let encodedBody = try JSONEncoder().encode(body)

        func send(forceRefreshFirst: Bool) async throws -> (Data, HTTPURLResponse) {
            let creds = forceRefreshFirst ? try await forceRefresh() : try await refreshTokenIfNeeded()
            var request = URLRequest(url: url)
            request.httpMethod = "PUT"
            request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
            request.httpBody = encodedBody
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
            return (data, http)
        }

        var (data, http) = try await send(forceRefreshFirst: false)
        if http.statusCode == 401 {
            boxLog.info("[Box] PUT /\(collection)/\(entry.id) shared_link → 401, force-refreshing and retrying once")
            (data, http) = try await send(forceRefreshFirst: true)
        }
        guard (200...299).contains(http.statusCode) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            boxLog.error("[Box] Share link failed: HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            // Box explains the real cause (password policy, plan limits,
            // admin restrictions) in its own message — surface it as-is.
            if let message = Self.parseErrorMessage(from: data), !message.isEmpty {
                throw CloudProviderError.commandFailed(message)
            }
            throw Self.mapHTTPError(statusCode: http.statusCode, responseBody: data)
        }

        let envelope = try JSONDecoder().decode(BoxSharedLinkEnvelope.self, from: data)
        guard let link = envelope.shared_link,
              let shareLink = Self.shareLink(from: link, requested: options) else {
            throw CloudProviderError.invalidResponse
        }
        return shareLink
    }

    /// Maps Box's `shared_link` object onto our own type. Nil when the object
    /// carries no usable URL, which callers treat as an invalid response.
    private static func shareLink(
        from link: BoxSharedLinkEnvelope.SharedLink,
        requested options: ShareLinkOptions
    ) -> CloudShareLink? {
        guard let raw = link.url, let shareURL = URL(string: raw) else { return nil }
        let requestedPassword = (options.password?.isEmpty == false)
        return CloudShareLink(
            url: shareURL,
            directDownloadURL: link.download_url.flatMap { URL(string: $0) },
            expiresAt: parseShareExpiry(link.unshared_at),
            hasPassword: link.is_password_enabled ?? requestedPassword,
            isPublic: (link.effective_access ?? "open").lowercased() == "open",
            note: shareDowngradeNote(link: link, options: options)
        )
    }

    /// Enterprise policy can silently downgrade what we asked for: a
    /// shared link requested as "open" comes back scoped to "company" or
    /// "collaborators", and downloads can be forced off. The request still
    /// succeeds, so the only way the user learns the link isn't public is
    /// if we read the effective values back and say so.
    private static func shareDowngradeNote(
        link: BoxSharedLinkEnvelope.SharedLink,
        options: ShareLinkOptions
    ) -> String? {
        var notes: [String] = []
        if let access = link.effective_access?.lowercased(), access != "open" {
            if access == "company" {
                notes.append(L10n.text("Your Box administrator restricted this link to people in your company — it is not publicly accessible."))
            } else {
                notes.append(L10n.text("Your Box administrator restricted this link to the file's collaborators — it is not publicly accessible."))
            }
        }
        if options.allowDownload, link.effective_permission?.lowercased() == "can_preview" {
            notes.append(L10n.text("Your Box administrator disabled downloads for shared links — recipients can only preview the file."))
        }
        return notes.isEmpty ? nil : notes.joined(separator: " ")
    }

    /// Box returns `unshared_at` as RFC3339, with or without fractional
    /// seconds depending on the endpoint.
    private static func parseShareExpiry(_ raw: String?) -> Date? {
        guard let raw, !raw.isEmpty else { return nil }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        if let date = formatter.date(from: raw) { return date }
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.date(from: raw)
    }

    // MARK: - File operations

    public func listFolder(path: String) async throws -> [CloudFileItem] {
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

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
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

    public func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let fileSize = (try? FileManager.default.attributesOfItem(atPath: localURL.path)[.size] as? Int64) ?? 0
        guard fileSize <= Self.maxChunkedUploadBytes else {
            throw CloudProviderError.fileTooLarge(fileBytes: fileSize, providerLimitBytes: Self.maxChunkedUploadBytes)
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

        // Small files go up in one request (held in memory); big ones are
        // streamed from disk part by part so memory stays flat.
        let fileData: Data? = fileSize <= Self.singlePutMaxBytes ? try Data(contentsOf: localURL) : nil

        func attempt(existingId: String?) async throws -> BoxItem {
            if let fileData {
                return try await performMultipartUpload(
                    fileData: fileData,
                    fileName: fileName,
                    parentId: parentId,
                    existingId: existingId,
                    modDate: modDate,
                    createdDate: createdDate,
                    onBytes: onBytes
                )
            }
            return try await performChunkedUpload(
                localURL: localURL,
                fileSize: fileSize,
                fileName: fileName,
                parentId: parentId,
                existingId: existingId,
                modDate: modDate,
                createdDate: createdDate,
                onBytes: onBytes
            )
        }

        do {
            pathIdCache[remotePath] = try await attempt(existingId: existingId).id
        } catch CloudProviderError.serverError(409) {
            // Already exists — query for the file id and retry as a
            // version upload so we replace cleanly.
            guard let foundId = try? await findFileId(parentId: parentId, name: fileName) else {
                throw CloudProviderError.serverError(409)
            }
            pathIdCache[remotePath] = try await attempt(existingId: foundId).id
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

    // MARK: - Chunked upload sessions

    private struct UploadSession: Decodable {
        struct Endpoints: Decodable {
            let upload_part: String
            let commit: String
            let abort: String
        }
        let id: String
        let part_size: Int64
        let session_endpoints: Endpoints
    }

    private struct UploadedPart: Decodable {
        struct Part: Decodable {
            let part_id: String
            let offset: Int64
            let size: Int64
        }
        let part: Part
    }

    /// Forwards byte progress for one part, but never reports the same
    /// bytes twice when the part is retried.
    private final class PartProgress: @unchecked Sendable {
        private let lock = NSLock()
        private let onBytes: ByteProgressHandler?
        private var high: Int64 = 0
        private var current: Int64 = 0

        init(_ onBytes: ByteProgressHandler?) { self.onBytes = onBytes }

        func beginAttempt() { lock.lock(); current = 0; lock.unlock() }

        var handler: ByteProgressHandler? {
            guard let onBytes else { return nil }
            return { [self] delta in
                lock.lock()
                current += delta
                let forward = max(0, current - high)
                high = max(high, current)
                lock.unlock()
                if forward > 0 { onBytes(forward) }
            }
        }
    }

    private static func sha1Header(_ digest: Insecure.SHA1.Digest) -> String {
        "sha=" + Data(digest).base64EncodedString()
    }

    private func performChunkedUpload(
        localURL: URL,
        fileSize: Int64,
        fileName: String,
        parentId: String,
        existingId: String?,
        modDate: Date?,
        createdDate: Date?,
        onBytes: ByteProgressHandler?
    ) async throws -> BoxItem {
        // 1. Open the session.
        var sessionBody: [String: Any] = ["file_size": fileSize, "file_name": fileName]
        let sessionPath: String
        if let existingId {
            sessionPath = "/files/\(existingId)/upload_sessions"
        } else {
            sessionPath = "/files/upload_sessions"
            sessionBody["folder_id"] = parentId
        }
        guard let sessionURL = URL(string: "\(Self.uploadURL)\(sessionPath)") else { throw CloudProviderError.invalidResponse }
        let (sessionData, sessionHTTP) = try await sendToUploadHost(
            url: sessionURL,
            method: "POST",
            headers: ["Content-Type": "application/json"],
            body: try JSONSerialization.data(withJSONObject: sessionBody)
        )
        guard (200...299).contains(sessionHTTP.statusCode) else {
            let bodyStr = String(data: sessionData, encoding: .utf8) ?? ""
            boxLog.error("[Box] Create upload session failed: HTTP \(sessionHTTP.statusCode): \(bodyStr.prefix(500))")
            if sessionHTTP.statusCode == 413 || bodyStr.contains("file_size_limit_exceeded") {
                // The account's plan caps single-file size (e.g. 250 MB on free).
                throw CloudProviderError.fileTooLarge(fileBytes: fileSize, providerLimitBytes: nil)
            }
            throw Self.mapHTTPError(statusCode: sessionHTTP.statusCode, responseBody: sessionData)
        }
        let uploadSession = try JSONDecoder().decode(UploadSession.self, from: sessionData)
        guard uploadSession.part_size > 0,
              let partURL = URL(string: uploadSession.session_endpoints.upload_part),
              let commitURL = URL(string: uploadSession.session_endpoints.commit) else {
            throw CloudProviderError.invalidResponse
        }

        do {
            // 2. Stream the file up part by part, hashing as we go.
            let handle = try FileHandle(forReadingFrom: localURL)
            defer { try? handle.close() }

            var fileHasher = Insecure.SHA1()
            var parts: [[String: Any]] = []
            var offset: Int64 = 0

            while offset < fileSize {
                try Task.checkCancellation()
                let want = Int(min(uploadSession.part_size, fileSize - offset))
                guard let chunk = try handle.read(upToCount: want), chunk.count == want else {
                    // File shrank or became unreadable mid-upload.
                    throw CloudProviderError.invalidResponse
                }
                fileHasher.update(data: chunk)

                let partHeaders = [
                    "Content-Type": "application/octet-stream",
                    "Content-Range": "bytes \(offset)-\(offset + Int64(want) - 1)/\(fileSize)",
                    "Digest": Self.sha1Header(Insecure.SHA1.hash(data: chunk)),
                ]
                let progress = PartProgress(onBytes)
                let (partData, partHTTP) = try await sendWithRetry {
                    progress.beginAttempt()
                    return try await self.sendToUploadHost(
                        url: partURL,
                        method: "PUT",
                        headers: partHeaders,
                        body: chunk,
                        onBytes: progress.handler
                    )
                }
                guard (200...299).contains(partHTTP.statusCode) else {
                    let bodyStr = String(data: partData, encoding: .utf8) ?? ""
                    boxLog.error("[Box] Upload part @\(offset) failed: HTTP \(partHTTP.statusCode): \(bodyStr.prefix(500))")
                    throw Self.mapHTTPError(statusCode: partHTTP.statusCode, responseBody: partData)
                }
                let uploaded = try JSONDecoder().decode(UploadedPart.self, from: partData).part
                parts.append(["part_id": uploaded.part_id, "offset": uploaded.offset, "size": uploaded.size])
                offset += Int64(want)
            }

            // 3. Commit. Box may answer 202 + Retry-After while it assembles
            // the parts; poll the same endpoint until it returns the file.
            var commitBody: [String: Any] = ["parts": parts]
            let attributes = Self.commitAttributes(contentModifiedAt: modDate, contentCreatedAt: createdDate)
            if !attributes.isEmpty { commitBody["attributes"] = attributes }
            let commitPayload = try JSONSerialization.data(withJSONObject: commitBody)
            let commitHeaders = [
                "Content-Type": "application/json",
                "Digest": Self.sha1Header(fileHasher.finalize()),
            ]

            for _ in 0..<30 {
                try Task.checkCancellation()
                let (commitData, commitHTTP) = try await sendWithRetry {
                    try await self.sendToUploadHost(url: commitURL, method: "POST", headers: commitHeaders, body: commitPayload)
                }
                if commitHTTP.statusCode == 202 {
                    let wait = Double(commitHTTP.value(forHTTPHeaderField: "Retry-After") ?? "") ?? 1
                    try await Task.sleep(nanoseconds: UInt64(min(max(wait, 1), 10) * 1_000_000_000))
                    continue
                }
                guard (200...299).contains(commitHTTP.statusCode) else {
                    let bodyStr = String(data: commitData, encoding: .utf8) ?? ""
                    boxLog.error("[Box] Commit upload session failed: HTTP \(commitHTTP.statusCode): \(bodyStr.prefix(500))")
                    throw Self.mapHTTPError(statusCode: commitHTTP.statusCode, responseBody: commitData)
                }
                let parsed = try JSONDecoder().decode(BoxFolderItems.self, from: commitData)
                guard let first = parsed.entries.first else { throw CloudProviderError.invalidResponse }
                return first
            }
            throw CloudProviderError.invalidResponse
        } catch {
            await abortUploadSession(uploadSession.session_endpoints.abort)
            throw error
        }
    }

    /// Runs `operation`, retrying transient failures (network errors, 429, 5xx)
    /// with a short backoff. Non-transient results are returned as-is for the
    /// caller to map.
    private func sendWithRetry(
        _ operation: () async throws -> (Data, HTTPURLResponse)
    ) async throws -> (Data, HTTPURLResponse) {
        var delay: UInt64 = 1_000_000_000
        for attempt in 1... {
            do {
                let result = try await operation()
                let code = result.1.statusCode
                if attempt < 4, code == 429 || (500...599).contains(code) {
                    boxLog.info("[Box] Upload request → HTTP \(code), retrying (attempt \(attempt))")
                } else {
                    return result
                }
            } catch let error as URLError where attempt < 4 && error.code != .cancelled {
                boxLog.info("[Box] Upload request failed (\(error.code.rawValue)), retrying (attempt \(attempt))")
            }
            try Task.checkCancellation()
            try await Task.sleep(nanoseconds: delay)
            delay *= 2
        }
        throw CloudProviderError.invalidResponse // unreachable
    }

    /// Authorized request to `upload.box.com`. Refreshes the token first, and
    /// once more if Box answers 401 — a multi-gigabyte upload can outlive an
    /// access token.
    private func sendToUploadHost(
        url: URL,
        method: String,
        headers: [String: String],
        body: Data,
        onBytes: ByteProgressHandler? = nil
    ) async throws -> (Data, HTTPURLResponse) {
        func send(forceRefreshFirst: Bool) async throws -> (Data, HTTPURLResponse) {
            let creds = forceRefreshFirst ? try await forceRefresh() : try await refreshTokenIfNeeded()
            var request = URLRequest(url: url)
            request.httpMethod = method
            request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
            for (name, value) in headers { request.setValue(value, forHTTPHeaderField: name) }
            let (data, response) = try await session.uploadReportingProgress(for: request, body: body, onBytes: onBytes)
            guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
            return (data, http)
        }
        var result = try await send(forceRefreshFirst: false)
        if result.1.statusCode == 401 {
            boxLog.info("[Box] \(method) upload request → 401, force-refreshing and retrying once")
            result = try await send(forceRefreshFirst: true)
        }
        return result
    }

    /// Best-effort cleanup so a failed or cancelled upload doesn't leave a
    /// half-assembled session behind. Detached so it still runs when the
    /// calling task has been cancelled.
    private func abortUploadSession(_ endpoint: String) async {
        guard let url = URL(string: endpoint),
              let creds = try? await refreshTokenIfNeeded() else { return }
        let session = self.session
        let userAgent = Self.userAgent
        await Task.detached {
            var request = URLRequest(url: url)
            request.httpMethod = "DELETE"
            request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
            request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
            _ = try? await session.data(for: request)
        }.value
    }

    private static func commitAttributes(contentModifiedAt: Date?, contentCreatedAt: Date?) -> [String: String] {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        var attrs: [String: String] = [:]
        if let contentModifiedAt { attrs["content_modified_at"] = formatter.string(from: contentModifiedAt) }
        if let contentCreatedAt { attrs["content_created_at"] = formatter.string(from: contentCreatedAt) }
        return attrs
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

    public func deleteItem(at path: String) async throws {
        let entry = try await resolvePathToEntry(path)
        let suffix = entry.isFolder ? "/folders/\(entry.id)?recursive=true" : "/files/\(entry.id)"
        try await apiRequestVoid(.delete, path: suffix)
        // The whole subtree went with it — see `removePathSubtree`.
        pathIdCache.removePathSubtree(path)
    }

    public func createFolder(at path: String) async throws {
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

    public func renameItem(at path: String, to newName: String) async throws {
        let entry = try await resolvePathToEntry(path)
        let suffix = entry.isFolder ? "/folders/\(entry.id)" : "/files/\(entry.id)"
        struct RenameBody: Encodable { let name: String }
        let _: BoxItem = try await apiRequest(.put, path: suffix, body: RenameBody(name: newName))

        pathIdCache.removePathSubtree(path)
        let parentPath = (path as NSString).deletingLastPathComponent
        let newPath = joinedPath(parent: parentPath, name: newName)
        pathIdCache[newPath] = entry.id
    }

    /// Server-side move. Box accepts a parent change on `PUT /files/{id}`
    /// and `PUT /folders/{id}`; no download/re-upload needed.
    public func moveItem(at path: String, toPath newPath: String) async throws {
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

        pathIdCache.removePathSubtree(path)
        pathIdCache[newPath] = entry.id
    }

    /// Server-side copy. Folder copies use `POST /folders/{id}/copy` and
    /// recursively duplicate the tree on Box's side.
    public func copyItem(at path: String, toPath newPath: String) async throws {
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

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        let entry = try await resolvePathToEntry(path)
        let parentPath = (path as NSString).deletingLastPathComponent
        let fields = "id,type,name,size,modified_at,content_modified_at,sha1"
        let suffix = entry.isFolder ? "/folders/\(entry.id)?fields=\(fields)" : "/files/\(entry.id)?fields=\(fields)"
        let item: BoxItem = try await apiRequest(.get, path: suffix)
        return item.toCloudFileItem(parentPath: parentPath)
    }

    public func folderSize(at path: String) async throws -> Int64 {
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

    public func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
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

    /// - Parameter surfaceServerMessage: when true, a failure that carries a
    ///   `{"message": …}` body throws `.commandFailed` with Box's own wording
    ///   instead of the coarse mapping below. Used by the sharing paths, where
    ///   the real cause (admin policy, plan limits) is only explicable
    ///   server-side. Off by default so existing callers keep their error
    ///   semantics.
    private func apiRequest<T: Decodable>(_ method: HTTPMethod, path: String, queryItems: [URLQueryItem] = [], body: (any Encodable)? = nil, surfaceServerMessage: Bool = false) async throws -> T {
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
            if surfaceServerMessage, let message = Self.parseErrorMessage(from: data), !message.isEmpty {
                throw CloudProviderError.commandFailed(message)
            }
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
        // Box's top-level message for a rejected share setting is just
        // "Forbidden"; the useful part — which field it objected to — lives
        // in context_info.errors.
        struct BoxContext: Decodable {
            struct Entry: Decodable {
                let reason: String?
                let name: String?
                let message: String?
            }
            let errors: [Entry]?
        }
        struct BoxErrorWithContext: Decodable {
            let message: String?
            let code: String?
            let context_info: BoxContext?
        }
        if let detailed = try? JSONDecoder().decode(BoxErrorWithContext.self, from: data),
           let entries = detailed.context_info?.errors, !entries.isEmpty {
            let details = entries
                .map { entry in
                    [entry.name, entry.message ?? entry.reason]
                        .compactMap { $0 }
                        .joined(separator: ": ")
                }
                .filter { !$0.isEmpty }
                .joined(separator: "; ")
            let head = detailed.message ?? detailed.code ?? ""
            if !details.isEmpty {
                return head.isEmpty ? details : "\(head) — \(details)"
            }
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

    public func toCloudFileItem(parentPath: String) -> CloudFileItem {
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
