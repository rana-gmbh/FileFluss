import Foundation

/// Always-on, in-memory ring buffer of file/cloud operation events. Cheap
/// to call from any thread; the macOS app's SupportLogService snapshots
/// entries within the recording window when the user saves a support log.
///
/// Originally lived alongside SupportLogService in the macOS app. Split
/// out in Phase 0 step 7 because FileSystemService (which moved into the
/// package) logs every operation through it. The save-to-disk side of
/// support-log capture (the SupportLogService class, NSSavePanel, etc.)
/// stays in the macOS app.
public final class SupportLogger: @unchecked Sendable {
    public static let shared = SupportLogger()

    public struct Entry: Sendable {
        public let date: Date
        public let level: Level
        public let category: String
        public let message: String
    }

    public enum Level: String, Sendable {
        case info, notice, error
    }

    private let lock = NSLock()
    private var entries: [Entry] = []
    private let maxEntries = 5000

    public func log(_ message: String, category: String = "general", level: Level = .info) {
        let entry = Entry(date: Date(), level: level, category: category, message: message)
        lock.lock()
        entries.append(entry)
        if entries.count > maxEntries {
            entries.removeFirst(entries.count - maxEntries)
        }
        lock.unlock()
    }

    public func snapshot(since date: Date) -> [Entry] {
        lock.lock()
        defer { lock.unlock() }
        return entries.filter { $0.date >= date }
    }
}
