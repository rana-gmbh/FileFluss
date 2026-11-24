import Testing
import Foundation
@testable import FileFlussCore

/// The QNAP paths that lost data: "Keep both" copies that overwrote the
/// file the user kept, a "wait until landed" that gave up silently or took
/// the old file for the new one, and listings cut off at 1,000 rows.
@Suite("QNAP data safety")
struct QNAPDataSafetyTests {
    typealias Handler = @Sendable (StubHTTP.Recorded) -> StubHTTP.Reply

    private static func client(host: String, landingTimeout: Duration = .seconds(60), handler: @escaping Handler) -> QNAPAPIClient {
        let credentials = QNAPCredentials(
            serverURL: "https://\(host)",
            username: "user",
            password: "secret",
            allowSelfSignedCertificate: false,
            displayName: "user@\(host)",
            clientId: "client"
        )
        return QNAPAPIClient(
            credentials: credentials,
            session: StubHTTP.session(host: host, handler: handler),
            sid: "sid1",
            landingBaseTimeout: landingTimeout,
            pollInterval: .milliseconds(1)
        )
    }

    private static func stat(size: Int, mtime: Int) -> StubHTTP.Reply {
        .json(#"{"datas":[{"filename":"x.txt","exist":1,"isfolder":0,"filesize":"\#(size)","epochmt":\#(mtime)}]}"#)
    }
    private static let missing = StubHTTP.Reply.json(#"{"datas":[{"exist":0}]}"#)
    private static let ok = StubHTTP.Reply.json(#"{"status":1}"#)

    /// The full path a `stat` request asks about.
    private static func statTarget(_ request: StubHTTP.Recorded) -> String? {
        guard request.query("func") == "stat", let parent = request.query("path"), let name = request.query("file_name") else { return nil }
        return parent == "/" ? "/" + name : parent + "/" + name
    }

    // MARK: - Listing

    @Test("A listing without a total keeps paging until a short page")
    func listingWithoutTotal() async throws {
        let host = StubHTTP.uniqueHost("qnap-list")
        let client = Self.client(host: host) { request in
            let start = Int(request.query("start") ?? "0") ?? 0
            let count = start == 0 ? 1000 : 5
            let rows = (0..<count).map { #"{"filename":"f\#(start + $0).txt","isfolder":0,"filesize":"1"}"# }
            return .json(#"{"datas":[\#(rows.joined(separator: ","))]}"#)
        }

        let items = try await client.listFolder(path: "/Public")

        #expect(items.count == 1005)
        #expect(StubHTTP.requests(host: host).count == 2)
    }

    @Test("A listing with a total stops at it")
    func listingWithTotal() async throws {
        let host = StubHTTP.uniqueHost("qnap-total")
        let client = Self.client(host: host) { _ in
            let rows = (0..<1000).map { #"{"filename":"f\#($0).txt","isfolder":0,"filesize":"1"}"# }
            return .json(#"{"total":1000,"datas":[\#(rows.joined(separator: ","))]}"#)
        }

        let items = try await client.listFolder(path: "/Public")

        #expect(items.count == 1000)
        #expect(StubHTTP.requests(host: host).count == 1)
    }

    // MARK: - Waiting for a copy to land

    @Test("An overwriting copy waits until the destination has changed, not just exists")
    func overwriteWaitsForChange() async throws {
        let host = StubHTTP.uniqueHost("qnap-land")
        let polls = CallCounter()
        let copied = CallCounter()
        let client = Self.client(host: host) { request in
            if request.query("func") == "copy" {
                _ = copied.next()
                return Self.ok
            }
            switch Self.statTarget(request) {
            case "/a/x.txt": return Self.stat(size: 10, mtime: 100)
            case "/b/x.txt":
                // The old file until the third look after the copy started.
                guard copied.count > 0 else { return Self.stat(size: 5, mtime: 50) }
                return polls.next() < 3 ? Self.stat(size: 5, mtime: 50) : Self.stat(size: 10, mtime: 200)
            default: return Self.missing
            }
        }

        try await client.copyMove(path: "/a/x.txt", toFolderPath: "/b", move: false)

        #expect(polls.count == 3)
    }

    @Test("A copy that never lands fails instead of passing for done")
    func neverLands() async throws {
        let host = StubHTTP.uniqueHost("qnap-never")
        let client = Self.client(host: host, landingTimeout: .milliseconds(50)) { request in
            if request.query("func") == "move" { return Self.ok }
            return Self.statTarget(request) == "/a/x.txt" ? Self.stat(size: 10, mtime: 100) : Self.missing
        }

        await #expect(throws: CloudProviderError.self) {
            try await client.copyMove(path: "/a/x.txt", toFolderPath: "/b", move: true)
        }
    }

    @Test("Landing: the size has to match, and an old file at the spot has to change")
    func hasLanded() {
        func file(_ size: Int64, _ mtime: TimeInterval) -> CloudFileItem {
            CloudFileItem(id: "x", name: "x", path: "/b/x", isDirectory: false, size: size,
                          modificationDate: Date(timeIntervalSince1970: mtime), checksum: nil)
        }
        let source = file(10, 100)
        #expect(QNAPAPIClient.hasLanded(source: source, before: nil, landed: file(10, 300)))
        #expect(!QNAPAPIClient.hasLanded(source: source, before: nil, landed: file(4, 300)))
        #expect(!QNAPAPIClient.hasLanded(source: source, before: file(10, 50), landed: file(10, 50)))
        #expect(QNAPAPIClient.hasLanded(source: source, before: file(10, 50), landed: file(10, 300)))
        // The spot already held this very file: nothing will change.
        #expect(QNAPAPIClient.hasLanded(source: source, before: file(10, 100), landed: file(10, 100)))
        #expect(QNAPAPIClient.hasLanded(source: nil, before: nil, landed: file(1, 1)))
    }

    // MARK: - Keep both

    @Test("A renamed copy onto a taken name is declined, so the caller copies the bytes")
    func keepBothDeclined() async throws {
        let host = StubHTTP.uniqueHost("qnap-keep")
        let provider = QNAPProvider(client: Self.client(host: host) { request in
            if Self.statTarget(request) == "/b/x.txt" { return Self.stat(size: 5, mtime: 50) }
            return Self.ok
        })

        await #expect {
            try await provider.copyItem(at: "/a/x.txt", toPath: "/b/x (2).txt")
        } throws: { error in
            if case CloudProviderError.notImplemented = error { return true }
            return false
        }
        let functions = StubHTTP.requests(host: host).compactMap { $0.query("func") }
        #expect(!functions.contains("copy"))
    }

    @Test("A renamed copy within its own folder is declined without asking the NAS")
    func keepBothSameFolder() async throws {
        let host = StubHTTP.uniqueHost("qnap-same")
        let provider = QNAPProvider(client: Self.client(host: host) { _ in Self.ok })

        await #expect {
            try await provider.copyItem(at: "/a/x.txt", toPath: "/a/x (2).txt")
        } throws: { error in
            if case CloudProviderError.notImplemented = error { return true }
            return false
        }
        #expect(StubHTTP.requests(host: host).isEmpty)
    }

    @Test("A renamed copy to a free spot lands and is renamed")
    func renamedCopyToFreeSpot() async throws {
        let host = StubHTTP.uniqueHost("qnap-free")
        let copied = CallCounter()
        let provider = QNAPProvider(client: Self.client(host: host) { request in
            switch request.query("func") {
            case "copy":
                _ = copied.next()
                return Self.ok
            case "rename": return Self.ok
            default:
                switch Self.statTarget(request) {
                case "/a/x.txt": return Self.stat(size: 10, mtime: 100)
                case "/b/x.txt": return copied.count > 0 ? Self.stat(size: 10, mtime: 100) : Self.missing
                default: return Self.missing
                }
            }
        })

        try await provider.copyItem(at: "/a/x.txt", toPath: "/b/y.txt")

        let rename = try #require(StubHTTP.requests(host: host).first { $0.query("func") == "rename" })
        #expect(rename.query("path") == "/b")
        #expect(rename.query("source_name") == "x.txt")
        #expect(rename.query("dest_name") == "y.txt")
    }
}
