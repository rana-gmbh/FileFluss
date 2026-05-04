import Foundation

struct PCloudCredentials: Codable, Sendable {
    let accessToken: String
    let hostname: String
    let userId: UInt64
}

actor PCloudAPIClient {
    let credentials: PCloudCredentials
    private let session: URLSession

    init(credentials: PCloudCredentials) {
        self.credentials = credentials
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        self.session = URLSession(configuration: config)
    }

    private var baseURL: String { "https://\(credentials.hostname)" }

    // MARK: - Folder Operations

    func listFolder(path: String) async throws -> [CloudFileItem] {
        let params = [
            "path": path,
            "timeformat": "timestamp",
        ]
        let response: PCloudListFolderResponse = try await request("listfolder", params: params)
        return response.metadata.contents?.map { $0.toCloudFileItem(parentPath: path) } ?? []
    }

    func folderSize(path: String) async throws -> Int64 {
        let params = [
            "path": path,
            "recursive": "1",
            "timeformat": "timestamp",
        ]
        let response: PCloudListFolderResponse = try await request("listfolder", params: params)
        return sumSize(of: response.metadata)
    }

    private func sumSize(of folder: PCloudFolderMetadata) -> Int64 {
        var total: Int64 = 0
        for item in folder.contents ?? [] {
            if item.isfolder {
                if let subContents = item.folderContents {
                    total += sumSize(of: subContents)
                }
            } else {
                total += item.size ?? 0
            }
        }
        return total
    }

    func createFolder(path: String) async throws {
        let params = ["path": path]
        let _: PCloudBasicResponse = try await request("createfolderifnotexists", params: params)
    }

    func deleteFolder(path: String) async throws {
        let params = ["path": path]
        let _: PCloudBasicResponse = try await request("deletefolderrecursive", params: params)
    }

    func renameFile(path: String, toName newName: String) async throws {
        let toPath = ((path as NSString).deletingLastPathComponent as NSString).appendingPathComponent(newName)
        try await renameFile(path: path, toPath: toPath)
    }

    func renameFolder(path: String, toName newName: String) async throws {
        let toPath = ((path as NSString).deletingLastPathComponent as NSString).appendingPathComponent(newName)
        try await renameFolder(path: path, toPath: toPath)
    }

    /// pCloud's `renamefile` accepts an arbitrary destination path, so it
    /// doubles as a server-side cross-folder move. The same is true of
    /// `renamefolder` for directories.
    func renameFile(path: String, toPath: String) async throws {
        let params = ["path": path, "topath": toPath]
        let _: PCloudBasicResponse = try await request("renamefile", params: params)
    }

    func renameFolder(path: String, toPath: String) async throws {
        let params = ["path": path, "topath": toPath]
        let _: PCloudBasicResponse = try await request("renamefolder", params: params)
    }

    func copyFile(path: String, toPath: String) async throws {
        let params = ["path": path, "topath": toPath]
        let _: PCloudBasicResponse = try await request("copyfile", params: params)
    }

    func copyFolder(path: String, toPath: String) async throws {
        let params = ["path": path, "topath": toPath]
        let _: PCloudBasicResponse = try await request("copyfolder", params: params)
    }

    // MARK: - File Operations

    func stat(path: String) async throws -> CloudFileItem {
        let params = [
            "path": path,
            "timeformat": "timestamp",
        ]
        let response: PCloudStatResponse = try await request("stat", params: params)
        let parentPath = (path as NSString).deletingLastPathComponent
        return response.metadata.toCloudFileItem(parentPath: parentPath)
    }

    func deleteFile(path: String) async throws {
        // pCloud quirks this handles:
        //   * Error 2055 on the first deletes after a bulk upload — the
        //     file's metadata is briefly locked while pCloud processes the
        //     upload. Transient; retry with backoff.
        //   * notFound on attempt 0 when the path is actually a folder —
        //     must propagate so PCloudProvider.deleteItem can fall through
        //     to deleteFolder.
        //   * Shadow duplicates occasionally left after rapid upload+replace
        //     cycles — loop the path-based delete until notFound.
        var deletedOnce = false
        for attempt in 0..<6 {
            do {
                let _: PCloudBasicResponse = try await request("deletefile", params: ["path": path])
                deletedOnce = true
            } catch CloudProviderError.notFound {
                if !deletedOnce && attempt == 0 {
                    throw CloudProviderError.notFound("File not found: \(path)")
                }
                return
            } catch CloudProviderError.serverError(let code) where code == 2055 {
                // Metadata locked — back off and retry (250ms, 500ms, … up to ~3.75s).
                try? await Task.sleep(nanoseconds: UInt64(250_000_000) * UInt64(attempt + 1))
                continue
            } catch {
                if !deletedOnce { throw error }
                break
            }
        }
        do {
            let _: PCloudStatResponse = try await request("stat", params: ["path": path])
            throw CloudProviderError.serverError(0)
        } catch CloudProviderError.notFound {
            return
        }
    }

    func getFileLink(path: String) async throws -> URL {
        let params = ["path": path]
        let response: PCloudFileLinkResponse = try await request("getfilelink", params: params)
        guard let host = response.hosts?.first, let filePath = response.path else {
            throw CloudProviderError.invalidResponse
        }
        guard let url = URL(string: "https://\(host)\(filePath)") else {
            throw CloudProviderError.invalidResponse
        }
        return url
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let downloadURL = try await getFileLink(path: remotePath)
        let request = URLRequest(url: downloadURL)
        let (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        guard let httpResponse = response as? HTTPURLResponse,
              (200...299).contains(httpResponse.statusCode) else {
            throw CloudProviderError.invalidResponse
        }
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    func uploadFile(from localURL: URL, toFolder folderPath: String, fileName: String) async throws {
        try await uploadFile(from: localURL, toFolder: folderPath, fileName: fileName, onBytes: nil)
    }

    func uploadFile(from localURL: URL, toFolder folderPath: String, fileName: String, onBytes: ByteProgressHandler?) async throws {
        var urlString = "\(baseURL)/uploadfile?auth=\(credentials.accessToken)&path=\(folderPath.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? folderPath)&filename=\(fileName.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? fileName)&nopartial=1"

        if let modDate = (try? FileManager.default.attributesOfItem(atPath: localURL.path)[.modificationDate]) as? Date {
            urlString += "&mtime=\(Int64(modDate.timeIntervalSince1970))"
        }
        if let createdDate = (try? FileManager.default.attributesOfItem(atPath: localURL.path)[.creationDate]) as? Date {
            urlString += "&ctime=\(Int64(createdDate.timeIntervalSince1970))"
        }

        guard let url = URL(string: urlString) else {
            throw CloudProviderError.invalidResponse
        }

        let fileData = try Data(contentsOf: localURL)
        let boundary = UUID().uuidString
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

        var body = Data()
        body.append("--\(boundary)\r\n".data(using: .utf8)!)
        body.append("Content-Disposition: form-data; name=\"file\"; filename=\"\(fileName)\"\r\n".data(using: .utf8)!)
        body.append("Content-Type: application/octet-stream\r\n\r\n".data(using: .utf8)!)
        body.append(fileData)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)

        let (responseData, httpResponse) = try await session.uploadReportingProgress(for: request, body: body, onBytes: onBytes)
        guard let http = httpResponse as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            throw CloudProviderError.invalidResponse
        }

        let result = try JSONDecoder().decode(PCloudBasicResponse.self, from: responseData)
        if result.result != 0 {
            throw Self.mapError(code: result.result)
        }
    }

    // MARK: - User Info

    func userInfo() async throws -> PCloudUserInfo {
        let response: PCloudUserInfoResponse = try await request("userinfo", params: [:])
        return PCloudUserInfo(
            email: response.email ?? "",
            userId: response.userid ?? 0,
            quota: response.quota ?? 0,
            usedQuota: response.usedquota ?? 0
        )
    }

    // MARK: - HTTP

    private func request<T: Decodable>(_ method: String, params: [String: String]) async throws -> T {
        var components = URLComponents(string: "\(baseURL)/\(method)")!
        var queryItems = [URLQueryItem(name: "auth", value: credentials.accessToken)]
        for (key, value) in params {
            queryItems.append(URLQueryItem(name: key, value: value))
        }
        components.queryItems = queryItems

        guard let url = components.url else {
            throw CloudProviderError.invalidResponse
        }

        let (data, response) = try await session.data(from: url)

        guard let httpResponse = response as? HTTPURLResponse else {
            throw CloudProviderError.invalidResponse
        }

        guard (200...299).contains(httpResponse.statusCode) else {
            throw CloudProviderError.serverError(httpResponse.statusCode)
        }

        // Check pCloud result code
        if let basicResult = try? JSONDecoder().decode(PCloudBasicResponse.self, from: data),
           basicResult.result != 0 {
            throw Self.mapError(code: basicResult.result)
        }

        return try JSONDecoder().decode(T.self, from: data)
    }

    private static func mapError(code: Int) -> CloudProviderError {
        switch code {
        case 1000: return .notAuthenticated
        case 2000: return .unauthorized
        case 2003: return .unauthorized
        case 2005: return .notFound("Directory not found")
        case 2009: return .notFound("File not found")
        case 2008: return .quotaExceeded
        case 4000: return .rateLimited
        default: return .serverError(code)
        }
    }
}

