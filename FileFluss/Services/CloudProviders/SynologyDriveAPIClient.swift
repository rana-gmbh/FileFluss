import Foundation
import FileFlussCore
import os

private let synologyLog = Logger(subsystem: "com.rana.FileFluss", category: "synologyDrive")

struct SynologyDriveCredentials: Codable, Sendable {
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
actor SynologyDriveAPIClient {
    let credentials: SynologyDriveCredentials
    private let session: URLSession
    /// Session ID returned by `SYNO.API.Auth` on login. Re-login when nil
    /// or after the server reports it has expired.
    private var sid: String?

    init(credentials: SynologyDriveCredentials) {
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

    func userDisplayName() -> String { credentials.displayName }

    // MARK: - Authentication

    /// Validates credentials by logging in. Stores the SID for use by
    /// subsequent calls. `otp` is required when the account has 2FA on.
    static func authenticate(
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

    func listFolder(path: String) async throws -> [CloudFileItem] {
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

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
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

        let progressDelegate = onBytes.map { ByteProgressDelegate(onBytes: $0) }
        let (tmp, response) = try await session.download(for: URLRequest(url: url), delegate: progressDelegate)
        try validateHTTP(response)

        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tmp, to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
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

    func deleteItem(at path: String) async throws {
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

    func createFolder(at path: String) async throws {
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

    func renameItem(at path: String, to newName: String) async throws {
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
    func copyMove(path: String, toFolderPath destFolder: String, removeSrc: Bool) async throws {
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

    func getFileInfo(at path: String) async throws -> CloudFileItem {
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

    func folderSize(path: String) async throws -> Int64 {
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

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem]? {
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
final class SynologyTrustingDelegate: NSObject, URLSessionDelegate, URLSessionTaskDelegate, URLSessionDownloadDelegate, @unchecked Sendable {
    let host: String
    init(host: String) { self.host = host }

    func urlSession(
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
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {}
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
