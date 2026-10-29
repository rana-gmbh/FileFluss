import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

/// Pins the parts of the Drime account that fail silently: how a listing
/// page becomes panel rows, how a big upload is cut into parts, which
/// server message reaches the user, and the redirect URI Drime compares
/// against its registration.
@Suite("Drime account")
struct DrimeProviderTests {

    // MARK: - Listing

    /// Shaped like the documented root listing, with the variations real
    /// responses carry: ids and sizes as strings, a null size on a folder,
    /// and a client modification time in milliseconds.
    private let listingPage = Data("""
    {
      "data": [
        {"id": 485529677, "name": "My Documents", "type": "folder", "hash": "NDg1NTI5Njc3fA",
         "file_size": null, "parent_id": null,
         "created_at": "2024-01-15T10:30:00.000000Z", "updated_at": "2024-01-15T10:30:00.000000Z"},
        {"id": "485529678", "name": "photo.jpg", "type": "image", "hash": "NDg1NTI5Njc4fA",
         "file_size": "2048576", "parent_id": null, "mime": "image/jpeg",
         "file_hash": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
         "created_at": "2024-01-14T08:20:00.000000Z", "updated_at": "2024-01-20T15:30:00.000000Z",
         "client_last_modified": 1712345678000},
        {"id": 485529679, "name": "notes.txt", "type": "text", "file_size": 12,
         "updated_at": "2024-01-20T15:30:00Z", "client_last_modified": null, "file_hash": ""}
      ],
      "folder": null, "current_page": 1, "last_page": 1, "per_page": 1000, "total": 3
    }
    """.utf8)

    @Test("A listing page becomes rows under the folder that was listed")
    func listingRows() throws {
        let items = try DrimeAPIClient.items(fromListingPage: listingPage, parentPath: "/Work/")
        #expect(items.map(\.path) == ["/Work/My Documents", "/Work/photo.jpg", "/Work/notes.txt"])
        #expect(items.map(\.isDirectory) == [true, false, false])
        // Only "folder" is a folder: Drime types files by content
        // ("image", "text"), and none of those may be browsed into.
        #expect(items[1].size == 2_048_576)
        #expect(items[0].size == 0)
    }

    /// The original mtime is what a sync compares, so it must win over the
    /// server's own `updated_at` — and an empty one must not read as 1970.
    @Test("The client's modification time wins over the server's")
    func modificationDates() throws {
        let items = try DrimeAPIClient.items(fromListingPage: listingPage, parentPath: "/")
        #expect(items[1].modificationDate == Date(timeIntervalSince1970: 1_712_345_678))
        let updated = ISO8601DateFormatter().date(from: "2024-01-20T15:30:00Z")
        #expect(items[2].modificationDate == updated)
    }

    @Test("The content hash comes through, an empty one doesn't")
    func checksums() throws {
        let items = try DrimeAPIClient.items(fromListingPage: listingPage, parentPath: "/")
        #expect(items[1].checksum == "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")
        #expect(items[2].checksum == nil)
    }

    // MARK: - Uploads

    @Test("Parts are 16 MiB until the 10,000-part limit forces them larger")
    func partSizes() {
        let mib: Int64 = 1024 * 1024
        #expect(DrimeAPIClient.partSize(forFileSize: 6 * mib) == 16 * mib)
        #expect(DrimeAPIClient.partSize(forFileSize: 100 * 1024 * mib) == 16 * mib)

        // 500 GiB / 10,000 parts needs more than 16 MiB per part.
        let huge: Int64 = 500 * 1024 * mib
        let part = DrimeAPIClient.partSize(forFileSize: huge)
        #expect(part % mib == 0)
        #expect((huge + part - 1) / part <= 10_000)
        #expect(part - mib < (huge + 9_999) / 10_000)
    }

    // MARK: - Errors

    /// A token without the right permission is the likeliest failure with
    /// personal tokens, and Drime names the missing scope — that has to
    /// reach the user rather than a generic "unauthorized".
    @Test("A missing scope is reported in Drime's words")
    func missingScope() {
        let body = Data(#"{"message": "This API key is missing the required scope: files.write"}"#.utf8)
        guard case .commandFailed(let message) = DrimeAPIClient.mapHTTPError(statusCode: 403, body: body) else {
            Issue.record("Expected the server message to be passed through")
            return
        }
        #expect(message.contains("files.write"))
    }

    @Test("A validation error shows the field message, not Laravel's summary")
    func validationMessage() {
        let body = Data(#"{"status": "error", "message": "The given data was invalid.", "errors": {"name": ["The name has already been taken."]}}"#.utf8)
        #expect(DrimeAPIClient.serverMessage(in: body) == "The name has already been taken.")
    }

    @Test("An unexplained 403 and a 401 keep their usual meaning")
    func plainStatuses() {
        let forbidden = Data(#"{"message": "This action is unauthorized."}"#.utf8)
        guard case .unauthorized = DrimeAPIClient.mapHTTPError(statusCode: 403, body: forbidden) else {
            Issue.record("Expected .unauthorized")
            return
        }
        guard case .notAuthenticated = DrimeAPIClient.mapHTTPError(statusCode: 401, body: nil) else {
            Issue.record("Expected .notAuthenticated")
            return
        }
    }

    // MARK: - Wire formats

    @Test("Expiry dates are written the way Drime writes them")
    func timestampFormat() {
        let date = Date(timeIntervalSince1970: 1_615_052_040) // 2021-03-06 17:34:00 UTC
        #expect(DrimeAPIClient.formatTimestamp(date) == "2021-03-06T17:34:00.000000Z")
    }

    /// Drime ignores the port only for the IP literal, so a random port
    /// with `localhost` would never match the registered redirect URL.
    @Test("Drime's redirect is the IP literal with a /callback path")
    func redirectURI() {
        #expect(LoopbackOAuthAuthenticator.redirectURI(port: 53682, loopback: .ipLiteralCallback) == "http://127.0.0.1:53682/callback")
        #expect(LoopbackOAuthAuthenticator.redirectURI(port: 53682, loopback: .localhost) == "http://localhost:53682")
    }
}
