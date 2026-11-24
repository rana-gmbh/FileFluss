import Testing
import Foundation
@testable import FileFlussCore

/// Host keys, secrets and batch commands of the SFTP client.
@Suite("SFTP security")
struct SFTPSecurityTests {

    // MARK: Batch commands

    /// Each command is one line of the batch file. A line break in a name
    /// would end the command there and make the rest of the name a command
    /// of its own, and a line starting with `!` runs on the Mac.
    @Test("A name with a line break is refused before sftp runs")
    func lineBreakIsRefused() async {
        #expect(SFTPAPIClient.containsLineBreak("ls \"/a\nb\""))
        #expect(SFTPAPIClient.containsLineBreak("ls \"/a\rb\""))
        #expect(!SFTPAPIClient.containsLineBreak("ls \"/a b\u{2028}\""))

        let client = SFTPAPIClient(credentials: SFTPCredentials(
            host: "127.0.0.1", port: 1, username: "nobody",
            authMethod: .password, password: "x",
            hostKey: SFTPHostKey(keys: ["ssh-ed25519 AAAA"], fingerprint: "SHA256:x")
        ))
        await #expect {
            _ = try await client.listFolder(path: "/x\n!touch PWNED")
        } throws: { error in
            guard case CloudProviderError.commandFailed(let message) = error else { return false }
            return message == L10n.text("SFTP can't work with a name that contains a line break.")
        }
    }

    // MARK: Host keys

    @Test("Keys are read from known_hosts lines, hashed or not")
    func readsKnownHosts() {
        let text = """
            # Host [nas.local]:2222 found: line 4
            |1|abc=|def= ssh-ed25519 AAAAkey1
            [nas.local]:2222 ecdsa-sha2-nistp256 AAAAkey2 comment
            @revoked [nas.local]:2222 ssh-rsa AAAArevoked
            @cert-authority *.local ssh-rsa AAAAca
            |1|ghi=|jkl= ssh-ed25519 AAAAkey1
            """
        #expect(SFTPHostKeys.keys(inKnownHosts: text) == [
            "ssh-ed25519 AAAAkey1",
            "ecdsa-sha2-nistp256 AAAAkey2",
        ])
    }

    @Test("The fingerprint is taken from ssh-keygen -l")
    func readsFingerprint() {
        let output = "256 SHA256:xfogEK7eLUls+Yv1ZgT65DuCFvvtqWxL2zajV/kyJIY filefluss-sftp (ED25519)\n"
        #expect(SFTPHostKeys.fingerprint(inKeygenOutput: output) == "SHA256:xfogEK7eLUls+Yv1ZgT65DuCFvvtqWxL2zajV/kyJIY")
        #expect(SFTPHostKeys.fingerprint(inKeygenOutput: "") == nil)
    }

    @Test("Servers are looked up in known_hosts as OpenSSH writes them")
    func knownHostsNames() {
        #expect(SFTPHostKeys.knownHostsName(host: "nas.local", port: 22) == "nas.local")
        #expect(SFTPHostKeys.knownHostsName(host: "nas.local", port: 2222) == "[nas.local]:2222")
        #expect(SFTPHostKeys.knownHostsText(SFTPHostKey(keys: ["ssh-ed25519 AAAA"], fingerprint: "f"))
                == "filefluss-sftp ssh-ed25519 AAAA\n")
    }

    @Test("Accounts stored before keys were pinned still load, and the key is kept")
    func credentialsCarryTheHostKey() throws {
        let legacy = #"{"host":"nas.local","port":22,"username":"me","password":"pw"}"#
        let old = try JSONDecoder().decode(SFTPCredentials.self, from: Data(legacy.utf8))
        #expect(old.hostKey == nil)
        #expect(old.authMethod == .password)

        let key = SFTPHostKey(keys: ["ssh-ed25519 AAAA"], fingerprint: "SHA256:abc")
        let pinned = old.with(hostKey: key)
        let decoded = try JSONDecoder().decode(SFTPCredentials.self, from: JSONEncoder().encode(pinned))
        #expect(decoded.hostKey == key)
        #expect(decoded.password == "pw")
    }

    // MARK: Secrets

    /// The password and the private key used to sit in the temp directory
    /// for as long as the connection lived, and after a crash for as long as
    /// the directory did.
    @Test("No password or private key is written to disk")
    func secretsStayOffDisk() throws {
        let password = "pw-\(UUID().uuidString)"
        let keyMarker = "KEY-\(UUID().uuidString)"
        let passphrase = "pp-\(UUID().uuidString)"
        let clients = [
            SFTPAPIClient(credentials: SFTPCredentials(host: "h", port: 22, username: "u", authMethod: .password, password: password)),
            SFTPAPIClient(credentials: SFTPCredentials(
                host: "h", port: 22, username: "u", authMethod: .privateKey,
                privateKey: "-----BEGIN OPENSSH PRIVATE KEY-----\n\(keyMarker)\n-----END OPENSSH PRIVATE KEY-----\n",
                passphrase: passphrase
            )),
        ]
        defer { withExtendedLifetime(clients) {} }

        let temp = URL(fileURLWithPath: NSTemporaryDirectory())
        let entries = (try? FileManager.default.contentsOfDirectory(at: temp, includingPropertiesForKeys: nil)) ?? []
        var files: [URL] = []
        for entry in entries where entry.lastPathComponent.contains("sftp") {
            files.append(entry)
            let inside = FileManager.default.enumerator(at: entry, includingPropertiesForKeys: nil)
            while let url = inside?.nextObject() as? URL { files.append(url) }
        }
        #expect(!files.isEmpty)
        for url in files {
            guard (try? url.resourceValues(forKeys: [.isRegularFileKey]))?.isRegularFile == true,
                  let text = try? String(contentsOf: url, encoding: .utf8) else { continue }
            #expect(!text.contains(password), "password found in \(url.path)")
            #expect(!text.contains(keyMarker), "private key found in \(url.path)")
            #expect(!text.contains(passphrase), "passphrase found in \(url.path)")
        }
    }

    @Test("The askpass helper fetches the secret from the socket, once")
    func askPassServesTheSecret() async throws {
        let dir = try SFTPHostKeys.makePrivateDirectory(prefix: "ff-test")
        defer { try? FileManager.default.removeItem(atPath: dir) }
        let server = try SFTPSecretServer(secret: "s3cret pass", socketPath: dir + "/s")
        defer { withExtendedLifetime(server) {} }
        let helper = dir + "/askpass"
        FileManager.default.createFile(atPath: helper, contents: Data(SFTPSecretServer.askPassScript.utf8), attributes: [.posixPermissions: 0o700])

        let env = ["PATH": "/usr/bin:/bin", "FILEFLUSS_ASKPASS_SOCKET": server.socketPath]
        let first = await ProcessRunner.run(helper, ["Enter passphrase for (stdin): "], environment: env)
        #expect(first.status == 0)
        #expect(first.stdout == "s3cret pass\n")

        // A retry means the secret was wrong; offering it again would loop.
        let retry = await ProcessRunner.run(helper, ["Bad passphrase, try again for (stdin): "], environment: env)
        #expect(retry.status != 0)
        #expect(retry.stdout.isEmpty)
    }
}

