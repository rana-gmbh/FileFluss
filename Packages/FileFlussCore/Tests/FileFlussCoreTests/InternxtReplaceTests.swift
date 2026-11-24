import Testing
import Foundation
@testable import FileFlussCore

/// Replacing a file on Internxt: Drive won't take a second entry under a
/// name that exists, so the new one is registered under a temporary name,
/// the old one goes, and the new one takes the name. Deleting the old entry
/// first lost it whenever the registration then failed.
///
/// Internxt's host is fixed, so these share one stub host and run one by one.
@Suite("Internxt replace", .serialized)
struct InternxtReplaceTests {
    private static let host = "gateway.internxt.com"

    private static let credentials = InternxtCredentials(
        email: "test@example.invalid",
        token: "token",
        mnemonic: "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
        bucket: "0123456789abcdef01234567",
        rootFolderUuid: "root",
        bridgeUser: "bridge",
        userId: "user"
    )

    /// Answers the listing (one file, `a.txt`, uuid "old") and the three
    /// network upload steps; everything else goes to `drive`.
    private static func client(
        drive: @escaping @Sendable (StubHTTP.Recorded) -> StubHTTP.Reply
    ) -> InternxtAPIClient {
        let session = StubHTTP.session(host: host) { request in
            let target = request.target
            if target.hasPrefix("/drive/folders/content/root/folders/") {
                return .json(#"{"folders":[]}"#)
            }
            if target.hasPrefix("/drive/folders/content/root/files/") {
                return .json(#"{"files":[{"uuid":"old","fileId":"n0","bucket":"0123456789abcdef01234567","plainName":"a","type":"txt","size":3}]}"#)
            }
            if target.hasPrefix("/network/v2/buckets/") && target.contains("/files/start") {
                return .json(#"{"uploads":[{"url":"https://gateway.internxt.com/put/shard","uuid":"shard1"}]}"#)
            }
            if target == "/put/shard" { return .json("") }
            if target.hasPrefix("/network/v2/buckets/") && target.hasSuffix("/files/finish") {
                return .json(#"{"id":"net-new"}"#)
            }
            return drive(request)
        }
        return InternxtAPIClient(credentials: credentials, session: session, retryDelayNanos: 1_000_000)
    }

    private static func localFile() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("internxt-\(UUID().uuidString).txt")
        try Data("new".utf8).write(to: url)
        return url
    }

    private static func driveRequests() -> [StubHTTP.Recorded] {
        StubHTTP.requests(host: host).filter { $0.url.path.hasPrefix("/drive/files") }
    }

    @Test("A registration that fails leaves the old file in place")
    func failedRegistrationKeepsOldFile() async throws {
        let client = Self.client { request in
            if request.method == "POST" && request.url.path == "/drive/files" {
                return .json(#"{"message":"bad"}"#, status: 400)
            }
            return .json("{}")
        }
        let file = try Self.localFile()
        defer { try? FileManager.default.removeItem(at: file) }

        await #expect(throws: CloudProviderError.self) {
            try await client.uploadFile(from: file, to: "/a.txt", onBytes: nil)
        }
        #expect(!Self.driveRequests().contains { $0.method == "DELETE" })
    }

    @Test("The replacement is registered aside, the old entry goes, then it takes the name")
    func replaceOrder() async throws {
        let metaAttempts = CallCounter()
        let client = Self.client { request in
            switch (request.method, request.url.path) {
            case ("POST", "/drive/files"):
                return .json(#"{"uuid":"new"}"#)
            case ("DELETE", "/drive/files/old"):
                return .json("{}")
            case ("PUT", "/drive/files/new/meta"):
                // Drive doesn't know the fresh entry yet the first time round.
                return metaAttempts.next() == 1 ? .json("{}", status: 404) : .json("{}")
            default:
                return .json("{}", status: 500)
            }
        }
        let file = try Self.localFile()
        defer { try? FileManager.default.removeItem(at: file) }

        try await client.uploadFile(from: file, to: "/a.txt", onBytes: nil)

        let requests = Self.driveRequests()
        let steps = requests.map { "\($0.method) \($0.url.path)" }
        #expect(steps == ["POST /drive/files", "DELETE /drive/files/old", "PUT /drive/files/new/meta", "PUT /drive/files/new/meta"])

        let registered = try #require(JSONSerialization.jsonObject(with: requests[0].body) as? [String: Any])
        let temporaryName = try #require(registered["plainName"] as? String)
        #expect(temporaryName.hasPrefix("a.fileflusspart-"))
        #expect(temporaryName.count == "a.fileflusspart-".count + 8)
        #expect(registered["type"] as? String == "txt")

        let renamed = try #require(JSONSerialization.jsonObject(with: requests[3].body) as? [String: Any])
        #expect(renamed["plainName"] as? String == "a")
    }

    @Test("When the old entry can't be deleted, the replacement is removed and the error reported")
    func undeletableOldFile() async throws {
        let client = Self.client { request in
            switch (request.method, request.url.path) {
            case ("POST", "/drive/files"): return .json(#"{"uuid":"new"}"#)
            case ("DELETE", "/drive/files/old"): return .json("{}", status: 403)
            case ("DELETE", "/drive/files/new"): return .json("{}")
            default: return .json("{}", status: 500)
            }
        }
        let file = try Self.localFile()
        defer { try? FileManager.default.removeItem(at: file) }

        await #expect(throws: CloudProviderError.self) {
            try await client.uploadFile(from: file, to: "/a.txt", onBytes: nil)
        }
        let steps = Self.driveRequests().map { "\($0.method) \($0.url.path)" }
        #expect(steps == ["POST /drive/files", "DELETE /drive/files/old", "DELETE /drive/files/new"])
    }

    @Test("A folder that can't be listed stops the upload before anything is replaced")
    func unlistableFolder() async throws {
        // A failed listing used to pass for "no file there": the upload then
        // registered under the real name, and the 409 that followed deleted
        // the old entry before the new one existed.
        let session = StubHTTP.session(host: Self.host) { request in
            if request.target.hasPrefix("/drive/folders/content/root/folders/") { return .json(#"{"folders":[]}"#) }
            if request.target.hasPrefix("/drive/folders/content/root/files/") { return .json("{}", status: 500) }
            return .json(#"{"uuid":"new","id":"net-new","uploads":[{"url":"https://gateway.internxt.com/put/shard","uuid":"s"}]}"#)
        }
        let client = InternxtAPIClient(credentials: Self.credentials, session: session, retryDelayNanos: 1_000_000)
        let file = try Self.localFile()
        defer { try? FileManager.default.removeItem(at: file) }

        await #expect(throws: CloudProviderError.self) {
            try await client.uploadFile(from: file, to: "/a.txt", onBytes: nil)
        }
        #expect(!StubHTTP.requests(host: Self.host).contains { $0.method == "POST" })
    }

    @Test("An old entry that is already gone doesn't stop the replacement")
    func oldEntryAlreadyGone() async throws {
        let client = Self.client { request in
            switch (request.method, request.url.path) {
            case ("POST", "/drive/files"): return .json(#"{"uuid":"new"}"#)
            case ("DELETE", "/drive/files/old"): return .json("{}", status: 404)
            case ("PUT", "/drive/files/new/meta"): return .json("{}")
            default: return .json("{}", status: 500)
            }
        }
        let file = try Self.localFile()
        defer { try? FileManager.default.removeItem(at: file) }

        try await client.uploadFile(from: file, to: "/a.txt", onBytes: nil)
        let steps = Self.driveRequests().map { "\($0.method) \($0.url.path)" }
        #expect(steps == ["POST /drive/files", "DELETE /drive/files/old", "PUT /drive/files/new/meta"])
    }
}
