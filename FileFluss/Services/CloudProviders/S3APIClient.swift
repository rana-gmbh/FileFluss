import Foundation
import CryptoKit
import os

private let s3Log = Logger(subsystem: "com.rana.FileFluss", category: "s3")

struct S3Credentials: Codable, Sendable {
    let accessKeyId: String
    let secretAccessKey: String
    let region: String
    let displayName: String
    /// Optional custom endpoint base host for S3-compatible services like
    /// Synology C2 Storage, Cloudflare R2, MinIO, Wasabi, etc. (e.g.
    /// `eu-001.s3.synologyc2.net`). When nil the client uses AWS S3 defaults.
    let endpointHost: String?

    init(
        accessKeyId: String,
        secretAccessKey: String,
        region: String,
        displayName: String,
        endpointHost: String? = nil
    ) {
        self.accessKeyId = accessKeyId
        self.secretAccessKey = secretAccessKey
        self.region = region
        self.displayName = displayName
        self.endpointHost = endpointHost
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        accessKeyId = try c.decode(String.self, forKey: .accessKeyId)
        secretAccessKey = try c.decode(String.self, forKey: .secretAccessKey)
        region = try c.decode(String.self, forKey: .region)
        displayName = try c.decode(String.self, forKey: .displayName)
        // Older v1.0.x AWS-only entries don't have this key — default to nil.
        endpointHost = try c.decodeIfPresent(String.self, forKey: .endpointHost)
    }
}

/// Internal error raised by the validator to signal "wrong region for
/// this bucket"; the calling helper catches it, updates the per-bucket
/// region cache, and retries the request once.
private enum S3InternalError: Error {
    case bucketRegionRedirect(newRegion: String)
}

/// Suppresses URLSession's built-in redirect handling so SigV4 errors
/// surface cleanly — we want to handle 301/307 ourselves so we can
/// re-sign for the correct region.
final class NoRedirectDelegate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession,
                    task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest,
                    completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

