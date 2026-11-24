import Testing
import Foundation
@testable import FileFlussCore

/// Query values that name a file have to reach the server whole. With
/// `.urlQueryAllowed` or `URLQueryItem`, `&` split the query (deleting
/// `/Docs/Q&A` deleted `/Docs/Q`) and `+` was read as a space.
@Suite("Strict query encoding")
struct QueryEncodingTests {

    /// Decodes a query the way a server does, `+` as a space included.
    private func serverDecoded(_ query: String) -> [String: String] {
        var result: [String: String] = [:]
        for pair in query.split(separator: "&", omittingEmptySubsequences: false) {
            let parts = pair.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            func decode(_ s: Substring) -> String {
                s.replacingOccurrences(of: "+", with: " ").removingPercentEncoding ?? String(s)
            }
            result[decode(parts[0])] = parts.count > 1 ? decode(parts[1]) : ""
        }
        return result
    }

    private let nasty = "/Docs/Q&A/a+b=c; 50% #1 ?x ü 🎉.txt"

    @Test("Only unreserved ASCII characters stay as they are")
    func helper() {
        #expect(StrictQueryEncoding.encode("AZaz09-._~") == "AZaz09-._~")
        #expect(StrictQueryEncoding.encode("/a&b+c=d e") == "%2Fa%26b%2Bc%3Dd%20e")
        #expect(StrictQueryEncoding.encode("ü🎉") == "%C3%BC%F0%9F%8E%89")
        #expect(StrictQueryEncoding.query([("path", "/Q&A"), ("x", "1+1")]) == "path=%2FQ%26A&x=1%2B1")
        #expect(serverDecoded(StrictQueryEncoding.query([("path", nasty)])) == ["path": nasty])
    }

    @Test("Koofr: path and filename survive & + =")
    func koofr() {
        #expect(KoofrAPIClient.pathQuery("/Docs/Q&A") == "path=%2FDocs%2FQ%26A")
        #expect(serverDecoded(KoofrAPIClient.pathQuery(nasty)) == ["path": nasty])
        let upload = serverDecoded(KoofrAPIClient.uploadQuery(folderPath: "/Rock&Roll", fileName: "a+b&c=d.txt"))
        #expect(upload["path"] == "/Rock&Roll")
        #expect(upload["filename"] == "a+b&c=d.txt")
        #expect(upload["overwrite"] == "true")
        #expect(upload["autorename"] == "false")
        #expect(upload.count == 4)
    }

    @Test("Seafile: p= keeps its slashes but escapes everything else")
    func seafilePath() {
        #expect(SeafileAPIClient.queryEncoded("/lib/Q&A") == "/lib/Q%26A")
        #expect(SeafileAPIClient.queryEncoded("/a+b=c ü") == "/a%2Bb%3Dc%20%C3%BC")
        #expect(serverDecoded("p=" + SeafileAPIClient.queryEncoded(nasty)) == ["p": nasty])
    }

    @Test("Seafile: login and rename forms are strictly encoded")
    func seafileForms() {
        let login = serverDecoded(SeafileAPIClient.loginBody(username: "me+1@example.com", password: "p&ss=w+rd %"))
        #expect(login == ["username": "me+1@example.com", "password": "p&ss=w+rd %"])
        let rename = serverDecoded(SeafileAPIClient.renameBody(newName: "Q&A + notes=1.txt"))
        #expect(rename == ["operation": "rename", "newname": "Q&A + notes=1.txt"])
    }

    @Test("pCloud: the upload names the file and folder whole")
    func pcloudUpload() {
        let auth = URLQueryItem(name: "access_token", value: "tok+en/=")
        let query = PCloudAPIClient.uploadQuery(auth: auth, folderPath: "/Rock&Roll", fileName: "a&b.txt", mtime: 1700000000, ctime: nil)
        let decoded = serverDecoded(query)
        #expect(decoded == [
            "access_token": "tok+en/=",
            "path": "/Rock&Roll",
            "filename": "a&b.txt",
            "nopartial": "1",
            "mtime": "1700000000",
        ])
    }

    @Test("pCloud: API calls escape + and & in every value")
    func pcloudAPI() throws {
        let url = try #require(PCloudAPIClient.apiURL(
            baseURL: "https://eapi.pcloud.com",
            method: "deletefile",
            auth: URLQueryItem(name: "auth", value: "abc"),
            params: ["path": "/a+b & c.txt"]
        ))
        #expect(url.absoluteString == "https://eapi.pcloud.com/deletefile?auth=abc&path=%2Fa%2Bb%20%26%20c.txt")
    }

    /// `setModificationDate` used to encode the path itself before the
    /// request encoded it again, so a space reached pCloud as "%20".
    @Test("pCloud: a pre-encoded path would be encoded twice")
    func pcloudNoDoubleEncoding() throws {
        let url = try #require(PCloudAPIClient.apiURL(
            baseURL: "https://api.pcloud.com", method: "setfilemtime",
            auth: URLQueryItem(name: "auth", value: "abc"),
            params: ["path": "/My File.txt", "newtime": "1"]
        ))
        let path = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == "path" }?.value
        #expect(path == "/My File.txt")
    }

    @Test("Jottacloud: mv and cp targets keep + and ;")
    func jottacloud() {
        let query = JottacloudAPIClient.jfsQuery([URLQueryItem(name: "mv", value: "/user/Jotta/Archive/a+b;c&d=e.txt")])
        #expect(query == "mv=/user/Jotta/Archive/a%2Bb%3Bc%26d%3De.txt")
        #expect(serverDecoded(query) == ["mv": "/user/Jotta/Archive/a+b;c&d=e.txt"])
        #expect(JottacloudAPIClient.jfsQuery([URLQueryItem(name: "mode", value: "bin")]) == "mode=bin")
    }
}

/// Dropbox's `Dropbox-API-Arg` header carries JSON with every non-ASCII
/// character as a `\u` escape — one UTF-16 code unit each.
@Suite("Dropbox API-Arg escaping")
struct DropboxArgEscapingTests {

    private func roundTrip(_ name: String) throws -> String {
        let json = try JSONEncoder().encode(["path": "/\(name)"])
        let escaped = DropboxAPIClient.escapeNonASCII(String(decoding: json, as: UTF8.self))
        #expect(escaped.unicodeScalars.allSatisfy { $0.value < 0x7F })
        let decoded = try JSONDecoder().decode([String: String].self, from: Data(escaped.utf8))
        return try #require(decoded["path"])
    }

    @Test("An emoji is written as its surrogate pair")
    func emoji() throws {
        #expect(DropboxAPIClient.escapeNonASCII("🎉") == "\\ud83c\\udf89")
        #expect(try roundTrip("Party 🎉.txt") == "/Party 🎉.txt")
    }

    @Test("Characters inside the BMP stay single escapes")
    func bmp() throws {
        #expect(DropboxAPIClient.escapeNonASCII("ü") == "\\u00fc")
        #expect(DropboxAPIClient.escapeNonASCII("a\u{7F}b") == "a\\u007fb")
        #expect(try roundTrip("Grüße 日本.txt") == "/Grüße 日本.txt")
    }

    @Test("The thumbnail argument escapes an emoji path the same way")
    func thumbnail() throws {
        let arg = try DropboxAPIClient.thumbnailArgument(path: "/🎉.jpg", fileName: "🎉.jpg", maxPixelSize: 200)
        #expect(arg.contains("\\ud83c\\udf89.jpg"))
    }
}
