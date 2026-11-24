import Testing
import Foundation
@testable import FileFlussCore

/// Google Drive's path→id cache, shared drives and Google's own document
/// types, each pinned against a scripted Drive API.
@Suite("Google Drive data safety")
struct GoogleDriveSafetyTests {

    private static let exportExtensions = [
        "application/vnd.google-apps.document": "docx",
        "application/vnd.google-apps.spreadsheet": "xlsx",
        "application/vnd.google-apps.presentation": "pptx",
        "application/vnd.google-apps.drawing": "pdf",
    ]

    private func client(host: String, handler: @escaping ProviderStubURLProtocol.Handler) -> GoogleDriveAPIClient {
        GoogleDriveAPIClient(
            credentials: GoogleDriveCredentials(
                accessToken: "token", refreshToken: "refresh",
                expiresAt: Date().addingTimeInterval(3600), userEmail: "u@example.com", displayName: "U"
            ),
            session: ProviderStubURLProtocol.session(host: host, handler: handler),
            apiHost: host
        )
    }

    private static func query(_ request: URLRequest) -> [String: String] {
        let items = URLComponents(url: request.url!, resolvingAgainstBaseURL: false)?.queryItems ?? []
        return Dictionary(items.map { ($0.name, $0.value ?? "") }, uniquingKeysWith: { $1 })
    }

