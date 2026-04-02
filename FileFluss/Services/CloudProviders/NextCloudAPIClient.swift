import Foundation
import os

private let nextCloudLog = Logger(subsystem: "com.rana.FileFluss", category: "nextCloud")

struct NextCloudCredentials: Codable, Sendable {
    let serverURL: String
    let username: String
    let appPassword: String
    let displayName: String
}

actor NextCloudAPIClient {
    let credentials: NextCloudCredentials
    private let session: URLSession
    private let davBaseURL: String

    init(credentials: NextCloudCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)

        let base = credentials.serverURL.hasSuffix("/")
            ? String(credentials.serverURL.dropLast())
            : credentials.serverURL
        self.davBaseURL = "\(base)/remote.php/dav/files/\(credentials.username)"
    }

    private var authHeader: String {
        let cred = "\(credentials.username):\(credentials.appPassword)"
        let encoded = Data(cred.utf8).base64EncodedString()
        return "Basic \(encoded)"
    }

    // MARK: - Authentication & User Info

    static func authenticate(serverURL: String, username: String, appPassword: String) async throws -> NextCloudCredentials {
        let base = serverURL.hasSuffix("/") ? String(serverURL.dropLast()) : serverURL
        let ocsURL = URL(string: "\(base)/ocs/v1.php/cloud/user")!

        var request = URLRequest(url: ocsURL)
        let cred = "\(username):\(appPassword)"
        let encoded = Data(cred.utf8).base64EncodedString()
        request.setValue("Basic \(encoded)", forHTTPHeaderField: "Authorization")
        request.setValue("true", forHTTPHeaderField: "OCS-APIRequest")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        if http.statusCode == 401 {
            throw CloudProviderError.invalidCredentials
        }
        guard (200...299).contains(http.statusCode) else {
            nextCloudLog.error("[NextCloud] User info failed: HTTP \(http.statusCode)")
            throw CloudProviderError.serverError(http.statusCode)
        }

        let displayName = parseDisplayName(from: data) ?? username
        nextCloudLog.info("[NextCloud] Authenticated as \(displayName)")

        return NextCloudCredentials(
            serverURL: base,
            username: username,
            appPassword: appPassword,
            displayName: displayName
        )
    }

    func userDisplayName() -> String {
        credentials.displayName
    }

    // MARK: - File Operations

    func listFolder(path: String) async throws -> [CloudFileItem] {
        let davPath = buildDAVPath(path)
        guard let url = URL(string: davPath) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "PROPFIND"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")
        request.setValue("1", forHTTPHeaderField: "Depth")
        request.setValue("application/xml", forHTTPHeaderField: "Content-Type")
        request.httpBody = propfindBody.data(using: .utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        guard http.statusCode == 207 else {
            nextCloudLog.error("[NextCloud] PROPFIND \(path) → HTTP \(http.statusCode)")
            throw Self.mapHTTPError(statusCode: http.statusCode)
        }

        let items = WebDAVResponseParser.parse(data: data, basePath: davBaseURL, requestPath: path)
        // PROPFIND depth 1 includes the folder itself as first entry — skip it
        return items.dropFirst().map { $0 }
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        let davPath = buildDAVPath(remotePath)
        guard let url = URL(string: davPath) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
        try data.write(to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        let davPath = buildDAVPath(remotePath)
        guard let url = URL(string: davPath) else {
            throw CloudProviderError.invalidResponse
        }

        let fileData = try Data(contentsOf: localURL)
        var request = URLRequest(url: url)
        request.httpMethod = "PUT"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.httpBody = fileData

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            nextCloudLog.error("[NextCloud] Upload failed: HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    func deleteItem(at path: String) async throws {
        let davPath = buildDAVPath(path)
        guard let url = URL(string: davPath) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "DELETE"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) || http.statusCode == 204 else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            nextCloudLog.error("[NextCloud] DELETE \(path) → HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    func createFolder(at path: String) async throws {
        let davPath = buildDAVPath(path)
        guard let url = URL(string: davPath) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "MKCOL"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) || http.statusCode == 201 else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            nextCloudLog.error("[NextCloud] MKCOL \(path) → HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    func renameItem(at path: String, to newName: String) async throws {
        let parentPath = (path as NSString).deletingLastPathComponent
        let destinationPath: String
        if parentPath == "/" {
            destinationPath = "/\(newName)"
        } else {
            destinationPath = "\(parentPath)/\(newName)"
        }

        let sourceDavPath = buildDAVPath(path)
        let destDavPath = buildDAVPath(destinationPath)

        guard let url = URL(string: sourceDavPath) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "MOVE"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")
        request.setValue(destDavPath, forHTTPHeaderField: "Destination")
        request.setValue("F", forHTTPHeaderField: "Overwrite")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) || http.statusCode == 201 else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            nextCloudLog.error("[NextCloud] MOVE \(path) → HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    func getFileInfo(at path: String) async throws -> CloudFileItem {
        let davPath = buildDAVPath(path)
        guard let url = URL(string: davPath) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "PROPFIND"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")
        request.setValue("0", forHTTPHeaderField: "Depth")
        request.setValue("application/xml", forHTTPHeaderField: "Content-Type")
        request.httpBody = propfindBody.data(using: .utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 207 else {
            let http = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }

        let items = WebDAVResponseParser.parse(data: data, basePath: davBaseURL, requestPath: path)
        guard let item = items.first else {
            throw CloudProviderError.notFound(path)
        }
        return item
    }

    func folderSize(path: String) async throws -> Int64 {
        return try await calculateFolderSizeRecursively(path: path)
    }

    private func calculateFolderSizeRecursively(path: String) async throws -> Int64 {
        let items = try await listFolder(path: path)
        var total: Int64 = 0
        for item in items {
            if item.isDirectory {
                total += try await calculateFolderSizeRecursively(path: item.path)
            } else {
                total += item.size
            }
        }
        return total
    }

    // MARK: - Search

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
        let searchPath = path ?? "/"
        let davPath = buildDAVPath(searchPath)
        guard let url = URL(string: davPath) else {
            throw CloudProviderError.invalidResponse
        }

        let searchBody = """
        <?xml version="1.0" encoding="UTF-8"?>
        <d:searchrequest xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns" xmlns:nc="http://nextcloud.org/ns">
            <d:basicsearch>
                <d:select>
                    <d:prop>
                        <d:getlastmodified/>
                        <d:getcontentlength/>
                        <d:getcontenttype/>
                        <d:resourcetype/>
                        <d:displayname/>
                        <oc:checksums/>
                        <oc:size/>
                    </d:prop>
                </d:select>
                <d:from>
                    <d:scope>
                        <d:href>\(davPath)</d:href>
                        <d:depth>infinity</d:depth>
                    </d:scope>
                </d:from>
                <d:where>
                    <d:like>
                        <d:prop><d:displayname/></d:prop>
                        <d:literal>%\(query)%</d:literal>
                    </d:like>
                </d:where>
                <d:limit>
                    <d:nresults>100</d:nresults>
                </d:limit>
            </d:basicsearch>
        </d:searchrequest>
        """

        var request = URLRequest(url: url)
        request.httpMethod = "SEARCH"
        request.setValue(authHeader, forHTTPHeaderField: "Authorization")
        request.setValue("application/xml", forHTTPHeaderField: "Content-Type")
        request.httpBody = searchBody.data(using: .utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        guard http.statusCode == 207 else {
            nextCloudLog.error("[NextCloud] SEARCH \(searchPath) → HTTP \(http.statusCode)")
            throw Self.mapHTTPError(statusCode: http.statusCode)
        }

        return WebDAVResponseParser.parse(data: data, basePath: davBaseURL, requestPath: searchPath)
    }

    // MARK: - Private

    private func buildDAVPath(_ path: String) -> String {
        let cleanPath = path.hasPrefix("/") ? String(path.dropFirst()) : path
        let encoded = cleanPath
            .split(separator: "/", omittingEmptySubsequences: true)
            .map { $0.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? String($0) }
            .joined(separator: "/")
        if encoded.isEmpty {
            return "\(davBaseURL)/"
        }
        return "\(davBaseURL)/\(encoded)"
    }

    private var propfindBody: String {
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <d:propfind xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns" xmlns:nc="http://nextcloud.org/ns">
            <d:prop>
                <d:getlastmodified/>
                <d:getcontentlength/>
                <d:getcontenttype/>
                <d:resourcetype/>
                <d:displayname/>
                <oc:checksums/>
                <oc:size/>
            </d:prop>
        </d:propfind>
        """
    }

    private static func mapHTTPError(statusCode: Int) -> CloudProviderError {
        switch statusCode {
        case 401: return .invalidCredentials
        case 403: return .unauthorized
        case 404: return .notFound("Resource not found")
        case 409: return .serverError(409)
        case 429: return .rateLimited
        case 507: return .quotaExceeded
        default: return .serverError(statusCode)
        }
    }

    /// Parse display name from NextCloud OCS XML response.
    private static func parseDisplayName(from data: Data) -> String? {
        let parser = OCSDisplayNameParser()
        let xmlParser = XMLParser(data: data)
        xmlParser.delegate = parser
        xmlParser.parse()
        let name = parser.displayName?.trimmingCharacters(in: .whitespacesAndNewlines)
        if let name, !name.isEmpty { return name }
        return nil
    }
}

// MARK: - OCS Display Name Parser

private final class OCSDisplayNameParser: NSObject, XMLParserDelegate, @unchecked Sendable {
    var displayName: String?
    private var currentElement = ""
    private var currentText = ""

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName: String?, attributes: [String: String] = [:]) {
        currentElement = elementName
        currentText = ""
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        currentText += string
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName: String?) {
        if elementName == "displayname" {
            displayName = currentText
        }
    }
}

// MARK: - WebDAV PROPFIND Response Parser

enum WebDAVResponseParser {
    static func parse(data: Data, basePath: String, requestPath: String) -> [CloudFileItem] {
        let parser = WebDAVXMLParser(basePath: basePath, requestPath: requestPath)
        let xmlParser = XMLParser(data: data)
        xmlParser.delegate = parser
        xmlParser.parse()
        return parser.items
    }
}

private final class WebDAVXMLParser: NSObject, XMLParserDelegate, @unchecked Sendable {
    let basePath: String
    let requestPath: String
    var items: [CloudFileItem] = []

    private var currentElement = ""
    private var currentText = ""
    private var isCollecting = false

    // Current item properties being built
    private var href: String?
    private var displayName: String?
    private var lastModified: String?
    private var contentLength: String?
    private var contentType: String?
    private var isDirectory = false
    private var checksum: String?
    private var ocSize: String?

    init(basePath: String, requestPath: String) {
        self.basePath = basePath
        self.requestPath = requestPath
    }

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName: String?, attributes: [String: String] = [:]) {
        let local = localName(elementName)
        currentElement = local
        currentText = ""

        if local == "response" {
            href = nil
            displayName = nil
            lastModified = nil
            contentLength = nil
            contentType = nil
            isDirectory = false
            checksum = nil
            ocSize = nil
        }

        if local == "collection" {
            isDirectory = true
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        currentText += string
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName: String?) {
        let local = localName(elementName)
        let text = currentText.trimmingCharacters(in: .whitespacesAndNewlines)

        switch local {
        case "href":
            href = text
        case "displayname":
            displayName = text
        case "getlastmodified":
            lastModified = text
        case "getcontentlength":
            contentLength = text
        case "getcontenttype":
            contentType = text
        case "checksums":
            checksum = text
        case "size":
            ocSize = text
        case "response":
            buildItem()
        default:
            break
        }
    }

    private func buildItem() {
        guard let href else { return }

        let decodedHref = href.removingPercentEncoding ?? href
        // Extract the relative path from the full DAV href
        let decodedBase = basePath
            .replacingOccurrences(of: basePath.components(separatedBy: "/remote.php").first ?? "", with: "")

        var relativePath: String
        if let range = decodedHref.range(of: "/remote.php/dav/files/") {
            let afterPrefix = String(decodedHref[range.upperBound...])
            // Remove the username segment
            if let slashIndex = afterPrefix.firstIndex(of: "/") {
                relativePath = String(afterPrefix[slashIndex...])
            } else {
                relativePath = "/"
            }
        } else {
            relativePath = decodedHref
        }

        // Remove trailing slash for directories
        if relativePath.hasSuffix("/") && relativePath != "/" {
            relativePath = String(relativePath.dropLast())
        }

        let name = displayName ?? (relativePath as NSString).lastPathComponent
        if name.isEmpty { return }

        let size: Int64
        if isDirectory {
            size = Int64(ocSize ?? "") ?? 0
        } else {
            size = Int64(contentLength ?? "0") ?? 0
        }

        let modDate: Date
        if let lastModified {
            modDate = Self.parseHTTPDate(lastModified) ?? .distantPast
        } else {
            modDate = .distantPast
        }

        let item = CloudFileItem(
            id: isDirectory ? "d\(relativePath.hashValue)" : "f\(relativePath.hashValue)",
            name: name,
            path: relativePath,
            isDirectory: isDirectory,
            size: size,
            modificationDate: modDate,
            checksum: checksum
        )
        items.append(item)
    }

    private func localName(_ element: String) -> String {
        if let colonIndex = element.lastIndex(of: ":") {
            return String(element[element.index(after: colonIndex)...])
        }
        return element
    }

    private static let httpDateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        return f
    }()

    private static func parseHTTPDate(_ string: String) -> Date? {
        httpDateFormatter.date(from: string)
    }
}
