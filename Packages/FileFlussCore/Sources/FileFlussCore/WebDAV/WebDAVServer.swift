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
    /// Optional store that absorbs writes to Finder sidecar files
    /// (`.DS_Store`, `._*`, custom icons) and serves them back. When nil
    /// those paths are silently no-op'd, like a `/dev/null`.
    private let sidecar: WebDAVSidecarStore?
    private var listener: NWListener?
    public private(set) var port: UInt16 = 0

    public init(provider: any CloudProvider, mountRoot: String = "/", sidecar: WebDAVSidecarStore? = nil) {
        self.provider = provider
        self.sidecar = sidecar
        // Normalise: always start with "/", never end with "/" (except root).
        var r = mountRoot
        if !r.hasPrefix("/") { r = "/" + r }
        while r.count > 1 && r.hasSuffix("/") { r.removeLast() }
        self.mountRoot = r
    }

    // MARK: - Lifecycle

    /// Starts the listener. Pass 0 (the default) to let the OS pick a free
    /// port; the bound port is returned.
    ///
    /// Binds explicitly to IPv4 `127.0.0.1`. `NWListener(using:on:)` defaults
    /// to the IPv6 wildcard `::`, and macOS sockets ship with
    /// `IPV6_V6ONLY=1` enabled, so IPv4 connections (which is what
    /// `webdavfs_agent` makes to localhost) get refused — observed in the
    /// NetAuthSysAgent log as "Mount failed 2". Forcing IPv4 via
    /// `requiredLocalEndpoint` sidesteps the dual-stack problem entirely.
    @discardableResult
    public func start(preferredPort: UInt16 = 0) async throws -> UInt16 {
        guard listener == nil else { return port }
        let nwPort: NWEndpoint.Port = preferredPort == 0 ? .any : NWEndpoint.Port(rawValue: preferredPort)!
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        params.requiredLocalEndpoint = NWEndpoint.hostPort(
            host: .ipv4(IPv4Address("127.0.0.1")!),
            port: nwPort
        )
        let listener = try NWListener(using: params)
        self.listener = listener

        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { connection.cancel(); return }
            // Defence in depth: reject anything that didn't come in over
            // loopback even though no remote process should reach us.
            guard Self.isLoopback(connection.endpoint) else {
                connection.cancel()
                return
            }
            Task { await self.accept(connection) }
        }

        // Single-shot continuation: resume on .ready (with the bound port)
        // or on .failed (with the error). Guarded against double-resume in
        // case state transitions ping-pong.
        let resumed = ContinuationGuard()
        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<UInt16, Error>) in
            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    if let p = listener.port?.rawValue, resumed.claim() {
                        webdavLog.info("[WebDAV] listening on 127.0.0.1:\(p, privacy: .public)")
                        continuation.resume(returning: p)
                    }
                case .failed(let error):
                    if resumed.claim() {
                        webdavLog.error("[WebDAV] listener failed: \(error.localizedDescription, privacy: .public)")
                        continuation.resume(throwing: error)
                    }
                default:
                    break
                }
            }
            listener.start(queue: .global(qos: .userInitiated))
        }
    }

    /// True when an inbound `NWEndpoint` is on the loopback interface.
    /// `NWEndpoint.Host.ipv4(...)`'s `rawValue` is the 4-byte address in
    /// network order, so the first byte being `127` covers the entire
    /// 127.0.0.0/8 loopback block.
    private static func isLoopback(_ endpoint: NWEndpoint) -> Bool {
        guard case .hostPort(let host, _) = endpoint else { return false }
        switch host {
        case .ipv4(let addr):
            return addr.rawValue.first == 127
        case .ipv6(let addr):
            return addr == .loopback
        case .name(let name, _):
            return name == "localhost"
        @unknown default:
            return false
        }
    }

    /// Single-fire continuation latch used by `start()`.
    private final class ContinuationGuard: @unchecked Sendable {
        private let lock = NSLock()
        private var claimed = false
        func claim() -> Bool {
            lock.lock(); defer { lock.unlock() }
            if claimed { return false }
            claimed = true
            return true
        }
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
            webdavLog.info("[WebDAV] → \(response.status, privacy: .public) \(request.method, privacy: .public) \(request.path, privacy: .public)")
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
        webdavLog.info("[WebDAV] \(request.method, privacy: .public) \(request.path, privacy: .public) →")
        switch request.method.uppercased() {
        case "OPTIONS": return handleOPTIONS()
        case "PROPFIND": return await handlePROPFIND(request)
        case "PROPPATCH": return handlePROPPATCH(request)
        case "GET", "HEAD": return await handleGET(request, includeBody: request.method == "GET")
        case "PUT": return await handlePUT(request)
        case "MKCOL": return await handleMKCOL(request)
        case "DELETE": return await handleDELETE(request)
        case "MOVE": return await handleMOVE(request)
        case "COPY": return await handleCOPY(request)
        case "LOCK": return handleLOCK(request)
        case "UNLOCK": return WebDAVResponse(status: 204, statusText: "No Content")
        default:
            return WebDAVResponse(status: 501, statusText: "Not Implemented")
        }
    }

    // PROPPATCH stub: Finder uses this to write extended attributes (custom
    // icons, comments, Finder flags). We don't persist them, but returning
    // 501 makes Finder treat the whole drag-and-drop as a failure with
    // error -43. Returning a 207 multistatus that pretends every property
    // succeeded keeps Finder moving.
    private func handlePROPPATCH(_ request: WebDAVRequest) -> WebDAVResponse {
        let href = encodeHref(request.path)
        let xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <D:multistatus xmlns:D="DAV:">
          <D:response>
            <D:href>\(href)</D:href>
            <D:propstat>
              <D:prop/>
              <D:status>HTTP/1.1 200 OK</D:status>
            </D:propstat>
          </D:response>
        </D:multistatus>
        """
        return WebDAVResponse(status: 207, statusText: "Multi-Status",
                              headers: ["Content-Type": "application/xml; charset=utf-8"],
                              body: .data(Data(xml.utf8)))
    }

    // COPY: Finder uses COPY (not MOVE) when moving between two distinct
    // volumes, or sometimes as a "safer move" probe. Implemented as a
    // best-effort wrapper around the provider's `copyItem`; the default
    // CloudProvider conformance throws `notImplemented`, which we surface
    // as 502 so Finder falls back to a per-file copy via PUT.
    private func handleCOPY(_ request: WebDAVRequest) async -> WebDAVResponse {
        guard let destinationRaw = request.headers["destination"] else {
            return WebDAVResponse(status: 400, statusText: "Bad Request")
        }
        let from = providerPath(for: request.path)
        let to = providerPath(for: decodeDestination(destinationRaw))
        do {
            try await provider.copyItem(at: from, toPath: to)
            return WebDAVResponse(status: 201, statusText: "Created")
        } catch CloudProviderError.notImplemented {
            return WebDAVResponse(status: 502, statusText: "Bad Gateway")
        } catch {
            return Self.mapError(error)
        }
    }

    // MARK: - Handlers

    private func handleOPTIONS() -> WebDAVResponse {
        // Apple's webdavfs_agent inspects the OPTIONS response to decide
        // whether the server is a WebDAV server worth mounting. With a bare
        // DAV header it concludes "not usable" and refuses without bothering
        // with PROPFIND — fix is to advertise the same set of headers
        // Apache mod_dav returns: a Server identifier, byte-range support,
        // and the full DAV class list webdavfs probes for.
        WebDAVResponse(
            status: 200,
            statusText: "OK",
            headers: [
                "DAV": "1, 2, 3",
                "MS-Author-Via": "DAV",
                "Allow": "OPTIONS, GET, HEAD, POST, DELETE, TRACE, PROPFIND, PROPPATCH, COPY, MOVE, LOCK, UNLOCK, PUT, MKCOL",
                "Accept-Ranges": "bytes",
                "Server": "FileFluss-WebDAV/1.0",
                "Cache-Control": "no-cache",
            ]
        )
    }

    private func handlePROPFIND(_ request: WebDAVRequest) async -> WebDAVResponse {
        let depth = (request.headers["depth"] ?? "1").trimmingCharacters(in: .whitespaces)
        let providerPath = self.providerPath(for: request.path)
        let name = (providerPath as NSString).lastPathComponent

        var entries: [PropEntry] = []

        // Fast-path: AppleDouble / `.DS_Store` / Finder-only sidecar paths
        // are served entirely from the local sidecar store (or 404 if
        // absent). Without this every drag fires hundreds of PROPFINDs
        // that hit the cloud provider's API and stall Finder long enough
        // to give up with error -43.
        if shouldIgnoreSidecar(name) {
            if let sidecar, let meta = await sidecar.metadata(at: providerPath) {
                entries.append(PropEntry(href: encodeHref(request.path),
                                         isCollection: false, size: meta.size,
                                         mtime: meta.mtime, name: name))
                let xml = renderMultiStatus(entries: entries)
                return WebDAVResponse(status: 207, statusText: "Multi-Status",
                                      headers: ["Content-Type": "application/xml; charset=utf-8"],
                                      body: .data(Data(xml.utf8)))
            }
            return WebDAVResponse(status: 404, statusText: "Not Found")
        }

        // The resource itself.
        if request.path == "/" || providerPath == mountRoot {
            // Use a sensible displayname for the root — webdavfs_agent (the
            // helper NetFS forks for HTTP URLs) rejects responses where the
            // root's displayname is "/" with "share does not exist".
            entries.append(PropEntry(href: encodeHref(request.path.hasSuffix("/") ? request.path : request.path + "/"),
                                     isCollection: true, size: 0, mtime: Date(), name: "FileFluss"))
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
        let name = (providerPath as NSString).lastPathComponent

        // Sidecar route — serve Finder-only files (.DS_Store etc.) from the
        // local store so view state and AppleDouble forks survive folder
        // revisits without us ever asking the cloud about them.
        if shouldIgnoreSidecar(name), let sidecar {
            guard let data = await sidecar.get(path: providerPath) else {
                return WebDAVResponse(status: 404, statusText: "Not Found")
            }
            let meta = await sidecar.metadata(at: providerPath)
            var headers = [
                "Content-Length": "\(data.count)",
                "Content-Type": contentType(forName: name),
                "Last-Modified": Self.httpDate(meta?.mtime ?? Date()),
            ]
            if !includeBody {
                return WebDAVResponse(status: 200, statusText: "OK", headers: headers)
            }
            _ = headers // keep mutable for symmetry with the provider branch
            return WebDAVResponse(status: 200, statusText: "OK", headers: headers, body: .data(data))
        }

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
            // Route Finder sidecar writes to the local store (if configured)
            // so they survive a folder revisit. With no store configured the
            // write is silently discarded so the cloud stays clean either way.
            if let sidecar, let bodyURL = request.bodyURL,
               let data = try? Data(contentsOf: bodyURL) {
                try? await sidecar.put(path: providerPath, contents: data)
            }
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
        let name = (providerPath as NSString).lastPathComponent
        if shouldIgnoreSidecar(name) {
            if let sidecar { await sidecar.delete(path: providerPath) }
            return WebDAVResponse(status: 204, statusText: "No Content")
        }
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
            // Provider can't move server-side. Returning 502 here makes
            // Finder give up with error -43 instead of doing its own
            // copy+delete (it apparently expects the server to do it).
            // Emulate copy+delete ourselves so Finder's MOVE just works.
            return await moveByCopyDelete(from: from, to: to, toName: toName)
        } catch {
            return Self.mapError(error)
        }
    }

    /// Fallback for cross-directory MOVE on providers that don't implement
    /// server-side move: download the source to a temp file, upload it at
    /// the destination, then delete the source. Files only — recursive
    /// folder copy is out of scope for the first pass.
    private func moveByCopyDelete(from: String, to: String, toName: String) async -> WebDAVResponse {
        do {
            let meta = try await provider.getFileMetadata(at: from)
            if meta.isDirectory {
                // Without a recursive copy step we can't honour a folder
                // move. Surface it as a real error.
                return WebDAVResponse(status: 501, statusText: "Not Implemented")
            }
            let tempURL = Self.makeTempFile(suffix: (toName as NSString).pathExtension)
            defer { try? FileManager.default.removeItem(at: tempURL) }
            try await provider.downloadFile(remotePath: from, to: tempURL)
            try await provider.uploadFile(from: tempURL, to: to)
            try await provider.deleteItem(at: from)
            return WebDAVResponse(status: 201, statusText: "Created")
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
        // Identify ourselves on every response — some WebDAV clients
        // (Apple's webdavfs_agent included) fingerprint the Server header
        // and behave differently if it looks like a real WebDAV server.
        head += "Server: FileFluss-WebDAV/1.0\r\n"
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
            // Synthesise a stable etag per (path, mtime, size) — webdavfs
            // uses this to detect remote changes between visits.
            let etag = "\"\(String(format: "%x-%llx-%llx", entry.href.hashValue & 0xFFFFFFFF, Int64(entry.mtime.timeIntervalSince1970), entry.size))\""
            xml += "  <D:response>\n"
            xml += "    <D:href>\(entry.href)</D:href>\n"
            xml += "    <D:propstat>\n"
            xml += "      <D:prop>\n"
            xml += "        <D:displayname>\(xmlEscape(entry.name))</D:displayname>\n"
            xml += "        <D:creationdate>\(Self.isoDate(entry.mtime))</D:creationdate>\n"
            xml += "        <D:getlastmodified>\(Self.httpDate(entry.mtime))</D:getlastmodified>\n"
            xml += "        <D:getetag>\(etag)</D:getetag>\n"
            if entry.isCollection {
                xml += "        <D:resourcetype><D:collection/></D:resourcetype>\n"
            } else {
                xml += "        <D:resourcetype/>\n"
                xml += "        <D:getcontentlength>\(entry.size)</D:getcontentlength>\n"
                xml += "        <D:getcontenttype>\(contentType(forName: entry.name))</D:getcontenttype>\n"
            }
            // Advertising lock support keeps webdavfs_agent happy — without
            // these blocks it concludes the share is read-only/unsupported.
            xml += "        <D:supportedlock>\n"
            xml += "          <D:lockentry><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockentry>\n"
            xml += "          <D:lockentry><D:lockscope><D:shared/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockentry>\n"
            xml += "        </D:supportedlock>\n"
            xml += "        <D:lockdiscovery/>\n"
            xml += "      </D:prop>\n"
            xml += "      <D:status>HTTP/1.1 200 OK</D:status>\n"
            xml += "    </D:propstat>\n"
            xml += "  </D:response>\n"
        }
        xml += "</D:multistatus>\n"
        return xml
    }

    /// RFC 3339 / ISO 8601 timestamp in UTC, the format
    /// `<D:creationdate>` expects.
    private static func isoDate(_ date: Date) -> String {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        f.timeZone = TimeZone(identifier: "UTC")
        return f.string(from: date)
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
