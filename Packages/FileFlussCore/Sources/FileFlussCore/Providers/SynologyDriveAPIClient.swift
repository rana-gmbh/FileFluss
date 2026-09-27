import Foundation
import os

private let synologyLog = Logger(subsystem: "com.rana.FileFluss", category: "synologyDrive")

public struct SynologyDriveCredentials: Codable, Sendable {
    let serverURL: String
    let username: String
    let password: String
    let allowSelfSignedCertificate: Bool
    let displayName: String
}

/// Talks to the Synology DSM Web API (`SYNO.API.Auth` + `SYNO.FileStation.*`)
/// running on the user's NAS. The same API every other third-party tool
/// (rclone, Mountain Duck, the official Drive client) uses — works for
/// any Synology NAS reachable on the network or via QuickConnect.
public actor SynologyDriveAPIClient {
    let credentials: SynologyDriveCredentials
    private let session: URLSession
    /// Session ID returned by `SYNO.API.Auth` on login. Re-login when nil
    /// or after the server reports it has expired.
    private var sid: String?

    public init(credentials: SynologyDriveCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 1800
        if credentials.allowSelfSignedCertificate {
            self.session = URLSession(
                configuration: config,
                delegate: SynologyTrustingDelegate(host: SynologyDriveAPIClient.host(from: credentials.serverURL)),
                delegateQueue: nil
            )
        } else {
            self.session = URLSession(configuration: config)
        }
    }

    public func userDisplayName() -> String { credentials.displayName }

    // MARK: - Authentication

    /// Validates credentials by logging in. Stores the SID for use by
    /// subsequent calls. `otp` is required when the account has 2FA on.
    public static func authenticate(
        serverURL: String,
        username: String,
        password: String,
        otp: String? = nil,
        allowSelfSignedCertificate: Bool
    ) async throws -> SynologyDriveCredentials {
        let normalized = normalizeServerURL(serverURL)
        let displayName = "\(username)@\(host(from: normalized))"
        let creds = SynologyDriveCredentials(
            serverURL: normalized,
            username: username,
            password: password,
            allowSelfSignedCertificate: allowSelfSignedCertificate,
            displayName: displayName
        )
        let client = SynologyDriveAPIClient(credentials: creds)
        try await client.login(otp: otp)
        return creds
    }

    private func login(otp: String? = nil) async throws {
        var query: [URLQueryItem] = [
            URLQueryItem(name: "api", value: "SYNO.API.Auth"),
            URLQueryItem(name: "version", value: "6"),
            URLQueryItem(name: "method", value: "login"),
            URLQueryItem(name: "account", value: credentials.username),
            URLQueryItem(name: "passwd", value: credentials.password),
            URLQueryItem(name: "session", value: "FileStation"),
            URLQueryItem(name: "format", value: "sid")
        ]
        if let otp, !otp.isEmpty {
            query.append(URLQueryItem(name: "otp_code", value: otp))
        }
        let url = try buildURL(path: "/webapi/auth.cgi", queryItems: query)
        let (data, _) = try await session.data(for: URLRequest(url: url))
        let decoded = try JSONDecoder().decode(SynologyAuthResponse.self, from: data)
        if !decoded.success {
            throw SynologyDriveAPIClient.mapAuthError(code: decoded.error?.code ?? -1)
        }
        guard let newSid = decoded.data?.sid else { throw CloudProviderError.invalidResponse }
        self.sid = newSid
        synologyLog.info("[Synology] Logged in as \(self.credentials.username) at \(self.credentials.serverURL)")
    }

    /// Returns a current SID, logging in first if there isn't one yet.
    private func ensureSession() async throws -> String {
        if let sid { return sid }
        try await login()
        guard let sid else { throw CloudProviderError.notAuthenticated }
        return sid
    }

    // MARK: - Listing

    public func listFolder(path: String) async throws -> [CloudFileItem] {
        let cleaned = path.isEmpty ? "/" : path
        if cleaned == "/" {
            return try await listShares()
        }
        return try await listInside(folderPath: cleaned)
    }

    /// Top-level: returns the NAS's shared folders as folder rows.
    private func listShares() async throws -> [CloudFileItem] {
        let result: SynologyListSharesData = try await call(
            api: "SYNO.FileStation.List",
            version: "2",
            method: "list_share",
            extra: [
                URLQueryItem(name: "additional", value: "[\"time\",\"real_path\"]")
            ]
        )
        return result.shares.map { share in
            CloudFileItem(
                id: share.path,
                name: share.name,
                path: share.path,
                isDirectory: true,
                size: 0,
                modificationDate: share.additional?.timeDate ?? .distantPast,
                checksum: nil
            )
        }
    }

    private func listInside(folderPath: String) async throws -> [CloudFileItem] {
        let result: SynologyListData = try await call(
            api: "SYNO.FileStation.List",
            version: "2",
            method: "list",
            extra: [
                URLQueryItem(name: "folder_path", value: folderPath),
                URLQueryItem(name: "additional", value: "[\"size\",\"time\",\"type\"]")
            ]
        )
        return result.files.map { file in
            CloudFileItem(
                id: file.path,
                name: file.name,
                path: file.path,
                isDirectory: file.isdir,
                size: file.additional?.size ?? 0,
                modificationDate: file.additional?.timeDate ?? .distantPast,
                checksum: nil
            )
        }
    }

    // MARK: - File operations

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let sid = try await ensureSession()
        let url = try buildURL(
            path: "/webapi/entry.cgi",
            queryItems: [
                URLQueryItem(name: "api", value: "SYNO.FileStation.Download"),
                URLQueryItem(name: "version", value: "2"),
                URLQueryItem(name: "method", value: "download"),
                URLQueryItem(name: "path", value: remotePath),
                URLQueryItem(name: "mode", value: "download"),
                URLQueryItem(name: "_sid", value: sid)
            ]
        )

        let (tmp, response) = try await session.downloadReportingProgress(for: URLRequest(url: url), onBytes: onBytes)
        try validateHTTP(response)

        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tmp, to: localURL)
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let sid = try await ensureSession()
        let parentPath = (remotePath as NSString).deletingLastPathComponent
        let fileName = (remotePath as NSString).lastPathComponent

        let boundary = "----FileFlussSynology\(UUID().uuidString)"
        let url = try buildURL(
            path: "/webapi/entry.cgi",
            queryItems: [
                URLQueryItem(name: "api", value: "SYNO.FileStation.Upload"),
                URLQueryItem(name: "version", value: "2"),
                URLQueryItem(name: "method", value: "upload"),
                URLQueryItem(name: "_sid", value: sid)
            ]
        )

        // Build the multipart body in a temp file so big uploads don't sit
        // in RAM. URLSession.upload(fromFile:) streams it.
        let bodyFile = FileManager.default.temporaryDirectory
            .appendingPathComponent("filefluss-syno-\(UUID().uuidString).bin")
        FileManager.default.createFile(atPath: bodyFile.path, contents: nil)
        let handle = try FileHandle(forWritingTo: bodyFile)
        defer {
            try? handle.close()
            try? FileManager.default.removeItem(at: bodyFile)
        }

        func writePart(_ name: String, value: String) throws {
            try handle.write(contentsOf: Data("--\(boundary)\r\n".utf8))
            try handle.write(contentsOf: Data("Content-Disposition: form-data; name=\"\(name)\"\r\n\r\n".utf8))
            try handle.write(contentsOf: Data("\(value)\r\n".utf8))
        }

        try writePart("path", value: parentPath.isEmpty ? "/" : parentPath)
        try writePart("create_parents", value: "true")
        try writePart("overwrite", value: "true")

        // File field
        try handle.write(contentsOf: Data("--\(boundary)\r\n".utf8))
        try handle.write(contentsOf: Data("Content-Disposition: form-data; name=\"file\"; filename=\"\(fileName)\"\r\n".utf8))
        try handle.write(contentsOf: Data("Content-Type: application/octet-stream\r\n\r\n".utf8))

        // Stream the source file into the body without loading it all.
        let inHandle = try FileHandle(forReadingFrom: localURL)
        defer { try? inHandle.close() }
        while let chunk = try inHandle.read(upToCount: 1 << 20), !chunk.isEmpty {
            try handle.write(contentsOf: chunk)
        }

        try handle.write(contentsOf: Data("\r\n--\(boundary)--\r\n".utf8))
        try handle.close()

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

        let progressDelegate = onBytes.map { ByteProgressDelegate(onBytes: $0) }
        let (data, response) = try await session.upload(for: request, fromFile: bodyFile, delegate: progressDelegate)
        try validateHTTP(response)
        let envelope = try JSONDecoder().decode(SynologyEnvelope<SynologyEmptyData>.self, from: data)
        if !envelope.success {
            throw mapAPIError(code: envelope.error?.code ?? -1)
        }
    }

    public func deleteItem(at path: String) async throws {
        // Synchronous mode (`recursive=true`) handles directories + their
        // contents in one call without needing to poll a task id.
        let _: SynologyEmptyData = try await call(
            api: "SYNO.FileStation.Delete",
            version: "2",
            method: "delete",
            extra: [
                URLQueryItem(name: "path", value: path),
                URLQueryItem(name: "recursive", value: "true")
            ]
        )
    }

    public func createFolder(at path: String) async throws {
        let parent = (path as NSString).deletingLastPathComponent
        let name = (path as NSString).lastPathComponent
        let _: SynologyCreateFolderData = try await call(
            api: "SYNO.FileStation.CreateFolder",
            version: "2",
            method: "create",
            extra: [
                URLQueryItem(name: "folder_path", value: parent),
                URLQueryItem(name: "name", value: name),
                URLQueryItem(name: "force_parent", value: "true")
            ]
        )
    }

    public func renameItem(at path: String, to newName: String) async throws {
        let _: SynologyEmptyData = try await call(
            api: "SYNO.FileStation.Rename",
            version: "2",
            method: "rename",
            extra: [
                URLQueryItem(name: "path", value: path),
                URLQueryItem(name: "name", value: newName)
            ]
        )
    }

    /// Server-side move/copy. `removeSrc=true` for move, `false` for copy.
    public func copyMove(path: String, toFolderPath destFolder: String, removeSrc: Bool) async throws {
        // Polling-based async API; we issue start, then poll status until
        // finished. Most ops complete in a single status call.
        let start: SynologyTaskStartData = try await call(
            api: "SYNO.FileStation.CopyMove",
            version: "3",
            method: "start",
            extra: [
                URLQueryItem(name: "path", value: path),
                URLQueryItem(name: "dest_folder_path", value: destFolder),
                URLQueryItem(name: "overwrite", value: "true"),
                URLQueryItem(name: "remove_src", value: removeSrc ? "true" : "false")
            ]
        )
        guard let taskid = start.taskid else { return }

        // Poll up to 60 seconds in 0.5s steps.
        for _ in 0..<120 {
            let status: SynologyTaskStatusData = try await call(
                api: "SYNO.FileStation.CopyMove",
                version: "3",
                method: "status",
                extra: [URLQueryItem(name: "taskid", value: taskid)]
            )
            if status.finished == true { return }
            try? await Task.sleep(nanoseconds: 500_000_000)
        }
    }

    public func getFileInfo(at path: String) async throws -> CloudFileItem {
        let result: SynologyGetInfoData = try await call(
            api: "SYNO.FileStation.List",
            version: "2",
            method: "getinfo",
            extra: [
                URLQueryItem(name: "path", value: "[\"\(path.replacingOccurrences(of: "\"", with: "\\\""))\"]"),
                URLQueryItem(name: "additional", value: "[\"size\",\"time\",\"type\"]")
            ]
        )
        guard let first = result.files.first else { throw CloudProviderError.notFound(path) }
        return CloudFileItem(
            id: first.path,
            name: first.name,
            path: first.path,
            isDirectory: first.isdir,
            size: first.additional?.size ?? 0,
            modificationDate: first.additional?.timeDate ?? .distantPast,
            checksum: nil
        )
    }

    public func folderSize(path: String) async throws -> Int64 {
        // Recursive enumeration. NAS shares can be huge; cap at first
        // 5000 items so we don't hammer the device for hours on a giant
        // share — the user can get an exact size from DSM directly.
        var total: Int64 = 0
        var queue: [String] = [path]
        var seen = 0
        while let next = queue.first {
            queue.removeFirst()
            let items = try await listInside(folderPath: next)
            for item in items {
                seen += 1
                if seen > 5000 { return total }
                if item.isDirectory {
                    queue.append(item.path)
                } else {
                    total += item.size
                }
            }
        }
        return total
    }

    public func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]? {
        // Fire a search task on the chosen folder (or root) and poll until
        // it finishes, then fetch the result list.
        let folderPath = path ?? "/"
        let start: SynologyTaskStartData = try await call(
            api: "SYNO.FileStation.Search",
            version: "2",
            method: "start",
            extra: [
                URLQueryItem(name: "folder_path", value: folderPath),
                URLQueryItem(name: "pattern", value: query),
                URLQueryItem(name: "recursive", value: "true")
            ]
        )
        guard let taskid = start.taskid else { return [] }

        // Poll until finished, but always proceed to fetch results too —
        // partial results are fine even before completion.
        var finished = false
        for _ in 0..<10 {
            let status: SynologyTaskStatusData = try await call(
                api: "SYNO.FileStation.Search",
                version: "2",
                method: "status",
                extra: [URLQueryItem(name: "taskid", value: taskid)]
            )
            if status.finished == true { finished = true; break }
            try? await Task.sleep(nanoseconds: 300_000_000)
        }
        _ = finished

        let listResult: SynologyListData = try await call(
            api: "SYNO.FileStation.Search",
            version: "2",
            method: "list",
            extra: [
                URLQueryItem(name: "taskid", value: taskid),
                URLQueryItem(name: "limit", value: "200"),
                URLQueryItem(name: "additional", value: "[\"size\",\"time\",\"type\"]")
            ]
        )

        // Best-effort cleanup of the server-side task.
        let _: SynologyEmptyData = (try? await call(
            api: "SYNO.FileStation.Search",
            version: "2",
            method: "stop",
            extra: [URLQueryItem(name: "taskid", value: taskid)]
        )) ?? SynologyEmptyData()

        return listResult.files.map { file in
            CloudFileItem(
                id: file.path,
                name: file.name,
                path: file.path,
                isDirectory: file.isdir,
                size: file.additional?.size ?? 0,
                modificationDate: file.additional?.timeDate ?? .distantPast,
                checksum: nil
            )
        }
    }

    // MARK: - Share links

    /// Payload of `SYNO.FileStation.Sharing` `create`. DSM answers with one
    /// entry per requested path, each carrying its own `error` code even
    /// when the envelope reports `success`.
    private struct SynologySharingLinksData: Decodable {
        let links: [Link]
        struct Link: Decodable {
            let id: String?
            let url: String?
            let qrcode: String?
            /// 0 on success, otherwise a sharing error code.
            let error: Int?
        }
    }

    /// Creates a File Station public sharing link for `path`.
    ///
    /// DSM builds the returned URL from whatever address it believes it has,
    /// which is frequently a LAN address or an unreachable DDNS name — the
    /// caller surfaces that caveat via `CloudShareLink.note`.
    public func createShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        // Sharing's `path` is a JSON-encoded parameter, like `List.getinfo`'s.
        var extra: [URLQueryItem] = [
            URLQueryItem(name: "path", value: Self.jsonQuoted(path))
        ]
        if let password = options.password, !password.isEmpty {
            extra.append(URLQueryItem(name: "password", value: password))
        }
        if let expiry = options.expiry {
            extra.append(URLQueryItem(name: "date_expired", value: Self.jsonQuoted(Self.expiryDateString(expiry))))
        }

        let result: SynologySharingLinksData = try await sharingCall(method: "create", extra: extra)

        guard let link = result.links.first else { throw CloudProviderError.invalidResponse }
        if let code = link.error, code != 0 {
            throw Self.mapSharingError(code: code)
        }
        guard let urlString = link.url, let url = URL(string: urlString) else {
            throw CloudProviderError.invalidResponse
        }

        return CloudShareLink(
            url: url,
            // File Station has no documented suffix that streams the file
            // itself, so there's no verified direct-download form.
            directDownloadURL: nil,
            // DSM doesn't echo the effective expiry back on create.
            expiresAt: options.expiry,
            hasPassword: !(options.password ?? "").isEmpty,
            note: Self.lanAddressNote
        )
    }

    /// Payload of `SYNO.FileStation.Sharing` `list` and `getinfo`: the links
    /// the logged-in user owns, with the settings DSM currently holds for
    /// each. Field coverage varies across DSM 6/7, so everything but the
    /// array itself is optional.
    private struct SynologySharingListData: Decodable {
        let links: [Entry]
        let total: Int?

        struct Entry: Decodable {
            let id: String?
            let url: String?
            /// The shared path, in File Station's own form (`/volume1/…` or
            /// `/share/…` depending on how the link was made).
            let path: String?
            /// "0" (or absent) when the link never expires, otherwise
            /// "yyyy-MM-dd", which DSM 7 may extend with a time.
            let date_expired: String?
            let has_password: Bool?
            /// Remaining download allowance; 0 means unlimited.
            let expire_times: Int?
        }
    }

    /// Returns the File Station link this path already has, or nil when it has
    /// none.
    ///
    /// DSM has no per-path lookup, so this pages through the links the account
    /// owns and matches on `path`. Not finding the path is the "not shared"
    /// answer; a DSM error code (session expired, insufficient privilege,
    /// sharing subsystem unreachable) is a real error and propagates.
    ///
    /// Only links this account owns are visible, so a link another DSM user
    /// made for the same file reads as "not shared" here.
    public func existingShareLink(at path: String) async throws -> CloudShareLink? {
        guard let entry = try await findSharingLink(at: path) else { return nil }
        guard let link = Self.shareLink(from: entry, requestedPassword: nil, requestedExpiry: nil) else {
            throw CloudProviderError.invalidResponse
        }
        return link
    }

    /// Rewrites an existing link's password and expiry with `edit`. The link
    /// id — and therefore the URL recipients already have — is preserved.
    public func updateShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        guard let existing = try await findSharingLink(at: path), let id = existing.id else {
            throw CloudProviderError.commandFailed(L10n.text("This item doesn't have a public link."))
        }

        // File Station clears a password with an empty string and lifts an
        // expiry with the literal "0" — leaving either parameter out keeps the
        // setting that's already there, so "no password" and "no expiry" have
        // to be stated explicitly.
        let password = (options.password?.isEmpty == false) ? options.password! : ""
        let extra: [URLQueryItem] = [
            URLQueryItem(name: "id", value: Self.jsonQuoted(id)),
            URLQueryItem(name: "password", value: password),
            URLQueryItem(
                name: "date_expired",
                value: Self.jsonQuoted(options.expiry.map(Self.expiryDateString) ?? "0")
            ),
            // The download-count limit. 0 is unlimited; set it every time so
            // an edit doesn't leave a previously counted-down link in place.
            URLQueryItem(name: "expire_times", value: "0"),
        ]

        let _: SynologyEmptyData = try await sharingCall(method: "edit", extra: extra)

        // `edit` answers with an empty payload, so read the link back instead
        // of assuming DSM applied what we asked for.
        let effective = (try? await sharingLink(id: id)) ?? nil
        guard let link = Self.shareLink(
            from: effective ?? existing,
            requestedPassword: options.password,
            requestedExpiry: options.expiry
        ) else {
            throw CloudProviderError.invalidResponse
        }
        return link
    }

    /// Withdraws the link with `delete`. DSM answers success for an id it no
    /// longer knows, so this only fails when the path has no link at all.
    public func removeShareLink(at path: String) async throws {
        guard let id = try await findSharingLink(at: path)?.id else {
            throw CloudProviderError.commandFailed(L10n.text("This item doesn't have a public link."))
        }
        let _: SynologyEmptyData = try await sharingCall(
            method: "delete",
            extra: [URLQueryItem(name: "id", value: Self.jsonQuoted(id))]
        )
    }

    /// Pages through the account's links looking for `path`. The page cap is a
    /// guard against a DSM build that ignores `offset` and keeps answering
    /// with the same first page.
    private func findSharingLink(at path: String) async throws -> SynologySharingListData.Entry? {
        let pageSize = 200
        var offset = 0
        for _ in 0..<50 {
            let page: SynologySharingListData = try await sharingCall(
                method: "list",
                extra: [
                    URLQueryItem(name: "offset", value: String(offset)),
                    URLQueryItem(name: "limit", value: String(pageSize)),
                ]
            )
            if let hit = page.links.first(where: { $0.path == path }) { return hit }
            if page.links.count < pageSize { return nil }
            offset += page.links.count
            if let total = page.total, offset >= total { return nil }
        }
        return nil
    }

    /// `getinfo` for one known link id.
    private func sharingLink(id: String) async throws -> SynologySharingListData.Entry? {
        let info: SynologySharingListData = try await sharingCall(
            method: "getinfo",
            extra: [URLQueryItem(name: "id", value: Self.jsonQuoted(id))]
        )
        return info.links.first
    }

    /// Every `SYNO.FileStation.Sharing` call, with the sharing-specific error
    /// codes mapped. `mapAPIError` only knows the generic DSM ones; the
    /// 2000-range codes belong to sharing.
    private func sharingCall<T: Decodable>(method: String, extra: [URLQueryItem]) async throws -> T {
        do {
            return try await call(
                api: "SYNO.FileStation.Sharing",
                version: "3",
                method: method,
                extra: extra
            )
        } catch CloudProviderError.serverError(let code) {
            throw Self.mapSharingError(code: code)
        }
    }

    private static func shareLink(
        from entry: SynologySharingListData.Entry,
        requestedPassword: String?,
        requestedExpiry: Date?
    ) -> CloudShareLink? {
        guard let raw = entry.url, let url = URL(string: raw) else { return nil }
        return CloudShareLink(
            url: url,
            directDownloadURL: nil,
            expiresAt: parseExpiryDate(entry.date_expired) ?? requestedExpiry,
            hasPassword: entry.has_password ?? !(requestedPassword ?? "").isEmpty,
            note: lanAddressNote
        )
    }

    /// DSM builds the link from whatever address it believes it has, which is
    /// frequently a LAN address or an unreachable DDNS name.
    private static let lanAddressNote = "The NAS built this link from the address DSM thinks it has, so it may only work on your local network. Check DSM's external access settings if recipients can't open it."

    /// `date_expired` as File Station wants it: a UTC calendar day.
    private static func expiryDateString(_ date: Date) -> String {
        let fmt = DateFormatter()
        fmt.locale = Locale(identifier: "en_US_POSIX")
        fmt.timeZone = TimeZone(secondsFromGMT: 0)
        fmt.dateFormat = "yyyy-MM-dd"
        return fmt.string(from: date)
    }

    /// Reads `date_expired` back. "0" and "" both mean "never expires".
    private static func parseExpiryDate(_ raw: String?) -> Date? {
        guard let raw, !raw.isEmpty, raw != "0" else { return nil }
        let fmt = DateFormatter()
        fmt.locale = Locale(identifier: "en_US_POSIX")
        fmt.timeZone = TimeZone(secondsFromGMT: 0)
        for format in ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"] {
            fmt.dateFormat = format
            if let date = fmt.date(from: raw) { return date }
        }
        return nil
    }

    private static func mapSharingError(code: Int) -> CloudProviderError {
        switch code {
        case 2000: return .commandFailed("The sharing link does not exist.")
        case 2001: return .commandFailed("The NAS has reached its maximum number of sharing links — delete some in File Station and try again.")
        case 2002: return .commandFailed("Failed to access the NAS's sharing links.")
        default: return .serverError(code)
        }
    }

    /// Encodes a value as a JSON string literal, for the FileStation
    /// parameters that expect JSON rather than a bare value.
    private static func jsonQuoted(_ value: String) -> String {
        let escaped = value
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
        return "\"\(escaped)\""
    }

    // MARK: - Plumbing

    /// Issues a `webapi/entry.cgi` call with the provided API/version/method
    /// and decodes the JSON envelope into `T`. Re-logins once on SID
    /// expiry (error 119) before giving up.
    private func call<T: Decodable>(api: String, version: String, method: String, extra: [URLQueryItem]) async throws -> T {
        var attempt = 0
        while true {
            let sid = try await ensureSession()
            var query: [URLQueryItem] = [
                URLQueryItem(name: "api", value: api),
                URLQueryItem(name: "version", value: version),
                URLQueryItem(name: "method", value: method),
                URLQueryItem(name: "_sid", value: sid)
            ]
            query.append(contentsOf: extra)
            let url = try buildURL(path: "/webapi/entry.cgi", queryItems: query)

            let (data, response) = try await session.data(for: URLRequest(url: url))
            try validateHTTP(response)

            let envelope = try JSONDecoder().decode(SynologyEnvelope<T>.self, from: data)
            if envelope.success, let payload = envelope.data {
                return payload
            }
            // Some endpoints succeed without a `data` payload (e.g. delete).
            // Detect by trying to decode an empty stub.
            if envelope.success, T.self == SynologyEmptyData.self {
                return SynologyEmptyData() as! T
            }
            let code = envelope.error?.code ?? -1
            if code == 119 && attempt == 0 {
                self.sid = nil
                attempt += 1
                continue
            }
            throw mapAPIError(code: code)
        }
    }

    private func buildURL(path: String, queryItems: [URLQueryItem]) throws -> URL {
        guard var components = URLComponents(string: credentials.serverURL) else {
            throw CloudProviderError.invalidResponse
        }
        components.path = path
        components.queryItems = queryItems
        guard let url = components.url else { throw CloudProviderError.invalidResponse }
        return url
    }

    private func validateHTTP(_ response: URLResponse) throws {
        guard let http = response as? HTTPURLResponse else { return }
        guard !(200..<300).contains(http.statusCode) else { return }
        synologyLog.error("[Synology] HTTP \(http.statusCode) from \(self.credentials.serverURL)")
        throw CloudProviderError.serverError(http.statusCode)
    }

    private static func normalizeServerURL(_ raw: String) -> String {
        var s = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if !s.lowercased().hasPrefix("http://") && !s.lowercased().hasPrefix("https://") {
            s = "https://" + s
        }
        // If the user typed a hostname without a port, default to DSM's
        // HTTPS port. Skip when the URL already contains an explicit port.
        if let comp = URLComponents(string: s), comp.port == nil {
            // Add the default DSM HTTPS port only when scheme is https.
            if comp.scheme?.lowercased() == "https" {
                if var c = URLComponents(string: s) {
                    c.port = 5001
                    s = c.string ?? s
                }
            } else if comp.scheme?.lowercased() == "http" {
                if var c = URLComponents(string: s) {
                    c.port = 5000
                    s = c.string ?? s
                }
            }
        }
        if s.hasSuffix("/") { s.removeLast() }
        return s
    }

    private static func host(from serverURL: String) -> String {
        URLComponents(string: serverURL)?.host ?? "synology"
    }

    private static func mapAuthError(code: Int) -> CloudProviderError {
        switch code {
        case 400: return .commandFailed("No such account or incorrect password.")
        case 401: return .commandFailed("Account is disabled.")
        case 402: return .commandFailed("Permission denied.")
        case 403: return .commandFailed("This account requires a 2-factor authentication (OTP) code.")
        case 404: return .commandFailed("OTP code is incorrect.")
        case 405: return .commandFailed("OTP code authentication has failed too many times.")
        case 406: return .commandFailed("Synology administrator must enforce 2-factor authentication.")
        case 407: return .commandFailed("Maximum number of OTP retries reached. Try again later.")
        case 408: return .commandFailed("Password expired — please reset it from DSM.")
        case 409: return .commandFailed("Password must be changed at first login.")
        default: return .commandFailed("Synology login failed (code \(code)).")
        }
    }

    private func mapAPIError(code: Int) -> CloudProviderError {
        switch code {
        case 400: return .commandFailed("Invalid parameter for the request.")
        case 401: return .commandFailed("Unknown API method.")
        case 402: return .commandFailed("Invalid API method version.")
        case 403: return .commandFailed("Invalid API method.")
        case 405: return .commandFailed("Insufficient user privilege.")
        case 406: return .commandFailed("Connection time out.")
        case 407: return .commandFailed("Multiple login detected.")
        case 408: return .commandFailed("Request denied — too many connections.")
        case 119: return .notAuthenticated
        case 401_400, 1_400, 1_401, 1_402: return .commandFailed("Synology rejected the file path.")
        default: return .serverError(code)
        }
    }
}

