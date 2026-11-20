import Testing
import Foundation
@testable import FileFlussCore

@Suite("Google Drive thumbnails")
struct GoogleDriveThumbnailTests {

    @Test("A listed file's id is its Drive id behind the type letter")
    func fileIdFromItemId() {
        #expect(GoogleDriveAPIClient.driveFileId(fromItemId: "f1AbC-d_9") == "1AbC-d_9")
        #expect(GoogleDriveAPIClient.driveFileId(fromItemId: "d1AbC") == nil)
        #expect(GoogleDriveAPIClient.driveFileId(fromItemId: "f") == nil)
        #expect(GoogleDriveAPIClient.driveFileId(fromItemId: "") == nil)
    }

    @Test("The requested edge follows the tile within bounds")
    func edge() {
        #expect(GoogleDriveAPIClient.thumbnailEdge(forMaxPixelSize: 240) == 240)
        #expect(GoogleDriveAPIClient.thumbnailEdge(forMaxPixelSize: 0) == 32)
        #expect(GoogleDriveAPIClient.thumbnailEdge(forMaxPixelSize: 10_000) == 1600)
    }

    @Test("A trailing size token is rewritten")
    func rewritesTrailingSize() {
        let link = "https://lh3.googleusercontent.com/drive-storage/AJQWtBN_x-Y=s220"
        let url = GoogleDriveAPIClient.thumbnailURL(from: link, edge: 288)
        #expect(url?.absoluteString == "https://lh3.googleusercontent.com/drive-storage/AJQWtBN_x-Y=s288")
    }

    @Test("An sz query item is rewritten and the rest of the query kept as it was")
    func rewritesSizeQueryItem() {
        let link = "https://docs.google.com/feeds/vt?gd=true&id=abc&v=2&s=AMedNnoAAAAA%2Bx&sz=s220"
        let url = GoogleDriveAPIClient.thumbnailURL(from: link, edge: 256)
        #expect(url?.absoluteString == "https://docs.google.com/feeds/vt?gd=true&id=abc&v=2&s=AMedNnoAAAAA%2Bx&sz=s256")
    }

    @Test("A link without a recognised size token is left alone")
    func leavesOtherLinksAlone() {
        let link = "https://lh3.googleusercontent.com/drive-storage/AJQWtBN=s220-c"
        #expect(GoogleDriveAPIClient.thumbnailURL(from: link, edge: 256)?.absoluteString == link)
        let plain = "https://lh3.googleusercontent.com/drive-storage/AJQWtBN"
        #expect(GoogleDriveAPIClient.thumbnailURL(from: plain, edge: 256)?.absoluteString == plain)
    }

    @Test("Only Google's own HTTPS hosts are sent the token")
    func tokenHosts() {
        #expect(GoogleDriveAPIClient.mayCarryGoogleToken(URL(string: "https://lh3.googleusercontent.com/x=s220")!))
        #expect(GoogleDriveAPIClient.mayCarryGoogleToken(URL(string: "https://docs.google.com/feeds/vt?sz=s220")!))
        #expect(GoogleDriveAPIClient.mayCarryGoogleToken(URL(string: "https://www.googleapis.com/drive/v3/files/1")!))
        #expect(!GoogleDriveAPIClient.mayCarryGoogleToken(URL(string: "http://lh3.googleusercontent.com/x")!))
        #expect(!GoogleDriveAPIClient.mayCarryGoogleToken(URL(string: "https://evilgoogle.com/x")!))
        #expect(!GoogleDriveAPIClient.mayCarryGoogleToken(URL(string: "https://google.com.example.org/x")!))
    }
}

@Suite("OneDrive thumbnails")
struct OneDriveThumbnailTests {

    @Test("A listed file's id is its Graph id behind the type letter")
    func itemIdFromItemId() {
        #expect(OneDriveAPIClient.graphItemId(fromItemId: "fA1B2C3!104") == "A1B2C3!104")
        #expect(OneDriveAPIClient.graphItemId(fromItemId: "dA1B2C3!104") == nil)
        #expect(OneDriveAPIClient.graphItemId(fromItemId: "f") == nil)
    }

    @Test("The custom size is a square box around the tile, within Graph's standard range")
    func customSize() {
        #expect(OneDriveAPIClient.thumbnailSize(forMaxPixelSize: 240) == "c240x240")
        #expect(OneDriveAPIClient.thumbnailSize(forMaxPixelSize: 20) == "c96x96")
        #expect(OneDriveAPIClient.thumbnailSize(forMaxPixelSize: 4000) == "c800x800")
    }

    @Test("The standard fallback is the smallest size that covers the tile")
    func standardSize() {
        #expect(OneDriveAPIClient.standardThumbnailSize(forMaxPixelSize: 64) == "small")
        #expect(OneDriveAPIClient.standardThumbnailSize(forMaxPixelSize: 96) == "small")
        #expect(OneDriveAPIClient.standardThumbnailSize(forMaxPixelSize: 150) == "medium")
        #expect(OneDriveAPIClient.standardThumbnailSize(forMaxPixelSize: 176) == "medium")
        #expect(OneDriveAPIClient.standardThumbnailSize(forMaxPixelSize: 240) == "large")
    }
}

@Suite("kDrive thumbnails")
struct KDriveThumbnailTests {

    @Test("A listed file's id is its numeric kDrive id behind the type letter")
    func fileIdFromItemId() {
        #expect(KDriveAPIClient.fileId(fromItemId: "f4711") == 4711)
        #expect(KDriveAPIClient.fileId(fromItemId: "d4711") == nil)
        #expect(KDriveAPIClient.fileId(fromItemId: "fabc") == nil)
        #expect(KDriveAPIClient.fileId(fromItemId: "f") == nil)
    }

    @Test("The edge stays inside the 10 to 400 the endpoint accepts")
    func edge() {
        #expect(KDriveAPIClient.thumbnailEdge(forMaxPixelSize: 240) == 240)
        #expect(KDriveAPIClient.thumbnailEdge(forMaxPixelSize: 2) == 10)
        #expect(KDriveAPIClient.thumbnailEdge(forMaxPixelSize: 1200) == 400)
    }

    @Test("The URL names the drive and file and sizes a square box")
    func url() {
        let url = KDriveAPIClient.thumbnailURL(baseURL: "https://api.infomaniak.com", driveId: 12, fileId: 345, maxPixelSize: 288)
        #expect(url?.absoluteString == "https://api.infomaniak.com/2/drive/12/files/345/thumbnail?width=288&height=288")
    }
}
