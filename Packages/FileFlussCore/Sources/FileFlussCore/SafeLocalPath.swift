import Foundation

/// Builds local file paths from names that came off the network.
///
/// A remote name is attacker-controlled input: a hostile or compromised
/// server (self-hosted WebDAV, Nextcloud, SFTP and friends are a
/// first-class feature here), or merely someone who shares a file with the
/// user, chooses it. `appendingPathComponent` happily accepts `../..` and
/// interior slashes, so a name like `../../Library/LaunchAgents/evil.plist`
/// writes far outside the folder the user picked — code execution at next
/// login, from nothing more than clicking Download.
///
/// Two independent defences, because either alone can be argued around:
/// the name is reduced to a single harmless path component, and the result
/// is then checked to be inside the intended directory.
public enum SafeLocalPath {

    /// A remote name reduced to something safe to use as one path
    /// component. Never empty, never `.`/`..`, never containing a
    /// separator.
    public static func fileName(from remoteName: String) -> String {
        var cleaned = remoteName
            .replacingOccurrences(of: "/", with: "-")
            .replacingOccurrences(of: "\\", with: "-")
            .replacingOccurrences(of: "\0", with: "")
            .trimmingCharacters(in: .whitespacesAndNewlines)

        // "." and ".." are directory references, not names.
        while cleaned == "." || cleaned == ".." {
            cleaned = cleaned.replacingOccurrences(of: ".", with: "_")
        }

        if cleaned.isEmpty { cleaned = "unnamed" }

        // HFS+/APFS tolerate long names up to 255 bytes; keep room for the
        // extension a provider may append later.
        if cleaned.utf8.count > 200 {
            cleaned = String(cleaned.prefix(200))
        }
        return cleaned
    }

    /// Whether a path holds a `.` or `..` component, the only way a path can
    /// point somewhere other than where it appears to. Both separators are
    /// checked: a backslash is an ordinary character here, but some of the
    /// servers these paths pass through treat it as a separator.
    public static func hasTraversal(_ path: String) -> Bool {
        path.split(whereSeparator: { $0 == "/" || $0 == "\\" })
            .contains { $0 == "." || $0 == ".." }
    }

    /// A slash-separated path that came off the network, resolved inside
    /// `root`. Unlike `destination(for:in:)` this keeps the folder structure,
    /// which sync needs, and refuses a path that would leave `root` rather
    /// than rewriting it.
    public static func destination(forRelativePath path: String, in root: URL) throws -> URL {
        let candidate = root.appendingPathComponent(path)
        guard !hasTraversal(path), isContained(path, in: root) else {
            throw CloudProviderError.commandFailed(
                L10n.format(
                    "The server returned an unsafe file name (\"%@\"), so the download was stopped.",
                    path
                )
            )
        }
        return candidate
    }

    /// Whether `path`, appended to `root`, still lies below it once both are
    /// standardized.
    ///
    /// The candidate is built from the root as standardized, not from the
    /// root as given. Standardizing drops "/private" from a path that exists
    /// (the folder) but not from one that doesn't exist yet (the file about
    /// to be written), so comparing the two refused everything synced or
    /// extracted into /private/tmp or /private/var.
    private static func isContained(_ path: String, in root: URL) -> Bool {
        let base = root.standardizedFileURL
        let basePath = base.path
        let resolved = base.appendingPathComponent(path).standardizedFileURL.path
        return resolved.hasPrefix(basePath.hasSuffix("/") ? basePath : basePath + "/")
    }

    /// Destination for `remoteName` inside `directory`, guaranteed to stay
    /// inside it. Throws rather than writing anywhere unexpected.
    public static func destination(for remoteName: String, in directory: URL) throws -> URL {
        let safeName = fileName(from: remoteName)
        let candidate = directory.appendingPathComponent(safeName)

        // Belt and braces: even with the name sanitised, resolve both sides
        // and confirm containment before handing the URL to a writer.
        guard isContained(safeName, in: directory) else {
            throw CloudProviderError.commandFailed(
                L10n.format(
                    "The server returned an unsafe file name (\"%@\"), so the download was stopped.",
                    remoteName
                )
            )
        }
        return candidate
    }
}
