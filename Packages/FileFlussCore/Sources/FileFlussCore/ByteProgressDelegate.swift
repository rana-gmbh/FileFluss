import Foundation

/// Delegate for URLSession tasks that reports byte-level transfer progress.
///
/// Pass to `session.download(for:delegate:)` for downloads (receives `didWriteData`)
/// or `session.upload(for:from:delegate:)` for uploads (receives `didSendBodyData`).
/// The `onBytes` closure is called with the byte *delta* since the last report,
/// so callers can simply accumulate.
public final class ByteProgressDelegate: NSObject, URLSessionTaskDelegate, URLSessionDownloadDelegate, @unchecked Sendable {
    private let onBytes: @Sendable (Int64) -> Void
    private var lastSent: Int64 = 0
    private var lastWritten: Int64 = 0

    public init(onBytes: @escaping @Sendable (Int64) -> Void) {
        self.onBytes = onBytes
    }

    // Upload progress
    public func urlSession(_ session: URLSession,
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
    public func urlSession(_ session: URLSession,
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
    public func urlSession(_ session: URLSession,
                           downloadTask: URLSessionDownloadTask,
                           didFinishDownloadingTo location: URL) {}
}

/// Closure passed to provider downloadFile/uploadFile to report byte-level deltas.
/// `@Sendable` because URLSession delegates are invoked off the main actor.
public typealias ByteProgressHandler = @Sendable (Int64) -> Void

/// URLSessionDataDelegate that streams a download straight to a staging file in
/// `StagingLocation`, reporting bytes and resuming a continuation on completion.
///
/// A data task (rather than a download task) is used deliberately: a download
/// task always buffers the whole response in URLSession's own temp on the
/// internal disk before handing it over, which defeats a user-chosen cache
/// folder on an external drive. Writing each chunk to our own file as it
/// arrives keeps memory flat and puts the bytes exactly where the user asked.
private final class DownloadProgressHandler: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let onBytes: (@Sendable (Int64) -> Void)?
    private var completion: ((Result<(URL, URLResponse), Error>) -> Void)?
    private let destURL: URL
    private var handle: FileHandle?
    private var response: URLResponse?
    private var failure: Error?
    var ownedSession: URLSession?

    init(onBytes: (@Sendable (Int64) -> Void)?,
         completion: @escaping (Result<(URL, URLResponse), Error>) -> Void) {
        self.onBytes = onBytes
        self.completion = completion
        self.destURL = StagingLocation.downloadStagingFile()
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                    didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        self.response = response
        FileManager.default.createFile(atPath: destURL.path, contents: nil)
        handle = try? FileHandle(forWritingTo: destURL)
        if handle == nil {
            failure = CocoaError(.fileWriteUnknown)
            completionHandler(.cancel)
            return
        }
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard failure == nil, let handle else { return }
        do {
            try handle.write(contentsOf: data)
            onBytes?(Int64(data.count))
        } catch {
            failure = error
            dataTask.cancel()
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let completion else { return }
        self.completion = nil
        try? handle?.close()
        handle = nil
        defer { ownedSession?.finishTasksAndInvalidate() }

        if let err = error ?? failure {
            try? FileManager.default.removeItem(at: destURL)
            completion(.failure(err))
            return
        }
        guard let response else {
            try? FileManager.default.removeItem(at: destURL)
            completion(.failure(URLError(.cannotLoadFromNetwork)))
            return
        }
        completion(.success((destURL, response)))
    }
}

extension URLSession {
    /// Download the request to a temp URL; reports byte progress if `onBytes` is non-nil.
    /// Returns (tempFileURL, URLResponse) like `session.download(for:)`.
    public func downloadReportingProgress(for request: URLRequest, onBytes: ByteProgressHandler?) async throws -> (URL, URLResponse) {
        return try await withCheckedThrowingContinuation { continuation in
            let handler = DownloadProgressHandler(onBytes: onBytes) { result in
                switch result {
                case .success(let value): continuation.resume(returning: value)
                case .failure(let error): continuation.resume(throwing: error)
                }
            }
            // Dedicated session so the delegate is strongly retained and reliably
            // receives the streaming `didReceive` callbacks (a per-task delegate
            // on the async `data(for:delegate:)` doesn't always deliver them).
            let session = URLSession(configuration: configuration, delegate: handler, delegateQueue: nil)
            handler.ownedSession = session
            let task = session.dataTask(with: request)
            task.resume()
        }
    }

    /// Upload `body` and return (responseData, URLResponse). Reports byte progress if `onBytes` is non-nil.
    public func uploadReportingProgress(for request: URLRequest, body: Data, onBytes: ByteProgressHandler?) async throws -> (Data, URLResponse) {
        if let onBytes {
            let delegate = ByteProgressDelegate(onBytes: onBytes)
            return try await upload(for: request, from: body, delegate: delegate)
        }
        var req = request
        req.httpBody = body
        return try await data(for: req)
    }
}
