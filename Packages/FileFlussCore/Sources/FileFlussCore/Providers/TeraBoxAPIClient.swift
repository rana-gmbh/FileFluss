import Foundation
import CryptoKit
import os

private let teraboxLog = Logger(subsystem: "com.rana.FileFluss", category: "terabox")

// MARK: - Application credentials (TeraBox Open Platform)
//
// These three are issued by TeraBox when an integration is approved — they are
// NOT self-serve. Until TeraBox provisions them, login cannot succeed. Fill in
// the values you receive (AppKey / SecretKey / private secret) here.
enum TeraBoxAppCredentials {
    static let clientID = "YOUR_TERABOX_CLIENT_ID"        // AppKey
    static let clientSecret = "YOUR_TERABOX_CLIENT_SECRET" // SecretKey
    static let privateSecret = "YOUR_TERABOX_PRIVATE_SECRET" // used only for the sign
    /// Fixed app id required by the shard-upload endpoint per the docs.
    static let uploadAppID = "250528"

    static var isConfigured: Bool {
        !clientID.hasPrefix("YOUR_") && !clientSecret.hasPrefix("YOUR_") && !privateSecret.hasPrefix("YOUR_")
    }
}

/// Persisted post-login state. TeraBox tokens are short-lived (access 2 days,
/// refresh 30 days, single-use), so we keep the refresh token and per-user
/// domains and refresh on demand.
public struct TeraBoxCredentials: Codable, Sendable {
    public var accessToken: String
    public var refreshToken: String
    public var expiresAt: Date
    /// Domain for all basic APIs except the shard upload (from /oauth/tokeninfo).
    public var apiDomain: String
    /// Domain for the /rest/2.0/pcs/superfile2 shard upload.
    public var uploadDomain: String
    public var userID: Int64
}

/// Device-code handshake payload handed to the UI: show the QR, poll for the token.
public struct TeraBoxDeviceCode: Sendable {
    public let deviceCode: String
    /// Decoded PNG bytes of the QR the user scans in the TeraBox app.
    public let qrImageData: Data?
    public let interval: Int
    public let expiresIn: Int
}

