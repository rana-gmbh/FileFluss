import CommonCrypto
import CryptoKit
import Foundation
import Security
import os

private let filenLog = Logger(subsystem: "com.rana.FileFluss", category: "filen")

// MARK: - Persisted credentials

/// Everything FileFluss needs to talk to Filen on behalf of an account
/// across launches. The user's email/password are exchanged once at
/// add-account time for an API key + a set of master keys; the password
/// itself is never stored. Master keys decrypt every file/folder metadata
/// blob we receive from the server.
public struct FilenCredentials: Codable, Sendable {
    public let email: String
    /// Bearer token sent on every gateway API call after login.
    public let apiKey: String
    /// One or more 64-char hex master keys, freshest first. Newer keys are
    /// generated each time the user changes their password; we keep every
    /// one we've received because older items may still be encrypted with
    /// an older key.
    public let masterKeys: [String]
    /// UUID of the user's "Cloud Drive" root — returned from /v3/user/baseFolder.
    /// All directory listings start from this UUID.
    public let rootFolderUuid: String
}

// MARK: - Filen API client
//
// ===== v2 spec, transliterated from filen-sdk-rs (filen-rs monorepo) =====
//
// API endpoints (POST JSON, base = https://gateway.filen.io):
//   /v3/auth/info        { email } → { email, authVersion, salt, id }
//   /v3/login            { email, password, twoFactorCode, authVersion } →
//                        { apiKey, masterKeys, publicKey, privateKey, dek }
//   /v3/user/baseFolder  → { uuid: "<root-uuid>" }
//   /v3/dir/content      { uuid } → { uploads: [File], folders: [Directory] }
//
// File chunks live on https://egest.filen.io, addressed by
//   {egest}/{region}/{bucket}/{uuid}/{chunkIdx}.
//
// Login derivation (auth version 2):
//   PBKDF2-HMAC-SHA512(pw.utf8, salt.utf8, 200_000 iter, 64 bytes)
//     → hex(128 chars), split at index 64
//     → first half = master-key string (used as a Filen "V2Key" — see below)
//     → second half = derived password string
//     → SHA-512(derived password string utf8) → hex (128 chars)
//     → that hex string is the password sent to /v3/login.
//
// Master-key string → AES-256 key:
//   PBKDF2-HMAC-SHA512(masterKeyAscii.utf8, masterKeyAscii.utf8, 1 iter, 32 bytes).
//   (Filen calls this a "V2Key". It looks unusual but matches the SDK exactly.)
//
// File-key string → AES-256 key:
//   The 32-char ASCII file-key string is used directly as 32 raw bytes — no
//   derivation. Per-file keys live inside the file's encrypted metadata.
//
// Metadata wire format ("002" envelope):
//   "002" || nonce(12 ASCII chars from [A-Za-z0-9]) || base64(ciphertext||tag).
//   AES-256-GCM, 16-byte tag appended to ciphertext, no AAD.
//
// File chunk wire format:
//   12-byte raw nonce || ciphertext || 16-byte tag. AES-256-GCM, no AAD.
//   Plaintext chunk size = 1 MiB; the last chunk may be shorter.
//
// Decrypted metadata payloads (JSON, camelCase):
//   Directory:  { name, creation? }
//   File:       { name, size, mime, key, lastModified, creation?, blake3? }
//
// Encrypted master-keys blob (returned by /v3/login):
//   A single "002"-envelope, decrypted by the user's derived master key.
//   The plaintext is "key1|key2|key3" — pipe-separated 64-char hex strings.
//   We keep them in the order returned, with the derived key inserted at the
//   front, and try each in turn when decrypting metadata.

