#if os(macOS)
import Foundation
import Darwin

// MARK: - Host keys

/// What the user is asked before FileFluss trusts an SFTP server's host key.
/// The Windows app asks the same two questions.
public enum SFTPTrustQuestion: Sendable, Equatable {
    /// A server this account has no key for yet: a new account, or a new address.
    case firstConnection(host: String, fingerprint: String)
    /// The server presents a key other than the one pinned for it.
    case changed(host: String, previous: String, now: String)
}

/// The host key pinned for an SFTP account: what OpenSSH would keep in
/// `known_hosts`, kept with the account instead.
public struct SFTPHostKey: Codable, Sendable, Equatable {
    /// `"<type> <base64>"` entries, as in a known_hosts line without the host
    /// field. Usually one; an account carried over from `~/.ssh/known_hosts`
    /// keeps every key the user had trusted for that server.
    public let keys: [String]
    /// `SHA256:…` of the key the server presented, for showing to the user.
    public let fingerprint: String

    public init(keys: [String], fingerprint: String) {
        self.keys = keys
        self.fingerprint = fingerprint
    }
}

/// The server presented a host key other than the pinned one, so the
/// connection was refused before anything was sent.
public struct SFTPHostKeyMismatch: LocalizedError, Equatable {
    public init() {}
    public var errorDescription: String? {
        L10n.text("Host key verification failed — the server's host key changed. If you expected this, edit the account and connect again to accept the new key.")
    }
}

/// Host-key handling around OpenSSH: learning a server's key without
/// signing in, carrying over what `~/.ssh/known_hosts` already trusts, and
/// the known_hosts file each connection checks against.
enum SFTPHostKeys {
    /// Every connection names the server by this alias in its own
    /// known_hosts file, so the file never depends on how the host and port
    /// were typed.
    static let alias = "filefluss-sftp"

    static func knownHostsText(_ key: SFTPHostKey) -> String {
        key.keys.map { "\(alias) \($0)" }.joined(separator: "\n") + "\n"
    }

    /// How the server appears in `~/.ssh/known_hosts`.
    static func knownHostsName(host: String, port: Int) -> String {
        port == 22 ? host : "[\(host)]:\(port)"
    }

    /// The `"<type> <base64>"` keys in known_hosts text. Comments, revoked
    /// keys and certificate authorities are left out: none of them is a key
    /// the user trusted for this server.
    static func keys(inKnownHosts text: String) -> [String] {
        var result: [String] = []
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") || line.hasPrefix("@") { continue }
            let fields = line.split(separator: " ", omittingEmptySubsequences: true)
            guard fields.count >= 3 else { continue }
            let entry = "\(fields[1]) \(fields[2])"
            if !result.contains(entry) { result.append(entry) }
        }
        return result
    }

    /// The `SHA256:…` fingerprint in `ssh-keygen -l` output.
    static func fingerprint(inKeygenOutput output: String) -> String? {
        for field in output.split(whereSeparator: { $0 == " " || $0.isNewline }) where field.hasPrefix("SHA256:") {
            return String(field)
        }
        return nil
    }

    /// Learns the key the server presents, without signing in. The probe
    /// offers no authentication method, so it ends with "Permission denied"
    /// and nothing but the user name reaches the server, which matters
    /// because the user hasn't said yet whether to trust it.
    static func probe(host: String, port: Int, username: String) async throws -> SFTPHostKey {
        let dir = try makePrivateDirectory(prefix: "ff-probe")
        defer { try? FileManager.default.removeItem(atPath: dir) }
        let file = dir + "/known_hosts"
        FileManager.default.createFile(atPath: file, contents: Data())

        let result = await ProcessRunner.run("/usr/bin/ssh", [
            "-T", "-p", "\(port)",
            "-oBatchMode=yes",
            "-oStrictHostKeyChecking=accept-new",
            "-oUserKnownHostsFile=\(file)",
            "-oGlobalKnownHostsFile=/dev/null",
            "-oHostKeyAlias=\(alias)",
            "-oHashKnownHosts=no",
            "-oUpdateHostKeys=no",
            "-oCheckHostIP=no",
            "-oPreferredAuthentications=none",
            "-oControlPath=none",
            "-oConnectTimeout=10",
            "-oIdentityAgent=none",
            "\(username)@\(host)", "true",
        ], environment: SFTPAPIClient.baseEnvironment)

        let text = (try? String(contentsOfFile: file, encoding: .utf8)) ?? ""
        let keys = keys(inKnownHosts: text)
        guard !keys.isEmpty else {
            let stderr = result.stderr.trimmingCharacters(in: .whitespacesAndNewlines)
            throw SFTPAPIClient.mapFailure(exit: Int(result.status), stderr: stderr, combined: stderr, authMethod: .password)
        }
        let listing = await ProcessRunner.run("/usr/bin/ssh-keygen", ["-l", "-f", file], environment: SFTPAPIClient.baseEnvironment)
        guard let fingerprint = fingerprint(inKeygenOutput: listing.stdout) else {
            throw CloudProviderError.invalidResponse
        }
        return SFTPHostKey(keys: keys, fingerprint: fingerprint)
    }

    /// What `~/.ssh/known_hosts` trusts for this server. Earlier versions
    /// let OpenSSH record the first key there, so this is the key an
    /// existing account has been trusting all along.
    static func fromUserKnownHosts(host: String, port: Int) async -> SFTPHostKey? {
        let userFile = NSHomeDirectory() + "/.ssh/known_hosts"
        guard FileManager.default.fileExists(atPath: userFile) else { return nil }
        let found = await ProcessRunner.run(
            "/usr/bin/ssh-keygen",
            ["-F", knownHostsName(host: host, port: port), "-f", userFile],
            environment: SFTPAPIClient.baseEnvironment
        )
        let keys = keys(inKnownHosts: found.stdout)
        guard found.status == 0, !keys.isEmpty else { return nil }

        guard let dir = try? makePrivateDirectory(prefix: "ff-kh") else { return nil }
        defer { try? FileManager.default.removeItem(atPath: dir) }
        let file = dir + "/known_hosts"
        let key = SFTPHostKey(keys: keys, fingerprint: "")
        FileManager.default.createFile(atPath: file, contents: Data(knownHostsText(key).utf8))
        let listing = await ProcessRunner.run("/usr/bin/ssh-keygen", ["-l", "-f", file], environment: SFTPAPIClient.baseEnvironment)
        guard let fingerprint = fingerprint(inKeygenOutput: listing.stdout) else { return nil }
        return SFTPHostKey(keys: keys, fingerprint: fingerprint)
    }

    /// A fresh directory only this user can enter.
    static func makePrivateDirectory(prefix: String) throws -> String {
        let path = NSTemporaryDirectory() + "\(prefix)-\(UUID().uuidString.prefix(8))"
        try FileManager.default.createDirectory(
            atPath: path,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700]
        )
        return path
    }
}

