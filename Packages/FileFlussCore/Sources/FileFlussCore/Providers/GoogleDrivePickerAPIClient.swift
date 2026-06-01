import CryptoKit
import Foundation
import os

private let pickerLog = Logger(subsystem: "com.rana.FileFluss", category: "googleDrivePicker")

// MARK: - Persisted credentials

/// One folder (or file) the user granted FileFluss access to through the
/// Google Picker. With the `drive.file` scope these are the only items the app
/// can see — a picked folder grants recursive access to its contents.
public struct GoogleDrivePickedRoot: Codable, Sendable, Hashable {
    public let id: String
    public var name: String
    public init(id: String, name: String) {
        self.id = id
        self.name = name
    }
}

/// Persisted credentials for a "Google Drive (Selected Folders)" account. This
/// is Project B — a *separate* Google Cloud project using the non-sensitive
/// `drive.file` scope (no security audit, no 100-user cap), kept entirely apart
/// from the full-`drive` Project A implementation that existing users rely on.
public struct GoogleDrivePickerCredentials: Codable, Sendable {
    public var accessToken: String
    public var refreshToken: String
    public var expiresAt: Date
    public var userEmail: String
    public var displayName: String
    /// Folders/files the user picked. These are the account's browsable roots.
    public var roots: [GoogleDrivePickedRoot]

    public init(accessToken: String, refreshToken: String, expiresAt: Date, userEmail: String, displayName: String, roots: [GoogleDrivePickedRoot]) {
        self.accessToken = accessToken
        self.refreshToken = refreshToken
        self.expiresAt = expiresAt
        self.userEmail = userEmail
        self.displayName = displayName
        self.roots = roots
    }
}

// MARK: - API client

