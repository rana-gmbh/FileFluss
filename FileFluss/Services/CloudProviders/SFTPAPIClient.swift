import Foundation
import os

private let sftpLog = Logger(subsystem: "com.rana.FileFluss", category: "sftp")

struct SFTPCredentials: Codable, Sendable {
    enum AuthMethod: String, Codable, Sendable {
        case password
        case privateKey
    }

    let host: String
    let port: Int
    let username: String
    let authMethod: AuthMethod
    /// Set when authMethod == .password.
    let password: String?
    /// Raw PEM contents of the private key — set when authMethod == .privateKey.
    let privateKey: String?
    /// Optional passphrase for the private key. nil when the key is unencrypted.
    let passphrase: String?
    /// Initial directory the panel lands in. "/" by default.
    let remotePath: String

    init(
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

    init(from decoder: Decoder) throws {
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

actor SFTPAPIClient {
    let credentials: SFTPCredentials
    private let controlPath: String
    /// SSH_ASKPASS script — echoes the password or the key passphrase.
    /// nil when auth is .privateKey with no passphrase.
    private let askPassScriptPath: String?
    /// Path to the private-key file written for `-i`. nil for password auth.
    private let privateKeyPath: String?

    init(credentials: SFTPCredentials) {
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

    static func authenticate(
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

    func userDisplayName() -> String {
        "\(credentials.username)@\(credentials.host)"
    }

    // MARK: - File Operations

    func listFolder(path: String) async throws -> [CloudFileItem] {
        let output = try await runBatch(commands: ["ls -la \(shellEscape(path))"])
        return parseListing(output: output, basePath: path)
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        _ = try await runBatch(commands: ["get \(shellEscape(remotePath)) \(shellEscape(localURL.path))"])
        if let onBytes,
           let attrs = try? FileManager.default.attributesOfItem(atPath: localURL.path),
           let size = attrs[.size] as? Int64 {
            onBytes(size)
        }
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let fileSize: Int64 = (try? FileManager.default.attributesOfItem(atPath: localURL.path)[.size] as? Int64) ?? 0
        // `put -p` preserves the source file's modification time and perms so
        // sync diffs stay stable across re-uploads.
        _ = try await runBatch(commands: ["put -p \(shellEscape(localURL.path)) \(shellEscape(remotePath))"])
        onBytes?(fileSize)
    }

    func deleteItem(at path: String, isDirectory: Bool) async throws {
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

    func createFolder(at path: String) async throws {
        if (try? await getFileInfo(at: path)) != nil { return }
        _ = try await runBatch(commands: ["mkdir \(shellEscape(path))"])
    }

    func renameItem(at path: String, to newName: String) async throws {
        let parentPath = (path as NSString).deletingLastPathComponent
        let destinationPath: String
        if parentPath == "/" {
            destinationPath = "/\(newName)"
        } else {
            destinationPath = "\(parentPath)/\(newName)"
        }
        _ = try await runBatch(commands: ["rename \(shellEscape(path)) \(shellEscape(destinationPath))"])
    }

    func getFileInfo(at path: String) async throws -> CloudFileItem {
        let parentPath = (path as NSString).deletingLastPathComponent
        let fileName = (path as NSString).lastPathComponent
        let output = try await runBatch(commands: ["ls -la \(shellEscape(parentPath))"])
        let items = parseListing(output: output, basePath: parentPath)
        guard let item = items.first(where: { $0.name == fileName }) else {
            throw CloudProviderError.notFound(path)
        }
        return item
    }

    func folderSize(path: String) async throws -> Int64 {
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

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
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

                // Debug: write to log file
                let debugLine = "[SFTP] exit=\(proc.terminationStatus) stdout=\(stdout.prefix(500)) stderr=\(stderr.prefix(500))\n"
                let logPath = "/tmp/filefluss-sftp.log"
                if let fh = FileHandle(forWritingAtPath: logPath) {
                    fh.seekToEndOfFile()
                    fh.write(Data(debugLine.utf8))
                    fh.closeFile()
                } else {
                    FileManager.default.createFile(atPath: logPath, contents: Data(debugLine.utf8))
                }

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
        let lines = output.components(separatedBy: "\n")

        for line in lines {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard !trimmed.isEmpty else { continue }

            // Skip sftp prompt lines and non-listing lines
            guard trimmed.first == "-" || trimmed.first == "d" || trimmed.first == "l" || trimmed.first == "c" || trimmed.first == "b" || trimmed.first == "p" || trimmed.first == "s" else { continue }

            // Parse: permissions links owner group size month day time/year name
            // Use regex for robust parsing with variable whitespace
            let pattern = #"^([dlcbps-][rwxsStT@+-]{9,})\s+(\d+)\s+(\S+)\s+(\S+)\s+(\d+)\s+(\w+)\s+(\d{1,2})\s+([\d:]+|\d{4})\s+(.+)$"#
            guard let regex = try? NSRegularExpression(pattern: pattern),
                  let match = regex.firstMatch(in: trimmed, range: NSRange(trimmed.startIndex..., in: trimmed)) else {
                continue
            }

            guard let permsRange = Range(match.range(at: 1), in: trimmed),
                  let sizeRange = Range(match.range(at: 5), in: trimmed),
                  let monthRange = Range(match.range(at: 6), in: trimmed),
                  let dayRange = Range(match.range(at: 7), in: trimmed),
                  let timeRange = Range(match.range(at: 8), in: trimmed),
                  let nameRange = Range(match.range(at: 9), in: trimmed) else {
                continue
            }

            let perms = String(trimmed[permsRange])
            let size = Int64(trimmed[sizeRange]) ?? 0
            let month = String(trimmed[monthRange])
            let day = String(trimmed[dayRange])
            let timeOrYear = String(trimmed[timeRange])
            var name = Self.unescapeOctal(String(trimmed[nameRange]))

            // Skip . and ..
            if name == "." || name == ".." { continue }

            // Handle symlinks: "name -> target"
            if perms.first == "l", let arrowRange = name.range(of: " -> ") {
                name = String(name[name.startIndex..<arrowRange.lowerBound])
            }

            let isDirectory = perms.first == "d"
            let modDate = Self.parseDate(month: month, day: day, timeOrYear: timeOrYear)

            let itemPath: String
            if basePath == "/" {
                itemPath = "/\(name)"
            } else {
                itemPath = "\(basePath)/\(name)"
            }

            let item = CloudFileItem(
                id: isDirectory ? "d\(itemPath.hashValue)" : "f\(itemPath.hashValue)",
                name: name,
                path: itemPath,
                isDirectory: isDirectory,
                size: isDirectory ? 0 : size,
                modificationDate: modDate,
                checksum: nil
            )
            items.append(item)
        }

        return items
    }

    private static func parseDate(month: String, day: String, timeOrYear: String) -> Date {
        let calendar = Calendar.current
        let now = Date()
        let currentYear = calendar.component(.year, from: now)

        let months = ["Jan": 1, "Feb": 2, "Mar": 3, "Apr": 4, "May": 5, "Jun": 6,
                       "Jul": 7, "Aug": 8, "Sep": 9, "Oct": 10, "Nov": 11, "Dec": 12]
        guard let monthNum = months[month], let dayNum = Int(day) else {
            return .distantPast
        }

        var components = DateComponents()
        components.month = monthNum
        components.day = dayNum

        if timeOrYear.contains(":") {
            // Time format: HH:MM — assume current year
            let parts = timeOrYear.split(separator: ":")
            components.year = currentYear
            components.hour = Int(parts[0]) ?? 0
            components.minute = parts.count > 1 ? Int(parts[1]) ?? 0 : 0
        } else {
            // Year format
            components.year = Int(timeOrYear) ?? currentYear
        }

        return calendar.date(from: components) ?? .distantPast
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
