import XCTest
@testable import FileFlussCore

/// End-to-end smoke test: spin up WebDAVServer backed by an in-memory
/// CloudProvider stub, then drive it from URLSession the way `mount_webdav`
/// would. Verifies the request/response shapes for the methods Finder uses
/// (OPTIONS, PROPFIND, GET, PUT, MKCOL, DELETE, MOVE).
final class WebDAVServerTests: XCTestCase {
    private var server: WebDAVServer!
    private var stub: StubProvider!
    private var port: UInt16 = 0

    override func setUp() async throws {
        stub = StubProvider()
        // Seed: /hello.txt + /folder/inside.txt
        await stub.seedFile(path: "/hello.txt", contents: Data("hi".utf8))
        await stub.seedFolder(path: "/folder")
        await stub.seedFile(path: "/folder/inside.txt", contents: Data("nested".utf8))
        server = WebDAVServer(provider: stub)
        port = try await server.start()
    }

    override func tearDown() async throws {
        await server.stop()
        server = nil
        stub = nil
    }

    // MARK: - OPTIONS

    func testOPTIONSAdvertisesDAVCapability() async throws {
        let (_, response) = try await send(method: "OPTIONS", path: "/")
        XCTAssertEqual(response.statusCode, 200)
        XCTAssertTrue((response.value(forHTTPHeaderField: "DAV") ?? "").contains("1"))
        XCTAssertTrue((response.value(forHTTPHeaderField: "Allow") ?? "").contains("PROPFIND"))
    }

    // MARK: - PROPFIND

    func testPROPFINDRootListsChildren() async throws {
        let (data, response) = try await send(method: "PROPFIND", path: "/", headers: ["Depth": "1"])
        XCTAssertEqual(response.statusCode, 207)
        let body = String(data: data, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("<D:multistatus"), "expected multistatus, got: \(body.prefix(200))")
        XCTAssertTrue(body.contains("hello.txt"), "root listing missing hello.txt")
        XCTAssertTrue(body.contains("/folder/"), "root listing missing folder href")
        XCTAssertTrue(body.contains("<D:collection/>"), "folder should report as collection")
    }

    func testPROPFINDOnFileReturnsOneEntry() async throws {
        let (data, response) = try await send(method: "PROPFIND", path: "/hello.txt", headers: ["Depth": "0"])
        XCTAssertEqual(response.statusCode, 207)
        let body = String(data: data, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("hello.txt"))
        XCTAssertTrue(body.contains("<D:getcontentlength>2</D:getcontentlength>"))
    }

    func testPROPFINDMissingPathIs404() async throws {
        let (_, response) = try await send(method: "PROPFIND", path: "/missing.txt", headers: ["Depth": "0"])
        XCTAssertEqual(response.statusCode, 404)
    }

    // MARK: - GET / HEAD

    func testGETStreamsBytes() async throws {
        let (data, response) = try await send(method: "GET", path: "/hello.txt")
        XCTAssertEqual(response.statusCode, 200)
        XCTAssertEqual(data, Data("hi".utf8))
        XCTAssertEqual(response.value(forHTTPHeaderField: "Content-Length"), "2")
    }

    func testHEADReturnsMetadataOnly() async throws {
        let (data, response) = try await send(method: "HEAD", path: "/hello.txt")
        XCTAssertEqual(response.statusCode, 200)
        XCTAssertEqual(data.count, 0, "HEAD response shouldn't carry a body")
        XCTAssertEqual(response.value(forHTTPHeaderField: "Content-Length"), "2")
    }

    // MARK: - PUT

    func testPUTCreatesFile() async throws {
        let payload = Data("written via PUT".utf8)
        let (_, response) = try await send(method: "PUT", path: "/created.txt", body: payload)
        XCTAssertEqual(response.statusCode, 201)
        let exists = await stub.contents(at: "/created.txt")
        XCTAssertEqual(exists, payload)
    }

    func testPUTDropsAppleSidecarFiles() async throws {
        _ = try await send(method: "PUT", path: "/.DS_Store", body: Data([0]))
        _ = try await send(method: "PUT", path: "/._ghost", body: Data([0]))
        let dsStore = await stub.contents(at: "/.DS_Store")
        let ghost = await stub.contents(at: "/._ghost")
        XCTAssertNil(dsStore, ".DS_Store should not be forwarded to the provider")
        XCTAssertNil(ghost, "._* AppleDouble sidecar should not be forwarded")
    }

