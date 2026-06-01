#if os(macOS)
import Foundation
import os

private let sftpLog = Logger(subsystem: "com.rana.FileFluss", category: "sftp")

public struct SFTPCredentials: Codable, Sendable {
    public enum AuthMethod: String, Codable, Sendable {
        case password
        case privateKey
    }

    public let host: String
    public let port: Int
    public let username: String
    public let authMethod: AuthMethod
    /// Set when authMethod == .password.
    public let password: String?
    /// Raw PEM contents of the private key — set when authMethod == .privateKey.
    public let privateKey: String?
    /// Optional passphrase for the private key. nil when the key is unencrypted.
    public let passphrase: String?
    /// Initial directory the panel lands in. "/" by default.
    public let remotePath: String

    public init(
        host: String,
        port: Int,
        username: String,
        authMethod: AuthMethod = .password,
        password: String? = nil,
        privateKey: String? = nil,
        passphrase: String? = nil,
        remotePath: String = "/"
    ) {
        self.host = host
        self.port = port
        self.username = username
        self.authMethod = authMethod
        self.password = password
        self.privateKey = privateKey
        self.passphrase = passphrase
        self.remotePath = remotePath
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        host = try c.decode(String.self, forKey: .host)
        port = try c.decode(Int.self, forKey: .port)
        username = try c.decode(String.self, forKey: .username)
        // Legacy keychain entries store only host/port/username/password.
        // Default the new fields so they decode cleanly.
        authMethod = try c.decodeIfPresent(AuthMethod.self, forKey: .authMethod) ?? .password
        password = try c.decodeIfPresent(String.self, forKey: .password)
        privateKey = try c.decodeIfPresent(String.self, forKey: .privateKey)
        passphrase = try c.decodeIfPresent(String.self, forKey: .passphrase)
        remotePath = try c.decodeIfPresent(String.self, forKey: .remotePath) ?? "/"
    }
}