/// Google Drive client scoped to `drive.file` and driven by the Picker. It is
/// deliberately **cache-free**: every listing is fetched live and keyed off
/// Drive file IDs, so a change made to a picked folder outside FileFluss (web
/// UI, another device) is always reflected on the next refresh. This is the
/// fix for the stale-listing bugs the earlier Picker attempt had.
public actor GoogleDrivePickerAPIClient {
    // Project B — distinct from GoogleDriveAPIClient's full-`drive` client.
    static let clientId = "368567371288-hvv93hksp81tblrrk4ra9vi5svi71dv2.apps.googleusercontent.com"
    static let clientSecret: String? = "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"
    /// API key the Google Picker needs (`setDeveloperKey`). Public by design.
    public static let pickerAPIKey = "AIzaSyDu59GsA1aYauH7lziUSoWkmWj-COWzjis"
    /// App ID = the Cloud project number (the numeric prefix of the client ID).
    /// The Picker MUST be given this (`setAppId`) so the `drive.file` access it
    /// grants is associated with this app's OAuth token — without it, listing
    /// the picked folders comes back empty.
    public static let pickerAppId = "368567371288"
    static let scopes = "https://www.googleapis.com/auth/drive.file https://www.googleapis.com/auth/userinfo.profile https://www.googleapis.com/auth/userinfo.email openid"
    static let oauthCallbackScheme = "filefluss-oauth"

    private(set) var credentials: GoogleDrivePickerCredentials
    private let session: URLSession
    private let apiURL = "https://www.googleapis.com/drive/v3"
    private let uploadURL = "https://www.googleapis.com/upload/drive/v3"

    private var inflightRefresh: Task<GoogleDrivePickerCredentials, Error>?

    /// Workspace-native files have no byte stream; export them to a common
    /// format on download.
    private static let exportMimeTypes: [String: String] = [
        "application/vnd.google-apps.document": "application/pdf",
        "application/vnd.google-apps.spreadsheet": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.google-apps.presentation": "application/pdf",
        "application/vnd.google-apps.drawing": "application/pdf",
    ]
    private static let exportExtensions: [String: String] = [
        "application/vnd.google-apps.document": "pdf",
        "application/vnd.google-apps.spreadsheet": "xlsx",
        "application/vnd.google-apps.presentation": "pdf",
        "application/vnd.google-apps.drawing": "pdf",
    ]

    public init(credentials: GoogleDrivePickerCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 3600
        self.session = URLSession(configuration: config)
    }

    // MARK: - OAuth

    /// Run the loopback OAuth flow and return credentials with an *empty* root
    /// list — the caller then shows the Picker and fills `roots` in.
    public static func startOAuthFlow() async throws -> GoogleDrivePickerCredentials {
        let codeVerifier = Self.randomURLSafe(64)
        let codeChallenge = Self.codeChallenge(for: codeVerifier)
        let expectedState = Self.randomURLSafe(32)

        let result = try await OAuthSession.authenticate(
            callbackURLScheme: oauthCallbackScheme,
            callbackPath: "/oauth2redirect"
        ) { redirectURI in
            var components = URLComponents(string: "https://accounts.google.com/o/oauth2/v2/auth")!
            components.queryItems = [
                URLQueryItem(name: "client_id", value: clientId),
                URLQueryItem(name: "redirect_uri", value: redirectURI),
                URLQueryItem(name: "response_type", value: "code"),
                URLQueryItem(name: "scope", value: scopes),
                URLQueryItem(name: "code_challenge", value: codeChallenge),
                URLQueryItem(name: "code_challenge_method", value: "S256"),
                URLQueryItem(name: "access_type", value: "offline"),
                URLQueryItem(name: "prompt", value: "consent"),
                URLQueryItem(name: "state", value: expectedState),
            ]
            return components.url!
        }

        let params = URLComponents(url: result.callbackURL, resolvingAgainstBaseURL: false)?.queryItems
        guard params?.first(where: { $0.name == "state" })?.value == expectedState else {
            throw CloudProviderError.unauthorized
        }
        if params?.first(where: { $0.name == "error" })?.value != nil {
            throw CloudProviderError.unauthorized
        }
        guard let code = params?.first(where: { $0.name == "code" })?.value else {
            throw CloudProviderError.invalidResponse
        }
        return try await exchangeCode(code, codeVerifier: codeVerifier, redirectURI: result.redirectURI)
    }

    private static func exchangeCode(_ code: String, codeVerifier: String, redirectURI: String) async throws -> GoogleDrivePickerCredentials {
        let encode = { (s: String) in s.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? s }
        var parts = [
            "code=\(encode(code))",
            "client_id=\(encode(clientId))",
            "redirect_uri=\(encode(redirectURI))",
            "grant_type=authorization_code",
            "code_verifier=\(encode(codeVerifier))",
        ]
        if let secret = clientSecret { parts.append("client_secret=\(encode(secret))") }

        var request = URLRequest(url: URL(string: "https://oauth2.googleapis.com/token")!)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.httpBody = parts.joined(separator: "&").data(using: .utf8)

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let body = String(data: data, encoding: .utf8) ?? ""
            pickerLog.error("[GDrivePicker] Token exchange failed: \(body.prefix(300))")
            throw CloudProviderError.invalidCredentials
        }
        let token = try JSONDecoder().decode(PickerTokenResponse.self, from: data)
        let info = try await fetchUserInfo(accessToken: token.access_token)
        return GoogleDrivePickerCredentials(
            accessToken: token.access_token,
            refreshToken: token.refresh_token ?? "",
            expiresAt: Date().addingTimeInterval(TimeInterval(token.expires_in)),
            userEmail: info.email,
            displayName: info.name ?? info.email,
            roots: []
        )
    }

    private static func fetchUserInfo(accessToken: String) async throws -> PickerUserInfo {
        var request = URLRequest(url: URL(string: "https://www.googleapis.com/oauth2/v2/userinfo")!)
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            return PickerUserInfo(email: "Unknown", name: nil)
        }
        return try JSONDecoder().decode(PickerUserInfo.self, from: data)
    }

    // MARK: - Token refresh (serialized)

    private func refreshTokenIfNeeded() async throws -> GoogleDrivePickerCredentials {
        if let inflight = inflightRefresh { return try await inflight.value }
        guard Date() >= credentials.expiresAt.addingTimeInterval(-60) else { return credentials }
        return try await startRefresh()
    }

    private func startRefresh() async throws -> GoogleDrivePickerCredentials {
        guard !credentials.refreshToken.isEmpty else { throw CloudProviderError.notAuthenticated }
        let task = Task<GoogleDrivePickerCredentials, Error> { [self] in try await performRefresh() }
        inflightRefresh = task
        defer { inflightRefresh = nil }
        return try await task.value
    }

    private func performRefresh() async throws -> GoogleDrivePickerCredentials {
        var parts = [
            "client_id=\(Self.clientId)",
            "refresh_token=\(credentials.refreshToken)",
            "grant_type=refresh_token",
        ]
        if let secret = Self.clientSecret { parts.append("client_secret=\(secret)") }

        var request = URLRequest(url: URL(string: "https://oauth2.googleapis.com/token")!)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.httpBody = parts.joined(separator: "&").data(using: .utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.notAuthenticated
        }
        let token = try JSONDecoder().decode(PickerTokenResponse.self, from: data)
        credentials.accessToken = token.access_token
        credentials.expiresAt = Date().addingTimeInterval(TimeInterval(token.expires_in))
        if let r = token.refresh_token, !r.isEmpty { credentials.refreshToken = r }
        return credentials
    }

    /// Current access token, refreshed if near expiry. Exposed so the Picker
    /// UI can authorize against the same token.
    public func validAccessToken() async throws -> String {
        try await refreshTokenIfNeeded().accessToken
    }

    public func userDisplayName() async throws -> String { credentials.displayName }

    // MARK: - Roots (picked folders)

    public func roots() -> [GoogleDrivePickedRoot] { credentials.roots }

    /// Merge newly picked roots into the account (dedupe by id), updating names.
    public func addRoots(_ newRoots: [GoogleDrivePickedRoot]) {
        var byId = Dictionary(uniqueKeysWithValues: credentials.roots.map { ($0.id, $0) })
        for r in newRoots { byId[r.id] = r }
        credentials.roots = Array(byId.values).sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    public func removeRoot(id: String) {
        credentials.roots.removeAll { $0.id == id }
    }

    // MARK: - Quota

    public func storageQuota() async throws -> CloudStorageQuota? {
        struct About: Decodable { let storageQuota: Quota; struct Quota: Decodable { let limit: String?; let usage: String? } }
        let creds = try await refreshTokenIfNeeded()
        var request = URLRequest(url: URL(string: "\(apiURL)/about?fields=storageQuota")!)
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else { return nil }
        let about = try JSONDecoder().decode(About.self, from: data)
        return CloudStorageQuota(usedBytes: Int64(about.storageQuota.usage ?? "0") ?? 0,
                                 totalBytes: about.storageQuota.limit.flatMap(Int64.init))
    }

    // MARK: - Listing

    public func listDirectory(at path: String) async throws -> [CloudFileItem] {
        // Root: the picked folders. Build the entries straight from the stored
        // roots so the displayed name is the SAME string `resolve(_:)` matches
        // a path against — using a live-fetched name here while resolving by
        // the stored name would break navigation (wrong folder / not found).
        // Folder *contents* are still always live (listChildren below).
        if path == "/" || path.isEmpty {
            var items: [CloudFileItem] = []
            for root in credentials.roots {
                // Use the stored name (so it matches what `resolve(_:)` looks
                // up), but pull the real modified date from the live metadata.
                let modDate = (try? await fetchFile(id: root.id))?
                    .toCloudFileItem(parentPath: "/").modificationDate ?? Date.distantPast
                items.append(CloudFileItem(
                    id: "d:\(root.id)",
                    name: root.name,
                    path: "/\(root.name)",
                    isDirectory: true,
                    size: 0,
                    modificationDate: modDate,
                    checksum: nil
                ))
            }
            return items.sorted { $0.name.lowercased() < $1.name.lowercased() }
        }

        let resolved = try await resolve(path)
        return try await listChildren(of: resolved.id, parentPath: path)
    }

    private func listChildren(of folderId: String, parentPath: String) async throws -> [CloudFileItem] {
        var out: [CloudFileItem] = []
        var pageToken: String?
        do {
            repeat {
                var q = [
                    URLQueryItem(name: "q", value: "'\(folderId)' in parents and trashed = false"),
                    URLQueryItem(name: "fields", value: "nextPageToken,files(id,name,mimeType,size,modifiedTime,md5Checksum)"),
                    URLQueryItem(name: "pageSize", value: "1000"),
                    URLQueryItem(name: "orderBy", value: "folder,name"),
                    URLQueryItem(name: "supportsAllDrives", value: "true"),
                    URLQueryItem(name: "includeItemsFromAllDrives", value: "true"),
                ]
                if let pageToken { q.append(URLQueryItem(name: "pageToken", value: pageToken)) }
                let resp: GoogleFileListResponse = try await apiRequest(.get, path: "/files", queryItems: q)
                out.append(contentsOf: resp.files.map { $0.toCloudFileItem(parentPath: parentPath) })
                pageToken = resp.nextPageToken
            } while pageToken != nil
            // Empty is normal in this access model (folders only expose what
            // FileFluss added or the user picked), so this is not an error.
            SupportLogger.shared.log(
                "listChildren \(parentPath) [parentId=\(folderId)] account=\(credentials.userEmail) → \(out.count) item(s)",
                category: "googleDrivePicker", level: .info)
        } catch {
            SupportLogger.shared.log(
                "listChildren \(parentPath) [parentId=\(folderId)] account=\(credentials.userEmail) FAILED: \(error)",
                category: "googleDrivePicker", level: .error)
            throw error
        }
        return out
    }

    // MARK: - Path resolution (live, no cache)

    private struct Resolved { let id: String; let mimeType: String; var isDirectory: Bool { mimeType == "application/vnd.google-apps.folder" } }

    /// Resolve a FileFluss path ("/RootName/sub/leaf") to a Drive file ID by
    /// walking from the matching picked root and listing children at each step.
    /// Performed fresh every call — nothing is cached, so external changes are
    /// always seen.
    private func resolve(_ path: String) async throws -> Resolved {
        var comps = path.split(separator: "/").map(String.init)
        guard !comps.isEmpty else { throw CloudProviderError.notFound(path) }
        let rootName = comps.removeFirst()
        guard let root = credentials.roots.first(where: { $0.name == rootName }) else {
            throw CloudProviderError.notFound(path)
        }
        var currentId = root.id
        var currentMime = "application/vnd.google-apps.folder"
        var walked = "/\(rootName)"
        for comp in comps {
            let children = try await listChildren(of: currentId, parentPath: walked)
            // Prefer a folder match for intermediate components; for the final
            // component fall back to any name match.
            guard let match = children.first(where: { $0.name == comp && $0.isDirectory })
                    ?? children.first(where: { $0.name == comp }) else {
                throw CloudProviderError.notFound(path)
            }
            // Strip the synthetic d/f id prefix added by toCloudFileItem.
            currentId = String(match.id.dropFirst())
            currentMime = match.isDirectory ? "application/vnd.google-apps.folder" : "application/octet-stream"
            walked = match.path
        }
        return Resolved(id: currentId, mimeType: currentMime)
    }

    private func resolveParentId(of path: String) async throws -> String {
        let parent = (path as NSString).deletingLastPathComponent
        if parent == "/" || parent.isEmpty {
            // Parent is a picked root itself.
            let name = (path as NSString).lastPathComponent
            _ = name
            throw CloudProviderError.notImplemented // handled by callers via resolve of parent path
        }
        return try await resolve(parent).id
    }

    /// Returns the Drive parent ID for a remote path, treating a top-level
    /// component as "inside the matching picked root".
    private func parentFolderId(for remotePath: String) async throws -> String {
        let parent = (remotePath as NSString).deletingLastPathComponent
        if parent == "/" || parent.isEmpty {
            throw CloudProviderError.notImplemented
        }
        // parent could be a root name only (e.g. "/RootName") or deeper.
        return try await resolve(parent).id
    }

    private func fetchFile(id: String) async throws -> GoogleDriveFile? {
        do {
            let f: GoogleDriveFile = try await apiRequest(.get, path: "/files/\(id)", queryItems: [
                URLQueryItem(name: "fields", value: "id,name,mimeType,size,modifiedTime,md5Checksum"),
                URLQueryItem(name: "supportsAllDrives", value: "true"),
            ])
            return f
        } catch CloudProviderError.notFound {
            return nil
        }
    }

    // MARK: - Mutations

    public func createDirectory(at path: String) async throws {
        let parentId = try await parentFolderId(for: path)
        let name = (path as NSString).lastPathComponent
        struct Body: Encodable { let name: String; let mimeType: String; let parents: [String] }
        let _: GoogleDriveFile = try await apiRequest(.post, path: "/files", body: Body(
            name: name, mimeType: "application/vnd.google-apps.folder", parents: [parentId]))
    }

    public func deleteItem(at path: String) async throws {
        let resolved = try await resolve(path)
        try await apiRequestVoid(.delete, path: "/files/\(resolved.id)")
    }

    public func renameItem(at path: String, to newName: String) async throws {
        let resolved = try await resolve(path)
        struct Body: Encodable { let name: String }
        let _: GoogleDriveFile = try await apiRequest(.patch, path: "/files/\(resolved.id)", body: Body(name: newName))
    }

    public func moveItem(at path: String, toPath newPath: String) async throws {
        let resolved = try await resolve(path)
        let newParentId = try await parentFolderId(for: newPath)
        let oldParentId = try await parentFolderId(for: path)
        let newName = (newPath as NSString).lastPathComponent
        var components = URLComponents(string: "\(apiURL)/files/\(resolved.id)")!
        components.queryItems = [
            URLQueryItem(name: "addParents", value: newParentId),
            URLQueryItem(name: "removeParents", value: oldParentId),
            URLQueryItem(name: "supportsAllDrives", value: "true"),
            URLQueryItem(name: "fields", value: "id"),
        ]
        struct Body: Encodable { let name: String }
        let creds = try await refreshTokenIfNeeded()
        var request = URLRequest(url: components.url!)
        request.httpMethod = "PATCH"
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(Body(name: newName))
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw Self.mapHTTPError(statusCode: (response as? HTTPURLResponse)?.statusCode ?? 0, path: path)
        }
        _ = data
    }

    public func copyItem(at path: String, toPath newPath: String) async throws {
        let resolved = try await resolve(path)
        let newParentId = try await parentFolderId(for: newPath)
        let newName = (newPath as NSString).lastPathComponent
        struct Body: Encodable { let name: String; let parents: [String] }
        let _: GoogleDriveFile = try await apiRequest(.post, path: "/files/\(resolved.id)/copy", body: Body(name: newName, parents: [newParentId]))
    }

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        let resolved = try await resolve(path)
        guard let file = try await fetchFile(id: resolved.id) else { throw CloudProviderError.notFound(path) }
        return file.toCloudFileItem(parentPath: (path as NSString).deletingLastPathComponent)
    }

    public func folderSize(at path: String) async throws -> Int64 {
        let items = try await listDirectory(at: path)
        var total: Int64 = 0
        for item in items {
            if item.isDirectory { total += try await folderSize(at: item.path) } else { total += item.size }
        }
        return total
    }

    // MARK: - Download / upload

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let resolved = try await resolve(remotePath)
        let creds = try await refreshTokenIfNeeded()
        let url: URL
        var dest = localURL
        if let exportMime = Self.exportMimeTypes[resolved.mimeType] {
            let enc = exportMime.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? exportMime
            url = URL(string: "\(apiURL)/files/\(resolved.id)/export?mimeType=\(enc)")!
            if let ext = Self.exportExtensions[resolved.mimeType] { dest = localURL.appendingPathExtension(ext) }
        } else {
            url = URL(string: "\(apiURL)/files/\(resolved.id)?alt=media&supportsAllDrives=true")!
        }
        var request = URLRequest(url: url)
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        let (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw Self.mapHTTPError(statusCode: (response as? HTTPURLResponse)?.statusCode ?? 0, path: remotePath)
        }
        try? FileManager.default.removeItem(at: dest)
        try FileManager.default.moveItem(at: tempURL, to: dest)
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let parentId = try await parentFolderId(for: remotePath)
        let fileName = (remotePath as NSString).lastPathComponent
        let existingId = try? await findChildId(parentId: parentId, name: fileName)

        let attrs = try? FileManager.default.attributesOfItem(atPath: localURL.path)
        let modDate = attrs?[.modificationDate] as? Date
        let createdDate = attrs?[.creationDate] as? Date
        let fileData = try Data(contentsOf: localURL, options: .mappedIfSafe)

        let uploaded: GoogleDriveFile
        if fileData.count <= 5_000_000 {
            uploaded = try await simpleUpload(data: fileData, fileName: fileName, parentId: parentId, existingId: existingId, modDate: modDate, createdDate: createdDate, onBytes: onBytes)
        } else {
            uploaded = try await resumableUpload(from: localURL, fileSize: fileData.count, fileName: fileName, parentId: parentId, existingId: existingId, modDate: modDate, createdDate: createdDate, onBytes: onBytes)
        }
        // Always stamp the source's modified/created time so the file lists with
        // its real date (not the upload time / epoch). The upload endpoint
        // rejects timestamps on a replace PATCH and the resumable init doesn't
        // always honour them, so do it unconditionally via a metadata PATCH.
        if modDate != nil || createdDate != nil {
            try? await patchTimestamps(fileId: uploaded.id, modDate: modDate, createdDate: createdDate)
        }
    }

    public func setModificationDate(at remotePath: String, to date: Date) async throws {
        let resolved = try await resolve(remotePath)
        try await patchTimestamps(fileId: resolved.id, modDate: date, createdDate: nil)
    }

    private func findChildId(parentId: String, name: String) async throws -> String? {
        let escaped = name.replacingOccurrences(of: "'", with: "\\'")
        let q = [
            URLQueryItem(name: "q", value: "'\(parentId)' in parents and name = '\(escaped)' and trashed = false"),
            URLQueryItem(name: "fields", value: "files(id)"),
            URLQueryItem(name: "pageSize", value: "1"),
            URLQueryItem(name: "supportsAllDrives", value: "true"),
        ]
        let resp: GoogleFileListResponse = try await apiRequest(.get, path: "/files", queryItems: q)
        return resp.files.first?.id
    }

    private static func buildMetadata(name: String?, parentId: String?, modDate: Date?, createdDate: Date?) -> String {
        var obj: [String: Any] = [:]
        if let name { obj["name"] = name }
        if let parentId { obj["parents"] = [parentId] }
        let f = ISO8601DateFormatter(); f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]; f.timeZone = TimeZone(secondsFromGMT: 0)
        if let modDate { obj["modifiedTime"] = f.string(from: modDate) }
        if let createdDate { obj["createdTime"] = f.string(from: createdDate) }
        if let data = try? JSONSerialization.data(withJSONObject: obj), let s = String(data: data, encoding: .utf8) { return s }
        return "{}"
    }

    private func simpleUpload(data: Data, fileName: String, parentId: String, existingId: String?, modDate: Date?, createdDate: Date?, onBytes: ByteProgressHandler?) async throws -> GoogleDriveFile {
        let creds = try await refreshTokenIfNeeded()
        let boundary = UUID().uuidString
        let urlString: String
        let method: String
        let metadata: String
        if let existingId {
            urlString = "\(uploadURL)/files/\(existingId)?uploadType=multipart&fields=id,name,mimeType&supportsAllDrives=true"
            method = "PATCH"
            metadata = Self.buildMetadata(name: nil, parentId: nil, modDate: nil, createdDate: nil)
        } else {
            urlString = "\(uploadURL)/files?uploadType=multipart&fields=id,name,mimeType&supportsAllDrives=true"
            method = "POST"
            metadata = Self.buildMetadata(name: fileName, parentId: parentId, modDate: modDate, createdDate: createdDate)
        }
        var request = URLRequest(url: URL(string: urlString)!)
        request.httpMethod = method
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("multipart/related; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
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
            throw Self.mapHTTPError(statusCode: (response as? HTTPURLResponse)?.statusCode ?? 0, path: fileName)
        }
        return try JSONDecoder().decode(GoogleDriveFile.self, from: responseData)
    }

    private func resumableUpload(from localURL: URL, fileSize: Int, fileName: String, parentId: String, existingId: String?, modDate: Date?, createdDate: Date?, onBytes: ByteProgressHandler?) async throws -> GoogleDriveFile {
        let creds = try await refreshTokenIfNeeded()
        let initURLString: String
        let initMethod: String
        let metadata: String
        if let existingId {
            initURLString = "\(uploadURL)/files/\(existingId)?uploadType=resumable&fields=id,name,mimeType&supportsAllDrives=true"
            initMethod = "PATCH"
            metadata = Self.buildMetadata(name: nil, parentId: nil, modDate: nil, createdDate: nil)
        } else {
            initURLString = "\(uploadURL)/files?uploadType=resumable&fields=id,name,mimeType&supportsAllDrives=true"
            initMethod = "POST"
            metadata = Self.buildMetadata(name: fileName, parentId: parentId, modDate: modDate, createdDate: createdDate)
        }
        var initRequest = URLRequest(url: URL(string: initURLString)!)
        initRequest.httpMethod = initMethod
        initRequest.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        initRequest.setValue("application/json; charset=UTF-8", forHTTPHeaderField: "Content-Type")
        initRequest.setValue("application/octet-stream", forHTTPHeaderField: "X-Upload-Content-Type")
        initRequest.setValue("\(fileSize)", forHTTPHeaderField: "X-Upload-Content-Length")
        initRequest.httpBody = metadata.data(using: .utf8)
        let (_, initResponse) = try await session.data(for: initRequest)
        guard let initHTTP = initResponse as? HTTPURLResponse, (200...299).contains(initHTTP.statusCode),
              let uploadURLString = initHTTP.value(forHTTPHeaderField: "Location"), let uploadURL = URL(string: uploadURLString) else {
            throw Self.mapHTTPError(statusCode: (initResponse as? HTTPURLResponse)?.statusCode ?? 0, path: fileName)
        }
        let chunkSize = 10 * 1024 * 1024
        let fileData = try Data(contentsOf: localURL, options: .mappedIfSafe)
        var offset = 0
        var finalData: Data?
        while offset < fileSize {
            let end = min(offset + chunkSize, fileSize)
            let chunk = fileData[offset..<end]
            var chunkRequest = URLRequest(url: uploadURL)
            chunkRequest.httpMethod = "PUT"
            chunkRequest.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
            chunkRequest.setValue("bytes \(offset)-\(end - 1)/\(fileSize)", forHTTPHeaderField: "Content-Range")
            let (chunkData, chunkResponse) = try await session.uploadReportingProgress(for: chunkRequest, body: Data(chunk), onBytes: onBytes)
            guard let chunkHTTP = chunkResponse as? HTTPURLResponse,
                  (200...299).contains(chunkHTTP.statusCode) || chunkHTTP.statusCode == 308 else {
                throw Self.mapHTTPError(statusCode: (chunkResponse as? HTTPURLResponse)?.statusCode ?? 0, path: fileName)
            }
            if (200...299).contains(chunkHTTP.statusCode) { finalData = chunkData }
            offset = end
        }
        guard let finalData else { throw CloudProviderError.invalidResponse }
        return try JSONDecoder().decode(GoogleDriveFile.self, from: finalData)
    }

    private func patchTimestamps(fileId: String, modDate: Date?, createdDate: Date?) async throws {
        let creds = try await refreshTokenIfNeeded()
        guard let url = URL(string: "\(apiURL)/files/\(fileId)?supportsAllDrives=true") else { return }
        var request = URLRequest(url: url)
        request.httpMethod = "PATCH"
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let f = ISO8601DateFormatter(); f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]; f.timeZone = TimeZone(secondsFromGMT: 0)
        var obj: [String: Any] = [:]
        if let modDate { obj["modifiedTime"] = f.string(from: modDate) }
        if let createdDate { obj["createdTime"] = f.string(from: createdDate) }
        request.httpBody = try JSONSerialization.data(withJSONObject: obj)
        _ = try? await session.data(for: request)
    }

    // MARK: - Request plumbing

    private enum HTTPMethod: String { case get = "GET", post = "POST", patch = "PATCH", delete = "DELETE" }

    private func apiRequest<T: Decodable>(_ method: HTTPMethod, path: String, queryItems: [URLQueryItem] = [], body: (any Encodable)? = nil) async throws -> T {
        let creds = try await refreshTokenIfNeeded()
        var components = URLComponents(string: "\(apiURL)\(path)")!
        if !queryItems.isEmpty { components.queryItems = queryItems }
        var request = URLRequest(url: components.url!)
        request.httpMethod = method.rawValue
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        if let body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONEncoder().encode(AnyEncodable(body))
        }
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw Self.mapHTTPError(statusCode: (response as? HTTPURLResponse)?.statusCode ?? 0, path: path)
        }
        return try JSONDecoder().decode(T.self, from: data)
    }

    private func apiRequestVoid(_ method: HTTPMethod, path: String) async throws {
        let creds = try await refreshTokenIfNeeded()
        var components = URLComponents(string: "\(apiURL)\(path)")!
        components.queryItems = [URLQueryItem(name: "supportsAllDrives", value: "true")]
        var request = URLRequest(url: components.url!)
        request.httpMethod = method.rawValue
        request.setValue("Bearer \(creds.accessToken)", forHTTPHeaderField: "Authorization")
        let (_, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw Self.mapHTTPError(statusCode: (response as? HTTPURLResponse)?.statusCode ?? 0, path: path)
        }
    }

    private static func mapHTTPError(statusCode: Int, path: String) -> CloudProviderError {
        switch statusCode {
        case 401, 403: return .notAuthenticated
        case 404: return .notFound(path)
        case 429: return .rateLimited
        case 500...599: return .serverError(statusCode)
        default: return .serverError(statusCode)
        }
    }

    // MARK: - PKCE helpers

    private static func randomURLSafe(_ length: Int) -> String {
        var bytes = [UInt8](repeating: 0, count: length)
        _ = SecRandomCopyBytes(kSecRandomDefault, length, &bytes)
        return Data(bytes).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    private static func codeChallenge(for verifier: String) -> String {
        let digest = SHA256.hash(data: Data(verifier.utf8))
        return Data(digest).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

// MARK: - Private wire types

private struct PickerTokenResponse: Decodable {
    let access_token: String
    let refresh_token: String?
    let expires_in: Int
}

private struct PickerUserInfo: Decodable {
    let email: String
    let name: String?
}

/// Type-erased Encodable so `apiRequest` can take a heterogeneous body.
private struct AnyEncodable: Encodable {
    private let encodeFunc: (Encoder) throws -> Void
    init(_ wrapped: any Encodable) { self.encodeFunc = wrapped.encode }
    func encode(to encoder: Encoder) throws { try encodeFunc(encoder) }
}
