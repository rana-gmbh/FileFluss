import Foundation

public struct KDriveCredentials: Codable, Sendable {
    let apiToken: String
    /// Not a secret — exposed publicly so callers (e.g. the edit-account
    /// flow) can preserve the chosen workspace when re-authenticating.
    public let driveId: Int
    let userEmail: String
}

public actor KDriveAPIClient {
    let credentials: KDriveCredentials
    private let session: URLSession
    private let baseURL = "https://api.infomaniak.com"

    // Cache path → fileId mapping for navigation
    private var pathToId: [String: Int] = ["/": 0] // root placeholder, set after init

    public init(credentials: KDriveCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)
        // Root folder ID is typically the drive's root
        pathToId["/"] = credentials.driveId > 0 ? 0 : 0
    }

    // MARK: - Drive Info

    public func fetchRootFileId() async throws -> Int {
        // Try to get root file ID from drive info
        if let response: KDriveResponse<KDriveDriveInfo> = try? await request(.get, path: "/2/drive/\(credentials.driveId)") {
            return response.data.rootFileId
        }
        // Fallback: try listing files at root (ID 1 is common default)
        // Verify by listing — if it works, 1 is the root
        let _: KDriveResponse<[KDriveFileMetadata]> = try await request(.get, path: "/2/drive/\(credentials.driveId)/files/1/files")
        return 1
    }

    /// Infomaniak's `/drive/{driveId}` endpoint returns `size` (total
    /// allotted bytes) and `used_size` (consumed bytes). Workspaces on
    /// the Free tier have a fixed cap; paid tiers carry their pack's
    /// configured limit. A 0 total means the API didn't surface the
    /// figure, in which case we leave it nil so the bar renders just
    /// used.
    public func storageQuota() async throws -> CloudStorageQuota? {
        struct Quota: Decodable {
            let size: Int64?
            let usedSize: Int64?
            private enum CodingKeys: String, CodingKey {
                case size
                case usedSize = "used_size"
            }
        }
        let response: KDriveResponse<Quota> = try await request(
            .get,
            path: "/2/drive/\(credentials.driveId)"
        )
        let total = (response.data.size ?? 0) > 0 ? response.data.size : nil
        return CloudStorageQuota(usedBytes: response.data.usedSize ?? 0, totalBytes: total)
    }

    public func setRootId(_ id: Int) {
        pathToId["/"] = id
    }

    // MARK: - Folder Operations

    func listFolder(fileId: Int) async throws -> [KDriveFileMetadata] {
        // kDrive's default per_page on this endpoint is small (10), so a single
        // unpaginated call silently truncates folders with more entries. Loop
        // until a page returns fewer items than per_page.
        let perPage = 500
        var page = 1
        var allItems: [KDriveFileMetadata] = []
        while true {
            let response: KDriveResponse<[KDriveFileMetadata]> = try await request(
                .get,
                path: "/2/drive/\(credentials.driveId)/files/\(fileId)/files",
                queryItems: [
                    URLQueryItem(name: "order_by", value: "name"),
                    URLQueryItem(name: "order", value: "asc"),
                    URLQueryItem(name: "per_page", value: "\(perPage)"),
                    URLQueryItem(name: "page", value: "\(page)"),
                ]
            )
            allItems.append(contentsOf: response.data)
            if response.data.count < perPage { break }
            page += 1
        }
        return allItems
    }

    public func listFolder(path: String) async throws -> [CloudFileItem] {
        let fileId = try await resolvePathToId(path)
        let items = try await listFolder(fileId: fileId)
        return items.map { $0.toCloudFileItem(parentPath: path) }
    }

    public func createFolder(parentId: Int, name: String) async throws {
        let url = URL(string: "\(baseURL)/2/drive/\(credentials.driveId)/files/\(parentId)/directory")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(["name": name])

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] POST directory → HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    public func createFolder(path: String) async throws {
        if (try? await resolvePathToId(path)) != nil { return }

        let parentPath = (path as NSString).deletingLastPathComponent
        let folderName = (path as NSString).lastPathComponent
        let parentId = try await resolvePathToId(parentPath)
        try await createFolder(parentId: parentId, name: folderName)
        // Cache the new folder's path → refresh parent to get the ID
        let items = try await listFolder(fileId: parentId)
        if let created = items.first(where: { $0.name == folderName }) {
            cachePath(path, fileId: created.id)
        }
    }

    // MARK: - File Operations

    public func downloadFile(fileId: Int, to localURL: URL, onBytes: ByteProgressHandler? = nil) async throws {
        let url = URL(string: "\(baseURL)/2/drive/\(credentials.driveId)/files/\(fileId)/download")!
        var request = URLRequest(url: url)
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")

        let (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let fileId = try await resolvePathToId(remotePath)
        try await downloadFile(fileId: fileId, to: localURL, onBytes: onBytes)
    }

    public func uploadFile(from localURL: URL, toFolderId folderId: Int, fileName: String, onBytes: ByteProgressHandler? = nil) async throws {
        let fileData = try Data(contentsOf: localURL)

        var queryItems: [URLQueryItem] = [
            URLQueryItem(name: "directory_id", value: "\(folderId)"),
            URLQueryItem(name: "file_name", value: fileName),
            URLQueryItem(name: "total_size", value: "\(fileData.count)"),
            URLQueryItem(name: "conflict", value: "version"),
        ]

        // Preserve the local file's timestamps on the cloud side so sync
        // diffs stay stable and Finder-style drags don't reset mod dates.
        let attrs = try? FileManager.default.attributesOfItem(atPath: localURL.path)
        if let modDate = attrs?[.modificationDate] as? Date {
            queryItems.append(URLQueryItem(name: "last_modified_at", value: "\(Int64(modDate.timeIntervalSince1970))"))
        }
        if let createdDate = attrs?[.creationDate] as? Date {
            queryItems.append(URLQueryItem(name: "created_at", value: "\(Int64(createdDate.timeIntervalSince1970))"))
        }

        var components = URLComponents(string: "\(baseURL)/3/drive/\(credentials.driveId)/upload")!
        components.queryItems = queryItems

        guard let url = components.url else {
            throw CloudProviderError.invalidResponse
        }

        // Infomaniak's upload service is on a separate subsystem from the
        // Drive metadata API. A freshly-created folder is visible to listing
        // immediately but the upload endpoint occasionally 404s for ~1s
        // afterwards. Retry a small number of times before giving up.
        var attempt = 0
        while true {
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")
            request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")

            KDriveProvider.log("[kDrive API] POST upload → folderId=\(folderId), fileName=\(fileName), size=\(fileData.count), attempt=\(attempt + 1)")
            let (data, response) = try await session.uploadReportingProgress(for: request, body: fileData, onBytes: onBytes)
            let http = response as? HTTPURLResponse
            let status = http?.statusCode ?? 0
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] POST upload → HTTP \(status): \(bodyStr.prefix(500))")
            if let http, (200...299).contains(http.statusCode) { return }
            if status == 404 && attempt < 3 {
                attempt += 1
                try? await Task.sleep(nanoseconds: UInt64(attempt * 500_000_000))
                continue
            }
            throw Self.mapHTTPError(statusCode: status)
        }
    }

    public func deleteFile(fileId: Int) async throws {
        let url = URL(string: "\(baseURL)/2/drive/\(credentials.driveId)/files/\(fileId)")!
        var request = URLRequest(url: url)
        request.httpMethod = "DELETE"
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] DELETE files/\(fileId) → HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    public func deleteFile(path: String) async throws {
        let fileId = try await resolvePathToId(path)
        try await deleteFile(fileId: fileId)
        pathToId.removeValue(forKey: path)
    }

    public func renameFile(fileId: Int, to newName: String) async throws {
        let url = URL(string: "\(baseURL)/2/drive/\(credentials.driveId)/files/\(fileId)/rename")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(["name": newName])

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            let http = response as? HTTPURLResponse
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] POST rename → HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    public func renameFile(path: String, to newName: String) async throws {
        let fileId = try await resolvePathToId(path)
        try await renameFile(fileId: fileId, to: newName)
        // Update cache: remove old path, add new path
        pathToId.removeValue(forKey: path)
        let parentPath = (path as NSString).deletingLastPathComponent
        let newPath = parentPath == "/" ? "/\(newName)" : "\(parentPath)/\(newName)"
        pathToId[newPath] = fileId
    }

    // MARK: - Server-side move / copy

    /// Infomaniak's `move` endpoint relocates a file/folder to a new
    /// directory. Pass `newName` to rename atomically; omit to keep the
    /// existing name.
    public func moveFile(fileId: Int, toDirectoryId: Int, newName: String?) async throws {
        let url = URL(string: "\(baseURL)/2/drive/\(credentials.driveId)/files/\(fileId)/move/\(toDirectoryId)")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")
        if let newName {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONEncoder().encode(["name": newName])
        }
        let (data, response) = try await session.data(for: request)
        let http = response as? HTTPURLResponse
        guard let http, (200...299).contains(http.statusCode) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] POST move \(fileId) → dir \(toDirectoryId) → HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    /// Server-side copy via Infomaniak's `duplicate` endpoint.
    public func duplicateFile(fileId: Int, toDirectoryId: Int, newName: String?) async throws {
        let url = URL(string: "\(baseURL)/2/drive/\(credentials.driveId)/files/\(fileId)/duplicate/\(toDirectoryId)")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")
        if let newName {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONEncoder().encode(["name": newName])
        }
        let (data, response) = try await session.data(for: request)
        let http = response as? HTTPURLResponse
        guard let http, (200...299).contains(http.statusCode) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] POST duplicate \(fileId) → dir \(toDirectoryId) → HTTP \(http?.statusCode ?? 0): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http?.statusCode ?? 0)
        }
    }

    public func moveItem(path: String, toPath newPath: String) async throws {
        let fileId = try await resolvePathToId(path)
        let destParentPath = (newPath as NSString).deletingLastPathComponent
        let destName = (newPath as NSString).lastPathComponent
        let destParentId = try await resolvePathToId(destParentPath.isEmpty ? "/" : destParentPath)
        let sourceName = (path as NSString).lastPathComponent
        let renameDuringMove = sourceName == destName ? nil : destName

        try await moveFile(fileId: fileId, toDirectoryId: destParentId, newName: renameDuringMove)

        // Refresh cache: source path no longer valid, new path now points
        // at the same file id.
        pathToId.removeValue(forKey: path)
        pathToId[newPath] = fileId
    }

    public func copyItem(path: String, toPath newPath: String) async throws {
        let fileId = try await resolvePathToId(path)
        let destParentPath = (newPath as NSString).deletingLastPathComponent
        let destName = (newPath as NSString).lastPathComponent
        let destParentId = try await resolvePathToId(destParentPath.isEmpty ? "/" : destParentPath)
        let sourceName = (path as NSString).lastPathComponent
        let renameDuringCopy = sourceName == destName ? nil : destName

        try await duplicateFile(fileId: fileId, toDirectoryId: destParentId, newName: renameDuringCopy)
        // Don't cache newPath — the duplicate gets a new file id we don't
        // know without listing the destination directory.
    }

    func stat(fileId: Int) async throws -> KDriveFileMetadata {
        let response: KDriveResponse<KDriveFileMetadata> = try await request(
            .get,
            path: "/2/drive/\(credentials.driveId)/files/\(fileId)"
        )
        return response.data
    }

    public func folderSize(path: String) async throws -> Int64 {
        let fileId = try await resolvePathToId(path)
        return try await calculateFolderSizeRecursively(fileId: fileId)
    }

    private func calculateFolderSizeRecursively(fileId: Int) async throws -> Int64 {
        let items = try await listFolder(fileId: fileId)
        var total: Int64 = 0
        for item in items {
            if item.isFolder {
                total += try await calculateFolderSizeRecursively(fileId: item.id)
            } else {
                total += item.size ?? 0
            }
        }
        return total
    }

    // MARK: - Share links

    /// Request body for `POST /2/drive/{driveId}/files/{fileId}/link`.
    /// Optional fields are dropped by `JSONEncoder` when nil, which keeps
    /// `valid_until` out of the request entirely on free tiers where the
    /// field isn't allowed.
    private struct ShareLinkBody: Encodable {
        /// `"public"` for an open link, `"password"` when one is set — the
        /// password alone doesn't switch the mode.
        let right: String
        let password: String?
        let valid_until: Int?
        let can_download: Bool
        let can_edit: Bool
    }

    /// Creates a public share link for the file or folder at `path`.
    ///
    /// Expiry is a paid kSuite feature: free workspaces reject `valid_until`
    /// with their own explanation, so the response body's message is surfaced
    /// verbatim instead of a bare status code. Unlike the other calls in this
    /// file this one doesn't go through `request(_:path:…)` for exactly that
    /// reason — the generic helper discards the body on failure.
    public func createShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        let fileId = try await resolvePathToId(path)
        let url = URL(string: "\(baseURL)/2/drive/\(credentials.driveId)/files/\(fileId)/link")!

        let password = (options.password?.isEmpty ?? true) ? nil : options.password
        let body = ShareLinkBody(
            right: password == nil ? "public" : "password",
            password: password,
            // Infomaniak dates are Unix timestamps throughout this API
            // (`last_modified_at`, `created_at`), so `valid_until` follows.
            valid_until: options.expiry.map { Int($0.timeIntervalSince1970) },
            can_download: options.allowDownload,
            can_edit: false
        )

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(body)

        let (data, response) = try await session.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200...299).contains(status) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] POST files/\(fileId)/link → HTTP \(status): \(bodyStr.prefix(500))")
            if let message = (try? JSONDecoder().decode(KDriveErrorEnvelope.self, from: data))?.error?.message {
                throw CloudProviderError.commandFailed(message)
            }
            throw Self.mapHTTPError(statusCode: status)
        }

        let parsed = try JSONDecoder().decode(KDriveResponse<KDriveShareLink>.self, from: data)
        guard let linkURL = URL(string: parsed.data.url) else {
            throw CloudProviderError.invalidResponse
        }

        // `capabilities` reports what the link ended up permitting, which can
        // be less than we asked for on restricted workspaces — worth telling
        // the user rather than silently disagreeing with the sheet.
        var note: String?
        if options.allowDownload, parsed.data.capabilities?.canDownload == false {
            note = L10n.text("kDrive created the link without download permission — recipients can view the file only.")
        }

        // kDrive documents no direct-download variant of the share URL — the
        // link always lands on Infomaniak's viewer page.
        return CloudShareLink(
            url: linkURL,
            directDownloadURL: nil,
            expiresAt: parsed.data.validUntil.map { Date(timeIntervalSince1970: TimeInterval($0)) },
            hasPassword: parsed.data.right == "password" || password != nil,
            note: note
        )
    }

    /// Update body for `PUT /2/drive/{driveId}/files/{fileId}/link`.
    ///
    /// Same fields as `ShareLinkBody`, but every one is encoded explicitly —
    /// including `valid_until: null`. PUT merges, so an *omitted* key leaves
    /// the server's current value alone, and clearing an expiry has to be said
    /// out loud. `ShareLinkBody`'s synthesised encoder drops nil keys, which is
    /// exactly what creation wants and exactly what an update must not do.
    private struct ShareLinkUpdateBody: Encodable {
        let right: String
        let password: String?
        let valid_until: Int?
        let can_download: Bool
        let can_edit: Bool

        private enum CodingKeys: String, CodingKey {
            case right, password, valid_until, can_download, can_edit
        }

        func encode(to encoder: Encoder) throws {
            var c = encoder.container(keyedBy: CodingKeys.self)
            try c.encode(right, forKey: .right)
            // A nil password with `right: "public"` is how the password comes
            // off again, so it is sent as an explicit null too.
            try c.encode(password, forKey: .password)
            try c.encode(valid_until, forKey: .valid_until)
            try c.encode(can_download, forKey: .can_download)
            try c.encode(can_edit, forKey: .can_edit)
        }
    }

    /// The public link this file already has, or nil when it has none.
    ///
    /// Infomaniak reports "no link on this file" as a 404 (with an
    /// `object_not_found`-style code), which is *not* a failure here: it is the
    /// answer. Every other status is surfaced with the API's own message.
    public func existingShareLink(at path: String) async throws -> CloudShareLink? {
        let fileId = try await resolvePathToId(path)
        let url = URL(string: "\(baseURL)/2/drive/\(credentials.driveId)/files/\(fileId)/link")!

        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let (data, response) = try await session.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 404 { return nil }
        guard (200...299).contains(status) else {
            let envelope = try? JSONDecoder().decode(KDriveErrorEnvelope.self, from: data)
            // Some deployments answer a link-less file with 403/422 and a
            // not-found code rather than a 404 status.
            if let code = envelope?.error?.code, code.contains("not_found") { return nil }
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] GET files/\(fileId)/link → HTTP \(status): \(bodyStr.prefix(500))")
            if let message = envelope?.error?.message {
                throw CloudProviderError.commandFailed(message)
            }
            throw Self.mapHTTPError(statusCode: status)
        }

        // A success whose `data` isn't a link object (`null` on some
        // deployments) is the other way this API says "no link on this file".
        guard let parsed = try? JSONDecoder().decode(KDriveResponse<KDriveShareLink>.self, from: data) else {
            KDriveProvider.log("[kDrive API] GET files/\(fileId)/link → 2xx without a link object, treating as not shared")
            return nil
        }
        return try Self.shareLink(from: parsed.data, requestedPassword: false, requestedDownload: false)
    }

    /// Changes the existing link's password, expiry and download permission in
    /// place. kDrive keeps the same URL.
    public func updateShareLink(at path: String, options: ShareLinkOptions) async throws -> CloudShareLink {
        let fileId = try await resolvePathToId(path)
        let url = URL(string: "\(baseURL)/2/drive/\(credentials.driveId)/files/\(fileId)/link")!

        let password = (options.password?.isEmpty ?? true) ? nil : options.password
        let body = ShareLinkUpdateBody(
            // The mode has to follow the password: leaving `right: "password"`
            // in place while sending a null password is rejected.
            right: password == nil ? "public" : "password",
            password: password,
            valid_until: options.expiry.map { Int($0.timeIntervalSince1970) },
            can_download: options.allowDownload,
            can_edit: false
        )

        var request = URLRequest(url: url)
        request.httpMethod = "PUT"
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(body)

        let (data, response) = try await session.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200...299).contains(status) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] PUT files/\(fileId)/link → HTTP \(status): \(bodyStr.prefix(500))")
            if let message = (try? JSONDecoder().decode(KDriveErrorEnvelope.self, from: data))?.error?.message {
                throw CloudProviderError.commandFailed(message)
            }
            throw Self.mapHTTPError(statusCode: status)
        }

        if let parsed = try? JSONDecoder().decode(KDriveResponse<KDriveShareLink>.self, from: data) {
            return try Self.shareLink(
                from: parsed.data,
                requestedPassword: password != nil,
                requestedDownload: options.allowDownload
            )
        }
        // Several Infomaniak write endpoints answer `{"result":"success",
        // "data":true}` instead of echoing the object back. Read the link
        // rather than assuming the request applied verbatim.
        guard let fresh = try await existingShareLink(at: path) else {
            throw CloudProviderError.invalidResponse
        }
        return fresh
    }

    /// Withdraws the public link. A 404 means there was none, which is the
    /// outcome the caller wanted, so it isn't reported as a failure.
    public func removeShareLink(at path: String) async throws {
        let fileId = try await resolvePathToId(path)
        let url = URL(string: "\(baseURL)/2/drive/\(credentials.driveId)/files/\(fileId)/link")!

        var request = URLRequest(url: url)
        request.httpMethod = "DELETE"
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let (data, response) = try await session.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 404 { return }
        guard (200...299).contains(status) else {
            let envelope = try? JSONDecoder().decode(KDriveErrorEnvelope.self, from: data)
            if let code = envelope?.error?.code, code.contains("not_found") { return }
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] DELETE files/\(fileId)/link → HTTP \(status): \(bodyStr.prefix(500))")
            if let message = envelope?.error?.message {
                throw CloudProviderError.commandFailed(message)
            }
            throw Self.mapHTTPError(statusCode: status)
        }
    }

    /// Shared mapping from Infomaniak's link object to `CloudShareLink`.
    /// `requestedPassword`/`requestedDownload` describe what the caller asked
    /// for, and only matter when the server leaves a field out (create/update);
    /// a plain lookup passes false for both.
    private static func shareLink(
        from link: KDriveShareLink,
        requestedPassword: Bool,
        requestedDownload: Bool
    ) throws -> CloudShareLink {
        guard let linkURL = URL(string: link.url) else {
            throw CloudProviderError.invalidResponse
        }

        // `capabilities` reports what the link ended up permitting, which can
        // be less than we asked for on restricted workspaces — worth telling
        // the user rather than silently disagreeing with the sheet.
        var note: String?
        if requestedDownload, link.capabilities?.canDownload == false {
            note = L10n.text("kDrive created the link without download permission — recipients can view the file only.")
        }

        // kDrive documents no direct-download variant of the share URL — the
        // link always lands on Infomaniak's viewer page.
        return CloudShareLink(
            url: linkURL,
            directDownloadURL: nil,
            expiresAt: link.validUntil.map { Date(timeIntervalSince1970: TimeInterval($0)) },
            hasPassword: link.right == "password" || requestedPassword,
            note: note
        )
    }

    // MARK: - User Info

    public func userInfo() async throws -> String {
        return credentials.userEmail
    }

    // MARK: - Path Resolution

    public func resolvePathToId(_ path: String) async throws -> Int {
        if let cached = pathToId[path] {
            return cached
        }

        // Resolve component by component
        let components = path.split(separator: "/", omittingEmptySubsequences: true)
        guard var currentId = pathToId["/"] else {
            throw CloudProviderError.notAuthenticated
        }
        var currentPath = ""

        for component in components {
            currentPath += "/\(component)"
            if let cached = pathToId[currentPath] {
                currentId = cached
                continue
            }
            let items = try await listFolder(fileId: currentId)
            guard let match = items.first(where: { $0.name == String(component) }) else {
                throw CloudProviderError.notFound(String(component))
            }
            currentId = match.id
            pathToId[currentPath] = currentId
        }
        return currentId
    }

    public func cachePath(_ path: String, fileId: Int) {
        pathToId[path] = fileId
    }

    // MARK: - HTTP

    private enum HTTPMethod: String {
        case get = "GET"
        case post = "POST"
        case put = "PUT"
        case delete = "DELETE"
    }

    private func request<T: Decodable>(_ method: HTTPMethod, path: String, queryItems: [URLQueryItem] = [], body: (any Encodable)? = nil) async throws -> T {
        var components = URLComponents(string: "\(baseURL)\(path)")!
        if !queryItems.isEmpty {
            components.queryItems = (components.queryItems ?? []) + queryItems
        }

        guard let url = components.url else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = method.rawValue
        request.setValue("Bearer \(credentials.apiToken)", forHTTPHeaderField: "Authorization")

        if let body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONEncoder().encode(body)
        }

        let (data, response) = try await session.data(for: request)

        guard let http = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        guard (200...299).contains(http.statusCode) else {
            let bodyStr = String(data: data, encoding: .utf8) ?? ""
            KDriveProvider.log("[kDrive API] \(method.rawValue) \(path) → HTTP \(http.statusCode): \(bodyStr.prefix(500))")
            throw Self.mapHTTPError(statusCode: http.statusCode)
        }

        return try JSONDecoder().decode(T.self, from: data)
    }

    private static func mapHTTPError(statusCode: Int) -> CloudProviderError {
        switch statusCode {
        case 401: return .notAuthenticated
        case 403: return .unauthorized
        case 404: return .notFound("Resource not found")
        case 429: return .rateLimited
        case 507: return .quotaExceeded
        default: return .serverError(statusCode)
        }
    }
}