public actor TeraBoxAPIClient {
    private let authHost = "https://www.terabox.com"
    public private(set) var credentials: TeraBoxCredentials
    private let session: URLSession

    public init(credentials: TeraBoxCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 600
        self.session = URLSession(configuration: config)
    }

    // MARK: - Device-code authentication

    /// Step 1: request a device code + QR. Only needs the client id.
    public static func requestDeviceCode() async throws -> TeraBoxDeviceCode {
        guard TeraBoxAppCredentials.isConfigured else { throw CloudProviderError.notImplemented }
        let session = URLSession(configuration: .default)
        let url = URL(string: "https://www.terabox.com/oauth/devicecode?client_id=\(TeraBoxAppCredentials.clientID)")!
        let (data, resp) = try await session.data(from: url)
        try checkHTTP(resp, data)
        struct Response: Decodable {
            let data: Payload
            struct Payload: Decodable { let device_code: String; let qrcode_url: String; let expires_in: Int; let interval: Int }
        }
        let decoded = try JSONDecoder().decode(Response.self, from: data)
        return TeraBoxDeviceCode(
            deviceCode: decoded.data.device_code,
            qrImageData: Self.decodeDataURIPNG(decoded.data.qrcode_url),
            interval: decoded.data.interval,
            expiresIn: decoded.data.expires_in
        )
    }

    /// Step 2: poll /oauth/gettoken until the user authorizes in the TeraBox app.
    public static func pollForToken(_ device: TeraBoxDeviceCode) async throws -> TeraBoxCredentials {
        let session = URLSession(configuration: .default)
        let deadline = Date().addingTimeInterval(TimeInterval(device.expiresIn))
        let interval = max(2, device.interval)

        while Date() < deadline {
            let tokens = try? await exchange(session: session, grantType: "device_code", code: device.deviceCode)
            if let tokens {
                return try await Self.completeWithTokenInfo(session: session, tokens: tokens)
            }
            try? await Task.sleep(nanoseconds: UInt64(interval) * 1_000_000_000)
        }
        throw CloudProviderError.commandFailed("Authorization timed out — the QR code expired before it was scanned.")
    }

    /// Authorization-code variant (kept for completeness if a redirect flow is added later).
    public static func exchangeAuthorizationCode(_ code: String) async throws -> TeraBoxCredentials {
        let session = URLSession(configuration: .default)
        let tokens = try await exchange(session: session, grantType: "authorization_code", code: code)
        return try await completeWithTokenInfo(session: session, tokens: tokens)
    }

    private struct TokenResponse: Decodable { let access_token: String; let refresh_token: String; let expires_in: Int }

    private static func exchange(session: URLSession, grantType: String, code: String) async throws -> TokenResponse {
        let timestamp = Int(Date().timeIntervalSince1970)
        let sign = Self.sign(timestamp: timestamp)
        let fields = [
            "client_id": TeraBoxAppCredentials.clientID,
            "client_secret": TeraBoxAppCredentials.clientSecret,
            "grant_type": grantType,
            "code": code,
            "timestamp": "\(timestamp)",
            "sign": sign,
        ]
        var request = URLRequest(url: URL(string: "https://www.terabox.com/oauth/gettoken")!)
        request.httpMethod = "POST"
        Self.attachMultipart(&request, fields: fields)
        let (data, resp) = try await session.data(for: request)
        guard let http = resp as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
        struct Envelope: Decodable { let errno: Int; let data: TokenResponse? }
        let env = try JSONDecoder().decode(Envelope.self, from: data)
        // 400001 = user hasn't authorized yet (device mode) — caller retries.
        guard http.statusCode == 200, env.errno == 0, let token = env.data else {
            throw Self.mapAuthError(env.errno)
        }
        return token
    }

    private static func completeWithTokenInfo(session: URLSession, tokens: TokenResponse) async throws -> TeraBoxCredentials {
        var request = URLRequest(url: URL(string: "https://www.terabox.com/oauth/tokeninfo")!)
        request.httpMethod = "POST"
        Self.attachMultipart(&request, fields: ["access_token": tokens.access_token])
        let (data, resp) = try await session.data(for: request)
        try checkHTTP(resp, data)
        struct Info: Decodable {
            let data: Payload
            struct Payload: Decodable { let api_domain: String; let upload_domain: String; let user_id: Int64 }
        }
        let info = try JSONDecoder().decode(Info.self, from: data)
        return TeraBoxCredentials(
            accessToken: tokens.access_token,
            refreshToken: tokens.refresh_token,
            expiresAt: Date().addingTimeInterval(TimeInterval(tokens.expires_in)),
            apiDomain: info.data.api_domain,
            uploadDomain: info.data.upload_domain,
            userID: info.data.user_id
        )
    }

    /// Refresh when the access token is near expiry. Each refresh token is
    /// single-use, so we persist the new pair via the provider's keychain save.
    public func refreshIfNeeded() async throws {
        guard Date() >= credentials.expiresAt.addingTimeInterval(-300) else { return }
        let timestamp = Int(Date().timeIntervalSince1970)
        let fields = [
            "client_id": TeraBoxAppCredentials.clientID,
            "client_secret": TeraBoxAppCredentials.clientSecret,
            "refresh_token": credentials.refreshToken,
            "timestamp": "\(timestamp)",
            "sign": Self.sign(timestamp: timestamp),
        ]
        var request = URLRequest(url: URL(string: "\(authHost)/oauth/refreshtoken")!)
        request.httpMethod = "POST"
        Self.attachMultipart(&request, fields: fields)
        let (data, resp) = try await session.data(for: request)
        try Self.checkHTTP(resp, data)
        struct Envelope: Decodable { let errno: Int; let data: TokenResponse? }
        let env = try JSONDecoder().decode(Envelope.self, from: data)
        guard env.errno == 0, let token = env.data else { throw Self.mapAuthError(env.errno) }
        credentials.accessToken = token.access_token
        credentials.refreshToken = token.refresh_token
        credentials.expiresAt = Date().addingTimeInterval(TimeInterval(token.expires_in))
    }

    /// Signature for the token endpoints: md5(client_id_timestamp_client_secret_private_secret).
    private static func sign(timestamp: Int) -> String {
        let raw = "\(TeraBoxAppCredentials.clientID)_\(timestamp)_\(TeraBoxAppCredentials.clientSecret)_\(TeraBoxAppCredentials.privateSecret)"
        return Insecure.MD5.hash(data: Data(raw.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    public func currentCredentials() -> TeraBoxCredentials { credentials }
    public func userDisplayName() -> String { "TeraBox (\(credentials.userID))" }

    // MARK: - Listing

    public func listFolder(path: String) async throws -> [CloudFileItem] {
        try await refreshIfNeeded()
        var items: [CloudFileItem] = []
        var page = 1
        while true {
            let dir = path.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? path
            let data = try await apiGet("/openapi/api/list?dir=\(dir)&order=name&desc=0&num=1000&page=\(page)")
            struct ListResponse: Decodable { let list: [Entry]?; struct Entry: Decodable {
                let server_filename: String; let isdir: Int; let size: Int64?; let fs_id: Int64
                let path: String; let server_mtime: Int64?; let md5: String?
            } }
            let decoded = try JSONDecoder().decode(ListResponse.self, from: data)
            let entries = decoded.list ?? []
            for e in entries {
                items.append(CloudFileItem(
                    id: "\(e.fs_id)",
                    name: e.server_filename,
                    path: e.path,
                    isDirectory: e.isdir == 1,
                    size: e.size ?? 0,
                    modificationDate: e.server_mtime.map { Date(timeIntervalSince1970: TimeInterval($0)) } ?? .distantPast,
                    checksum: e.md5
                ))
            }
            if entries.count < 1000 { break }
            page += 1
        }
        return items
    }

    // MARK: - Download

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        try await refreshIfNeeded()
        // Resolve the file's download link by path.
        let target = "[\"\(remotePath.addingPercentEncoding(withAllowedCharacters: .urlHostAllowed) ?? remotePath)\"]"
        let encodedTarget = target.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? target
        let data = try await apiGet("/openapi/api/filemetas?target=\(encodedTarget)&dlink=1")
        struct MetaResponse: Decodable { let info: [Info]?; struct Info: Decodable { let dlink: String? } }
        let meta = try JSONDecoder().decode(MetaResponse.self, from: data)
        guard let dlink = meta.info?.first?.dlink, !dlink.isEmpty else { throw CloudProviderError.notFound(remotePath) }

        // The dlink requires the access token appended, and a TeraBox UA.
        let separator = dlink.contains("?") ? "&" : "?"
        guard let url = URL(string: "\(dlink)\(separator)access_tokens=\(credentials.accessToken)") else {
            throw CloudProviderError.invalidResponse
        }
        var request = URLRequest(url: url)
        request.setValue("pan.baidu.com", forHTTPHeaderField: "User-Agent")
        let (tempURL, resp) = try await session.download(for: request)
        guard (resp as? HTTPURLResponse).map({ (200...299).contains($0.statusCode) }) == true else {
            throw CloudProviderError.serverError((resp as? HTTPURLResponse)?.statusCode ?? 0)
        }
        onBytes?((try? FileManager.default.attributesOfItem(atPath: tempURL.path)[.size] as? Int64 ?? 0) ?? 0)
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    // MARK: - Upload (precreate → superfile2 → create)

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        try await refreshIfNeeded()
        let fileData = try Data(contentsOf: localURL)
        let sliceSize = 4 * 1024 * 1024 // 4 MB; the API requires ≥4 MB per non-final shard.

        // Split into shards and MD5 each.
        var shards: [Data] = []
        var offset = 0
        while offset < fileData.count {
            let end = min(offset + sliceSize, fileData.count)
            shards.append(fileData.subdata(in: offset..<end))
            offset = end
        }
        if shards.isEmpty { shards = [Data()] } // zero-byte file → one empty shard
        let blockMD5s = shards.map { Self.md5Hex($0) }

        // 1. precreate
        let pathEnc = remotePath
        var precreateFields = [
            "path": pathEnc,
            "autoinit": "1",
            "block_list": Self.jsonArray(blockMD5s),
        ]
        let precreateData = try await apiPost("/openapi/api/precreate", fields: precreateFields)
        struct Precreate: Decodable { let uploadid: String?; let return_type: Int? }
        let pre = try JSONDecoder().decode(Precreate.self, from: precreateData)
        if pre.return_type == 2 { return } // already exists in cloud (rapid upload) — nothing to do
        guard let uploadid = pre.uploadid else { throw CloudProviderError.invalidResponse }
        precreateFields.removeAll()

        // 2. upload each shard via the upload_domain superfile2 endpoint
        for (index, shard) in shards.enumerated() {
            let pathParam = remotePath.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? remotePath
            let uploadidParam = uploadid.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? uploadid
            let urlString = "https://\(credentials.uploadDomain)/rest/2.0/pcs/superfile2?method=upload&app_id=\(TeraBoxAppCredentials.uploadAppID)&path=\(pathParam)&uploadid=\(uploadidParam)&partseq=\(index)&access_tokens=\(credentials.accessToken)"
            guard let url = URL(string: urlString) else { throw CloudProviderError.invalidResponse }
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            let body = Self.multipartFileBody(fieldName: "file", filename: "blob", fileData: shard, boundary: Self.boundary)
            request.setValue("multipart/form-data; boundary=\(Self.boundary)", forHTTPHeaderField: "Content-Type")
            let (data, resp) = try await session.upload(for: request, from: body)
            guard (resp as? HTTPURLResponse).map({ (200...299).contains($0.statusCode) }) == true else {
                let bodyStr = String(data: data, encoding: .utf8) ?? ""
                teraboxLog.error("[TeraBox] superfile2 shard \(index) failed: \(bodyStr.prefix(300))")
                throw CloudProviderError.serverError((resp as? HTTPURLResponse)?.statusCode ?? 0)
            }
            onBytes?(Int64(shard.count))
        }

        // 3. create (merge)
        let createFields = [
            "path": pathEnc,
            "size": "\(fileData.count)",
            "uploadid": uploadid,
            "block_list": Self.jsonArray(blockMD5s),
            "rtype": "3", // overwrite
        ]
        let createData = try await apiPost("/openapi/api/create", fields: createFields)
        try Self.checkErrno(createData)
    }

    // MARK: - Mutations

    /// Best-effort directory create. TeraBox doesn't document a dedicated mkdir;
    /// this mirrors the Baidu xpan convention (create with isdir=1).
    public func createFolder(at path: String) async throws {
        try await refreshIfNeeded()
        let fields = ["path": path, "isdir": "1", "rtype": "0"]
        let data = try await apiPost("/openapi/api/create", fields: fields)
        try Self.checkErrno(data)
    }

    public func deleteItem(at path: String) async throws {
        try await refreshIfNeeded()
        let data = try await apiPost("/openapi/api/filemanager?opera=delete", fields: ["filelist": Self.jsonArray([path])])
        try Self.checkErrno(data)
    }

    public func renameItem(at path: String, to newName: String) async throws {
        try await refreshIfNeeded()
        let entry = "[{\"path\":\"\(path)\",\"newname\":\"\(newName)\"}]"
        let data = try await apiPost("/openapi/api/filemanager?opera=rename", fields: ["filelist": entry])
        try Self.checkErrno(data)
    }

    public func moveItem(at path: String, toPath newPath: String) async throws {
        try await refreshIfNeeded()
        let dest = (newPath as NSString).deletingLastPathComponent
        let newName = (newPath as NSString).lastPathComponent
        let entry = "[{\"path\":\"\(path)\",\"dest\":\"\(dest)\",\"newname\":\"\(newName)\"}]"
        let data = try await apiPost("/openapi/api/filemanager?opera=move", fields: ["filelist": entry])
        try Self.checkErrno(data)
    }

    // MARK: - Quota

    public func storageQuota() async throws -> CloudStorageQuota? {
        try await refreshIfNeeded()
        guard let data = try? await apiGet("/openapi/api/quota") else { return nil }
        struct Quota: Decodable { let total: Int64?; let used: Int64? }
        guard let q = try? JSONDecoder().decode(Quota.self, from: data) else { return nil }
        return CloudStorageQuota(usedBytes: q.used ?? 0, totalBytes: q.total)
    }

    // MARK: - HTTP helpers

    private func apiGet(_ endpoint: String) async throws -> Data {
        let sep = endpoint.contains("?") ? "&" : "?"
        let urlString = "https://\(credentials.apiDomain)\(endpoint)\(sep)access_tokens=\(credentials.accessToken)"
        guard let url = URL(string: urlString) else { throw CloudProviderError.invalidResponse }
        let (data, resp) = try await session.data(from: url)
        try Self.checkHTTP(resp, data)
        return data
    }

    private func apiPost(_ endpoint: String, fields: [String: String]) async throws -> Data {
        let sep = endpoint.contains("?") ? "&" : "?"
        let urlString = "https://\(credentials.apiDomain)\(endpoint)\(sep)access_tokens=\(credentials.accessToken)"
        guard let url = URL(string: urlString) else { throw CloudProviderError.invalidResponse }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.httpBody = Self.formURLEncoded(fields)
        let (data, resp) = try await session.data(for: request)
        try Self.checkHTTP(resp, data)
        return data
    }

    // MARK: - Encoding helpers

    private static let boundary = "----FileFlussTeraBoxBoundary"

    private static func attachMultipart(_ request: inout URLRequest, fields: [String: String]) {
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        var body = Data()
        for (k, v) in fields {
            body.append("--\(boundary)\r\n".data(using: .utf8)!)
            body.append("Content-Disposition: form-data; name=\"\(k)\"\r\n\r\n".data(using: .utf8)!)
            body.append("\(v)\r\n".data(using: .utf8)!)
        }
        body.append("--\(boundary)--\r\n".data(using: .utf8)!)
        request.httpBody = body
    }

    private static func multipartFileBody(fieldName: String, filename: String, fileData: Data, boundary: String) -> Data {
        var body = Data()
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"\(fieldName)\"; filename=\"\(filename)\"\r\n".data(using: .utf8)!)
        body.append("Content-Type: application/octet-stream\r\n\r\n".data(using: .utf8)!)
        body.append(fileData)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)
        return body
    }

    private static func formURLEncoded(_ params: [String: String]) -> Data {
        let allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.~")
        let pairs = params.map { key, value in
            let k = key.addingPercentEncoding(withAllowedCharacters: allowed) ?? key
            let v = value.addingPercentEncoding(withAllowedCharacters: allowed) ?? value
            return "\(k)=\(v)"
        }
        return pairs.joined(separator: "&").data(using: .utf8) ?? Data()
    }

    private static func jsonArray(_ items: [String]) -> String {
        let data = (try? JSONSerialization.data(withJSONObject: items)) ?? Data("[]".utf8)
        return String(data: data, encoding: .utf8) ?? "[]"
    }

    private static func md5Hex(_ data: Data) -> String {
        Insecure.MD5.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    private static func decodeDataURIPNG(_ uri: String) -> Data? {
        guard let comma = uri.firstIndex(of: ",") else { return Data(base64Encoded: uri) }
        return Data(base64Encoded: String(uri[uri.index(after: comma)...]))
    }

    // MARK: - Error mapping

    private static func checkHTTP(_ resp: URLResponse, _ data: Data) throws {
        guard let http = resp as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
        guard (200...299).contains(http.statusCode) else {
            teraboxLog.error("[TeraBox] HTTP \(http.statusCode): \(String(data: data, encoding: .utf8)?.prefix(300) ?? "")")
            throw CloudProviderError.serverError(http.statusCode)
        }
    }

    /// openapi endpoints return `errno` in the JSON body even on HTTP 200.
    private static func checkErrno(_ data: Data) throws {
        struct Envelope: Decodable { let errno: Int }
        guard let env = try? JSONDecoder().decode(Envelope.self, from: data) else { return }
        guard env.errno == 0 else {
            teraboxLog.error("[TeraBox] API errno \(env.errno)")
            switch env.errno {
            case -8: throw CloudProviderError.commandFailed("A file with that name already exists.")
            case -9: throw CloudProviderError.notFound("Item not found.")
            case -7: throw CloudProviderError.commandFailed("Invalid file name.")
            default: throw CloudProviderError.serverError(env.errno)
            }
        }
    }

    private static func mapAuthError(_ errno: Int) -> CloudProviderError {
        switch errno {
        case 400001: return .commandFailed("Waiting for authorization in the TeraBox app.")
        case 100001: return .invalidCredentials
        case 100002: return .commandFailed("Authorization code invalid or expired.")
        case 200002, 200003: return .notAuthenticated
        case 200004, 200005: return .notAuthenticated
        default: return .serverError(errno)
        }
    }
}
