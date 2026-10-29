import Foundation
import os

private let qnapLog = Logger(subsystem: "com.rana.FileFluss", category: "qnap")

public struct QNAPCredentials: Codable, Sendable {
    let serverURL: String
    let username: String
    let password: String
    let allowSelfSignedCertificate: Bool
    let displayName: String
    /// Identifies this app to QTS, which ties the tokens below to it and
    /// lists it under the account's login devices.
    let clientId: String
    /// "Remember me" token from `remme=1`. Replaying it logs in without the
    /// password or a 2-step code. Optional: QTS may not hand one out.
    var qtoken: String?
    /// Issued at a 2-step login with `dont_verify_2sv_again=1`. Sent with
    /// the password, it skips the code on later logins.
    var vtoken: String?
}

/// Talks to QNAP's File Station HTTP API (`authLogin.cgi` +
/// `filemanager/utilRequest.cgi`) on the user's NAS — the API QNAP documents
/// for third-party integrators and that its own Qfile apps use.
///
/// Paths are File Station's own: the first component is the shared folder
/// ("/Public/Photos/a.jpg"), and "/" lists the shared folders.
public actor QNAPAPIClient {
    private(set) var credentials: QNAPCredentials
    private let session: URLSession
    /// Session id from `authLogin.cgi`. Nil until the first call, and again
    /// after File Station reports the session gone.
    private var sid: String?

    private static let clientApp = "FileFluss"
    private static let listPageSize = 1000

    public init(credentials: QNAPCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.fileTransfer()
        if credentials.allowSelfSignedCertificate {
            self.session = URLSession(
                configuration: config,
                delegate: SynologyTrustingDelegate(host: Self.host(from: credentials.serverURL)),
                delegateQueue: nil
            )
        } else {
            self.session = URLSession(configuration: config)
        }
    }

    public func userDisplayName() -> String { credentials.displayName }

    // MARK: - Authentication

    /// Logs in and hands back the client that did it, so the session the
    /// login — possibly a 2-step one — established is the one that's used.
    /// `previous` are the tokens this account already holds; the caller
    /// passes them only when server and user are unchanged.
    static func authenticatedClient(
        serverURL: String,
        username: String,
        password: String,
        securityCode: String?,
        allowSelfSignedCertificate: Bool,
        previous: QNAPCredentials?
    ) async throws -> QNAPAPIClient {
        let normalized = normalizeServerURL(serverURL)
        let creds = QNAPCredentials(
            serverURL: normalized,
            username: username,
            password: password,
            allowSelfSignedCertificate: allowSelfSignedCertificate,
            displayName: "\(username)@\(host(from: normalized))",
            clientId: previous?.clientId ?? UUID().uuidString,
            // The qtoken stands in for the old password, so a password
            // change must not log in with it. The vtoken only skips the
            // 2-step code and still needs the right password.
            qtoken: nil,
            vtoken: previous?.vtoken
        )
        let client = QNAPAPIClient(credentials: creds)
        try await client.login(securityCode: securityCode, allowToken: false)
        return client
    }

    /// Gets a session id. A stored qtoken is tried first; when QTS rejects
    /// it (expired, revoked) the password login follows, with the vtoken so
    /// a 2-step account isn't asked for a code it can't give from here.
    private func login(securityCode: String? = nil, allowToken: Bool = true) async throws {
        let code = securityCode?.trimmingCharacters(in: .whitespaces) ?? ""

        if allowToken, code.isEmpty, let qtoken = credentials.qtoken, !qtoken.isEmpty {
            let reply = try await authLogin([
                ("user", credentials.username),
                ("qtoken", qtoken),
                ("remme", "1"),
                ("client_id", credentials.clientId),
                ("client_app", Self.clientApp),
            ])
            if let newSid = Self.sessionId(in: reply) {
                sid = newSid
                return
            }
            qnapLog.info("[QNAP] Stored login token was refused; signing in with the password")
            credentials.qtoken = nil
        }

        var fields: [(String, String)] = [
            ("user", credentials.username),
            ("pwd", Self.encodePassword(credentials.password)),
            ("remme", "1"),
            ("serviceKey", "1"),
            ("client_id", credentials.clientId),
            ("client_app", Self.clientApp),
        ]
        if !code.isEmpty {
            fields.append(("security_code", code))
            fields.append(("dont_verify_2sv_again", "1"))
        } else if let vtoken = credentials.vtoken, !vtoken.isEmpty {
            fields.append(("vtoken", vtoken))
        }

        let reply = try await authLogin(fields)
        guard let newSid = Self.sessionId(in: reply) else {
            throw Self.loginError(from: reply, sentCode: !code.isEmpty)
        }
        sid = newSid
        if let qtoken = reply["qtoken"], !qtoken.isEmpty { credentials.qtoken = qtoken }
        if let vtoken = reply["vtoken"], !vtoken.isEmpty { credentials.vtoken = vtoken }
        qnapLog.info("[QNAP] Logged in as \(self.credentials.username) at \(self.credentials.serverURL)")
    }

    /// POSTs the login form — keeping the password out of the URL, where
    /// proxies and server logs would keep it — and reads QTS's XML reply.
    private func authLogin(_ fields: [(String, String)]) async throws -> [String: String] {
        guard let url = URL(string: credentials.serverURL + "/cgi-bin/authLogin.cgi") else {
            throw CloudProviderError.invalidResponse
        }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.httpBody = Data(Self.formEncode(fields).utf8)
        let (data, response) = try await session.data(for: request)
        try validateHTTP(response)
        return QNAPXMLReader.topLevelValues(data)
    }

    /// The session id in an `authLogin.cgi` reply, or the reason there's
    /// none. `sentCode` says whether the login carried a 2-step code.
    public static func sessionId(fromLoginReply data: Data, sentCode: Bool) throws -> String {
        let reply = QNAPXMLReader.topLevelValues(data)
        guard let sid = sessionId(in: reply) else { throw loginError(from: reply, sentCode: sentCode) }
        return sid
    }

    static func sessionId(in reply: [String: String]) -> String? {
        guard reply["authPassed"] == "1", let sid = reply["authSid"], !sid.isEmpty else { return nil }
        return sid
    }

    static func loginError(from reply: [String: String], sentCode: Bool) -> CloudProviderError {
        if reply["need_2sv"] == "1" || reply["need_2_step_verification"] == "1" {
            return sentCode
                ? .commandFailed(L10n.text("The 2-step verification code is incorrect."))
                : .twoFactorRequired
        }
        switch reply["errorValue"] {
        case "-3", "-4":
            return .commandFailed(L10n.text("The password has expired. Change it in QTS, then sign in again."))
        default:
            if reply["user_enable"] == "0" {
                return .commandFailed(L10n.text("This account is disabled on the NAS."))
            }
            return .invalidCredentials
        }
    }

    /// `ezEncode` in QNAP's own login page is plain Base64 of the UTF-8 bytes.
    public static func encodePassword(_ password: String) -> String {
        Data(password.utf8).base64EncodedString()
    }

    /// Ends the session on the NAS. Best effort: the account is being
    /// removed either way.
    public func logout() async {
        guard let sid, let url = buildURL(path: "/cgi-bin/authLogout.cgi", query: [("sid", sid), ("logout", "1")]) else { return }
        _ = try? await session.data(for: URLRequest(url: url))
        self.sid = nil
    }

    private func ensureSession() async throws -> String {
        if let sid { return sid }
        try await login()
        guard let sid else { throw CloudProviderError.notAuthenticated }
        return sid
    }

    // MARK: - Listing

    public func listFolder(path: String) async throws -> [CloudFileItem] {
        let cleaned = Self.cleanPath(path)
        if cleaned == "/" { return try await listShares() }

        var items: [CloudFileItem] = []
        var start = 0
        // A page count cap guards against a firmware that ignores `start`.
        for _ in 0..<1000 {
            let data = try await call("get_list", [
                ("is_iso", "0"),
                ("list_mode", "all"),
                ("path", cleaned),
                ("dir", "ASC"),
                ("sort", "filename"),
                ("start", String(start)),
                ("limit", String(Self.listPageSize)),
                ("hidden_file", "1"),
                ("v", "1"),
            ])
            let page = try Self.items(fromListing: data, parentPath: cleaned)
            items.append(contentsOf: page.items)
            start += page.rawCount
            if page.rawCount < Self.listPageSize || start >= (page.total ?? 0) { break }
        }
        return items
    }

    /// The shared folders the account can see, as the top level.
    private func listShares() async throws -> [CloudFileItem] {
        let data = try await call("get_tree", [
            ("is_iso", "0"),
            ("node", "share_root"),
            ("hidden_file", "0"),
        ])
        return try Self.shares(fromTree: data)
    }

    /// One `get_list` page as panel rows. `rawCount` counts the rows QTS sent
    /// before system folders were dropped, which is what paging moves by.
    public static func items(fromListing data: Data, parentPath: String) throws -> (items: [CloudFileItem], rawCount: Int, total: Int?) {
        let json = try JSONSerialization.jsonObject(with: data)
        guard let object = json as? [String: Any] else { throw CloudProviderError.invalidResponse }
        let rows = object["datas"] as? [[String: Any]] ?? []
        let parent = parentPath.hasSuffix("/") ? parentPath : parentPath + "/"
        let items = rows.compactMap { row -> CloudFileItem? in
            guard let name = row["filename"] as? String, !name.isEmpty, !isSystemEntry(name) else { return nil }
            return item(from: row, path: parent + name, name: name)
        }
        return (items, rows.count, int(object["total"]))
    }

    /// `get_tree` with `node=share_root`: a bare array of shares.
    public static func shares(fromTree data: Data) throws -> [CloudFileItem] {
        let json = try JSONSerialization.jsonObject(with: data)
        guard let nodes = json as? [[String: Any]] else {
            if let object = json as? [String: Any], let status = int(object["status"]) {
                throw mapStatus(status)
            }
            throw CloudProviderError.invalidResponse
        }
        return nodes.compactMap { node in
            guard let id = node["id"] as? String, !id.isEmpty else { return nil }
            // Disc images, phones and optical drives show up as shares too;
            // only real shared folders hold files FileFluss can write to.
            if let kind = node["iconCls"] as? String, kind != "folder" { return nil }
            let name = (node["text"] as? String) ?? (id as NSString).lastPathComponent
            return CloudFileItem(
                id: id,
                name: name,
                path: id,
                isDirectory: true,
                size: 0,
                modificationDate: .distantPast,
                checksum: nil
            )
        }
    }

    private static func item(from row: [String: Any], path: String, name: String) -> CloudFileItem {
        let isFolder = int(row["isfolder"]) == 1
        return CloudFileItem(
            id: path,
            name: name,
            path: path,
            isDirectory: isFolder,
            size: isFolder ? 0 : (int64(row["filesize"]) ?? 0),
            modificationDate: modificationDate(row) ?? .distantPast,
            checksum: nil
        )
    }

    /// `epochmt` is UTC seconds. `mt` is the NAS's local wall-clock time and
    /// only a fallback: the NAS's time zone isn't known here.
    private static func modificationDate(_ row: [String: Any]) -> Date? {
        if let epoch = int64(row["epochmt"]), epoch > 0 {
            return Date(timeIntervalSince1970: TimeInterval(epoch))
        }
        if let epoch = int64(row["mt"]), epoch > 0 {
            return Date(timeIntervalSince1970: TimeInterval(epoch))
        }
        guard let raw = row["mt"] as? String, !raw.isEmpty else { return nil }
        let fmt = DateFormatter()
        fmt.locale = Locale(identifier: "en_US_POSIX")
        fmt.dateFormat = "yyyy/MM/dd HH:mm:ss"
        return fmt.date(from: raw)
    }

    /// QTS's own bookkeeping folders: the recycle bin, snapshots and the
    /// thumbnail caches File Station hides.
    static func isSystemEntry(_ name: String) -> Bool {
        name.hasPrefix("@") || name.hasPrefix(".@__") || name == ".streams"
    }

    // MARK: - File operations

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let (parent, name) = Self.split(remotePath)
        let sid = try await ensureSession()
        guard let url = buildURL(path: "/cgi-bin/filemanager/utilRequest.cgi", query: [
            ("func", "download"),
            ("sid", sid),
            ("isfolder", "0"),
            ("compress", "0"),
            ("source_path", parent),
            ("source_file", name),
            ("source_total", "1"),
        ]) else { throw CloudProviderError.invalidResponse }

        let (tmp, response) = try await session.downloadReportingProgress(for: URLRequest(url: url), onBytes: onBytes)
        do {
            try validateHTTP(response)
            // A refused download is still HTTP 200, carrying a JSON status
            // (or nothing) instead of the file.
            if let http = response as? HTTPURLResponse,
               http.value(forHTTPHeaderField: "Content-Disposition") == nil,
               let status = Self.statusInSmallJSON(at: tmp) {
                throw Self.mapStatus(status, path: remotePath)
            }
        } catch {
            try? FileManager.default.removeItem(at: tmp)
            throw error
        }
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tmp, to: localURL)
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let (parent, name) = Self.split(remotePath)
        let boundary = "----FileFlussQNAP\(UUID().uuidString)"

        // The multipart body goes to a temp file and is streamed from there,
        // so a large upload doesn't sit in memory.
        let bodyFile = FileManager.default.temporaryDirectory
            .appendingPathComponent("filefluss-qnap-\(UUID().uuidString).bin")
        FileManager.default.createFile(atPath: bodyFile.path, contents: nil)
        defer { try? FileManager.default.removeItem(at: bodyFile) }
        do {
            let handle = try FileHandle(forWritingTo: bodyFile)
            defer { try? handle.close() }
            let safeName = name.replacingOccurrences(of: "\"", with: "%22")
            try handle.write(contentsOf: Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"fileName\"\r\n\r\n\(name)\r\n".utf8))
            try handle.write(contentsOf: Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"\(safeName)\"\r\nContent-Type: application/octet-stream\r\n\r\n".utf8))
            let input = try FileHandle(forReadingFrom: localURL)
            defer { try? input.close() }
            while let chunk = try input.read(upToCount: 1 << 20), !chunk.isEmpty {
                try handle.write(contentsOf: chunk)
            }
            try handle.write(contentsOf: Data("\r\n--\(boundary)--\r\n".utf8))
        }

        var attempt = 0
        while true {
            let sid = try await ensureSession()
            guard let url = buildURL(path: "/cgi-bin/filemanager/utilRequest.cgi", query: [
                ("func", "upload"),
                ("type", "standard"),
                ("sid", sid),
                ("dest_path", parent),
                ("overwrite", "1"),
                // File Station's progress key: the destination path with
                // every "/" turned into "-".
                ("progress", remotePath.replacingOccurrences(of: "/", with: "-")),
            ]) else { throw CloudProviderError.invalidResponse }
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

            let (data, response) = try await session.uploadReportingProgress(for: request, fromFile: bodyFile, onBytes: onBytes)
            try validateHTTP(response)
            let status = Self.status(in: data)
            if status == nil || status == 1 { return }
            if status == 3, attempt == 0 {
                self.sid = nil
                attempt += 1
                continue
            }
            throw Self.mapStatus(status ?? 0, path: remotePath)
        }
    }

    public func deleteItem(at path: String) async throws {
        let (parent, name) = Self.split(path)
        _ = try await call("delete", [
            ("path", parent),
            ("file_total", "1"),
            ("file_name", name),
        ], path: path)
    }

    public func createFolder(at path: String) async throws {
        let (parent, name) = Self.split(path)
        do {
            _ = try await call("createdir", [
                ("dest_folder", name),
                ("dest_path", parent),
            ], path: path)
        } catch CloudProviderError.commandFailed(let message) where message == Self.alreadyExistsMessage {
            // Creating a folder that's already there is what a sync wants.
        }
    }

    public func renameItem(at path: String, to newName: String) async throws {
        let (parent, name) = Self.split(path)
        _ = try await call("rename", [
            ("path", parent),
            ("source_name", name),
            ("dest_name", newName),
        ], path: path)
    }

    /// Server-side copy or move into `destFolder`, keeping the source name.
    /// `mode=0` overwrites what's there, as every other provider does.
    public func copyMove(path: String, toFolderPath destFolder: String, move: Bool) async throws {
        let (parent, name) = Self.split(path)
        _ = try await call(move ? "move" : "copy", [
            ("source_file", name),
            ("source_total", "1"),
            ("source_path", parent),
            ("dest_path", Self.cleanPath(destFolder)),
            ("mode", "0"),
        ], path: path)
        // QTS may hand back a process id and finish in the background; the
        // destination has to exist before a follow-up rename can find it.
        let destination = Self.join(Self.cleanPath(destFolder), name)
        for _ in 0..<120 {
            if (try? await getFileInfo(at: destination)) != nil { return }
            try? await Task.sleep(nanoseconds: 500_000_000)
        }
    }

    public func setModificationDate(at path: String, to date: Date) async throws {
        let (parent, name) = Self.split(path)
        _ = try await call("stat", [
            ("settime", "1"),
            ("mtime", String(Int64(date.timeIntervalSince1970))),
            ("path", parent),
            ("file_total", "1"),
            ("file_name", name),
        ], path: path)
    }

    public func getFileInfo(at path: String) async throws -> CloudFileItem {
        let cleaned = Self.cleanPath(path)
        if cleaned == "/" {
            return CloudFileItem(id: "/", name: "/", path: "/", isDirectory: true, size: 0, modificationDate: .distantPast, checksum: nil)
        }
        let (parent, name) = Self.split(cleaned)
        let data = try await call("stat", [
            ("path", parent),
            ("file_total", "1"),
            ("file_name", name),
        ], path: path)
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let row = (object["datas"] as? [[String: Any]])?.first,
              Self.int(row["exist"]) != 0
        else { throw CloudProviderError.notFound(path) }
        // A share root has no parent folder to stat it in; QTS answers with
        // an empty name, which still means "there".
        return Self.item(from: row, path: cleaned, name: name)
    }

    public func folderSize(path: String) async throws -> Int64 {
        let (parent, name) = Self.split(path)
        if let data = try? await call("get_file_size", [
            ("path", parent),
            ("total", "1"),
            ("name", name),
        ], path: path),
           let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let size = Self.int64(object["size"]) {
            return size
        }
        // Large folders are sized in the background and answered with a
        // process id instead; count the files ourselves then, capped so a
        // giant share doesn't keep the NAS busy for hours.
        var total: Int64 = 0
        var queue = [path]
        var seen = 0
        while let next = queue.first {
            queue.removeFirst()
            for item in try await listFolder(path: next) {
                seen += 1
                if seen > 5000 { return total }
                if item.isDirectory { queue.append(item.path) } else { total += item.size }
            }
        }
        return total
    }

    /// File Station's search across `path` (or every shared folder), by name.
    public func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
        let folders: [String]
        if let path, Self.cleanPath(path) != "/" {
            folders = [Self.cleanPath(path)]
        } else {
            folders = try await listShares().map(\.path)
        }
        guard !folders.isEmpty else { return [] }
        var params: [(String, String)] = [
            ("folderCount", String(folders.count)),
            ("keyword", query),
            ("searchType", "0"),
            ("fileSizeType", "0"),
            ("ownerType", "0"),
            ("dateType", "0"),
            ("dateField", "0"),
            ("hidden_file", "1"),
            ("v", "1"),
            ("start", "0"),
            ("limit", "200"),
            ("sort", "filename"),
            ("dir", "ASC"),
        ]
        folders.forEach { params.append(("folders", $0)) }
        let data = try await call("search_ext", params)
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw CloudProviderError.invalidResponse
        }
        let rows = object["datas"] as? [[String: Any]] ?? []
        let base = folders.count == 1 ? folders[0] : "/"
        return rows.compactMap { row in
            guard let raw = row["filename"] as? String, !raw.isEmpty else { return nil }
            let fullPath = raw.hasPrefix("/") ? raw : Self.join(base, raw)
            let components = fullPath.split(separator: "/").map(String.init)
            guard let name = components.last, !components.contains(where: Self.isSystemEntry) else { return nil }
            return Self.item(from: row, path: fullPath, name: name)
        }
    }

    // MARK: - Share links

    /// Creates a File Station share link for one item. QTS builds the URL
    /// from the host name it's given, so the address this account connects
    /// with is passed — otherwise it would use the NAS's LAN address.
    public func createShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        let (parent, name) = Self.split(path)
        let components = URLComponents(string: credentials.serverURL)
        var query: [(String, String)] = [
            ("c", "1"),
            ("path", parent),
            ("file_total", "1"),
            ("file_name", name),
            ("hostname", components?.host ?? ""),
            ("ssl", components?.scheme?.lowercased() == "https" ? "true" : "false"),
            ("expire_time", options.expiry.map { String(Int64($0.timeIntervalSince1970)) } ?? "0"),
        ]
        if let password = options.password, !password.isEmpty {
            query.append(("access_enabled", "true"))
            query.append(("access_code", password))
        }
        let data = try await call("get_share_link", query, path: path)
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let link = (object["links"] as? [[String: Any]])?.first,
              let raw = link["link_url"] as? String,
              let url = URL(string: raw)
        else { throw CloudProviderError.invalidResponse }
        return CloudShareLink(
            url: url,
            expiresAt: options.expiry,
            hasPassword: !(options.password ?? "").isEmpty,
            note: Self.lanAddressNote
        )
    }

    public func existingShareLink(at path: String) async throws -> CloudShareLink? {
        guard let entry = try await findShareLink(at: path) else { return nil }
        guard let raw = entry["download_link"] as? String, let url = URL(string: raw) else {
            throw CloudProviderError.invalidResponse
        }
        let expiry = Self.int64(entry["expire_time"]).flatMap { $0 > 0 ? Date(timeIntervalSince1970: TimeInterval($0)) : nil }
        let code = (entry["access_code"] as? String) ?? ""
        return CloudShareLink(
            url: url,
            expiresAt: expiry,
            // "----" is how File Station shows "no access code".
            hasPassword: !code.isEmpty && code != "----",
            note: Self.lanAddressNote
        )
    }

    public func removeShareLink(at path: String) async throws {
        guard let ssid = try await findShareLink(at: path)?["ssid"] as? String else {
            throw CloudProviderError.commandFailed(L10n.text("This item doesn't have a public link."))
        }
        _ = try await call("delete_share", [
            ("file_total", "1"),
            ("ssids", ssid),
        ], path: path)
    }

    /// Pages through the account's links for the one on `path`. QTS has no
    /// lookup by path.
    private func findShareLink(at path: String) async throws -> [String: Any]? {
        let target = Self.cleanPath(path)
        let pageSize = 200
        var start = 0
        for _ in 0..<50 {
            let data = try await call("get_share_list", [
                ("dir", "ASC"),
                ("sort", "filename"),
                ("start", String(start)),
                ("limit", String(pageSize)),
            ])
            guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                throw CloudProviderError.invalidResponse
            }
            let rows = object["datas"] as? [[String: Any]] ?? []
            // Links to several items at once have no single path to match.
            if let hit = rows.first(where: { ($0["filename"] as? String) == target && Self.int($0["multiple"]) != 1 }) {
                return hit
            }
            start += rows.count
            if rows.count < pageSize { return nil }
            if let total = Self.int(object["total"]), start >= total { return nil }
        }
        return nil
    }

    private static let lanAddressNote = "The NAS built this link from the address you connect to it with. If that's a local address, recipients outside your network can't open it — set up myQNAPcloud or port forwarding for that."

    // MARK: - Plumbing

    /// One `utilRequest.cgi` call. A `status` other than 1 is an error;
    /// listings carry no status at all when they succeed. A lost session
    /// (status 3) logs in again once.
    private func call(_ function: String, _ params: [(String, String)], path: String? = nil) async throws -> Data {
        var attempt = 0
        while true {
            let sid = try await ensureSession()
            guard let url = buildURL(
                path: "/cgi-bin/filemanager/utilRequest.cgi",
                query: [("func", function), ("sid", sid)] + params
            ) else { throw CloudProviderError.invalidResponse }

            let (data, response) = try await session.data(for: URLRequest(url: url))
            try validateHTTP(response)
            guard let status = Self.status(in: data), status != 1 else { return data }
            if status == 3, attempt == 0 {
                self.sid = nil
                attempt += 1
                continue
            }
            qnapLog.error("[QNAP] \(function) → status \(status)")
            throw Self.mapStatus(status, path: path)
        }
    }

    /// The `status` of a JSON object reply, as a number or a string.
    static func status(in data: Data) -> Int? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        return int(object["status"])
    }

    /// Reads a downloaded file as a File Station status reply when it's
    /// small enough to be one.
    private static func statusInSmallJSON(at url: URL) -> Int? {
        guard let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size]) as? NSNumber,
              size.intValue < 512,
              let data = try? Data(contentsOf: url),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              object.count <= 4
        else { return nil }
        return int(object["status"])
    }

    private static let alreadyExistsMessage = "An item with this name already exists on the NAS."

    /// File Station's status codes, from QNAP's API reference.
    public static func mapStatus(_ status: Int, path: String? = nil) -> CloudProviderError {
        switch status {
        case 2, 33: return .commandFailed(alreadyExistsMessage)
        case 3: return .notAuthenticated
        case 4, 10, 11: return .unauthorized
        case 5: return .notFound(path ?? "")
        case 7: return .commandFailed("The NAS couldn't write the file.")
        case 8: return .commandFailed("File Station is turned off on the NAS.")
        case 9: return .quotaExceeded
        case 12: return .commandFailed("The NAS doesn't allow this name.")
        case 19, 21: return .commandFailed("The NAS is busy. Try again in a moment.")
        default: return .serverError(status)
        }
    }

    private func buildURL(path: String, query: [(String, String)]) -> URL? {
        guard var components = URLComponents(string: credentials.serverURL) else { return nil }
        components.path = path
        components.percentEncodedQuery = Self.formEncode(query)
        return components.url
    }

    /// Strict percent-encoding. `URLComponents` leaves "+" alone, which
    /// QTS reads as a space — "a+b.txt" would become "a b.txt".
    public static func formEncode(_ fields: [(String, String)]) -> String {
        // ASCII only: `CharacterSet.alphanumerics` would let "ü" through raw.
        let allowed = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
        return fields.map { key, value in
            let k = key.addingPercentEncoding(withAllowedCharacters: allowed) ?? key
            let v = value.addingPercentEncoding(withAllowedCharacters: allowed) ?? value
            return "\(k)=\(v)"
        }.joined(separator: "&")
    }

    private func validateHTTP(_ response: URLResponse) throws {
        guard let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) else { return }
        qnapLog.error("[QNAP] HTTP \(http.statusCode) from \(self.credentials.serverURL)")
        throw CloudProviderError.serverError(http.statusCode)
    }

    static func cleanPath(_ path: String) -> String {
        var p = path.isEmpty ? "/" : path
        if !p.hasPrefix("/") { p = "/" + p }
        while p.count > 1 && p.hasSuffix("/") { p.removeLast() }
        return p
    }

    /// Parent folder and name, the pair File Station's calls take.
    static func split(_ path: String) -> (parent: String, name: String) {
        let cleaned = cleanPath(path)
        let parent = (cleaned as NSString).deletingLastPathComponent
        return (parent.isEmpty ? "/" : parent, (cleaned as NSString).lastPathComponent)
    }

    static func join(_ folder: String, _ name: String) -> String {
        folder.hasSuffix("/") ? folder + name : folder + "/" + name
    }

    /// Adds `https://` when no scheme was typed, and QTS's HTTP port 8080
    /// for a plain-HTTP address without one. HTTPS keeps the standard 443,
    /// QTS's default for secure access.
    public static func normalizeServerURL(_ raw: String) -> String {
        var s = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if !s.lowercased().hasPrefix("http://") && !s.lowercased().hasPrefix("https://") {
            s = "https://" + s
        }
        while s.hasSuffix("/") { s.removeLast() }
        if var components = URLComponents(string: s), components.port == nil, components.scheme?.lowercased() == "http" {
            components.port = 8080
            s = components.string ?? s
        }
        return s
    }

    private static func host(from serverURL: String) -> String {
        URLComponents(string: serverURL)?.host ?? "qnap"
    }

    /// QTS sends numbers as numbers or strings, depending on the field and
    /// the firmware.
    static func int(_ value: Any?) -> Int? {
        if let n = value as? Int { return n }
        if let n = value as? NSNumber { return n.intValue }
        if let s = value as? String { return Int(s) }
        return nil
    }

    static func int64(_ value: Any?) -> Int64? {
        if let n = value as? Int64 { return n }
        if let n = value as? NSNumber { return n.int64Value }
        if let s = value as? String { return Int64(s) }
        return nil
    }
}

/// Collects the text of each direct child of `<QDocRoot>` — the shape of
/// every `authLogin.cgi` reply. Nested elements are ignored.
enum QNAPXMLReader {
    static func topLevelValues(_ data: Data) -> [String: String] {
        let delegate = Delegate()
        let parser = XMLParser(data: data)
        parser.delegate = delegate
        parser.parse()
        return delegate.values
    }

    private final class Delegate: NSObject, XMLParserDelegate {
        var values: [String: String] = [:]
        private var depth = 0
        private var text = ""

        func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName: String?, attributes: [String: String] = [:]) {
            depth += 1
            if depth == 2 { text = "" }
        }

        func parser(_ parser: XMLParser, foundCharacters string: String) {
            if depth == 2 { text += string }
        }

        func parser(_ parser: XMLParser, foundCDATA CDATABlock: Data) {
            if depth == 2, let s = String(data: CDATABlock, encoding: .utf8) { text += s }
        }

        func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName: String?) {
            if depth == 2 { values[elementName] = text.trimmingCharacters(in: .whitespacesAndNewlines) }
            depth -= 1
        }
    }
}
