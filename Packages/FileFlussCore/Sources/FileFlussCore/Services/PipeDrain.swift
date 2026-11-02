import Foundation

/// Reads a `Pipe` while its process runs.
///
/// A pipe's buffer is small. A process that writes more than it holds blocks
/// until someone reads, so reading only after the process has exited means it
/// never exits: that is how a large SFTP or FTP listing hung for good.
///
/// `collected()` waits a moment rather than for end-of-file, because a child
/// that outlives the process can keep the write end open. For `sftp` that's
/// the `ControlPersist` master connection, which stays up for minutes.
public final class PipeDrain: @unchecked Sendable {
    private let lock = NSLock()
    private var data = Data()
    private let finished = DispatchSemaphore(value: 0)

    public init(_ pipe: Pipe) {
        let handle = pipe.fileHandleForReading
        DispatchQueue.global(qos: .utility).async { [self] in
            while true {
                let chunk = handle.availableData
                if chunk.isEmpty { break }
                lock.lock()
                data.append(chunk)
                lock.unlock()
            }
            finished.signal()
        }
    }

    /// Everything read so far. Call it once, after the process has exited.
    public func collected(graceSeconds: TimeInterval = 2) -> Data {
        _ = finished.wait(timeout: .now() + graceSeconds)
        lock.lock()
        defer { lock.unlock() }
        return data
    }
}

/// Stops a running process when the task waiting for it is cancelled.
/// Call `started` straight after `run()`: a cancel that came first then still
/// takes effect, and `terminate()` on a process not yet running would throw.
public final class ProcessCancellation: @unchecked Sendable {
    private let lock = NSLock()
    private var process: Process?
    private var cancelled = false

    public init() {}

    public func started(_ process: Process) {
        lock.lock()
        defer { lock.unlock() }
        self.process = process
        if cancelled, process.isRunning { process.terminate() }
    }

    public func cancel() {
        lock.lock()
        defer { lock.unlock() }
        cancelled = true
        if let process, process.isRunning { process.terminate() }
    }
}