// MARK: - kDrive API Response Types

struct KDriveResponse<T: Decodable>: Decodable {
    let result: String
    let data: T
}

struct KDriveDriveInfo: Decodable {
    let id: Int
    let name: String

    private enum CodingKeys: String, CodingKey {
        case id, name
        case rootFileId = "root_file_id"
    }

    let rootFileId: Int

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(Int.self, forKey: .id)
        name = try container.decode(String.self, forKey: .name)
        // Root file ID might be nested or at top level
        rootFileId = (try? container.decode(Int.self, forKey: .rootFileId)) ?? 1
    }
}

/// `data` payload of `POST /2/drive/{driveId}/files/{fileId}/link`.
/// `valid_until` is null for a link that never expires, and `capabilities`
/// reports what the created link actually permits.
struct KDriveShareLink: Decodable {
    struct Capabilities: Decodable {
        let canEdit: Bool?
        let canDownload: Bool?

        private enum CodingKeys: String, CodingKey {
            case canEdit = "can_edit"
            case canDownload = "can_download"
        }
    }

    let url: String
    /// `"public"` or `"password"` — echoed back from the request.
    let right: String?
    let validUntil: Int?
    let capabilities: Capabilities?

    private enum CodingKeys: String, CodingKey {
        case url, right, capabilities
        case validUntil = "valid_until"
    }
}