// MARK: - URLSession delegate for self-signed certs

/// Trusts the certificate presented by the configured Synology host even
/// when it doesn't chain to a system-trusted CA. Used only when the user
/// opts in via the "Allow self-signed certificate" checkbox.
public final class SynologyTrustingDelegate: NSObject, URLSessionDelegate, URLSessionTaskDelegate, URLSessionDownloadDelegate, @unchecked Sendable {
    let host: String
    public init(host: String) { self.host = host }

    public func urlSession(
        _ session: URLSession,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping @Sendable (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              challenge.protectionSpace.host == host,
              let trust = challenge.protectionSpace.serverTrust
        else {
            completionHandler(.performDefaultHandling, nil)
            return
        }
        completionHandler(.useCredential, URLCredential(trust: trust))
    }

    // The download/upload delegate methods are inherited via NSObject; this
    // class just acts as the SSL trust evaluator and stays out of the way
    // for everything else.
    public func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {}
}

// MARK: - JSON envelope models

private struct SynologyEnvelope<T: Decodable>: Decodable {
    let success: Bool
    let data: T?
    let error: SynologyError?

    struct SynologyError: Decodable { let code: Int }
}

private struct SynologyAuthResponse: Decodable {
    let success: Bool
    let data: AuthData?
    let error: ErrorCode?

