import Testing
import Foundation
@testable import FileFlussCore

/// The Synology Drive paths that lost or misreported data: names with "+",
/// DSM's HTTP-200 error replies to a download, an expired session during an
/// upload, a CopyMove that was reported done before it was, and "Keep both"
/// copies that overwrote the file the user kept.
@Suite("Synology Drive data safety")
struct SynologyDriveDataSafetyTests {
    typealias Handler = @Sendable (StubHTTP.Recorded) -> StubHTTP.Reply

    private static func client(
        host: String,
        sid: String? = "sid1",
        copyMoveTimeout: Duration = .seconds(30 * 60),
        handler: @escaping Handler
    ) -> SynologyDriveAPIClient {
        let credentials = SynologyDriveCredentials(
            serverURL: "https://\(host):5001",
            username: "user",
            password: "p+ss&word",
            allowSelfSignedCertificate: false,
            displayName: "user@\(host)"
        )
        return SynologyDriveAPIClient(
            credentials: credentials,
            session: StubHTTP.session(host: host, handler: handler),
            sid: sid,
            copyMoveTimeout: copyMoveTimeout,
            pollInterval: .milliseconds(1)
        )
    }

    private static func call(_ request: StubHTTP.Recorded) -> String {
        "\(request.query("api") ?? "?").\(request.query("method") ?? "?")"
    }

