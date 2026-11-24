import Foundation

/// Answers provider requests from a script instead of the network.
///
/// Each test registers its own host, so suites running in parallel never
/// see each other's responses: the routes live in a lock-protected table
/// keyed by host, and every request is recorded for the test to inspect.
final class ProviderStubURLProtocol: URLProtocol, @unchecked Sendable {

    struct Reply: Sendable {
        var status: Int
        var body: Data
        var headers: [String: String] = [:]

        static func json(_ text: String, status: Int = 200) -> Reply {
            Reply(status: status, body: Data(text.utf8), headers: ["Content-Type": "application/json"])
        }

        static func status(_ status: Int, _ text: String = "") -> Reply {
            Reply(status: status, body: Data(text.utf8))
        }
    }

    struct Recorded: Sendable {
        let method: String
        let url: URL
        let body: Data?
    }

    typealias Handler = @Sendable (URLRequest, Data?) -> Reply

    private static let lock = NSLock()
    nonisolated(unsafe) private static var handlers: [String: Handler] = [:]
    nonisolated(unsafe) private static var recorded: [String: [Recorded]] = [:]

    /// A session whose requests to `host` are answered by `handler`.
    static func session(host: String, handler: @escaping Handler) -> URLSession {
        lock.lock()
        handlers[host] = handler
        recorded[host] = []
        lock.unlock()
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [ProviderStubURLProtocol.self]
        return URLSession(configuration: configuration)
    }

    static func requests(for host: String) -> [Recorded] {
        lock.lock()
        defer { lock.unlock() }
        return recorded[host] ?? []
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let url = request.url, let host = url.host else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }
        let body = Self.bodyData(of: request)
        Self.lock.lock()
        let handler = Self.handlers[host]
        Self.recorded[host, default: []].append(Recorded(method: request.httpMethod ?? "GET", url: url, body: body))
        Self.lock.unlock()

        let reply = handler?(request, body) ?? .status(599, "no stub for \(host)")
        let response = HTTPURLResponse(url: url, statusCode: reply.status, httpVersion: "HTTP/1.1", headerFields: reply.headers)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: reply.body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    /// URLSession hands a protocol uploads as a stream, not `httpBody`.
    private static func bodyData(of request: URLRequest) -> Data? {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return nil }
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