actor S3APIClient {
    let credentials: S3Credentials
    private let session: URLSession
    /// Cache of bucket → region discovered via 301 redirects. Avoids a
    /// round-trip cost on every subsequent request for the same bucket.
    private var bucketRegions: [String: String] = [:]

    init(credentials: S3Credentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 600
        self.session = URLSession(configuration: config, delegate: NoRedirectDelegate(), delegateQueue: nil)
    }

    private func regionFor(bucket: String) -> String {
        bucketRegions[bucket] ?? credentials.region
    }

    /// Top-level host (no bucket) used for `ListBuckets`. Custom-endpoint
    /// services use the base host as-is; AWS uses `s3.<region>.amazonaws.com`.
    private func topLevelHost(region: String) -> String {
        if let custom = credentials.endpointHost, !custom.isEmpty {
            return custom
        }
        return "s3.\(region).amazonaws.com"
    }

    /// Per-bucket host. Custom-endpoint services prepend the bucket name
    /// to the base host (`<bucket>.<endpointHost>`); AWS uses
    /// `<bucket>.s3.<region>.amazonaws.com`.
    private func bucketHost(_ bucket: String, region: String) -> String {
        if let custom = credentials.endpointHost, !custom.isEmpty {
            return "\(bucket).\(custom)"
        }
        return "\(bucket).s3.\(region).amazonaws.com"
    }

    /// Runs `op`, retrying once if the server returned a 301 with the
    /// real region — caches the region so future calls skip the bounce.
    private func withResolvedRegion<T>(bucket: String, _ op: () async throws -> T) async throws -> T {
        do {
            return try await op()
        } catch let S3InternalError.bucketRegionRedirect(newRegion) {
            s3Log.info("[S3] Bucket \(bucket) lives in \(newRegion), retrying")
            bucketRegions[bucket] = newRegion
            return try await op()
        }
    }

    // MARK: - Authentication / connectivity check

    /// Validates the credentials by issuing a minimal `ListBuckets` request
    /// (`GET /`). On success, returns a `S3Credentials` with a sensible
    /// display name so the account label can show the access-key id.
    static func authenticate(accessKeyId: String, secretAccessKey: String, region: String) async throws -> S3Credentials {
        let displayName = "AWS S3 (\(maskedAccessKey(accessKeyId)))"
        let creds = S3Credentials(
            accessKeyId: accessKeyId,
            secretAccessKey: secretAccessKey,
            region: region,
            displayName: displayName
        )
        let client = S3APIClient(credentials: creds)
        _ = try await client.listBuckets()
        s3Log.info("[S3] Authenticated for region \(region)")
        return creds
    }

    func userDisplayName() -> String { credentials.displayName }

    // MARK: - Public listing API

    /// Lists the items at the given absolute panel path.
    ///   - "/"         → buckets as folders.
    ///   - "/bucket"   → top-level entries inside `bucket`.
    ///   - "/bucket/sub" → entries inside `sub/` within `bucket`.
    func listFolder(path: String) async throws -> [CloudFileItem] {
        let cleaned = path.hasPrefix("/") ? String(path.dropFirst()) : path
        if cleaned.isEmpty {
            return try await listBuckets()
        }
        let parts = cleaned.split(separator: "/", maxSplits: 1, omittingEmptySubsequences: false).map(String.init)
        let bucket = parts[0]
        let prefix: String
        if parts.count > 1 {
            let p = parts[1]
            prefix = p.hasSuffix("/") ? p : p + "/"
        } else {
            prefix = ""
        }
        return try await listObjects(bucket: bucket, prefix: prefix, basePath: "/" + bucket + "/")
    }

    func listBuckets() async throws -> [CloudFileItem] {
        let host = topLevelHost(region: credentials.region)
        guard !host.isEmpty, let url = URL(string: "https://\(host)/") else {
            throw CloudProviderError.commandFailed("S3 endpoint URL is invalid (host: \"\(host)\"). Double-check the Endpoint URL field.")
        }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        try sign(&request, host: host, payloadHash: emptyPayloadHash, service: "s3")

        let (data, response) = try await session.data(for: request)
        try validate(response, body: data)

        let parsed = try ListBucketsParser.parse(data: data)
        return parsed.map { name, creationDate in
            CloudFileItem(
                id: "/" + name,
                name: name,
                path: "/" + name,
                isDirectory: true,
                size: 0,
                modificationDate: creationDate ?? .distantPast,
                checksum: nil
            )
        }
    }

    /// Single page of `ListObjectsV2` results turned into folder rows
    /// (CommonPrefixes) plus file rows (Contents). `basePath` is prepended
    /// to every emitted item path so the caller doesn't have to.
    func listObjects(bucket: String, prefix: String, basePath: String) async throws -> [CloudFileItem] {
        try await withResolvedRegion(bucket: bucket) {
            try await listObjectsRaw(bucket: bucket, prefix: prefix, basePath: basePath)
        }
    }

    private func listObjectsRaw(bucket: String, prefix: String, basePath: String) async throws -> [CloudFileItem] {
        var continuationToken: String?
        var folders: [CloudFileItem] = []
        var files: [CloudFileItem] = []
        let region = regionFor(bucket: bucket)

        repeat {
            var components = URLComponents()
            components.scheme = "https"
            components.host = bucketHost(bucket, region: region)
            components.path = "/"
            var items: [URLQueryItem] = [
                URLQueryItem(name: "list-type", value: "2"),
                URLQueryItem(name: "delimiter", value: "/"),
                URLQueryItem(name: "prefix", value: prefix),
                URLQueryItem(name: "max-keys", value: "1000")
            ]
            if let token = continuationToken {
                items.append(URLQueryItem(name: "continuation-token", value: token))
            }
            components.queryItems = items
            guard let url = components.url else { throw CloudProviderError.invalidResponse }

            var request = URLRequest(url: url)
            request.httpMethod = "GET"
            try sign(&request, host: components.host!, payloadHash: emptyPayloadHash, service: "s3", region: region)

            let (data, response) = try await session.data(for: request)
            try validate(response, body: data)

            let page = try ListObjectsV2Parser.parse(data: data)
            for prefixName in page.commonPrefixes {
                let folderName = lastPathComponent(stripTrailingSlash(prefixName), under: prefix)
                guard !folderName.isEmpty else { continue }
                folders.append(CloudFileItem(
                    id: basePath + prefixName,
                    name: folderName,
                    path: basePath + stripTrailingSlash(prefixName),
                    isDirectory: true,
                    size: 0,
                    modificationDate: .distantPast,
                    checksum: nil
                ))
            }
            for object in page.contents {
                // Skip the synthetic key that represents the folder itself.
                if object.key == prefix { continue }
                let name = lastPathComponent(object.key, under: prefix)
                guard !name.isEmpty else { continue }
                files.append(CloudFileItem(
                    id: basePath + object.key,
                    name: name,
                    path: basePath + object.key,
                    isDirectory: false,
                    size: object.size,
                    modificationDate: object.lastModified ?? .distantPast,
                    checksum: object.etag
                ))
            }
            continuationToken = page.nextContinuationToken
        } while continuationToken != nil

        return folders + files
    }

    // MARK: - File operations

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let (bucket, key) = try splitPath(remotePath)
        try await withResolvedRegion(bucket: bucket) {
            let region = regionFor(bucket: bucket)
            let host = bucketHost(bucket, region: region)
            guard let url = URL(string: "https://\(host)/\(uriEncodeKey(key))") else {
                throw CloudProviderError.invalidResponse
            }
            var request = URLRequest(url: url)
            request.httpMethod = "GET"
            try sign(&request, host: host, payloadHash: emptyPayloadHash, service: "s3", region: region)

            let progressDelegate = onBytes.map { ByteProgressDelegate(onBytes: $0) }
            let (tmp, response) = try await session.download(for: request, delegate: progressDelegate)
            try validate(response, body: nil)

            try? FileManager.default.removeItem(at: localURL)
            try FileManager.default.moveItem(at: tmp, to: localURL)
        }
    }

    func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let (bucket, key) = try splitPath(remotePath)
        try await withResolvedRegion(bucket: bucket) {
            let region = regionFor(bucket: bucket)
            let host = bucketHost(bucket, region: region)
            guard let url = URL(string: "https://\(host)/\(uriEncodeKey(key))") else {
                throw CloudProviderError.invalidResponse
            }

            // S3 single-PUT cap is 5 GB — see maxUploadFileSize in the provider.
            let attrs = try FileManager.default.attributesOfItem(atPath: localURL.path)
            let size = (attrs[.size] as? NSNumber)?.int64Value ?? 0
            let bodyHash = try sha256Hex(of: localURL)

            var request = URLRequest(url: url)
            request.httpMethod = "PUT"
            request.setValue("\(size)", forHTTPHeaderField: "Content-Length")
            if let mime = mimeType(for: localURL) {
                request.setValue(mime, forHTTPHeaderField: "Content-Type")
            }
            try sign(&request, host: host, payloadHash: bodyHash, service: "s3", region: region)

            let progressDelegate = onBytes.map { ByteProgressDelegate(onBytes: $0) }
            let (data, response) = try await session.upload(for: request, fromFile: localURL, delegate: progressDelegate)
            try validate(response, body: data)
        }
    }

    func deleteItem(at remotePath: String) async throws {
        let (bucket, key) = try splitPath(remotePath)
        // Folders in S3 are prefixes — to "delete" a folder we delete every
        // object underneath it.
        if key.isEmpty {
            // Bucket-level delete is intentionally not exposed; bucket
            // creation/deletion belongs in the AWS console.
            throw CloudProviderError.notImplemented
        }

        // Test whether `path` looks like a folder (no exact-key HEAD hit
        // but objects exist underneath). If so, recursively delete.
        if try await isFolder(bucket: bucket, key: key) {
            try await deleteFolder(bucket: bucket, prefix: key.hasSuffix("/") ? key : key + "/")
            return
        }

        try await deleteObject(bucket: bucket, key: key)
    }

    private func deleteObject(bucket: String, key: String) async throws {
        try await withResolvedRegion(bucket: bucket) {
            let region = regionFor(bucket: bucket)
            let host = bucketHost(bucket, region: region)
            guard let url = URL(string: "https://\(host)/\(uriEncodeKey(key))") else {
                throw CloudProviderError.invalidResponse
            }
            var request = URLRequest(url: url)
            request.httpMethod = "DELETE"
            try sign(&request, host: host, payloadHash: emptyPayloadHash, service: "s3", region: region)

            let (data, response) = try await session.data(for: request)
            try validate(response, body: data)
        }
    }

    private func deleteFolder(bucket: String, prefix: String) async throws {
        // Walk every object under the prefix (without delimiter) and
        // delete them one at a time. S3 has a Delete-Multiple-Objects API
        // we could use to batch this, but per-object is simpler and
        // matches FileFluss's per-item progress UI.
        var continuationToken: String?
        repeat {
            let token = continuationToken
            let page: ListObjectsV2Page = try await withResolvedRegion(bucket: bucket) {
                let region = regionFor(bucket: bucket)
                var components = URLComponents()
                components.scheme = "https"
                components.host = bucketHost(bucket, region: region)
                components.path = "/"
                var items: [URLQueryItem] = [
                    URLQueryItem(name: "list-type", value: "2"),
                    URLQueryItem(name: "prefix", value: prefix),
                    URLQueryItem(name: "max-keys", value: "1000")
                ]
                if let t = token {
                    items.append(URLQueryItem(name: "continuation-token", value: t))
                }
                components.queryItems = items
                guard let url = components.url else { throw CloudProviderError.invalidResponse }

                var request = URLRequest(url: url)
                request.httpMethod = "GET"
                try sign(&request, host: components.host!, payloadHash: emptyPayloadHash, service: "s3", region: region)
                let (data, response) = try await session.data(for: request)
                try validate(response, body: data)
                return try ListObjectsV2Parser.parse(data: data)
            }

            for object in page.contents {
                try await deleteObject(bucket: bucket, key: object.key)
            }
            continuationToken = page.nextContinuationToken
        } while continuationToken != nil
    }

    func createFolder(at remotePath: String) async throws {
        // S3 folders are virtual — create a zero-byte object whose key
        // ends in "/" so subsequent listings show the empty prefix.
        let (bucket, key) = try splitPath(remotePath)
        let folderKey = key.hasSuffix("/") ? key : key + "/"
        try await withResolvedRegion(bucket: bucket) {
            let region = regionFor(bucket: bucket)
            let host = bucketHost(bucket, region: region)
            guard let url = URL(string: "https://\(host)/\(uriEncodeKey(folderKey))") else {
                throw CloudProviderError.invalidResponse
            }
            var request = URLRequest(url: url)
            request.httpMethod = "PUT"
            request.setValue("0", forHTTPHeaderField: "Content-Length")
            try sign(&request, host: host, payloadHash: emptyPayloadHash, service: "s3", region: region)

            let (data, response) = try await session.upload(for: request, from: Data())
            try validate(response, body: data)
        }
    }

    func renameItem(at remotePath: String, to newName: String) async throws {
        let (bucket, key) = try splitPath(remotePath)
        let parent = (key as NSString).deletingLastPathComponent
        let newKey = parent.isEmpty ? newName : parent + "/" + newName
        try await copyObject(sourceBucket: bucket, sourceKey: key, destBucket: bucket, destKey: newKey)
        try await deleteObject(bucket: bucket, key: key)
    }

    func moveItem(at remotePath: String, toPath newPath: String) async throws {
        let (sb, sk) = try splitPath(remotePath)
        let (db, dk) = try splitPath(newPath)
        try await copyObject(sourceBucket: sb, sourceKey: sk, destBucket: db, destKey: dk)
        try await deleteObject(bucket: sb, key: sk)
    }

    func copyItem(at remotePath: String, toPath newPath: String) async throws {
        let (sb, sk) = try splitPath(remotePath)
        let (db, dk) = try splitPath(newPath)
        try await copyObject(sourceBucket: sb, sourceKey: sk, destBucket: db, destKey: dk)
    }

    private func copyObject(sourceBucket: String, sourceKey: String, destBucket: String, destKey: String) async throws {
        // For cross-bucket copies, both buckets must be in the same
        // region. We resolve the destination region (which AWS uses for
        // the request) and trust the source's region matches; if not,
        // AWS will reject with a clear "wrong region" message.
        try await withResolvedRegion(bucket: destBucket) {
            let region = regionFor(bucket: destBucket)
            let host = bucketHost(destBucket, region: region)
            guard let url = URL(string: "https://\(host)/\(uriEncodeKey(destKey))") else {
                throw CloudProviderError.invalidResponse
            }
            var request = URLRequest(url: url)
            request.httpMethod = "PUT"
            request.setValue("/\(sourceBucket)/\(uriEncodeKey(sourceKey))", forHTTPHeaderField: "x-amz-copy-source")
            try sign(&request, host: host, payloadHash: emptyPayloadHash, service: "s3", region: region)
            let (data, response) = try await session.data(for: request)
            try validate(response, body: data)
        }
    }

    func getFileInfo(at remotePath: String) async throws -> CloudFileItem {
        let (bucket, key) = try splitPath(remotePath)
        return try await withResolvedRegion(bucket: bucket) {
            let region = regionFor(bucket: bucket)
            let host = bucketHost(bucket, region: region)
            guard let url = URL(string: "https://\(host)/\(uriEncodeKey(key))") else {
                throw CloudProviderError.invalidResponse
            }
            var request = URLRequest(url: url)
            request.httpMethod = "HEAD"
            try sign(&request, host: host, payloadHash: emptyPayloadHash, service: "s3", region: region)
            let (_, response) = try await session.data(for: request)
            try validate(response, body: nil)

            let http = response as! HTTPURLResponse
            let size = (http.value(forHTTPHeaderField: "Content-Length") as NSString?)?.longLongValue ?? 0
            let etag = http.value(forHTTPHeaderField: "ETag")
            let lastModified = (http.value(forHTTPHeaderField: "Last-Modified")).flatMap(parseHTTPDate)

            let name = (key as NSString).lastPathComponent
            return CloudFileItem(
                id: remotePath,
                name: name,
                path: remotePath,
                isDirectory: false,
                size: size,
                modificationDate: lastModified ?? .distantPast,
                checksum: etag
            )
        }
    }

    func folderSize(path: String) async throws -> Int64 {
        let cleaned = path.hasPrefix("/") ? String(path.dropFirst()) : path
        if cleaned.isEmpty {
            // Sum across every bucket — too expensive; return 0.
            return 0
        }
        let parts = cleaned.split(separator: "/", maxSplits: 1, omittingEmptySubsequences: false).map(String.init)
        let bucket = parts[0]
        var prefix = parts.count > 1 ? parts[1] : ""
        if !prefix.isEmpty && !prefix.hasSuffix("/") { prefix += "/" }

        var total: Int64 = 0
        var continuationToken: String?
        repeat {
            let token = continuationToken
            let page: ListObjectsV2Page = try await withResolvedRegion(bucket: bucket) {
                let region = regionFor(bucket: bucket)
                var components = URLComponents()
                components.scheme = "https"
                components.host = bucketHost(bucket, region: region)
                components.path = "/"
                var items: [URLQueryItem] = [
                    URLQueryItem(name: "list-type", value: "2"),
                    URLQueryItem(name: "prefix", value: prefix),
                    URLQueryItem(name: "max-keys", value: "1000")
                ]
                if let t = token {
                    items.append(URLQueryItem(name: "continuation-token", value: t))
                }
                components.queryItems = items
                guard let url = components.url else { throw CloudProviderError.invalidResponse }
                var request = URLRequest(url: url)
                request.httpMethod = "GET"
                try sign(&request, host: components.host!, payloadHash: emptyPayloadHash, service: "s3", region: region)
                let (data, response) = try await session.data(for: request)
                try validate(response, body: data)
                return try ListObjectsV2Parser.parse(data: data)
            }
            for object in page.contents { total += object.size }
            continuationToken = page.nextContinuationToken
        } while continuationToken != nil
        return total
    }

    // MARK: - Helpers

    private func isFolder(bucket: String, key: String) async throws -> Bool {
        // A "folder" either ends in "/" or has objects underneath it.
        let probe = key.hasSuffix("/") ? key : key + "/"
        return try await withResolvedRegion(bucket: bucket) {
            let region = regionFor(bucket: bucket)
            var components = URLComponents()
            components.scheme = "https"
            components.host = bucketHost(bucket, region: region)
            components.path = "/"
            components.queryItems = [
                URLQueryItem(name: "list-type", value: "2"),
                URLQueryItem(name: "prefix", value: probe),
                URLQueryItem(name: "max-keys", value: "1")
            ]
            guard let url = components.url else { return false }
            var request = URLRequest(url: url)
            request.httpMethod = "GET"
            try sign(&request, host: components.host!, payloadHash: emptyPayloadHash, service: "s3", region: region)
            let (data, response) = try await session.data(for: request)
            try validate(response, body: data)
            let page = try ListObjectsV2Parser.parse(data: data)
            return !page.contents.isEmpty || !page.commonPrefixes.isEmpty
        }
    }

    private func splitPath(_ remotePath: String) throws -> (bucket: String, key: String) {
        let cleaned = remotePath.hasPrefix("/") ? String(remotePath.dropFirst()) : remotePath
        guard !cleaned.isEmpty else { throw CloudProviderError.invalidResponse }
        let parts = cleaned.split(separator: "/", maxSplits: 1, omittingEmptySubsequences: false).map(String.init)
        let bucket = parts[0]
        let key = parts.count > 1 ? parts[1] : ""
        return (bucket, key)
    }

    private func validate(_ response: URLResponse, body: Data?) throws {
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }
        guard !(200..<300).contains(http.statusCode) else { return }

        // 301 PermanentRedirect / 307 TemporaryRedirect from S3 means
        // "you hit the wrong region for this bucket". Surface the actual
        // region so the caller can retry against the right endpoint.
        if http.statusCode == 301 || http.statusCode == 307 {
            if let newRegion = http.value(forHTTPHeaderField: "x-amz-bucket-region"),
               !newRegion.isEmpty {
                throw S3InternalError.bucketRegionRedirect(newRegion: newRegion)
            }
            // Fallback: parse <Region> out of the response body if header
            // wasn't set (rare, but documented for legacy responses).
            if let body, let bodyRegion = S3RegionParser.parse(data: body), !bodyRegion.isEmpty {
                throw S3InternalError.bucketRegionRedirect(newRegion: bodyRegion)
            }
        }

        let message = body.flatMap { S3ErrorParser.parse(data: $0) }
        s3Log.error("[S3] HTTP \(http.statusCode): \(message ?? "<no body>")")
        switch http.statusCode {
        case 401, 403:
            // S3-compatible services return a structured <Error> body for
            // most 401/403 — surfacing it makes "Server error (HTTP 403)"
            // turn into something like "AccessDenied: Signature mismatch."
            if let message {
                throw CloudProviderError.commandFailed(message)
            }
            throw CloudProviderError.unauthorized
        case 404: throw CloudProviderError.notFound(message ?? "Not Found")
        case 429, 503: throw CloudProviderError.rateLimited
        default:
            if let message {
                throw CloudProviderError.commandFailed("S3 error (HTTP \(http.statusCode)): \(message)")
            }
            throw CloudProviderError.serverError(http.statusCode)
        }
    }

    // MARK: - Path helpers

    private func stripTrailingSlash(_ s: String) -> String {
        s.hasSuffix("/") ? String(s.dropLast()) : s
    }

    private func lastPathComponent(_ key: String, under prefix: String) -> String {
        let trimmed = key.hasPrefix(prefix) ? String(key.dropFirst(prefix.count)) : key
        // Drop any further trailing slash in case caller still has it.
        let noTrailing = trimmed.hasSuffix("/") ? String(trimmed.dropLast()) : trimmed
        return (noTrailing as NSString).lastPathComponent
    }

    private func mimeType(for url: URL) -> String? {
        if let utType = try? url.resourceValues(forKeys: [.contentTypeKey]).contentType {
            return utType.preferredMIMEType
        }
        return nil
    }

    // MARK: - SigV4 signing

    private static let emptyPayloadHashConst = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    private var emptyPayloadHash: String { Self.emptyPayloadHashConst }

    /// Adds the `Authorization`, `x-amz-date`, `x-amz-content-sha256`, and
    /// `Host` headers required by AWS Signature V4 to `request`. `region`
    /// must match the region of the URL host — bucket-scoped requests
    /// pass the bucket's resolved region, top-level (ListBuckets) pass
    /// the credentials' default region.
    private func sign(_ request: inout URLRequest, host: String, payloadHash: String, service: String, region: String? = nil) throws {
        let signingRegion = region ?? credentials.region
        guard let url = request.url else { throw CloudProviderError.invalidResponse }

        let now = Date()
        let amzDate = Self.amzDateFormatter.string(from: now)        // e.g. 20260510T123045Z
        let dateStamp = Self.dateStampFormatter.string(from: now)    // e.g. 20260510

        request.setValue(host, forHTTPHeaderField: "Host")
        request.setValue(amzDate, forHTTPHeaderField: "x-amz-date")
        request.setValue(payloadHash, forHTTPHeaderField: "x-amz-content-sha256")

        // Build canonical request
        let method = request.httpMethod ?? "GET"
        let canonicalURI: String
        if let path = url.path(percentEncoded: false).addingPercentEncoding(withAllowedCharacters: Self.uriAllowed) {
            canonicalURI = path.isEmpty ? "/" : path
        } else {
            canonicalURI = "/"
        }
        let canonicalQuery = canonicalQueryString(from: url)

        // Headers we sign — keep this list aligned with what we set above.
        var signedHeaders: [(String, String)] = [
            ("host", host),
            ("x-amz-content-sha256", payloadHash),
            ("x-amz-date", amzDate)
        ]
        if let copySource = request.value(forHTTPHeaderField: "x-amz-copy-source") {
            signedHeaders.append(("x-amz-copy-source", copySource))
        }
        signedHeaders.sort { $0.0 < $1.0 }
        let canonicalHeaders = signedHeaders.map { "\($0.0):\(trimWhitespace($0.1))\n" }.joined()
        let signedHeadersString = signedHeaders.map { $0.0 }.joined(separator: ";")

        let canonicalRequest = [
            method,
            canonicalURI,
            canonicalQuery,
            canonicalHeaders,
            signedHeadersString,
            payloadHash
        ].joined(separator: "\n")

        // String to sign
        let credentialScope = "\(dateStamp)/\(signingRegion)/\(service)/aws4_request"
        let stringToSign = [
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            sha256Hex(canonicalRequest)
        ].joined(separator: "\n")

        // Signing key
        let kDate = hmac(key: Data(("AWS4" + credentials.secretAccessKey).utf8), message: dateStamp)
        let kRegion = hmac(key: kDate, message: signingRegion)
        let kService = hmac(key: kRegion, message: service)
        let kSigning = hmac(key: kService, message: "aws4_request")
        let signature = hmac(key: kSigning, message: stringToSign).map { String(format: "%02x", $0) }.joined()

        let authorization =
            "AWS4-HMAC-SHA256 " +
            "Credential=\(credentials.accessKeyId)/\(credentialScope), " +
            "SignedHeaders=\(signedHeadersString), " +
            "Signature=\(signature)"
        request.setValue(authorization, forHTTPHeaderField: "Authorization")
    }

    private func canonicalQueryString(from url: URL) -> String {
        guard let components = URLComponents(url: url, resolvingAgainstBaseURL: false),
              let items = components.queryItems else { return "" }
        let encoded = items.map { item -> (String, String) in
            let name = encodeQuery(item.name)
            let value = encodeQuery(item.value ?? "")
            return (name, value)
        }
        let sorted = encoded.sorted { lhs, rhs in
            lhs.0 == rhs.0 ? lhs.1 < rhs.1 : lhs.0 < rhs.0
        }
        return sorted.map { "\($0.0)=\($0.1)" }.joined(separator: "&")
    }

    private func encodeQuery(_ s: String) -> String {
        s.addingPercentEncoding(withAllowedCharacters: Self.queryAllowed) ?? s
    }

    private func trimWhitespace(_ s: String) -> String {
        s.trimmingCharacters(in: .whitespaces)
    }

    private func hmac(key: Data, message: String) -> Data {
        let signed = HMAC<SHA256>.authenticationCode(for: Data(message.utf8), using: SymmetricKey(data: key))
        return Data(signed)
    }

    private func sha256Hex(_ s: String) -> String {
        let digest = SHA256.hash(data: Data(s.utf8))
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    private func sha256Hex(of fileURL: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: fileURL)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        let digest = hasher.finalize()
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    private static let amzDateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.timeZone = TimeZone(identifier: "UTC")
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyyMMdd'T'HHmmss'Z'"
        return f
    }()

    private static let dateStampFormatter: DateFormatter = {
        let f = DateFormatter()
        f.timeZone = TimeZone(identifier: "UTC")
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyyMMdd"
        return f
    }()

    /// Allowed in the canonical URI per AWS rules: unreserved + "/".
    private static let uriAllowed: CharacterSet = {
        var s = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~/")
        return s
    }()

    /// Allowed in canonical query strings: unreserved only (no "/").
    private static let queryAllowed: CharacterSet = {
        CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
    }()

    /// URI-encode an object key for placing in the request path. "/" is
    /// kept as-is so virtual-folder structure survives.
    private func uriEncodeKey(_ key: String) -> String {
        let segments = key.components(separatedBy: "/")
        let encoded = segments.map {
            $0.addingPercentEncoding(withAllowedCharacters: Self.queryAllowed) ?? $0
        }
        return encoded.joined(separator: "/")
    }

    private static func maskedAccessKey(_ key: String) -> String {
        let suffix = key.suffix(4)
        return "***\(suffix)"
    }

    private func parseHTTPDate(_ s: String) -> Date? {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "GMT")
        f.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        return f.date(from: s)
    }
}
