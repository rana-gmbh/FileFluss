import Foundation

public struct PCloudCredentials: Codable, Sendable {
    let accessToken: String
    let hostname: String
    let userId: UInt64
}

public actor PCloudAPIClient {
    let credentials: PCloudCredentials
    private let session: URLSession

    public init(credentials: PCloudCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)
    }

    private var baseURL: String { "https://\(credentials.hostname)" }

    // MARK: - Folder Operations

    public func listFolder(path: String) async throws -> [CloudFileItem] {
        let params = [
            "path": path,
            "timeformat": "timestamp",
        ]
        let response: PCloudListFolderResponse = try await request("listfolder", params: params)
        return response.metadata.contents?.map { $0.toCloudFileItem(parentPath: path) } ?? []
    }

    public func folderSize(path: String) async throws -> Int64 {
        let params = [
            "path": path,
            "recursive": "1",
            "timeformat": "timestamp",
        ]
        let response: PCloudListFolderResponse = try await request("listfolder", params: params)
        return sumSize(of: response.metadata)
    }

    private func sumSize(of folder: PCloudFolderMetadata) -> Int64 {
        var total: Int64 = 0
        for item in folder.contents ?? [] {
            if item.isfolder {
                if let subContents = item.folderContents {
                    total += sumSize(of: subContents)
                }
            } else {
                total += item.size ?? 0
            }
        }
        return total
    }

    public func createFolder(path: String) async throws {
        let params = ["path": path]
        let _: PCloudBasicResponse = try await request("createfolderifnotexists", params: params)
    }

    public func deleteFolder(path: String) async throws {
        let params = ["path": path]
        let _: PCloudBasicResponse = try await request("deletefolderrecursive", params: params)
    }

    public func renameFile(path: String, toName newName: String) async throws {
        let toPath = ((path as NSString).deletingLastPathComponent as NSString).appendingPathComponent(newName)
        try await renameFile(path: path, toPath: toPath)
    }

    public func renameFolder(path: String, toName newName: String) async throws {
        let toPath = ((path as NSString).deletingLastPathComponent as NSString).appendingPathComponent(newName)
        try await renameFolder(path: path, toPath: toPath)
    }

    /// pCloud's `renamefile` accepts an arbitrary destination path, so it
    /// doubles as a server-side cross-folder move. The same is true of
    /// `renamefolder` for directories.
    public func renameFile(path: String, toPath: String) async throws {
        let params = ["path": path, "topath": toPath]
        let _: PCloudBasicResponse = try await request("renamefile", params: params)
    }

    public func renameFolder(path: String, toPath: String) async throws {
        let params = ["path": path, "topath": toPath]
        let _: PCloudBasicResponse = try await request("renamefolder", params: params)
    }

    public func copyFile(path: String, toPath: String) async throws {
        let params = ["path": path, "topath": toPath]
        let _: PCloudBasicResponse = try await request("copyfile", params: params)
    }

    public func copyFolder(path: String, toPath: String) async throws {
        let params = ["path": path, "topath": toPath]
        let _: PCloudBasicResponse = try await request("copyfolder", params: params)
    }

    // MARK: - File Operations

    public func stat(path: String) async throws -> CloudFileItem {
        let params = [
            "path": path,
            "timeformat": "timestamp",
        ]
        let response: PCloudStatResponse = try await request("stat", params: params)
        let parentPath = (path as NSString).deletingLastPathComponent
        return response.metadata.toCloudFileItem(parentPath: parentPath)
    }

    public func deleteFile(path: String) async throws {
        // pCloud quirks this handles:
        //   * Error 2055 on the first deletes after a bulk upload — the
        //     file's metadata is briefly locked while pCloud processes the
        //     upload. Transient; retry with backoff.
        //   * notFound on attempt 0 when the path is actually a folder —
        //     must propagate so PCloudProvider.deleteItem can fall through
        //     to deleteFolder.
        //   * Shadow duplicates occasionally left after rapid upload+replace
        //     cycles — loop the path-based delete until notFound.
        var deletedOnce = false
        for attempt in 0..<6 {
            do {
                let _: PCloudBasicResponse = try await request("deletefile", params: ["path": path])
                deletedOnce = true
            } catch CloudProviderError.notFound {
                if !deletedOnce && attempt == 0 {
                    throw CloudProviderError.notFound("File not found: \(path)")
                }
                return
            } catch CloudProviderError.serverError(let code) where code == 2055 {
                // Metadata locked — back off and retry (250ms, 500ms, … up to ~3.75s).
                try? await Task.sleep(nanoseconds: UInt64(250_000_000) * UInt64(attempt + 1))
                continue
            } catch {
                if !deletedOnce { throw error }
                break
            }
        }
        do {
            // Same `timeformat` requirement as above: a decode failure here
            // would masquerade as "the file is still there".
            let _: PCloudStatResponse = try await request(
                "stat",
                params: ["path": path, "timeformat": "timestamp"]
            )
            throw CloudProviderError.serverError(0)
        } catch CloudProviderError.notFound {
            return
        }
    }

    public func getFileLink(path: String) async throws -> URL {
        let params = ["path": path]
        let response: PCloudFileLinkResponse = try await request("getfilelink", params: params)
        guard let host = response.hosts?.first, let filePath = response.path else {
            throw CloudProviderError.invalidResponse
        }
        guard let url = URL(string: "https://\(host)\(filePath)") else {
            throw CloudProviderError.invalidResponse
        }
        return url
    }

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let downloadURL = try await getFileLink(path: remotePath)
        let request = URLRequest(url: downloadURL)
        let (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        guard let httpResponse = response as? HTTPURLResponse,
              (200...299).contains(httpResponse.statusCode) else {
            throw CloudProviderError.invalidResponse
        }
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    public func uploadFile(from localURL: URL, toFolder folderPath: String, fileName: String) async throws {
        try await uploadFile(from: localURL, toFolder: folderPath, fileName: fileName, onBytes: nil)
    }

    public func uploadFile(from localURL: URL, toFolder folderPath: String, fileName: String, onBytes: ByteProgressHandler?) async throws {
        var urlString = "\(baseURL)/uploadfile?auth=\(credentials.accessToken)&path=\(folderPath.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? folderPath)&filename=\(fileName.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? fileName)&nopartial=1"

        if let modDate = (try? FileManager.default.attributesOfItem(atPath: localURL.path)[.modificationDate]) as? Date {
            urlString += "&mtime=\(Int64(modDate.timeIntervalSince1970))"
        }
        if let createdDate = (try? FileManager.default.attributesOfItem(atPath: localURL.path)[.creationDate]) as? Date {
            urlString += "&ctime=\(Int64(createdDate.timeIntervalSince1970))"
        }

        guard let url = URL(string: urlString) else {
            throw CloudProviderError.invalidResponse
        }

        let fileData = try Data(contentsOf: localURL)
        let boundary = UUID().uuidString
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

        var body = Data()
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"file\"; filename=\"\(fileName)\"\r\n".data(using: .utf8)!)
        body.append("Content-Type: application/octet-stream\r\n\r\n".data(using: .utf8)!)
        body.append(fileData)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)

        let (responseData, httpResponse) = try await session.uploadReportingProgress(for: request, body: body, onBytes: onBytes)
        guard let http = httpResponse as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.invalidResponse
        }

        let result = try JSONDecoder().decode(PCloudBasicResponse.self, from: responseData)
        if result.result != 0 {
            throw Self.mapError(code: result.result)
        }
    }

    /// Updates a file's mtime via pCloud's `setfilemtime` endpoint so
    /// cross-source copies preserve the original "Date Modified".
    public func setModificationDate(at remotePath: String, to date: Date) async throws {
        let encoded = remotePath.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? remotePath
        let params: [String: String] = [
            "path": encoded,
            "newtime": "\(Int64(date.timeIntervalSince1970))"
        ]
        let _: PCloudBasicResponse = try await request("setfilemtime", params: params)
    }

    // MARK: - User Info

    public func userInfo() async throws -> PCloudUserInfo {
        let response: PCloudUserInfoResponse = try await request("userinfo", params: [:])
        return PCloudUserInfo(
            email: response.email ?? "",
            userId: response.userid ?? 0,
            quota: response.quota ?? 0,
            usedQuota: response.usedquota ?? 0
        )
    }

    // MARK: - Share links

    /// `getfilepublink` reply. `code` is the short public-link code that
    /// `getpublinkdownload` needs to resolve the CDN URL.
    private struct PCloudPubLinkResponse: Decodable {
        let result: Int
        let error: String?
        let link: String?
        let linkid: UInt64?
        let code: String?
    }

    /// Creates a public download link for `path`.
    ///
    /// Password (`linkpassword`) and expiry (`expire`) are Premium-only
    /// features: a free account gets a non-zero `result` with pCloud's own
    /// wording, which we pass through verbatim rather than flattening to a
    /// code the user can't act on.
    public func createShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        // Every other call in this client addresses files by `path`, so use
        // that rather than `fileid` — no extra stat round-trip needed.
        var params: [String: String] = ["path": path]
        if let password = options.password, !password.isEmpty {
            params["linkpassword"] = password
        }
        if let expiry = options.expiry {
            params["expire"] = Self.expiryParameter(expiry)
        }

        let response: PCloudPubLinkResponse = try await requestSurfacingServerMessage("getfilepublink", params: params)
        guard let link = response.link, let shareURL = URL(string: link) else {
            throw CloudProviderError.invalidResponse
        }

        // Second hop resolves the landing page to a host we can stream the
        // bytes from. Best effort: a landing-page-only link is still useful,
        // so a failure here must not sink the whole operation.
        var direct: URL?
        if let code = response.code {
            direct = try? await publicLinkDownloadURL(code: code)
        }

        return CloudShareLink(
            url: shareURL,
            directDownloadURL: direct,
            // `getfilepublink` doesn't echo the effective expiry back, so
            // report what we asked for.
            expiresAt: options.expiry,
            hasPassword: !(options.password ?? "").isEmpty
        )
    }

    /// One entry of `listpublinks`.
    ///
    /// pCloud has moved these fields around between API revisions and doesn't
    /// return the same set for file and folder links, so every identifying
    /// value is optional and read from wherever it turns up — the link object
    /// itself or its `metadata`.
    private struct PCloudPublink: Decodable {
        let linkid: UInt64?
        let code: String?
        let link: String?
        let fileid: UInt64?
        let folderid: UInt64?
        let path: String?
        let haspassword: Bool?
        let expire: PCloudLinkDate?
        let expires: PCloudLinkDate?
        let metadata: Metadata?

        struct Metadata: Decodable {
            let fileid: UInt64?
            let folderid: UInt64?
            let path: String?
            let isfolder: Bool?
        }

        var resolvedFileId: UInt64? { fileid ?? metadata?.fileid }
        var resolvedPath: String? { path ?? metadata?.path }
        var expiryDate: Date? { (expire ?? expires)?.date }
    }

    /// pCloud reports dates as RFC 2822 text by default and as a Unix
    /// timestamp when the call passes `timeformat=timestamp`. Accept both, so
    /// an endpoint that ignores the parameter doesn't fail the whole decode.
    private enum PCloudLinkDate: Decodable {
        case timestamp(TimeInterval)
        case text(String)

        init(from decoder: Decoder) throws {
            let container = try decoder.singleValueContainer()
            if let seconds = try? container.decode(TimeInterval.self) {
                self = .timestamp(seconds)
                return
            }
            self = .text(try container.decode(String.self))
        }

        var date: Date? {
            switch self {
            case .timestamp(let seconds):
                // 0 is pCloud's "never expires".
                return seconds > 0 ? Date(timeIntervalSince1970: seconds) : nil
            case .text(let raw):
                guard !raw.isEmpty, raw != "0" else { return nil }
                let rfc2822 = DateFormatter()
                rfc2822.locale = Locale(identifier: "en_US_POSIX")
                rfc2822.dateFormat = "EEE, dd MMM yyyy HH:mm:ss Z"
                if let date = rfc2822.date(from: raw) { return date }
                return ISO8601DateFormatter().date(from: raw)
            }
        }
    }

    private struct PCloudPublinksResponse: Decodable {
        let result: Int
        let error: String?
        let publinks: [PCloudPublink]?
    }

    /// Returns the public link the file already has, or nil when it has none.
    ///
    /// pCloud has no "is this file shared?" call, so this lists the account's
    /// public links and looks for this file among them. An empty match is the
    /// "not shared" answer; a non-zero `result` (expired token, rate limit) is
    /// a real error and propagates with pCloud's own wording.
    public func existingShareLink(at path: String) async throws -> CloudShareLink? {
        guard let publink = try await findPublink(at: path) else { return nil }
        return try await shareLink(from: publink, requestedPassword: nil)
    }

    /// Rewrites an existing link's password and expiry via `changepublink`.
    /// The link (and its `code`) survives, so the URL the recipient has keeps
    /// working.
    ///
    /// `deletepassword` / `deleteexpire` are what pCloud wants for *clearing*
    /// a setting — omitting `linkpassword` simply leaves the old password in
    /// place, so an options object asking for none has to say so explicitly.
    public func updateShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        guard let existing = try await findPublink(at: path), let linkId = existing.linkid else {
            throw CloudProviderError.commandFailed(L10n.text("This file doesn't have a public link."))
        }

        var params: [String: String] = ["linkid": String(linkId)]
        if let password = options.password, !password.isEmpty {
            params["linkpassword"] = password
        } else {
            params["deletepassword"] = "1"
        }
        if let expiry = options.expiry {
            params["expire"] = Self.expiryParameter(expiry)
        } else {
            params["deleteexpire"] = "1"
        }

        // Premium-only settings fail here exactly as they do on create, with
        // pCloud's own explanation.
        let _: PCloudBasicResponse = try await requestSurfacingServerMessage("changepublink", params: params)

        // `changepublink` answers with the bare result envelope, so read the
        // link back rather than assuming pCloud applied what we asked for.
        if let updated = try await findPublink(at: path) {
            return try await shareLink(from: updated, requestedPassword: options.password)
        }
        return try await shareLink(from: existing, requestedPassword: options.password)
    }

    /// Withdraws the link with `deletepublink`.
    public func removeShareLink(at path: String) async throws {
        guard let linkId = try await findPublink(at: path)?.linkid else {
            throw CloudProviderError.commandFailed(L10n.text("This file doesn't have a public link."))
        }
        let _: PCloudBasicResponse = try await requestSurfacingServerMessage(
            "deletepublink",
            params: ["linkid": String(linkId)]
        )
    }

    /// Finds `path` among the account's public links.
    ///
    /// Matching on `fileid` is exact, so that comes first: pCloud echoes the
    /// stored path inconsistently (and omits it entirely for some links), and
    /// a path match would also confuse two files whose links were made before
    /// a rename. The path comparison stays as a fallback for links that carry
    /// no id. A `stat` that fails (file gone) leaves only that fallback.
    private func findPublink(at path: String) async throws -> PCloudPublink? {
        var fileId: UInt64?
        do {
            fileId = try await self.fileId(at: path)
        } catch {
            SupportLogger.shared.log(
                "pCloud: couldn't resolve a file id for \(path) — \(error.localizedDescription)",
                category: "sharing",
                level: .error
            )
        }
        let response: PCloudPublinksResponse = try await requestSurfacingServerMessage(
            "listpublinks",
            params: ["timeformat": "timestamp"]
        )
        let links = response.publinks ?? []

        if let fileId, let hit = links.first(where: { $0.resolvedFileId == fileId }) {
            return hit
        }
        if let hit = links.first(where: { $0.resolvedPath == path }) {
            return hit
        }
        // pCloud reports a link's path inconsistently (with or without the
        // leading slash, sometimes only the file name via metadata), so fall
        // back to comparing the last component before giving up.
        let fileName = (path as NSString).lastPathComponent
        if !fileName.isEmpty,
           let hit = links.first(where: { link in
               guard let candidate = link.resolvedPath else { return false }
               return (candidate as NSString).lastPathComponent == fileName
           }) {
            return hit
        }

        // Nothing matched: record what came back, because "this file has no
        // public link" is indistinguishable from "the matching failed"
        // without seeing the payload.
        SupportLogger.shared.log(
            "pCloud listpublinks: no match for \(path) (fileid \(fileId.map(String.init) ?? "unknown")) among \(links.count) link(s): "
                + links.map { "[id=\($0.linkid.map(String.init) ?? "?") fileid=\($0.resolvedFileId.map(String.init) ?? "?") path=\($0.resolvedPath ?? "?")]" }
                    .joined(separator: " "),
            category: "sharing",
            level: .error
        )
        return nil
    }

    private func fileId(at path: String) async throws -> UInt64? {
        // `timeformat=timestamp` is required, not decoration: without it
        // pCloud returns RFC 2822 date *strings* where the metadata model
        // expects numbers, the decode fails, and the caller silently ends up
        // with no file id — which is exactly how "stop sharing" came to
        // claim a shared file had no link.
        let response: PCloudStatResponse = try await request(
            "stat",
            params: ["path": path, "timeformat": "timestamp"]
        )
        return response.metadata.fileid
    }

    /// Maps a `listpublinks` entry onto our own type, resolving the CDN URL
    /// the same best-effort way `createShareLink` does.
    private func shareLink(from publink: PCloudPublink, requestedPassword: String?) async throws -> CloudShareLink {
        let landingURL: URL
        if let raw = publink.link, let url = URL(string: raw) {
            landingURL = url
        } else if let code = publink.code,
                  let url = URL(string: "https://my.pcloud.com/publink/show?code=\(code)") {
            // `listpublinks` doesn't always echo the full link; the landing
            // page is the documented `code` form.
            landingURL = url
        } else {
            throw CloudProviderError.invalidResponse
        }

        var direct: URL?
        if let code = publink.code {
            direct = try? await publicLinkDownloadURL(code: code)
        }

        return CloudShareLink(
            url: landingURL,
            directDownloadURL: direct,
            expiresAt: publink.expiryDate,
            hasPassword: publink.haspassword ?? !(requestedPassword ?? "").isEmpty
        )
    }

    /// pCloud's `expire` parameter: UTC, seconds precision.
    private static func expiryParameter(_ date: Date) -> String {
        let fmt = DateFormatter()
        fmt.locale = Locale(identifier: "en_US_POSIX")
        fmt.timeZone = TimeZone(secondsFromGMT: 0)
        fmt.dateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'"
        return fmt.string(from: date)
    }

    /// Resolves a public-link `code` to the CDN URL serving the file bytes —
    /// same `hosts` + `path` shape as `getfilelink`.
    private func publicLinkDownloadURL(code: String) async throws -> URL {
        let response: PCloudFileLinkResponse = try await request("getpublinkdownload", params: ["code": code])
        guard let host = response.hosts?.first, let filePath = response.path,
              let url = URL(string: "https://\(host)\(filePath)") else {
            throw CloudProviderError.invalidResponse
        }
        return url
    }

    // MARK: - HTTP

    private func request<T: Decodable>(_ method: String, params: [String: String]) async throws -> T {
        let data = try await fetch(method, params: params)

        // Check pCloud result code
        if let basicResult = try? JSONDecoder().decode(PCloudBasicResponse.self, from: data),
           basicResult.result != 0 {
            throw Self.mapError(code: basicResult.result)
        }

        return try JSONDecoder().decode(T.self, from: data)
    }

    /// Variant of `request` that surfaces pCloud's own `error` text.
    ///
    /// The plain helper collapses a non-zero `result` to a mapped code so
    /// callers like `deleteFile` can pattern-match on `.notFound` /
    /// `.serverError(2055)`. Share-link creation wants the opposite: its
    /// interesting failures (password and expiry need Premium) are only
    /// explicable in pCloud's own words.
    private func requestSurfacingServerMessage<T: Decodable>(_ method: String, params: [String: String]) async throws -> T {
        let data = try await fetch(method, params: params)

        if let envelope = try? JSONDecoder().decode(PCloudErrorEnvelope.self, from: data), envelope.result != 0 {
            if let message = envelope.error, !message.isEmpty {
                throw CloudProviderError.commandFailed(message)
            }
            throw Self.mapError(code: envelope.result)
        }

        return try JSONDecoder().decode(T.self, from: data)
    }

    /// Shared plumbing: auth-stamped GET against the account's own API host
    /// (`api.pcloud.com` for US accounts, `eapi.pcloud.com` for EU ones —
    /// `credentials.hostname` records which one logged in).
    private func fetch(_ method: String, params: [String: String]) async throws -> Data {
        var components = URLComponents(string: "\(baseURL)/\(method)")!
        var queryItems = [URLQueryItem(name: "auth", value: credentials.accessToken)]
        for (key, value) in params {
            queryItems.append(URLQueryItem(name: key, value: value))
        }
        components.queryItems = queryItems

        guard let url = components.url else {
            throw CloudProviderError.invalidResponse
        }

        let (data, response) = try await session.data(from: url)

        guard let httpResponse = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        guard (200...299).contains(httpResponse.statusCode) else {
            throw CloudProviderError.serverError(httpResponse.statusCode)
        }

        return data
    }

    private static func mapError(code: Int) -> CloudProviderError {
        switch code {
        case 1000: return .notAuthenticated
        case 2000: return .unauthorized
        case 2003: return .unauthorized
        case 2005: return .notFound("Directory not found")
        case 2009: return .notFound("File not found")
        case 2008: return .quotaExceeded
        case 4000: return .rateLimited
        default: return .serverError(code)
        }
    }
}

