import Testing
import Foundation
import CommonCrypto
@testable import FileFlussCore

/// MEGA's crypto, done independently of the client so the tests can build
/// a node tree and encrypted file content the way MEGA's servers hold them.
private enum TestMega {
    static func words(_ bytes: [UInt8]) -> [UInt32] {
        stride(from: 0, to: bytes.count - bytes.count % 4, by: 4).map {
            UInt32(bytes[$0]) << 24 | UInt32(bytes[$0 + 1]) << 16 | UInt32(bytes[$0 + 2]) << 8 | UInt32(bytes[$0 + 3])
        }
    }

    static func bytes(_ words: [UInt32]) -> [UInt8] {
        words.flatMap { [UInt8($0 >> 24 & 0xFF), UInt8($0 >> 16 & 0xFF), UInt8($0 >> 8 & 0xFF), UInt8($0 & 0xFF)] }
    }

    static func aes(_ input: [UInt8], key: [UInt32], options: CCOptions, iv: [UInt8]? = nil) -> [UInt8] {
        let keyBytes = bytes(key)
        var out = [UInt8](repeating: 0, count: input.count)
        var moved = 0
        let status = CCCrypt(CCOperation(kCCEncrypt), CCAlgorithm(kCCAlgorithmAES128), options,
                             keyBytes, keyBytes.count, iv, input, input.count, &out, out.count, &moved)
        precondition(status == kCCSuccess)
        return out
    }

    static func ecb(_ words: [UInt32], key: [UInt32]) -> [UInt32] {
        self.words(aes(bytes(words), key: key, options: CCOptions(kCCOptionECBMode)))
    }

    static func ctr(_ data: [UInt8], key: [UInt32], nonce: [UInt32]) -> [UInt8] {
        let keyBytes = bytes(key)
        let iv = bytes([nonce[0], nonce[1], 0, 0])
        var cryptor: CCCryptorRef?
        CCCryptorCreateWithMode(CCOperation(kCCEncrypt), CCMode(kCCModeCTR), CCAlgorithm(kCCAlgorithmAES128),
                                CCPadding(ccNoPadding), iv, keyBytes, keyBytes.count, nil, 0, 0,
                                CCModeOptions(kCCModeOptionCTR_BE), &cryptor)
        var out = [UInt8](repeating: 0, count: data.count)
        var moved = 0
        CCCryptorUpdate(cryptor, data, data.count, &out, out.count, &moved)
        CCCryptorRelease(cryptor)
        return out
    }

    static func b64(_ bytes: [UInt8]) -> String {
        Data(bytes).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    static func attributes(name: String, key: [UInt32]) -> String {
        var plain = Array("MEGA{\"n\":\"\(name)\"}".utf8)
        while plain.count % 16 != 0 { plain.append(0) }
        return b64(aes(plain, key: key, options: 0, iv: [UInt8](repeating: 0, count: 16)))
    }

    /// MEGA's meta-MAC the slow, obvious way: one AES call per block.
    static func naiveMetaMAC(_ data: [UInt8], key: [UInt32], nonce: [UInt32]) -> [UInt32] {
        var fileMac: [UInt32] = [0, 0, 0, 0]
        var offset = 0
        var index = 0
        while offset < data.count {
            let length = min(min(index + 1, 8) * 131_072, data.count - offset)
            var chunkMac = [nonce[0], nonce[1], nonce[0], nonce[1]]
            var position = offset
            while position < offset + length {
                var block = Array(data[position..<min(position + 16, offset + length)])
                while block.count < 16 { block.append(0) }
                let blockWords = words(block)
                chunkMac = ecb((0..<4).map { chunkMac[$0] ^ blockWords[$0] }, key: key)
                position += 16
            }
            fileMac = ecb((0..<4).map { fileMac[$0] ^ chunkMac[$0] }, key: key)
            offset += length
            index += 1
        }
        return [fileMac[0] ^ fileMac[1], fileMac[2] ^ fileMac[3]]
    }

    /// The 8-word key a file node carries.
    static func fileKey(content k: [UInt32], nonce n: [UInt32], mac m: [UInt32]) -> [UInt32] {
        [k[0] ^ n[0], k[1] ^ n[1], k[2] ^ m[0], k[3] ^ m[1], n[0], n[1], m[0], m[1]]
    }
}

/// Shared, lock-protected state a scripted server reads.
private final class Box<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: Value
    init(_ value: Value) { stored = value }
    var value: Value {
        get { lock.lock(); defer { lock.unlock() }; return stored }
        set { lock.lock(); stored = newValue; lock.unlock() }
    }
}

