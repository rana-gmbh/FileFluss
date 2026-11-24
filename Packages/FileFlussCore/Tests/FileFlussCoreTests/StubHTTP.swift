import Foundation

/// A canned HTTP server for provider tests. Answers are looked up by the
/// request's host, so suites that use a host of their own can run in
/// parallel; a suite whose provider has a fixed host (Internxt) runs
/// `.serialized`.
final class StubHTTP: URLProtocol, @unchecked Sendable {
    struct Reply: Sendable {
        var status: Int = 200
        var headers: [String: String] = ["Content-Type": "application/json"]
        var body = Data()

        static func json(_ text: String, status: Int = 200) -> Reply {
            Reply(status: status, body: Data(text.utf8))
        }
    }

    /// A request as the server saw it, body included.
    struct Recorded: Sendable {
        let method: String
        let url: URL
        let body: Data

        /// The path plus the raw (still percent-encoded) query.
        var target: String {
            url.path(percentEncoded: true) + (url.query(percentEncoded: true).map { "?" + $0 } ?? "")
        }

        /// A query value, decoded the way a strict form decoder would: a
        /// raw `+` stays a `+` here, so a test can tell `%2B` from `+`.
        func query(_ name: String) -> String? {
            guard let raw = url.query(percentEncoded: true) else { return nil }
            for pair in raw.split(separator: "&") {
                let parts = pair.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
                guard parts.count == 2, parts[0] == name else { continue }
                return String(parts[1]).removingPercentEncoding
            }
            return nil
        }

        var bodyText: String { String(data: body, encoding: .utf8) ?? "" }
    }

    typealias Handler = @Sendable (Recorded) -> Reply

    private static let lock = NSLock()
    nonisolated(unsafe) private static var handlers: [String: Handler] = [:]
    nonisolated(unsafe) private static var logs: [String: [Recorded]] = [:]

    /// A session whose requests to `host` are answered by `handler`.
    static func session(host: String, handler: @escaping Handler) -> URLSession {
        lock.withLock {
            handlers[host] = handler
            logs[host] = []
        }
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [StubHTTP.self]
        return URLSession(configuration: config)
    }

    /// Adds or replaces the handler for another host on an existing setup.
    static func register(host: String, handler: @escaping Handler) {
        lock.withLock {
            handlers[host] = handler
            logs[host] = logs[host] ?? []
        }
    }

    static func requests(host: String) -> [Recorded] {
        lock.withLock { logs[host] ?? [] }
    }

    static func uniqueHost(_ label: String) -> String {
        "\(label)-\(UUID().uuidString.prefix(8).lowercased()).test"
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let url = request.url else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }
        let recorded = Recorded(method: request.httpMethod ?? "GET", url: url, body: Self.body(of: request))
        let host = url.host ?? ""
        let handler: Handler? = Self.lock.withLock {
            Self.logs[host, default: []].append(recorded)
            return Self.handlers[host]
        }
        guard let handler else {
            client?.urlProtocol(self, didFailWithError: URLError(.cannotConnectToHost))
            return
        }
        let reply = handler(recorded)
        var headers = reply.headers
        headers["Content-Length"] = String(reply.body.count)
        let response = HTTPURLResponse(url: url, statusCode: reply.status, httpVersion: "HTTP/1.1", headerFields: headers)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: reply.body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    /// Uploads and most POSTs reach a URLProtocol as a stream, not `httpBody`.
    private static func body(of request: URLRequest) -> Data {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return Data() }
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 64 * 1024)
        while stream.hasBytesAvailable {
            let read = stream.read(&buffer, maxLength: buffer.count)
            if read <= 0 { break }
            data.append(buffer, count: read)
        }
        return data
    }
}

/// A thread-safe call counter for stub handlers.
final class CallCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0

    /// Counts one more call and returns the new total (1 for the first).
    func next() -> Int {
        lock.withLock {
            value += 1
            return value
        }
    }

    var count: Int { lock.withLock { value } }
}
