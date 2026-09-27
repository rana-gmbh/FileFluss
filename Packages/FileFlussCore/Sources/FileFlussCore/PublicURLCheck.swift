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
        // First attempt is a cheap ranged GET. Some providers serve their
        // share pages only to browser-shaped requests and answer anything
        // else with 403 — which would be reported as "not public" although
        // the link works perfectly in a browser. So a refusal is retried
        // once, without the Range header and with a browser user agent,
        // and only a second failure counts.
        let first = await attempt(url, timeout: timeout, ranged: true, browserLike: false)
        if first.ok || first.isSignInRedirect { return first.result }
        let second = await attempt(url, timeout: timeout, ranged: false, browserLike: true)
        return second.result
    }

    private struct Attempt {
        let result: Result
        let isSignInRedirect: Bool
        var ok: Bool { result.ok }
    }

    private static func attempt(
        _ url: URL,
        timeout: TimeInterval,
        ranged: Bool,
        browserLike: Bool
    ) async -> Attempt {
        // Ephemeral, cookie-less and credential-less: whatever this session
        // sees is what an outside recipient would see.
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil
        config.urlCredentialStorage = nil
        config.timeoutIntervalForRequest = timeout
        let session = URLSession(configuration: config)

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        if ranged {
            // Enough to prove the link serves something without pulling a
            // whole file; servers that ignore Range simply send more.
            request.setValue("bytes=0-2047", forHTTPHeaderField: "Range")
        }
        if browserLike {
            request.setValue(
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15",
                forHTTPHeaderField: "User-Agent"
            )
            request.setValue("text/html,application/xhtml+xml,*/*", forHTTPHeaderField: "Accept")
        }

        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                return Attempt(result: Result(ok: false, detail: "no HTTP response"), isSignInRedirect: false)
            }
            guard (200...299).contains(http.statusCode) else {
                let landed = http.url?.absoluteString ?? url.absoluteString
                return Attempt(
                    result: Result(
                        ok: false,
                        detail: "HTTP \(http.statusCode) (final URL: \(landed)) — \(summarize(data))"
                    ),
                    isSignInRedirect: false
                )
            }
            if data.isEmpty {
                return Attempt(result: Result(ok: false, detail: "empty response body"), isSignInRedirect: false)
            }
            // A sign-in page is served as a perfectly good HTTP 200, so the
            // status code alone can't tell a public link from one that
            // quietly demands an account. Where we ended up can.
            if let landed = http.url, let host = landed.host?.lowercased(),
               Self.signInHosts.contains(where: { host == $0 || host.hasSuffix(".\($0)") }) {
                return Attempt(
                    result: Result(
                        ok: false,
                        detail: "the link redirects to a sign-in page at \(host) — it is not public"
                    ),
                    isSignInRedirect: true
                )
            }
            return Attempt(result: Result(ok: true, detail: nil), isSignInRedirect: false)
        } catch {
            return Attempt(result: Result(ok: false, detail: error.localizedDescription), isSignInRedirect: false)
        }
    }

    /// Hosts that mean "sign in first". Landing on one of these is proof
    /// the link isn't usable by a stranger, whatever the status code says.
    private static let signInHosts: Set<String> = [
        "login.live.com",
        "login.microsoftonline.com",
        "login.microsoft.com",
        "account.live.com",
        "accounts.google.com",
        "login.yahoo.com",
        "appleid.apple.com",
        "secure.login.gov",
    ]

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