public actor FilenAPIClient {
    static let baseURL = "https://gateway.filen.io"
    static let egestURL = "https://egest.filen.io"
    /// Plaintext chunk size used by Filen for content encryption. Each
    /// encrypted chunk on the wire is 12 bytes longer (nonce) plus 16
    /// (auth tag), so the on-disk encrypted last chunk can be up to
    /// 1 MiB + 28 bytes.
    static let plaintextChunkSize = 1024 * 1024

    /// Per-file info we need to download chunks, harvested as a side effect
    /// of `listChildren`. Keyed by Filen's file UUID (the SDK's `UuidStr`).
    /// Populated whenever a parent folder is listed; the download path
    /// re-lists the parent on a cache miss so a freshly-launched app can
    /// download a file by path without first opening every parent folder.
    struct FileChunkInfo: Sendable {
        let key: String
        let region: String
        let bucket: String
        let chunks: Int
        let size: Int64
    }
    private var fileChunkInfo: [String: FileChunkInfo] = [:]

    private(set) var credentials: FilenCredentials
    private let session: URLSession

    public init(credentials: FilenCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 600
        self.session = URLSession(configuration: config)
    }

    // MARK: - Authentication

    /// Run the full Filen login flow. Returns a credentials struct ready to
    /// persist in the keychain. The password is held only locally for the
    /// duration of this call; the persisted blob never contains it.
    public static func login(email: String, password: String, twoFactorCode: String) async throws -> FilenCredentials {
        let info = try await fetchAuthInfo(email: email)
        guard info.authVersion == 2 else {
            throw CloudProviderError.commandFailed(
                "Filen account uses auth version \(info.authVersion). FileFluss currently supports v2 accounts only — Filen v1 / v3 (Argon2) are not yet implemented."
            )
        }

        // Step 1: derive the v2 master key + login password from email + pw.
        let derivation = try FilenCrypto.deriveV2Login(password: password, salt: info.salt)

        // Step 2: exchange derived password for an apiKey + encrypted master keys.
        let loginCode = twoFactorCode.isEmpty ? "XXXXXX" : twoFactorCode
        let loginRequest: [String: Any] = [
            "email": email,
            "password": derivation.loginPasswordHex,
            "twoFactorCode": loginCode,
            "authVersion": 2,
        ]
        let loginResponse = try await postUnauthenticated(
            path: "/v3/login",
            body: loginRequest,
            expectedKey: "data"
        )
        guard let apiKey = loginResponse["apiKey"] as? String else {
            throw CloudProviderError.commandFailed("Filen login succeeded but the server didn't return an apiKey.")
        }
        let masterKeysBlob = loginResponse["masterKeys"] as? String

        // Step 3: decrypt the master-keys blob with the freshly-derived key.
        // New accounts that have never logged in elsewhere may not have one
        // yet; in that case the derived key is the only key we'll ever use.
        var masterKeys: [String] = [derivation.masterKeyHex]
        if let blob = masterKeysBlob, !blob.isEmpty {
            let derivedAesKey = try FilenCrypto.aesKeyFromV2MasterKey(derivation.masterKeyHex)
            let decoded = try FilenCrypto.decryptMetadata(blob, aesKey: derivedAesKey)
            for piece in decoded.split(separator: "|") {
                let key = String(piece)
                if !masterKeys.contains(key) { masterKeys.append(key) }
            }
        }

        // Step 4: ask the gateway which directory UUID is this user's root.
        // Same authenticated session is reused for all subsequent calls.
        let baseFolder = try await fetchBaseFolder(apiKey: apiKey)

        return FilenCredentials(
            email: email,
            apiKey: apiKey,
            masterKeys: masterKeys,
            rootFolderUuid: baseFolder
        )
    }

    // MARK: - Listing

    /// Walk the directory tree from the account root and return the children
    /// of `path` (panel-level path, "/foo/bar"). Each path segment requires
    /// listing the parent and decrypting every child's metadata until the
    /// name matches — Filen's API is UUID-keyed, so we have no way to
    /// resolve a path in one round-trip.
    public func listFolder(path: String) async throws -> [CloudFileItem] {
        let targetUuid = try await resolveUuid(forPath: path)
        return try await listChildren(parentUuid: targetUuid, parentPath: path)
    }

    private func resolveUuid(forPath path: String) async throws -> String {
        let trimmed = path.hasPrefix("/") ? String(path.dropFirst()) : path
        var currentUuid = credentials.rootFolderUuid
        var currentPath = ""
        if trimmed.isEmpty { return currentUuid }

        for segment in trimmed.split(separator: "/") {
            currentPath += "/\(segment)"
            let children = try await listChildren(parentUuid: currentUuid, parentPath: "")
            guard let match = children.first(where: { $0.name == String(segment) && $0.isDirectory }) else {
                throw CloudProviderError.notFound(currentPath)
            }
            // Children carry their UUID embedded in the synthetic id we mint
            // in `listChildren` — strip the "d:" / "f:" prefix to recover it.
            currentUuid = String(match.id.dropFirst(2))
        }
        return currentUuid
    }

    private func listChildren(parentUuid: String, parentPath: String) async throws -> [CloudFileItem] {
        let body: [String: Any] = ["uuid": parentUuid]
        let response = try await postAuthenticated(path: "/v3/dir/content", body: body, expectedKey: "data")

        let folders = (response["folders"] as? [[String: Any]]) ?? []
        let files = (response["uploads"] as? [[String: Any]]) ?? []
        let parentBase = parentPath.hasSuffix("/") ? String(parentPath.dropLast()) : parentPath

        var items: [CloudFileItem] = []
        items.reserveCapacity(folders.count + files.count)

        for folder in folders {
            guard let uuid = folder["uuid"] as? String,
                  let nameMeta = folder["name"] as? String else { continue }
            let timestamp = (folder["timestamp"] as? Double).map(timestampToDate) ?? Date.distantPast
            // Folder metadata blob decrypts to JSON `{ name, creation? }`.
            // Skip folders whose metadata fails to decrypt — they may have
            // been created with a key we no longer have (e.g. shared from
            // another account before the master-keys handshake completed).
            guard let decoded = try? decryptItemMetadata(nameMeta),
                  let parsed = parseFolderMeta(decoded) else { continue }
            let itemPath = parentBase.isEmpty ? "/\(parsed.name)" : "\(parentBase)/\(parsed.name)"
            items.append(CloudFileItem(
                id: "d:\(uuid)",
                name: parsed.name,
                path: itemPath,
                isDirectory: true,
                size: 0,
                modificationDate: parsed.created ?? timestamp,
                checksum: nil
            ))
        }

        for file in files {
            guard let uuid = file["uuid"] as? String,
                  let nameMeta = file["metadata"] as? String else { continue }
            guard let decoded = try? decryptItemMetadata(nameMeta),
                  let parsed = parseFileMeta(decoded) else { continue }
            let itemPath = parentBase.isEmpty ? "/\(parsed.name)" : "\(parentBase)/\(parsed.name)"
            // Stash everything the download path needs so it doesn't have
            // to re-list the parent + re-decrypt metadata for each
            // request. CloudFileItem is intentionally minimal (it crosses
            // the SwiftUI boundary), so per-file crypto state lives in
            // this actor's cache instead of being threaded through the
            // item.
            let chunks = (file["chunks"] as? NSNumber)?.intValue ?? 0
            let region = (file["region"] as? String) ?? ""
            let bucket = (file["bucket"] as? String) ?? ""
            fileChunkInfo[uuid] = FileChunkInfo(
                key: parsed.key,
                region: region,
                bucket: bucket,
                chunks: chunks,
                size: parsed.size
            )
            items.append(CloudFileItem(
                id: "f:\(uuid)",
                name: parsed.name,
                path: itemPath,
                isDirectory: false,
                size: parsed.size,
                modificationDate: parsed.lastModified,
                checksum: parsed.hash
            ))
        }

        return items
    }

    // MARK: - Download

    /// Stream a Filen file to disk by downloading chunks sequentially and
    /// decrypting each one with the file's per-chunk AES-256-GCM key. Per
    /// the SDK's `FileKey`, the 32-char ASCII key string IS the AES-256 key
    /// in raw bytes — no derivation. Each chunk on the wire is
    /// `12-byte nonce || ciphertext || 16-byte tag`.
    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        // resolveUuid only walks directory paths, so we list the file's
        // parent and find the file by name from the returned items. The
        // listing also populates `fileChunkInfo` for the file's UUID,
        // which is everything the chunk loop needs.
        let parent = (remotePath as NSString).deletingLastPathComponent
        let name = (remotePath as NSString).lastPathComponent
        let parentNormalized = parent.isEmpty ? "/" : parent
        let entries = try await listFolder(path: parentNormalized)
        guard let item = entries.first(where: { $0.name == name && !$0.isDirectory }) else {
            throw CloudProviderError.notFound(remotePath)
        }
        // CloudFileItem.id for a file is "f:<uuid>" — strip the prefix.
        let uuid = String(item.id.dropFirst(2))
        guard let info = fileChunkInfo[uuid] else {
            throw CloudProviderError.commandFailed("Filen file metadata not cached — listing didn't populate chunk info.")
        }
        try await downloadChunks(uuid: uuid, info: info, to: localURL, onBytes: onBytes)
    }

    private func downloadChunks(uuid: String, info: FileChunkInfo, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        // Filen's per-file AES-256 key is the 32-char ASCII string used as
        // 32 raw bytes — see FileKey::try_from in filen-sdk-rs/src/crypto/v2.rs.
        guard info.key.count == 32, let keyData = info.key.data(using: .utf8) else {
            throw CloudProviderError.commandFailed("Filen file key isn't 32 ASCII bytes — encrypted with an auth-version we don't support yet.")
        }
        let aesKey = SymmetricKey(data: keyData)

        // Empty file: there's still a request to be made? Per the SDK,
        // chunks count is 0 for empty files; just touch the destination.
        try? FileManager.default.removeItem(at: localURL)
        FileManager.default.createFile(atPath: localURL.path, contents: nil)
        guard info.chunks > 0 else { return }

        guard let handle = try? FileHandle(forWritingTo: localURL) else {
            throw CloudProviderError.commandFailed("Couldn't open destination file for writing.")
        }
        defer { try? handle.close() }

        for chunkIdx in 0..<info.chunks {
            let encrypted = try await downloadChunkRaw(
                region: info.region,
                bucket: info.bucket,
                uuid: uuid,
                chunkIdx: chunkIdx
            )
            let plain = try FilenCrypto.decryptFileChunk(encrypted, aesKey: aesKey)
            try handle.write(contentsOf: plain)
            onBytes?(Int64(plain.count))
        }
    }

    private func downloadChunkRaw(region: String, bucket: String, uuid: String, chunkIdx: Int) async throws -> Data {
        // egest URL pattern, transcribed from filen-sdk-rs/src/api/download.rs:
        //   {egest}/{region}/{bucket}/{uuid}/{chunkIdx}
        let urlString = "\(Self.egestURL)/\(region)/\(bucket)/\(uuid)/\(chunkIdx)"
        guard let url = URL(string: urlString) else {
            throw CloudProviderError.invalidResponse
        }
        var request = URLRequest(url: url)
        request.setValue("Bearer \(credentials.apiKey)", forHTTPHeaderField: "Authorization")
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            throw CloudProviderError.serverError(code)
        }
        return data
    }

    // MARK: - Upload

    /// Upload a local file to `remotePath` (full destination path including
    /// the new filename). Each chunk is encrypted with a per-file AES-256
    /// key, POSTed to a random ingest host, then a final /v3/upload/done
    /// publishes the file. Mostly transliterated from filen-sdk-rs's
    /// FileWriter — see filen-sdk-rs/src/fs/file/write.rs.
    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let parentPath = (remotePath as NSString).deletingLastPathComponent
        let name = (remotePath as NSString).lastPathComponent
        let parentUuid = try await resolveUuid(forPath: parentPath.isEmpty ? "/" : parentPath)

        // Read whole file into memory. Filen's plaintext chunk size is 1 MiB
        // and even modest cloud uploads tend to be smaller than the user's
        // RAM; for now we accept that tradeoff to keep this code path
        // straightforward. A streaming variant can come later when we have
        // a known-good baseline.
        let plaintext = try Data(contentsOf: localURL)
        let attrs = try? FileManager.default.attributesOfItem(atPath: localURL.path)
        let mtime: Date = (attrs?[.modificationDate] as? Date) ?? Date()
        let mime = Self.guessMimeType(for: name)

        // Per-file 32-char ASCII key. The raw 32 bytes are the AES-256 key
        // used to encrypt every chunk (FileKey::try_from in v2.rs).
        let fileKeyAscii = Self.randomFilenString(length: 32)
        guard fileKeyAscii.count == 32, let fileKeyData = fileKeyAscii.data(using: .utf8) else {
            throw CloudProviderError.commandFailed("Couldn't mint a 32-byte file key.")
        }
        let fileKeyAes = SymmetricKey(data: fileKeyData)

        // Per-file metadata encryption key: take the same file-key string
        // and feed it through the v2 MasterKey-style PBKDF2(1) derivation.
        // The SDK calls this `to_meta_key()` — it's how name/size/mime
        // get encrypted as standalone server-side index fields.
        let fileMetaAes = try FilenCrypto.aesKeyFromV2MasterKey(fileKeyAscii)

        let masterAes = try activeMasterAesKey()
        let uploadKey = Self.randomFilenString(length: 32)
        let rmKey = Self.randomFilenString(length: 32)
        let fileUuid = UUID().uuidString.lowercased()

        // Pre-flight encryption of the four envelopes we hand to /v3/upload/done.
        let metadataJson: [String: Any] = [
            "name": name,
            "size": plaintext.count,
            "mime": mime,
            "key": fileKeyAscii,
            "lastModified": Int64(mtime.timeIntervalSince1970),
        ]
        let metadataPlain = String(data: try JSONSerialization.data(withJSONObject: metadataJson), encoding: .utf8) ?? "{}"
        let encryptedMetadata = try FilenCrypto.encryptMetadata(metadataPlain, aesKey: masterAes)
        let encryptedName = try FilenCrypto.encryptMetadata(name, aesKey: fileMetaAes)
        let encryptedMime = try FilenCrypto.encryptMetadata(mime, aesKey: fileMetaAes)
        let encryptedSize = try FilenCrypto.encryptMetadata(String(plaintext.count), aesKey: fileMetaAes)
        let nameHashed = FilenCrypto.v2HashName(name)

        // Empty file: /v3/upload/empty short-circuits the chunk loop. The
        // server still wants the encrypted envelopes so it has metadata
        // to store, but no chunks fly.
        if plaintext.isEmpty {
            let emptyBody: [String: Any] = [
                "uuid": fileUuid,
                "name": encryptedName,
                "nameHashed": nameHashed,
                "size": encryptedSize,
                "parent": parentUuid,
                "mime": encryptedMime,
                "metadata": encryptedMetadata,
                "version": 2,
            ]
            _ = try await postAuthenticated(path: "/v3/upload/empty", body: emptyBody, expectedKey: "data")
            return
        }

        // Slice plaintext into 1 MiB chunks, encrypt each one, POST it.
        // Filen's per-chunk POST is to {ingest}/v3/upload?uuid=&index=&parent=&uploadKey=&hash=
        // with the encrypted chunk bytes as the raw body and the chunk's
        // SHA-512(hex) passed in the URL.
        let chunkCount = (plaintext.count + Self.plaintextChunkSize - 1) / Self.plaintextChunkSize
        var streamed: Int64 = 0
        for idx in 0..<chunkCount {
            let start = idx * Self.plaintextChunkSize
            let end = min(start + Self.plaintextChunkSize, plaintext.count)
            let plainChunk = plaintext.subdata(in: start..<end)
            let encrypted = try FilenCrypto.encryptFileChunk(plainChunk, aesKey: fileKeyAes)
            let chunkSha512 = Data(SHA512.hash(data: encrypted)).map { String(format: "%02x", $0) }.joined()
            try await uploadChunkRaw(
                uuid: fileUuid,
                parent: parentUuid,
                uploadKey: uploadKey,
                chunkIdx: idx,
                hashHex: chunkSha512,
                body: encrypted
            )
            streamed += Int64(plainChunk.count)
            onBytes?(Int64(plainChunk.count))
        }

        // Finalize the upload. /v3/upload/done is regular gateway POST.
        let doneBody: [String: Any] = [
            "uuid": fileUuid,
            "name": encryptedName,
            "nameHashed": nameHashed,
            "size": encryptedSize,
            "parent": parentUuid,
            "mime": encryptedMime,
            "metadata": encryptedMetadata,
            "version": 2,
            "chunks": chunkCount,
            "rm": rmKey,
            "uploadKey": uploadKey,
        ]
        _ = try await postAuthenticated(path: "/v3/upload/done", body: doneBody, expectedKey: "data")
    }

    private static let ingestURL = "https://ingest.filen.io"

    private func uploadChunkRaw(
        uuid: String,
        parent: String,
        uploadKey: String,
        chunkIdx: Int,
        hashHex: String,
        body: Data
    ) async throws {
        // URL exactly as filen-sdk-rs/src/api/v3/upload/mod.rs builds it.
        // The chunk body is raw bytes, not multipart and not JSON.
        let urlString = "\(Self.ingestURL)/v3/upload?uuid=\(uuid)&index=\(chunkIdx)&parent=\(parent)&uploadKey=\(uploadKey)&hash=\(hashHex)"
        guard let url = URL(string: urlString) else {
            throw CloudProviderError.invalidResponse
        }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(credentials.apiKey)", forHTTPHeaderField: "Authorization")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        let (data, response) = try await session.upload(for: request, from: body)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let preview = String(data: data, encoding: .utf8)?.prefix(200) ?? ""
            filenLog.error("[Filen] upload chunk \(chunkIdx) failed: HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0): \(preview, privacy: .public)")
            throw CloudProviderError.serverError((response as? HTTPURLResponse)?.statusCode ?? 0)
        }
    }

    /// 32-char random string from `[A-Za-z0-9]`. Used for per-file keys,
    /// upload keys, and the `rm` value the SDK passes to /v3/upload/done.
    private static func randomFilenString(length: Int) -> String {
        let alphabet = Array("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789")
        var bytes = [UInt8](repeating: 0, count: length)
        _ = SecRandomCopyBytes(kSecRandomDefault, length, &bytes)
        return String(bytes.map { alphabet[Int($0) % alphabet.count] })
    }

    private static func guessMimeType(for filename: String) -> String {
        let ext = (filename as NSString).pathExtension.lowercased()
        switch ext {
        case "txt", "md", "log": return "text/plain"
        case "json": return "application/json"
        case "xml": return "application/xml"
        case "html", "htm": return "text/html"
        case "pdf": return "application/pdf"
        case "png": return "image/png"
        case "jpg", "jpeg": return "image/jpeg"
        case "gif": return "image/gif"
        case "webp": return "image/webp"
        case "mp4": return "video/mp4"
        case "mov": return "video/quicktime"
        case "mp3": return "audio/mpeg"
        case "zip": return "application/zip"
        default: return "application/octet-stream"
        }
    }

    // MARK: - Mkdir / trash / rename

    /// Create a new folder under `path`'s parent. The folder's display name
    /// is stored encrypted (master-key "002" envelope) under `name`, and a
    /// non-reversible v2 hash of the lower-cased name goes in `name_hashed`
    /// for server-side search/indexing.
    public func createFolder(at path: String) async throws {
        let parent = (path as NSString).deletingLastPathComponent
        let name = (path as NSString).lastPathComponent
        let parentUuid = try await resolveUuid(forPath: parent.isEmpty ? "/" : parent)
        let masterKey = try activeMasterAesKey()

        let folderMeta: [String: Any] = [
            "name": name,
            "creation": Int64(Date().timeIntervalSince1970),
        ]
        let folderMetaJson = String(data: try JSONSerialization.data(withJSONObject: folderMeta), encoding: .utf8) ?? "{}"
        let encryptedName = try FilenCrypto.encryptMetadata(folderMetaJson, aesKey: masterKey)
        let nameHashed = FilenCrypto.v2HashName(name)
        let newUuid = UUID().uuidString.lowercased()

        let body: [String: Any] = [
            "uuid": newUuid,
            "name": encryptedName,
            "nameHashed": nameHashed,
            "parent": parentUuid,
        ]
        _ = try await postAuthenticated(path: "/v3/dir/create", body: body, expectedKey: "data")
    }

    /// Trash a file or folder by panel path. Filen has two endpoints —
    /// `/v3/dir/trash` and `/v3/file/trash` — both take `{uuid}` and route
    /// the item into the user's trash (not a hard delete).
    public func trashItem(at path: String) async throws {
        // Find the item in its parent's listing so we can pick the right
        // endpoint (dir vs file) and pull the UUID. The CloudFileItem id
        // we minted in `listChildren` carries both as a "d:<uuid>" / "f:<uuid>" tag.
        let parent = (path as NSString).deletingLastPathComponent
        let name = (path as NSString).lastPathComponent
        let entries = try await listFolder(path: parent.isEmpty ? "/" : parent)
        guard let item = entries.first(where: { $0.name == name }) else {
            throw CloudProviderError.notFound(path)
        }
        let uuid = String(item.id.dropFirst(2))
        let endpoint = item.isDirectory ? "/v3/dir/trash" : "/v3/file/trash"
        _ = try await postAuthenticated(path: endpoint, body: ["uuid": uuid], expectedKey: "data")
    }

    /// Rename a file or folder. Filen has different endpoints for each
    /// (`/v3/dir/metadata` vs `/v3/file/metadata`) and they take slightly
    /// different payloads — folders just need the new encrypted folder-meta
    /// blob; files also need a standalone encrypted name (encrypted with
    /// the per-file key, like at upload time).
    public func renameItem(at path: String, to newName: String) async throws {
        let parent = (path as NSString).deletingLastPathComponent
        let oldName = (path as NSString).lastPathComponent
        let entries = try await listFolder(path: parent.isEmpty ? "/" : parent)
        guard let item = entries.first(where: { $0.name == oldName }) else {
            throw CloudProviderError.notFound(path)
        }
        let uuid = String(item.id.dropFirst(2))
        let masterKey = try activeMasterAesKey()
        let nameHashed = FilenCrypto.v2HashName(newName)

        if item.isDirectory {
            let folderMeta: [String: Any] = [
                "name": newName,
                "creation": Int64(item.modificationDate.timeIntervalSince1970),
            ]
            let json = String(data: try JSONSerialization.data(withJSONObject: folderMeta), encoding: .utf8) ?? "{}"
            let encryptedName = try FilenCrypto.encryptMetadata(json, aesKey: masterKey)
            let body: [String: Any] = [
                "uuid": uuid,
                "nameHashed": nameHashed,
                "name": encryptedName,
            ]
            _ = try await postAuthenticated(path: "/v3/dir/metadata", body: body, expectedKey: "data")
        } else {
            // For files we also need to rewrite the master-key-encrypted
            // metadata blob (so listing shows the new name) AND a standalone
            // file-key-encrypted name (server-side index). Reuse the cached
            // file-chunk info to pull the original per-file key.
            guard let info = fileChunkInfo[uuid] else {
                throw CloudProviderError.commandFailed("Filen file metadata not cached — re-open the folder and try again.")
            }
            let fileKeyAes = try FilenCrypto.aesKeyFromV2MasterKey(info.key)
            let fileMeta: [String: Any] = [
                "name": newName,
                "size": info.size,
                "mime": (newName as NSString).pathExtension.isEmpty ? "application/octet-stream" : "application/octet-stream",
                "key": info.key,
                "lastModified": Int64(item.modificationDate.timeIntervalSince1970),
            ]
            let metaJson = String(data: try JSONSerialization.data(withJSONObject: fileMeta), encoding: .utf8) ?? "{}"
            let encryptedMetadata = try FilenCrypto.encryptMetadata(metaJson, aesKey: masterKey)
            let encryptedNameStandalone = try FilenCrypto.encryptMetadata(newName, aesKey: fileKeyAes)
            let body: [String: Any] = [
                "uuid": uuid,
                "name": encryptedNameStandalone,
                "nameHashed": nameHashed,
                "metadata": encryptedMetadata,
            ]
            _ = try await postAuthenticated(path: "/v3/file/metadata", body: body, expectedKey: "data")
        }
    }

    /// The freshest master key is at index 0 (see how `login` prepends the
    /// derived key). Used as the default encryption key for any new
    /// metadata we write.
    private func activeMasterAesKey() throws -> SymmetricKey {
        guard let first = credentials.masterKeys.first else {
            throw CloudProviderError.notAuthenticated
        }
        return try FilenCrypto.aesKeyFromV2MasterKey(first)
    }

    // MARK: - Metadata decryption

    /// Try every master key in order, oldest-first turning into newest-first
    /// because the derived key is at index 0. The first success wins. We
    /// throw an opaque error rather than leaking which key matched, since
    /// metadata strings can be quite verbose in logs.
    private func decryptItemMetadata(_ encrypted: String) throws -> String {
        for key in credentials.masterKeys {
            if let aesKey = try? FilenCrypto.aesKeyFromV2MasterKey(key),
               let plain = try? FilenCrypto.decryptMetadata(encrypted, aesKey: aesKey) {
                return plain
            }
        }
        throw CloudProviderError.invalidResponse
    }

    private func parseFolderMeta(_ json: String) -> (name: String, created: Date?)? {
        guard let data = json.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let name = obj["name"] as? String else { return nil }
        let created = (obj["creation"] as? Double).map(timestampToDate)
        return (name, created)
    }

    private func parseFileMeta(_ json: String) -> (name: String, size: Int64, mime: String, key: String, lastModified: Date, hash: String?)? {
        guard let data = json.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let name = obj["name"] as? String,
              let key = obj["key"] as? String else { return nil }
        let size = (obj["size"] as? NSNumber)?.int64Value ?? 0
        let mime = (obj["mime"] as? String) ?? "application/octet-stream"
        let last = (obj["lastModified"] as? Double).map(timestampToDate) ?? Date.distantPast
        let hash = obj["blake3"] as? String
        return (name, size, mime, key, last, hash)
    }

    // MARK: - HTTP helpers

    private static func fetchAuthInfo(email: String) async throws -> (email: String, authVersion: Int, salt: String, userId: Int64) {
        let body: [String: Any] = ["email": email]
        let response = try await postUnauthenticated(path: "/v3/auth/info", body: body, expectedKey: "data")
        guard let salt = response["salt"] as? String,
              let authVersion = (response["authVersion"] as? NSNumber)?.intValue else {
            throw CloudProviderError.commandFailed("Filen /v3/auth/info returned an unexpected payload.")
        }
        let userId = (response["id"] as? NSNumber)?.int64Value ?? 0
        let respEmail = (response["email"] as? String) ?? email
        return (respEmail, authVersion, salt, userId)
    }

    private static func fetchBaseFolder(apiKey: String) async throws -> String {
        // Unlike every other v3 endpoint we touch, baseFolder is a GET —
        // see filen-sdk-rs/src/api/v3/user/base_folder.rs (`client.get_auth`).
        // Sending a POST returns a generic "wrong method" envelope, which
        // surfaces as "didn't return a root UUID" downstream.
        let url = URL(string: "\(baseURL)/v3/user/baseFolder")!
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.commandFailed("Filen /v3/user/baseFolder failed (HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0))")
        }
        let envelope = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        guard let nested = envelope?["data"] as? [String: Any],
              let uuid = nested["uuid"] as? String else {
            throw CloudProviderError.commandFailed("Filen /v3/user/baseFolder didn't return a root UUID.")
        }
        return uuid
    }

    /// Filen wraps every JSON response in `{ status, message, code, data }`.
    /// On `status: true` we hand back the inner `data` object; on `false`
    /// we throw a CloudProviderError that carries the server's `message`.
    private static func postUnauthenticated(path: String, body: [String: Any], expectedKey: String) async throws -> [String: Any] {
        let url = URL(string: "\(baseURL)\(path)")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let (data, response) = try await URLSession.shared.data(for: request)
        return try Self.parseEnvelope(data: data, response: response, expectedKey: expectedKey, endpoint: path)
    }

    private func postAuthenticated(path: String, body: [String: Any], expectedKey: String) async throws -> [String: Any] {
        let url = URL(string: "\(Self.baseURL)\(path)")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(credentials.apiKey)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let (data, response) = try await session.data(for: request)
        return try Self.parseEnvelope(data: data, response: response, expectedKey: expectedKey, endpoint: path)
    }

    private static func parseEnvelope(data: Data, response: URLResponse, expectedKey: String, endpoint: String) throws -> [String: Any] {
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }
        guard (200...299).contains(http.statusCode) else {
            let body = String(data: data, encoding: .utf8) ?? ""
            filenLog.error("[Filen] \(endpoint, privacy: .public) HTTP \(http.statusCode): \(body.prefix(300), privacy: .public)")
            switch http.statusCode {
            case 401, 403: throw CloudProviderError.notAuthenticated
            case 404: throw CloudProviderError.notFound(endpoint)
            case 429: throw CloudProviderError.rateLimited
            default: throw CloudProviderError.serverError(http.statusCode)
            }
        }
        guard let envelope = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw CloudProviderError.invalidResponse
        }
        if let status = envelope["status"] as? Bool, status == false {
            let msg = (envelope["message"] as? String) ?? "Filen API error"
            throw CloudProviderError.commandFailed(msg)
        }
        // The wrapped payload may be a dict OR an array (e.g. masterKeys list).
        // Both list and dict callers route through expectedKey="data".
        if let nested = envelope[expectedKey] as? [String: Any] {
            return nested
        }
        if envelope[expectedKey] != nil {
            // Caller expected a dict but the data is a non-dict scalar; let
            // them work with the envelope directly.
            return envelope
        }
        return envelope
    }

    private func timestampToDate(_ raw: Double) -> Date {
        // Filen returns timestamps in seconds OR milliseconds depending on
        // the endpoint. Values larger than year-3000-in-seconds are
        // certainly milliseconds — switch units accordingly.
        if raw > 32_503_680_000 {
            return Date(timeIntervalSince1970: raw / 1000)
        }
        return Date(timeIntervalSince1970: raw)
    }

    private static func timestampToDate(_ raw: Double) -> Date {
        if raw > 32_503_680_000 {
            return Date(timeIntervalSince1970: raw / 1000)
        }
        return Date(timeIntervalSince1970: raw)
    }
}

