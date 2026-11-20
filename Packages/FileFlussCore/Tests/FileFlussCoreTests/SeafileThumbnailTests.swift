import Testing
import Foundation
@testable import FileFlussCore

/// The request behind a Seafile gallery tile:
/// `/api2/repos/<repo>/thumbnail/?p=<path in library>&size=<n>`.
@Suite("Seafile thumbnails")
struct SeafileThumbnailTests {

    @Test("Tile sizes snap to the few sizes Seahub keeps on disk")
    func sizes() {
        #expect(SeafileAPIClient.thumbnailSize(forMaxPixelSize: 48) == 256)
        #expect(SeafileAPIClient.thumbnailSize(forMaxPixelSize: 190) == 256)
        #expect(SeafileAPIClient.thumbnailSize(forMaxPixelSize: 256) == 256)
        #expect(SeafileAPIClient.thumbnailSize(forMaxPixelSize: 290) == 512)
        #expect(SeafileAPIClient.thumbnailSize(forMaxPixelSize: 512) == 512)
        #expect(SeafileAPIClient.thumbnailSize(forMaxPixelSize: 800) == 1024)
    }

    @Test("The query carries the path inside the library and the size")
    func query() {
        #expect(SeafileAPIClient.thumbnailQuery(innerPath: "/Photos/IMG_0001.jpg", size: 256)
                == "p=/Photos/IMG_0001.jpg&size=256")
    }

    @Test("Characters that would split or alter the query are escaped")
    func escaping() throws {
        let query = SeafileAPIClient.thumbnailQuery(innerPath: "/A&B/x+y=z #1.jpg", size: 512)
        #expect(query == "p=/A%26B/x%2By%3Dz%20%231.jpg&size=512")
        let components = try #require(URLComponents(string: "https://seafile.example.com/api2/repos/r/thumbnail/?\(query)"))
        let items = components.queryItems ?? []
        #expect(items.count == 2)
        #expect(items.first?.value == "/A&B/x+y=z #1.jpg")
    }
}
