import Testing
import Foundation
@testable import FileFlussCore

/// Exercises the GoPro `/gopro/media/list` decoder and the media→CloudFileItem
/// tree builder against representative firmware JSON — including the quirk that
/// numeric fields arrive as quoted strings.
@Suite("GoPro media list")
struct GoProMediaListTests {

    private let sampleJSON = """
    {
      "id": "1696600000",
      "media": [
        {
          "d": "100GOPRO",
          "fs": [
            { "n": "GH010397.MP4", "cre": "1696600100", "mod": "1696600109", "s": "11587660" },
            { "n": "GOPR0987.JPG", "cre": "1696600200", "mod": "1696600200", "s": "4194304" }
          ]
        },
        {
          "d": "101GOPRO",
          "fs": [
            { "n": "GH010001.MP4", "cre": "1696700000", "mod": "1696700000", "s": "2048" }
          ]
        }
      ]
    }
    """

    private func decode(_ json: String) throws -> GoProMediaList {
        try JSONDecoder().decode(GoProMediaList.self, from: Data(json.utf8))
    }

    @Test("Root lists each on-card directory as a folder")
    func rootListsDirectories() throws {
        let list = try decode(sampleJSON)
        let root = try list.items(at: "/")
        let allFolders = root.allSatisfy { $0.isDirectory }
        #expect(root.count == 2)
        #expect(allFolders)
        #expect(root.map(\.name).sorted() == ["100GOPRO", "101GOPRO"])

        // Folder size is the sum of its files (11587660 + 4194304).
        let first = try #require(root.first { $0.name == "100GOPRO" })
        #expect(first.size == 15_781_964)
        #expect(first.path == "/100GOPRO")
    }

    @Test("Directory lists its files with full paths and parsed sizes")
    func directoryListsFiles() throws {
        let list = try decode(sampleJSON)
        let files = try list.items(at: "/100GOPRO")
        let noneAreFolders = files.allSatisfy { !$0.isDirectory }
        #expect(files.count == 2)
        #expect(noneAreFolders)

        let video = try #require(files.first { $0.name == "GH010397.MP4" })
        #expect(video.path == "/100GOPRO/GH010397.MP4")
        #expect(video.size == 11_587_660)
        // mod = 1696600109 → that exact instant.
        #expect(video.modificationDate == Date(timeIntervalSince1970: 1_696_600_109))
    }

    /// After a card swap or format the folder a sync reads from is gone.
    /// Listing it as empty would make a Mirror delete the copied footage.
    @Test("Unknown directory is not found, not empty")
    func unknownDirectory() throws {
        let list = try decode(sampleJSON)
        #expect(throws: CloudProviderError.self) { try list.items(at: "/999GOPRO") }
        let empty = try decode(#"{ "media": [] }"#)
        #expect(throws: CloudProviderError.self) { try empty.items(at: "/100GOPRO") }
        #expect(try empty.items(at: "/").isEmpty)
    }

    @Test("Single-file lookup by path")
    func fileLookup() throws {
        let list = try decode(sampleJSON)
        let item = try #require(list.file(at: "/101GOPRO/GH010001.MP4"))
        #expect(item.size == 2048)
        #expect(item.isDirectory == false)
        #expect(list.file(at: "/101GOPRO/missing.mp4") == nil)
        // A directory path is not a file.
        #expect(list.file(at: "/101GOPRO") == nil)
    }

    @Test("Tolerates numeric (non-string) size/date fields")
    func numericFields() throws {
        let json = """
        { "media": [ { "d": "100GOPRO", "fs": [ { "n": "A.JPG", "s": 1234, "mod": 1696600109 } ] } ] }
        """
        let list = try decode(json)
        let item = try #require(try list.items(at: "/100GOPRO").first)
        #expect(item.size == 1234)
        #expect(item.modificationDate == Date(timeIntervalSince1970: 1_696_600_109))
    }

    @Test("Missing dates fall back to distantPast")
    func missingDates() throws {
        let json = """
        { "media": [ { "d": "100GOPRO", "fs": [ { "n": "A.JPG", "s": "10" } ] } ] }
        """
        let list = try decode(json)
        let item = try #require(try list.items(at: "/100GOPRO").first)
        #expect(item.modificationDate == .distantPast)
    }
}

/// Pure-logic checks for the USB subnet heuristic used by discovery.
@Suite("GoPro discovery helpers")
struct GoProDiscoveryTests {

    @Test("Recognises the GoPro USB address range 172.16–172.31")
    func usbRange() {
        #expect(GoProDiscovery.isInGoProUSBRange("172.16.0.1"))
        #expect(GoProDiscovery.isInGoProUSBRange("172.24.106.50"))
        #expect(GoProDiscovery.isInGoProUSBRange("172.31.255.254"))
        // Outside the range.
        #expect(!GoProDiscovery.isInGoProUSBRange("172.15.0.1"))
        #expect(!GoProDiscovery.isInGoProUSBRange("172.32.0.1"))
        #expect(!GoProDiscovery.isInGoProUSBRange("192.168.1.10"))
        #expect(!GoProDiscovery.isInGoProUSBRange("10.5.5.9"))
        #expect(!GoProDiscovery.isInGoProUSBRange("not.an.ip"))
    }
}
