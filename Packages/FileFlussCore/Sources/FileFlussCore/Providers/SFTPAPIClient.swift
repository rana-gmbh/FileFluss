#if os(macOS)
import Darwin
import Foundation
import os

private let sftpLog = Logger(subsystem: "com.rana.FileFluss", category: "sftp")

public struct SFTPCredentials: Codable, Sendable {
    public enum AuthMethod: String, Codable, Sendable {
        case password
        case privateKey
        case sshAgent
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
    private let knownHostsFile: URL?
    private lazy var controlDirectory = try? SFTPTemporaryDirectory()

    public init(credentials: SFTPCredentials) {
        self.credentials = credentials
        self.knownHostsFile = nil
    }

    init(credentials: SFTPCredentials, knownHostsFile: URL) {
        self.credentials = credentials
        self.knownHostsFile = knownHostsFile
    }

    // MARK: - Authentication

    public static func authenticate(
        host: String,
        port: Int,
        username: String,
        password: String? = nil,
        privateKey: String? = nil,
        passphrase: String? = nil,
        useSSHAgent: Bool = false,
        remotePath: String = "/"
    ) async throws -> SFTPCredentials {
        let authMethod: SFTPCredentials.AuthMethod
        if useSSHAgent {
            authMethod = .sshAgent
        } else {
            authMethod = (privateKey?.isEmpty == false) ? .privateKey : .password
        }
        let creds = SFTPCredentials(
            host: host,
            port: port,
            username: username,
            authMethod: authMethod,
            password: authMethod == .password ? password : nil,
            privateKey: authMethod == .privateKey ? privateKey : nil,
            passphrase: authMethod == .privateKey ? passphrase : nil,
            remotePath: remotePath
        )
        let client = SFTPAPIClient(credentials: creds)
        // Verify by listing the chosen remote path (or "/" if it's empty).
        let probePath = remotePath.isEmpty ? "/" : remotePath
        _ = try await client.runBatch(commands: ["ls \(SFTPAPIClient.shellEscape(probePath))"])
        sftpLog.info("[SFTP] Authenticated as \(username)@\(host):\(port) via \(authMethod.rawValue)")
        return creds
    }

    public func userDisplayName() -> String {
        "\(credentials.username)@\(credentials.host)"
    }

    // MARK: - File Operations

    public func listFolder(path: String) async throws -> [CloudFileItem] {
        let output = try await runListing(path: path)
        return try parseListing(output: output, basePath: path)
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
        try await runBatch(commands: ["ls -la \(Self.shellEscape(path))"])
    }

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        // `get` writes as it goes, so it gets a name of its own: a drop part
        // way must not leave a short file, or cost the file already there.
        let partial = localURL.deletingLastPathComponent()
            .appendingPathComponent(FileSystemService.temporaryReplacementName(for: localURL.lastPathComponent))
        do {
            _ = try await runBatch(commands: ["get \(Self.shellEscape(remotePath)) \(Self.shellEscape(partial.path))"])
            try FileSystemService.placeDownload(from: partial, at: localURL)
        } catch {
            try? FileManager.default.removeItem(at: partial)
            throw error
        }
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
        _ = try await runBatch(commands: ["put -p \(Self.shellEscape(localURL.path)) \(Self.shellEscape(remotePath))"])
        onBytes?(fileSize)
    }

    public func deleteItem(at path: String, isDirectory: Bool) async throws {
        if isDirectory {
            // Remove directory contents recursively, then the directory
            let items = try await listFolder(path: path)
            for item in items {
                try await deleteItem(at: item.path, isDirectory: item.isDirectory)
            }
            _ = try await runBatch(commands: ["rmdir \(Self.shellEscape(path))"])
        } else {
            _ = try await runBatch(commands: ["rm \(Self.shellEscape(path))"])
        }
    }

    public func createFolder(at path: String) async throws {
        if (try? await getFileInfo(at: path)) != nil { return }
        _ = try await runBatch(commands: ["mkdir \(Self.shellEscape(path))"])
    }

