import Foundation

/// Checks whether a URL really is reachable *without* credentials.
///
/// A share link is only worth handing to someone if it works for a stranger,
/// and some providers hand back a direct-download URL their own account plan
/// then refuses to serve — Box rejects direct links on plans without that
/// feature, answering with an HTML page rather than the file. Only an
/// anonymous request can tell the difference.
public enum PublicURLCheck {

    public struct Result: Sendable {
        public let ok: Bool
        /// The server's own explanation when `ok` is false, condensed to one
        /// readable line.
        public let detail: String?
    }

    public static func isReachable(_ url: URL, timeout: TimeInterval = 20) async -> Result {
        // Ephemeral, cookie-less and credential-less: whatever this session
        // sees is what an outside recipient would see.
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil
        config.urlCredentialStorage = nil
        config.timeoutIntervalForRequest = timeout
        let session = URLSession(configuration: config)

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        // Enough to prove the link serves something without pulling a whole
        // file; servers that ignore Range simply send more.
        request.setValue("bytes=0-2047", forHTTPHeaderField: "Range")

        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                return Result(ok: false, detail: "no HTTP response")
            }
            guard (200...299).contains(http.statusCode) else {
                let landed = http.url?.absoluteString ?? url.absoluteString
                return Result(
                    ok: false,
                    detail: "HTTP \(http.statusCode) (final URL: \(landed)) — \(summarize(data))"
                )
            }
            if data.isEmpty {
                return Result(ok: false, detail: "empty response body")
            }
            return Result(ok: true, detail: nil)
        } catch {
            return Result(ok: false, detail: error.localizedDescription)
        }
    }

    /// One-line gist of an error body: XML and JSON errors are short and
    /// precise, HTML never is, so tags are stripped and the rest truncated.
    public static func summarize(_ data: Data) -> String {
        guard let raw = String(data: data.prefix(4096), encoding: .utf8), !raw.isEmpty else {
            return "empty body"
        }
        let stripped = raw
            .replacingOccurrences(of: "<[^>]+>", with: " ", options: .regularExpression)
            .replacingOccurrences(of: "&amp;", with: "&")
            .replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return stripped.isEmpty ? "body had no readable text" : String(stripped.prefix(300))
    }
}
