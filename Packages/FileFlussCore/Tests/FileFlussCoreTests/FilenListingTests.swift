import Testing
import Foundation
@testable import FileFlussCore

/// A Filen listing that leaves out what it can't decrypt looks emptier than
/// the folder is, and Mirror then deletes those files on the other side.
@Suite("Filen listing integrity")
struct FilenListingTests {

    private let masterKey = String(repeating: "ab", count: 32)
    private let otherKey = String(repeating: "cd", count: 32)
    private let olderKey = String(repeating: "01", count: 32)

    private func encrypt(_ json: String, with key: String) throws -> String {
        try FilenCrypto.encryptMetadata(json, aesKey: FilenCrypto.aesKeyFromV2MasterKey(key))
    }

    private func fileMeta(_ name: String) -> String {
        #"{"name":"\#(name)","size":5,"mime":"text/plain","key":"abcdefghijklmnopqrstuvwxyzABCDEF","lastModified":1700000500}"#
    }

    private func parse(_ response: [String: Any]) throws -> [CloudFileItem] {
        try FilenAPIClient.parseDirContent(response, parentPath: "/Docs", masterKeys: [masterKey, olderKey]).items
    }

    @Test("Readable entries are listed, under any of the master keys")
    func readable() throws {
        let response: [String: Any] = [
            "folders": [["uuid": "f1", "name": try encrypt(#"{"name":"Photos"}"#, with: olderKey), "timestamp": 1_600_000_000.0]],
            "uploads": [["uuid": "u1", "metadata": try encrypt(fileMeta("a.txt"), with: masterKey), "chunks": 1, "region": "de-1", "bucket": "b"]],
        ]
        let content = try FilenAPIClient.parseDirContent(response, parentPath: "/Docs", masterKeys: [masterKey, olderKey])
        #expect(content.items.map(\.path) == ["/Docs/Photos", "/Docs/a.txt"])
        #expect(content.items.map(\.id) == ["d:f1", "f:u1"])
        #expect(content.chunkInfo["u1"]?.size == 5)
    }

    @Test("Empty arrays are an empty folder")
    func empty() throws {
        #expect(try parse(["folders": [[String: Any]](), "uploads": [[String: Any]]()]).isEmpty)
    }

    @Test("A file whose metadata doesn't decrypt makes the listing unreadable")
    func undecryptableFile() throws {
        let response: [String: Any] = [
            "folders": [[String: Any]](),
            "uploads": [
                ["uuid": "u1", "metadata": try encrypt(fileMeta("a.txt"), with: masterKey)],
                ["uuid": "u2", "metadata": try encrypt(fileMeta("b.txt"), with: otherKey)],
            ],
        ]
        #expect(throws: CloudProviderError.self) { try parse(response) }
    }

    @Test("A folder whose metadata doesn't decrypt makes the listing unreadable")
    func undecryptableFolder() throws {
        let response: [String: Any] = [
            "folders": [["uuid": "f1", "name": "002notreallyencrypted"]],
            "uploads": [[String: Any]](),
        ]
        #expect(throws: CloudProviderError.self) { try parse(response) }
    }

    @Test("Metadata that decrypts but doesn't parse, or an entry without uuid, is unreadable too")
    func malformedEntries() throws {
        let noKey: [String: Any] = [
            "folders": [[String: Any]](),
            "uploads": [["uuid": "u1", "metadata": try encrypt(#"{"name":"a.txt"}"#, with: masterKey)]],
        ]
        #expect(throws: CloudProviderError.self) { try parse(noKey) }
        let noUuid: [String: Any] = [
            "folders": [["name": try encrypt(#"{"name":"Photos"}"#, with: masterKey)]],
            "uploads": [[String: Any]](),
        ]
        #expect(throws: CloudProviderError.self) { try parse(noUuid) }
    }

    @Test("An answer without folders or uploads is unreadable, not empty")
    func missingArrays() {
        #expect(throws: CloudProviderError.self) { try parse([:]) }
        #expect(throws: CloudProviderError.self) { try parse(["folders": [[String: Any]]()]) }
        #expect(throws: CloudProviderError.self) { try parse(["uploads": [[String: Any]]()]) }
        #expect(throws: CloudProviderError.self) { try parse(["folders": NSNull(), "uploads": [[String: Any]]()]) }
    }
}