// MARK: - Crypto primitives (CommonCrypto + CryptoKit)

/// All Filen-specific crypto isolated in one place. Every routine here has
/// a 1:1 correspondence with a function in filen-sdk-rs/src/crypto/v2.rs;
/// see the header comment in FilenAPIClient for the full spec. Bugs in
/// this file present as "wrong password" or "garbled file names", which
/// is why each method is named for the exact Filen concept it implements.
enum FilenCrypto {
    struct V2LoginDerivation {
        /// 64-char hex string. Used as the input to `aesKeyFromV2MasterKey`
        /// to get the AES-256 key for decrypting the master-keys blob.
        let masterKeyHex: String
        /// 128-char hex string sent verbatim as the `password` field in
        /// /v3/login.
        let loginPasswordHex: String
    }

    /// PBKDF2-HMAC-SHA512 with 200_000 iterations and a 64-byte output, then
    /// the split-then-SHA512-the-second-half dance described in
    /// filen-sdk-rs/src/crypto/v2.rs::derive_password_and_mk.
    public static func deriveV2Login(password: String, salt: String) throws -> V2LoginDerivation {
        // Filen feeds the *bytes of the salt string* to PBKDF2 — not a
        // base64 or hex decode — matching the comment in filen-types/api/auth/info.rs
        // ("this is not base64 or hex encoded, so probably bad practice").
        let derived = try pbkdf2SHA512(
            password: Array(password.utf8),
            salt: Array(salt.utf8),
            iterations: 200_000,
            outputBytes: 64
        )
        let hex = derived.map { String(format: "%02x", $0) }.joined()
        // First 64 chars = master key string; remaining 64 chars get SHA-512'd
        // to produce the login password.
        let masterKeyHex = String(hex.prefix(64))
        let secondHalf = String(hex.suffix(hex.count - 64))
        let secondHash = Data(SHA512.hash(data: Data(secondHalf.utf8)))
        let loginPasswordHex = secondHash.map { String(format: "%02x", $0) }.joined()
        return V2LoginDerivation(masterKeyHex: masterKeyHex, loginPasswordHex: loginPasswordHex)
    }

