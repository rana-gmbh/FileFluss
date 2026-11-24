import Testing
import Foundation
@testable import FileFlussCore

/// A WordPress media library behind a scripted `/wp-json/wp/v2/media`.
///
/// The fake answers the query parameters the client uses (`after`,
/// `before`, `orderby=date`, `order`, `per_page`, `page`, `search`) the way
/// WordPress does, including the pagination headers and the 400 for a page
/// past the end, so the tests pin behaviour rather than request shapes.
private struct FakeMediaLibrary: Sendable {
    struct Media: Sendable {
        let id: Int
        let date: String      // "2015-03-04T10:00:00"
        let file: String      // "photo.jpg"

        var json: String {
            let folder = String(date.prefix(7)).replacingOccurrences(of: "-", with: "/")
            return #"{"id":\#(id),"date":"\#(date)","media_details":{"file":"\#(folder)/\#(file)","filesize":10},"source_url":"https://cdn.example/\#(folder)/\#(file)"}"#
        }
    }

    var media: [Media]
    /// Pages that fail with this status, keyed by "after|page".
    var failures: [String: Int] = [:]

    func reply(to request: URLRequest) -> ProviderStubURLProtocol.Reply {
        let components = URLComponents(url: request.url!, resolvingAgainstBaseURL: false)!
        let query = Dictionary((components.queryItems ?? []).map { ($0.name, $0.value ?? "") }, uniquingKeysWith: { $1 })
        guard request.httpMethod ?? "GET" == "GET", components.path.hasSuffix("/media") else {
            return .status(200, "{}")
        }

        var matches = media
        if let after = query["after"] { matches = matches.filter { $0.date > after } }
        if let before = query["before"] { matches = matches.filter { $0.date < before } }
        if let search = query["search"] { matches = matches.filter { $0.file.contains(search) } }
        let ascending = query["order"] == "asc"
        matches.sort { ascending ? $0.date < $1.date : $0.date > $1.date }

        let perPage = Int(query["per_page"] ?? "10") ?? 10
        let page = Int(query["page"] ?? "1") ?? 1
        if let status = failures["\(query["after"] ?? "")|\(page)"] {
            return .status(status, #"{"code":"boom"}"#)
        }
        let totalPages = max(1, (matches.count + perPage - 1) / perPage)
        guard page <= totalPages else {
            return .status(400, #"{"code":"rest_post_invalid_page_number"}"#)
        }
        let slice = matches.dropFirst((page - 1) * perPage).prefix(perPage)
        var reply = ProviderStubURLProtocol.Reply.json("[" + slice.map(\.json).joined(separator: ",") + "]")
        reply.headers["X-WP-Total"] = "\(matches.count)"
        reply.headers["X-WP-TotalPages"] = "\(totalPages)"
        return reply
    }
}

@Suite("WordPress listing safety")
struct WordPressListingSafetyTests {

    private func client(host: String, library: FakeMediaLibrary) -> WordPressAPIClient {
        WordPressAPIClient(
            credentials: WordPressCredentials(siteURL: "https://\(host)", username: "u", password: "p", displayName: "Site"),
            session: ProviderStubURLProtocol.session(host: host) { request, _ in library.reply(to: request) }
        )
    }

    /// Twelve hundred items in 2010, one in 2015, a hundred in 2024: with
    /// 100 per page the 2015 item sits on page 13, past the nine pages the
    /// root listing used to sample.
    @Test("The root lists every year, however large the library")
    func everyYearIsListed() async throws {
        var media: [FakeMediaLibrary.Media] = []
        for i in 0..<1200 {
            media.append(.init(id: i, date: String(format: "2010-01-01T%02d:%02d:%02d", i / 3600, (i / 60) % 60, i % 60), file: "a\(i).jpg"))
        }
        media.append(.init(id: 5000, date: "2015-06-01T12:00:00", file: "middle.jpg"))
        for i in 0..<100 {
            media.append(.init(id: 6000 + i, date: String(format: "2024-03-01T00:00:%02d", i % 60), file: "z\(i).jpg"))
        }
        let client = client(host: "wp-years.test", library: FakeMediaLibrary(media: media))
        let years = try await client.listFolder(path: "/").map(\.name)
        #expect(years == ["2024", "2015", "2010"])
    }

    @Test("An empty library lists no years")
    func emptyLibrary() async throws {
        let client = client(host: "wp-empty.test", library: FakeMediaLibrary(media: []))
        #expect(try await client.listFolder(path: "/").isEmpty)
    }

    /// A month listing that stopped at a failed page used to return what it
    /// had, and a Mirror from it deleted everything on the later pages.
    @Test("A failed page fails the month listing instead of truncating it")
    func failedPageThrows() async throws {
        let media = (0..<150).map { FakeMediaLibrary.Media(id: $0, date: String(format: "2024-05-02T10:%02d:%02d", $0 / 60, $0 % 60), file: "p\($0).jpg") }
        var library = FakeMediaLibrary(media: media)
        library.failures["2024-05-01T00:00:00|2"] = 500
        let client = client(host: "wp-fail.test", library: library)
        await #expect(throws: CloudProviderError.self) {
            _ = try await client.listFolder(path: "/2024/05")
        }

        // Without the failure every page is read.
        let healthy = self.client(host: "wp-ok.test", library: FakeMediaLibrary(media: media))
        #expect(try await healthy.listFolder(path: "/2024/05").count == 150)
    }

    @Test("A year listing with a failed page throws")
    func failedYearPageThrows() async throws {
        let media = (0..<150).map { FakeMediaLibrary.Media(id: $0, date: String(format: "2024-05-02T10:%02d:%02d", $0 / 60, $0 % 60), file: "p\($0).jpg") }
        var library = FakeMediaLibrary(media: media)
        library.failures["2024-01-01T00:00:00|2"] = 503
        let client = client(host: "wp-yfail.test", library: library)
        await #expect(throws: CloudProviderError.self) {
            _ = try await client.listFolder(path: "/2024")
        }
    }

