import Foundation
import os

private let wpLog = Logger(subsystem: "com.rana.FileFluss", category: "wordpress")

struct WordPressCredentials: Codable, Sendable {
    let siteURL: String
    let username: String
    let password: String
    let displayName: String
}

/// Represents a WordPress media item from the REST API.
private struct WPMediaItem: Decodable {
    let id: Int
    let date: String
    let title: WPRendered?
    let media_type: String?
    let mime_type: String?
    let source_url: String?
    let media_details: WPMediaDetails?

    struct WPRendered: Decodable {
        let rendered: String
    }

    struct WPMediaDetails: Decodable {
        let filesize: AnyCodableInt?
        let file: String?
    }

    /// Handles filesize being returned as either Int or String from different WordPress versions.
    enum AnyCodableInt: Decodable {
        case int(Int)
        case string(String)

        init(from decoder: Decoder) throws {
            let container = try decoder.singleValueContainer()
            if let v = try? container.decode(Int.self) {
                self = .int(v)
            } else if let s = try? container.decode(String.self) {
                self = .string(s)
            } else {
                self = .int(0)
            }
        }

        var intValue: Int {
            switch self {
            case .int(let v): return v
            case .string(let s): return Int(s) ?? 0
            }
        }
    }
}

/// Preserves Authorization header, HTTP method, and body on same-host redirects.
/// URLSession strips auth headers and converts POST→GET on redirects by default.
private final class WPSessionDelegate: NSObject, URLSessionTaskDelegate, Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        var redirected = request
        guard let original = task.originalRequest,
              original.url?.host == request.url?.host else {
            completionHandler(redirected)
            return
        }
        // Preserve auth header
        if let auth = original.value(forHTTPHeaderField: "Authorization") {
            redirected.setValue(auth, forHTTPHeaderField: "Authorization")
        }
        // Preserve HTTP method and body (URLSession changes POST to GET on 301/302)
        if let method = original.httpMethod, redirected.httpMethod != method {
            redirected.httpMethod = method
            redirected.httpBody = original.httpBody
            if let contentType = original.value(forHTTPHeaderField: "Content-Type") {
                redirected.setValue(contentType, forHTTPHeaderField: "Content-Type")
            }
        }
        completionHandler(redirected)
    }
}

