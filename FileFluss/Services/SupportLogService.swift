import Foundation
import FileFlussCore
import SwiftUI
import AppKit

/// Records support events until the user stops, then prompts them to save
/// the bundle for a bug report.
///
/// It used to stop by itself after sixty seconds. That is shorter than the
/// problems people need to record: someone trying to report a 1.5 GB upload
/// failing part way through couldn't capture it at all, because the
/// recording ended minutes before the failure did. Recording now runs until
/// it is stopped, with a long backstop so a forgotten recording can't run
/// for a day.
@Observable @MainActor
final class SupportLogService {
    static let shared = SupportLogService()

    private(set) var isRecording: Bool = false
    /// How long the current recording has been running.
    private(set) var secondsElapsed: Int = 0

    /// Stops a recording the user has forgotten about. Long enough for any
    /// transfer worth reporting; the log itself is a bounded ring buffer, so
    /// a long recording costs no memory.
    private static let maximumDuration: Int = 60 * 60

    private var startDate: Date?
    private var tickTimer: Timer?

    private init() {}

    func start() {
        guard !isRecording else { return }
        isRecording = true
        startDate = Date()
        secondsElapsed = 0
        SupportLogger.shared.log("Support log recording started", category: "support", level: .notice)

        tickTimer?.invalidate()
        tickTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { [weak self] _ in
            Task { @MainActor in
                self?.tick()
            }
        }
    }

    /// Ends the recording and offers the report for saving. Called from the
    /// menu item and the banner's Stop button.
    func stop() {
        guard isRecording else { return }
        tickTimer?.invalidate()
        tickTimer = nil
        Task { await finish() }
    }

    private func tick() {
        secondsElapsed += 1
        if secondsElapsed >= Self.maximumDuration {
            SupportLogger.shared.log(
                "Support log recording hit the one-hour limit and stopped itself",
                category: "support", level: .notice
            )
            stop()
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
        secondsElapsed = 0
        isRecording = false
    }

    /// `3:42`, for the banner.
    var elapsedText: String {
        String(format: "%d:%02d", secondsElapsed / 60, secondsElapsed % 60)
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