    /// Deleting is permanent. `logo.png` from 2019/02 must never be taken for
    /// a `logo.png` the user asked to delete in 2024/05.
    @Test("A delete never resolves to a same-named file in another month")
    func deleteDoesNotMatchAnotherMonth() async throws {
        let library = FakeMediaLibrary(media: [
            .init(id: 7, date: "2019-02-10T09:00:00", file: "logo.png"),
            .init(id: 8, date: "2024-05-10T09:00:00", file: "other.png"),
        ])
        let host = "wp-delete.test"
        let client = client(host: host, library: library)
        await #expect(throws: CloudProviderError.self) {
            try await client.deleteItem(at: "/2024/05/logo.png")
        }
        #expect(!ProviderStubURLProtocol.requests(for: host).contains { $0.method == "DELETE" })

        // The non-destructive lookups don't take the other month's file either.
        await #expect(throws: CloudProviderError.self) {
            _ = try await client.getFileInfo(at: "/2024/05/logo.png")
        }
    }

    @Test("Listing a path that is no year or month folder is not found")
    func unknownPath() async throws {
        let client = client(host: "wp-path.test", library: FakeMediaLibrary(media: []))
        await #expect(throws: CloudProviderError.self) {
            _ = try await client.listFolder(path: "/uploads")
        }
    }

    /// In a cloud-to-cloud transfer the local file is a staging copy named
    /// `filefluss-sync-<UUID>`; the media library must get the real name.
    @Test("An upload is named after the remote path, not the staging file")
    func uploadUsesRemoteName() async throws {
        let host = "wp-upload.test"
        let staging = FileManager.default.temporaryDirectory.appendingPathComponent("filefluss-sync-\(UUID().uuidString)")
        try Data("bytes".utf8).write(to: staging)
        defer { try? FileManager.default.removeItem(at: staging) }

        let client = WordPressAPIClient(
            credentials: WordPressCredentials(siteURL: "https://\(host)", username: "u", password: "p", displayName: "Site"),
            session: ProviderStubURLProtocol.session(host: host) { _, _ in .json(#"{"id":1}"#, status: 201) }
        )
        try await client.uploadFile(from: staging, to: "/2024/05/Grüße \"x\".jpg")

        let upload = try #require(ProviderStubURLProtocol.requests(for: host).first { $0.method == "POST" })
        let body = String(decoding: upload.body ?? Data(), as: UTF8.self)
        #expect(body.contains(#"filename="Grüße 'x'.jpg""#))
        #expect(body.contains("Content-Type: image/jpeg"))
        #expect(!body.contains("filefluss-sync-"))

        #expect(WordPressAPIClient.uploadFileName(remotePath: "/2024/05/a.png", localURL: staging) == "a.png")
        #expect(WordPressAPIClient.uploadFileName(remotePath: "/", localURL: staging) == staging.lastPathComponent)
    }
}