    /// Filen's "V2Key" derives the AES-256 cipher key from the 64-char hex
    /// master-key string by running PBKDF2(key, key, 1 iter, 32 bytes). It
    /// looks redundant — using the same value as password and salt with one
    /// iteration is essentially a hash — but it's the algorithm the SDK uses,
    /// so we mirror it.
    public static func aesKeyFromV2MasterKey(_ keyAscii: String) throws -> SymmetricKey {
        let bytes = try pbkdf2SHA512(
            password: Array(keyAscii.utf8),
            salt: Array(keyAscii.utf8),
            iterations: 1,
            outputBytes: 32
        )
        return SymmetricKey(data: bytes)
    }

    /// Encrypt a single plaintext chunk for upload. Output layout matches
    /// what `decryptFileChunk` consumes: 12-byte raw nonce || ciphertext
    /// || 16-byte tag. AES-256-GCM, no AAD.
    public static func encryptFileChunk(_ plain: Data, aesKey: SymmetricKey) throws -> Data {
        var nonceBytes = [UInt8](repeating: 0, count: 12)
        _ = SecRandomCopyBytes(kSecRandomDefault, 12, &nonceBytes)
        let nonce = try AES.GCM.Nonce(data: Data(nonceBytes))
        let sealed = try AES.GCM.seal(plain, using: aesKey, nonce: nonce)
        return Data(nonceBytes) + sealed.ciphertext + sealed.tag
    }