/// Against a real `sshd`, started by hand: a user-run sshd on 127.0.0.1
/// with public-key login (a non-root sshd can't check passwords). Set
/// `FILEFLUSS_TEST_SSHD_DIR` to a folder holding `hostkey.pub`, `userkey`
/// (unencrypted) and `userkey_enc` (passphrase "secret pass"), both
/// authorized, and `FILEFLUSS_TEST_SSHD_PORT` to its port.
@Suite("SFTP against a local sshd", .enabled(if: LocalSSHD.isConfigured))
struct SFTPLocalServerTests {
    let server = LocalSSHD()

    @Test("The probe learns the server's key without signing in")
    func probeLearnsTheKey() async throws {
        let key = try await SFTPHostKeys.probe(host: "127.0.0.1", port: server.port, username: NSUserName())
        #expect(key.fingerprint == server.fingerprint)
    }

    @Test("A pinned key and an imported private key connect, without the key on disk")
    func connectsWithAgentLoadedKey() async throws {
        let key = try await SFTPHostKeys.probe(host: "127.0.0.1", port: server.port, username: NSUserName())
        let credentials = try await SFTPAPIClient.authenticate(
            host: "127.0.0.1", port: server.port, username: NSUserName(),
            privateKey: server.privateKey, remotePath: server.directory, hostKey: key
        )
        let client = SFTPAPIClient(credentials: credentials)
        // The second listing rides on the master connection the first one left.
        _ = try await client.listFolder(path: server.directory)
        let items = try await client.listFolder(path: server.directory)
        #expect(items.contains { $0.name == "hostkey.pub" })
    }

    /// Later commands skip loading the key while the master connection is
    /// up. When it has gone, the command signs in again with the key.
    @Test("A command after the master connection ended signs in again")
    func reconnectsAfterMasterEnded() async throws {
        let key = try await SFTPHostKeys.probe(host: "127.0.0.1", port: server.port, username: NSUserName())
        let credentials = try await SFTPAPIClient.authenticate(
            host: "127.0.0.1", port: server.port, username: NSUserName(),
            privateKey: server.privateKey, remotePath: server.directory, hostKey: key
        )
        let client = SFTPAPIClient(credentials: credentials)
        _ = try await client.listFolder(path: server.directory)

        let controlPath = client.controlPath
        _ = await ProcessRunner.run("/usr/bin/ssh", ["-O", "exit", "-oControlPath=\(controlPath)", "x"], environment: SFTPAPIClient.baseEnvironment)
        // A master killed outright leaves its socket behind.
        if !FileManager.default.fileExists(atPath: controlPath) {
            FileManager.default.createFile(atPath: controlPath, contents: Data())
        }

        let items = try await client.listFolder(path: server.directory)
        #expect(items.contains { $0.name == "hostkey.pub" })
    }

