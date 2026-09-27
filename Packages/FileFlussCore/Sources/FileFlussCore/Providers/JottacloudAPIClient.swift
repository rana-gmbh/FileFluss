import CryptoKit
import Foundation
import os

private let jottaLog = Logger(subsystem: "com.rana.FileFluss", category: "jottacloud")

// MARK: - Persisted credentials

/// Everything FileFluss needs to talk to Jottacloud on behalf of an account
/// across launches. The user pastes a one-time "Personal Login Token" from
/// jottacloud.com/web/secure, which we exchange for an OAuth refresh token.
/// The access token is short-lived (≈1 h) and lives only in memory; the
/// refresh token is what we persist.
///
/// IMPORTANT: Jottacloud rotates the refresh token on every refresh and
/// detects reuse — presenting an old refresh token invalidates the whole
/// token family and forces the user to re-paste a personal token. So whenever
/// a refresh returns a new refresh token we must persist it immediately (see
/// `onCredentialsChanged`).
public struct JottacloudCredentials: Codable, Sendable {
    public let username: String
    /// OAuth refresh token. Rotated on every refresh; persist the new value.
    public var refreshToken: String
    /// OIDC token endpoint discovered from the personal token's well-known
    /// link. Persisted so refreshes across launches don't need the (discarded)
    /// personal token.
    public let tokenEndpoint: String

    public init(username: String, refreshToken: String, tokenEndpoint: String) {
        self.username = username
        self.refreshToken = refreshToken
        self.tokenEndpoint = tokenEndpoint
    }
}

