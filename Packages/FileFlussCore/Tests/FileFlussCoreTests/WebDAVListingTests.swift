import Testing
import Foundation
@testable import FileFlussCore

/// Reading a folder's contents out of a PROPFIND response.
///
/// Issue #64: a Hetzner Storage Box with two folders showed one. The
/// folder's own entry was removed by position — the first one — but the
/// parser had already dropped it, because the root's entry has no name of
/// its own when the server sends no `displayname`. So the positional drop
/// took a real folder instead.
@Suite("WebDAV listing")
struct WebDAVListingTests {

    private let base = "https://u123456.your-storagebox.de"

    private func body(_ responses: String) -> Data {
        Data("""
        <?xml version="1.0" encoding="utf-8"?>
        <d:multistatus xmlns:d="DAV:">
        \(responses)
        </d:multistatus>
        """.utf8)
    }

    private func folder(href: String, displayName: String?) -> String {
        let name = displayName.map { "<d:displayname>\($0)</d:displayname>" } ?? ""
        return """
          <d:response>
            <d:href>\(href)</d:href>
            <d:propstat><d:prop>\(name)
              <d:resourcetype><d:collection/></d:resourcetype>
            </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
        """
    }

    private func file(href: String, displayName: String?, length: Int) -> String {
        let name = displayName.map { "<d:displayname>\($0)</d:displayname>" } ?? ""
        return """
          <d:response>
            <d:href>\(href)</d:href>
            <d:propstat><d:prop>\(name)
              <d:getcontentlength>\(length)</d:getcontentlength>
              <d:resourcetype/>
            </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
        """
    }

    /// The reported case, exactly: a root whose own entry carries no
    /// displayname, and two folders under it.
    @Test("Every folder in the root is listed")
    func rootWithoutDisplayNameKeepsEveryChild() {
        let data = body(
            folder(href: "/", displayName: nil)
            + folder(href: "/Backups/", displayName: "Backups")
            + folder(href: "/Photos/", displayName: "Photos")
        )

        let items = WebDAVResponseParser.children(data: data, basePath: base, requestPath: "/")

        #expect(items.map(\.name) == ["Backups", "Photos"])
    }

    @Test("A root that does send a displayname is listed the same way")
    func rootWithDisplayName() {
        let data = body(
            folder(href: "/", displayName: "/")
            + folder(href: "/Backups/", displayName: "Backups")
            + folder(href: "/Photos/", displayName: "Photos")
        )

        let items = WebDAVResponseParser.children(data: data, basePath: base, requestPath: "/")

        #expect(items.map(\.name) == ["Backups", "Photos"])
    }

    /// Nothing in the spec says the folder comes first, and some servers
    /// put it last. Position was never a safe way to find it.
    @Test("The folder's own entry is found wherever it appears")
    func selfEntryLast() {
        let data = body(
            folder(href: "/Backups/", displayName: "Backups")
            + folder(href: "/Photos/", displayName: "Photos")
            + folder(href: "/", displayName: "/")
        )

        let items = WebDAVResponseParser.children(data: data, basePath: base, requestPath: "/")

        #expect(items.map(\.name) == ["Backups", "Photos"])
    }

    /// Removing something anyway, when there is nothing to remove, is how
    /// the bug worked.
    @Test("A server that omits the folder's entry loses nothing")
    func selfEntryAbsent() {
        let data = body(
            folder(href: "/Backups/", displayName: "Backups")
            + folder(href: "/Photos/", displayName: "Photos")
        )

        let items = WebDAVResponseParser.children(data: data, basePath: base, requestPath: "/")

        #expect(items.map(\.name) == ["Backups", "Photos"])
    }

    @Test("A subfolder lists its contents and not itself")
    func subfolderListing() {
        let data = body(
            folder(href: "/Backups/", displayName: "Backups")
            + file(href: "/Backups/january.tar", displayName: "january.tar", length: 42)
            + folder(href: "/Backups/2026/", displayName: "2026")
        )

        let items = WebDAVResponseParser.children(data: data, basePath: base, requestPath: "/Backups")

        #expect(items.map(\.name) == ["january.tar", "2026"])
        #expect(items.first?.size == 42)
    }

    /// Hrefs come percent-encoded, and the folder's own entry has to be
    /// recognised through that.
    @Test("An encoded folder name still matches its own entry")
    func percentEncodedSelfEntry() {
        let data = body(
            folder(href: "/My%20Files/", displayName: "My Files")
            + file(href: "/My%20Files/note.txt", displayName: "note.txt", length: 7)
        )

        let items = WebDAVResponseParser.children(data: data, basePath: base, requestPath: "/My Files")

        #expect(items.map(\.name) == ["note.txt"])
    }

    /// Some servers answer with absolute URLs rather than paths.
    @Test("An absolute URL in the href is matched too")
    func absoluteHrefs() {
        let data = body(
            folder(href: "\(base)/Backups/", displayName: "Backups")
            + file(href: "\(base)/Backups/january.tar", displayName: "january.tar", length: 1)
        )

        let items = WebDAVResponseParser.children(data: data, basePath: base, requestPath: "/Backups")

        #expect(items.map(\.name) == ["january.tar"])
    }

    @Test("A trailing slash is the same path")
    func trailingSlashesCompareEqual() {
        #expect(WebDAVResponseParser.normalisedPath("/Backups/") == "/Backups")
        #expect(WebDAVResponseParser.normalisedPath("/Backups") == "/Backups")
        #expect(WebDAVResponseParser.normalisedPath("/") == "/")
        #expect(WebDAVResponseParser.normalisedPath("") == "/")
        #expect(WebDAVResponseParser.normalisedPath("Backups") == "/Backups")
        #expect(WebDAVResponseParser.normalisedPath("/My%20Files") == "/My Files")
    }

    /// `parse` still returns everything: fetching metadata for one file is
    /// a Depth-0 PROPFIND, and the entry it wants is the one `children`
    /// exists to remove.
    @Test("Metadata lookups still see the item itself")
    func parseKeepsTheRequestedItem() {
        let data = body(file(href: "/Backups/january.tar", displayName: "january.tar", length: 99))

        let items = WebDAVResponseParser.parse(data: data, basePath: base, requestPath: "/Backups/january.tar")

        #expect(items.first?.name == "january.tar")
        #expect(items.first?.size == 99)
    }
}