    // MARK: - MKCOL / DELETE

    func testMKCOLCreatesFolder() async throws {
        let (_, response) = try await send(method: "MKCOL", path: "/newdir/")
        XCTAssertEqual(response.statusCode, 201)
        let isDir = await stub.isDirectory(at: "/newdir")
        XCTAssertTrue(isDir)
    }

    func testDELETERemovesFile() async throws {
        let (_, response) = try await send(method: "DELETE", path: "/hello.txt")
        XCTAssertEqual(response.statusCode, 204)
        let after = await stub.contents(at: "/hello.txt")
        XCTAssertNil(after)
    }

    // MARK: - MOVE

    func testMOVERenameWithinSameFolder() async throws {
        let dest = "http://127.0.0.1:\(port)/hello_renamed.txt"
        let (_, response) = try await send(method: "MOVE", path: "/hello.txt", headers: ["Destination": dest])
        XCTAssertEqual(response.statusCode, 201)
        let original = await stub.contents(at: "/hello.txt")
        let moved = await stub.contents(at: "/hello_renamed.txt")
        XCTAssertNil(original)
        XCTAssertEqual(moved, Data("hi".utf8))
    }

    func testMOVECrossDirectory() async throws {
        let dest = "http://127.0.0.1:\(port)/folder/hello.txt"
        let (_, response) = try await send(method: "MOVE", path: "/hello.txt", headers: ["Destination": dest])
        XCTAssertEqual(response.statusCode, 201)
        let original = await stub.contents(at: "/hello.txt")
        let moved = await stub.contents(at: "/folder/hello.txt")
        XCTAssertNil(original)
        XCTAssertEqual(moved, Data("hi".utf8))
    }

    // MARK: - LOCK / UNLOCK

    func testLOCKReturnsToken() async throws {
        let (data, response) = try await send(method: "LOCK", path: "/hello.txt")
        XCTAssertEqual(response.statusCode, 200)
        let lockToken = response.value(forHTTPHeaderField: "Lock-Token") ?? ""
        XCTAssertTrue(lockToken.hasPrefix("<urn:uuid:"), "Lock-Token should be an opaque token, got: \(lockToken)")
        let body = String(data: data, encoding: .utf8) ?? ""
        XCTAssertTrue(body.contains("<D:locktoken>"))
    }

    func testUNLOCKReturns204() async throws {
        let (_, response) = try await send(method: "UNLOCK", path: "/hello.txt", headers: ["Lock-Token": "<urn:uuid:test>"])
        XCTAssertEqual(response.statusCode, 204)
    }

    // MARK: - HTTP helper

    private func send(
        method: String,
        path: String,
        headers: [String: String] = [:],
        body: Data? = nil
    ) async throws -> (Data, HTTPURLResponse) {
        var request = URLRequest(url: URL(string: "http://127.0.0.1:\(port)\(path)")!)
        request.httpMethod = method
        request.httpBody = body
        for (k, v) in headers { request.setValue(v, forHTTPHeaderField: k) }
        let (data, response) = try await URLSession.shared.data(for: request)
        return (data, response as! HTTPURLResponse)
    }
}

// MARK: - In-memory CloudProvider stub

