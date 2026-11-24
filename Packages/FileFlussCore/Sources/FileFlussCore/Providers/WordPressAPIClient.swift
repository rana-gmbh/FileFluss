import Foundation
import os

private let wpLog = Logger(subsystem: "com.rana.FileFluss", category: "wordpress")

public struct WordPressCredentials: Codable, Sendable {
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
    public func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        var redirected = request
        guard let original = task.originalRequest,
              CredentialScope.allows(request.url, for: original.url) else {
            redirected.setValue(nil, forHTTPHeaderField: "Authorization")
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

public actor WordPressAPIClient {
    let credentials: WordPressCredentials
    private let session: URLSession
    private let baseAPIURL: String

    /// Cache mapping virtual paths to WordPress media IDs for delete/rename operations.
    private var mediaIdCache: [String: Int] = [:]

    public init(credentials: WordPressCredentials) {
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        self.init(
            credentials: credentials,
            session: URLSession(configuration: config, delegate: WPSessionDelegate(), delegateQueue: nil)
        )
    }

    /// The session is injectable so tests can answer requests from a script.
    init(credentials: WordPressCredentials, session: URLSession) {
        self.credentials = credentials
        self.session = session

        // Normalize site URL
        var url = credentials.siteURL.trimmingCharacters(in: .whitespacesAndNewlines)
        if !url.hasPrefix("http://") && !url.hasPrefix("https://") {
            url = "https://" + url
        }
        while url.hasSuffix("/") { url.removeLast() }
        self.baseAPIURL = url + "/wp-json/wp/v2"
    }

    // MARK: - Authentication

    public static func authenticate(siteURL: String, username: String, password: String) async throws -> WordPressCredentials {
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
            throw CloudProviderError.notFound(L10n.text("WordPress REST API not found. Make sure the site has the REST API enabled at /wp-json/wp/v2/media"))
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

    public func userDisplayName() -> String {
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

    public func listFolder(path: String) async throws -> [CloudFileItem] {
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

        // Not a year or month folder: there is nothing here to list, and an
        // empty listing would read to a sync as "everything was deleted".
        throw CloudProviderError.notFound(path)
    }

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let sourceURL = try await resolveSourceURL(for: remotePath)

        guard let url = URL(string: sourceURL) else {
            throw CloudProviderError.invalidResponse
        }
        var request = URLRequest(url: url)
        // The address comes from the site's media library and often points at
        // a CDN or an S3 bucket, which must not receive the site's password.
        if CredentialScope.allows(url, for: URL(string: credentials.siteURL)) {
            request.addValue(authHeaderValue(), forHTTPHeaderField: "Authorization")
        }

        let (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.serverError((response as? HTTPURLResponse)?.statusCode ?? -1)
        }
        try FileSystemService.placeDownload(from: tempURL, at: localURL)
    }

    public func uploadFile(from localURL: URL, to remotePath: String) async throws {
        try await uploadFile(from: localURL, to: remotePath, onBytes: nil)
    }

    /// Uploads into the media library. WordPress files it under the upload
    /// month, so the virtual folder in `remotePath` is not where it lands —
    /// but its name is the one the user asked for. The local file is often a
    /// staging copy (`filefluss-sync-<UUID>` in a cloud-to-cloud transfer),
    /// whose name must never reach the library.
    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let fileName = Self.uploadFileName(remotePath: remotePath, localURL: localURL)
        let fileData = try Data(contentsOf: localURL)

        let boundary = UUID().uuidString
        var body = Data()

        // file field. The name goes in as raw UTF-8 (WordPress's PHP ignores
        // RFC 2231 `filename*`); a quote or line break would end the header.
        let quoteSafeName = fileName
            .replacingOccurrences(of: "\"", with: "'")
            .replacingOccurrences(of: "\r", with: "")
            .replacingOccurrences(of: "\n", with: "")
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"file\"; filename=\"\(quoteSafeName)\"\r\n".data(using: .utf8)!)

        let mimeType = mimeTypeForExtension((fileName as NSString).pathExtension)
        body.append("Content-Type: \(mimeType)\r\n\r\n".data(using: .utf8)!)
        body.append(fileData)
        body.append("\r\n".data(using: .utf8)!)
        body.append("--\(boundary)--\r\n".data(using: .utf8)!)

        var request = try makeRequest(path: "/media", query: [])
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

        let (_, response) = try await session.uploadReportingProgress(for: request, body: body, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        // Status only, into the support log: request URLs and response bodies
        // used to go to a world-readable file in /tmp in every build.
        SupportLogger.shared.log(
            "[WordPress] Upload \(fileName) → HTTP \(http.statusCode)",
            category: "wordpress",
            level: (200...299).contains(http.statusCode) ? .info : .error
        )

        if http.statusCode == 401 { throw CloudProviderError.unauthorized }
        guard (200...299).contains(http.statusCode) else {
            throw CloudProviderError.serverError(http.statusCode)
        }
    }

    static func uploadFileName(remotePath: String, localURL: URL) -> String {
        let name = (remotePath as NSString).lastPathComponent
        return name.isEmpty || name == "/" ? localURL.lastPathComponent : name
    }

    public func deleteItem(at path: String) async throws {
        // Deletion is permanent (`force=true`), so the id must come from the
        // month folder the path names — never from a library-wide search
        // that could match a same-named file in another month.
        let mediaId = try await resolveMediaId(for: path, allowSearch: false)

        var request = try makeRequest(path: "/media/\(mediaId)", query: [("force", "true")])
        request.httpMethod = "DELETE"

        let (_, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.serverError((response as? HTTPURLResponse)?.statusCode ?? -1)
        }

        mediaIdCache.removeValue(forKey: path)
    }

    public func renameItem(at path: String, to newName: String) async throws {
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

    public func getFileInfo(at path: String) async throws -> CloudFileItem {
        let mediaId = try await resolveMediaId(for: path)

        let request = try makeRequest(path: "/media/\(mediaId)", query: [])
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.notFound(path)
        }

        let item = try JSONDecoder().decode(WPMediaItem.self, from: data)
        return mediaItemToCloudFile(item, basePath: (path as NSString).deletingLastPathComponent)
    }

    /// WordPress media is already served publicly from the uploads
    /// directory, so "sharing" means handing back `source_url` — which is
    /// itself the direct-download URL. Nothing is changed on the server.
    ///
    /// The URL is only reachable if the uploads directory is publicly
    /// served: membership plugins, private sites and maintenance mode can
    /// all make it 403/404, and we deliberately don't promise otherwise.
    public func publicMediaURL(at path: String) async throws -> URL {
        let mediaId = try await resolveMediaId(for: path)

        let request = try makeRequest(path: "/media/\(mediaId)", query: [])
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.notFound(path)
        }

        let item = try JSONDecoder().decode(WPMediaItem.self, from: data)
        guard let source = item.source_url, let url = URL(string: source) else {
            throw CloudProviderError.invalidResponse
        }
        return url
    }

    public func folderSize(path: String) async throws -> Int64 {
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

    public func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
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

    /// One folder per year that has media. The API has no "distinct years"
    /// query, so the oldest and the newest item bound the range and one
    /// single-item probe per year in between says whether it has media.
    /// Exact for any library size — sampling pages, as this used to, left
    /// out the years between page 9 and the last page, and a Mirror from
    /// the library then deleted them.
    private func listYearFolders() async throws -> [CloudFileItem] {
        let byDate: [(String, String)] = [("per_page", "1"), ("orderby", "date")]
        guard let oldest = try await mediaPage(byDate + [("order", "asc")], page: 1)?.items.first else {
            return []   // The library is empty.
        }
        guard let newest = try await mediaPage(byDate + [("order", "desc")], page: 1)?.items.first,
              let firstYear = extractYear(from: oldest.date),
              let lastYear = extractYear(from: newest.date),
              firstYear <= lastYear else {
            throw CloudProviderError.unreadableListing
        }

        var years: Set<Int> = [firstYear, lastYear]
        if lastYear - firstYear > 1 {
            for year in (firstYear + 1)..<lastYear {
                let probe = try await mediaPage(Self.yearQuery(year) + [("per_page", "1")], page: 1)
                if probe?.items.isEmpty == false { years.insert(year) }
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

        let monthNames = ["", "January", "February", "March", "April", "May", "June",
                          "July", "August", "September", "October", "November", "December"]

        for item in try await allMedia(Self.yearQuery(year)) {
            if let month = extractMonth(from: item.date), (1...12).contains(month) { months.insert(month) }
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
        let paddedMonth = String(format: "%02d", month)

        // Calculate next month for the before date
        let nextYear = month == 12 ? year + 1 : year
        let nextMonth = month == 12 ? 1 : month + 1
        let query: [(String, String)] = [
            ("after", "\(year)-\(paddedMonth)-01T00:00:00"),
            ("before", "\(nextYear)-\(String(format: "%02d", nextMonth))-01T00:00:00"),
        ]

        let basePath = "/\(year)/\(paddedMonth)"
        let allItems = try await allMedia(query).map { item in
            let cloudFile = mediaItemToCloudFile(item, basePath: basePath)
            mediaIdCache[cloudFile.path] = item.id
            return cloudFile
        }

        // The listing ran to the end, so whatever it no longer shows is gone
        // and must not resolve to a stale id later.
        let listed = Set(allItems.map(\.path))
        for path in mediaIdCache.keys where path.hasPrefix(basePath + "/") && !listed.contains(path) {
            mediaIdCache.removeValue(forKey: path)
        }

        return allItems
    }

    private static func yearQuery(_ year: Int) -> [(String, String)] {
        [("after", "\(year)-01-01T00:00:00"), ("before", "\(year + 1)-01-01T00:00:00")]
    }

    private static let pageSize = 100

    /// One page of `/media`. Any failure throws: a listing that stopped at a
    /// 429 or 500 and returned what it had would make a sync read everything
    /// after it as deleted. The one exception is WordPress's 400 for a page
    /// past the end, which comes back as nil.
    private func mediaPage(_ query: [(String, String)], page: Int) async throws -> (items: [WPMediaItem], totalPages: Int?)? {
        let request = try makeRequest(path: "/media", query: query + [("page", "\(page)")])
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
        guard (200...299).contains(http.statusCode) else {
            if page > 1 && http.statusCode == 400 { return nil }
            SupportLogger.shared.log(
                "[WordPress] Listing page \(page) failed: HTTP \(http.statusCode)",
                category: "wordpress", level: .error
            )
            if http.statusCode == 401 { throw CloudProviderError.unauthorized }
            throw CloudProviderError.serverError(http.statusCode)
        }
        guard let items = try? JSONDecoder().decode([WPMediaItem].self, from: data) else {
            throw CloudProviderError.unreadableListing
        }
        return (items, http.value(forHTTPHeaderField: "X-WP-TotalPages").flatMap { Int($0) })
    }

    /// Every media item matching `query`, page by page to the very end.
    private func allMedia(_ query: [(String, String)]) async throws -> [WPMediaItem] {
        var all: [WPMediaItem] = []
        var page = 1
        while let result = try await mediaPage(query + [("per_page", "\(Self.pageSize)")], page: page) {
            all.append(contentsOf: result.items)
            if result.items.isEmpty { break }
            // Without the header (a proxy may strip it) a full page means
            // there may be another; WordPress answers 400 past the end.
            let morePages = result.totalPages.map { page < $0 } ?? (result.items.count == Self.pageSize)
            if !morePages { break }
            page += 1
        }
        return all
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

    /// Cache, then a full listing of the month folder, then (unless
    /// `allowSearch` is false) a title search restricted to that same month.
    private func resolveMediaId(for path: String, allowSearch: Bool = true) async throws -> Int {
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
        guard allowSearch else { throw CloudProviderError.notFound(path) }

        // Try searching by filename — but only accept a match filed under
        // the same year and month. The search covers the whole library, and
        // `logo.png` from 2019/02 is not `logo.png` from 2024/05.
        let monthFolder = "/\(year)/\(String(format: "%02d", month))"
        let nameWithoutExt = (fileName as NSString).deletingPathExtension
        let request = try makeRequest(path: "/media", query: [("search", nameWithoutExt)])
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.notFound(path)
        }

        let items = try JSONDecoder().decode([WPMediaItem].self, from: data)
        for item in items {
            let itemFileName = fileNameFromItem(item)
            if itemFileName == fileName, virtualPathPrefix(for: item) == monthFolder {
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
