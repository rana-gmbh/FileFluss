import Foundation
import SwiftUI
import AppKit

/// Always-on, in-memory ring buffer of file/cloud operation events. Cheap to
/// call from any thread; SupportLogService snapshots entries within the
/// recording window when the user saves a support log.
final class SupportLogger: @unchecked Sendable {
    static let shared = SupportLogger()

    struct Entry: Sendable {
        let date: Date
        let level: Level
        let category: String
        let message: String
    }

    enum Level: String, Sendable {
        case info, notice, error
    }

    private let lock = NSLock()
    private var entries: [Entry] = []
    private let maxEntries = 5000

    func log(_ message: String, category: String = "general", level: Level = .info) {
        let entry = Entry(date: Date(), level: level, category: category, message: message)
        lock.lock()
        entries.append(entry)
        if entries.count > maxEntries {
            entries.removeFirst(entries.count - maxEntries)
        }
        lock.unlock()
    }

    func snapshot(since date: Date) -> [Entry] {
        lock.lock()
        defer { lock.unlock() }
        return entries.filter { $0.date >= date }
    }
}

/// Records a 60-second window of support events, then prompts the user to
/// save the bundle for bug reports.
@Observable @MainActor
final class SupportLogService {
    static let shared = SupportLogService()

    private(set) var isRecording: Bool = false
    private(set) var secondsRemaining: Int = 0

    private static let recordingDuration: Int = 60

    private var startDate: Date?
    private var tickTimer: Timer?

    private init() {}

    func start() {
        guard !isRecording else { return }
        isRecording = true
        startDate = Date()
        secondsRemaining = Self.recordingDuration
        SupportLogger.shared.log("Support log recording started", category: "support", level: .notice)

        tickTimer?.invalidate()
        tickTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor in
                self?.tick()
            }
        }
    }

    private func tick() {
        secondsRemaining -= 1
        if secondsRemaining <= 0 {
            tickTimer?.invalidate()
            tickTimer = nil
            Task { await finish() }
        }
    }

    private func finish() async {
        guard let startDate else { reset(); return }
        SupportLogger.shared.log("Support log recording stopped", category: "support", level: .notice)
        let report = collectReport(since: startDate)
        reset()
        presentSavePanel(report: report)
    }

    private func reset() {
        tickTimer?.invalidate()
        tickTimer = nil
        startDate = nil
        secondsRemaining = 0
        isRecording = false
    }

    // MARK: - Report

    private func collectReport(since startDate: Date) -> String {
        var output = ""
        output += header()
        output += "\n\n--- Events captured ---\n"
        let entries = SupportLogger.shared.snapshot(since: startDate)
        if entries.isEmpty {
            output += "(no events captured during this window)\n"
        } else {
            let formatter = DateFormatter()
            formatter.dateFormat = "HH:mm:ss.SSS"
            for entry in entries {
                let ts = formatter.string(from: entry.date)
                output += "\(ts) [\(entry.level.rawValue.uppercased())] [\(entry.category)] \(entry.message)\n"
            }
        }
        return output
    }

    private func header() -> String {
        let info = Bundle.main.infoDictionary
        let appVersion = info?["CFBundleShortVersionString"] as? String ?? "?"
        let build = info?["CFBundleVersion"] as? String ?? "?"
        let os = ProcessInfo.processInfo.operatingSystemVersionString
        let locale = Locale.current.identifier
        let now = ISO8601DateFormatter().string(from: Date())

        return """
        FileFluss Support Log
        =====================
        Generated: \(now)
        App version: \(appVersion) (build \(build))
        macOS: \(os)
        Locale: \(locale)
        Hardware: \(hardwareModel())
        """
    }

    private func hardwareModel() -> String {
        var size: Int = 0
        sysctlbyname("hw.model", nil, &size, nil, 0)
        var bytes = [CChar](repeating: 0, count: size)
        sysctlbyname("hw.model", &bytes, &size, nil, 0)
        return String(cString: bytes)
    }

    // MARK: - Save panel

    private func presentSavePanel(report: String) {
        let panel = NSSavePanel()
        panel.title = "Save FileFluss Support Log"
        panel.allowedContentTypes = [.plainText, .log]
        panel.canCreateDirectories = true
        let stamp = Self.fileNameStamp.string(from: Date())
        panel.nameFieldStringValue = "FileFluss-support-\(stamp).log"

        let response = panel.runModal()
        guard response == .OK, let url = panel.url else { return }
        do {
            try report.write(to: url, atomically: true, encoding: .utf8)
        } catch {
            NSAlert(error: error).runModal()
        }
    }

    private static let fileNameStamp: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyyMMdd-HHmmss"
        return f
    }()
}
