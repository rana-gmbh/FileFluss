import Foundation
import os
import Security
import CryptoKit
import UniformTypeIdentifiers

private let drimeLog = Logger(subsystem: "com.rana.FileFluss", category: "drime")

public struct DrimeCredentials: Codable, Sendable {
    /// How the account was connected. Decides whether a 401 can be
    /// recovered by refreshing (OAuth) or means the token was deleted
    /// (personal access token, which never expires on its own).
    public enum Kind: String, Codable, Sendable {
        case oauth
        case personalToken
    }

    public let kind: Kind
    public let accessToken: String
    /// Empty for a personal access token.
    public let refreshToken: String
    /// `.distantFuture` for a personal access token.
    public let expiresAt: Date
    public let email: String
    public let displayName: String

    public init(
        kind: Kind,
        accessToken: String,
        refreshToken: String,
        expiresAt: Date,
        email: String,
        displayName: String
    ) {
        self.kind = kind
        self.accessToken = accessToken
        self.refreshToken = refreshToken
        self.expiresAt = expiresAt
        self.email = email
        self.displayName = displayName
    }
}

/// Drime Cloud (https://docs.drime.cloud).
///
/// Drime addresses everything by numeric entry id; FileFluss addresses by
/// path. The client keeps a path → id cache filled from listings, the same
/// way Box does. The root of the personal Drive has no entry of its own —
/// the API spells it as an omitted `folderId` / a `null` parent — and the
/// cache stores it as id 0.
///
/// Only the personal workspace (`workspaceId=0`) is exposed.
public actor DrimeAPIClient {
    // MARK: - OAuth registration

    /// Client ID of the FileFluss application in Drime's developer console
    /// (https://app.drime.cloud/developers), registered as "A desktop or
    /// mobile app" with access to "The whole Drive" and the redirect URL
    /// `http://127.0.0.1/callback`.
    ///
    /// A desktop app is a public client: PKCE stands in for a secret, so
    /// there is none to ship. While this is empty, browser sign-in is not
    /// offered and accounts connect with a personal access token instead.
    static let clientId = ""

    public static var isOAuthAvailable: Bool { !clientId.isEmpty }

    /// `.write` brings its `.read`. `analytics.read` is what
    /// `/user/space-usage` needs for the storage bar.
    static let scopes = "files.write folders.write sharing.write analytics.read"

    private static let authorizeURL = "https://app.drime.cloud/oauth/authorize"
    private static let tokenURL = "https://app.drime.cloud/oauth/token"
    private static let revokeURL = "https://app.drime.cloud/oauth/revoke"
    private static let apiURL = "https://app.drime.cloud/api/v1"
    private static let shareBaseURL = "https://app.drime.cloud/drive/shares/"

    /// Where the user creates a personal access token.
    public static let tokenSettingsURL = URL(string: "https://app.drime.cloud/account-settings#developers")!

    private static let userAgent = "FileFluss/1.0 (macOS)"

    // MARK: - Upload sizing

    /// Below this a file goes up in one `POST /uploads`; at or above it,
    /// through an S3 multipart upload. Drime's own threshold.
    static let multipartThreshold: Int64 = 5 * 1024 * 1024

    /// Parts are at least this big. Drime's minimum is 5 MiB; larger parts
    /// mean fewer signing round trips, which is what dominates on a fast
    /// line.
    static let minimumPartSize: Int64 = 16 * 1024 * 1024
    static let maximumParts: Int64 = 10_000
    /// Parts in flight at once. Drime suggests up to 8.
    static let partConcurrency = 4

    /// Drime serves at most this many entries per listing page.
    private static let pageSize = 1000

    // MARK: - State

    private(set) var credentials: DrimeCredentials
    /// Called whenever the credentials change. Drime rotates refresh tokens
    /// and treats a reused one as stolen — revoking every token FileFluss
    /// holds — so the new one must reach the keychain before anything else.
    private var credentialsDidChange: (@Sendable (DrimeCredentials) -> Void)?
    private var inflightRefresh: Task<DrimeCredentials, Error>?
    private let session: URLSession

    /// Path → entry id. "/" is the root, id 0.
    private var pathIdCache: [String: Int64] = ["/": 0]

    public init(credentials: DrimeCredentials) {
        self.credentials = credentials
        self.session = URLSession(configuration: .fileTransfer())
    }

    public func setCredentialsDidChange(_ handler: @escaping @Sendable (DrimeCredentials) -> Void) {
        credentialsDidChange = handler
    }

    // MARK: - Sign-in

    public static func startOAuthFlow() async throws -> DrimeCredentials {
        guard isOAuthAvailable else { throw CloudProviderError.notImplemented }
        let state = randomURLSafeString(byteCount: 32)
        let verifier = randomURLSafeString(byteCount: 48)
        let challenge = base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))

        let result = try await OAuthSession.authenticate(
            callbackURLScheme: "filefluss-oauth",
            loopback: .ipLiteralCallback
        ) { redirectURI in
            var components = URLComponents(string: authorizeURL)!
            components.queryItems = [
                URLQueryItem(name: "client_id", value: clientId),
                URLQueryItem(name: "redirect_uri", value: redirectURI),
                URLQueryItem(name: "response_type", value: "code"),
                URLQueryItem(name: "scope", value: scopes),
                URLQueryItem(name: "state", value: state),
                URLQueryItem(name: "code_challenge", value: challenge),
                URLQueryItem(name: "code_challenge_method", value: "S256"),
            ]
            return components.url!
        }

        let params = URLComponents(url: result.callbackURL, resolvingAgainstBaseURL: false)?.queryItems
        guard params?.first(where: { $0.name == "state" })?.value == state else {
            drimeLog.error("[Drime] OAuth state mismatch — rejecting callback")
            throw CloudProviderError.unauthorized
        }
        if let error = params?.first(where: { $0.name == "error" })?.value {
            drimeLog.error("[Drime] OAuth error: \(error)")
            throw CloudProviderError.unauthorized
        }
        guard let code = params?.first(where: { $0.name == "code" })?.value else {
            throw CloudProviderError.invalidResponse
        }

        let token = try await postTokenRequest([
            "grant_type": "authorization_code",
            "code": code,
            "redirect_uri": result.redirectURI,
            "client_id": clientId,
            "code_verifier": verifier,
        ])
        guard let refresh = token.refresh_token, !refresh.isEmpty else {
            throw CloudProviderError.invalidResponse
        }
        let user = try await fetchUser(accessToken: token.access_token)
        drimeLog.info("[Drime] Authenticated via OAuth, scope: \(token.scope ?? "?")")
        return DrimeCredentials(
            kind: .oauth,
            accessToken: token.access_token,
            refreshToken: refresh,
            expiresAt: Date().addingTimeInterval(TimeInterval(token.expires_in ?? 3600)),
            email: user.email ?? "",
            displayName: user.display_name ?? ""
        )
    }

    /// Checks a personal access token by asking who it belongs to — every
    /// token may read `/users/me`, whatever its permissions.
    public static func validatePersonalToken(_ token: String) async throws -> DrimeCredentials {
        let token = token.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !token.isEmpty else { throw CloudProviderError.invalidCredentials }
        let user: DrimeUser
        do {
            user = try await fetchUser(accessToken: token)
        } catch CloudProviderError.notAuthenticated {
            throw CloudProviderError.commandFailed(L10n.text("Drime didn't accept this token. Check that it was copied completely and hasn't been deleted."))
        }
        return DrimeCredentials(
            kind: .personalToken,
            accessToken: token,
            refreshToken: "",
            expiresAt: .distantFuture,
            email: user.email ?? "",
            displayName: user.display_name ?? ""
        )
    }

    /// Ends the authorization on Drime's side, so a removed account doesn't
    /// leave a working grant behind. Personal tokens can only be deleted on
    /// the website. Best effort: removal goes ahead either way.
    public func revoke() async {
        guard credentials.kind == .oauth, !credentials.refreshToken.isEmpty else { return }
        var request = URLRequest(url: URL(string: Self.revokeURL)!)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
        request.httpBody = Self.formEncode(["token": credentials.refreshToken, "client_id": Self.clientId])
        _ = try? await session.data(for: request)
    }

    private static func fetchUser(accessToken: String) async throws -> DrimeUser {
        var request = URLRequest(url: URL(string: "\(apiURL)/users/me")!)
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
        guard (200...299).contains(http.statusCode) else {
            throw mapHTTPError(statusCode: http.statusCode, body: data)
        }
        return try JSONDecoder().decode(DrimeUserEnvelope.self, from: data).user
    }

    private static func postTokenRequest(_ fields: [String: String]) async throws -> DrimeTokenResponse {
        var request = URLRequest(url: URL(string: tokenURL)!)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        request.httpBody = formEncode(fields)
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
        guard (200...299).contains(http.statusCode) else {
            let oauthError = try? JSONDecoder().decode(DrimeOAuthError.self, from: data)
            drimeLog.error("[Drime] Token request failed: HTTP \(http.statusCode) \(oauthError?.error ?? "")")
            // invalid_grant on a refresh means the grant is gone (revoked,
            // expired after 90 days, or a refresh token was reused): only a
            // new sign-in helps.
            if oauthError?.error == "invalid_grant" { throw CloudProviderError.notAuthenticated }
            if http.statusCode == 429 { throw CloudProviderError.rateLimited }
            let detail = oauthError?.error_description ?? oauthError?.error ?? "HTTP \(http.statusCode)"
            throw CloudProviderError.commandFailed(L10n.format("Drime sign-in failed: %@", detail))
        }
        return try JSONDecoder().decode(DrimeTokenResponse.self, from: data)
    }

    // MARK: - Token refresh

    public func refreshTokenIfNeeded() async throws -> DrimeCredentials {
        if let inflightRefresh { return try await inflightRefresh.value }
        guard credentials.kind == .oauth,
              Date() >= credentials.expiresAt.addingTimeInterval(-60) else {
            return credentials
        }
        return try await startRefresh()
    }

    private func forceRefresh() async throws -> DrimeCredentials {
        if let inflightRefresh { return try await inflightRefresh.value }
        guard credentials.kind == .oauth else { throw CloudProviderError.notAuthenticated }
        return try await startRefresh()
    }

    /// One refresh at a time: a refresh token presented twice makes Drime
    /// revoke the whole grant.
    private func startRefresh() async throws -> DrimeCredentials {
        guard !credentials.refreshToken.isEmpty else { throw CloudProviderError.notAuthenticated }
        let task = Task<DrimeCredentials, Error> { [self] in try await self.performRefresh() }
        inflightRefresh = task
        defer { inflightRefresh = nil }
        return try await task.value
    }

    private func performRefresh() async throws -> DrimeCredentials {
        let token = try await Self.postTokenRequest([
            "grant_type": "refresh_token",
            "refresh_token": credentials.refreshToken,
            "client_id": Self.clientId,
        ])
        let updated = DrimeCredentials(
            kind: .oauth,
            accessToken: token.access_token,
            refreshToken: token.refresh_token ?? credentials.refreshToken,
            expiresAt: Date().addingTimeInterval(TimeInterval(token.expires_in ?? 3600)),
            email: credentials.email,
            displayName: credentials.displayName
        )
        credentials = updated
        credentialsDidChange?(updated)
        return updated
    }

    // MARK: - Account

    public func userDisplayName() -> String {
        credentials.displayName.isEmpty ? credentials.email : credentials.displayName
    }

    /// `available` is the plan's total, not what is left of it.
    public func storageQuota() async throws -> CloudStorageQuota? {
        struct Usage: Decodable {
            let used: Int64?
            let available: Int64?
        }
        let usage: Usage = try await get("/user/space-usage", query: [URLQueryItem(name: "workspaceId", value: "0")])
        let total = (usage.available ?? 0) > 0 ? usage.available : nil
        return CloudStorageQuota(usedBytes: usage.used ?? 0, totalBytes: total)
    }

    // MARK: - Listing

    public func listFolder(path: String) async throws -> [CloudFileItem] {
        let folderId = try await resolveFolderId(path)
        let entries = try await listEntries(folderId: folderId)
        let normalized = Self.normalize(path)
        for entry in entries {
            pathIdCache[Self.join(normalized, entry.name)] = entry.id
        }
        pathIdCache.retainPathChildren(of: normalized, named: Set(entries.map(\.name)))
        return entries.map { $0.cloudFileItem(parentPath: normalized) }
    }

    /// Decodes one page of `GET /drive/file-entries` into panel items, the
    /// way a listing does. Exposed for the tests, which pin the wire format.
    public static func items(fromListingPage data: Data, parentPath: String) throws -> [CloudFileItem] {
        try JSONDecoder().decode(DrimeListing.self, from: data).data
            .map { $0.cloudFileItem(parentPath: normalize(parentPath)) }
    }

    /// Every entry directly inside `folderId` (0 = root), all pages.
    private func listEntries(folderId: Int64) async throws -> [DrimeEntry] {
        var all: [DrimeEntry] = []
        var page = 1
        while true {
            var query = [
                URLQueryItem(name: "workspaceId", value: "0"),
                URLQueryItem(name: "perPage", value: "\(Self.pageSize)"),
                URLQueryItem(name: "page", value: "\(page)"),
                URLQueryItem(name: "orderBy", value: "name"),
                URLQueryItem(name: "orderDir", value: "asc"),
            ]
            if folderId != 0 {
                query.append(URLQueryItem(name: "folderId", value: "\(folderId)"))
            }
            let listing: DrimeListing = try await get("/drive/file-entries", query: query)
            // Past the last page it allows, Drime serves that page again
            // instead of an empty one. Returning what we have would make
            // the rest of the folder look deleted — to a mirror sync above
            // all — so refuse instead.
            if let current = listing.current_page, current != page {
                throw CloudProviderError.commandFailed(L10n.text("This Drime folder holds more items than Drime will list. Split it into smaller folders to browse it here."))
            }
            all.append(contentsOf: listing.data)
            guard let last = listing.last_page, page < last, !listing.data.isEmpty else { break }
            page += 1
        }
        return all
    }

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        let entry = try await resolveEntry(path)
        return entry.cloudFileItem(parentPath: Self.parent(of: Self.normalize(path)))
    }

    /// Folder entries carry no reliable aggregate size, so add it up.
    public func folderSize(at path: String) async throws -> Int64 {
        var total: Int64 = 0
        for item in try await listFolder(path: path) {
            try Task.checkCancellation()
            total += item.isDirectory ? try await folderSize(at: item.path) : item.size
        }
        return total
    }

    // MARK: - Download

    /// `GET /file-entries/{id}` serves the content (with `/model` it would
    /// be the JSON). Drime may redirect to its storage; URLSession drops the
    /// Authorization header when the host changes, which the signed URL
    /// doesn't need anyway.
    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let entry = try await resolveEntry(remotePath)
        guard !entry.isFolder else { throw CloudProviderError.notFound(remotePath) }
        let url = URL(string: "\(Self.apiURL)/file-entries/\(entry.id)")!

        func attempt(forceRefreshFirst: Bool) async throws -> (URL, HTTPURLResponse) {
            let creds = forceRefreshFirst ? try await forceRefresh() : try await refreshTokenIfNeeded()
            var request = URLRequest(url: url)
            request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
            request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
            let (temp, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
            guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
            return (temp, http)
        }

        var (tempURL, http) = try await attempt(forceRefreshFirst: false)
        if http.statusCode == 401, credentials.kind == .oauth {
            try? FileManager.default.removeItem(at: tempURL)
            (tempURL, http) = try await attempt(forceRefreshFirst: true)
        }
        guard (200...299).contains(http.statusCode) else {
            let body = (try? Data(contentsOf: tempURL)) ?? Data()
            try? FileManager.default.removeItem(at: tempURL)
            drimeLog.error("[Drime] Download failed: HTTP \(http.statusCode)")
            throw Self.mapHTTPError(statusCode: http.statusCode, body: body)
        }
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    // MARK: - Upload

    /// Drime never overwrites: a file uploaded next to one of the same name
    /// is stored as "name (1)". So a replacement uploads first, then trashes
    /// the old file and gives the new one its name — the old content stays
    /// until the new content is safely stored.
    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let path = Self.normalize(remotePath)
        let name = (path as NSString).lastPathComponent
        let parentId = try await resolveFolderId(Self.parent(of: path))
        let size = (try FileManager.default.attributesOfItem(atPath: localURL.path)[.size] as? NSNumber)?.int64Value ?? 0

        let existing = try await listEntries(folderId: parentId).first { $0.name == name }
        if let existing, existing.isFolder {
            throw CloudProviderError.commandFailed(L10n.format("A folder named “%@” is already there.", name))
        }

        var uploaded: DrimeEntry
        if size < Self.multipartThreshold {
            uploaded = try await simpleUpload(localURL, name: name, parentId: parentId, onBytes: onBytes)
        } else {
            uploaded = try await multipartUpload(localURL, size: size, name: name, parentId: parentId, onBytes: onBytes)
        }

        if let existing {
            try await trash([existing.id])
        }
        if uploaded.name != name {
            uploaded = try await rename(entryId: uploaded.id, to: name)
        }
        pathIdCache[path] = uploaded.id
    }

    private func simpleUpload(_ localURL: URL, name: String, parentId: Int64, onBytes: ByteProgressHandler?) async throws -> DrimeEntry {
        let boundary = "FileFluss-\(UUID().uuidString)"
        var body = Data()
        func field(_ key: String, _ value: String) {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(key)\"\r\n\r\n\(value)\r\n".utf8))
        }
        if parentId != 0 { field("parentId", "\(parentId)") }
        field("workspaceId", "0")
        // Just the leaf: a longer relativePath makes Drime build folders
        // from it.
        field("relativePath", name)
        let quotedName = name.replacingOccurrences(of: "\"", with: "%22")
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"\(quotedName)\"\r\nContent-Type: \(Self.mimeType(for: name))\r\n\r\n".utf8))
        body.append(try Data(contentsOf: localURL))
        body.append(Data("\r\n--\(boundary)--\r\n".utf8))

        let url = URL(string: "\(Self.apiURL)/uploads")!
        func attempt(forceRefreshFirst: Bool) async throws -> (Data, HTTPURLResponse) {
            let creds = forceRefreshFirst ? try await forceRefresh() : try await refreshTokenIfNeeded()
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
            request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
            let (data, response) = try await session.uploadReportingProgress(for: request, body: body, onBytes: onBytes)
            guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
            return (data, http)
        }
        var (data, http) = try await attempt(forceRefreshFirst: false)
        if http.statusCode == 401, credentials.kind == .oauth {
            (data, http) = try await attempt(forceRefreshFirst: true)
        }
        guard (200...299).contains(http.statusCode) else {
            drimeLog.error("[Drime] Upload failed: HTTP \(http.statusCode): \(String(decoding: data.prefix(300), as: UTF8.self))")
            if let sizeError = CloudProviderError.sizeLimitError(forStatus: http.statusCode, localFile: localURL) { throw sizeError }
            throw Self.mapHTTPError(statusCode: http.statusCode, body: data)
        }
        return try JSONDecoder().decode(DrimeFileEntryEnvelope.self, from: data).fileEntry
    }

    /// S3 multipart: open, then sign and PUT each part straight to
    /// storage, complete, and finally register the object as a Drime entry.
    private func multipartUpload(_ localURL: URL, size: Int64, name: String, parentId: Int64, onBytes: ByteProgressHandler?) async throws -> DrimeEntry {
        let partSize = Self.partSize(forFileSize: size)
        let partCount = Int((size + partSize - 1) / partSize)
        let mime = Self.mimeType(for: name)
        // Drime's multipart endpoints insist on an extension.
        let fileExtension = (name as NSString).pathExtension.isEmpty ? "bin" : (name as NSString).pathExtension

        struct Create: Encodable {
            let filename: String
            let mime: String
            let size: Int64
            let `extension`: String
            let relativePath: String
            let workspaceId: Int
            let parentId: Int64?
        }
        struct Created: Decodable {
            let key: String
            let uploadId: String
        }
        let created: Created = try await send("POST", "/s3/multipart/create", body: Create(
            filename: name, mime: mime, size: size, extension: fileExtension,
            relativePath: name, workspaceId: 0, parentId: parentId == 0 ? nil : parentId
        ))

        do {
            let parts = try await uploadParts(
                localURL, partSize: partSize, partCount: partCount, fileSize: size,
                key: created.key, uploadId: created.uploadId, onBytes: onBytes
            )

            struct Part: Encodable {
                let PartNumber: Int
                let ETag: String
            }
            struct Complete: Encodable {
                let key: String
                let uploadId: String
                let parts: [Part]
            }
            struct Completed: Decodable { let location: String? }
            let _: Completed = try await send("POST", "/s3/multipart/complete", body: Complete(
                key: created.key, uploadId: created.uploadId,
                parts: parts.map { Part(PartNumber: $0.number, ETag: $0.etag) }
            ))

            struct Entry: Encodable {
                let filename: String
                let size: Int64
                let clientName: String
                let clientMime: String
                let clientExtension: String
                let parentId: Int64?
                let relativePath: String
                let workspaceId: Int
            }
            let entry: DrimeFileEntryEnvelope = try await send("POST", "/s3/entries", body: Entry(
                filename: (created.key as NSString).lastPathComponent,
                size: size, clientName: name, clientMime: mime, clientExtension: fileExtension,
                parentId: parentId == 0 ? nil : parentId, relativePath: name, workspaceId: 0
            ))
            return entry.fileEntry
        } catch {
            await abortMultipart(key: created.key, uploadId: created.uploadId)
            throw error
        }
    }

    /// At least `minimumPartSize`, and large enough to stay under the
    /// 10,000-part limit; whole MiB so every part but the last is equal.
    public static func partSize(forFileSize size: Int64) -> Int64 {
        let mib: Int64 = 1024 * 1024
        let needed = (size + maximumParts - 1) / maximumParts
        let rounded = ((needed + mib - 1) / mib) * mib
        return max(minimumPartSize, rounded)
    }

    private struct UploadedPart: Sendable {
        let number: Int
        let etag: String
    }

    private func uploadParts(
        _ localURL: URL,
        partSize: Int64,
        partCount: Int,
        fileSize: Int64,
        key: String,
        uploadId: String,
        onBytes: ByteProgressHandler?
    ) async throws -> [UploadedPart] {
        let session = self.session
        var results: [UploadedPart] = []
        try await withThrowingTaskGroup(of: UploadedPart.self) { group in
            var next = 1
            func addPart(_ number: Int) {
                group.addTask {
                    try Task.checkCancellation()
                    let offset = Int64(number - 1) * partSize
                    let length = Int(min(partSize, fileSize - offset))
                    let handle = try FileHandle(forReadingFrom: localURL)
                    defer { try? handle.close() }
                    try handle.seek(toOffset: UInt64(offset))
                    guard let chunk = try handle.read(upToCount: length), chunk.count == length else {
                        throw CloudProviderError.commandFailed(L10n.text("The file changed while it was being uploaded."))
                    }
                    let url = try await self.signPart(number, key: key, uploadId: uploadId)
                    let etag = try await Self.putPart(chunk, to: url, session: session, onBytes: onBytes)
                    return UploadedPart(number: number, etag: etag)
                }
            }
            while next <= min(Self.partConcurrency, partCount) {
                addPart(next)
                next += 1
            }
            while let finished = try await group.next() {
                results.append(finished)
                if next <= partCount {
                    addPart(next)
                    next += 1
                }
            }
        }
        return results.sorted { $0.number < $1.number }
    }

    private func signPart(_ number: Int, key: String, uploadId: String) async throws -> URL {
        struct Sign: Encodable {
            let key: String
            let uploadId: String
            let partNumbers: [Int]
        }
        struct Signed: Decodable {
            struct Entry: Decodable {
                let partNumber: Int
                let url: String
            }
            let urls: [Entry]
        }
        let signed: Signed = try await send("POST", "/s3/multipart/batch-sign-part-urls", body: Sign(key: key, uploadId: uploadId, partNumbers: [number]))
        guard let raw = signed.urls.first(where: { $0.partNumber == number })?.url ?? signed.urls.first?.url,
              let url = URL(string: raw) else {
            throw CloudProviderError.invalidResponse
        }
        return url
    }

    /// PUTs one part to its presigned storage URL — no Authorization
    /// header, the signature is in the URL. Transient failures are retried;
    /// progress is only reported once per byte however often it's resent.
    private static func putPart(_ chunk: Data, to url: URL, session: URLSession, onBytes: ByteProgressHandler?) async throws -> String {
        let progress = PartProgress(onBytes)
        var delay: UInt64 = 1_000_000_000
        for attempt in 1...4 {
            try Task.checkCancellation()
            progress.beginAttempt()
            var request = URLRequest(url: url)
            request.httpMethod = "PUT"
            request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
            do {
                let (data, response) = try await session.uploadReportingProgress(for: request, body: chunk, onBytes: progress.handler)
                guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
                if (200...299).contains(http.statusCode) {
                    // S3 wants the ETag back exactly as sent, quotes and all.
                    guard let etag = http.value(forHTTPHeaderField: "ETag"), !etag.isEmpty else {
                        throw CloudProviderError.invalidResponse
                    }
                    return etag
                }
                guard attempt < 4, http.statusCode == 429 || (500...599).contains(http.statusCode) else {
                    drimeLog.error("[Drime] Part upload failed: HTTP \(http.statusCode): \(String(decoding: data.prefix(300), as: UTF8.self))")
                    throw mapHTTPError(statusCode: http.statusCode, body: data)
                }
            } catch let error as URLError where attempt < 4 && error.code != .cancelled {
                drimeLog.info("[Drime] Part upload error \(error.code.rawValue), retrying")
            }
            try await Task.sleep(nanoseconds: delay)
            delay *= 2
        }
        throw CloudProviderError.invalidResponse
    }

    /// Best effort, detached so it still runs when the upload was cancelled.
    private func abortMultipart(key: String, uploadId: String) async {
        struct Abort: Encodable {
            let key: String
            let uploadId: String
        }
        guard let creds = try? await refreshTokenIfNeeded(),
              let body = try? JSONEncoder().encode(Abort(key: key, uploadId: uploadId)) else { return }
        let session = self.session
        await Task.detached {
            var request = URLRequest(url: URL(string: "\(Self.apiURL)/s3/multipart/abort")!)
            request.httpMethod = "POST"
            request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
            request.httpBody = body
            _ = try? await session.data(for: request)
        }.value
    }

    // MARK: - Changes

    public func createFolder(at path: String) async throws {
        let path = Self.normalize(path)
        if (try? await resolveEntry(path))?.isFolder == true { return }
        let parentId = try await resolveFolderId(Self.parent(of: path))
        struct Create: Encodable {
            let name: String
            let parentId: Int64?
        }
        struct Created: Decodable { let folder: DrimeEntry }
        let created: Created = try await send(
            "POST", "/folders",
            query: [URLQueryItem(name: "workspaceId", value: "0")],
            body: Create(name: (path as NSString).lastPathComponent, parentId: parentId == 0 ? nil : parentId)
        )
        pathIdCache[path] = created.folder.id
    }

    /// Moves to Drime's trash, like the web app's delete — recoverable for
    /// the user there.
    public func deleteItem(at path: String) async throws {
        let path = Self.normalize(path)
        let entry = try await resolveEntry(path)
        try await trash([entry.id])
        pathIdCache.removePathSubtree(path)
    }

    private func trash(_ ids: [Int64]) async throws {
        struct Delete: Encodable {
            let entryIds: [Int64]
            let deleteForever: Bool
        }
        let _: DrimeStatus = try await send("POST", "/file-entries/delete", body: Delete(entryIds: ids, deleteForever: false))
    }

    public func renameItem(at path: String, to newName: String) async throws {
        let path = Self.normalize(path)
        let entry = try await resolveEntry(path)
        let renamed = try await rename(entryId: entry.id, to: newName)
        pathIdCache.removePathSubtree(path)
        pathIdCache[Self.join(Self.parent(of: path), newName)] = renamed.id
    }

    /// A plain PUT to this route comes back from Drime's origin malformed
    /// (Cloudflare reports a 520), so it goes as a POST with Laravel's
    /// method override, which reaches the same handler.
    private func rename(entryId: Int64, to newName: String) async throws -> DrimeEntry {
        struct Rename: Encodable { let name: String }
        let renamed: DrimeFileEntryEnvelope = try await send(
            "POST", "/file-entries/\(entryId)",
            query: [URLQueryItem(name: "workspaceId", value: "0")],
            body: Rename(name: newName),
            headers: ["X-HTTP-Method-Override": "PUT"]
        )
        return renamed.fileEntry
    }

    public func moveItem(at path: String, toPath newPath: String) async throws {
        let source = Self.normalize(path)
        let destination = Self.normalize(newPath)
        let entry = try await resolveEntry(source)
        let newName = (destination as NSString).lastPathComponent
        let targetParent = Self.parent(of: destination)

        if Self.parent(of: source) != targetParent {
            let parentId = try await resolveFolderId(targetParent)
            struct Move: Encodable {
                let entryIds: [Int64]
                let destinationId: Int64?
                let workspaceId: Int
            }
            let _: DrimeStatus = try await send(
                "POST", "/file-entries/move",
                query: [URLQueryItem(name: "workspaceId", value: "0")],
                body: Move(entryIds: [entry.id], destinationId: parentId == 0 ? nil : parentId, workspaceId: 0)
            )
        }
        if entry.name != newName {
            _ = try await rename(entryId: entry.id, to: newName)
        }
        pathIdCache.removePathSubtree(source)
        pathIdCache[destination] = entry.id
    }

    /// `duplicate` copies whole folders server-side. In a folder that
    /// already has the name it produces "name (1)", hence the rename.
    public func copyItem(at path: String, toPath newPath: String) async throws {
        let source = Self.normalize(path)
        let destination = Self.normalize(newPath)
        let entry = try await resolveEntry(source)
        let parentId = try await resolveFolderId(Self.parent(of: destination))
        struct Duplicate: Encodable {
            let entryIds: [Int64]
            let destinationId: Int64?
            let workspaceId: Int
        }
        struct Duplicated: Decodable { let entries: [DrimeEntry] }
        let result: Duplicated = try await send(
            "POST", "/file-entries/duplicate",
            query: [URLQueryItem(name: "workspaceId", value: "0")],
            body: Duplicate(entryIds: [entry.id], destinationId: parentId == 0 ? nil : parentId, workspaceId: 0)
        )
        guard var copy = result.entries.first else { throw CloudProviderError.invalidResponse }
        let newName = (destination as NSString).lastPathComponent
        if copy.name != newName {
            copy = try await rename(entryId: copy.id, to: newName)
        }
        pathIdCache[destination] = copy.id
    }

    /// Stores the original modification time (Unix milliseconds), which
    /// listings then report as `client_last_modified`.
    public func setModificationDate(at path: String, to date: Date) async throws {
        let entry = try await resolveEntry(Self.normalize(path))
        guard !entry.isFolder else { throw CloudProviderError.notImplemented }
        struct Metadata: Encodable { let lastModified: Int64 }
        let _: DrimeStatus = try await send(
            "POST", "/file-entries/\(entry.id)/metadata",
            body: Metadata(lastModified: Int64((date.timeIntervalSince1970 * 1000).rounded()))
        )
    }

    // MARK: - Share links

    private struct ShareLinkEnvelope: Decodable {
        struct Link: Decodable {
            let hash: String?
            let password: String?
            let expires_at: String?
        }
        let link: Link?
    }

    public func existingShareLink(at path: String) async throws -> CloudShareLink? {
        let entry = try await resolveEntry(Self.normalize(path))
        do {
            let envelope: ShareLinkEnvelope = try await get("/file-entries/\(entry.id)/shareable-link")
            guard let link = envelope.link else { return nil }
            return Self.shareLink(from: link)
        } catch CloudProviderError.notFound {
            return nil
        }
    }

    public func createShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        let entry = try await resolveEntry(Self.normalize(path))
        struct Create: Encodable {
            let password: String?
            let expires_at: String?
            let allow_edit: Bool
            let allow_download: Bool
        }
        let envelope: ShareLinkEnvelope = try await send(
            "POST", "/file-entries/\(entry.id)/shareable-link",
            body: Create(
                password: Self.nonEmpty(options.password),
                expires_at: options.expiry.map(Self.formatTimestamp),
                allow_edit: false,
                allow_download: options.allowDownload
            ),
            surfaceServerMessage: true
        )
        guard let link = envelope.link, let shareLink = Self.shareLink(from: link) else {
            throw CloudProviderError.invalidResponse
        }
        return shareLink
    }

    public func updateShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        let entry = try await resolveEntry(Self.normalize(path))
        struct Update: Encodable {
            let password: String?
            let expiresAt: String?
            let allowEdit: Bool
            let allowDownload: Bool
        }
        let envelope: ShareLinkEnvelope = try await send(
            "PUT", "/file-entries/\(entry.id)/shareable-link",
            body: Update(
                password: Self.nonEmpty(options.password),
                expiresAt: options.expiry.map(Self.formatTimestamp),
                allowEdit: false,
                allowDownload: options.allowDownload
            ),
            surfaceServerMessage: true
        )
        guard let link = envelope.link, let shareLink = Self.shareLink(from: link) else {
            throw CloudProviderError.invalidResponse
        }
        return shareLink
    }

    public func removeShareLink(at path: String) async throws {
        let entry = try await resolveEntry(Self.normalize(path))
        let _: DrimeStatus = try await send("DELETE", "/file-entries/\(entry.id)/shareable-link", surfaceServerMessage: true)
    }

    /// Recipients open `/drive/shares/{hash}`. The password comes back
    /// hashed, so it only tells us whether there is one.
    private static func shareLink(from link: ShareLinkEnvelope.Link) -> CloudShareLink? {
        guard let hash = link.hash, !hash.isEmpty, let url = URL(string: shareBaseURL + hash) else { return nil }
        return CloudShareLink(
            url: url,
            expiresAt: parseTimestamp(link.expires_at),
            hasPassword: !(link.password ?? "").isEmpty
        )
    }

    // MARK: - Path resolution

    private func resolveFolderId(_ path: String) async throws -> Int64 {
        let path = Self.normalize(path)
        if let cached = pathIdCache[path] { return cached }
        let entry = try await resolveEntry(path)
        guard entry.isFolder else { throw CloudProviderError.notFound(path) }
        return entry.id
    }

    /// Walks the path from the root, listing each parent once and caching
    /// every child it sees on the way.
    private func resolveEntry(_ path: String) async throws -> DrimeEntry {
        let path = Self.normalize(path)
        guard path != "/" else { return DrimeEntry.root }
        let parentPath = Self.parent(of: path)
        let parentId = try await resolveFolderId(parentPath)
        let name = (path as NSString).lastPathComponent
        let children = try await listEntries(folderId: parentId)
        for child in children {
            pathIdCache[Self.join(parentPath, child.name)] = child.id
        }
        guard let match = children.first(where: { $0.name == name }) else {
            pathIdCache.removePathSubtree(path)
            throw CloudProviderError.notFound(path)
        }
        return match
    }

    static func normalize(_ path: String) -> String {
        let trimmed = path.split(separator: "/", omittingEmptySubsequences: true).joined(separator: "/")
        return "/" + trimmed
    }

    static func parent(of path: String) -> String {
        let parent = (path as NSString).deletingLastPathComponent
        return parent.isEmpty ? "/" : parent
    }

    static func join(_ parent: String, _ name: String) -> String {
        parent == "/" ? "/\(name)" : "\(parent)/\(name)"
    }

    // MARK: - HTTP

    private func get<T: Decodable>(_ path: String, query: [URLQueryItem] = []) async throws -> T {
        try await send("GET", path, query: query)
    }

    /// One authorized JSON request. Refreshes ahead of expiry, and once
    /// more on a 401 (OAuth only — a personal token that gets a 401 was
    /// deleted). A 429 is retried after a pause for any method since Drime
    /// didn't act on it; 5xx only for GET, where repeating can't duplicate
    /// anything.
    private func send<T: Decodable>(
        _ method: String,
        _ path: String,
        query: [URLQueryItem] = [],
        body: (any Encodable)? = nil,
        headers: [String: String] = [:],
        surfaceServerMessage: Bool = false
    ) async throws -> T {
        var components = URLComponents(string: "\(Self.apiURL)\(path)")!
        if !query.isEmpty { components.queryItems = query }
        guard let url = components.url else { throw CloudProviderError.invalidResponse }
        let encodedBody = try body.map { try JSONEncoder().encode($0) }

        func attempt(forceRefreshFirst: Bool) async throws -> (Data, HTTPURLResponse) {
            let creds = forceRefreshFirst ? try await forceRefresh() : try await refreshTokenIfNeeded()
            var request = URLRequest(url: url)
            request.httpMethod = method
            request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Accept")
            request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
            for (name, value) in headers { request.setValue(value, forHTTPHeaderField: name) }
            if let encodedBody {
                request.setValue("application/json", forHTTPHeaderField: "Content-Type")
                request.httpBody = encodedBody
            }
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
            return (data, http)
        }

        var delay: UInt64 = 1_000_000_000
        var refreshed = false
        for tries in 1... {
            var (data, http) = try await attempt(forceRefreshFirst: false)
            if http.statusCode == 401, credentials.kind == .oauth, !refreshed {
                refreshed = true
                drimeLog.info("[Drime] \(method) \(path) → 401, refreshing and retrying once")
                (data, http) = try await attempt(forceRefreshFirst: true)
            }
            if (200...299).contains(http.statusCode) {
                if T.self == DrimeStatus.self, data.isEmpty { return DrimeStatus(status: nil) as! T }
                return try JSONDecoder().decode(T.self, from: data)
            }
            let retryable = http.statusCode == 429 || (method == "GET" && [500, 502, 503, 504, 520, 521, 522, 523, 524].contains(http.statusCode))
            if retryable, tries < 4 {
                try await Task.sleep(nanoseconds: delay)
                delay *= 2
                continue
            }
            drimeLog.error("[Drime] \(method) \(path) → HTTP \(http.statusCode): \(String(decoding: data.prefix(300), as: UTF8.self))")
            if surfaceServerMessage, http.statusCode != 401, let message = Self.serverMessage(in: data) {
                throw CloudProviderError.commandFailed(message)
            }
            throw Self.mapHTTPError(statusCode: http.statusCode, body: data)
        }
        throw CloudProviderError.invalidResponse
    }

    public static func mapHTTPError(statusCode: Int, body: Data?) -> CloudProviderError {
        let message = serverMessage(in: body)
        switch statusCode {
        case 401: return .notAuthenticated
        case 403:
            // A token whose permissions don't cover the call says which
            // scope is missing — worth showing as is.
            if let message, message.contains("scope") || message.contains("restricted") {
                return .commandFailed(message)
            }
            return .unauthorized
        case 404: return .notFound(message ?? "Resource not found")
        case 413: return .fileTooLarge(fileBytes: 0, providerLimitBytes: nil)
        case 422: return message.map { .commandFailed($0) } ?? .serverError(422)
        case 429: return .rateLimited
        case 507: return .quotaExceeded
        default: return .serverError(statusCode)
        }
    }

    /// `{"message": …}`, plus the first field error of a 422 when there is
    /// one — Laravel's top-level message for those is just "The given data
    /// was invalid."
    public static func serverMessage(in data: Data?) -> String? {
        guard let data,
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        if let errors = object["errors"] as? [String: Any] {
            for value in errors.values {
                if let list = value as? [String], let first = list.first, !first.isEmpty { return first }
                if let text = value as? String, !text.isEmpty { return text }
            }
        }
        if let message = object["message"] as? String, !message.isEmpty { return message }
        return nil
    }

    // MARK: - Helpers

    private static func formEncode(_ fields: [String: String]) -> Data {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        let pairs = fields.map { key, value in
            "\(key)=\(value.addingPercentEncoding(withAllowedCharacters: allowed) ?? value)"
        }
        return Data(pairs.joined(separator: "&").utf8)
    }

    private static func randomURLSafeString(byteCount: Int) -> String {
        var bytes = [UInt8](repeating: 0, count: byteCount)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return base64URL(Data(bytes))
    }

    static func base64URL(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    static func mimeType(for name: String) -> String {
        let ext = (name as NSString).pathExtension
        return UTType(filenameExtension: ext)?.preferredMIMEType ?? "application/octet-stream"
    }

    private static func nonEmpty(_ value: String?) -> String? {
        guard let value, !value.isEmpty else { return nil }
        return value
    }

    /// Drime writes timestamps as `2024-01-20T15:30:00.000000Z`.
    public static func formatTimestamp(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyy-MM-dd'T'HH:mm:ss.SSSSSS'Z'"
        return formatter.string(from: date)
    }

    static func parseTimestamp(_ raw: String?) -> Date? {
        guard let raw, !raw.isEmpty else { return nil }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: raw) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: raw)
    }
}

