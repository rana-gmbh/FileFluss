import Testing
import Foundation
@testable import FileFlussCore

/// The request behind a Synology gallery tile: `SYNO.FileStation.Thumb`
/// version 2, method `get`, with the path JSON-quoted as in the API guide.
@Suite("Synology thumbnails")
struct SynologyThumbnailTests {

    @Test("Tile sizes map onto DSM's named thumbnail sizes")
    func sizes() {
        #expect(SynologyDriveAPIClient.thumbnailSize(forMaxPixelSize: 64) == "small")
        #expect(SynologyDriveAPIClient.thumbnailSize(forMaxPixelSize: 120) == "small")
        #expect(SynologyDriveAPIClient.thumbnailSize(forMaxPixelSize: 190) == "medium")
        #expect(SynologyDriveAPIClient.thumbnailSize(forMaxPixelSize: 290) == "medium")
        #expect(SynologyDriveAPIClient.thumbnailSize(forMaxPixelSize: 320) == "medium")
        #expect(SynologyDriveAPIClient.thumbnailSize(forMaxPixelSize: 600) == "large")
    }

    @Test("The query names the Thumb API and quotes the path")
    func queryItems() {
        let items = SynologyDriveAPIClient.thumbnailQueryItems(path: "/photo/test.jpg", size: "medium")
        let query = Dictionary(uniqueKeysWithValues: items.map { ($0.name, $0.value ?? "") })
        #expect(query["api"] == "SYNO.FileStation.Thumb")
        #expect(query["version"] == "2")
        #expect(query["method"] == "get")
        #expect(query["path"] == "\"/photo/test.jpg\"")
        #expect(query["size"] == "medium")
        // The session id is added by the client, never part of the shared part.
        #expect(query["_sid"] == nil)
    }

    @Test("Quotes and backslashes in a path stay inside the JSON string")
    func quotingEscapes() {
        let items = SynologyDriveAPIClient.thumbnailQueryItems(path: "/photo/say \"hi\"\\.jpg", size: "small")
        #expect(items.first { $0.name == "path" }?.value == "\"/photo/say \\\"hi\\\"\\\\.jpg\"")
    }

    @Test("A plus sign in the query is escaped so DSM doesn't read a space")
    func plusEscaped() throws {
        var components = URLComponents(string: "https://nas.example.com:5001/webapi/entry.cgi")!
        components.queryItems = SynologyDriveAPIClient.thumbnailQueryItems(path: "/photo/a+b.jpg", size: "medium")
        let url = SynologyDriveAPIClient.encodingPlus(in: try #require(components.url))
        let raw = try #require(URLComponents(url: url, resolvingAgainstBaseURL: false)?.percentEncodedQuery)
        #expect(!raw.contains("+"))
        #expect(raw.contains("a%2Bb.jpg"))
    }
}