// MARK: - Secrets

/// Hands the account's password or key passphrase to OpenSSH's askpass
/// helper over a Unix socket, so the secret is never written to disk. It
/// used to sit in a script in the temp directory for as long as the
/// connection lived, and after a crash for as long as the directory did.
///
/// The socket lives in a directory only this user can enter, answers only
/// processes of this user, and goes away with the client.
final class SFTPSecretServer: @unchecked Sendable {
    let socketPath: String
    private let source: DispatchSourceRead

    init(secret: String, socketPath: String) throws {
        self.socketPath = socketPath
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { throw CloudProviderError.invalidResponse }

        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        let pathBytes = Array(socketPath.utf8)
        let capacity = MemoryLayout.size(ofValue: address.sun_path)
        guard pathBytes.count < capacity else {
            close(fd)
            throw CloudProviderError.invalidResponse
        }
        withUnsafeMutableBytes(of: &address.sun_path) { buffer in
            buffer.copyBytes(from: pathBytes)
        }
        address.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)

        unlink(socketPath)
        let bound = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard bound == 0, chmod(socketPath, 0o600) == 0, listen(fd, 8) == 0 else {
            close(fd)
            unlink(socketPath)
            throw CloudProviderError.invalidResponse
        }
        _ = fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK)

        let payload = Data((secret + "\n").utf8)
        let source = DispatchSource.makeReadSource(
            fileDescriptor: fd,
            queue: DispatchQueue(label: "com.rana.FileFluss.sftp-askpass")
        )
        source.setEventHandler {
            while true {
                let client = accept(fd, nil, nil)
                if client < 0 { break }
                var noSigPipe: Int32 = 1
                setsockopt(client, SOL_SOCKET, SO_NOSIGPIPE, &noSigPipe, socklen_t(MemoryLayout<Int32>.size))
                var uid: uid_t = 0
                var gid: gid_t = 0
                if getpeereid(client, &uid, &gid) == 0, uid == getuid() {
                    payload.withUnsafeBytes { bytes in
                        _ = write(client, bytes.baseAddress, bytes.count)
                    }
                }
                close(client)
            }
        }
        source.setCancelHandler { close(fd) }
        source.resume()
        self.source = source
    }

    deinit {
        source.cancel()
        unlink(socketPath)
    }

    /// The askpass helper OpenSSH runs. It holds no secret: it fetches it
    /// from the socket. A retry prompt gets no answer, so a wrong passphrase
    /// fails once instead of being offered again and again.
    static let askPassScript = """
        #!/bin/sh
        case "$1" in *"try again"*|*"Bad passphrase"*) exit 1;; esac
        exec /usr/bin/nc -w 10 -U "$FILEFLUSS_ASKPASS_SOCKET" </dev/null

        """
}

// MARK: - Running helpers

/// Runs a short-lived helper (ssh-keygen, the host-key probe) and collects
/// what it printed.
enum ProcessRunner {
    struct Result: Sendable {
        let status: Int32
        let stdout: String
        let stderr: String
    }

    static func run(_ executable: String, _ arguments: [String], environment: [String: String]) async -> Result {
        await withCheckedContinuation { continuation in
            let process = Process()
            process.executableURL = URL(fileURLWithPath: executable)
            process.arguments = arguments
            process.environment = environment
            process.standardInput = FileHandle.nullDevice
            let stdoutPipe = Pipe()
            let stderrPipe = Pipe()
            process.standardOutput = stdoutPipe
            process.standardError = stderrPipe
            let stdoutDrain = PipeDrain(stdoutPipe)
            let stderrDrain = PipeDrain(stderrPipe)
            process.terminationHandler = { proc in
                continuation.resume(returning: Result(
                    status: proc.terminationStatus,
                    stdout: String(data: stdoutDrain.collected(), encoding: .utf8) ?? "",
                    stderr: String(data: stderrDrain.collected(), encoding: .utf8) ?? ""
                ))
            }
            do {
                try process.run()
            } catch {
                try? stdoutPipe.fileHandleForWriting.close()
                try? stderrPipe.fileHandleForWriting.close()
                continuation.resume(returning: Result(status: -1, stdout: "", stderr: error.localizedDescription))
            }
        }
    }
}
#endif

#if os(macOS)
/// The user declined to trust a server's host key, so FileFluss didn't connect.
public struct SFTPHostKeyNotTrusted: LocalizedError, Equatable {
    public init() {}
    public var errorDescription: String? {
        L10n.text("The server's host key wasn't trusted, so FileFluss didn't connect.")
    }
}
#endif