// MARK: - Jottacloud API client
//
// Jottacloud has no official API; this is built against the same endpoints the
// rclone "jottacloud" backend uses (observed behaviour, may change):
//
//   Auth (OIDC, client_id "jottacli"):
//     <well-known>.token_endpoint  (default
//       https://id.jottacloud.com/auth/realms/jottacloud/protocol/openid-connect/token)
//     grant_type=password  (personal-token exchange) / refresh_token
//
//   File tree (XML, "JFS"):
//     GET  https://jfs.jottacloud.com/jfs/{user}/{device}/{mountpoint}/{path}    list / metadata
//     POST .../{path}?mkDir=true                                                 create folder
//     POST .../{path}?dl=true                                                    delete (to trash)
//     POST .../{path}?mv=/{user}/{device}/{mountpoint}/{dest}                    move/rename
//     POST .../{path}?cp=/{user}/{device}/{mountpoint}/{dest}                    copy
//     GET  .../{path}?mode=bin                                                   download
//     GET  https://jfs.jottacloud.com/jfs/{user}                                 account capacity/usage
//
//   Upload (allocate then PUT):
//     POST https://api.jottacloud.com/files/v1/allocate  {path,bytes,md5,created,modified}
//     POST {upload_url}  (octet-stream body)             unless allocate state == COMPLETED (dedupe)
//
// The default file area is device "Jotta", mountpoint "Archive" — the general
// file store, matching what rclone exposes and the web "Files" view shows.
public actor JottacloudAPIClient {
    public private(set) var credentials: JottacloudCredentials
    /// Called whenever the refresh token rotates so the provider can persist
    /// it to the keychain. Must be cheap and non-throwing.
    private let onCredentialsChanged: @Sendable (JottacloudCredentials) -> Void

    private let device = "Jotta"
    private let mountpoint = "Archive"
    private let clientId = "jottacli"

    private static let jfsBase = "https://jfs.jottacloud.com/jfs/"
    private static let apiBase = "https://api.jottacloud.com/"
    /// Host the public share links point at — the same one the web UI serves.
    private static let wwwBase = "https://www.jottacloud.com/"
    static let defaultTokenURL = "https://id.jottacloud.com/auth/realms/jottacloud/protocol/openid-connect/token"

    private var accessToken: String?
    private var accessTokenExpiry: Date?

    private let session: URLSession = .shared

    public init(
        credentials: JottacloudCredentials,
        onCredentialsChanged: @escaping @Sendable (JottacloudCredentials) -> Void
    ) {
        self.credentials = credentials
        self.onCredentialsChanged = onCredentialsChanged
    }

    // MARK: - Login (personal token → credentials)

    /// Exchange a pasted Personal Login Token for OAuth credentials. The token
    /// is base64url-encoded JSON carrying the username, a well-known OIDC link,
    /// and an embedded auth token used as the password in a `password` grant.
    public static func login(personalToken: String) async throws -> JottacloudCredentials {
        let token = personalToken.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let decoded = Self.decodeBase64URL(token),
              let parsed = try? JSONDecoder().decode(LoginToken.self, from: decoded),
              !parsed.username.isEmpty, !parsed.authToken.isEmpty
        else {
            throw CloudProviderError.invalidCredentials
        }

        // Resolve the OIDC token endpoint from the well-known link.
        var tokenEndpoint = Self.defaultTokenURL
        if let linkURL = URL(string: parsed.wellKnownLink) {
            if let (data, resp) = try? await URLSession.shared.data(from: linkURL),
               (resp as? HTTPURLResponse).map({ (200...299).contains($0.statusCode) }) ?? false,
               let wk = try? JSONDecoder().decode(WellKnown.self, from: data),
               !wk.tokenEndpoint.isEmpty {
                tokenEndpoint = wk.tokenEndpoint
            }
        }

        // password grant → access + refresh tokens.
        let form = [
            "grant_type": "password",
            "client_id": "jottacli",
            "username": parsed.username,
            "password": parsed.authToken,
            "scope": "openid offline_access",
        ]
        let tokenResp = try await Self.postToken(endpoint: tokenEndpoint, form: form)
        guard let refresh = tokenResp.refreshToken, !refresh.isEmpty else {
            throw CloudProviderError.invalidCredentials
        }
        jottaLog.info("[Jottacloud] Authenticated as \(parsed.username, privacy: .public)")
        return JottacloudCredentials(username: parsed.username, refreshToken: refresh, tokenEndpoint: tokenEndpoint)
    }

    // MARK: - Token management

    /// A non-expired access token, refreshing if needed. Serialized by the
    /// actor so two concurrent callers can't both spend the refresh token.
    private func validAccessToken() async throws -> String {
        if let token = accessToken, let exp = accessTokenExpiry, exp > Date().addingTimeInterval(60) {
            return token
        }
        let form = [
            "grant_type": "refresh_token",
            "client_id": clientId,
            "refresh_token": credentials.refreshToken,
        ]
        let resp: TokenResponse
        do {
            resp = try await Self.postToken(endpoint: credentials.tokenEndpoint, form: form)
        } catch {
            // A revoked/rotated-away refresh token comes back as 400 invalid_grant.
            throw CloudProviderError.notAuthenticated
        }
        accessToken = resp.accessToken
        accessTokenExpiry = Date().addingTimeInterval(TimeInterval(resp.expiresIn ?? 3600))
        // Persist the rotated refresh token so the next launch / refresh works.
        if let newRefresh = resp.refreshToken, !newRefresh.isEmpty, newRefresh != credentials.refreshToken {
            credentials.refreshToken = newRefresh
            onCredentialsChanged(credentials)
        }
        return resp.accessToken
    }

    private static func postToken(endpoint: String, form: [String: String]) async throws -> TokenResponse {
        guard let url = URL(string: endpoint) else { throw CloudProviderError.invalidResponse }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        req.httpBody = Self.formEncode(form).data(using: .utf8)
        let (data, response) = try await URLSession.shared.data(for: req)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            jottaLog.error("[Jottacloud] Token request failed: HTTP \(code)")
            throw CloudProviderError.serverError(code)
        }
        return try JSONDecoder().decode(TokenResponse.self, from: data)
    }

    public var isAuthenticated: Bool {
        get async { !credentials.refreshToken.isEmpty }
    }

    // MARK: - File operations

    public func listFolder(path: String) async throws -> [CloudFileItem] {
        let items = try await listFolderRaw(path: path)
        // A JFS listing carries dates for files but not for sub-folders. Fetch
        // each sub-folder's own modified time so the panel shows real dates
        // (one request per folder, run concurrently to stay responsive).
        return await enrichFolderDates(items)
    }

    /// Listing without the per-sub-folder date fetch — used by recursive paths
    /// (folderSize, metadata lookups) so they don't fan out N extra requests.
    private func listFolderRaw(path: String) async throws -> [CloudFileItem] {
        let url = jfsURL(forPath: path)
        let data = try await jfsRequest(url, method: "GET")
        return JottacloudXMLParser.parseListing(data: data, basePath: normalizedDir(path))
    }

    private func enrichFolderDates(_ items: [CloudFileItem]) async -> [CloudFileItem] {
        let folderIndices = items.indices.filter { items[$0].isDirectory }
        guard !folderIndices.isEmpty else { return items }
        let dates = await withTaskGroup(of: (Int, Date?).self) { group -> [Int: Date] in
            for idx in folderIndices {
                let path = items[idx].path
                group.addTask { (idx, await self.folderModifiedDate(at: path)) }
            }
            var result: [Int: Date] = [:]
            for await (idx, date) in group { if let date { result[idx] = date } }
            return result
        }
        guard !dates.isEmpty else { return items }
        return items.enumerated().map { index, item in
            guard let date = dates[index] else { return item }
            return CloudFileItem(
                id: item.id, name: item.name, path: item.path,
                isDirectory: item.isDirectory, size: item.size,
                modificationDate: date, checksum: item.checksum,
                downloadStatus: item.downloadStatus,
                symbolIconOverride: item.symbolIconOverride, role: item.role)
        }
    }

    /// A single folder's own modified date (from a direct folder GET). Returns
    /// nil on any failure so a folder simply keeps its placeholder date.
    private func folderModifiedDate(at path: String) async -> Date? {
        guard let data = try? await jfsRequest(jfsURL(forPath: path), method: "GET") else { return nil }
        return JottacloudXMLParser.parseFolderModified(data: data)
    }

    public func createFolder(at path: String) async throws {
        let url = jfsURL(forPath: path, query: [URLQueryItem(name: "mkDir", value: "true")])
        _ = try await jfsRequest(url, method: "POST")
    }

    public func deleteItem(at path: String) async throws {
        // Both move the item to the Jottacloud trash (recoverable). `dl=true`
        // only deletes files; folders need `dlDir=true` (recursive), following
        // Jottacloud's mkDir/dlDir naming. We don't know the item type here, so
        // try the file form first (the common case, one request) and fall back
        // to the folder form on failure.
        let fileURL = jfsURL(forPath: path, query: [URLQueryItem(name: "dl", value: "true")])
        do {
            _ = try await jfsRequest(fileURL, method: "POST")
        } catch {
            let dirURL = jfsURL(forPath: path, query: [URLQueryItem(name: "dlDir", value: "true")])
            do {
                _ = try await jfsRequest(dirURL, method: "POST")
            } catch {
                throw error  // surface the original (file-delete) error
            }
        }
    }

    public func renameItem(at path: String, to newName: String) async throws {
        let parent = (path as NSString).deletingLastPathComponent
        let dest = parent == "/" || parent.isEmpty ? "/\(newName)" : "\(parent)/\(newName)"
        try await moveItem(at: path, toPath: dest)
    }

    public func moveItem(at path: String, toPath newPath: String) async throws {
        let url = jfsURL(forPath: path, query: [URLQueryItem(name: "mv", value: absoluteJFSPath(newPath))])
        _ = try await jfsRequest(url, method: "POST")
    }

    public func copyItem(at path: String, toPath newPath: String) async throws {
        let url = jfsURL(forPath: path, query: [URLQueryItem(name: "cp", value: absoluteJFSPath(newPath))])
        _ = try await jfsRequest(url, method: "POST")
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let url = jfsURL(forPath: remotePath, query: [URLQueryItem(name: "mode", value: "bin")])
        var req = try await authedRequest(url, method: "GET")
        req.timeoutInterval = 3600
        let (tempURL, response) = try await session.downloadReportingProgress(for: req, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw Self.mapError((response as? HTTPURLResponse)?.statusCode ?? 0, path: remotePath)
        }
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let attrs = try FileManager.default.attributesOfItem(atPath: localURL.path)
        let size = (attrs[.size] as? NSNumber)?.int64Value ?? 0
        let modDate = (attrs[.modificationDate] as? Date) ?? Date()
        let createdDate = (attrs[.creationDate] as? Date) ?? modDate
        let md5 = try Self.streamingMD5(of: localURL)

        // 1) allocate: server may report COMPLETED for content it already has
        //    (md5 dedupe) — an instant, zero-byte upload.
        let alloc = AllocateRequest(
            path: allocatePath(remotePath),
            bytes: size,
            md5: md5,
            created: Self.jottaTime(createdDate),
            modified: Self.jottaTime(modDate)
        )
        let allocURL = URL(string: Self.apiBase + "files/v1/allocate")!
        var allocReq = try await authedRequest(allocURL, method: "POST")
        allocReq.setValue("application/json", forHTTPHeaderField: "Content-Type")
        allocReq.httpBody = try JSONEncoder().encode(alloc)
        let (allocData, allocResp) = try await session.data(for: allocReq)
        guard let allocHTTP = allocResp as? HTTPURLResponse, (200...299).contains(allocHTTP.statusCode) else {
            let status = (allocResp as? HTTPURLResponse)?.statusCode ?? 0
            if let sizeErr = CloudProviderError.sizeLimitError(forStatus: status, localFile: localURL) { throw sizeErr }
            throw Self.mapError(status, path: remotePath)
        }
        let allocation = try JSONDecoder().decode(AllocateResponse.self, from: allocData)
        if allocation.state.uppercased() == "COMPLETED" {
            return // deduped — nothing to upload
        }
        guard let uploadURLString = allocation.uploadUrl, let uploadURL = URL(string: uploadURLString) else {
            throw CloudProviderError.invalidResponse
        }

        // 2) upload the bytes to the allocated URL.
        var putReq = try await authedRequest(uploadURL, method: "POST")
        putReq.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        putReq.timeoutInterval = 3600
        let body = try Data(contentsOf: localURL, options: .mappedIfSafe)
        let (_, putResp) = try await session.uploadReportingProgress(for: putReq, body: body, onBytes: onBytes)
        guard let putHTTP = putResp as? HTTPURLResponse, (200...299).contains(putHTTP.statusCode) else {
            let status = (putResp as? HTTPURLResponse)?.statusCode ?? 0
            if let sizeErr = CloudProviderError.sizeLimitError(forStatus: status, localFile: localURL) { throw sizeErr }
            throw Self.mapError(status, path: remotePath)
        }
    }

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        let parent = (path as NSString).deletingLastPathComponent
        let name = (path as NSString).lastPathComponent
        let entries = try await listFolderRaw(path: parent.isEmpty ? "/" : parent)
        if let match = entries.first(where: { $0.name == name }) { return match }
        throw CloudProviderError.notFound(path)
    }

    public func folderSize(at path: String) async throws -> Int64 {
        let entries = try await listFolderRaw(path: path)
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

    public func storageQuota() async throws -> CloudStorageQuota? {
        // The account root (jfs/{user}) returns <capacity>/<usage>; capacity
        // -1 means an unlimited plan → no upper bound.
        guard let url = URL(string: Self.jfsBase + encodePath(credentials.username)) else { return nil }
        let data = try await jfsRequest(url, method: "GET")
        guard let usage = JottacloudXMLParser.parseAccountUsage(data: data) else { return nil }
        let total: Int64? = usage.capacity < 0 ? nil : usage.capacity
        return CloudStorageQuota(usedBytes: usage.usage, totalBytes: total)
    }

    // MARK: - Share links
    //
    // Sharing is part of the same undocumented JFS API, and these are the
    // endpoints rclone's jottacloud backend has used for years:
    //
    //   GET {jfsURL}?mode=enableShare    turn the public link on
    //   GET {jfsURL}?mode=disableShare   turn it off again
    //   GET {jfsURL}                     read it back
    //
    // All three answer with the file's ordinary JFS XML; a shared file's
    // document additionally carries <publicURI> and <publicSharePath>. There
    // is no password and no expiry — sharing is a plain on/off toggle, so
    // there is nothing an update could change (`canUpdate` is false).

    /// Turns the public link on and returns it.
    ///
    /// `options` is accepted and ignored: `enableShare` takes no parameters,
    /// and `shareLinkCapabilities` advertises neither password nor expiry, so
    /// the UI never offers those fields.
    public func createShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        let url = jfsURL(forPath: path, query: [URLQueryItem(name: "mode", value: "enableShare")])
        let data = try await shareRequest(url)
        guard let share = Self.parsePublicShare(data: data) else {
            // A 2xx whose XML carries no <publicURI> means the toggle didn't
            // take — an account or plan that can't share, most likely.
            throw CloudProviderError.commandFailed(
                L10n.text("Jottacloud accepted the request but returned no public link for this file.")
            )
        }
        return try shareLink(from: share)
    }

    /// The public link this file already has, or nil when it isn't shared.
    ///
    /// A plain file GET carries <publicURI> only while sharing is on, so its
    /// absence *is* the "not shared" answer rather than a failure. A missing
    /// file still throws (`shareRequest` maps 404 to `.notFound`), so the two
    /// can't be confused.
    public func existingShareLink(at path: String) async throws -> CloudShareLink? {
        let data = try await shareRequest(jfsURL(forPath: path))
        guard let share = Self.parsePublicShare(data: data) else { return nil }
        return try shareLink(from: share)
    }

    /// Withdraws the public link. `disableShare` on a file that isn't shared is
    /// a no-op that succeeds, so this deliberately doesn't look first: the
    /// caller's goal — "this file is not shared" — ends up true either way.
    public func removeShareLink(at path: String) async throws {
        let url = jfsURL(forPath: path, query: [URLQueryItem(name: "mode", value: "disableShare")])
        _ = try await shareRequest(url)
    }

    /// What a shared file's JFS document says about its public link.
    private struct PublicShare {
        /// Opaque id the public-download endpoint is keyed by.
        let publicURI: String
        /// Path of the public landing page under www.jottacloud.com. Empty on
        /// the (unexpected) documents that carry only a publicURI.
        let publicSharePath: String
    }

    private func shareLink(from share: PublicShare) throws -> CloudShareLink {
        let direct = URL(string: Self.wwwBase
            + "opin/io/downloadPublic/"
            + encodePath(credentials.username) + "/"
            + encodePath(share.publicURI))
        let page = share.publicSharePath.isEmpty
            ? nil
            : URL(string: Self.wwwBase + encodePath(relativePath(share.publicSharePath)))
        // The landing page is what the web UI hands out; fall back to the
        // direct link so a document missing publicSharePath still yields
        // something usable rather than an error.
        guard let landing = page ?? direct else { throw CloudProviderError.invalidResponse }
        return CloudShareLink(url: landing, directDownloadURL: direct)
    }

    /// A share-mode JFS request. Same access token, session and error mapping
    /// as `jfsRequest`; the one difference is that a failure body is scanned
    /// for JFS's own `<message>`, so a refusal reaches the user in
    /// Jottacloud's words rather than as a bare status code.
    private func shareRequest(_ url: URL) async throws -> Data {
        let req = try await authedRequest(url, method: "GET")
        let (data, response) = try await session.data(for: req)
        guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
        guard (200...299).contains(http.statusCode) else {
            jottaLog.error("[Jottacloud] share request failed: HTTP \(http.statusCode)")
            // 404 keeps its mapped `.notFound` so callers can still tell a
            // missing file from a share the server refused.
            if http.statusCode != 404, let message = Self.parseShareError(data: data) {
                throw CloudProviderError.commandFailed(message)
            }
            throw Self.mapError(http.statusCode, path: url.path)
        }
        return data
    }

    /// `<publicURI>`/`<publicSharePath>` out of a JFS file document, or nil
    /// when the file carries no (non-empty) publicURI — i.e. isn't shared.
    private static func parsePublicShare(data: Data) -> PublicShare? {
        let delegate = ShareDelegate()
        let parser = XMLParser(data: data)
        parser.delegate = delegate
        parser.parse()
        guard let uri = delegate.publicURI, !uri.isEmpty else { return nil }
        return PublicShare(publicURI: uri, publicSharePath: delegate.publicSharePath ?? "")
    }

    /// JFS wraps a failure in `<error>` with a `<message>` and a `<reason>`.
    private static func parseShareError(data: Data) -> String? {
        let delegate = ShareDelegate()
        let parser = XMLParser(data: data)
        parser.delegate = delegate
        parser.parse()
        for candidate in [delegate.message, delegate.reason] {
            if let candidate, !candidate.isEmpty { return candidate }
        }
        return nil
    }

    // MARK: - Request plumbing

    private func authedRequest(_ url: URL, method: String) async throws -> URLRequest {
        let token = try await validAccessToken()
        var req = URLRequest(url: url)
        req.httpMethod = method
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        return req
    }

    /// A JFS request expecting an XML (or empty) body. Returns the response
    /// data; throws a mapped `CloudProviderError` on non-2xx.
    @discardableResult
    private func jfsRequest(_ url: URL, method: String) async throws -> Data {
        let req = try await authedRequest(url, method: method)
        let (data, response) = try await session.data(for: req)
        guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
        guard (200...299).contains(http.statusCode) else {
            throw Self.mapError(http.statusCode, path: url.path)
        }
        return data
    }

    // MARK: - Path helpers

    /// Strip the leading slash and collapse "/" → "" so the path can be joined
    /// onto the device/mountpoint root.
    private func relativePath(_ path: String) -> String {
        var p = path
        while p.hasPrefix("/") { p.removeFirst() }
        return p
    }

    private func normalizedDir(_ path: String) -> String {
        if path.isEmpty || path == "/" { return "/" }
        return path.hasPrefix("/") ? path : "/\(path)"
    }

    /// JFS URL for a FileFluss path: jfs/{user}/{device}/{mountpoint}/{path}.
    private func jfsURL(forPath path: String, query: [URLQueryItem] = []) -> URL {
        let rel = relativePath(path)
        var full = "\(credentials.username)/\(device)/\(mountpoint)"
        if !rel.isEmpty { full += "/\(rel)" }
        var components = URLComponents(string: Self.jfsBase + encodePath(full))!
        if !query.isEmpty { components.queryItems = query }
        return components.url!
    }

    /// Absolute JFS path used as the value of `mv`/`cp`:
    /// /{user}/{device}/{mountpoint}/{dest}.
    private func absoluteJFSPath(_ path: String) -> String {
        let rel = relativePath(path)
        var full = "/\(credentials.username)/\(device)/\(mountpoint)"
        if !rel.isEmpty { full += "/\(rel)" }
        return full
    }

    /// Path for the files-v1 allocate API: /jfs/{device}/{mountpoint}/{path}
    /// (no username — inferred from the token). Sent as a JSON string value, so
    /// it is not percent-encoded.
    private func allocatePath(_ path: String) -> String {
        let rel = relativePath(path)
        var full = "/jfs/\(device)/\(mountpoint)"
        if !rel.isEmpty { full += "/\(rel)" }
        return full
    }

    /// Percent-encode a JFS path, preserving the "/" separators.
    private func encodePath(_ path: String) -> String {
        path.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? path
    }

    // MARK: - Static helpers

    private static func formEncode(_ form: [String: String]) -> String {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        return form.map { key, value in
            let k = key.addingPercentEncoding(withAllowedCharacters: allowed) ?? key
            let v = value.addingPercentEncoding(withAllowedCharacters: allowed) ?? value
            return "\(k)=\(v)"
        }.joined(separator: "&")
    }

    private static func decodeBase64URL(_ string: String) -> Data? {
        var s = string.replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        let pad = s.count % 4
        if pad != 0 { s += String(repeating: "=", count: 4 - pad) }
        return Data(base64Encoded: s)
    }

    /// MD5 of a file, computed by streaming so large files never load fully
    /// into memory. Returned as a lowercase hex string (what allocate wants).
    static func streamingMD5(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = Insecure.MD5()
        while autoreleasepool(invoking: {
            let chunk = handle.readData(ofLength: 4 * 1024 * 1024)
            if chunk.isEmpty { return false }
            hasher.update(data: chunk)
            return true
        }) {}
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    /// Jottacloud timestamp format, e.g. "2024-01-15-T10:30:00Z".
    static func jottaTime(_ date: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "yyyy-MM-dd'-'T'HH:mm:ss'Z'"
        return f.string(from: date)
    }

    private static func mapError(_ status: Int, path: String) -> CloudProviderError {
        switch status {
        case 401, 403: return .notAuthenticated
        case 404: return .notFound(path)
        case 429: return .rateLimited
        case 500...599: return .serverError(status)
        default: return .serverError(status)
        }
    }

    // MARK: - Wire types

    private struct LoginToken: Decodable {
        let username: String
        let wellKnownLink: String
        let authToken: String
        enum CodingKeys: String, CodingKey {
            case username
            case wellKnownLink = "well_known_link"
            case authToken = "auth_token"
        }
    }

    private struct WellKnown: Decodable {
        let tokenEndpoint: String
        enum CodingKeys: String, CodingKey { case tokenEndpoint = "token_endpoint" }
    }

    private struct TokenResponse: Decodable {
        let accessToken: String
        let refreshToken: String?
        let expiresIn: Int?
        enum CodingKeys: String, CodingKey {
            case accessToken = "access_token"
            case refreshToken = "refresh_token"
            case expiresIn = "expires_in"
        }
    }

    private struct AllocateRequest: Encodable {
        let path: String
        let bytes: Int64
        let md5: String
        let created: String
        let modified: String
    }

    private struct AllocateResponse: Decodable {
        let state: String
        let uploadUrl: String?
        let resumePos: Int64?
        enum CodingKeys: String, CodingKey {
            case state
            case uploadUrl = "upload_url"
            case resumePos = "resume_pos"
        }
    }
}