// MARK: - pCloud API Response Types

struct PCloudBasicResponse: Decodable {
    let result: Int
}

struct PCloudListFolderResponse: Decodable {
    let result: Int
    let metadata: PCloudFolderMetadata
}

struct PCloudStatResponse: Decodable {
    let result: Int
    let metadata: PCloudItemMetadata
}

struct PCloudFileLinkResponse: Decodable {
    let result: Int
    let path: String?
    let hosts: [String]?
}

struct PCloudUserInfoResponse: Decodable {
    let result: Int
    let email: String?
    let userid: UInt64?
    let quota: Int64?
    let usedquota: Int64?
}

struct PCloudFolderMetadata: Decodable {
    let name: String?
    let folderid: UInt64?
    let contents: [PCloudItemMetadata]?
}

struct PCloudItemMetadata: Decodable {
    let name: String
    let isfolder: Bool
    let fileid: UInt64?
    let folderid: UInt64?
    let size: Int64?
    let modified: TimeInterval?
    let created: TimeInterval?
    let contenttype: String?
    let hash: UInt64?
    let icon: String?
    let contents: [PCloudItemMetadata]?

    var folderContents: PCloudFolderMetadata? {
        guard isfolder else { return nil }
        return PCloudFolderMetadata(name: name, folderid: folderid, contents: contents)
    }

    func toCloudFileItem(parentPath: String) -> CloudFileItem {
        let itemPath: String
        if parentPath == "/" {
            itemPath = "/\(name)"
        } else {
            itemPath = "\(parentPath)/\(name)"
        }

        let modDate: Date
        if let ts = modified {
            modDate = Date(timeIntervalSince1970: ts)
        } else {
            modDate = Date.distantPast
        }

        return CloudFileItem(
            id: isfolder ? "d\(folderid ?? 0)" : "f\(fileid ?? 0)",
            name: name,
            path: itemPath,
            isDirectory: isfolder,
            size: size ?? 0,
            modificationDate: modDate,
            checksum: hash.map { String($0) }
        )
    }
}

struct PCloudUserInfo: Sendable {
    let email: String
    let userId: UInt64
    let quota: Int64
    let usedQuota: Int64
}
