import Foundation

// Originally lived inside NextCloudAPIClient.swift; extracted in Phase 0
// step 6b because WebDAVAPIClient (which moved into the package) needs it
// and NextCloudAPIClient is still in the macOS app for now (its browser-
// OAuth path imports AppKit). Internal: both call sites are in the
// FileFlussCore module.

public enum WebDAVResponseParser {
    public static func parse(data: Data, basePath: String, requestPath: String) -> [CloudFileItem] {
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

        // Extract the URL path from basePath to strip from href
        let baseURLPath: String
        if let url = URL(string: basePath) {
            baseURLPath = url.path
        } else {
            baseURLPath = basePath
        }
        let normalizedBase = baseURLPath.hasSuffix("/") ? String(baseURLPath.dropLast()) : baseURLPath

        // Also handle hrefs that are full URLs by extracting the path component
        let hrefPath: String
        if decodedHref.hasPrefix("http://") || decodedHref.hasPrefix("https://") {
            hrefPath = URL(string: decodedHref)?.path ?? decodedHref
        } else {
            hrefPath = decodedHref
        }

        var relativePath: String
        if !normalizedBase.isEmpty && hrefPath.hasPrefix(normalizedBase) {
            relativePath = String(hrefPath.dropFirst(normalizedBase.count))
            if relativePath.isEmpty { relativePath = "/" }
            if !relativePath.hasPrefix("/") { relativePath = "/" + relativePath }
        } else {
            relativePath = hrefPath
        }

        // Remove trailing slash for directories
        if relativePath.hasSuffix("/") && relativePath != "/" {
            relativePath = String(relativePath.dropLast())
        }

        let resolvedName = (displayName?.isEmpty == false) ? displayName! : (relativePath as NSString).lastPathComponent
        let name = resolvedName.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
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