// MARK: - Share XML

/// Pulls the public-link fields out of a JFS file document, and the error
/// fields out of a JFS failure body.
///
/// `<publicURI>` and `<publicSharePath>` are direct children of `<file>`, and
/// `<message>`/`<reason>` of `<error>`; none of those names occurs anywhere
/// else in a JFS document, so a flat scan is enough and no depth tracking is
/// needed. Kept private here rather than added to `JottacloudXMLParser`, whose
/// two delegates parse listings and quota and have no share concern.
private final class ShareDelegate: NSObject, XMLParserDelegate {
    var publicURI: String?
    var publicSharePath: String?
    var message: String?
    var reason: String?

    private var text = ""
    private var capturing = false

    private static let wanted: Set<String> = ["publicURI", "publicSharePath", "message", "reason"]

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName: String?, attributes attributeDict: [String: String] = [:]) {
        capturing = Self.wanted.contains(elementName)
        text = ""
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        if capturing { text += string }
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName: String?) {
        if capturing {
            let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
            // First occurrence wins, so a later empty element can't clear a
            // value that was already found.
            switch elementName {
            case "publicURI": if publicURI == nil { publicURI = value }
            case "publicSharePath": if publicSharePath == nil { publicSharePath = value }
            case "message": if message == nil { message = value }
            case "reason": if reason == nil { reason = value }
            default: break
            }
        }
        capturing = false
        text = ""
    }
}