    public func renameItem(at path: String, to newName: String) async throws {
        let parentPath = (path as NSString).deletingLastPathComponent
        let destinationPath: String
        if parentPath == "/" {
            destinationPath = "/\(newName)"
        } else {
            destinationPath = "\(parentPath)/\(newName)"
        }
        _ = try await runBatch(commands: ["rename \(Self.shellEscape(path)) \(Self.shellEscape(destinationPath))"])
    }

    // MARK: - Server-side move and copy

    /// Moves an item on the server. A rename, so it costs the same whether
    /// the file is a kilobyte or a terabyte — which is what the operation
    /// looked like to the user until now, when it downloaded the file and
    /// uploaded it again (issue #62).
    ///
    /// The sftp client issues `posix-rename@openssh.com` where the server
    /// offers it, so an overwrite is atomic.
    public func moveItem(at path: String, toPath newPath: String) async throws {
        do {
            _ = try await runBatch(commands: [Self.renameCommand(from: path, to: newPath)])
        } catch {
            // A rename the server won't do — across filesystems, most
            // often — is not a failure to report but a reason to fall back
            // to copying the bytes through. `.notImplemented` is how a
            // provider says that.
            throw Self.isRefusedServerSideOperation(error) ? CloudProviderError.notImplemented : error
        }
    }

    /// Copies an item on the server, where the server can.
    ///
    /// `cp` needs the `copy-data` extension, which OpenSSH's sftp-server
    /// has had since 9.0 — and plenty of servers in use are older, or
    /// aren't OpenSSH at all. The first attempt against an account settles
    /// it; after that the answer is remembered, so a folder of a thousand
    /// files doesn't pay a failed round trip a thousand times.
    ///
    /// Files only: `cp` does not recurse, so a directory is declined here
    /// and the caller falls back to its own copy.
    public func copyItem(at path: String, toPath newPath: String) async throws {
        if serverRefusesCopyData { throw CloudProviderError.notImplemented }

        let info = try? await getFileInfo(at: path)
        if info?.isDirectory == true {
            // Nothing to do server-side with one command; let the caller
            // walk it instead.
            throw CloudProviderError.notImplemented
        }

        do {
            _ = try await runBatch(commands: [Self.copyCommand(from: path, to: newPath)])
        } catch {
            if Self.isUnsupportedCopy(error) {
                serverRefusesCopyData = true
                throw CloudProviderError.notImplemented
            }
            throw Self.isRefusedServerSideOperation(error) ? CloudProviderError.notImplemented : error
        }
    }

    /// True once this server has shown it has no `copy-data` extension.
    private var serverRefusesCopyData = false

    /// The server having no server-side copy at all, as opposed to this
    /// particular copy failing. OpenSSH says so in the client, before it
    /// ever reaches the server.
    static func isUnsupportedCopy(_ error: Error) -> Bool {
        let text = message(from: error).lowercased()
        return text.contains("copy-data")
            || text.contains("does not support")
            || text.contains("unknown command")
            || text.contains("invalid command")
    }

    /// A server-side operation the server declined, where copying the bytes
    /// through the client would still work: a rename across filesystems is
    /// the usual one, and the reason the fallback has to stay.
    static func isRefusedServerSideOperation(_ error: Error) -> Bool {
        if case CloudProviderError.notImplemented = error { return true }
        let text = message(from: error).lowercased()
        return text.contains("cross-device")
            || text.contains("invalid cross-device link")
            || text.contains("operation unsupported")
            || text.contains("op unsupported")
            || isUnsupportedCopy(error)
    }

    private static func message(from error: Error) -> String {
        if case CloudProviderError.commandFailed(let text) = error { return text }
        if case CloudProviderError.notFound(let text) = error { return text }
        return error.localizedDescription
    }