    @Test("An encrypted key's passphrase arrives through the askpass socket")
    func encryptedKey() async throws {
        let key = try await SFTPHostKeys.probe(host: "127.0.0.1", port: server.port, username: NSUserName())
        _ = try await SFTPAPIClient.authenticate(
            host: "127.0.0.1", port: server.port, username: NSUserName(),
            privateKey: server.encryptedPrivateKey, passphrase: "secret pass",
            remotePath: server.directory, hostKey: key
        )
    }

    @Test("A wrong passphrase fails once, with its own message")
    func wrongPassphrase() async throws {
        let key = try await SFTPHostKeys.probe(host: "127.0.0.1", port: server.port, username: NSUserName())
        await #expect {
            _ = try await SFTPAPIClient.authenticate(
                host: "127.0.0.1", port: server.port, username: NSUserName(),
                privateKey: server.encryptedPrivateKey, passphrase: "wrong",
                remotePath: server.directory, hostKey: key
            )
        } throws: { error in
            guard case CloudProviderError.commandFailed(let message) = error else { return false }
            return message == L10n.text("Wrong passphrase for the private key.")
        }
    }

    @Test("A server presenting another key is refused")
    func otherKeyIsRefused() async throws {
        let wrong = SFTPHostKey(
            keys: ["ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl"],
            fingerprint: "SHA256:wrong"
        )
        await #expect(throws: SFTPHostKeyMismatch.self) {
            _ = try await SFTPAPIClient.authenticate(
                host: "127.0.0.1", port: server.port, username: NSUserName(),
                privateKey: server.privateKey, remotePath: server.directory, hostKey: wrong
            )
        }
    }

    @Test("An account stored before keys were pinned takes over the server's key")
    func olderAccountPinsOnFirstUse() async throws {
        let pinned = PinnedBox()
        let client = SFTPAPIClient(
            credentials: SFTPCredentials(
                host: "127.0.0.1", port: server.port, username: NSUserName(),
                authMethod: .privateKey, privateKey: server.privateKey, remotePath: server.directory
            ),
            onHostKeyPinned: { pinned.set($0) }
        )
        _ = try await client.listFolder(path: server.directory)
        #expect(pinned.value?.hostKey?.fingerprint == server.fingerprint)
    }

    @Test("Names with $ and a backtick survive upload and download")
    func awkwardNamesRoundTrip() async throws {
        let key = try await SFTPHostKeys.probe(host: "127.0.0.1", port: server.port, username: NSUserName())
        let credentials = try await SFTPAPIClient.authenticate(
            host: "127.0.0.1", port: server.port, username: NSUserName(),
            privateKey: server.privateKey, remotePath: server.directory, hostKey: key
        )
        let client = SFTPAPIClient(credentials: credentials)
        let local = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("ff-up-\(UUID().uuidString.prefix(6))")
        try FileManager.default.createDirectory(at: local, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: local) }

        let name = "Price $5 `x`.txt"
        let source = local.appendingPathComponent(name)
        try Data("five".utf8).write(to: source)
        let remote = server.directory + "/remote-\(UUID().uuidString.prefix(6))"
        try await client.createFolder(at: remote)
        try await client.uploadFile(from: source, to: remote + "/" + name)

        let names = try FileManager.default.contentsOfDirectory(atPath: remote)
        #expect(names == [name])

        let back = local.appendingPathComponent("back " + name)
        try await client.downloadFile(remotePath: remote + "/" + name, to: back)
        #expect(try String(contentsOf: back, encoding: .utf8) == "five")
        try? FileManager.default.removeItem(atPath: remote)
    }
}

private final class PinnedBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: SFTPCredentials?
    func set(_ value: SFTPCredentials) { lock.lock(); stored = value; lock.unlock() }
    var value: SFTPCredentials? { lock.lock(); defer { lock.unlock() }; return stored }
}

struct LocalSSHD {
    static var isConfigured: Bool {
        ProcessInfo.processInfo.environment["FILEFLUSS_TEST_SSHD_DIR"] != nil
            && ProcessInfo.processInfo.environment["FILEFLUSS_TEST_SSHD_PORT"] != nil
    }

    let directory: String
    let port: Int

    init() {
        let env = ProcessInfo.processInfo.environment
        directory = env["FILEFLUSS_TEST_SSHD_DIR"] ?? ""
        port = Int(env["FILEFLUSS_TEST_SSHD_PORT"] ?? "") ?? 22
    }

    var privateKey: String { (try? String(contentsOfFile: directory + "/userkey", encoding: .utf8)) ?? "" }
    var encryptedPrivateKey: String { (try? String(contentsOfFile: directory + "/userkey_enc", encoding: .utf8)) ?? "" }

    var fingerprint: String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/ssh-keygen")
        process.arguments = ["-l", "-f", directory + "/hostkey.pub"]
        let pipe = Pipe()
        process.standardOutput = pipe
        try? process.run()
        process.waitUntilExit()
        let output = String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        return SFTPHostKeys.fingerprint(inKeygenOutput: output) ?? ""
    }
}