@Suite("MEGA data safety")
struct MegaSafetyTests {

    static let masterKey: [UInt32] = [0x0102_0304, 0x0506_0708, 0x090A_0B0C, 0x0D0E_0F10]
    static let ownHandle = "OWNUSER0001"

    private static func root() -> String {
        #"{"h":"ROOT","p":"","u":"\#(ownHandle)","t":2,"a":"","k":"","ts":0}"#
    }

    /// A file of ours, its key wrapped with the master key under our handle.
    private static func ownFile(handle: String, name: String, content: [UInt8], key: [UInt32], nonce: [UInt32]) -> String {
        let mac = TestMega.naiveMetaMAC(content, key: key, nonce: nonce)
        let full = TestMega.fileKey(content: key, nonce: nonce, mac: mac)
        let wrapped = TestMega.b64(TestMega.bytes(TestMega.ecb(full, key: masterKey)))
        return #"{"h":"\#(handle)","p":"ROOT","u":"\#(ownHandle)","t":0,"s":\#(content.count),"ts":1700000000,"a":"\#(TestMega.attributes(name: name, key: key))","k":"\#(ownHandle):\#(wrapped)"}"#
    }

    private func client(host: String, maxAge: TimeInterval, handler: @escaping ProviderStubURLProtocol.Handler) -> MegaAPIClient {
        MegaAPIClient(
            credentials: MegaCredentials(sessionId: "sid", masterKey: Self.masterKey, email: "u@example.com"),
            session: ProviderStubURLProtocol.session(host: host, handler: handler),
            apiURL: "https://\(host)/cs",
            nodeTreeMaxAge: maxAge
        )
    }

    private static func command(_ body: Data?) -> [String: Any] {
        guard let body, let array = try? JSONSerialization.jsonObject(with: body) as? [[String: Any]] else { return [:] }
        return array.first ?? [:]
    }

    // MARK: - Freshness

