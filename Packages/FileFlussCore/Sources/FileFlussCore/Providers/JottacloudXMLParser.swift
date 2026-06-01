import Foundation

/// Parses the two Jottacloud "JFS" XML payloads FileFluss needs:
///  - a single-level folder listing (`<folder><folders>…</folders><files>…</files></folder>`)
///  - the account root (`<user><capacity/><usage/>…</user>`) for quota.
///
/// The JFS API is undocumented; element/attribute names match the rclone
/// backend's observed shapes. Listing is always one level deep, so simple
/// `inFolders`/`inFiles` flags are enough to tell the root `<folder>` from its
/// child `<folder>` entries.
public enum JottacloudXMLParser {

    public static func parseListing(data: Data, basePath: String) -> [CloudFileItem] {
        let delegate = ListingDelegate(basePath: basePath)
        let parser = XMLParser(data: data)
        parser.delegate = delegate
        parser.parse()
        return delegate.items
    }

    public struct AccountUsage: Sendable {
        public let capacity: Int64
        public let usage: Int64
    }

    public static func parseAccountUsage(data: Data) -> AccountUsage? {
        let delegate = UsageDelegate()
        let parser = XMLParser(data: data)
        parser.delegate = delegate
        parser.parse()
        guard let usage = delegate.usage else { return nil }
        return AccountUsage(capacity: delegate.capacity ?? -1, usage: usage)
    }

    /// Jottacloud revision timestamps, e.g. "2024-01-15-T10:30:00Z".
    static func parseDate(_ string: String) -> Date {
        let trimmed = string.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return Date(timeIntervalSince1970: 0) }
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        for format in ["yyyy-MM-dd'-'T'HH:mm:ss'Z'", "yyyy-MM-dd'-'T'HH:mm:ssZ", "yyyy-MM-dd'T'HH:mm:ss'Z'"] {
            f.dateFormat = format
            if let date = f.date(from: trimmed) { return date }
        }
        return Date(timeIntervalSince1970: 0)
    }
}

// MARK: - Listing

private final class ListingDelegate: NSObject, XMLParserDelegate {
    let basePath: String
    var items: [CloudFileItem] = []

    private var inFolders = false
    private var inFiles = false
    private var text = ""

    // file being built
    private var fileName: String?
    private var fileDeleted = false
    private var size: Int64 = 0
    private var md5: String?
    private var modified = ""
    private var state = ""

    init(basePath: String) {
        self.basePath = basePath
    }

    private func childPath(_ name: String) -> String {
        basePath == "/" ? "/\(name)" : "\(basePath)/\(name)"
    }

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName: String?, attributes attributeDict: [String: String] = [:]) {
        text = ""
        switch elementName {
        case "folders":
            inFolders = true
        case "files":
            inFiles = true
        case "folder" where inFolders:
            // A child directory. Skip trashed entries (carry a `deleted` attr).
            if attributeDict["deleted"] == nil, let name = attributeDict["name"], !name.isEmpty {
                items.append(CloudFileItem(
                    id: "d:\(childPath(name))",
                    name: name,
                    path: childPath(name),
                    isDirectory: true,
                    size: 0,
                    modificationDate: Date(timeIntervalSince1970: 0),
                    checksum: nil
                ))
            }
        case "file" where inFiles:
            fileName = attributeDict["name"]
            fileDeleted = attributeDict["deleted"] != nil
            size = 0
            md5 = nil
            modified = ""
            state = ""
        default:
            break
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        text += string
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName: String?) {
        let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
        switch elementName {
        case "folders":
            inFolders = false
        case "files":
            inFiles = false
        case "size" where inFiles:
            size = Int64(value) ?? 0
        case "md5" where inFiles:
            if !value.isEmpty { md5 = value }
        case "modified" where inFiles:
            if !value.isEmpty { modified = value }
        case "state" where inFiles:
            state = value
        case "file" where inFiles:
            // Only surface completed, non-trashed revisions. Incomplete or
            // corrupt uploads (state != COMPLETED) are hidden.
            if let name = fileName, !name.isEmpty, !fileDeleted, state.uppercased() == "COMPLETED" {
                items.append(CloudFileItem(
                    id: "f:\(childPath(name))",
                    name: name,
                    path: childPath(name),
                    isDirectory: false,
                    size: size,
                    modificationDate: JottacloudXMLParser.parseDate(modified),
                    checksum: md5
                ))
            }
            fileName = nil
        default:
            break
        }
        text = ""
    }
}

// MARK: - Account usage

private final class UsageDelegate: NSObject, XMLParserDelegate {
    var capacity: Int64?
    var usage: Int64?

    private var text = ""
    private var depth = 0
    private var userDepth: Int?

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName: String?, attributes attributeDict: [String: String] = [:]) {
        depth += 1
        if elementName == "user" && userDepth == nil { userDepth = depth }
        text = ""
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        text += string
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName: String?) {
        let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
        // Only the account-level capacity/usage (direct children of <user>);
        // per-device <usage> elements sit deeper and must not overwrite them.
        if let ud = userDepth, depth == ud + 1 {
            switch elementName {
            case "capacity": if capacity == nil { capacity = Int64(value) }
            case "usage": if usage == nil { usage = Int64(value) }
            default: break
            }
        }
        depth -= 1
        text = ""
    }
}