/// Minimal in-memory CloudProvider for testing. Stores files as Data keyed
/// by full provider path; folders are tracked separately so listing knows
/// what to return for a directory query.
private actor StubProvider: CloudProvider {
    nonisolated var providerType: CloudProviderType { .webDAV }
    nonisolated var maxUploadFileSize: Int64? { get async { nil } }

    private var files: [String: Data] = [:]
    private var folders: Set<String> = ["/"]
    private var modDates: [String: Date] = [:]

    func seedFile(path: String, contents: Data) {
        files[path] = contents
        modDates[path] = Date()
        // Ensure parents exist as folders.
        var parent = (path as NSString).deletingLastPathComponent
        while !parent.isEmpty {
            folders.insert(parent.isEmpty ? "/" : parent)
            if parent == "/" { break }
            parent = (parent as NSString).deletingLastPathComponent
            if parent.isEmpty { folders.insert("/") }
        }
    }

    func seedFolder(path: String) {
        folders.insert(path)
        modDates[path] = Date()
    }

    func contents(at path: String) -> Data? { files[path] }
    func isDirectory(at path: String) -> Bool { folders.contains(path) }

    // MARK: CloudProvider conformance

    func authenticate() async throws {}
    func disconnect() async throws {}
    var isAuthenticated: Bool { get async { true } }

    func listDirectory(at path: String) async throws -> [CloudFileItem] {
        let dir = path == "" ? "/" : path
        guard folders.contains(dir) else { throw CloudProviderError.notFound(dir) }
        let prefix = dir == "/" ? "/" : dir + "/"
        var items: [CloudFileItem] = []
        for (filePath, data) in files where filePath.hasPrefix(prefix) {
            let rest = String(filePath.dropFirst(prefix.count))
            if rest.contains("/") { continue }  // only direct children
            items.append(CloudFileItem(id: filePath, name: rest, path: filePath,
                                       isDirectory: false, size: Int64(data.count),
                                       modificationDate: modDates[filePath] ?? Date(), checksum: nil))
        }
        for folderPath in folders where folderPath != dir && folderPath.hasPrefix(prefix) {
            let rest = String(folderPath.dropFirst(prefix.count))
            if rest.contains("/") { continue }
            items.append(CloudFileItem(id: folderPath, name: rest, path: folderPath,
                                       isDirectory: true, size: 0,
                                       modificationDate: modDates[folderPath] ?? Date(), checksum: nil))
        }
        return items
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        guard let data = files[remotePath] else { throw CloudProviderError.notFound(remotePath) }
        try? FileManager.default.removeItem(at: localURL)
        try data.write(to: localURL)
    }

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL)
        if let onBytes { onBytes(Int64(files[remotePath]?.count ?? 0)) }
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        files[remotePath] = try Data(contentsOf: localURL)
        modDates[remotePath] = Date()
    }

    func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        try await uploadFile(from: localURL, to: remotePath)
    }

    func deleteItem(at path: String) async throws {
        if files.removeValue(forKey: path) != nil { return }
        if folders.remove(path) != nil { return }
        throw CloudProviderError.notFound(path)
    }

    func createDirectory(at path: String) async throws {
        folders.insert(path)
        modDates[path] = Date()
    }

    func renameItem(at path: String, to newName: String) async throws {
        let parent = (path as NSString).deletingLastPathComponent
        let dest = parent == "/" ? "/\(newName)" : "\(parent)/\(newName)"
        if let data = files.removeValue(forKey: path) {
            files[dest] = data
            modDates[dest] = Date()
        } else if folders.remove(path) != nil {
            folders.insert(dest)
        } else {
            throw CloudProviderError.notFound(path)
        }
    }

    func moveItem(at path: String, toPath newPath: String) async throws {
        if let data = files.removeValue(forKey: path) {
            files[newPath] = data
            modDates[newPath] = Date()
        } else if folders.remove(path) != nil {
            folders.insert(newPath)
        } else {
            throw CloudProviderError.notFound(path)
        }
    }

    func getFileMetadata(at path: String) async throws -> CloudFileItem {
        if let data = files[path] {
            return CloudFileItem(id: path, name: (path as NSString).lastPathComponent, path: path,
                                 isDirectory: false, size: Int64(data.count),
                                 modificationDate: modDates[path] ?? Date(), checksum: nil)
        }
        if folders.contains(path) {
            return CloudFileItem(id: path, name: (path as NSString).lastPathComponent, path: path,
                                 isDirectory: true, size: 0,
                                 modificationDate: modDates[path] ?? Date(), checksum: nil)
        }
        throw CloudProviderError.notFound(path)
    }

    func folderSize(at path: String) async throws -> Int64 {
        let items = try await listDirectory(at: path)
        var total: Int64 = 0
        for item in items {
            if item.isDirectory { total += try await folderSize(at: item.path) }
            else { total += item.size }
        }
        return total
    }

    func storageQuota() async throws -> CloudStorageQuota? { nil }
}
