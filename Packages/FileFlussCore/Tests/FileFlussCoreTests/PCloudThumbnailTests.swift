import Testing
import Foundation
@testable import FileFlussCore

/// The request side of pCloud's `getthumb`.
@Suite("pCloud thumbnails")
struct PCloudThumbnailTests {

    /// pCloud rejects a size that isn't divisible by 4 or 5, or lies
    /// outside 16...1024 for the height.
    @Test("The box edge is the first size pCloud accepts from the tile up")
    func edge() {
        #expect(PCloudAPIClient.thumbnailEdge(forMaxPixelSize: 190) == 190)
        #expect(PCloudAPIClient.thumbnailEdge(forMaxPixelSize: 191) == 192)
        #expect(PCloudAPIClient.thumbnailEdge(forMaxPixelSize: 193) == 195)
        #expect(PCloudAPIClient.thumbnailEdge(forMaxPixelSize: 289) == 290)
        #expect(PCloudAPIClient.thumbnailEdge(forMaxPixelSize: 1) == 16)
        #expect(PCloudAPIClient.thumbnailEdge(forMaxPixelSize: 5000) == 1024)
        for size in 1...1100 {
            let edge = PCloudAPIClient.thumbnailEdge(forMaxPixelSize: size)
            #expect(edge % 4 == 0 || edge % 5 == 0)
            #expect((16...1024).contains(edge))
            #expect(edge >= min(size, 1024))
        }
    }

    @Test("A listed file's id is used; anything else falls back to the path")
    func fileId() {
        #expect(PCloudAPIClient.fileId(fromItemId: "f123") == 123)
        #expect(PCloudAPIClient.fileId(fromItemId: "f0") == nil)
        #expect(PCloudAPIClient.fileId(fromItemId: "d42") == nil)
        #expect(PCloudAPIClient.fileId(fromItemId: "/a/b.jpg") == nil)
        #expect(PCloudAPIClient.fileId(fromItemId: "f") == nil)
    }

    @Test("The parameters name the file by id when there is one")
    func parameters() {
        #expect(PCloudAPIClient.thumbnailParameters(fileId: 7, path: "/a.jpg", maxPixelSize: 240) == [
            URLQueryItem(name: "fileid", value: "7"),
            URLQueryItem(name: "size", value: "240x240"),
        ])
        #expect(PCloudAPIClient.thumbnailParameters(fileId: nil, path: "/a.jpg", maxPixelSize: 241) == [
            URLQueryItem(name: "path", value: "/a.jpg"),
            URLQueryItem(name: "size", value: "244x244"),
        ])
    }

    /// Form decoding reads "+" as a space and "&" as the next field, so
    /// both must be escaped in a file name.
    @Test("Form values are escaped down to unreserved characters")
    func formEncoding() {
        let body = PCloudAPIClient.formEncoded([
            URLQueryItem(name: "access_token", value: "a.b-c_d~e"),
            URLQueryItem(name: "path", value: "/Fotos/A+B & C=ü.jpg"),
        ])
        #expect(String(decoding: body, as: UTF8.self)
            == "access_token=a.b-c_d~e&path=%2FFotos%2FA%2BB%20%26%20C%3D%C3%BC.jpg")
    }

    @Test("Unsupported types and failed drawings count as no preview")
    func refusals() {
        #expect(PCloudAPIClient.isThumbnailRefusal(code: 1014))
        #expect(PCloudAPIClient.isThumbnailRefusal(code: 3001))
        #expect(!PCloudAPIClient.isThumbnailRefusal(code: 2009))
        #expect(!PCloudAPIClient.isThumbnailRefusal(code: 1015))
        #expect(!PCloudAPIClient.isThumbnailRefusal(code: 1000))
    }
}
