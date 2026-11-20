import Testing
import Foundation
@testable import FileFlussCore

/// The request behind a Nextcloud gallery tile: `core/preview.png` with the
/// file's path, a fixed preview size and the aspect ratio kept.
@Suite("Nextcloud thumbnails")
struct NextcloudThumbnailTests {

    private func queryItems(_ url: URL?) -> [String: String] {
        let items = url.flatMap { URLComponents(url: $0, resolvingAgainstBaseURL: false)?.queryItems } ?? []
        return Dictionary(uniqueKeysWithValues: items.map { ($0.name, $0.value ?? "") })
    }

    @Test("Tile sizes snap to the few preview sizes the server keeps")
    func previewEdges() {
        #expect(NextCloudAPIClient.previewEdge(forMaxPixelSize: 64) == 256)
        #expect(NextCloudAPIClient.previewEdge(forMaxPixelSize: 190) == 256)
        #expect(NextCloudAPIClient.previewEdge(forMaxPixelSize: 256) == 256)
        #expect(NextCloudAPIClient.previewEdge(forMaxPixelSize: 290) == 512)
        #expect(NextCloudAPIClient.previewEdge(forMaxPixelSize: 512) == 512)
        #expect(NextCloudAPIClient.previewEdge(forMaxPixelSize: 600) == 1024)
    }

    @Test("The preview URL names the file by its path and keeps the aspect ratio")
    func previewURL() {
        let url = NextCloudAPIClient.previewURL(
            serverURL: "https://cloud.example.com/",
            path: "/Photos/Summer 2026/IMG_0001.jpg",
            edge: 256
        )
        #expect(url?.host == "cloud.example.com")
        #expect(url?.path == "/index.php/core/preview.png")
        let query = queryItems(url)
        #expect(query["file"] == "/Photos/Summer 2026/IMG_0001.jpg")
        #expect(query["x"] == "256")
        #expect(query["y"] == "256")
        #expect(query["a"] == "1")
        #expect(query["forceIcon"] == "0")
    }

    @Test("A server installed in a subfolder keeps its prefix")
    func subfolderInstall() {
        let url = NextCloudAPIClient.previewURL(serverURL: "https://example.com/nextcloud", path: "a.png", edge: 512)
        #expect(url?.path == "/nextcloud/index.php/core/preview.png")
        #expect(queryItems(url)["file"] == "/a.png")
    }

    @Test("Characters that would split or alter the query are escaped")
    func escaping() throws {
        let url = try #require(NextCloudAPIClient.previewURL(
            serverURL: "https://cloud.example.com",
            path: "/A&B/x+y=z#1.jpg",
            edge: 256
        ))
        let raw = try #require(URLComponents(url: url, resolvingAgainstBaseURL: false)?.percentEncodedQuery)
        #expect(raw.contains("%2B"))
        #expect(!raw.contains("+"))
        #expect(queryItems(url)["file"] == "/A&B/x+y=z#1.jpg")
    }
}
