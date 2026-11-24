import Foundation
import os

private let synologyLog = Logger(subsystem: "com.rana.FileFluss", category: "synologyDrive")

public struct SynologyDriveCredentials: Codable, Sendable {
    let serverURL: String
    let username: String
    let password: String
    let allowSelfSignedCertificate: Bool
    let displayName: String
    /// Device token DSM hands out at an OTP login (`enable_device_token`).
    /// Replaying it as `device_id` lets later logins skip the OTP prompt.
    /// Optional so credentials saved by older versions still decode.
    var deviceId: String?
}

/// Talks to the Synology DSM Web API (`SYNO.API.Auth` + `SYNO.FileStation.*`)
/// running on the user's NAS. The same API every other third-party tool
/// (rclone, Mountain Duck, the official Drive client) uses — works for
/// any Synology NAS reachable on the network or via QuickConnect.
public actor SynologyDriveAPIClient {
    private(set) var credentials: SynologyDriveCredentials
    private let session: URLSession
    /// Session ID returned by `SYNO.API.Auth` on login. Re-login when nil
    /// or after the server reports it has expired.
    private var sid: String?
    /// Name DSM lists under "Trusted devices" for the token requested at OTP login.
    private static let deviceName = "FileFluss"

    /// How long a server-side copy or move may run before it's given up on,
    /// and the pause between status polls. Tests shorten both.
    private let copyMoveTimeout: Duration
    private let pollInterval: Duration

    public init(credentials: SynologyDriveCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.fileTransfer()
        if credentials.allowSelfSignedCertificate {
            self.session = URLSession(
                configuration: config,
                delegate: SynologyTrustingDelegate(host: SynologyDriveAPIClient.host(from: credentials.serverURL)),
                delegateQueue: nil
            )
        } else {
            self.session = URLSession(configuration: config)
        }
        self.copyMoveTimeout = .seconds(30 * 60)
        self.pollInterval = .milliseconds(500)
    }

    /// Tests hand in a session whose requests they answer themselves, and a
    /// session id so no login is needed.
    init(
        credentials: SynologyDriveCredentials,
        session: URLSession,
        sid: String?,
        copyMoveTimeout: Duration = .seconds(30 * 60),
        pollInterval: Duration = .milliseconds(500)
    ) {
        self.credentials = credentials
        self.session = session
        self.sid = sid
        self.copyMoveTimeout = copyMoveTimeout
        self.pollInterval = pollInterval
    }

    public func userDisplayName() -> String { credentials.displayName }

    // MARK: - Authentication

    /// Validates credentials by logging in and hands back the credentials.
    ///
    /// Prefer `authenticatedClient`, which hands back the logged-in client
    /// as well: a caller that builds a new client from these credentials
    /// starts without a session, and its first request logs in again
    /// without an OTP — which DSM rejects on a 2FA account. That was the
    /// bug in issue #66.
    @available(*, deprecated, message: "Use authenticatedClient, which keeps the session the OTP login established.")
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
        return await client.credentials
    }

    /// Same login as `authenticate`, but hands back the client that performed
    /// it, so the caller keeps the logged-in session. A fresh client would
    /// start without a session and log in again without an OTP, which DSM
    /// rejects for 2FA accounts right after the first successful login.
    /// `deviceId` is a token this account was given earlier. Pass it when
    /// re-authenticating the same account — editing its password, say —
    /// so a 2FA account doesn't have to produce a fresh OTP, and doesn't
    /// lose the token it already has. The caller is responsible for only
    /// passing a token that belongs to this server and user.
    static func authenticatedClient(
        serverURL: String,
        username: String,
        password: String,
        otp: String? = nil,
        allowSelfSignedCertificate: Bool,
        deviceId: String? = nil
    ) async throws -> SynologyDriveAPIClient {
        let normalized = normalizeServerURL(serverURL)
        let creds = SynologyDriveCredentials(
            serverURL: normalized,
            username: username,
            password: password,
            allowSelfSignedCertificate: allowSelfSignedCertificate,
            displayName: "\(username)@\(host(from: normalized))",
            deviceId: deviceId
        )
        let client = SynologyDriveAPIClient(credentials: creds)
        try await client.login(otp: otp)
        return client
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
        let hasOTP = !(otp ?? "").isEmpty
        if hasOTP, let otp {
            query.append(URLQueryItem(name: "otp_code", value: otp))
            // Ask DSM for a device token so later logins can skip the OTP.
            query.append(URLQueryItem(name: "enable_device_token", value: "yes"))
            query.append(URLQueryItem(name: "device_name", value: Self.deviceName))
        } else if let deviceId = credentials.deviceId, !deviceId.isEmpty {
            // Re-login without an OTP (after a restart or an expired SID):
            // present the stored device token instead.
            query.append(URLQueryItem(name: "device_name", value: Self.deviceName))
            query.append(URLQueryItem(name: "device_id", value: deviceId))
        }
        let url = try buildURL(path: "/webapi/auth.cgi", queryItems: query)
        let (data, _) = try await session.data(for: URLRequest(url: url))
        let decoded = try JSONDecoder().decode(SynologyAuthResponse.self, from: data)
        if !decoded.success {
            throw SynologyDriveAPIClient.mapAuthError(code: decoded.error?.code ?? -1)
        }
        guard let newSid = decoded.data?.sid else { throw CloudProviderError.invalidResponse }
        self.sid = newSid
        if hasOTP {
            // API v6 names the token `did`, v7 `device_id`. The value itself
            // is a credential and is never logged.
            if let token = decoded.data?.did ?? decoded.data?.deviceId, !token.isEmpty {
                self.credentials.deviceId = token
                synologyLog.info("[Synology] DSM issued a device token; later logins skip the OTP")
            } else {
                synologyLog.warning("[Synology] OTP login worked but DSM issued no device token; a later re-login will ask for an OTP again")
            }
        }
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
        var attempt = 0
        while true {
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
            // DSM reports a failed download (missing file, expired session)
            // as a small JSON envelope with HTTP 200. Saved as it was, that
            // envelope became the file's contents and the download a success.
            let failure: Int?
            do {
                try validateHTTP(response)
                failure = Self.errorCodeInSmallJSON(at: tmp, response: response)
            } catch {
                try? FileManager.default.removeItem(at: tmp)
                throw error
            }
            if let code = failure {
                try? FileManager.default.removeItem(at: tmp)
                if code == 119 && attempt == 0 {
                    self.sid = nil
                    attempt += 1
                    continue
                }
                synologyLog.error("[Synology] Download failed with code \(code)")
                throw code == 408 ? CloudProviderError.notFound(remotePath) : mapAPIError(code: code)
            }

            try FileSystemService.placeDownload(from: tmp, at: localURL)
            return
        }
    }

    /// The DSM error code in a downloaded body that is really an error
    /// envelope. Only a small JSON-ish reply carrying an explicit
    /// `"success": false` counts: any other JSON is the file itself.
    static func errorCodeInSmallJSON(at url: URL, response: URLResponse) -> Int? {
        let jsonTypes: Set<String> = ["application/json", "text/json", "text/plain"]
        guard let mime = response.mimeType?.lowercased(), jsonTypes.contains(mime),
              let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size]) as? NSNumber,
              size.intValue <= 4096,
              let data = try? Data(contentsOf: url),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let success = object["success"] as? Bool, !success
        else { return nil }
        let error = object["error"] as? [String: Any]
        return (error?["code"] as? NSNumber)?.intValue ?? -1
    }

    // MARK: - Thumbnails

    /// A thumbnail DSM keeps for the file, through `SYNO.FileStation.Thumb`
    /// (File Station API guide, "SYNO.FileStation.Thumb", DSM 6.0 and
    /// later). It answers with the image itself, and with a plain HTTP 404
    /// when it has none: a type it doesn't thumbnail, or a video outside the
    /// "photo" share and the home folders, the only places DSM indexes them.
    /// An expired session can still come back as the usual JSON envelope,
    /// so that is read the way `call` reads it, logging in again once.
    public func thumbnail(path: String, maxPixelSize: Int) async throws -> Data? {
        let size = Self.thumbnailSize(forMaxPixelSize: maxPixelSize)
        var attempt = 0
        while true {
            let sid = try await ensureSession()
            let url = try thumbnailURL(path: path, size: size, sid: sid)
            var request = URLRequest(url: url)
            request.setValue("image/*", forHTTPHeaderField: "Accept")

            let (data, response) = try await session.data(for: request)
            if let http = response as? HTTPURLResponse, http.statusCode == 404 {
                return nil
            }
            try validateHTTP(response)

            if (response as? HTTPURLResponse)?.mimeType?.hasPrefix("image/") ?? false {
                return data.isEmpty ? nil : data
            }
            guard let envelope = try? JSONDecoder().decode(SynologyEnvelope<SynologyEmptyData>.self, from: data) else {
                return nil
            }
            let code = envelope.error?.code ?? -1
            if code == 119 {
                guard attempt == 0 else { throw CloudProviderError.notAuthenticated }
                self.sid = nil
                attempt += 1
                continue
            }
            // Any other refusal (no such file, no permission for this one)
            // is about the file, not the account.
            return nil
        }
    }

    /// DSM's thumbnails come in three fixed sizes; which pixel size each
    /// one is varies by DSM version and isn't documented, so this asks for
    /// one comfortably above the tile and lets the caller scale it down:
    /// "small" only for tiny tiles, "medium" for the gallery's usual sizes,
    /// "large" beyond that. "original" would be the whole picture.
    static func thumbnailSize(forMaxPixelSize maxPixelSize: Int) -> String {
        switch maxPixelSize {
        case ...120: return "small"
        case ...320: return "medium"
        default: return "large"
        }
    }

    /// The path goes in JSON-quoted, as the API guide's own example passes
    /// it. `_sid` sits in the query like on every other File Station call.
    private func thumbnailURL(path: String, size: String, sid: String) throws -> URL {
        let url = try buildURL(
            path: "/webapi/entry.cgi",
            queryItems: Self.thumbnailQueryItems(path: path, size: size) + [URLQueryItem(name: "_sid", value: sid)]
        )
        return Self.encodingPlus(in: url)
    }

    static func thumbnailQueryItems(path: String, size: String) -> [URLQueryItem] {
        [
            URLQueryItem(name: "api", value: "SYNO.FileStation.Thumb"),
            URLQueryItem(name: "version", value: "2"),
            URLQueryItem(name: "method", value: "get"),
            URLQueryItem(name: "path", value: jsonQuoted(path)),
            URLQueryItem(name: "size", value: size),
        ]
    }

    /// URLComponents leaves `+` as it is, and DSM's CGI reads a bare `+` in
    /// a query as a space, so "a+b.jpg" would name a file that isn't there.
    static func encodingPlus(in url: URL) -> URL {
        guard var components = URLComponents(url: url, resolvingAgainstBaseURL: false) else { return url }
        components.percentEncodedQuery = components.percentEncodedQuery?
            .replacingOccurrences(of: "+", with: "%2B")
        return components.url ?? url
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let parentPath = (remotePath as NSString).deletingLastPathComponent
        let fileName = (remotePath as NSString).lastPathComponent

        let boundary = "----FileFlussSynology\(UUID().uuidString)"

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

        var attempt = 0
        while true {
            let sid = try await ensureSession()
            let url = try buildURL(
                path: "/webapi/entry.cgi",
                queryItems: [
                    URLQueryItem(name: "api", value: "SYNO.FileStation.Upload"),
                    URLQueryItem(name: "version", value: "2"),
                    URLQueryItem(name: "method", value: "upload"),
                    URLQueryItem(name: "_sid", value: sid)
                ]
            )
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

            let progressDelegate = onBytes.map { ByteProgressDelegate(onBytes: $0) }
            let (data, response) = try await session.upload(for: request, fromFile: bodyFile, delegate: progressDelegate)
            try validateHTTP(response)
            let envelope = try JSONDecoder().decode(SynologyEnvelope<SynologyEmptyData>.self, from: data)
            if envelope.success { return }
            let code = envelope.error?.code ?? -1
            if code == 119 && attempt == 0 {
                // A session that expired while idle, as `call` handles it:
                // log in again and send the body once more, rather than
                // failing the upload and keeping the dead session id.
                self.sid = nil
                attempt += 1
                continue
            }
            throw mapAPIError(code: code)
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

        // A big folder takes the NAS minutes. Returning while the task still
        // runs (as after a minute before) reported a move as done — and the
        // caller went on to rename, or to delete the set-aside original —
        // before it had happened. So keep asking, slower after the first
        // minute, read the task's own error, and fail when it never ends.
        let clock = ContinuousClock()
        let deadline = clock.now + copyMoveTimeout
        var poll = 0
        while true {
            let status: SynologyTaskStatusData = try await call(
                api: "SYNO.FileStation.CopyMove",
                version: "3",
                method: "status",
                extra: [URLQueryItem(name: "taskid", value: taskid)]
            )
            if let code = status.errorCode, code != 0 {
                synologyLog.error("[Synology] CopyMove of \(path) failed with code \(code)")
                throw mapAPIError(code: code)
            }
            if status.finished == true { return }
            if clock.now >= deadline {
                synologyLog.error("[Synology] CopyMove of \(path) didn't finish in time")
                throw CloudProviderError.networkError(URLError(.timedOut))
            }
            let interval = poll < 120 ? pollInterval : max(pollInterval, .seconds(5))
            try await Task.sleep(for: interval)
            poll += 1
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
            ],
            notFoundPath: path
        )
        // A path that isn't there comes back as an entry carrying its own
        // error code (408) and little else. It has to read as "not found":
        // copy and move ask exactly that before landing a file.
        guard let first = result.files.first, (first.code ?? 0) == 0,
              let firstPath = first.path, let isdir = first.isdir
        else { throw CloudProviderError.notFound(path) }
        return CloudFileItem(
            id: firstPath,
            name: first.name ?? (firstPath as NSString).lastPathComponent,
            path: firstPath,
            isDirectory: isdir,
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
    private static var lanAddressNote: String { L10n.text("The NAS built this link from the address DSM thinks it has, so it may only work on your local network. Check DSM's external access settings if recipients can't open it.") }

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
        case 2000: return .commandFailed(L10n.text("The sharing link does not exist."))
        case 2001: return .commandFailed(L10n.text("The NAS has reached its maximum number of sharing links — delete some in File Station and try again."))
        case 2002: return .commandFailed(L10n.text("Failed to access the NAS's sharing links."))
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
    /// expiry (error 119) before giving up. With `notFoundPath`, DSM's 408
    /// ("no such file or directory") becomes `.notFound` for that path.
    private func call<T: Decodable>(
        api: String,
        version: String,
        method: String,
        extra: [URLQueryItem],
        notFoundPath: String? = nil
    ) async throws -> T {
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
            if code == 408, let notFoundPath {
                throw CloudProviderError.notFound(notFoundPath)
            }
            throw mapAPIError(code: code)
        }
    }

    private func buildURL(path: String, queryItems: [URLQueryItem]) throws -> URL {
        guard var components = URLComponents(string: credentials.serverURL) else {
            throw CloudProviderError.invalidResponse
        }
        components.path = path
        components.percentEncodedQuery = Self.encodeQuery(queryItems)
        guard let url = components.url else { throw CloudProviderError.invalidResponse }
        return url
    }

    /// Strict percent-encoding of every name and value: only the unreserved
    /// characters stay as they are. `URLComponents.queryItems` leaves `+`
    /// raw, and DSM's CGI reads a bare `+` as a space — deleting "a+b.txt"
    /// deleted "a b.txt". This covers every call, the login included.
    static func encodeQuery(_ items: [URLQueryItem]) -> String {
        StrictQueryEncoding.query(items.map { ($0.name, $0.value ?? "") })
    }

    private func validateHTTP(_ response: URLResponse) throws {
        guard let http = response as? HTTPURLResponse else { return }
        guard !(200..<300).contains(http.statusCode) else { return }
        synologyLog.error("[Synology] HTTP \(http.statusCode) from \(self.credentials.serverURL)")
        throw CloudProviderError.serverError(http.statusCode)
    }

    /// Internal rather than private: the provider compares a stored
    /// server URL against a freshly entered one before reusing a device
    /// token, and both have to be normalised the same way.
    static func normalizeServerURL(_ raw: String) -> String {
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
        case 400: return .commandFailed(L10n.text("No such account or incorrect password."))
        case 401: return .commandFailed(L10n.text("Account is disabled."))
        case 402: return .commandFailed(L10n.text("Permission denied."))
        case 403: return .commandFailed(L10n.text("This account requires a 2-factor authentication (OTP) code."))
        case 404: return .commandFailed(L10n.text("OTP code is incorrect."))
        case 405: return .commandFailed(L10n.text("OTP code authentication has failed too many times."))
        case 406: return .commandFailed(L10n.text("Synology administrator must enforce 2-factor authentication."))
        case 407: return .commandFailed(L10n.text("Maximum number of OTP retries reached. Try again later."))
        case 408: return .commandFailed(L10n.text("Password expired — please reset it from DSM."))
        case 409: return .commandFailed(L10n.text("Password must be changed at first login."))
        default: return .commandFailed(L10n.format("Synology login failed (code %d).", code))
        }
    }

    private func mapAPIError(code: Int) -> CloudProviderError {
        switch code {
        case 400: return .commandFailed(L10n.text("Invalid parameter for the request."))
        case 401: return .commandFailed(L10n.text("Unknown API method."))
        case 402: return .commandFailed(L10n.text("Invalid API method version."))
        case 403: return .commandFailed(L10n.text("Invalid API method."))
        case 405: return .commandFailed(L10n.text("Insufficient user privilege."))
        case 406: return .commandFailed(L10n.text("Connection time out."))
        case 407: return .commandFailed(L10n.text("Multiple login detected."))
        case 408: return .commandFailed(L10n.text("Request denied — too many connections."))
        case 119: return .notAuthenticated
        case 401_400, 1_400, 1_401, 1_402: return .commandFailed(L10n.text("Synology rejected the file path."))
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

    struct AuthData: Decodable {
        let sid: String?
        let did: String?
        let deviceId: String?
        enum CodingKeys: String, CodingKey {
            case sid, did
            case deviceId = "device_id"
        }
    }
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
    let files: [Entry]
    /// Like a listing entry, but everything is optional: an entry for a
    /// missing path has only `code` and the path that was asked for.
    struct Entry: Decodable {
        let name: String?
        let path: String?
        let isdir: Bool?
        let code: Int?
        let additional: SynologyListData.Additional?
    }
}

private struct SynologyCreateFolderData: Decodable {
    let folders: [SynologyListData.Entry]?
}

private struct SynologyTaskStartData: Decodable {
    let taskid: String?
}

private struct SynologyTaskStatusData: Decodable {
    let finished: Bool?
    /// The task's own failure, which DSM reports beside `finished` as
    /// `{"code": …}` (or a bare number) while the envelope says success.
    let errorCode: Int?

    enum CodingKeys: String, CodingKey { case finished, error }
    private struct ErrorObject: Decodable { let code: Int? }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        finished = try container.decodeIfPresent(Bool.self, forKey: .finished)
        if let number = try? container.decodeIfPresent(Int.self, forKey: .error) {
            errorCode = number
        } else if let object = try? container.decodeIfPresent(ErrorObject.self, forKey: .error) {
            errorCode = object.code
        } else {
            errorCode = nil
        }
    }
}

/// `additional.time` returns POSIX timestamps for atime/ctime/mtime.
private struct TimeBlock: Decodable {
    let mtime: TimeInterval?
    var mtimeDate: Date? {
        guard let mtime else { return nil }
        return Date(timeIntervalSince1970: mtime)
    }
}
