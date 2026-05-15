import Foundation
import FileFlussCore

/// Parses `ListAllMyBucketsResult` from `GET /` (s3.<region>.amazonaws.com).
enum ListBucketsParser {
    static func parse(data: Data) throws -> [(name: String, creationDate: Date?)] {
        let parser = XMLParser(data: data)
        let delegate = Delegate()
        parser.delegate = delegate
        guard parser.parse() else { throw CloudProviderError.invalidResponse }
        return delegate.buckets
    }

    private final class Delegate: NSObject, XMLParserDelegate {
        var buckets: [(name: String, creationDate: Date?)] = []
        private var currentName: String?
        private var currentDate: String?
        private var currentElement: String?
        private var inBucket = false

        func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String : String] = [:]) {
            currentElement = elementName
            if elementName == "Bucket" {
                inBucket = true
                currentName = nil
                currentDate = nil
            }
        }

        func parser(_ parser: XMLParser, foundCharacters string: String) {
            guard let element = currentElement else { return }
            if inBucket {
                if element == "Name" {
                    currentName = (currentName ?? "") + string
                } else if element == "CreationDate" {
                    currentDate = (currentDate ?? "") + string
                }
            }
        }

        func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
            if elementName == "Bucket" {
                if let name = currentName?.trimmingCharacters(in: .whitespacesAndNewlines), !name.isEmpty {
                    let date = currentDate.flatMap(S3DateParser.parseISO8601)
                    buckets.append((name, date))
                }
                inBucket = false
            }
            if currentElement == elementName { currentElement = nil }
        }
    }
}

struct ListObjectsV2Page: Sendable {
    struct Object: Sendable {
        let key: String
        let size: Int64
        let lastModified: Date?
        let etag: String?
    }
    var contents: [Object] = []
    var commonPrefixes: [String] = []
    var nextContinuationToken: String?
}

/// Parses `ListBucketResult` (S3 ListObjectsV2 response).
enum ListObjectsV2Parser {
    static func parse(data: Data) throws -> ListObjectsV2Page {
        let parser = XMLParser(data: data)
        let delegate = Delegate()
        parser.delegate = delegate
        guard parser.parse() else { throw CloudProviderError.invalidResponse }
        return delegate.page
    }

    private final class Delegate: NSObject, XMLParserDelegate {
        var page = ListObjectsV2Page()
        private var current = ""
        private var inContents = false
        private var inCommonPrefixes = false
        private var key: String?
        private var size: String?
        private var lastModified: String?
        private var etag: String?
        private var prefix: String?
        private var nextToken: String?

        func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String : String] = [:]) {
            current = elementName
            switch elementName {
            case "Contents":
                inContents = true
                key = nil; size = nil; lastModified = nil; etag = nil
            case "CommonPrefixes":
                inCommonPrefixes = true
                prefix = nil
            default:
                break
            }
        }

        func parser(_ parser: XMLParser, foundCharacters string: String) {
            if inContents {
                switch current {
                case "Key": key = (key ?? "") + string
                case "Size": size = (size ?? "") + string
                case "LastModified": lastModified = (lastModified ?? "") + string
                case "ETag": etag = (etag ?? "") + string
                default: break
                }
            } else if inCommonPrefixes && current == "Prefix" {
                prefix = (prefix ?? "") + string
            } else if current == "NextContinuationToken" {
                nextToken = (nextToken ?? "") + string
            }
        }

        func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
            if elementName == "Contents" {
                if let k = key {
                    let bytes = Int64(size?.trimmingCharacters(in: .whitespacesAndNewlines) ?? "") ?? 0
                    let date = lastModified.flatMap(S3DateParser.parseISO8601)
                    page.contents.append(.init(key: k, size: bytes, lastModified: date, etag: etag))
                }
                inContents = false
            } else if elementName == "CommonPrefixes" {
                if let p = prefix?.trimmingCharacters(in: .whitespacesAndNewlines), !p.isEmpty {
                    page.commonPrefixes.append(p)
                }
                inCommonPrefixes = false
            }
            if elementName == "NextContinuationToken" {
                page.nextContinuationToken = nextToken?.trimmingCharacters(in: .whitespacesAndNewlines)
            }
            if current == elementName { current = "" }
        }
    }
}

/// Pulls Code/Message out of the standard `<Error>...</Error>` body that
/// every S3 4xx/5xx response carries.
enum S3ErrorParser {
    static func parse(data: Data) -> String? {
        let parser = XMLParser(data: data)
        let delegate = Delegate()
        parser.delegate = delegate
        parser.parse()
        if let code = delegate.code, let message = delegate.message {
            return "\(code): \(message)"
        }
        return delegate.message ?? delegate.code
    }

    private final class Delegate: NSObject, XMLParserDelegate {
        var code: String?
        var message: String?
        private var current = ""

        func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String : String] = [:]) {
            current = elementName
        }
        func parser(_ parser: XMLParser, foundCharacters string: String) {
            switch current {
            case "Code": code = (code ?? "") + string
            case "Message": message = (message ?? "") + string
            default: break
            }
        }
        func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
            if current == elementName { current = "" }
        }
    }
}

/// Pulls `<Region>` (the bucket's actual region) out of S3's
/// `PermanentRedirect` error body. AWS sets this on 301 responses for
/// requests that hit the wrong regional endpoint.
enum S3RegionParser {
    static func parse(data: Data) -> String? {
        let parser = XMLParser(data: data)
        let delegate = Delegate()
        parser.delegate = delegate
        parser.parse()
        return delegate.region?.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private final class Delegate: NSObject, XMLParserDelegate {
        var region: String?
        private var current = ""

        func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?, attributes attributeDict: [String : String] = [:]) {
            current = elementName
        }
        func parser(_ parser: XMLParser, foundCharacters string: String) {
            if current == "Region" {
                region = (region ?? "") + string
            }
        }
        func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
            if current == elementName { current = "" }
        }
    }
}

enum S3DateParser {
    // ISO8601DateFormatter is thread-safe for parsing once configured;
    // nonisolated(unsafe) tells Swift 6 to trust us on that.
    nonisolated(unsafe) private static let iso8601: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
    nonisolated(unsafe) private static let iso8601NoFrac: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    static func parseISO8601(_ s: String) -> Date? {
        let trimmed = s.trimmingCharacters(in: .whitespacesAndNewlines)
        return iso8601.date(from: trimmed) ?? iso8601NoFrac.date(from: trimmed)
    }
}
