import Foundation

/// Decides where login details may go.
public enum CredentialScope {

    /// Whether a request to `target` may carry credentials meant for
    /// `origin`: the same host and port, over a connection no weaker than the
    /// original. Moving up from `http` to `https` is fine, the other way round
    /// would send the password in the clear.
    public static func allows(_ target: URL?, for origin: URL?) -> Bool {
        guard let target, let origin,
              let targetHost = target.host?.lowercased(),
              targetHost == origin.host?.lowercased() else { return false }
        let targetScheme = target.scheme?.lowercased()
        let originScheme = origin.scheme?.lowercased()
        switch (originScheme, targetScheme) {
        case ("https", "https"), ("http", "http"):
            return effectivePort(target) == effectivePort(origin)
        case ("http", "https"):
            // The usual upgrade, on the standard ports only.
            return effectivePort(origin) == 80 && effectivePort(target) == 443
        default:
            return false
        }
    }

    private static func effectivePort(_ url: URL) -> Int? {
        url.port ?? (url.scheme?.lowercased() == "https" ? 443 : 80)
    }

    /// Whether a password sent to `address` would cross a network in the
    /// clear. Addresses on this Mac or the local network don't count: home
    /// NAS units are often reached over plain HTTP, and that's a fair choice.
    public static func exposesPassword(address: String) -> Bool {
        let trimmed = address.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard trimmed.hasPrefix("http://"), let host = URLComponents(string: trimmed)?.host else { return false }
        if host == "localhost" || host.hasSuffix(".local") || host.hasSuffix(".localhost") { return false }
        let octets = host.split(separator: ".").compactMap { Int($0) }
        guard octets.count == 4 else { return true }
        switch (octets[0], octets[1]) {
        case (10, _), (127, _), (192, 168), (169, 254): return false
        case (172, 16...31): return false
        default: return true
        }
    }
}