    private static func files(_ entries: [String]) -> ProviderStubURLProtocol.Reply {
        .json(#"{"files":["# + entries.joined(separator: ",") + "]}")
    }

    private static func file(_ id: String, _ name: String, _ mime: String = "text/plain", size: Int? = 3) -> String {
        let sizePart = size.map { #","size":"\#($0)""# } ?? ""
        return #"{"id":"\#(id)","name":"\#(name)","mimeType":"\#(mime)"\#(sizePart),"modifiedTime":"2026-01-01T00:00:00.000Z"}"#
    }

    private static let notFound = ProviderStubURLProtocol.Reply.json(#"{"error":{"code":404,"message":"File not found"}}"#, status: 404)

    private func temporaryFile(_ contents: String = "abc") throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("gd-\(UUID().uuidString).txt")
        try Data(contents.utf8).write(to: url)
        return url
    }

    // MARK: - Workspace naming

    @Test("Google documents list under their exported names; unexportable types are left out")
    func workspaceNaming() {
        let ext = Self.exportExtensions
        #expect(GoogleWorkspaceNaming.listedName(name: "Doc", mimeType: "application/vnd.google-apps.document", exportExtensions: ext) == "Doc.docx")
        #expect(GoogleWorkspaceNaming.listedName(name: "Report.docx", mimeType: "application/vnd.google-apps.document", exportExtensions: ext) == "Report.docx")
        #expect(GoogleWorkspaceNaming.listedName(name: "Budget", mimeType: "application/vnd.google-apps.spreadsheet", exportExtensions: ext) == "Budget.xlsx")
        #expect(GoogleWorkspaceNaming.listedName(name: "Survey", mimeType: "application/vnd.google-apps.form", exportExtensions: ext) == nil)
        #expect(GoogleWorkspaceNaming.listedName(name: "Link", mimeType: "application/vnd.google-apps.shortcut", exportExtensions: ext) == nil)
        #expect(GoogleWorkspaceNaming.listedName(name: "Folder", mimeType: "application/vnd.google-apps.folder", exportExtensions: ext) == "Folder")
        #expect(GoogleWorkspaceNaming.listedName(name: "scan.pdf", mimeType: "application/pdf", exportExtensions: ext) == "scan.pdf")

        let origin = GoogleWorkspaceNaming.exportedOrigin(of: "Doc.docx", exportExtensions: ext)
        #expect(origin?.name == "Doc")
        #expect(origin?.mimeTypes == ["application/vnd.google-apps.document"])
        #expect(GoogleWorkspaceNaming.exportedOrigin(of: "photo.jpg", exportExtensions: ext) == nil)
    }

    /// The listing and the download must agree on the name, or a sync
    /// re-downloads every Doc on every run and deletes `Doc.docx` as extra.
    @Test("A Doc listed as Doc.docx downloads to exactly the path asked for")
    func workspaceListingAndDownloadAgree() async throws {
        let client = client(host: "gd-workspace.test") { request, _ in
            let path = request.url!.path
            if path.hasSuffix("/drives") { return .json(#"{"drives":[]}"#) }
            if path.hasSuffix("/files/doc1/export") { return .status(200, "exported-bytes") }
            if path.hasSuffix("/files") {
                return Self.files([
                    Self.file("doc1", "Doc", "application/vnd.google-apps.document", size: nil),
                    Self.file("form1", "Survey", "application/vnd.google-apps.form", size: nil),
                    Self.file("short1", "Link", "application/vnd.google-apps.shortcut", size: nil),
                    Self.file("txt1", "a.txt"),
                ])
            }
            return Self.notFound
        }
        let names = try await client.listFolder(path: "/").map(\.name)
        #expect(names == ["Doc.docx", "a.txt"])

        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("gd-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let target = directory.appendingPathComponent("Doc.docx")
        try await client.downloadFile(remotePath: "/Doc.docx", to: target)
        #expect(FileManager.default.fileExists(atPath: target.path))
        #expect(!FileManager.default.fileExists(atPath: target.path + ".docx"))
        #expect(try String(contentsOf: target, encoding: .utf8) == "exported-bytes")
    }

    // MARK: - Stale cached ids

    /// The file was deleted on the web after it was listed. Every upload used
    /// to PATCH the dead id and fail until the app restarted.
    @Test("An upload over a file deleted on the web creates it anew")
    func uploadAfterWebDelete() async throws {
        let host = "gd-deleted.test"
        let client = client(host: host) { request, _ in
            let path = request.url!.path
            let method = request.httpMethod ?? "GET"
            if path.hasSuffix("/drives") { return .json(#"{"drives":[]}"#) }
            if method == "GET", path.hasSuffix("/drive/v3/files") {
                // The root listing knows a.txt; the by-name lookup no longer does.
                return Self.query(request)["q"]?.contains("name =") == true ? Self.files([]) : Self.files([Self.file("dead", "a.txt")])
            }
            if path.hasSuffix("/files/dead") { return Self.notFound }
            if method == "POST", path.hasSuffix("/upload/drive/v3/files") { return .json(Self.file("new1", "a.txt")) }
            return Self.notFound
        }
        _ = try await client.listFolder(path: "/")
        let local = try temporaryFile()
        defer { try? FileManager.default.removeItem(at: local) }
        try await client.uploadFile(from: local, to: "/a.txt")

        let requests = ProviderStubURLProtocol.requests(for: host)
        #expect(!requests.contains { $0.method == "PATCH" && $0.url.path.contains("/upload/drive/v3/files/dead") })
        #expect(requests.contains { $0.method == "POST" && $0.url.path.hasSuffix("/upload/drive/v3/files") })
    }

    /// Trashed on the web: the PATCH would have succeeded into the trash and
    /// the sync would never converge.
    @Test("An upload never goes into a trashed file")
    func uploadAfterWebTrash() async throws {
        let host = "gd-trashed.test"
        let client = client(host: host) { request, _ in
            let path = request.url!.path
            let method = request.httpMethod ?? "GET"
            if path.hasSuffix("/drives") { return .json(#"{"drives":[]}"#) }
            if method == "GET", path.hasSuffix("/drive/v3/files") {
                return Self.query(request)["q"]?.contains("name =") == true ? Self.files([]) : Self.files([Self.file("old", "a.txt")])
            }
            if method == "GET", path.hasSuffix("/files/old") {
                return .json(#"{"id":"old","name":"a.txt","mimeType":"text/plain","trashed":true,"parents":["x"]}"#)
            }
            if method == "POST", path.hasSuffix("/upload/drive/v3/files") { return .json(Self.file("new1", "a.txt")) }
            return Self.notFound
        }
        _ = try await client.listFolder(path: "/")
        let local = try temporaryFile()
        defer { try? FileManager.default.removeItem(at: local) }
        try await client.uploadFile(from: local, to: "/a.txt")

        let requests = ProviderStubURLProtocol.requests(for: host)
        #expect(!requests.contains { $0.method == "PATCH" && $0.url.path.contains("/files/old") })
        #expect(requests.contains { $0.method == "POST" && $0.url.path.hasSuffix("/upload/drive/v3/files") })
    }

    @Test("An upload over a live cached file still replaces it in place")
    func uploadOverLiveFile() async throws {
        let host = "gd-live.test"
        let client = client(host: host) { request, _ in
            let path = request.url!.path
            let method = request.httpMethod ?? "GET"
            if path.hasSuffix("/drives") { return .json(#"{"drives":[]}"#) }
            if method == "GET", path.hasSuffix("/drive/v3/files") { return Self.files([Self.file("live", "a.txt")]) }
            if method == "GET", path.hasSuffix("/files/live") {
                return .json(#"{"id":"live","name":"a.txt","mimeType":"text/plain","trashed":false,"parents":["0AROOT"]}"#)
            }
            if method == "PATCH" { return .json(Self.file("live", "a.txt")) }
            return Self.notFound
        }
        _ = try await client.listFolder(path: "/")
        let local = try temporaryFile()
        defer { try? FileManager.default.removeItem(at: local) }
        try await client.uploadFile(from: local, to: "/a.txt")

        let requests = ProviderStubURLProtocol.requests(for: host)
        #expect(requests.contains { $0.method == "PATCH" && $0.url.path.hasSuffix("/upload/drive/v3/files/live") })
        #expect(!requests.contains { $0.method == "POST" })
    }

    /// The folder was deleted and recreated on the web. Its old id lists as
    /// empty rather than failing, and a Mirror from it would delete the
    /// destination.
    @Test("A folder whose cached id went stale is looked up again, not listed as empty")
    func staleFolderListing() async throws {
        let client = client(host: "gd-stale-folder.test") { request, _ in
            let path = request.url!.path
            let q = Self.query(request)["q"] ?? ""
            if path.hasSuffix("/drives") { return .json(#"{"drives":[]}"#) }
            if path.hasSuffix("/files/oldF") { return Self.notFound }
            if path.hasSuffix("/drive/v3/files") {
                if q.hasPrefix("'root' in parents and name =") { return Self.files([Self.file("newF", "Photos", "application/vnd.google-apps.folder")]) }
                if q.hasPrefix("'root' in parents") { return Self.files([Self.file("oldF", "Photos", "application/vnd.google-apps.folder")]) }
                if q.hasPrefix("'oldF' in parents") { return Self.files([]) }
                if q.hasPrefix("'newF' in parents") { return Self.files([Self.file("p1", "x.jpg")]) }
            }
            return Self.notFound
        }
        _ = try await client.listFolder(path: "/")
        let items = try await client.listFolder(path: "/Photos")
        #expect(items.map(\.name) == ["x.jpg"])
    }

    @Test("createFolder does not trust a cached id of a folder deleted on the web")
    func createFolderAfterWebDelete() async throws {
        let host = "gd-mkdir.test"
        let client = client(host: host) { request, _ in
            let path = request.url!.path
            let method = request.httpMethod ?? "GET"
            let q = Self.query(request)["q"] ?? ""
            if path.hasSuffix("/drives") { return .json(#"{"drives":[]}"#) }
            if path.hasSuffix("/files/deadF") { return Self.notFound }
            if method == "GET", path.hasSuffix("/files/newF") { return .json(Self.file("newF", "Docs", "application/vnd.google-apps.folder")) }
            if method == "POST", path.hasSuffix("/drive/v3/files") { return .json(Self.file("newF", "Docs", "application/vnd.google-apps.folder")) }
            if method == "GET", path.hasSuffix("/drive/v3/files") {
                return q.contains("name =") ? Self.files([]) : Self.files([Self.file("deadF", "Docs", "application/vnd.google-apps.folder")])
            }
            return Self.notFound
        }
        _ = try await client.listFolder(path: "/")
        try await client.createFolder(at: "/Docs")
        #expect(ProviderStubURLProtocol.requests(for: host).contains { $0.method == "POST" && $0.url.path.hasSuffix("/drive/v3/files") })
    }

    // MARK: - Shared drives

    /// A folder created or renamed on a shared drive must keep listing with
    /// the shared-drive corpus, or it comes back empty.
    @Test("Folders created and renamed on a shared drive keep their drive scope")
    func sharedDriveScopeSurvives() async throws {
        let client = client(host: "gd-shared.test") { request, _ in
            let path = request.url!.path
            let method = request.httpMethod ?? "GET"
            let query = Self.query(request)
            let q = query["q"] ?? ""
            if path.hasSuffix("/drives") { return .json(#"{"drives":[{"id":"D1","name":"Team"}]}"#) }
            if method == "POST", path.hasSuffix("/drive/v3/files") { return .json(Self.file("F1", "New", "application/vnd.google-apps.folder")) }
            if method == "PATCH", path.hasSuffix("/files/F1") { return .json(Self.file("F1", "Renamed", "application/vnd.google-apps.folder")) }
            if method == "GET", path.hasSuffix("/files/F1") { return .json(Self.file("F1", "New", "application/vnd.google-apps.folder")) }
            if method == "GET", path.hasSuffix("/drive/v3/files") {
                if q.contains("name =") { return Self.files([]) }
                if q.hasPrefix("'F1' in parents") {
                    // Drive only shows shared-drive items to a corpus-scoped query.
                    return query["driveId"] == "D1" && query["corpora"] == "drive"
                        ? Self.files([Self.file("c1", "inside.txt")])
                        : Self.files([])
                }
            }
            return Self.notFound
        }
        _ = try await client.listFolder(path: "/Shared drives")
        try await client.createFolder(at: "/Shared drives/Team/New")
        #expect(try await client.listFolder(path: "/Shared drives/Team/New").map(\.name) == ["inside.txt"])

        try await client.renameItem(at: "/Shared drives/Team/New", to: "Renamed")
        #expect(try await client.listFolder(path: "/Shared drives/Team/Renamed").map(\.name) == ["inside.txt"])
    }
}
