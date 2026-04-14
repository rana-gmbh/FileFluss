import Foundation

/// Delegate for URLSession tasks that reports byte-level transfer progress.
///
/// Pass to `session.download(for:delegate:)` for downloads (receives `didWriteData`)
/// or `session.upload(for:from:delegate:)` for uploads (receives `didSendBodyData`).
/// The `onBytes` closure is called with the byte *delta* since the last report,
/// so callers can simply accumulate.
final class ByteProgressDelegate: NSObject, URLSessionTaskDelegate, URLSessionDownloadDelegate, @unchecked Sendable {
    private let onBytes: @Sendable (Int64) -> Void
    private var lastSent: Int64 = 0
    private var lastWritten: Int64 = 0

    init(onBytes: @escaping @Sendable (Int64) -> Void) {
        self.onBytes = onBytes
    }

    // Upload progress
    func urlSession(_ session: URLSession,
                    task: URLSessionTask,
                    didSendBodyData bytesSent: Int64,
                    totalBytesSent: Int64,
                    totalBytesExpectedToSend: Int64) {
        let delta = totalBytesSent - lastSent
        if delta > 0 {
            lastSent = totalBytesSent
            onBytes(delta)
        }
    }

    // Download progress
    func urlSession(_ session: URLSession,
                    downloadTask: URLSessionDownloadTask,
                    didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64,
                    totalBytesExpectedToWrite: Int64) {
        let delta = totalBytesWritten - lastWritten
        if delta > 0 {
            lastWritten = totalBytesWritten
            onBytes(delta)
        }
    }

    // Required by URLSessionDownloadDelegate — no-op; the async API returns the file URL directly.
    func urlSession(_ session: URLSession,
                    downloadTask: URLSessionDownloadTask,
                    didFinishDownloadingTo location: URL) {}
}

/// Closure passed to provider downloadFile/uploadFile to report byte-level deltas.
/// `@Sendable` because URLSession delegates are invoked off the main actor.
typealias ByteProgressHandler = @Sendable (Int64) -> Void

/// URLSessionDownloadDelegate that handles a single download, reporting bytes and
/// resuming a continuation on completion. Owns the temp file so URLSession's own
/// temp doesn't get deleted before the caller reads it.
private final class DownloadProgressHandler: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    private let onBytes: @Sendable (Int64) -> Void
    private var completion: ((Result<(URL, URLResponse), Error>) -> Void)?
    private var lastWritten: Int64 = 0
    private var savedTempURL: URL?
    var ownedSession: URLSession?

    init(onBytes: @escaping @Sendable (Int64) -> Void,
         completion: @escaping (Result<(URL, URLResponse), Error>) -> Void) {
        self.onBytes = onBytes
        self.completion = completion
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask,
                    didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64,
                    totalBytesExpectedToWrite: Int64) {
        let delta = totalBytesWritten - lastWritten
        if delta > 0 {
            lastWritten = totalBytesWritten
            onBytes(delta)
        }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask,
                    didFinishDownloadingTo location: URL) {
        // `location` is deleted after this callback returns — move to a file we own.
        let tempURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("filefluss-dl-\(UUID().uuidString)")
        do {
            try FileManager.default.moveItem(at: location, to: tempURL)
            savedTempURL = tempURL
        } catch {
            // didCompleteWithError will surface a failure; nothing to do here.
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let completion else { return }
        self.completion = nil
        defer { ownedSession?.finishTasksAndInvalidate() }

        if let error {
            if let savedTempURL { try? FileManager.default.removeItem(at: savedTempURL) }
            completion(.failure(error))
            return
        }
        guard let tempURL = savedTempURL, let response = task.response else {
            completion(.failure(URLError(.cannotLoadFromNetwork)))
            return
        }
        completion(.success((tempURL, response)))
    }
}

extension URLSession {
    /// Download the request to a temp URL; reports byte progress if `onBytes` is non-nil.
    /// Returns (tempFileURL, URLResponse) like `session.download(for:)`.
    func downloadReportingProgress(for request: URLRequest, onBytes: ByteProgressHandler?) async throws -> (URL, URLResponse) {
        guard let onBytes else {
            return try await download(for: request)
        }

        return try await withCheckedThrowingContinuation { continuation in
            let handler = DownloadProgressHandler(onBytes: onBytes) { result in
                switch result {
                case .success(let value): continuation.resume(returning: value)
                case .failure(let error): continuation.resume(throwing: error)
                }
            }
            // Dedicated session so the delegate is strongly retained and receives
            // `didWriteData` reliably (per-task delegate on async `download(for:delegate:)`
            // doesn't always deliver URLSessionDownloadDelegate callbacks).
            let session = URLSession(configuration: configuration, delegate: handler, delegateQueue: nil)
            handler.ownedSession = session
            let task = session.downloadTask(with: request)
            task.resume()
        }
    }

    /// Upload `body` and return (responseData, URLResponse). Reports byte progress if `onBytes` is non-nil.
    func uploadReportingProgress(for request: URLRequest, body: Data, onBytes: ByteProgressHandler?) async throws -> (Data, URLResponse) {
        if let onBytes {
            let delegate = ByteProgressDelegate(onBytes: onBytes)
            return try await upload(for: request, from: body, delegate: delegate)
        }
        var req = request
        req.httpBody = body
        return try await data(for: req)
    }
}
