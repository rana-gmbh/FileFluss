import Foundation
import os

private let goProLog = Logger(subsystem: "com.rana.FileFluss", category: "goProAPIClient")

/// Talks to a single GoPro camera's local HTTP server (Open GoPro API).
///
/// Base URL is `http://<ip>:8080`; the local API requires no authentication.
/// The camera exposes its SD-card media under `/videos/DCIM/<dir>/<file>` and
/// a JSON index at `/gopro/media/list`. This client is read-only by design —
/// it lists, downloads (streamed to `StagingLocation`), deletes, and fetches
/// thumbnails. Camera models HERO9 and newer are the supported baseline.
public actor GoProAPIClient {
    private let baseURL: URL
    private let session: URLSession

    /// Short-lived cache of the last media-list fetch so that browsing into a
    /// directory right after listing the root doesn't re-hit the camera. The
    /// list reflects the whole card, so one fetch answers every path.
    private var cachedList: GoProMediaList?
    private var cachedAt: Date?
    private let cacheTTL: TimeInterval = 5

    public init(ipAddress: String, port: Int = 8080) {
        self.baseURL = URL(string: "http://\(ipAddress):\(port)")!
        let config = URLSessionConfiguration.default
        // The camera AP/USB link is local; fail fast rather than hang the UI
        // when it has gone to sleep or been unplugged.
        config.timeoutIntervalForRequest = 15
        config.waitsForConnectivity = false
        self.session = URLSession(configuration: config)
    }

    // MARK: - Reachability / lifecycle

    /// Cheap liveness probe used at connect time and before lazy reconnects.
    @discardableResult
    public func ping() async -> Bool {
        do {
            _ = try await get("/gopro/camera/state")
            return true
        } catch {
            return false
        }
    }

    /// Enables wired (USB) control so the HTTP API responds over the
    /// USB-Ethernet link. Harmless over Wi-Fi. Best-effort.
    public func enableWiredControl() async {
        _ = try? await get("/gopro/camera/control/wired_usb?p=1")
    }

    /// Keeps the camera's Wi-Fi/connection awake during idle browsing.
    /// Best-effort; failures are ignored (the next real request will surface
    /// a disconnect).
    public func keepAlive() async {
        _ = try? await get("/gopro/camera/keep_alive")
    }

    // MARK: - Listing

    public func listDirectory(at path: String) async throws -> [CloudFileItem] {
        let list = try await mediaList()
        return list.items(at: path)
    }

    public func getFileInfo(at path: String) async throws -> CloudFileItem {
        let list = try await mediaList()
        guard let item = list.file(at: path) else {
            throw CloudProviderError.notFound(path)
        }
        return item
    }

    public func folderSize(at path: String) async throws -> Int64 {
        let items = try await listDirectory(at: path)
        return items.reduce(Int64(0)) { $0 + $1.size }
    }

    private func mediaList(forceRefresh: Bool = false) async throws -> GoProMediaList {
        if !forceRefresh, let cachedList, let cachedAt,
           Date().timeIntervalSince(cachedAt) < cacheTTL {
            return cachedList
        }
        let data = try await get("/gopro/media/list")
        let list = try JSONDecoder().decode(GoProMediaList.self, from: data)
        cachedList = list
        cachedAt = Date()
        return list
    }

    // MARK: - Download

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let rel = remotePath.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        guard rel.count == 2 else { throw CloudProviderError.notFound(remotePath) }
        // Media is served from /videos/DCIM/<dir>/<file> (both photos & videos).
        guard let url = endpoint("/videos/DCIM/\(rel[0])/\(rel[1])") else {
            throw CloudProviderError.invalidResponse
        }

        var request = URLRequest(url: url)
        request.httpMethod = "GET"

        let (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        guard let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode) else {
            try? FileManager.default.removeItem(at: tempURL)
            throw Self.mapHTTPError((response as? HTTPURLResponse)?.statusCode ?? 0)
        }
        try? FileManager.default.removeItem(at: localURL)
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    // MARK: - Delete

    public func deleteItem(at path: String) async throws {
        // GoPro deletes a single file by its on-card "<dir>/<file>" path.
        let rel = path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard let encoded = rel.addingPercentEncoding(withAllowedCharacters: .urlQueryValueAllowed) else {
            throw CloudProviderError.notFound(path)
        }
        _ = try await get("/gopro/media/delete?path=\(encoded)")
        invalidateCache()
    }

    // MARK: - Thumbnail

    /// Raw JPEG bytes for a media item's thumbnail, or nil if unavailable.
    public func thumbnail(at path: String) async -> Data? {
        let rel = path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard let encoded = rel.addingPercentEncoding(withAllowedCharacters: .urlQueryValueAllowed) else {
            return nil
        }
        return try? await get("/gopro/media/thumbnail?path=\(encoded)")
    }

    // MARK: - Private HTTP

    private func invalidateCache() {
        cachedList = nil
        cachedAt = nil
    }

    private func endpoint(_ path: String) -> URL? {
        URL(string: path, relativeTo: baseURL)
    }

    @discardableResult
    private func get(_ path: String) async throws -> Data {
        guard let url = endpoint(path) else { throw CloudProviderError.invalidResponse }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                throw CloudProviderError.invalidResponse
            }
            guard (200...299).contains(http.statusCode) else {
                throw Self.mapHTTPError(http.statusCode)
            }
            return data
        } catch let error as CloudProviderError {
            throw error
        } catch {
            goProLog.error("[GoPro] GET \(path) failed: \(error.localizedDescription)")
            throw CloudProviderError.networkError(error)
        }
    }

    private static func mapHTTPError(_ status: Int) -> CloudProviderError {
        switch status {
        case 401, 403: return .unauthorized
        case 404: return .notFound("")
        case 429: return .rateLimited
        case 500...599: return .serverError(status)
        default: return .serverError(status)
        }
    }
}

private extension CharacterSet {
    /// Query-value-safe set: like `.urlQueryAllowed` but also escapes the
    /// sub-delimiters that would otherwise be read as query structure.
    static let urlQueryValueAllowed: CharacterSet = {
        var set = CharacterSet.urlQueryAllowed
        set.remove(charactersIn: "&=?+/")
        return set
    }()
}