    /// Decrypt a single file chunk:
    ///   12-byte raw nonce || ciphertext || 16-byte tag.
    /// Same AES-256-GCM cipher as metadata, just a different envelope —
    /// see filen-sdk-rs/src/crypto/shared.rs::decrypt_data.
    public static func decryptFileChunk(_ data: Data, aesKey: SymmetricKey) throws -> Data {
        guard data.count >= 12 + 16 else {
            throw CloudProviderError.invalidResponse
        }
        let nonceData = data.prefix(12)
        let body = data.dropFirst(12)
        let tag = body.suffix(16)
        let ciphertext = body.prefix(body.count - 16)
        let nonce = try AES.GCM.Nonce(data: nonceData)
        let box = try AES.GCM.SealedBox(nonce: nonce, ciphertext: ciphertext, tag: tag)
        return try AES.GCM.open(box, using: aesKey)
    }

    /// Encrypt a string with the v2 "002" metadata envelope. The output is
    /// stable across runs *for a fixed nonce*; we pick a fresh 12-byte
    /// ASCII nonce per call (matching the SDK's BadNonce — chars from
    /// `[A-Za-z0-9]` so the server can store the envelope as a single
    /// UTF-8 string).
    public static func encryptMetadata(_ plaintext: String, aesKey: SymmetricKey) throws -> String {
        var nonceBytes = [UInt8](repeating: 0, count: 12)
        // The SDK uses a 62-character alphabet for the printable nonce.
        // SecRandomCopyBytes-derived index keeps the nonce uniformly random.
        let alphabet: [UInt8] = Array("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789".utf8)
        var rand = [UInt8](repeating: 0, count: 12)
        _ = SecRandomCopyBytes(kSecRandomDefault, 12, &rand)
        for i in 0..<12 {
            nonceBytes[i] = alphabet[Int(rand[i]) % alphabet.count]
        }
        let nonce = try AES.GCM.Nonce(data: Data(nonceBytes))
        let sealed = try AES.GCM.seal(Data(plaintext.utf8), using: aesKey, nonce: nonce)
        let body = sealed.ciphertext + sealed.tag
        guard let asciiNonce = String(data: Data(nonceBytes), encoding: .utf8) else {
            throw CloudProviderError.commandFailed("Couldn't render Filen metadata nonce as ASCII.")
        }
        return "002" + asciiNonce + body.base64EncodedString()
    }

