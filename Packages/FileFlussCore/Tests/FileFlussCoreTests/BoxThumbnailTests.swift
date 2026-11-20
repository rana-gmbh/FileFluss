import Testing
import Foundation
@testable import FileFlussCore

/// The request side of Box's `/files/{id}/thumbnail.jpg`.
@Suite("Box thumbnails")
struct BoxThumbnailTests {

    @Test("The smallest JPEG size reaching the tile is chosen, at most 320")
    func sizeBucket() {
        #expect(BoxAPIClient.thumbnailSize(forMaxPixelSize: 20) == 32)
        #expect(BoxAPIClient.thumbnailSize(forMaxPixelSize: 32) == 32)
        #expect(BoxAPIClient.thumbnailSize(forMaxPixelSize: 100) == 160)
        #expect(BoxAPIClient.thumbnailSize(forMaxPixelSize: 190) == 320)
        #expect(BoxAPIClient.thumbnailSize(forMaxPixelSize: 290) == 320)
        #expect(BoxAPIClient.thumbnailSize(forMaxPixelSize: 1000) == 320)
    }

    @Test("Only lower bounds are sent")
    func queryItems() {
        #expect(BoxAPIClient.thumbnailQueryItems(forMaxPixelSize: 240) == [
            URLQueryItem(name: "min_width", value: "320"),
            URLQueryItem(name: "min_height", value: "320"),
        ])
    }

    @Test("Box's no-preview codes count as no preview, other errors don't")
    func refusals() {
        func body(_ code: String) -> Data {
            Data(#"{"type": "error", "status": 404, "code": "\#(code)", "message": "x"}"#.utf8)
        }
        #expect(BoxAPIClient.isThumbnailRefusal(statusCode: 404, body: body("preview_cannot_be_generated")))
        #expect(BoxAPIClient.isThumbnailRefusal(statusCode: 400, body: body("requested_preview_unavailable")))
        #expect(!BoxAPIClient.isThumbnailRefusal(statusCode: 404, body: body("not_found")))
        #expect(!BoxAPIClient.isThumbnailRefusal(statusCode: 400, body: body("bad_request")))
        #expect(!BoxAPIClient.isThumbnailRefusal(statusCode: 500, body: body("preview_cannot_be_generated")))
        #expect(!BoxAPIClient.isThumbnailRefusal(statusCode: 404, body: Data()))
    }
}
