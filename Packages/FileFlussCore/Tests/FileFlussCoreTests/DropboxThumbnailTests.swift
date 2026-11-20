import Testing
import Foundation
@testable import FileFlussCore

/// The request side of Dropbox's `/files/get_thumbnail_v2`.
@Suite("Dropbox thumbnails")
struct DropboxThumbnailTests {

    /// The shorter side has to reach the tile: w480h320 is the first size
    /// that covers 290, because a square picture comes back 320 square.
    @Test("The smallest size whose shorter side reaches the tile is chosen")
    func sizeBucket() {
        #expect(DropboxAPIClient.thumbnailSizeName(forMaxPixelSize: 1) == "w32h32")
        #expect(DropboxAPIClient.thumbnailSizeName(forMaxPixelSize: 128) == "w128h128")
        #expect(DropboxAPIClient.thumbnailSizeName(forMaxPixelSize: 190) == "w256h256")
        #expect(DropboxAPIClient.thumbnailSizeName(forMaxPixelSize: 256) == "w256h256")
        #expect(DropboxAPIClient.thumbnailSizeName(forMaxPixelSize: 257) == "w480h320")
        #expect(DropboxAPIClient.thumbnailSizeName(forMaxPixelSize: 290) == "w480h320")
        #expect(DropboxAPIClient.thumbnailSizeName(forMaxPixelSize: 321) == "w640h480")
        #expect(DropboxAPIClient.thumbnailSizeName(forMaxPixelSize: 5000) == "w2048h1536")
    }

    @Test("PNG and GIF files ask for PNG, everything else for JPEG")
    func format() {
        #expect(DropboxAPIClient.thumbnailFormat(forFileName: "Screen.PNG") == "png")
        #expect(DropboxAPIClient.thumbnailFormat(forFileName: "anim.gif") == "png")
        #expect(DropboxAPIClient.thumbnailFormat(forFileName: "photo.jpg") == "jpeg")
        #expect(DropboxAPIClient.thumbnailFormat(forFileName: "clip.mov") == "jpeg")
        #expect(DropboxAPIClient.thumbnailFormat(forFileName: "noext") == "jpeg")
    }

    @Test("The argument names the file by path, in bestfit mode")
    func argument() throws {
        let arg = try DropboxAPIClient.thumbnailArgument(
            path: "/Photos/a.jpg", fileName: "a.jpg", maxPixelSize: 240
        )
        #expect(arg == #"{"format":"jpeg","mode":"bestfit","resource":{".tag":"path","path":"/Photos/a.jpg"},"size":"w256h256"}"#)
    }

    /// HTTP headers are ASCII; Dropbox wants everything else as \u escapes.
    @Test("Non-ASCII characters in the path are escaped for the header")
    func argumentEscapesNonASCII() throws {
        let arg = try DropboxAPIClient.thumbnailArgument(
            path: "/Fotos/Grüße.png", fileName: "Grüße.png", maxPixelSize: 200
        )
        let isASCII = arg.unicodeScalars.allSatisfy { $0.isASCII }
        #expect(isASCII)
        #expect(arg.contains("Gr\\u00fc\\u00dfe.png"))
        #expect(arg.contains(#""format":"png""#))
    }

    @Test("Unsupported types and unreadable pictures count as no preview")
    func refusals() {
        func body(_ tag: String) -> Data {
            Data(#"{"error_summary": "\#(tag)/..", "error": {".tag": "\#(tag)"}}"#.utf8)
        }
        #expect(DropboxAPIClient.isThumbnailRefusal(body("unsupported_extension")))
        #expect(DropboxAPIClient.isThumbnailRefusal(body("unsupported_image")))
        #expect(DropboxAPIClient.isThumbnailRefusal(body("conversion_error")))
        #expect(!DropboxAPIClient.isThumbnailRefusal(body("path")))
        #expect(!DropboxAPIClient.isThumbnailRefusal(body("access_denied")))
        #expect(!DropboxAPIClient.isThumbnailRefusal(Data("not json".utf8)))
    }
}