actor WordPressAPIClient {
    let credentials: WordPressCredentials
    private let session: URLSession
    private let baseAPIURL: String

    /// Cache mapping virtual paths to WordPress media IDs for delete/rename operations.
    private var mediaIdCache: [String: Int] = [:]

    init(credentials: WordPressCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        self.session = URLSession(configuration: config, delegate: WPSessionDelegate(), delegateQueue: nil)

        // Normalize site URL
        var url = credentials.siteURL.trimmingCharacters(in: .whitespacesAndNewlines)
        if !url.hasPrefix("http://") && !url.hasPrefix("https://") {
            url = "https://" + url
        }
        while url.hasSuffix("/") { url.removeLast() }
        self.baseAPIURL = url + "/wp-json/wp/v2"
    }

    // MARK: - Authentication

    static func authenticate(siteURL: String, username: String, password: String) async throws -> WordPressCredentials {
        let creds = WordPressCredentials(siteURL: siteURL, username: username, password: password, displayName: "")
        let client = WordPressAPIClient(credentials: creds)
        return try await client.verifyAndGetCredentials()
    }

    private func verifyAndGetCredentials() async throws -> WordPressCredentials {
        // Verify connection by fetching media (limit 1)
        let request = try makeRequest(path: "/media", query: [("per_page", "1")])
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        if http.statusCode == 401 || http.statusCode == 403 {
            throw CloudProviderError.invalidCredentials
        }
        if http.statusCode == 404 {
            throw CloudProviderError.notFound("WordPress REST API not found. Make sure the site has the REST API enabled at /wp-json/wp/v2/media")
        }
        guard (200...299).contains(http.statusCode) else {
            throw CloudProviderError.serverError(http.statusCode)
        }

        // Verify the response is valid JSON (not an HTML error page)
        if (try? JSONSerialization.jsonObject(with: data)) == nil {
            throw CloudProviderError.invalidResponse
        }

        // Fetch user info for display name
        var displayName = credentials.username
        do {
            let userRequest = try makeRequest(path: "/users/me", query: [])
            let (userData, userResponse) = try await session.data(for: userRequest)
            if let userHttp = userResponse as? HTTPURLResponse, (200...299).contains(userHttp.statusCode) {
                if let json = try? JSONSerialization.jsonObject(with: userData) as? [String: Any],
                   let name = json["name"] as? String, !name.isEmpty {
                    displayName = name
                }
            }
        } catch {
            // Non-critical, keep username as display name
        }

        wpLog.info("[WordPress] Authenticated as \(self.credentials.username) at \(self.credentials.siteURL)")
        return WordPressCredentials(siteURL: self.credentials.siteURL, username: self.credentials.username, password: self.credentials.password, displayName: displayName)
    }

    func userDisplayName() -> String {
        if !credentials.displayName.isEmpty {
            return credentials.displayName
        }
        // Extract domain from URL for display
        if let url = URL(string: credentials.siteURL), let host = url.host {
            return host
        }
        return credentials.username
    }

    // MARK: - File Operations

    func listFolder(path: String) async throws -> [CloudFileItem] {
        let normalizedPath = path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))

        if normalizedPath.isEmpty {
            // Root: show year folders
            return try await listYearFolders()
        }

        let components = normalizedPath.split(separator: "/").map(String.init)

        if components.count == 1, let year = Int(components[0]), year >= 1900 && year <= 2100 {
            // Year folder: show month folders
            return try await listMonthFolders(year: year)
        }

        if components.count == 2, let year = Int(components[0]), let month = Int(components[1]),
           year >= 1900 && year <= 2100, month >= 1 && month <= 12 {
            // Year/Month folder: show media files
            return try await listMediaFiles(year: year, month: month)
        }

        return []
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        // remotePath is like /2026/04/filename.jpg — find the source_url from cache or fetch
        let sourceURL = try await resolveSourceURL(for: remotePath)

        var request = URLRequest(url: URL(string: sourceURL)!)
        request.addValue(authHeaderValue(), forHTTPHeaderField: "Authorization")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.serverError((response as? HTTPURLResponse)?.statusCode ?? -1)
        }

        try data.write(to: localURL)
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        let fileName = localURL.lastPathComponent
        let fileData = try Data(contentsOf: localURL)

        let boundary = UUID().uuidString
        var body = Data()

        // file field
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"file\"; filename=\"\(fileName)\"\r\n".data(using: .utf8)!)

        let mimeType = mimeTypeForExtension(localURL.pathExtension)
        body.append("Content-Type: \(mimeType)\r\n\r\n".data(using: .utf8)!)
        body.append(fileData)
        body.append("\r\n".data(using: .utf8)!)
        body.append("--\(boundary)--\r\n".data(using: .utf8)!)

        var request = try makeRequest(path: "/media", query: [])
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.httpBody = body

        let (responseData, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        // Debug logging
        let debugLine = "[WP Upload] url=\(request.url?.absoluteString ?? "nil") status=\(http.statusCode) responseURL=\(http.url?.absoluteString ?? "nil") body=\(String(data: responseData.prefix(500), encoding: .utf8) ?? "nil")\n"
        let logPath = "/tmp/filefluss-wordpress.log"
        if let fh = FileHandle(forWritingAtPath: logPath) {
            fh.seekToEndOfFile()
            fh.write(Data(debugLine.utf8))
            fh.closeFile()
        } else {
            FileManager.default.createFile(atPath: logPath, contents: Data(debugLine.utf8))
        }

        guard (200...299).contains(http.statusCode) else {
            throw CloudProviderError.serverError(http.statusCode)
        }
    }

    func deleteItem(at path: String) async throws {
        let mediaId = try await resolveMediaId(for: path)

        var request = try makeRequest(path: "/media/\(mediaId)", query: [("force", "true")])
        request.httpMethod = "DELETE"

        let (_, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.serverError((response as? HTTPURLResponse)?.statusCode ?? -1)
        }

        mediaIdCache.removeValue(forKey: path)
    }

    func renameItem(at path: String, to newName: String) async throws {
        let mediaId = try await resolveMediaId(for: path)

        // WordPress rename updates the title only — the actual file URL remains unchanged
        let titleWithoutExt = (newName as NSString).deletingPathExtension

        var request = try makeRequest(path: "/media/\(mediaId)", query: [])
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let payload = ["title": titleWithoutExt]
        request.httpBody = try JSONSerialization.data(withJSONObject: payload)

        let (_, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.serverError((response as? HTTPURLResponse)?.statusCode ?? -1)
        }
    }

    func getFileInfo(at path: String) async throws -> CloudFileItem {
        let mediaId = try await resolveMediaId(for: path)

        let request = try makeRequest(path: "/media/\(mediaId)", query: [])
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.notFound(path)
        }

        let item = try JSONDecoder().decode(WPMediaItem.self, from: data)
        return mediaItemToCloudFile(item, basePath: (path as NSString).deletingLastPathComponent)
    }

    func folderSize(path: String) async throws -> Int64 {
        let items = try await listFolder(path: path)
        var total: Int64 = 0
        for item in items {
            if item.isDirectory {
                total += try await folderSize(path: item.path)
            } else {
                total += item.size
            }
        }
        return total
    }

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
        var allItems: [CloudFileItem] = []
        var page = 1
        let perPage = 100

        while true {
            let request = try makeRequest(path: "/media", query: [
                ("search", query),
                ("per_page", "\(perPage)"),
                ("page", "\(page)")
            ])

            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
                break
            }

            let items = try JSONDecoder().decode([WPMediaItem].self, from: data)
            if items.isEmpty { break }

            for item in items {
                let cloudFile = mediaItemToCloudFile(item, basePath: virtualPathPrefix(for: item))
                allItems.append(cloudFile)
            }

            // Check if there are more pages
            let totalPages = Int(http.value(forHTTPHeaderField: "X-WP-TotalPages") ?? "1") ?? 1
            if page >= totalPages { break }
            page += 1
        }

        return allItems
    }

    // MARK: - Virtual Folder Structure

    private func listYearFolders() async throws -> [CloudFileItem] {
        // Fetch all media to determine which years have content
        // Use a single request to get the date range
        var years = Set<Int>()
        var page = 1

        // Fetch first page to get total count
        let firstRequest = try makeRequest(path: "/media", query: [
            ("per_page", "100"),
            ("page", "1"),
            ("orderby", "date"),
            ("order", "asc")
        ])
        let (firstData, firstResponse) = try await session.data(for: firstRequest)
        guard let firstHttp = firstResponse as? HTTPURLResponse, (200...299).contains(firstHttp.statusCode) else {
            throw CloudProviderError.invalidResponse
        }

        let totalPages = Int(firstHttp.value(forHTTPHeaderField: "X-WP-TotalPages") ?? "1") ?? 1
        let firstItems = try JSONDecoder().decode([WPMediaItem].self, from: firstData)
        for item in firstItems {
            if let year = extractYear(from: item.date) { years.insert(year) }
        }

        // If there are more pages, also fetch last page to get the most recent year
        if totalPages > 1 {
            let lastRequest = try makeRequest(path: "/media", query: [
                ("per_page", "100"),
                ("page", "\(totalPages)"),
                ("orderby", "date"),
                ("order", "asc")
            ])
            let (lastData, lastResponse) = try await session.data(for: lastRequest)
            if let lastHttp = lastResponse as? HTTPURLResponse, (200...299).contains(lastHttp.statusCode) {
                let lastItems = try JSONDecoder().decode([WPMediaItem].self, from: lastData)
                for item in lastItems {
                    if let year = extractYear(from: item.date) { years.insert(year) }
                }
            }

            // Fetch middle pages if needed (up to 10 pages total to be reasonable)
            let pagesToFetch = min(totalPages, 10)
            for p in 2..<pagesToFetch {
                let request = try makeRequest(path: "/media", query: [
                    ("per_page", "100"),
                    ("page", "\(p)"),
                    ("orderby", "date"),
                    ("order", "asc")
                ])
                let (data, response) = try await session.data(for: request)
                if let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) {
                    let items = try JSONDecoder().decode([WPMediaItem].self, from: data)
                    for item in items {
                        if let year = extractYear(from: item.date) { years.insert(year) }
                    }
                }
            }
        }

        return years.sorted(by: >).map { year in
            CloudFileItem(
                id: "wp-year-\(year)",
                name: "\(year)",
                path: "/\(year)",
                isDirectory: true,
                size: 0,
                modificationDate: .distantPast,
                checksum: nil
            )
        }
    }

    private func listMonthFolders(year: Int) async throws -> [CloudFileItem] {
        var months = Set<Int>()
        var page = 1

        let monthNames = ["", "January", "February", "March", "April", "May", "June",
                          "July", "August", "September", "October", "November", "December"]

        while true {
            let afterDate = "\(year)-01-01T00:00:00"
            let beforeDate = "\(year + 1)-01-01T00:00:00"

            let request = try makeRequest(path: "/media", query: [
                ("after", afterDate),
                ("before", beforeDate),
                ("per_page", "100"),
                ("page", "\(page)")
            ])

            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
                break
            }

            let items = try JSONDecoder().decode([WPMediaItem].self, from: data)
            if items.isEmpty { break }

            for item in items {
                if let month = extractMonth(from: item.date) { months.insert(month) }
            }

            let totalPages = Int(http.value(forHTTPHeaderField: "X-WP-TotalPages") ?? "1") ?? 1
            if page >= totalPages { break }
            page += 1
        }

        return months.sorted(by: >).map { month in
            let paddedMonth = String(format: "%02d", month)
            return CloudFileItem(
                id: "wp-month-\(year)-\(paddedMonth)",
                name: "\(paddedMonth) - \(monthNames[month])",
                path: "/\(year)/\(paddedMonth)",
                isDirectory: true,
                size: 0,
                modificationDate: .distantPast,
                checksum: nil
            )
        }
    }

    private func listMediaFiles(year: Int, month: Int) async throws -> [CloudFileItem] {
        var allItems: [CloudFileItem] = []
        var page = 1
        let paddedMonth = String(format: "%02d", month)

        // Calculate next month for the before date
        let nextYear = month == 12 ? year + 1 : year
        let nextMonth = month == 12 ? 1 : month + 1

        while true {
            let afterDate = "\(year)-\(paddedMonth)-01T00:00:00"
            let beforeDate = "\(nextYear)-\(String(format: "%02d", nextMonth))-01T00:00:00"

            let request = try makeRequest(path: "/media", query: [
                ("after", afterDate),
                ("before", beforeDate),
                ("per_page", "100"),
                ("page", "\(page)")
            ])

            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
                let debugLine = "[WP List] url=\(request.url?.absoluteString ?? "nil") status=\((response as? HTTPURLResponse)?.statusCode ?? -1) body=\(String(data: Data(), encoding: .utf8) ?? "")\n"
                Self.writeLog(debugLine)
                break
            }

            let items = try JSONDecoder().decode([WPMediaItem].self, from: data)
            let debugLine = "[WP List] url=\(request.url?.absoluteString ?? "nil") status=\(http.statusCode) items=\(items.count) body=\(String(data: data.prefix(300), encoding: .utf8) ?? "")\n"
            Self.writeLog(debugLine)
            if items.isEmpty { break }

            let basePath = "/\(year)/\(paddedMonth)"
            for item in items {
                let cloudFile = mediaItemToCloudFile(item, basePath: basePath)
                allItems.append(cloudFile)
                mediaIdCache[cloudFile.path] = item.id
            }

            let totalPages = Int(http.value(forHTTPHeaderField: "X-WP-TotalPages") ?? "1") ?? 1
            if page >= totalPages { break }
            page += 1
        }

        return allItems
    }

    private static func writeLog(_ line: String) {
        let logPath = "/tmp/filefluss-wordpress.log"
        if let fh = FileHandle(forWritingAtPath: logPath) {
            fh.seekToEndOfFile()
            fh.write(Data(line.utf8))
            fh.closeFile()
        } else {
            FileManager.default.createFile(atPath: logPath, contents: Data(line.utf8))
        }
    }

    // MARK: - Helpers

    private func makeRequest(path: String, query: [(String, String)]) throws -> URLRequest {
        var urlString = baseAPIURL + path
        if !query.isEmpty {
            let queryString = query.map { key, value in
                "\(key)=\(value.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? value)"
            }.joined(separator: "&")
            urlString += "?\(queryString)"
        }

        guard let url = URL(string: urlString) else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.addValue(authHeaderValue(), forHTTPHeaderField: "Authorization")
        return request
    }

    private func authHeaderValue() -> String {
        let loginString = "\(credentials.username):\(credentials.password)"
        let loginData = loginString.data(using: .utf8)!
        return "Basic \(loginData.base64EncodedString())"
    }

    private func mediaItemToCloudFile(_ item: WPMediaItem, basePath: String) -> CloudFileItem {
        let fileName = fileNameFromItem(item)
        let itemPath: String
        if basePath == "/" {
            itemPath = "/\(fileName)"
        } else {
            itemPath = "\(basePath)/\(fileName)"
        }

        let size = Int64(item.media_details?.filesize?.intValue ?? 0)
        let modDate = parseWordPressDate(item.date)

        return CloudFileItem(
            id: "wp-\(item.id)",
            name: fileName,
            path: itemPath,
            isDirectory: false,
            size: size,
            modificationDate: modDate,
            checksum: nil
        )
    }

    private func fileNameFromItem(_ item: WPMediaItem) -> String {
        // Prefer the actual file name from media_details, fall back to title + extension from source_url
        if let file = item.media_details?.file, !file.isEmpty {
            // file is like "2026/04/photo.jpg" — extract just the filename
            return (file as NSString).lastPathComponent
        }

        let title = (item.title?.rendered ?? "")
            .replacingOccurrences(of: "<[^>]+>", with: "", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)

        // Get extension from source_url
        let sourceExt = ((item.source_url ?? "") as NSString).pathExtension
        if !sourceExt.isEmpty && !title.isEmpty {
            return "\(title).\(sourceExt)"
        }

        return title.isEmpty ? "media-\(item.id)" : title
    }

    private func parseWordPressDate(_ dateString: String) -> Date {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: dateString) { return date }

        // Try without fractional seconds
        formatter.formatOptions = [.withInternetDateTime]
        if let date = formatter.date(from: dateString) { return date }

        // Try basic format "2026-04-08T12:00:00"
        let df = DateFormatter()
        df.dateFormat = "yyyy-MM-dd'T'HH:mm:ss"
        df.locale = Locale(identifier: "en_US_POSIX")
        return df.date(from: dateString) ?? .distantPast
    }

    private func extractYear(from dateString: String) -> Int? {
        guard dateString.count >= 4 else { return nil }
        return Int(dateString.prefix(4))
    }

    private func extractMonth(from dateString: String) -> Int? {
        guard dateString.count >= 7 else { return nil }
        let monthStr = dateString[dateString.index(dateString.startIndex, offsetBy: 5)..<dateString.index(dateString.startIndex, offsetBy: 7)]
        return Int(monthStr)
    }

    private func virtualPathPrefix(for item: WPMediaItem) -> String {
        guard let year = extractYear(from: item.date),
              let month = extractMonth(from: item.date) else {
            return "/"
        }
        return "/\(year)/\(String(format: "%02d", month))"
    }

    private func resolveMediaId(for path: String) async throws -> Int {
        if let cached = mediaIdCache[path] {
            return cached
        }

        // Parse year/month from path and search
        let components = path.trimmingCharacters(in: CharacterSet(charactersIn: "/")).split(separator: "/")
        guard components.count >= 3,
              let year = Int(components[0]),
              let month = Int(components[1]) else {
            throw CloudProviderError.notFound(path)
        }

        let fileName = components.dropFirst(2).joined(separator: "/")

        // List the folder to populate cache
        _ = try await listMediaFiles(year: year, month: month)

        if let cached = mediaIdCache[path] {
            return cached
        }

        // Try searching by filename
        let nameWithoutExt = (fileName as NSString).deletingPathExtension
        let request = try makeRequest(path: "/media", query: [("search", nameWithoutExt)])
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.notFound(path)
        }

        let items = try JSONDecoder().decode([WPMediaItem].self, from: data)
        for item in items {
            let itemFileName = fileNameFromItem(item)
            if itemFileName == fileName {
                mediaIdCache[path] = item.id
                return item.id
            }
        }

        throw CloudProviderError.notFound(path)
    }

    private func resolveSourceURL(for path: String) async throws -> String {
        let mediaId = try await resolveMediaId(for: path)

        let request = try makeRequest(path: "/media/\(mediaId)", query: [])
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.notFound(path)
        }

        let item = try JSONDecoder().decode(WPMediaItem.self, from: data)
        guard let sourceURL = item.source_url else {
            throw CloudProviderError.notFound(path)
        }
        return sourceURL
    }

    private func mimeTypeForExtension(_ ext: String) -> String {
        switch ext.lowercased() {
        case "jpg", "jpeg": return "image/jpeg"
        case "png": return "image/png"
        case "gif": return "image/gif"
        case "webp": return "image/webp"
        case "svg": return "image/svg+xml"
        case "mp4": return "video/mp4"
        case "mov": return "video/quicktime"
        case "avi": return "video/x-msvideo"
        case "mp3": return "audio/mpeg"
        case "wav": return "audio/wav"
        case "pdf": return "application/pdf"
        case "doc": return "application/msword"
        case "docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        case "zip": return "application/zip"
        default: return "application/octet-stream"
        }
    }
}