/// Forwards byte progress for one part without counting resent bytes twice.
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

// MARK: - Wire types

private struct DrimeTokenResponse: Decodable {
    let access_token: String
    let refresh_token: String?
    let expires_in: Int?
    let scope: String?
}

private struct DrimeOAuthError: Decodable {
    let error: String?
    let error_description: String?
}

private struct DrimeUserEnvelope: Decodable {
    let user: DrimeUser
}

private struct DrimeUser: Decodable {
    let email: String?
    let display_name: String?
}

struct DrimeStatus: Decodable {
    let status: String?
}

struct DrimeFileEntryEnvelope: Decodable {
    let fileEntry: DrimeEntry
}

struct DrimeListing: Decodable {
    let data: [DrimeEntry]
    let current_page: Int?
    let last_page: Int?
}

/// A Drime file entry, decoded leniently: ids and sizes arrive as numbers
/// or numeric strings depending on the route, and most fields are nullable.
struct DrimeEntry: Decodable, Sendable {
    let id: Int64
    let name: String
    let type: String
    let fileSize: Int64
    let updatedAt: Date?
    /// Original mtime on the uploading machine, Unix milliseconds.
    let clientLastModified: Date?
    /// SHA-256 of the content, when Drime has it.
    let fileHash: String?

    var isFolder: Bool { type == "folder" }