// MARK: - pCloud API Response Types

struct PCloudBasicResponse: Decodable {
    let result: Int
}

/// Non-zero `result` plus pCloud's own human-readable reason. Used where a
/// caller wants to show the server's wording instead of a mapped code.
struct PCloudErrorEnvelope: Decodable {
    let result: Int
    let error: String?
}

struct PCloudListFolderResponse: Decodable {
    let result: Int
    let metadata: PCloudFolderMetadata
}

struct PCloudStatResponse: Decodable {
    let result: Int
    let metadata: PCloudItemMetadata
}

struct PCloudFileLinkResponse: Decodable {
    let result: Int
    let path: String?
    let hosts: [String]?
}

struct PCloudUserInfoResponse: Decodable {
    let result: Int
    let email: String?
    let userid: UInt64?
    let quota: Int64?
    let usedquota: Int64?
}

struct PCloudFolderMetadata: Decodable {
    let name: String?
    let folderid: UInt64?
    let contents: [PCloudItemMetadata]?
}

struct PCloudItemMetadata: Decodable {
    let name: String
    let isfolder: Bool
    let fileid: UInt64?
    let folderid: UInt64?
    let size: Int64?
    let modified: TimeInterval?
    let created: TimeInterval?
    let contenttype: String?
    let hash: UInt64?
    let icon: String?
    let contents: [PCloudItemMetadata]?

    var folderContents: PCloudFolderMetadata? {
        guard isfolder else { return nil }
        return PCloudFolderMetadata(name: name, folderid: folderid, contents: contents)
    }

    public func toCloudFileItem(parentPath: String) -> CloudFileItem {
        let itemPath: String
        if parentPath == "/" {
            itemPath = "/\(name)"
        } else {
            itemPath = "\(parentPath)/\(name)"
        }

        let modDate: Date
        if let ts = modified {
            modDate = Date(timeIntervalSince1970: ts)
        } else {
            modDate = Date.distantPast
        }

        return CloudFileItem(
            id: isfolder ? "d\(folderid ?? 0)" : "f\(fileid ?? 0)",
            name: name,
            path: itemPath,
            isDirectory: isfolder,
            size: size ?? 0,
            modificationDate: modDate,
            checksum: hash.map { String($0) }
        )
    }
}

public struct PCloudUserInfo: Sendable {
    public let email: String
    public let userId: UInt64
    public let quota: Int64
    public let usedQuota: Int64

    public init(email: String, userId: UInt64, quota: Int64, usedQuota: Int64) {
        self.email = email
        self.userId = userId
        self.quota = quota
        self.usedQuota = usedQuota
    }
}