    private static let ok = StubHTTP.Reply.json(#"{"success":true}"#)
    private static func failure(_ code: Int) -> StubHTTP.Reply {
        .json(#"{"success":false,"error":{"code":\#(code)}}"#)
    }

    private static func temporaryURL() -> URL {
        FileManager.default.temporaryDirectory.appendingPathComponent("syno-\(UUID().uuidString)")
    }

    // MARK: - Query encoding

    @Test("A + in a path reaches DSM as %2B, not as a space")
    func plusIsEncoded() async throws {
        let host = StubHTTP.uniqueHost("syno-plus")
        let client = Self.client(host: host) { _ in Self.ok }

        try await client.deleteItem(at: "/home/a+b & c.txt")

        let request = try #require(StubHTTP.requests(host: host).first)
        let raw = try #require(request.url.query(percentEncoded: true))
        #expect(raw.contains("path=%2Fhome%2Fa%2Bb%20%26%20c.txt"))
        #expect(!raw.contains("+"))
    }

    @Test("Every query value is strictly encoded, the login's password included")
    func strictEncoding() {
        let encoded = SynologyDriveAPIClient.encodeQuery([
            URLQueryItem(name: "passwd", value: "p+ss&wo=rd"),
            URLQueryItem(name: "path", value: "/Fotos ü/a,b.txt"),
        ])
        #expect(encoded == "passwd=p%2Bss%26wo%3Drd&path=%2FFotos%20%C3%BC%2Fa%2Cb.txt")
    }

    // MARK: - Download

    @Test("DSM's JSON error reply to a download is an error, not the file")
    func downloadErrorEnvelope() async throws {
        let host = StubHTTP.uniqueHost("syno-dl")
        let client = Self.client(host: host) { _ in Self.failure(408) }
        let target = Self.temporaryURL()
        defer { try? FileManager.default.removeItem(at: target) }

        await #expect {
            try await client.downloadFile(remotePath: "/home/gone.txt", to: target, onBytes: nil)
        } throws: { error in
            if case CloudProviderError.notFound = error { return true }
            return false
        }
        #expect(!FileManager.default.fileExists(atPath: target.path))
    }

    @Test("A download refused for an expired session signs in again and fetches the file")
    func downloadExpiredSession() async throws {
        let host = StubHTTP.uniqueHost("syno-dl119")
        let client = Self.client(host: host) { request in
            if request.url.path == "/webapi/auth.cgi" {
                return .json(#"{"success":true,"data":{"sid":"sid2"}}"#)
            }
            if request.query("_sid") == "sid2" {
                return StubHTTP.Reply(headers: ["Content-Type": "application/octet-stream"], body: Data("file bytes".utf8))
            }
            return Self.failure(119)
        }
        let target = Self.temporaryURL()
        defer { try? FileManager.default.removeItem(at: target) }

        try await client.downloadFile(remotePath: "/home/a.txt", to: target, onBytes: nil)

        #expect(try String(contentsOf: target, encoding: .utf8) == "file bytes")
        let paths = StubHTTP.requests(host: host).map(\.url.path)
        #expect(paths == ["/webapi/entry.cgi", "/webapi/auth.cgi", "/webapi/entry.cgi"])
    }

    @Test("A small JSON file that isn't an error envelope downloads as itself")
    func downloadJSONFile() async throws {
        let host = StubHTTP.uniqueHost("syno-json")
        let client = Self.client(host: host) { _ in .json(#"{"success":true,"note":"my own file"}"#) }
        let target = Self.temporaryURL()
        defer { try? FileManager.default.removeItem(at: target) }

        try await client.downloadFile(remotePath: "/home/data.json", to: target, onBytes: nil)
        #expect(try String(contentsOf: target, encoding: .utf8) == #"{"success":true,"note":"my own file"}"#)
    }

    // MARK: - Upload

    @Test("An upload refused for an expired session signs in again and is sent once more")
    func uploadExpiredSession() async throws {
        let host = StubHTTP.uniqueHost("syno-up")
        let client = Self.client(host: host) { request in
            if request.url.path == "/webapi/auth.cgi" {
                return .json(#"{"success":true,"data":{"sid":"sid2"}}"#)
            }
            return request.query("_sid") == "sid2" ? Self.ok : Self.failure(119)
        }
        let source = Self.temporaryURL()
        try Data("payload".utf8).write(to: source)
        defer { try? FileManager.default.removeItem(at: source) }

        try await client.uploadFile(from: source, to: "/home/a.txt", onBytes: nil)

        let uploads = StubHTTP.requests(host: host).filter { $0.query("api") == "SYNO.FileStation.Upload" }
        #expect(uploads.map { $0.query("_sid") } == ["sid1", "sid2"])
    }

    // MARK: - CopyMove

    @Test("A CopyMove task that reports an error fails")
    func copyMoveTaskError() async throws {
        let host = StubHTTP.uniqueHost("syno-cm-err")
        let client = Self.client(host: host) { request in
            switch Self.call(request) {
            case "SYNO.FileStation.CopyMove.start": return .json(#"{"success":true,"data":{"taskid":"t1"}}"#)
            case "SYNO.FileStation.CopyMove.status":
                return .json(#"{"success":true,"data":{"finished":true,"error":{"code":1000}}}"#)
            default: return Self.failure(400)
            }
        }
        await #expect(throws: CloudProviderError.self) {
            try await client.copyMove(path: "/a/x.txt", toFolderPath: "/b", removeSrc: true)
        }
    }

    @Test("A CopyMove task that never finishes fails instead of passing for done")
    func copyMoveNeverFinishes() async throws {
        let host = StubHTTP.uniqueHost("syno-cm-slow")
        let client = Self.client(host: host, copyMoveTimeout: .milliseconds(50)) { request in
            switch Self.call(request) {
            case "SYNO.FileStation.CopyMove.start": return .json(#"{"success":true,"data":{"taskid":"t1"}}"#)
            case "SYNO.FileStation.CopyMove.status": return .json(#"{"success":true,"data":{"finished":false}}"#)
            default: return Self.failure(400)
            }
        }
        await #expect(throws: CloudProviderError.self) {
            try await client.copyMove(path: "/a/x.txt", toFolderPath: "/b", removeSrc: true)
        }
    }

    // MARK: - Keep both

    /// A NAS where `/b/x.txt` exists (or not), and copy, move, rename and
    /// delete answer as `renameFails` says.
    private static func keepBothClient(host: String, landingTaken: Bool, renameFails: Bool = false) -> SynologyDriveAPIClient {
        client(host: host) { request in
            switch call(request) {
            case "SYNO.FileStation.List.getinfo":
                return landingTaken
                    ? .json(#"{"success":true,"data":{"files":[{"name":"x.txt","path":"/b/x.txt","isdir":false}]}}"#)
                    : .json(#"{"success":true,"data":{"files":[{"code":408,"path":"/b/x.txt"}]}}"#)
            case "SYNO.FileStation.CopyMove.start": return .json(#"{"success":true,"data":{"taskid":"t1"}}"#)
            case "SYNO.FileStation.CopyMove.status": return .json(#"{"success":true,"data":{"finished":true}}"#)
            case "SYNO.FileStation.Rename.rename": return renameFails ? failure(1200) : .json(#"{"success":true,"data":{"files":[]}}"#)
            case "SYNO.FileStation.Delete.delete": return ok
            default: return failure(400)
            }
        }
    }

    @Test("A renamed copy onto a taken name is declined, so the caller copies the bytes")
    func keepBothDeclined() async throws {
        let host = StubHTTP.uniqueHost("syno-keep")
        let provider = SynologyDriveProvider(client: Self.keepBothClient(host: host, landingTaken: true))

        await #expect {
            try await provider.copyItem(at: "/a/x.txt", toPath: "/b/x (2).txt")
        } throws: { error in
            if case CloudProviderError.notImplemented = error { return true }
            return false
        }
        #expect(!StubHTTP.requests(host: host).map(Self.call).contains("SYNO.FileStation.CopyMove.start"))
    }

    @Test("A renamed copy within its own folder is declined without asking the NAS")
    func keepBothSameFolder() async throws {
        let host = StubHTTP.uniqueHost("syno-same")
        let provider = SynologyDriveProvider(client: Self.keepBothClient(host: host, landingTaken: false))

        await #expect {
            try await provider.moveItem(at: "/a/x.txt", toPath: "/a/x (2).txt")
        } throws: { error in
            if case CloudProviderError.notImplemented = error { return true }
            return false
        }
        #expect(StubHTTP.requests(host: host).isEmpty)
    }

    @Test("A renamed copy to a free spot lands and is renamed")
    func renamedCopyToFreeSpot() async throws {
        let host = StubHTTP.uniqueHost("syno-free")
        let provider = SynologyDriveProvider(client: Self.keepBothClient(host: host, landingTaken: false))

        try await provider.copyItem(at: "/a/x.txt", toPath: "/b/y.txt")

        let calls = StubHTTP.requests(host: host).map(Self.call)
        #expect(calls == ["SYNO.FileStation.List.getinfo", "SYNO.FileStation.CopyMove.start",
                          "SYNO.FileStation.CopyMove.status", "SYNO.FileStation.Rename.rename"])
    }

    @Test("A failed rename after a copy is reported, and the stray copy removed")
    func renameAfterCopyFails() async throws {
        let host = StubHTTP.uniqueHost("syno-rn-copy")
        let provider = SynologyDriveProvider(client: Self.keepBothClient(host: host, landingTaken: false, renameFails: true))

        await #expect(throws: CloudProviderError.self) {
            try await provider.copyItem(at: "/a/x.txt", toPath: "/b/y.txt")
        }
        let deletes = StubHTTP.requests(host: host).filter { Self.call($0) == "SYNO.FileStation.Delete.delete" }
        #expect(deletes.map { $0.query("path") } == ["/b/x.txt"])
    }

    @Test("A failed rename after a move is reported, and the moved item kept")
    func renameAfterMoveFails() async throws {
        let host = StubHTTP.uniqueHost("syno-rn-move")
        let provider = SynologyDriveProvider(client: Self.keepBothClient(host: host, landingTaken: false, renameFails: true))

        await #expect(throws: CloudProviderError.self) {
            try await provider.moveItem(at: "/a/x.txt", toPath: "/b/y.txt")
        }
        #expect(!StubHTTP.requests(host: host).map(Self.call).contains("SYNO.FileStation.Delete.delete"))
    }
}
