import AppKit
import Foundation

/// Periodic background poll of the GitHub Releases API. When a newer
/// version is published the notifier opens the update notification
/// window once per version. Toggleable via the
/// `automaticUpdateChecksEnabled` UserDefaults key.
actor UpdateNotifier {
    private enum DefaultsKey {
        static let lastCheckDate = "backgroundUpdateLastCheckDate"
        static let lastNotifiedVersion = "backgroundUpdateLastNotifiedVersion"
        static let automaticChecksEnabled = "automaticUpdateChecksEnabled"
    }

    private let defaults: UserDefaults
    private let currentVersion: String
    private let checkInterval: TimeInterval
    private var schedulerTask: Task<Void, Never>?

    init(
        defaults: UserDefaults = .standard,
        currentVersion: String = UpdateLookup.currentVersion(),
        checkInterval: TimeInterval = 24 * 60 * 60
    ) {
        self.defaults = defaults
        self.currentVersion = currentVersion
        self.checkInterval = checkInterval
    }

    deinit {
        schedulerTask?.cancel()
    }

    func start() {
        setAutomaticChecksEnabled(defaults.bool(forKey: DefaultsKey.automaticChecksEnabled))
    }

    func stop() {
        schedulerTask?.cancel()
        schedulerTask = nil
    }

    func setAutomaticChecksEnabled(_ enabled: Bool) {
        if enabled {
            guard schedulerTask == nil else { return }
            schedulerTask = Task { [weak self] in
                await self?.runScheduler()
            }
        } else {
            stop()
        }
    }

    private func runScheduler() async {
        while !Task.isCancelled {
            let delay = nextCheckDelay()
            if delay > 0 {
                let nanoseconds = UInt64(delay * 1_000_000_000)
                try? await Task.sleep(nanoseconds: nanoseconds)
            }
            guard !Task.isCancelled else { return }
            await performScheduledCheck()
        }
    }

    private func nextCheckDelay(now: Date = Date()) -> TimeInterval {
        guard let lastCheckDate = defaults.object(forKey: DefaultsKey.lastCheckDate) as? Date else {
            return 0
        }
        let nextCheckDate = lastCheckDate.addingTimeInterval(checkInterval)
        return max(0, nextCheckDate.timeIntervalSince(now))
    }

    private func performScheduledCheck(now: Date = Date()) async {
        defaults.set(now, forKey: DefaultsKey.lastCheckDate)

        do {
            guard let update = try await UpdateLookup.fetchLatestUpdate(currentVersion: currentVersion) else { return }
            guard shouldNotify(for: update) else { return }

            await MainActor.run {
                UpdateNotificationWindowController.shared.show(update: update)
            }
            defaults.set(update.version, forKey: DefaultsKey.lastNotifiedVersion)
        } catch {
            // Background failures stay silent — manual check surfaces them.
        }
    }

    private func shouldNotify(for update: AvailableUpdate) -> Bool {
        defaults.string(forKey: DefaultsKey.lastNotifiedVersion) != update.version
    }
}