/// Infomaniak's failure envelope: `{"result":"error","error":{"code":…,
/// "description":…}}`. Some endpoints send `message` instead of
/// `description`, so both are accepted and the code is the last resort.
struct KDriveErrorEnvelope: Decodable {
    struct APIError: Decodable {
        let code: String?
        let errorDescription: String?
        let messageText: String?

        /// Best human-readable text this error carries. The error code is the
        /// last resort — it's terse (`"validation_failed"`) but still more
        /// useful than an HTTP status on its own.
        var message: String? {
            [errorDescription, messageText, code]
                .compactMap { $0 }
                .first { !$0.isEmpty }
        }

        private enum CodingKeys: String, CodingKey {
            case code
            case errorDescription = "description"
            case messageText = "message"
        }
    }
    let result: String?
    let error: APIError?
}

struct KDriveFileMetadata: Decodable {
    let id: Int
    let name: String
    let type: String // "file" or "dir"
    let size: Int64?
    let lastModifiedAt: Int?
    let createdAt: Int?

    private enum CodingKeys: String, CodingKey {
        case id, name, type, size
        case lastModifiedAt = "last_modified_at"
        case createdAt = "created_at"
    }

    var isFolder: Bool { type == "dir" }

    public func toCloudFileItem(parentPath: String) -> CloudFileItem {
        let itemPath: String
        if parentPath == "/" {
            itemPath = "/\(name)"
        } else {
            itemPath = "\(parentPath)/\(name)"
        }

        let modDate: Date
        if let ts = lastModifiedAt {
            modDate = Date(timeIntervalSince1970: TimeInterval(ts))
        } else {
            modDate = Date.distantPast
        }

        return CloudFileItem(
            id: "\(type == "dir" ? "d" : "f")\(id)",
            name: name,
            path: itemPath,
            isDirectory: isFolder,
            size: size ?? 0,
            modificationDate: modDate,
            checksum: nil
        )
    }
}

public struct KDriveDriveListItem: Decodable, Sendable {
    public let id: Int
    public let name: String
}