    /// v2 name hash: SHA-1(hex(SHA-512(lowercase(name).utf8))) → 20 bytes
    /// → 40-char lowercase hex. Mirrors filen-sdk-rs/src/crypto/v2.rs::hash.
    /// Used as the server-side index value for filename / foldername
    /// searches; the server never sees the cleartext name.
    public static func v2HashName(_ name: String) -> String {
        let lower = name.lowercased()
        let sha512 = Data(SHA512.hash(data: Data(lower.utf8)))
        let hexBytes = Array(sha512.map { String(format: "%02x", $0) }.joined().utf8)
        let sha1 = Data(Insecure.SHA1.hash(data: Data(hexBytes)))
        return sha1.map { String(format: "%02x", $0) }.joined()
    }

    /// Decrypt a Filen "002" metadata envelope:
    ///   "002" || 12-byte ASCII nonce || base64(ciphertext || 16-byte tag).
    /// AES-256-GCM, no AAD, 12-byte nonce, 16-byte tag.
    public static func decryptMetadata(_ encrypted: String, aesKey: SymmetricKey) throws -> String {
        guard encrypted.count >= 15, encrypted.hasPrefix("002") else {
            throw CloudProviderError.invalidResponse
        }
        let nonceASCII = encrypted.dropFirst(3).prefix(12)
        let base64Part = encrypted.dropFirst(15)
        guard let nonceData = String(nonceASCII).data(using: .utf8),
              nonceData.count == 12 else {
            throw CloudProviderError.invalidResponse
        }
        guard let cipherAndTag = Data(base64Encoded: String(base64Part)) else {
            throw CloudProviderError.invalidResponse
        }
        guard cipherAndTag.count >= 16 else {
            throw CloudProviderError.invalidResponse
        }
        let tag = cipherAndTag.suffix(16)
        let ciphertext = cipherAndTag.prefix(cipherAndTag.count - 16)
        let nonce = try AES.GCM.Nonce(data: nonceData)
        let box = try AES.GCM.SealedBox(nonce: nonce, ciphertext: ciphertext, tag: tag)
        let plain = try AES.GCM.open(box, using: aesKey)
        guard let str = String(data: plain, encoding: .utf8) else {
            throw CloudProviderError.invalidResponse
        }
        return str
    }

    // MARK: - PBKDF2

    /// PBKDF2-HMAC-SHA512 via CommonCrypto. CryptoKit doesn't expose PBKDF2
    /// directly on macOS 14, so we drop to the C API.
    public static func pbkdf2SHA512(password: [UInt8], salt: [UInt8], iterations: UInt32, outputBytes: Int) throws -> Data {
        var derived = [UInt8](repeating: 0, count: outputBytes)
        let status = password.withUnsafeBufferPointer { pwPtr in
            salt.withUnsafeBufferPointer { saltPtr in
                CCKeyDerivationPBKDF(
                    CCPBKDFAlgorithm(kCCPBKDF2),
                    pwPtr.baseAddress?.withMemoryRebound(to: Int8.self, capacity: password.count) { $0 },
                    password.count,
                    saltPtr.baseAddress,
                    salt.count,
                    CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA512),
                    iterations,
                    &derived,
                    outputBytes
                )
            }
        }
        guard status == kCCSuccess else {
            throw CloudProviderError.commandFailed("PBKDF2 derivation failed (status=\(status))")
        }
        return Data(derived)
    }
}
