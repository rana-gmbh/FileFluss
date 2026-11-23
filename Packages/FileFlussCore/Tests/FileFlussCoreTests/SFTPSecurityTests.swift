#if os(macOS)
import Foundation
import Testing
@testable import FileFlussCore

@Suite("SFTP authentication and host trust")
struct SFTPSecurityTests {
    @Test func legacyPasswordAccountStillDecodes() throws {
        let stored = Data(#"{"host":"example.test","port":22,"username":"user","password":"fixture"}"#.utf8)
        let credentials = try JSONDecoder().decode(SFTPCredentials.self, from: stored)
        #expect(credentials.authMethod == .password)
        #expect(credentials.password == "fixture")
        #expect(credentials.remotePath == "/")
    }

    @Test func agentAccountStoresNoSecret() throws {
        let credentials = SFTPCredentials(host: "example.test", port: 22, username: "user", authMethod: .sshAgent)
        let encoded = try JSONEncoder().encode(credentials)
        let decoded = try JSONDecoder().decode(SFTPCredentials.self, from: encoded)
        #expect(decoded.authMethod == .sshAgent)
        #expect(decoded.password == nil)
        #expect(decoded.privateKey == nil)
        #expect(decoded.passphrase == nil)
    }

    @Test func destinationKeepsIPv6AddressSeparateFromRemotePath() throws {
        let credentials = SFTPCredentials(host: "2001:db8::1", port: 2222, username: "user")
        #expect(try SFTPAPIClient.destination(for: credentials) == "user@[2001:db8::1]")
    }

    @Test(arguments: ["", "host/path", "user@host", "[host", "host\npart"])
    func rejectsMalformedDestination(host: String) {
        let credentials = SFTPCredentials(host: host, port: 22, username: "user")
        #expect(throws: CloudProviderError.self) { try SFTPAPIClient.destination(for: credentials) }
    }

    @Test func askPassUsesOneSocketResponseAndNoSecretFiles() throws {
        let secret = "-n œ'\\ value"
        let directory = try SFTPTemporaryDirectory()
        let askPass = try SFTPAskPass(secret: secret, directory: directory)
        defer { withExtendedLifetime(askPass) {} }
        let environment = try SFTPAskPass.environment(directory: directory)
        let helper = try #require(environment["SSH_ASKPASS"])
        #expect(try !String(contentsOfFile: helper, encoding: .utf8).contains(secret))
        #expect(!environment.values.contains(where: { $0.contains(secret) }))
        let permissions = try FileManager.default.attributesOfItem(atPath: directory.url.path)[.posixPermissions] as? Int
        #expect(permissions == 0o700)

        let confirmation = try runAskPass(helper, environment: environment.merging(["SSH_ASKPASS_PROMPT": "confirm"]) { _, new in new })
        #expect(confirmation.status != 0)
        #expect(confirmation.output.isEmpty)

        let answer = try runAskPass(helper, environment: environment)
        #expect(answer.status == 0)
        #expect(answer.output == Data(secret.utf8))

        let second = try runAskPass(helper, environment: environment)
        #expect(second.status != 0)
        #expect(second.output.isEmpty)
    }

    @Test(arguments: ["one\ntwo", "one\rtwo", "one\0two", String(repeating: "a", count: 1024)])
    func rejectsUnrepresentableSecrets(secret: String) throws {
        let directory = try SFTPTemporaryDirectory()
        #expect(throws: CloudProviderError.self) { try SFTPAskPass(secret: secret, directory: directory) }
    }

    @Test func hostVerificationErrorsDistinguishUnknownAndChangedKeys() {
        let unknown = SFTPAPIClient.mapSFTPFailure(
            exit: 255, stderr: "", combined: "Host key verification failed.", authMethod: .password
        )
        let changed = SFTPAPIClient.mapSFTPFailure(
            exit: 255, stderr: "", combined: "WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!", authMethod: .password
        )
        #expect(unknown.localizedDescription.contains("known_hosts"))
        #expect(changed.localizedDescription.contains("known_hosts"))
        #expect(unknown.localizedDescription != changed.localizedDescription)
    }

    @Test(.enabled(if: ProcessInfo.processInfo.environment["FILEFLUSS_TEST_SFTP_ROOT"] != nil))
    func openSSHVerifiesHostAndSupportsImportedKeysAndAgent() async throws {
        let environment = ProcessInfo.processInfo.environment
        let root = URL(fileURLWithPath: try #require(environment["FILEFLUSS_TEST_SFTP_ROOT"]), isDirectory: true)
        let port = try #require(environment["FILEFLUSS_TEST_SFTP_PORT"].flatMap(Int.init))
        let plainKey = try String(contentsOf: root.appendingPathComponent("plain"), encoding: .utf8)
        let encryptedKey = try String(contentsOf: root.appendingPathComponent("encrypted"), encoding: .utf8)

        func client(_ method: SFTPCredentials.AuthMethod, key: String? = nil, passphrase: String? = nil,
                    hosts: String = "known_hosts") -> SFTPAPIClient {
            let credentials = SFTPCredentials(
                host: "127.0.0.1", port: port, username: NSUserName(), authMethod: method,
                privateKey: key, passphrase: passphrase
            )
            return SFTPAPIClient(credentials: credentials, knownHostsFile: root.appendingPathComponent(hosts))
        }

        for session in [
            client(.privateKey, key: plainKey),
            client(.privateKey, key: encryptedKey, passphrase: "test key passphrase"),
            client(.sshAgent),
        ] {
            let listing = try await session.listFolder(path: root.path)
            #expect(listing.contains { $0.name == "sample.txt" })
        }

        do {
            _ = try await client(.privateKey, key: encryptedKey, passphrase: "wrong").listFolder(path: root.path)
            Issue.record("An encrypted key loaded with the wrong passphrase.")
        } catch {
            #expect(error.localizedDescription.contains("format and passphrase"))
        }
        for (hosts, expected) in [("unknown_hosts", "could not be verified"), ("changed_hosts", "changed or was revoked")] {
            do {
                _ = try await client(.sshAgent, hosts: hosts).listFolder(path: root.path)
                Issue.record("OpenSSH accepted a host key that did not match the trust store.")
            } catch {
                #expect(error.localizedDescription.contains(expected))
            }
        }
        #expect(try Data(contentsOf: root.appendingPathComponent("unknown_hosts")).isEmpty)
    }

    private func runAskPass(_ helper: String, environment: [String: String]) throws -> (status: Int32, output: Data) {
        let process = Process()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: helper)
        process.environment = environment
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        try process.run()
        let bytes = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return (process.terminationStatus, bytes)
    }
}
#endif