    public func getFileInfo(at path: String) async throws -> CloudFileItem {
        let parentPath = (path as NSString).deletingLastPathComponent
        let fileName = (path as NSString).lastPathComponent
        let output = try await runListing(path: parentPath)
        let items = try parseListing(output: output, basePath: parentPath)
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
        try Task.checkCancellation()
        guard let controlDirectory else { throw CloudProviderError.invalidResponse }
        let directory = try SFTPTemporaryDirectory()
        let batchContent = commands.joined(separator: "\n") + "\nbye\n"
        let batchPath = directory.url.appendingPathComponent("batch").path
        guard FileManager.default.createFile(
            atPath: batchPath,
            contents: Data(batchContent.utf8),
            attributes: [.posixPermissions: 0o600]
        ) else {
            throw CloudProviderError.invalidResponse
        }

        let destination = try Self.destination(for: credentials)
        var args = [
            // Keep authentication and trust on this account's explicit settings.
            "-F", "/dev/null",
            "-oStrictHostKeyChecking=yes",
            "-oUpdateHostKeys=no",
            "-oForwardAgent=no",
            "-oAddKeysToAgent=no",
            "-oControlMaster=auto",
            "-oControlPath=\(controlDirectory.url.appendingPathComponent("control").path)",
            "-oControlPersist=600",
            "-oConnectTimeout=10",
            "-oServerAliveInterval=15",
            "-oServerAliveCountMax=4",
            "-P", "\(credentials.port)",
        ]
        if let knownHostsFile {
            args += ["-oUserKnownHostsFile=\(knownHostsFile.path)", "-oGlobalKnownHostsFile=none"]
        }

        var environment = ["LC_ALL": "C", "SSH_ASKPASS_REQUIRE": "never"]
        var executable = "/usr/bin/sftp"
        var secret: String?
        var keyData: Data?
        switch credentials.authMethod {
        case .password:
            guard let password = credentials.password, !password.isEmpty else {
                throw CloudProviderError.notAuthenticated
            }
            secret = password
            args += [
                "-oBatchMode=no",
                "-oNumberOfPasswordPrompts=1",
                "-oIdentityAgent=none",
                "-oPubkeyAuthentication=no",
                "-oPreferredAuthentications=password,keyboard-interactive",
            ]
        case .privateKey:
            guard let key = credentials.privateKey, !key.isEmpty else {
                throw CloudProviderError.notAuthenticated
            }
            keyData = Data((key.hasSuffix("\n") ? key : key + "\n").utf8)
            if let passphrase = credentials.passphrase, !passphrase.isEmpty {
                secret = passphrase
            } else {
                // A key without a saved passphrase must fail without prompting.
                environment["SSH_ASKPASS"] = "/usr/bin/false"
                environment["SSH_ASKPASS_REQUIRE"] = "force"
            }
            executable = "/usr/bin/ssh-agent"
            args += Self.agentAuthenticationOptions
        case .sshAgent:
            guard let socket = ProcessInfo.processInfo.environment["SSH_AUTH_SOCK"], !socket.isEmpty else {
                throw CloudProviderError.commandFailed(L10n.text("No SSH agent was found. Start an SSH agent and make its socket available to FileFluss."))
            }
            environment["SSH_AUTH_SOCK"] = socket
            args += Self.agentAuthenticationOptions
        }

        let askPass: SFTPAskPass?
        if let secret {
            askPass = try SFTPAskPass(secret: secret, directory: directory)
            environment.merge(try SFTPAskPass.environment(directory: directory)) { _, new in new }
        } else {
            askPass = nil
        }
        defer {
            withExtendedLifetime(directory) {}
            withExtendedLifetime(askPass) {}
        }

        args += ["-b", batchPath, "--", destination]
        if keyData != nil {
            // ssh-agent runs the command with its own socket and exits when
            // the command exits. ssh-add reads the key directly from stdin.
            args = [
                "-a", directory.url.appendingPathComponent("agent.sock").path,
                "-t", "60",
                "/bin/sh", "-c", """
                /usr/bin/ssh-add -q -k - 2>/dev/null || exit 42
                unset SSH_ASKPASS FILEFLUSS_ASKPASS_SOCKET
                SSH_ASKPASS_REQUIRE=never
                exec /usr/bin/sftp "$@"
                """, "filefluss-sftp",
            ] + args
        }

        let cancellation = ProcessCancellation()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                let process = Process()
                process.executableURL = URL(fileURLWithPath: executable)
                process.arguments = args
                process.environment = environment

                let stdoutPipe = Pipe()
                let stderrPipe = Pipe()
                let keyPipe = keyData.map { _ in Pipe() }
                process.standardOutput = stdoutPipe
                process.standardError = stderrPipe
                process.standardInput = keyPipe as Any? ?? FileHandle.nullDevice

                let stdoutDrain = PipeDrain(stdoutPipe)
                let stderrDrain = PipeDrain(stderrPipe)

                process.terminationHandler = { proc in
                    let stdoutData = stdoutDrain.collected()
                    let stderrData = stderrDrain.collected()
                    let stdout = String(data: stdoutData, encoding: .utf8) ?? ""
                    let stderr = String(data: stderrData, encoding: .utf8) ?? ""

                    // Diagnostic capture for the recurring "SFTP folder appears
                    // empty" reports (issue #31). Routed through SupportLogger so
                    // it lands in the user-exportable Support Log on notarized
                    // builds — the os.Logger output never reached that export,
                    // which is why earlier support logs came back empty. No secret
                    // is logged: the askpass response goes straight to SSH.
                    // stdout is the raw `ls` listing whose exact format is what
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
                    if let writer = keyPipe?.fileHandleForWriting,
                       fcntl(writer.fileDescriptor, F_SETNOSIGPIPE, 1) < 0 {
                        throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
                    }
                    try process.run()
                    cancellation.started(process)
                    if let keyData, let writer = keyPipe?.fileHandleForWriting {
                        DispatchQueue.global(qos: .utility).async {
                            defer { try? writer.close() }
                            try? writer.write(contentsOf: keyData)
                        }
                    }
                } catch {
                    // Never started, so nothing will close these; the drains
                    // would otherwise wait on them forever.
                    try? stdoutPipe.fileHandleForWriting.close()
                    try? stderrPipe.fileHandleForWriting.close()
                    continuation.resume(throwing: CloudProviderError.networkError(error))
                }
            }
        } onCancel: {
            cancellation.cancel()
        }
    }

    private static let agentAuthenticationOptions = [
        "-oBatchMode=yes",
        "-oPreferredAuthentications=publickey",
        "-oIdentityAgent=SSH_AUTH_SOCK",
        "-oIdentityFile=none",
        "-oIdentitiesOnly=no",
    ]

    static func destination(for credentials: SFTPCredentials) throws -> String {
        let host = credentials.host
        let username = credentials.username
        let invalid = CharacterSet.whitespacesAndNewlines.union(.controlCharacters)
        guard (1...65535).contains(credentials.port),
              !host.isEmpty, !host.hasPrefix("-"), !host.contains("/"), !host.contains("@"),
              !username.isEmpty, !username.hasPrefix("-"), !username.contains("/"), !username.contains("@"),
              host.rangeOfCharacter(from: invalid) == nil,
              username.rangeOfCharacter(from: invalid) == nil else {
            throw CloudProviderError.commandFailed(L10n.text("Enter a valid SSH hostname, port, and username."))
        }
        let address = host.hasPrefix("[") && host.hasSuffix("]") ? String(host.dropFirst().dropLast()) : host
        guard !address.isEmpty, !address.contains("["), !address.contains("]") else {
            throw CloudProviderError.commandFailed(L10n.text("Enter a valid SSH hostname, port, and username."))
        }
        let sftpHost = address.contains(":") ? "[\(address)]" : address
        return "\(username)@\(sftpHost)"
    }

    /// Translate an `sftp`/`ssh` failure into a useful CloudProviderError.
    /// We pattern-match the stderr lines openssh actually emits — the exit
    /// code on its own (255 for almost everything ssh-related) is too
    /// coarse to be useful, so the messages drive the dispatch.
    static func mapSFTPFailure(exit: Int, stderr: String, combined: String, authMethod: SFTPCredentials.AuthMethod) -> CloudProviderError {
        let lower = combined.lowercased()

        if exit == 42, authMethod == .privateKey {
            return .commandFailed(L10n.text("OpenSSH could not load the private key. Check its format and passphrase."))
        }
        if lower.contains("remote host identification has changed") || lower.contains("revoked host key") {
            return .commandFailed(L10n.text("The SSH host key changed or was revoked. Verify the server key with your administrator before updating known_hosts."))
        }
        if lower.contains("host key verification failed") {
            return .commandFailed(L10n.text("The SSH host key could not be verified. Verify the server key with your administrator and add the verified key to ~/.ssh/known_hosts."))
        }

        // Auth failures — distinguish password vs key vs passphrase so
        // the user knows which thing to fix.
        if authMethod == .sshAgent, lower.contains("agent refused operation") {
            return .commandFailed(L10n.text("SSH agent authentication failed. Check that the agent is unlocked and holds a key authorized by the server."))
        }
        if authMethod == .privateKey, lower.contains("permission denied (publickey)") {
            return .commandFailed(L10n.text("SSH key was rejected by the server. Make sure the public key is in the server's ~/.ssh/authorized_keys (or added to the provider's SSH-keys panel for managed services like Hetzner Storage Box)."))
        }
        if lower.contains("permission denied") || lower.contains("authentication failed") {
            switch authMethod {
            case .password:
                return .commandFailed(L10n.text("SSH authentication failed. Check the username and password."))
            case .privateKey:
                return .commandFailed(L10n.text("SSH authentication failed. The server may not have your public key authorized, or the passphrase is wrong."))
            case .sshAgent:
                return .commandFailed(L10n.text("SSH agent authentication failed. Check that the agent is unlocked and holds a key authorized by the server."))
            }
        }

        // Key-loading problems on the client side.
        if lower.contains("invalid format") || lower.contains("error in libcrypto") {
            return .commandFailed(L10n.text("Private key is in an unsupported format. Convert it to OpenSSH format (e.g. with `puttygen key.ppk -O private-openssh -o id_rsa`)."))
        }
        if lower.contains("are too open") || lower.contains("bad permissions") {
            return .commandFailed(L10n.text("Private key file has unsafe permissions. Run `chmod 600 <keyfile>` and try again."))
        }
        if lower.contains("incorrect passphrase") || lower.contains("bad passphrase") || lower.contains("could not decrypt") {
            return .commandFailed(L10n.text("Wrong passphrase for the private key."))
        }

        // Connection-level issues.
        if lower.contains("connection refused") {
            return .commandFailed(L10n.text("Connection refused — is the SSH daemon running on this host and port?"))
        }
        if lower.contains("connection timed out") || lower.contains("operation timed out") {
            return .commandFailed(L10n.text("Connection timed out. Check the hostname, port, and your network."))
        }
        if lower.contains("no route to host") || lower.contains("network is unreachable") {
            return .commandFailed(L10n.text("Network is unreachable from this Mac."))
        }
        if lower.contains("could not resolve hostname") || lower.contains("name or service not known") {
            return .commandFailed(L10n.text("Could not resolve hostname. Check spelling or DNS."))
        }

        // SFTP-level errors during a command (mkdir/put/get) — surface the
        // first useful line so the user sees what actually failed.
        if lower.contains("no such file") || lower.contains("not found") {
            return .notFound(stderr.isEmpty ? combined : stderr)
        }
        if lower.contains("couldn't") || lower.contains("failure") {
            // e.g. "Couldn't create directory: Permission denied"
            return .commandFailed(combined.isEmpty ? L10n.format("SFTP command failed (exit %d).", exit) : combined)
        }

        // Fall-through: surface the raw stderr if we have it, otherwise
        // the exit code. Truncate aggressively — SFTP can spew banners.
        if !stderr.isEmpty {
            let snippet = stderr.split(separator: "\n").prefix(3).joined(separator: " — ")
            return .commandFailed(L10n.format("SFTP failed (exit %d): %@", exit, snippet))
        }
        return .commandFailed(L10n.format("SFTP failed with exit code %d. Check the server is reachable and the credentials are correct.", exit))
    }

    // MARK: - Parsing

    private func parseListing(output: String, basePath: String) throws -> [CloudFileItem] {
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

        try Self.validateListing(output: output, itemCount: items.count, everyLineIsARow: false)
        return items
    }

    /// Heuristic for "this output contains at least one line that looks
    /// like a long-form `ls -la` row" — used to distinguish a genuinely
    /// empty directory (no listing lines) from a parser miss.
    public static func outputLooksLikeListing(_ output: String) -> Bool {
        output.components(separatedBy: "\n").contains {
            isListingRow($0.trimmingCharacters(in: .whitespaces))
        }
    }

    /// Cheap structural check: leading filetype char + at least a few perms
    /// chars. Avoids false positives on prompt lines.
    private static func isListingRow(_ line: String) -> Bool {
        guard let first = line.first, "-dlcbps".contains(first), line.count >= 10 else { return false }
        return line.prefix(10).dropFirst().allSatisfy { "rwxsStT@+.\\*-".contains($0) }
    }

    /// Throws when a listing holds rows that weren't understood, so that a
    /// folder is never reported as emptier than it is.
    ///
    /// Only files, folders and links count. Device rows put `major, minor`
    /// where the size goes and don't parse, but nothing here can copy them.
    ///
    /// `everyLineIsARow` is for FTP, where the output is the listing and
    /// nothing else: a Windows-style server's rows don't look like `ls -l` at
    /// all, so with no parsed rows, any line left over means an unknown format.
    /// `sftp` also prints its own prompt lines, so it can't make that claim.
    ///
    /// `alsoUnderstood` names rows the caller parsed another way — FTP's
    /// Windows-style rows — so they don't count as unexplained.
    public static func validateListing(
        output: String,
        itemCount: Int,
        everyLineIsARow: Bool,
        alsoUnderstood: (String) -> Bool = { _ in false }
    ) throws {
        var unexplainedLine = false
        for raw in output.components(separatedBy: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty, parseListingLine(line) == nil, !alsoUnderstood(line) else { continue }
            let isSelfOrParent = line.hasSuffix(" .") || line.hasSuffix(" ..")
                || line.hasSuffix("/.") || line.hasSuffix("/..")
            if isListingRow(line), "-dl".contains(line.first ?? " "), !isSelfOrParent {
                throw CloudProviderError.unreadableListing
            }
            if !isListingRow(line), !line.lowercased().hasPrefix("total ") {
                unexplainedLine = true
            }
        }
        if everyLineIsARow, itemCount == 0, unexplainedLine {
            throw CloudProviderError.unreadableListing
        }
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

    /// The month abbreviations `monthNumber` knows, for the patterns below.
    private static let monthAlternatives = "Jan|Feb|Mar|Mär|Mrz|Apr|May|Mai|Jun|Jul|Aug|Sept|Sep|Oct|Okt|Nov|Dec|Dez"

    /// Fallbacks for servers that leave fields out of the row. RFC 959
    /// doesn't fix the format of `LIST`, and small servers trim it: no group
    /// column (issue #70 — OpenHASP's FTP server on an ESP32 board sends
    /// `-rw-rw-r--  1  UserName  775  Nov 21 12:41  config.json`), no owner
    /// either, or no link count. These patterns anchor on what every such
    /// row has — the permissions first, the size right before the date, the
    /// name after it — and pass over up to three fields in between, as few
    /// as fit. The day may come before the month (`21 Nov`). Tried only
    /// after the strict patterns, so a row that parses today parses the same.
    private static let flexibleListingRegex = try? NSRegularExpression(
        pattern: "^(\(permsClass))\\s+(?:\\S+\\s+){0,3}?(\\d+)\\s+"
            + "(?:(\(monthAlternatives))\\.?\\s+(\\d{1,2})|(\\d{1,2})\\.?\\s+(\(monthAlternatives))\\.?)"
            + "\\s+(\\d{1,2}:\\d{2}(?::\\d{2})?|\\d{4})\\s+(.+)$",
        options: [.caseInsensitive]
    )
    private static let flexibleISOListingRegex = try? NSRegularExpression(
        pattern: "^(\(permsClass))\\s+(?:\\S+\\s+){0,3}?(\\d+)\\s+(\\d{4}-\\d{2}-\\d{2})(?:\\s+(\\d{2}:\\d{2}(?::\\d{2})?))?\\s+(.+)$"
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

        return parseTrimmedListingLine(trimmed, range: range)
    }

    /// The fallbacks for rows with fields left out (see
    /// `flexibleListingRegex`).
    private static func parseTrimmedListingLine(_ trimmed: String, range: NSRange) -> ParsedListingLine? {
        func text(_ match: NSTextCheckingResult, _ group: Int) -> String? {
            Range(match.range(at: group), in: trimmed).map { String(trimmed[$0]) }
        }

        if let regex = Self.flexibleListingRegex,
           let match = regex.firstMatch(in: trimmed, range: range),
           let perms = text(match, 1),
           let sizeText = text(match, 2),
           let month = text(match, 3) ?? text(match, 6),
           let day = text(match, 4) ?? text(match, 5),
           let timeOrYear = text(match, 7),
           let rawName = text(match, 8),
           let name = Self.cleanupListingName(rawName: rawName, perms: perms) {
            return ParsedListingLine(
                perms: perms,
                size: Int64(sizeText) ?? 0,
                isDirectory: perms.first == "d",
                modDate: Self.parseDate(month: month, day: day, timeOrYear: timeOrYear),
                name: name
            )
        }

        if let regex = Self.flexibleISOListingRegex,
           let match = regex.firstMatch(in: trimmed, range: range),
           let perms = text(match, 1),
           let sizeText = text(match, 2),
           let date = text(match, 3),
           let rawName = text(match, 5),
           let name = Self.cleanupListingName(rawName: rawName, perms: perms) {
            return ParsedListingLine(
                perms: perms,
                size: Int64(sizeText) ?? 0,
                isDirectory: perms.first == "d",
                modDate: Self.parseISODate(date: date, time: text(match, 4)),
                name: name
            )
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

        guard let date = calendar.date(from: components) else { return .distantPast }
        // `ls` omits the year for the last six months, so a December file
        // listed in January comes out in the future until it's moved back.
        if timeOrYear.contains(":"), date.timeIntervalSinceNow > 86_400,
           let lastYear = calendar.date(byAdding: .year, value: -1, to: date) {
            return lastYear
        }
        return date
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
        // Any case: a few small servers write "NOV" or "nov".
        switch token.prefix(1).uppercased() + token.dropFirst().lowercased() {
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

    /// The command sent for a server-side move. Pure, so the quoting can
    /// be tested: these strings are handed to another program, and a path
    /// with a quote or a `$` in it is the case nobody tries by hand.
    static func renameCommand(from path: String, to newPath: String) -> String {
        "rename \(Self.shellEscape(path)) \(Self.shellEscape(newPath))"
    }

    /// The command sent for a server-side copy.
    static func copyCommand(from path: String, to newPath: String) -> String {
        "cp \(Self.shellEscape(path)) \(Self.shellEscape(newPath))"
    }

    static func shellEscape(_ path: String) -> String {
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
