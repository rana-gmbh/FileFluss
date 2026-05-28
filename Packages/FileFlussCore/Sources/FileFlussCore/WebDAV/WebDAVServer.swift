import Foundation
import Network
import os

private let webdavLog = Logger(subsystem: "com.rana.FileFluss", category: "webdav")

/// A loopback WebDAV server that exposes any `CloudProvider` (rooted at a
/// chosen folder) over HTTP so macOS's built-in `mount_webdav` can mount it
/// as a Finder drive. Every request is translated 1:1 to the provider's API.
///
/// Scope of this first pass is "what `mount_webdav` actually sends":
///   • OPTIONS, PROPFIND (Depth 0/1), GET, HEAD, PUT, MKCOL, DELETE, MOVE.
///   • LOCK/UNLOCK return success stubs — Finder asks before some writes but
///     doesn't need a real lock manager for a single-client mount.
///   • COPY isn't implemented (mount_webdav copies via download+upload).
///
/// The server binds to 127.0.0.1 only and always responds `Connection: close`
/// so we don't have to track keep-alive state. Finder reconnects as needed.
public actor WebDAVServer {
    private let provider: any CloudProvider
    /// Path inside the provider that maps to the WebDAV root `/`. So a
    /// request for `/foo/bar.txt` resolves to `<mountRoot>/foo/bar.txt`.
    private let mountRoot: String
    private var listener: NWListener?
    public private(set) var port: UInt16 = 0

    public init(provider: any CloudProvider, mountRoot: String = "/") {
        self.provider = provider
        // Normalise: always start with "/", never end with "/" (except root).
        var r = mountRoot
        if !r.hasPrefix("/") { r = "/" + r }
        while r.count > 1 && r.hasSuffix("/") { r.removeLast() }
        self.mountRoot = r
    }

    // MARK: - Lifecycle

    /// Starts the listener on a loopback port. Pass 0 (the default) to let
    /// the OS pick a free port; the bound port is returned.
    @discardableResult
    public func start(preferredPort: UInt16 = 0) async throws -> UInt16 {
        guard listener == nil else { return port }
        let nwPort = preferredPort == 0 ? NWEndpoint.Port.any : NWEndpoint.Port(rawValue: preferredPort)!
        let params = NWParameters.tcp
        params.requiredLocalEndpoint = NWEndpoint.hostPort(host: "127.0.0.1", port: nwPort)
        let listener = try NWListener(using: params)
        self.listener = listener

        // Drop connections that aren't from loopback. NWListener already
        // restricts to the bound host, but be explicit.
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { connection.cancel(); return }
            Task { await self.accept(connection) }
        }

        let ready = AsyncThrowingStream<UInt16, Error> { continuation in
            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    if let p = listener.port?.rawValue {
                        continuation.yield(p)
                        continuation.finish()
                    }
                case .failed(let error):
                    continuation.finish(throwing: error)
                case .cancelled:
                    continuation.finish()
                default:
                    break
                }
            }
        }
        listener.start(queue: .global(qos: .userInitiated))
        for try await p in ready {
            self.port = p
            webdavLog.info("[WebDAV] listening on 127.0.0.1:\(p, privacy: .public)")
            return p
        }
        throw CloudProviderError.networkError(URLError(.cannotConnectToHost))
    }

    public func stop() {
        listener?.cancel()
        listener = nil
        port = 0
    }

    // MARK: - Connection handling

    private func accept(_ connection: NWConnection) async {
        connection.start(queue: .global(qos: .userInitiated))
        do {
            let request = try await Self.readRequest(from: connection)
            let response = await dispatch(request)
            try await Self.write(response, to: connection)
        } catch {
            // Best-effort close on any failure — Finder retries.
            webdavLog.debug("[WebDAV] connection error: \(error.localizedDescription, privacy: .public)")
        }
        // Always close after one request — see file header comment.
        connection.cancel()
    }

    // MARK: - Method dispatch

    private func dispatch(_ request: WebDAVRequest) async -> WebDAVResponse {
        webdavLog.debug("[WebDAV] \(request.method, privacy: .public) \(request.path, privacy: .public)")
        switch request.method.uppercased() {
        case "OPTIONS": return handleOPTIONS()
        case "PROPFIND": return await handlePROPFIND(request)
        case "GET", "HEAD": return await handleGET(request, includeBody: request.method == "GET")
        case "PUT": return await handlePUT(request)
        case "MKCOL": return await handleMKCOL(request)
        case "DELETE": return await handleDELETE(request)
        case "MOVE": return await handleMOVE(request)
        case "LOCK": return handleLOCK(request)
        case "UNLOCK": return WebDAVResponse(status: 204, statusText: "No Content")
        default:
            return WebDAVResponse(status: 501, statusText: "Not Implemented")
        }
    }

    // MARK: - Handlers

    private func handleOPTIONS() -> WebDAVResponse {
        // `DAV: 1` is the minimum the Finder client requires to treat the
        // mount as a real WebDAV volume. `2` would advertise LOCK; we stub
        // it but don't claim full lock semantics.
        WebDAVResponse(
            status: 200,
            statusText: "OK",
            headers: [
                "DAV": "1, 2",
                "MS-Author-Via": "DAV",
                "Allow": "OPTIONS, PROPFIND, GET, HEAD, PUT, MKCOL, DELETE, MOVE, LOCK, UNLOCK",
            ]
        )
    }

    private func handlePROPFIND(_ request: WebDAVRequest) async -> WebDAVResponse {
        let depth = (request.headers["depth"] ?? "1").trimmingCharacters(in: .whitespaces)
        let providerPath = self.providerPath(for: request.path)

        var entries: [PropEntry] = []

        // The resource itself.
        if request.path == "/" || providerPath == mountRoot {
            entries.append(PropEntry(href: encodeHref(request.path.hasSuffix("/") ? request.path : request.path + "/"),
                                     isCollection: true, size: 0, mtime: Date(), name: "/"))
        } else {
            do {
                let meta = try await provider.getFileMetadata(at: providerPath)
                entries.append(PropEntry(href: encodeHref(meta.isDirectory ? withTrailingSlash(request.path) : request.path),
                                         isCollection: meta.isDirectory, size: meta.size,
                                         mtime: meta.modificationDate, name: meta.name))
            } catch {
                return WebDAVResponse(status: 404, statusText: "Not Found")
            }
        }

        // Children, if Depth: 1 (or unspecified → treat as 1, matches macOS expectation).
        if depth != "0", entries.first?.isCollection == true {
            do {
                let children = try await provider.listDirectory(at: providerPath)
                for child in children {
                    // Skip the Finder noise we filter on PUT anyway.
                    if shouldIgnoreSidecar(child.name) { continue }
                    let childRequestPath = appendingChild(name: child.name, to: request.path)
                    let href = encodeHref(child.isDirectory ? withTrailingSlash(childRequestPath) : childRequestPath)
                    entries.append(PropEntry(href: href, isCollection: child.isDirectory,
                                             size: child.size, mtime: child.modificationDate, name: child.name))
                }
            } catch {
                // Surface as 207 anyway — return just the self entry. Some
                // servers do this and Finder copes.
                webdavLog.error("[WebDAV] list failed for \(providerPath, privacy: .public): \(error.localizedDescription, privacy: .public)")
            }
        }

        let xml = renderMultiStatus(entries: entries)
        return WebDAVResponse(status: 207, statusText: "Multi-Status",
                              headers: ["Content-Type": "application/xml; charset=utf-8"],
                              body: .data(Data(xml.utf8)))
    }

    private func handleGET(_ request: WebDAVRequest, includeBody: Bool) async -> WebDAVResponse {
        let providerPath = self.providerPath(for: request.path)
        do {
            let meta = try await provider.getFileMetadata(at: providerPath)
            if meta.isDirectory {
                // GET on a collection isn't meaningful; Finder won't send it.
                return WebDAVResponse(status: 405, statusText: "Method Not Allowed")
            }
            var headers = [
                "Content-Length": "\(meta.size)",
                "Content-Type": contentType(forName: meta.name),
                "Last-Modified": Self.httpDate(meta.modificationDate),
            ]
            if !includeBody {
                // HEAD: report metadata only.
                return WebDAVResponse(status: 200, statusText: "OK", headers: headers)
            }
            let tempURL = Self.makeTempFile(suffix: (meta.name as NSString).pathExtension)
            try await provider.downloadFile(remotePath: providerPath, to: tempURL)
            // Re-stat in case the provider's reported size was off.
            if let actualSize = (try? FileManager.default.attributesOfItem(atPath: tempURL.path))?[.size] as? Int64 {
                headers["Content-Length"] = "\(actualSize)"
            }
            return WebDAVResponse(status: 200, statusText: "OK", headers: headers, body: .file(tempURL))
        } catch {
            return Self.mapError(error)
        }
    }

    private func handlePUT(_ request: WebDAVRequest) async -> WebDAVResponse {
        let providerPath = self.providerPath(for: request.path)
        let name = (providerPath as NSString).lastPathComponent

        if shouldIgnoreSidecar(name) {
            // Pretend we stored it so Finder is happy; we don't push hidden
            // sidecar files (.DS_Store, ._*, .Spotlight-V100, …) to the cloud.
            return WebDAVResponse(status: 201, statusText: "Created")
        }

        guard let bodyURL = request.bodyURL else {
            return WebDAVResponse(status: 411, statusText: "Length Required")
        }
        do {
            try await provider.uploadFile(from: bodyURL, to: providerPath)
            return WebDAVResponse(status: 201, statusText: "Created")
        } catch {
            return Self.mapError(error)
        }
    }

    private func handleMKCOL(_ request: WebDAVRequest) async -> WebDAVResponse {
        let providerPath = self.providerPath(for: request.path)
        do {
            try await provider.createDirectory(at: providerPath)
            return WebDAVResponse(status: 201, statusText: "Created")
        } catch {
            return Self.mapError(error)
        }
    }

    private func handleDELETE(_ request: WebDAVRequest) async -> WebDAVResponse {
        let providerPath = self.providerPath(for: request.path)
        do {
            try await provider.deleteItem(at: providerPath)
            return WebDAVResponse(status: 204, statusText: "No Content")
        } catch {
            return Self.mapError(error)
        }
    }

    private func handleMOVE(_ request: WebDAVRequest) async -> WebDAVResponse {
        guard let destinationRaw = request.headers["destination"] else {
            return WebDAVResponse(status: 400, statusText: "Bad Request")
        }
        let destinationPath = decodeDestination(destinationRaw)
        let from = providerPath(for: request.path)
        let to = providerPath(for: destinationPath)

        let fromParent = (from as NSString).deletingLastPathComponent
        let toParent = (to as NSString).deletingLastPathComponent
        let toName = (to as NSString).lastPathComponent

        do {
            if fromParent == toParent {
                // Pure rename within the same directory.
                try await provider.renameItem(at: from, to: toName)
            } else {
                // Cross-directory move (also handles rename + move atomically).
                try await provider.moveItem(at: from, toPath: to)
            }
            return WebDAVResponse(status: 201, statusText: "Created")
        } catch CloudProviderError.notImplemented {
            // Provider doesn't support server-side move/rename; signal that
            // to the client so Finder can fall back to copy+delete.
            return WebDAVResponse(status: 502, statusText: "Bad Gateway")
        } catch {
            return Self.mapError(error)
        }
    }

    private func handleLOCK(_ request: WebDAVRequest) -> WebDAVResponse {
        // Return a stub LOCK that gives Finder the token it expects but
        // does no real synchronisation — fine for a single-client mount.
        let token = "urn:uuid:\(UUID().uuidString.lowercased())"
        let xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <D:prop xmlns:D="DAV:">
          <D:lockdiscovery>
            <D:activelock>
              <D:locktype><D:write/></D:locktype>
              <D:lockscope><D:exclusive/></D:lockscope>
              <D:depth>infinity</D:depth>
              <D:owner><D:href>filefluss</D:href></D:owner>
              <D:timeout>Second-3600</D:timeout>
              <D:locktoken><D:href>\(token)</D:href></D:locktoken>
            </D:activelock>
          </D:lockdiscovery>
        </D:prop>
        """
        return WebDAVResponse(
            status: 200, statusText: "OK",
            headers: [
                "Lock-Token": "<\(token)>",
                "Content-Type": "application/xml; charset=utf-8",
            ],
            body: .data(Data(xml.utf8))
        )
    }

    // MARK: - Path helpers

    /// Maps a WebDAV request path to a provider path rooted at `mountRoot`.
    /// Both paths are POSIX-style with `/` separators; the request path is
    /// already percent-decoded by the parser.
    private func providerPath(for requestPath: String) -> String {
        var trimmed = requestPath
        while trimmed.count > 1 && trimmed.hasSuffix("/") { trimmed.removeLast() }
        if !trimmed.hasPrefix("/") { trimmed = "/" + trimmed }
        if mountRoot == "/" { return trimmed }
        if trimmed == "/" { return mountRoot }
        return mountRoot + trimmed
    }

    // MARK: - Sidecar filter

    /// macOS Finder writes a handful of hidden metadata files (Desktop
    /// services store, AppleDouble extended-attribute forks, Spotlight
    /// index hints) that have no business being uploaded to a cloud. We
    /// pretend the writes succeeded without forwarding them.
    private func shouldIgnoreSidecar(_ name: String) -> Bool {
        if name == ".DS_Store" { return true }
        if name.hasPrefix("._") { return true }
        if name == ".Spotlight-V100" || name == ".Trashes" || name == ".fseventsd" { return true }
        return false
    }

    // MARK: - HTTP read/write

    private static func readRequest(from connection: NWConnection) async throws -> WebDAVRequest {
        var buffer = Data()
        // Read until headers are complete.
        while buffer.range(of: Data("\r\n\r\n".utf8)) == nil {
            let chunk = try await receive(connection, max: 64 * 1024)
            if chunk.isEmpty { break }
            buffer.append(chunk)
            if buffer.count > 64 * 1024 { throw URLError(.dataLengthExceedsMaximum) }
        }
        guard let headerEnd = buffer.range(of: Data("\r\n\r\n".utf8)) else {
            throw URLError(.zeroByteResource)
        }
        let headerBytes = buffer.subdata(in: 0..<headerEnd.lowerBound)
        guard let headerText = String(data: headerBytes, encoding: .utf8) else {
            throw URLError(.cannotDecodeRawData)
        }
        let lines = headerText.components(separatedBy: "\r\n")
        guard let requestLine = lines.first else { throw URLError(.badServerResponse) }
        let parts = requestLine.components(separatedBy: " ")
        guard parts.count >= 3 else { throw URLError(.badServerResponse) }
        let method = parts[0]
        let rawPath = parts[1]
        // Headers are case-insensitive; normalise keys to lowercase.
        var headers: [String: String] = [:]
        for line in lines.dropFirst() where line.contains(":") {
            if let colon = line.firstIndex(of: ":") {
                let key = String(line[..<colon]).trimmingCharacters(in: .whitespaces).lowercased()
                let value = String(line[line.index(after: colon)...]).trimmingCharacters(in: .whitespaces)
                headers[key] = value
            }
        }

        let path = rawPath.removingPercentEncoding ?? rawPath

        // Body: stream the rest into a temp file up to Content-Length.
        var bodyURL: URL?
        if let lengthString = headers["content-length"], let length = Int64(lengthString), length > 0 {
            let alreadyHave = buffer.count - headerEnd.upperBound
            let url = Self.makeTempFile(suffix: "")
            FileManager.default.createFile(atPath: url.path, contents: nil)
            let handle = try FileHandle(forWritingTo: url)
            defer { try? handle.close() }
            if alreadyHave > 0 {
                try handle.write(contentsOf: buffer.subdata(in: headerEnd.upperBound..<buffer.count))
            }
            var remaining = length - Int64(max(alreadyHave, 0))
            while remaining > 0 {
                let chunk = try await receive(connection, max: Int(min(remaining, 1024 * 1024)))
                if chunk.isEmpty { break }
                try handle.write(contentsOf: chunk)
                remaining -= Int64(chunk.count)
            }
            bodyURL = url
        }

        return WebDAVRequest(method: method, path: path, headers: headers, bodyURL: bodyURL)
    }

    private static func write(_ response: WebDAVResponse, to connection: NWConnection) async throws {
        var head = "HTTP/1.1 \(response.status) \(response.statusText)\r\n"
        head += "Connection: close\r\n"
        head += "Date: \(httpDate(Date()))\r\n"
        // Compute Content-Length for the framed body up front so clients
        // never have to guess.
        let bodyLength: Int64
        switch response.body {
        case .empty: bodyLength = 0
        case .data(let d): bodyLength = Int64(d.count)
        case .file(let url): bodyLength = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int64) ?? 0
        }
        var headers = response.headers
        if headers["Content-Length"] == nil { headers["Content-Length"] = "\(bodyLength)" }
        for (k, v) in headers { head += "\(k): \(v)\r\n" }
        head += "\r\n"
        try await send(connection, Data(head.utf8))

        switch response.body {
        case .empty: break
        case .data(let d): try await send(connection, d)
        case .file(let url):
            let handle = try FileHandle(forReadingFrom: url)
            defer {
                try? handle.close()
                try? FileManager.default.removeItem(at: url)
            }
            while true {
                let chunk = try handle.read(upToCount: 256 * 1024) ?? Data()
                if chunk.isEmpty { break }
                try await send(connection, chunk)
            }
        }
    }

    // MARK: - NWConnection bridging

    private static func receive(_ connection: NWConnection, max: Int) async throws -> Data {
        try await withCheckedThrowingContinuation { continuation in
            connection.receive(minimumIncompleteLength: 1, maximumLength: max) { data, _, _, error in
                if let error { continuation.resume(throwing: error); return }
                continuation.resume(returning: data ?? Data())
            }
        }
    }

    private static func send(_ connection: NWConnection, _ data: Data) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            connection.send(content: data, completion: .contentProcessed { error in
                if let error { continuation.resume(throwing: error) }
                else { continuation.resume() }
            })
        }
    }

    // MARK: - Encoding helpers

    private func appendingChild(name: String, to base: String) -> String {
        let trimmedBase = base.hasSuffix("/") && base != "/" ? String(base.dropLast()) : base
        return trimmedBase == "/" ? "/\(name)" : "\(trimmedBase)/\(name)"
    }

    private func withTrailingSlash(_ path: String) -> String {
        path.hasSuffix("/") ? path : path + "/"
    }

    /// Percent-encodes the path components for use in a `<D:href>` element.
    /// We keep `/` literal so the path structure stays readable.
    private func encodeHref(_ path: String) -> String {
        var allowed = CharacterSet.urlPathAllowed
        allowed.remove(charactersIn: "?#")
        return path.addingPercentEncoding(withAllowedCharacters: allowed) ?? path
    }

    /// The `Destination` header is either an absolute URL or a path. We only
    /// care about the path portion; trim everything before it.
    private func decodeDestination(_ raw: String) -> String {
        let pathPart: String
        if let url = URL(string: raw), url.host != nil { pathPart = url.path }
        else { pathPart = raw }
        return pathPart.removingPercentEncoding ?? pathPart
    }

    private static func httpDate(_ date: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "GMT")
        f.dateFormat = "EEE, dd MMM yyyy HH:mm:ss 'GMT'"
        return f.string(from: date)
    }

    private static func makeTempFile(suffix: String) -> URL {
        let ext = suffix.isEmpty ? "" : ".\(suffix)"
        return URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("filefluss-webdav-\(UUID().uuidString)\(ext)")
    }

    // MARK: - Multi-Status XML

    private func renderMultiStatus(entries: [PropEntry]) -> String {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<D:multistatus xmlns:D=\"DAV:\">\n"
        for entry in entries {
            xml += "  <D:response>\n"
            xml += "    <D:href>\(entry.href)</D:href>\n"
            xml += "    <D:propstat>\n"
            xml += "      <D:prop>\n"
            xml += "        <D:displayname>\(xmlEscape(entry.name))</D:displayname>\n"
            xml += "        <D:getlastmodified>\(Self.httpDate(entry.mtime))</D:getlastmodified>\n"
            if entry.isCollection {
                xml += "        <D:resourcetype><D:collection/></D:resourcetype>\n"
            } else {
                xml += "        <D:resourcetype/>\n"
                xml += "        <D:getcontentlength>\(entry.size)</D:getcontentlength>\n"
                xml += "        <D:getcontenttype>\(contentType(forName: entry.name))</D:getcontenttype>\n"
            }
            xml += "      </D:prop>\n"
            xml += "      <D:status>HTTP/1.1 200 OK</D:status>\n"
            xml += "    </D:propstat>\n"
            xml += "  </D:response>\n"
        }
        xml += "</D:multistatus>\n"
        return xml
    }

    private func xmlEscape(_ s: String) -> String {
        s.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
            .replacingOccurrences(of: "'", with: "&apos;")
    }

    private func contentType(forName name: String) -> String {
        let ext = (name as NSString).pathExtension.lowercased()
        switch ext {
        case "txt", "md": return "text/plain; charset=utf-8"
        case "html", "htm": return "text/html; charset=utf-8"
        case "json": return "application/json"
        case "xml": return "application/xml"
        case "pdf": return "application/pdf"
        case "jpg", "jpeg": return "image/jpeg"
        case "png": return "image/png"
        case "gif": return "image/gif"
        case "mp4", "m4v", "mov": return "video/mp4"
        case "mp3", "m4a": return "audio/mpeg"
        default: return "application/octet-stream"
        }
    }

    // MARK: - Error mapping

    private static func mapError(_ error: Error) -> WebDAVResponse {
        if let providerError = error as? CloudProviderError {
            switch providerError {
            case .notFound: return WebDAVResponse(status: 404, statusText: "Not Found")
            case .unauthorized, .notAuthenticated, .invalidCredentials:
                return WebDAVResponse(status: 401, statusText: "Unauthorized")
            case .quotaExceeded: return WebDAVResponse(status: 507, statusText: "Insufficient Storage")
            case .rateLimited: return WebDAVResponse(status: 429, statusText: "Too Many Requests")
            case .notImplemented: return WebDAVResponse(status: 501, statusText: "Not Implemented")
            default: return WebDAVResponse(status: 502, statusText: "Bad Gateway")
            }
        }
        return WebDAVResponse(status: 500, statusText: "Internal Server Error")
    }
}

// MARK: - Supporting types

private struct WebDAVRequest {
    let method: String
    let path: String
    let headers: [String: String]
    let bodyURL: URL?
}

private struct WebDAVResponse {
    var status: Int
    var statusText: String
    var headers: [String: String] = [:]
    var body: Body = .empty
    enum Body {
        case empty
        case data(Data)
        /// File on disk that should be streamed back to the client and
        /// deleted afterwards.
        case file(URL)
    }
}

private struct PropEntry {
    let href: String
    let isCollection: Bool
    let size: Int64
    let mtime: Date
    let name: String
}