    struct AuthData: Decodable { let sid: String? }
    struct ErrorCode: Decodable { let code: Int }
}

struct SynologyEmptyData: Decodable, Sendable {}

private struct SynologyListSharesData: Decodable {
    let shares: [Share]
    struct Share: Decodable {
        let name: String
        let path: String
        let additional: AdditionalShare?
    }
    struct AdditionalShare: Decodable {
        let time: TimeBlock?
        var timeDate: Date? { time?.mtimeDate }
    }
}

private struct SynologyListData: Decodable {
    let files: [Entry]
    struct Entry: Decodable {
        let name: String
        let path: String
        let isdir: Bool
        let additional: Additional?
    }
    struct Additional: Decodable {
        let size: Int64?
        let time: TimeBlock?
        var timeDate: Date? { time?.mtimeDate }
    }
}

private struct SynologyGetInfoData: Decodable {
    let files: [SynologyListData.Entry]
}

private struct SynologyCreateFolderData: Decodable {
    let folders: [SynologyListData.Entry]?
}

private struct SynologyTaskStartData: Decodable {
    let taskid: String?
}

private struct SynologyTaskStatusData: Decodable {
    let finished: Bool?
}

/// `additional.time` returns POSIX timestamps for atime/ctime/mtime.
private struct TimeBlock: Decodable {
    let mtime: TimeInterval?
    var mtimeDate: Date? {
        guard let mtime else { return nil }
        return Date(timeIntervalSince1970: mtime)
    }
}