    static let root = DrimeEntry(id: 0, name: "", type: "folder", fileSize: 0, updatedAt: nil, clientLastModified: nil, fileHash: nil)

    init(id: Int64, name: String, type: String, fileSize: Int64, updatedAt: Date?, clientLastModified: Date?, fileHash: String?) {
        self.id = id
        self.name = name
        self.type = type
        self.fileSize = fileSize
        self.updatedAt = updatedAt
        self.clientLastModified = clientLastModified
        self.fileHash = fileHash
    }

    private enum CodingKeys: String, CodingKey {
        case id, name, type, file_size, updated_at, client_last_modified, file_hash
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        guard let id = Self.int64(c, .id) else {
            throw DecodingError.dataCorruptedError(forKey: .id, in: c, debugDescription: "Missing entry id")
        }
        self.id = id
        name = try c.decode(String.self, forKey: .name)
        type = (try? c.decode(String.self, forKey: .type)) ?? "file"
        fileSize = Self.int64(c, .file_size) ?? 0
        updatedAt = DrimeAPIClient.parseTimestamp(try? c.decode(String.self, forKey: .updated_at))
        clientLastModified = Self.int64(c, .client_last_modified)
            .flatMap { $0 > 0 ? Date(timeIntervalSince1970: TimeInterval($0) / 1000) : nil }
        fileHash = (try? c.decode(String.self, forKey: .file_hash)).flatMap { $0.isEmpty ? nil : $0 }
    }

    private static func int64(_ c: KeyedDecodingContainer<CodingKeys>, _ key: CodingKeys) -> Int64? {
        if let value = try? c.decode(Int64.self, forKey: key) { return value }
        if let value = try? c.decode(Double.self, forKey: key) { return Int64(value) }
        if let text = try? c.decode(String.self, forKey: key) { return Int64(text) }
        return nil
    }

    func cloudFileItem(parentPath: String) -> CloudFileItem {
        let path = DrimeAPIClient.join(parentPath, name)
        return CloudFileItem(
            id: path,
            name: name,
            path: path,
            isDirectory: isFolder,
            size: isFolder ? 0 : fileSize,
            modificationDate: clientLastModified ?? updatedAt ?? .distantPast,
            checksum: fileHash
        )
    }
}
