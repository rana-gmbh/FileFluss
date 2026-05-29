import Foundation
import os

private let internxtLog = Logger(subsystem: "com.rana.FileFluss", category: "internxt")

/// Persisted, post-login secrets. The mnemonic is the master secret for file
/// content; we store it (like Filen's master keys) because every download must
/// re-derive per-file keys from it. `userId` is the network basic-auth password.
public struct InternxtCredentials: Codable, Sendable {
    public let email: String
    /// `newToken` JWT — bearer for every Drive (gateway) API call.
    public let token: String
    /// Decrypted BIP39 mnemonic — root of all file-content key derivation.
    public let mnemonic: String
    /// User's root storage bucket (hex). All file content lives here.
    public let bucket: String
    /// UUID of the user's Drive root folder — listings start here.
    public let rootFolderUuid: String
    /// Network basic-auth username.
    public let bridgeUser: String
    /// Network basic-auth password (hashed with SHA-256 per request).
    public let userId: String
}

// MARK: - Internxt API client
//
// Endpoints, transliterated from @internxt/cli + @internxt/sdk.
//   Drive (gateway, base = https://gateway.internxt.com/drive, Bearer token):
//     POST /auth/login                         { email } → { sKey, tfa }
//     POST /auth/login/access                  { email, password, tfa } → { newToken, user }
//     GET  /folders/content/{uuid}/folders/    ?offset&limit → { folders: [...] }
//     GET  /folders/content/{uuid}/files/      ?offset&limit → { files: [...] }
//     POST /folders                            { plainName, parentFolderUuid }
//     DELETE /folders/{uuid}  •  /files/{uuid}
//     PUT  /folders/{uuid}/meta  •  /files/{uuid}/meta   { plainName }
//     PATCH /folders/{uuid}  •  /files/{uuid}            { destinationFolder }
//     GET  /users/usage  (storage quota)
//   Network (base = https://gateway.internxt.com/network, Basic auth):
//     GET  /buckets/{bucket}/files/{fileId}/info   (x-api-version: 2) → download links
public actor InternxtAPIClient {
    private let driveAPI = "https://gateway.internxt.com/drive"
    private let networkAPI = "https://gateway.internxt.com/network"
    private let clientName = "drive-web"
    private let clientVersion = "1.0.0"

    public private(set) var credentials: InternxtCredentials
    private let session: URLSession

    /// Internxt is UUID-addressed, not path-addressed. Cache resolved nodes so
    /// repeat navigations don't re-walk the tree.
    private var nodeCache: [String: Node]

    struct Node {
        let uuid: String
        let isFolder: Bool
        let fileId: String?   // network file id (files only)
        let bucket: String?
        let size: Int64
        let type: String?     // extension (files only)
    }

    public init(credentials: InternxtCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 600
        self.session = URLSession(configuration: config)
        self.nodeCache = ["/": Node(uuid: credentials.rootFolderUuid, isFolder: true, fileId: nil, bucket: nil, size: 0, type: nil)]
    }

    // MARK: - Authentication

    /// Two-step login: fetch the encrypted salt, hash the password against it,
    /// exchange for tokens, then locally decrypt the mnemonic with the password.
    public static func login(email: String, password: String, twoFactorCode: String) async throws -> InternxtCredentials {
        let driveAPI = "https://gateway.internxt.com/drive"
        let session = URLSession(configuration: .default)

        // 1. securityDetails → encrypted salt.
        let saltBody = try JSONSerialization.data(withJSONObject: ["email": email])
        let (saltData, saltResp) = try await Self.send(session, "\(driveAPI)/auth/login", method: "POST", body: saltBody, bearer: nil)
        guard (saltResp as? HTTPURLResponse).map({ (200...299).contains($0.statusCode) }) == true else {
            throw CloudProviderError.invalidCredentials
        }
        struct SecurityDetails: Decodable { let sKey: String; let tfa: Bool? }
        let security = try JSONDecoder().decode(SecurityDetails.self, from: saltData)

        // 2. Derive the wrapped password hash.
        guard let passwordHash = InternxtCrypto.encryptPasswordHash(password: password, encryptedSalt: security.sKey) else {
            throw CloudProviderError.invalidCredentials
        }

        // 3. login/access → tokens + user. 2FA required iff securityDetails said so.
        var accessPayload: [String: Any] = ["email": email, "password": passwordHash]
        let code = twoFactorCode.trimmingCharacters(in: .whitespacesAndNewlines)
        if security.tfa == true {
            guard !code.isEmpty else { throw CloudProviderError.twoFactorRequired }
            accessPayload["tfa"] = code
        } else if !code.isEmpty {
            accessPayload["tfa"] = code
        }
        let accessBody = try JSONSerialization.data(withJSONObject: accessPayload)
        let (accessData, accessResp) = try await Self.send(session, "\(driveAPI)/auth/login/access", method: "POST", body: accessBody, bearer: nil)
        guard let http = accessResp as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
        guard (200...299).contains(http.statusCode) else {
            if http.statusCode == 401 { throw CloudProviderError.twoFactorRequired }
            internxtLog.error("[Internxt] login/access HTTP \(http.statusCode)")
            throw CloudProviderError.invalidCredentials
        }

        struct AccessResponse: Decodable {
            let newToken: String
            let user: User
            struct User: Decodable {
                let email: String
                let mnemonic: String
                let bucket: String
                let bridgeUser: String
                let userId: String
                let rootFolderId: String
            }
        }
        let access = try JSONDecoder().decode(AccessResponse.self, from: accessData)

        // 4. Decrypt the mnemonic with the plaintext password.
        guard let mnemonic = InternxtCrypto.decryptMnemonic(access.user.mnemonic, password: password) else {
            internxtLog.error("[Internxt] mnemonic decrypt failed")
            throw CloudProviderError.invalidCredentials
        }

        internxtLog.info("[Internxt] Authenticated as \(access.user.email, privacy: .public)")
        return InternxtCredentials(
            email: access.user.email,
            token: access.newToken,
            mnemonic: mnemonic,
            bucket: access.user.bucket,
            rootFolderUuid: access.user.rootFolderId,
            bridgeUser: access.user.bridgeUser,
            userId: access.user.userId
        )
    }

    public func userDisplayName() -> String { credentials.email }

    // MARK: - Listing

    public func listFolder(path: String) async throws -> [CloudFileItem] {
        let node = try await resolve(path)
        guard node.isFolder else { throw CloudProviderError.notFound(path) }

        var items: [CloudFileItem] = []

        // Subfolders (paginated).
        struct FolderPage: Decodable {
            let folders: [Entry]
            struct Entry: Decodable { let uuid: String; let plainName: String?; let name: String?; let updatedAt: String? }
        }
        var offset = 0
        while true {
            let data = try await driveGet("/folders/content/\(node.uuid)/folders/?offset=\(offset)&limit=50")
            let page = try JSONDecoder().decode(FolderPage.self, from: data)
            for f in page.folders {
                let name = f.plainName ?? f.name ?? "Untitled"
                let childPath = path == "/" ? "/\(name)" : "\(path)/\(name)"
                nodeCache[childPath] = Node(uuid: f.uuid, isFolder: true, fileId: nil, bucket: nil, size: 0, type: nil)
                items.append(CloudFileItem(id: "d\(f.uuid)", name: name, path: childPath, isDirectory: true,
                                           size: 0, modificationDate: Self.parseDate(f.updatedAt), checksum: nil))
            }
            if page.folders.count < 50 { break }
            offset += 50
        }

        // Files (paginated).
        struct FilePage: Decodable {
            let files: [Entry]
            struct Entry: Decodable {
                let uuid: String
                let fileId: String?
                let bucket: String?
                let plainName: String?
                let name: String?
                let type: String?
                let size: StringOrInt?
                let updatedAt: String?
            }
        }
        offset = 0
        while true {
            let data = try await driveGet("/folders/content/\(node.uuid)/files/?offset=\(offset)&limit=50")
            let page = try JSONDecoder().decode(FilePage.self, from: data)
            for f in page.files {
                let base = f.plainName ?? f.name ?? "Untitled"
                let ext = f.type ?? ""
                let name = ext.isEmpty ? base : "\(base).\(ext)"
                let childPath = path == "/" ? "/\(name)" : "\(path)/\(name)"
                let size = f.size?.int64Value ?? 0
                nodeCache[childPath] = Node(uuid: f.uuid, isFolder: false, fileId: f.fileId, bucket: f.bucket, size: size, type: ext)
                items.append(CloudFileItem(id: "f\(f.uuid)", name: name, path: childPath, isDirectory: false,
                                           size: size, modificationDate: Self.parseDate(f.updatedAt), checksum: nil))
            }
            if page.files.count < 50 { break }
            offset += 50
        }

        return items
    }

    // MARK: - Download

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let node = try await resolve(remotePath)
        guard !node.isFolder, let fileId = node.fileId, let bucket = node.bucket else {
            throw CloudProviderError.notFound(remotePath)
        }

        // 1. Network download links.
        struct DownloadInfo: Decodable {
            let index: String
            let shards: [Shard]
            let size: Int64?
            struct Shard: Decodable { let index: Int; let url: String }
        }
        let infoData = try await networkRequest("/buckets/\(bucket)/files/\(fileId)/info", method: "GET", body: nil, apiVersion2: true)
        let info = try JSONDecoder().decode(DownloadInfo.self, from: infoData)

        guard let index = InternxtCrypto.hexDecode(info.index), index.count >= 16,
              let key = InternxtCrypto.generateFileKey(mnemonic: credentials.mnemonic, bucketIdHex: bucket, index: index) else {
            throw CloudProviderError.invalidResponse
        }
        let iv = index.prefix(16)

        // 2. Pull every shard (in order) and concatenate the ciphertext.
        var ciphertext = Data()
        for shard in info.shards.sorted(by: { $0.index < $1.index }) {
            guard let url = URL(string: shard.url) else { throw CloudProviderError.invalidResponse }
            let (data, resp) = try await session.data(from: url)
            guard (resp as? HTTPURLResponse).map({ (200...299).contains($0.statusCode) }) == true else {
                throw CloudProviderError.serverError((resp as? HTTPURLResponse)?.statusCode ?? 0)
            }
            ciphertext.append(data)
            onBytes?(Int64(data.count))
        }

        // 3. Decrypt (AES-256-CTR) and write out.
        guard let plaintext = InternxtCrypto.aesCTR(ciphertext, key: Data(key), iv: Data(iv)) else {
            throw CloudProviderError.invalidResponse
        }
        try? FileManager.default.removeItem(at: localURL)
        try plaintext.write(to: localURL)
    }

    // MARK: - Upload

    /// Encrypt locally (AES-256-CTR) and push through the 3-step network flow,
    /// then register the file in Drive. Replaces any existing file at the path.
    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let plaintext = try Data(contentsOf: localURL)
        let parentPath = (remotePath as NSString).deletingLastPathComponent
        let fileName = (remotePath as NSString).lastPathComponent
        let parent = try await resolve(parentPath.isEmpty ? "/" : parentPath)
        guard parent.isFolder else { throw CloudProviderError.notFound(parentPath) }
        let bucket = credentials.bucket

        // A pre-existing file at this path is replaced once the new one lands.
        let existing = try? await resolve(remotePath)

        // 1. Per-file key material.
        var index = Data(count: 32)
        index.withUnsafeMutableBytes { _ = SecRandomCopyBytes(kSecRandomDefault, 32, $0.baseAddress!) }
        let iv = index.prefix(16)
        guard let key = InternxtCrypto.generateFileKey(mnemonic: credentials.mnemonic, bucketIdHex: bucket, index: index),
              let ciphertext = InternxtCrypto.aesCTR(plaintext, key: Data(key), iv: Data(iv)) else {
            throw CloudProviderError.invalidResponse
        }

        // 2. Reserve an upload slot (CTR ciphertext is the same length as plaintext).
        // These transfer steps are idempotent (re-reserving a slot / re-PUTting
        // the same bytes / finishing the same shard hash is safe), so retry
        // through Internxt's occasional transient gateway errors (HTTP 502/503).
        struct StartResp: Decodable { let uploads: [Upload]; struct Upload: Decodable { let url: String?; let uuid: String } }
        let startBody = try JSONSerialization.data(withJSONObject: ["uploads": [["index": 0, "size": plaintext.count]]])
        let startData = try await retryingTransient {
            try await self.networkRequest("/v2/buckets/\(bucket)/files/start?multiparts=1", method: "POST", body: startBody)
        }
        let start = try JSONDecoder().decode(StartResp.self, from: startData)
        guard let upload = start.uploads.first, let url = upload.url else { throw CloudProviderError.invalidResponse }

        // 3. PUT the ciphertext to the presigned URL; the shard hash is hash160 of it.
        try await retryingTransient { try await self.put(ciphertext, to: url, onBytes: onBytes) }
        let hash = InternxtCrypto.shardHashHex(ciphertext)

        // 4. Finish → network file id.
        struct FinishResp: Decodable { let id: String }
        let finishBody = try JSONSerialization.data(withJSONObject: [
            "index": InternxtCrypto.hexEncode(index),
            "shards": [["hash": hash, "uuid": upload.uuid]],
        ])
        let finishData = try await retryingTransient {
            try await self.networkRequest("/v2/buckets/\(bucket)/files/finish", method: "POST", body: finishBody)
        }
        let finish = try JSONDecoder().decode(FinishResp.self, from: finishData)

        // 5. Drop the previous version *before* registering the new one.
        // Internxt's Drive API rejects creating a file whose plainName+type
        // already exists in the folder with HTTP 409, so a replace must
        // remove the old entry first. The new ciphertext is already safely
        // uploaded (steps 2-4), so the old data isn't the only copy when we
        // delete its Drive entry here.
        if let existing, !existing.isFolder {
            _ = try? await driveSend("/files/\(existing.uuid)", method: "DELETE", body: nil)
            nodeCache.removeValue(forKey: remotePath)
        }

        // 6. Register the file in Drive.
        let base = (fileName as NSString).deletingPathExtension
        let ext = (fileName as NSString).pathExtension
        var entry: [String: Any] = [
            "plainName": base, "type": ext, "size": plaintext.count,
            "folderUuid": parent.uuid, "fileId": finish.id, "bucket": bucket,
            "encryptVersion": "03-aes",
        ]
        if let attrs = try? FileManager.default.attributesOfItem(atPath: localURL.path) {
            let f = ISO8601DateFormatter(); f.formatOptions = [.withInternetDateTime]
            if let m = attrs[.modificationDate] as? Date { entry["modificationTime"] = f.string(from: m) }
            if let c = attrs[.creationDate] as? Date { entry["creationTime"] = f.string(from: c) }
        }
        let entryBody = try JSONSerialization.data(withJSONObject: entry)
        let createData: Data
        do {
            createData = try await driveSend("/files", method: "POST", body: entryBody)
        } catch CloudProviderError.serverError(409) {
            // The name still collides: either the step-5 delete hasn't
            // propagated yet, or `existing` was a stale cache entry pointing at
            // an already-gone uuid. Re-list the parent to get the *live* entry,
            // delete it, and retry the create once.
            _ = try? await listFolder(path: parentPath.isEmpty ? "/" : parentPath)
            if let conflict = nodeCache[remotePath], !conflict.isFolder {
                _ = try? await driveSend("/files/\(conflict.uuid)", method: "DELETE", body: nil)
                nodeCache.removeValue(forKey: remotePath)
            }
            createData = try await driveSend("/files", method: "POST", body: entryBody)
        }
        struct Created: Decodable { let uuid: String }
        if let created = try? JSONDecoder().decode(Created.self, from: createData) {
            nodeCache[remotePath] = Node(uuid: created.uuid, isFolder: false, fileId: finish.id, bucket: bucket, size: Int64(plaintext.count), type: ext)
        }
    }

    /// Retries `op` through Internxt's occasional transient gateway failures
    /// (HTTP 500/502/503/504 and rate-limits) with a short backoff. Only wrap
    /// idempotent operations — re-running must be safe.
    private func retryingTransient<T>(_ attempts: Int = 3, _ op: () async throws -> T) async throws -> T {
        var lastError: Error = CloudProviderError.invalidResponse
        for attempt in 0..<attempts {
            do {
                return try await op()
            } catch let error as CloudProviderError where Self.isTransient(error) {
                lastError = error
                if attempt < attempts - 1 {
                    try? await Task.sleep(nanoseconds: UInt64(attempt + 1) * 600_000_000) // 0.6s, 1.2s
                }
            }
        }
        throw lastError
    }

    private static func isTransient(_ error: CloudProviderError) -> Bool {
        switch error {
        case .serverError(let code): return code == 500 || code == 502 || code == 503 || code == 504
        case .rateLimited: return true
        default: return false
        }
    }

    private func put(_ data: Data, to urlString: String, onBytes: ByteProgressHandler?) async throws {
        guard let url = URL(string: urlString) else { throw CloudProviderError.invalidResponse }
        var request = URLRequest(url: url)
        request.httpMethod = "PUT"
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        let (_, resp) = try await session.upload(for: request, from: data)
        guard (resp as? HTTPURLResponse).map({ (200...299).contains($0.statusCode) }) == true else {
            throw CloudProviderError.serverError((resp as? HTTPURLResponse)?.statusCode ?? 0)
        }
        onBytes?(Int64(data.count))
    }

    // MARK: - Mutations

    public func createFolder(at path: String) async throws {
        if (try? await resolve(path)) != nil { return } // idempotent
        let parentPath = (path as NSString).deletingLastPathComponent
        let name = (path as NSString).lastPathComponent
        let parent = try await resolve(parentPath.isEmpty ? "/" : parentPath)
        let body = try JSONSerialization.data(withJSONObject: ["plainName": name, "parentFolderUuid": parent.uuid])
        let data = try await driveSend("/folders", method: "POST", body: body)
        struct Created: Decodable { let uuid: String }
        if let created = try? JSONDecoder().decode(Created.self, from: data) {
            nodeCache[path] = Node(uuid: created.uuid, isFolder: true, fileId: nil, bucket: nil, size: 0, type: nil)
        }
    }

    public func deleteItem(at path: String) async throws {
        let node = try await resolve(path)
        let endpoint = node.isFolder ? "/folders/\(node.uuid)" : "/files/\(node.uuid)"
        _ = try await driveSend(endpoint, method: "DELETE", body: nil)
        nodeCache.removeValue(forKey: path)
    }

    public func renameItem(at path: String, to newName: String) async throws {
        let node = try await resolve(path)
        // Rename uses the bare name; the gateway keeps `type` (extension) for files.
        let base = node.isFolder ? newName : (newName as NSString).deletingPathExtension
        let endpoint = node.isFolder ? "/folders/\(node.uuid)/meta" : "/files/\(node.uuid)/meta"
        let body = try JSONSerialization.data(withJSONObject: ["plainName": base])
        _ = try await driveSend(endpoint, method: "PUT", body: body)
        nodeCache.removeValue(forKey: path)
        let parent = (path as NSString).deletingLastPathComponent
        let newPath = parent == "/" ? "/\(newName)" : "\(parent)/\(newName)"
        nodeCache[newPath] = node
    }

    public func moveItem(at path: String, toPath newPath: String) async throws {
        let node = try await resolve(path)
        let destParent = try await resolve((newPath as NSString).deletingLastPathComponent)
        let endpoint = node.isFolder ? "/folders/\(node.uuid)" : "/files/\(node.uuid)"
        let body = try JSONSerialization.data(withJSONObject: ["destinationFolder": destParent.uuid])
        _ = try await driveSend(endpoint, method: "PATCH", body: body)
        nodeCache.removeValue(forKey: path)
    }

    // MARK: - Quota

    public func storageQuota() async throws -> CloudStorageQuota? {
        struct Usage: Decodable { let drive: Int64?; let total: Int64?; let used: Int64? }
        // /users/usage → { drive, ... }; /users/limit → { maxSpaceBytes }
        guard let usageData = try? await driveGet("/users/usage"),
              let usage = try? JSONDecoder().decode(Usage.self, from: usageData) else { return nil }
        let used = usage.used ?? usage.drive ?? 0
        struct Limit: Decodable { let maxSpaceBytes: StringOrInt? }
        var total: Int64? = usage.total
        if total == nil, let limitData = try? await driveGet("/users/limit"),
           let limit = try? JSONDecoder().decode(Limit.self, from: limitData) {
            total = limit.maxSpaceBytes?.int64Value
        }
        return CloudStorageQuota(usedBytes: used, totalBytes: total)
    }

    // MARK: - Path resolution

    private func resolve(_ path: String) async throws -> Node {
        let normalized = path.isEmpty ? "/" : path
        if let cached = nodeCache[normalized] { return cached }

        // Walk from the deepest cached ancestor by listing each missing level.
        let components = normalized.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        var currentPath = "/"
        for component in components {
            _ = try await listFolder(path: currentPath) // populates nodeCache for children
            currentPath = currentPath == "/" ? "/\(component)" : "\(currentPath)/\(component)"
            guard nodeCache[currentPath] != nil else { throw CloudProviderError.notFound(currentPath) }
        }
        guard let node = nodeCache[normalized] else { throw CloudProviderError.notFound(normalized) }
        return node
    }

    // MARK: - HTTP

    private func driveGet(_ endpoint: String) async throws -> Data {
        let (data, resp) = try await Self.send(session, "\(driveAPI)\(endpoint)", method: "GET", body: nil,
                                               bearer: credentials.token, clientName: clientName, clientVersion: clientVersion)
        try Self.check(resp, data)
        return data
    }

    @discardableResult
    private func driveSend(_ endpoint: String, method: String, body: Data?) async throws -> Data {
        let (data, resp) = try await Self.send(session, "\(driveAPI)\(endpoint)", method: method, body: body,
                                               bearer: credentials.token, clientName: clientName, clientVersion: clientVersion)
        try Self.check(resp, data)
        return data
    }

    /// Network service calls authenticate with HTTP Basic — username is the
    /// bridge user, password is the SHA-256 of the userId (hex).
    private func networkRequest(_ endpoint: String, method: String, body: Data?, apiVersion2: Bool = false) async throws -> Data {
        let pass = InternxtCrypto.hexEncode(InternxtCrypto.sha256(Data(credentials.userId.utf8)))
        let basic = Data("\(credentials.bridgeUser):\(pass)".utf8).base64EncodedString()
        var headers = ["Authorization": "Basic \(basic)"]
        if apiVersion2 { headers["x-api-version"] = "2" }
        let (data, resp) = try await Self.send(session, "\(networkAPI)\(endpoint)", method: method, body: body,
                                               bearer: nil, clientName: clientName, clientVersion: clientVersion, extraHeaders: headers)
        try Self.check(resp, data)
        return data
    }

    private static func send(_ session: URLSession, _ urlString: String, method: String, body: Data?,
                             bearer: String?, clientName: String = "drive-web", clientVersion: String = "1.0.0",
                             extraHeaders: [String: String] = [:]) async throws -> (Data, URLResponse) {
        guard let url = URL(string: urlString) else { throw CloudProviderError.invalidResponse }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue(clientVersion, forHTTPHeaderField: "internxt-version")
        request.setValue(clientName, forHTTPHeaderField: "internxt-client")
        if let bearer { request.setValue("Bearer \(bearer)", forHTTPHeaderField: "Authorization") }
        for (k, v) in extraHeaders { request.setValue(v, forHTTPHeaderField: k) }
        request.httpBody = body
        return try await session.data(for: request)
    }

    private static func check(_ resp: URLResponse, _ data: Data) throws {
        guard let http = resp as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
        guard (200...299).contains(http.statusCode) else {
            internxtLog.error("[Internxt] HTTP \(http.statusCode): \(String(data: data, encoding: .utf8)?.prefix(300) ?? "")")
            switch http.statusCode {
            case 401, 403: throw CloudProviderError.notAuthenticated
            case 404: throw CloudProviderError.notFound("Resource not found")
            case 429: throw CloudProviderError.rateLimited
            default: throw CloudProviderError.serverError(http.statusCode)
            }
        }
    }

    private static func parseDate(_ s: String?) -> Date {
        guard let s else { return .distantPast }
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f.date(from: s) ?? ISO8601DateFormatter().date(from: s) ?? .distantPast
    }
}

/// Internxt's API is inconsistent about whether numeric fields arrive as JSON
/// numbers or strings (sizes especially). Decode either.
struct StringOrInt: Decodable {
    let int64Value: Int64?
    init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if let i = try? c.decode(Int64.self) { int64Value = i }
        else if let s = try? c.decode(String.self) { int64Value = Int64(s) }
        else { int64Value = nil }
    }
}