public actor SFTPAPIClient {
    let credentials: SFTPCredentials
    private let controlPath: String
    /// SSH_ASKPASS script — echoes the password or the key passphrase.
    /// nil when auth is .privateKey with no passphrase.
    private let askPassScriptPath: String?
    /// Path to the private-key file written for `-i`. nil for password auth.
    private let privateKeyPath: String?

    public init(credentials: SFTPCredentials) {
        self.credentials = credentials
        let id = UUID().uuidString.prefix(8)
        self.controlPath = NSTemporaryDirectory() + "filefluss-sftp-\(id)"

        // Pick the secret SSH_ASKPASS should echo (password OR key passphrase).
        let secret: String?
        switch credentials.authMethod {
        case .password:
            secret = credentials.password
        case .privateKey:
            // Empty passphrase => key is unencrypted, no askpass needed.
            secret = (credentials.passphrase?.isEmpty ?? true) ? nil : credentials.passphrase
        }

        if let secret {
            let path = NSTemporaryDirectory() + "filefluss-sftp-askpass-\(id)"
            let escaped = secret.replacingOccurrences(of: "'", with: "'\\''")
            let script = "#!/bin/sh\necho '\(escaped)'\n"
            FileManager.default.createFile(atPath: path, contents: Data(script.utf8), attributes: [.posixPermissions: 0o700])
            self.askPassScriptPath = path
        } else {
            self.askPassScriptPath = nil
        }

        if credentials.authMethod == .privateKey, let key = credentials.privateKey, !key.isEmpty {
            let path = NSTemporaryDirectory() + "filefluss-sftp-key-\(id)"
            // Make sure the key ends in a newline; some SSH builds reject otherwise.
            let body = key.hasSuffix("\n") ? key : (key + "\n")
            FileManager.default.createFile(atPath: path, contents: Data(body.utf8), attributes: [.posixPermissions: 0o600])
            self.privateKeyPath = path
        } else {
            self.privateKeyPath = nil
        }
    }

    // MARK: - Authentication

    public static func authenticate(
        host: String,
        port: Int,
        username: String,
        password: String? = nil,
        privateKey: String? = nil,
        passphrase: String? = nil,
        remotePath: String = "/"
    ) async throws -> SFTPCredentials {
        let authMethod: SFTPCredentials.AuthMethod = (privateKey?.isEmpty == false) ? .privateKey : .password
        let creds = SFTPCredentials(
            host: host,
            port: port,
            username: username,
            authMethod: authMethod,
            password: password,
            privateKey: privateKey,
            passphrase: passphrase,
            remotePath: remotePath
        )
        let client = SFTPAPIClient(credentials: creds)
        // Verify by listing the chosen remote path (or "/" if it's empty).
        let probePath = remotePath.isEmpty ? "/" : remotePath
        _ = try await client.runBatch(commands: ["ls \(client.shellEscape(probePath))"])
        sftpLog.info("[SFTP] Authenticated as \(username)@\(host):\(port) via \(authMethod.rawValue)")
        return creds
    }

    public func userDisplayName() -> String {
        "\(credentials.username)@\(credentials.host)"
    }

    // MARK: - File Operations

    public func listFolder(path: String) async throws -> [CloudFileItem] {
        let output = try await runListing(path: path)
        return parseListing(output: output, basePath: path)
    }

    /// Runs `ls -la <path>` and returns the raw output.
    ///
    /// We deliberately do **not** pass `-L`. The command here is sftp's
    /// *built-in* `ls` (run inside the SFTP subsystem), not the remote
    /// shell's `ls`, and its flag set is fixed at `[-1afhlnrSt]` across
    /// every OpenSSH version — there is no `-L`. Passing it makes the
    /// built-in print `ls: Invalid flag -L` to stderr, emit **no listing
    /// rows**, and still exit 0. That silent failure made every folder
    /// look empty on every server (issue #31): the empty stdout parsed to
    /// zero items, no error was thrown, and nothing reached the support
    /// log. Sending plain `-la` is what Forklift/FileZilla effectively do.
    private func runListing(path: String) async throws -> String {
        try await runBatch(commands: ["ls -la \(shellEscape(path))"])
    }

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        _ = try await runBatch(commands: ["get \(shellEscape(remotePath)) \(shellEscape(localURL.path))"])
        if let onBytes,
           let attrs = try? FileManager.default.attributesOfItem(atPath: localURL.path),
           let size = attrs[.size] as? Int64 {
            onBytes(size)
        }
    }

    public func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let fileSize: Int64 = (try? FileManager.default.attributesOfItem(atPath: localURL.path)[.size] as? Int64) ?? 0
        // `put -p` preserves the source file's modification time and perms so
        // sync diffs stay stable across re-uploads.
        _ = try await runBatch(commands: ["put -p \(shellEscape(localURL.path)) \(shellEscape(remotePath))"])
        onBytes?(fileSize)
    }

    public func deleteItem(at path: String, isDirectory: Bool) async throws {
        if isDirectory {
            // Remove directory contents recursively, then the directory
            let items = try await listFolder(path: path)
            for item in items {
                try await deleteItem(at: item.path, isDirectory: item.isDirectory)
            }
            _ = try await runBatch(commands: ["rmdir \(shellEscape(path))"])
        } else {
            _ = try await runBatch(commands: ["rm \(shellEscape(path))"])
        }
    }

    public func createFolder(at path: String) async throws {
        if (try? await getFileInfo(at: path)) != nil { return }
        _ = try await runBatch(commands: ["mkdir \(shellEscape(path))"])
    }

    public func renameItem(at path: String, to newName: String) async throws {
        let parentPath = (path as NSString).deletingLastPathComponent
        let destinationPath: String
        if parentPath == "/" {
            destinationPath = "/\(newName)"
        } else {
            destinationPath = "\(parentPath)/\(newName)"
        }
        _ = try await runBatch(commands: ["rename \(shellEscape(path)) \(shellEscape(destinationPath))"])
    }

    public func getFileInfo(at path: String) async throws -> CloudFileItem {
        let parentPath = (path as NSString).deletingLastPathComponent
        let fileName = (path as NSString).lastPathComponent
        let output = try await runListing(path: parentPath)
        let items = parseListing(output: output, basePath: parentPath)
        guard let item = items.first(where: { $0.name == fileName }) else {
            throw CloudProviderError.notFound(path)
        }
        return item
    }

    public func folderSize(path: String) async throws -> Int64 {
        let items = try await listFolder(path: path)
        var total: Int64 = 0
        for item in items {
            if item.isDirectory {
                total += try await folderSize(path: item.path)
            } else {
                total += item.size
            }
        }
        return total
    }

    public func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
        let searchPath = path ?? "/"
        let allItems = try await listAllRecursively(path: searchPath)
        let lowered = query.lowercased()
        return allItems.filter { $0.name.lowercased().contains(lowered) }
    }

    private func listAllRecursively(path: String) async throws -> [CloudFileItem] {
        let items = try await listFolder(path: path)
        var result = items
        for item in items where item.isDirectory {
            let children = try await listAllRecursively(path: item.path)
            result.append(contentsOf: children)
        }
        return result
    }

    // MARK: - Process Execution

    private func runBatch(commands: [String]) async throws -> String {
        let batchContent = commands.joined(separator: "\n") + "\nbye\n"
        let batchPath = NSTemporaryDirectory() + "filefluss-sftp-batch-\(UUID().uuidString.prefix(8))"
        defer { try? FileManager.default.removeItem(atPath: batchPath) }

        guard FileManager.default.createFile(atPath: batchPath, contents: Data(batchContent.utf8)) else {
            throw CloudProviderError.invalidResponse
        }

        return try await withCheckedThrowingContinuation { continuation in
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/sftp")

            var args: [String] = [
                "-oBatchMode=no",
                "-oStrictHostKeyChecking=accept-new",
                "-oControlMaster=auto",
                "-oControlPath=\(controlPath)",
                "-oControlPersist=600",
                "-oConnectTimeout=10",
                "-P", "\(credentials.port)"
            ]

            // Auth-method-specific flags: tell sftp exactly which method
            // to use so it doesn't fall back and prompt the user
            // interactively when the chosen method fails.
            switch credentials.authMethod {
            case .password:
                args.append(contentsOf: [
                    "-oPubkeyAuthentication=no",
                    "-oPreferredAuthentications=password,keyboard-interactive"
                ])
            case .privateKey:
                if let keyPath = privateKeyPath {
                    args.append(contentsOf: ["-i", keyPath])
                }
                args.append(contentsOf: [
                    "-oPasswordAuthentication=no",
                    "-oPreferredAuthentications=publickey",
                    "-oIdentitiesOnly=yes"
                ])
            }

            args.append(contentsOf: ["-b", batchPath, "\(credentials.username)@\(credentials.host)"])
            process.arguments = args

            var env: [String: String] = [
                "DISPLAY": ":0",
                "PATH": "/usr/bin:/bin:/usr/sbin:/sbin",
                "HOME": NSHomeDirectory()
            ]
            if let askPass = askPassScriptPath {
                env["SSH_ASKPASS"] = askPass
                env["SSH_ASKPASS_REQUIRE"] = "force"
            }
            process.environment = env

            let stdoutPipe = Pipe()
            let stderrPipe = Pipe()
            process.standardOutput = stdoutPipe
            process.standardError = stderrPipe
            process.standardInput = FileHandle.nullDevice

            process.terminationHandler = { proc in
                let stdoutData = stdoutPipe.fileHandleForReading.readDataToEndOfFile()
                let stderrData = stderrPipe.fileHandleForReading.readDataToEndOfFile()
                let stdout = String(data: stdoutData, encoding: .utf8) ?? ""
                let stderr = String(data: stderrData, encoding: .utf8) ?? ""

                // Diagnostic capture for the recurring "SFTP folder appears
                // empty" reports (issue #31). Routed through SupportLogger so
                // it lands in the user-exportable Support Log on notarized
                // builds — the os.Logger output never reached that export,
                // which is why earlier support logs came back empty. No secret
                // is logged: the password / key passphrase travels via the
                // SSH_ASKPASS script, never in argv, stdout, or stderr. stdout
                // is the raw `ls` listing whose exact longname format is what
                // we need to parse the affected (newer) servers correctly.
                SupportLogger.shared.log(
                    "cmd: \(commands.joined(separator: " ; ")) @ \(self.credentials.host):\(self.credentials.port) "
                        + "exit=\(proc.terminationStatus)\n"
                        + "stdout (\(stdoutData.count) bytes):\n\(stdout.prefix(1800))\n"
                        + "stderr:\n\(stderr.prefix(600))",
                    category: "sftp",
                    level: proc.terminationStatus == 0 ? .info : .error
                )

                if proc.terminationStatus != 0 {
                    let exit = Int(proc.terminationStatus)
                    let stderrTrim = stderr.trimmingCharacters(in: .whitespacesAndNewlines)
                    let combined = (stderrTrim + " " + stdout).trimmingCharacters(in: .whitespacesAndNewlines)
                    let authMethod = self.credentials.authMethod
                    let error = Self.mapSFTPFailure(exit: exit, stderr: stderrTrim, combined: combined, authMethod: authMethod)
                    continuation.resume(throwing: error)
                    return
                }
                continuation.resume(returning: stdout)
            }

            do {
                try process.run()
            } catch {
                continuation.resume(throwing: CloudProviderError.networkError(error))
            }
        }
    }

    /// Translate an `sftp`/`ssh` failure into a useful CloudProviderError.
    /// We pattern-match the stderr lines openssh actually emits — the exit
    /// code on its own (255 for almost everything ssh-related) is too
    /// coarse to be useful, so the messages drive the dispatch.
    private static func mapSFTPFailure(exit: Int, stderr: String, combined: String, authMethod: SFTPCredentials.AuthMethod) -> CloudProviderError {
        let lower = combined.lowercased()

        // Auth failures — distinguish password vs key vs passphrase so
        // the user knows which thing to fix.
        if lower.contains("permission denied (publickey)") {
            return .commandFailed("SSH key was rejected by the server. Make sure the public key is in the server's ~/.ssh/authorized_keys (or added to the provider's SSH-keys panel for managed services like Hetzner Storage Box).")
        }
        if lower.contains("permission denied") || lower.contains("authentication failed") {
            switch authMethod {
            case .password:
                return .commandFailed("SSH authentication failed. Check the username and password.")
            case .privateKey:
                return .commandFailed("SSH authentication failed. The server may not have your public key authorized, or the passphrase is wrong.")
            }
        }

        // Key-loading problems on the client side.
        if lower.contains("invalid format") || lower.contains("error in libcrypto") {
            return .commandFailed("Private key is in an unsupported format. Convert it to OpenSSH format (e.g. with `puttygen key.ppk -O private-openssh -o id_rsa`).")
        }
        if lower.contains("are too open") || lower.contains("bad permissions") {
            return .commandFailed("Private key file has unsafe permissions. Run `chmod 600 <keyfile>` and try again.")
        }
        if lower.contains("incorrect passphrase") || lower.contains("bad passphrase") || lower.contains("could not decrypt") {
            return .commandFailed("Wrong passphrase for the private key.")
        }

        // Host-key problems.
        if lower.contains("host key verification failed") {
            return .commandFailed("Host key verification failed — the server's host key changed. Remove the stale entry from ~/.ssh/known_hosts and retry.")
        }

        // Connection-level issues.
        if lower.contains("connection refused") {
            return .commandFailed("Connection refused — is the SSH daemon running on this host and port?")
        }
        if lower.contains("connection timed out") || lower.contains("operation timed out") {
            return .commandFailed("Connection timed out. Check the hostname, port, and your network.")
        }
        if lower.contains("no route to host") || lower.contains("network is unreachable") {
            return .commandFailed("Network is unreachable from this Mac.")
        }
        if lower.contains("could not resolve hostname") || lower.contains("name or service not known") {
            return .commandFailed("Could not resolve hostname. Check spelling or DNS.")
        }

        // SFTP-level errors during a command (mkdir/put/get) — surface the
        // first useful line so the user sees what actually failed.
        if lower.contains("no such file") || lower.contains("not found") {
            return .notFound(stderr.isEmpty ? combined : stderr)
        }
        if lower.contains("couldn't") || lower.contains("failure") {
            // e.g. "Couldn't create directory: Permission denied"
            return .commandFailed(combined.isEmpty ? "SFTP command failed (exit \(exit))." : combined)
        }

        // Fall-through: surface the raw stderr if we have it, otherwise
        // the exit code. Truncate aggressively — SFTP can spew banners.
        if !stderr.isEmpty {
            let snippet = stderr.split(separator: "\n").prefix(3).joined(separator: " — ")
            return .commandFailed("SFTP failed (exit \(exit)): \(snippet)")
        }
        return .commandFailed("SFTP failed with exit code \(exit). Check the server is reachable and the credentials are correct.")
    }

    // MARK: - Parsing

    private func parseListing(output: String, basePath: String) -> [CloudFileItem] {
        var items: [CloudFileItem] = []
        for line in output.components(separatedBy: "\n") {
            guard let parsed = Self.parseListingLine(line) else { continue }
            let itemPath: String
            if basePath == "/" {
                itemPath = "/\(parsed.name)"
            } else {
                itemPath = "\(basePath)/\(parsed.name)"
            }
            items.append(CloudFileItem(
                id: parsed.isDirectory ? "d\(itemPath.hashValue)" : "f\(itemPath.hashValue)",
                name: parsed.name,
                path: itemPath,
                isDirectory: parsed.isDirectory,
                size: parsed.isDirectory ? 0 : parsed.size,
                modificationDate: parsed.modDate,
                checksum: nil
            ))
        }

        // Record the parse outcome in the Support Log so it correlates with
        // the raw `ls` output already captured in runBatch — tells us at a
        // glance whether the folder is genuinely empty, parsed fine, or hit
        // an unrecognised longname format (issue #31).
        let totalLines = output.components(separatedBy: "\n").filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }.count
        SupportLogger.shared.log(
            "parsed \(items.count) item(s) from \(totalLines) line(s) for \(basePath) "
                + "(looksLikeListing=\(Self.outputLooksLikeListing(output)))",
            category: "sftp",
            level: (items.isEmpty && Self.outputLooksLikeListing(output)) ? .error : .info
        )

        if items.isEmpty, Self.outputLooksLikeListing(output) {
            // The server returned text that looks like a long listing (had
            // at least one permissions-prefixed line) but none of our
            // patterns matched. This is the symptom behind issue #31:
            // ProFTPD/mod_sftp and a few other SFTP daemons emit ISO-style
            // dates or other longname variants we don't yet recognise.
            sftpLog.error(
                "[SFTP] Folder \(basePath, privacy: .public) parsed 0 items from listing-shaped output; unrecognised longname format. Sample: \(output.prefix(500), privacy: .public)"
            )
        }

        return items
    }

    /// Heuristic for "this output contains at least one line that looks
    /// like a long-form `ls -la` row" — used to distinguish a genuinely
    /// empty directory (no listing lines) from a parser miss.
    public static func outputLooksLikeListing(_ output: String) -> Bool {
        for raw in output.components(separatedBy: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard let first = line.first, "-dlcbps".contains(first) else { continue }
            // Cheap structural check: leading filetype char + at least
            // a few perms chars. Avoids false positives on prompt lines.
            if line.count >= 10 {
                let prefix = line.prefix(10)
                if prefix.dropFirst().allSatisfy({ "rwxsStT@+.\\*-".contains($0) }) {
                    return true
                }
            }
        }
        return false
    }

    /// Parsed shape returned by `parseListingLine` — kept narrow so the
    /// caller (and tests) only see the fields they need.
    public struct ParsedListingLine: Equatable, Sendable {
        public let perms: String
        public let size: Int64
        public let isDirectory: Bool
        public let modDate: Date
        public let name: String
    }

    /// Permissions block: a filetype char followed by the mode bits, plus
    /// the `.`/`*`/`@`/`+` suffixes SELinux contexts and ACL markers add.
    private static let permsClass = #"[dlcbps-][rwxsStT@+.\*\-]{9,}"#

    /// Precompiled once and reused — `parseListingLine` runs per line of
    /// every directory listing, so compiling these on each call (the old
    /// behaviour) meant recompiling the regex thousands of times for a
    /// large folder. `NSRegularExpression` is immutable and safe to share.
    // The second field is the hard-link count. Most servers send a number,
    // but some don't include `st_nlink` in the SFTP attributes, in which case
    // OpenSSH's `sftp` client prints a literal `?` (issue #31: Raspberry Pi
    // OS, OpenWRT, and some hosting providers). Accept either so the whole
    // line still matches — the field itself is unused.
    private static let linkCount = "(\\d+|\\?)"
    private static let classicListingRegex = try? NSRegularExpression(
        pattern: "^(\(permsClass))\\s+\(linkCount)\\s+(\\S+)\\s+(\\S+)\\s+(\\d+)\\s+(\\S+)\\s+(\\d{1,2})\\s+([\\d:]+|\\d{4})\\s+(.+)$"
    )
    private static let isoListingRegex = try? NSRegularExpression(
        pattern: "^(\(permsClass))\\s+\(linkCount)\\s+(\\S+)\\s+(\\S+)\\s+(\\d+)\\s+(\\d{4}-\\d{2}-\\d{2})(?:\\s+(\\d{2}:\\d{2}(?::\\d{2})?))?\\s+(.+)$"
    )

    /// Parses one line of an SFTP server's long-form directory listing.
    /// Returns `nil` for blank lines, prompt lines, `.`/`..` entries, or
    /// rows that don't match any of the recognised longname formats.
    ///
    /// We try the classic OpenSSH `MONTH DAY TIME-OR-YEAR` form first
    /// (preserves every working server) and only fall back to the ISO
    /// `YYYY-MM-DD HH:MM[:SS]?` form emitted by ProFTPD `mod_sftp` and a
    /// few other daemons.
    public static func parseListingLine(_ line: String) -> ParsedListingLine? {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return nil }
        // Permissions field must start with one of these filetype chars.
        // Other leading chars (e.g. "total 4", "sftp>" prompts) are noise.
        guard let first = trimmed.first, "-dlcbps".contains(first) else { return nil }

        let range = NSRange(trimmed.startIndex..., in: trimmed)

        if let regex = Self.classicListingRegex,
           let match = regex.firstMatch(in: trimmed, range: range),
           let permsRange = Range(match.range(at: 1), in: trimmed),
           let sizeRange = Range(match.range(at: 5), in: trimmed),
           let monthRange = Range(match.range(at: 6), in: trimmed),
           let dayRange = Range(match.range(at: 7), in: trimmed),
           let timeRange = Range(match.range(at: 8), in: trimmed),
           let nameRange = Range(match.range(at: 9), in: trimmed) {
            let perms = String(trimmed[permsRange])
            // Sanity-check the month token — if it's not a recognised
            // abbreviation we likely matched the wrong fields (e.g. a
            // numeric "owner" was 1-char which the regex tolerated). Bail
            // and let the ISO branch try.
            let monthStr = String(trimmed[monthRange])
            if Self.monthNumber(for: monthStr) != nil {
                let size = Int64(trimmed[sizeRange]) ?? 0
                let day = String(trimmed[dayRange])
                let timeOrYear = String(trimmed[timeRange])
                let modDate = Self.parseDate(month: monthStr, day: day, timeOrYear: timeOrYear)
                let name = Self.cleanupListingName(rawName: String(trimmed[nameRange]), perms: perms)
                if let name {
                    return ParsedListingLine(
                        perms: perms,
                        size: size,
                        isDirectory: perms.first == "d",
                        modDate: modDate,
                        name: name
                    )
                }
            }
        }

        if let regex = Self.isoListingRegex,
           let match = regex.firstMatch(in: trimmed, range: range),
           let permsRange = Range(match.range(at: 1), in: trimmed),
           let sizeRange = Range(match.range(at: 5), in: trimmed),
           let dateRange = Range(match.range(at: 6), in: trimmed),
           let nameRange = Range(match.range(at: 8), in: trimmed) {
            let perms = String(trimmed[permsRange])
            let size = Int64(trimmed[sizeRange]) ?? 0
            let date = String(trimmed[dateRange])
            let timeStr: String? = {
                guard let r = Range(match.range(at: 7), in: trimmed) else { return nil }
                return String(trimmed[r])
            }()
            let modDate = Self.parseISODate(date: date, time: timeStr)
            let name = Self.cleanupListingName(rawName: String(trimmed[nameRange]), perms: perms)
            if let name {
                return ParsedListingLine(
                    perms: perms,
                    size: size,
                    isDirectory: perms.first == "d",
                    modDate: modDate,
                    name: name
                )
            }
        }

        return nil
    }

    /// Octal-unescape the raw name field, drop `.`/`..` self-references,
    /// and strip the `" -> target"` suffix for symlinks.
    private static func cleanupListingName(rawName: String, perms: String) -> String? {
        var name = Self.unescapeOctal(rawName)
        // Symlinks print "name -> target"; keep only the link's own name.
        // Strip the target first so a "/" inside it can't be mistaken for a
        // path separator when we take the basename below.
        if perms.first == "l", let arrow = name.range(of: " -> ") {
            name = String(name[name.startIndex..<arrow.lowerBound])
        }
        // Some servers/clients print full paths rather than basenames when the
        // listing target is an absolute path (issue #31: `ls -la /` yields
        // "/VERSION", "/html/.htaccess", "/.", …). A filename can't contain a
        // "/", so the entry's real name is whatever follows the last one.
        if let slash = name.lastIndex(of: "/") {
            name = String(name[name.index(after: slash)...])
        }
        if name == "." || name == ".." { return nil }
        return name.isEmpty ? nil : name
    }

    private static func parseDate(month: String, day: String, timeOrYear: String) -> Date {
        let calendar = Calendar.current
        let currentYear = calendar.component(.year, from: Date())

        guard let monthNum = monthNumber(for: month), let dayNum = Int(day) else {
            return .distantPast
        }

        var components = DateComponents()
        components.month = monthNum
        components.day = dayNum

        if timeOrYear.contains(":") {
            // Time format: HH:MM — assume current year (matches the
            // convention `ls` uses for files modified in the last 6 mo).
            let parts = timeOrYear.split(separator: ":")
            components.year = currentYear
            components.hour = Int(parts[0]) ?? 0
            components.minute = parts.count > 1 ? Int(parts[1]) ?? 0 : 0
        } else {
            components.year = Int(timeOrYear) ?? currentYear
        }

        return calendar.date(from: components) ?? .distantPast
    }

    /// ISO 8601-ish `YYYY-MM-DD [HH:MM[:SS]]` form used by ProFTPD
    /// `mod_sftp` and a handful of other SFTP daemons.
    private static func parseISODate(date: String, time: String?) -> Date {
        let parts = date.split(separator: "-")
        guard parts.count == 3,
              let year = Int(parts[0]),
              let month = Int(parts[1]),
              let day = Int(parts[2]) else {
            return .distantPast
        }
        var components = DateComponents()
        components.year = year
        components.month = month
        components.day = day
        if let time {
            let tp = time.split(separator: ":")
            components.hour = tp.count > 0 ? Int(tp[0]) ?? 0 : 0
            components.minute = tp.count > 1 ? Int(tp[1]) ?? 0 : 0
            components.second = tp.count > 2 ? Int(tp[2]) ?? 0 : 0
        }
        return Calendar.current.date(from: components) ?? .distantPast
    }

    /// English month-abbreviation lookup plus the handful of German
    /// abbreviations OpenSSH builds emit when forced into a non-C
    /// locale (`Mär`, `Mrz`, `Okt`, `Dez`). `Mai` happens to match the
    /// English `May` semantically so we get it for free.
    private static func monthNumber(for token: String) -> Int? {
        switch token {
        case "Jan": return 1
        case "Feb": return 2
        case "Mar", "Mär", "Mrz": return 3
        case "Apr": return 4
        case "May", "Mai": return 5
        case "Jun": return 6
        case "Jul": return 7
        case "Aug": return 8
        case "Sep", "Sept": return 9
        case "Oct", "Okt": return 10
        case "Nov": return 11
        case "Dec", "Dez": return 12
        default: return nil
        }
    }

    // MARK: - Helpers

    private func shellEscape(_ path: String) -> String {
        // Wrap in double quotes and escape special chars
        let escaped = path
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
            .replacingOccurrences(of: "$", with: "\\$")
            .replacingOccurrences(of: "`", with: "\\`")
        return "\"\(escaped)\""
    }

    /// Decode octal escape sequences (e.g. `\314\210`) in filenames from `ls -la` output.
    private static func unescapeOctal(_ input: String) -> String {
        var bytes: [UInt8] = []
        var i = input.startIndex
        while i < input.endIndex {
            if input[i] == "\\" {
                let next = input.index(after: i)
                // Check for 3 octal digits after backslash
                if next < input.endIndex,
                   let end = input.index(next, offsetBy: 3, limitedBy: input.endIndex),
                   let value = UInt8(input[next..<end], radix: 8) {
                    bytes.append(value)
                    i = end
                } else {
                    bytes.append(contentsOf: Array(String(input[i]).utf8))
                    i = input.index(after: i)
                }
            } else {
                bytes.append(contentsOf: Array(String(input[i]).utf8))
                i = input.index(after: i)
            }
        }
        return String(bytes: bytes, encoding: .utf8) ?? input
    }
}
#endif