    /// The tree was fetched once per session, so a change made on the web or
    /// a phone stayed invisible — and a sync planned from it — until restart.
    @Test("Changes made elsewhere show up once the tree is older than its maximum age")
    func treeIsRefetched() async throws {
        let key: [UInt32] = [1, 2, 3, 4], nonce: [UInt32] = [5, 6]
        let fileA = Self.ownFile(handle: "A", name: "a.txt", content: Array("a".utf8), key: key, nonce: nonce)
        let fileB = Self.ownFile(handle: "B", name: "b.txt", content: Array("b".utf8), key: key, nonce: nonce)
        let tree = Box([Self.root(), fileA])
        let handler: ProviderStubURLProtocol.Handler = { _, body in
            guard Self.command(body)["a"] as? String == "f" else { return .json("[0]") }
            return .json(#"[{"f":["# + tree.value.joined(separator: ",") + "]}]")
        }

        let fresh = client(host: "mega-fresh.test", maxAge: 0, handler: handler)
        #expect(try await fresh.listFolder(path: "/").map(\.name) == ["a.txt"])
        tree.value = [Self.root(), fileA, fileB]
        #expect(try await fresh.listFolder(path: "/").map(\.name) == ["a.txt", "b.txt"])

        // Within the maximum age one fetch serves every listing.
        tree.value = [Self.root(), fileA]
        let cached = client(host: "mega-cached.test", maxAge: 3600, handler: handler)
        _ = try await cached.listFolder(path: "/")
        tree.value = [Self.root(), fileA, fileB]
        #expect(try await cached.listFolder(path: "/").map(\.name) == ["a.txt"])
        let fetches = ProviderStubURLProtocol.requests(for: "mega-cached.test").filter { Self.command($0.body)["a"] as? String == "f" }
        #expect(fetches.count == 1)
    }

    // MARK: - Keys

    /// A file a collaborator put into a folder this account shares carries
    /// the collaborator's key part first. Taking it named the file "Unknown"
    /// and downloaded it as garbage.
    @Test("Shared-folder nodes open with the share key; nodes without a usable key are left out")
    func sharedFolderKeys() async throws {
        let shareKey: [UInt32] = [0xAAAA_AAAA, 0xBBBB_BBBB, 0xCCCC_CCCC, 0xDDDD_DDDD]
        let otherMaster: [UInt32] = [9, 9, 9, 9]
        let folderKey: [UInt32] = [11, 12, 13, 14]
        let fileKey: [UInt32] = [21, 22, 23, 24], nonce: [UInt32] = [25, 26]
        let fullFileKey = TestMega.fileKey(content: fileKey, nonce: nonce, mac: [0, 0])

        let wrappedShareKey = TestMega.b64(TestMega.bytes(TestMega.ecb(shareKey, key: Self.masterKey)))
        let folder = #"{"h":"SHR","p":"ROOT","u":"\#(Self.ownHandle)","t":1,"ts":0,"a":"\#(TestMega.attributes(name: "Team", key: folderKey))","k":"\#(Self.ownHandle):\#(TestMega.b64(TestMega.bytes(TestMega.ecb(folderKey, key: Self.masterKey))))"}"#
        let byOther = "OTHER000001:" + TestMega.b64(TestMega.bytes(TestMega.ecb(fullFileKey, key: otherMaster)))
        let byShare = "SHR:" + TestMega.b64(TestMega.bytes(TestMega.ecb(fullFileKey, key: shareKey)))
        let sharedFile = #"{"h":"F1","p":"SHR","u":"OTHER000001","t":0,"s":1,"ts":0,"a":"\#(TestMega.attributes(name: "report.pdf", key: fileKey))","k":"\#(byOther)/\#(byShare)"}"#
        let unreadable = #"{"h":"F2","p":"SHR","u":"OTHER000001","t":0,"s":1,"ts":0,"a":"\#(TestMega.attributes(name: "secret.pdf", key: fileKey))","k":"\#(byOther)"}"#
        let tree = [Self.root(), folder, sharedFile, unreadable].joined(separator: ",")

        let client = client(host: "mega-share.test", maxAge: 3600) { _, body in
            guard Self.command(body)["a"] as? String == "f" else { return .json("[0]") }
            return .json(#"[{"f":[\#(tree)],"ok":[{"h":"SHR","k":"\#(wrappedShareKey)","ha":"x"}]}]"#)
        }
        let names = try await client.listFolder(path: "/Team").map(\.name)
        #expect(names == ["report.pdf"])
        #expect(!names.contains("Unknown"))
    }

    @Test("A key part that fails the MEGA check is never taken")
    func wrongKeyIsRejected() {
        let rightKey: [UInt32] = [1, 2, 3, 4]
        let attr = TestMega.attributes(name: "x.txt", key: rightKey)
        let wrapped = TestMega.b64(TestMega.bytes(TestMega.ecb(rightKey, key: [7, 7, 7, 7])))
        let result = MegaAPIClient.decryptNodeAttributes(
            attr: attr, keyStr: "\(Self.ownHandle):\(wrapped)", ownHandle: Self.ownHandle,
            shareKeys: [:], masterKey: Self.masterKey
        )
        #expect(result == nil)
    }

    // MARK: - Downloads

    @Test("The fast MAC matches the block-by-block one across chunk boundaries")
    func macMatchesNaive() throws {
        let key: [UInt32] = [0x0011_2233, 0x4455_6677, 0x8899_AABB, 0xCCDD_EEFF]
        let nonce: [UInt32] = [0x0102_0304, 0x0506_0708]
        for size in [0, 1, 15, 16, 17, 131_072, 131_073, 2_621_447] {
            let data = (0..<size).map { UInt8(truncatingIfNeeded: $0 &* 31 &+ 7) }
            #expect(try MegaFileMAC.metaMAC(of: Data(data), key: key, nonce: nonce) == TestMega.naiveMetaMAC(data, key: key, nonce: nonce), "size \(size)")
        }

        // The same input through the Windows port's MegaMac, written
        // independently: D1B0189428BECC63.
        let data = (0..<2_621_447).map { UInt8(truncatingIfNeeded: $0 &* 31 &+ 7) }
        #expect(try MegaFileMAC.metaMAC(of: Data(data), key: key, nonce: nonce) == [0xD1B0_1894, 0x28BE_CC63])
    }

    @Test("A download that fails its MAC check throws; zero-MAC files from FileFluss still open")
    func macIsVerified() throws {
        let key: [UInt32] = [1, 2, 3, 4], nonce: [UInt32] = [5, 6]
        let plain = Array("hello mega".utf8)
        let cipher = Data(TestMega.ctr(plain, key: key, nonce: nonce))
        let mac = TestMega.naiveMetaMAC(plain, key: key, nonce: nonce)

        let good = TestMega.fileKey(content: key, nonce: nonce, mac: mac)
        #expect(try MegaAPIClient.decryptFile(cipher, fullKey: good) == Data(plain))

        var tampered = cipher
        tampered[0] ^= 0xFF
        #expect(throws: CloudProviderError.self) { try MegaAPIClient.decryptFile(tampered, fullKey: good) }

        let zeroMac = TestMega.fileKey(content: key, nonce: nonce, mac: [0, 0])
        #expect(try MegaAPIClient.decryptFile(cipher, fullKey: zeroMac) == Data(plain))
    }

    @Test("A download decrypts and lands; a corrupted one leaves nothing behind")
    func downloadEndToEnd() async throws {
        let key: [UInt32] = [31, 32, 33, 34], nonce: [UInt32] = [35, 36]
        let plain = (0..<300_000).map { UInt8(truncatingIfNeeded: $0) }
        let file = Self.ownFile(handle: "D1", name: "big.bin", content: plain, key: key, nonce: nonce)
        let tree = [Self.root(), file].joined(separator: ",")
        let cipher = Box(TestMega.ctr(plain, key: key, nonce: nonce))
        let host = "mega-download.test"
        let client = client(host: host, maxAge: 3600) { request, body in
            if request.url!.path == "/dl" { return .init(status: 200, body: Data(cipher.value)) }
            switch Self.command(body)["a"] as? String {
            case "f": return .json(#"[{"f":[\#(tree)]}]"#)
            case "g": return .json(#"[{"g":"https://\#(host)/dl","s":300000}]"#)
            default: return .json("[0]")
            }
        }

        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mega-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let good = directory.appendingPathComponent("big.bin")
        try await client.downloadFile(remotePath: "/big.bin", to: good)
        #expect(try Data(contentsOf: good) == Data(plain))

        var corrupted = cipher.value
        corrupted[1000] ^= 0x01
        cipher.value = corrupted
        let bad = directory.appendingPathComponent("bad.bin")
        await #expect(throws: CloudProviderError.self) {
            try await client.downloadFile(remotePath: "/big.bin", to: bad)
        }
        #expect(!FileManager.default.fileExists(atPath: bad.path))
    }
}
