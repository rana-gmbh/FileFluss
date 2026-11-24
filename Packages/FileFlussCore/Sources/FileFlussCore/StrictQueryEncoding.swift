import Foundation

/// Percent-encoding for a single query or form value.
///
/// `.urlQueryAllowed` (and `URLQueryItem`) leave `&`, `=` and `+` alone: a
/// file named `Q&A` then splits the query, so deleting `/Docs/Q&A` deletes
/// `/Docs/Q`, and servers that form-decode read `+` as a space. Only the
/// RFC 3986 unreserved characters stay as they are; everything else —
/// slashes and non-ASCII included, as UTF-8 — is escaped. This matches
/// `Uri.EscapeDataString` on Windows (`CloudHttp.Escape`).
enum StrictQueryEncoding {
    /// ASCII only: `CharacterSet.alphanumerics` would let "ü" through raw.
    private static let unreserved = CharacterSet(
        charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~"
    )

    static func encode(_ value: String) -> String {
        // Fails only for unpaired surrogates, which a Swift String can't hold.
        value.addingPercentEncoding(withAllowedCharacters: unreserved) ?? value
    }

    /// `name=value&…`, every name and value encoded strictly.
    static func query(_ pairs: [(String, String)]) -> String {
        pairs.map { "\(encode($0.0))=\(encode($0.1))" }.joined(separator: "&")
    }
}
