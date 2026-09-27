import AppKit
import Combine
import Sparkle

/// In-app updates via Sparkle 2, the same setup NetFluss uses.
///
/// The feed (`SUFeedURL`) is the `appcast.xml` attached to the latest GitHub
/// release. Every update must carry an EdDSA signature matching
/// `SUPublicEDKey` *and* the same Developer ID signature as the running app,
/// so a tampered download is refused before anything is installed. Sparkle
/// shows the release notes, lets the user pick Install / Skip / Remind Me
/// Later, replaces the app and relaunches it.
@MainActor
final class AppUpdater: NSObject, ObservableObject {
    static let shared = AppUpdater()

    @Published private(set) var canCheckForUpdates = false
    /// False for unbundled dev runs and for builds without the public EdDSA
    /// key — Sparkle would reject every update in that state, so it isn't
    /// started at all and "Check for Updates" falls back to the releases page.
    @Published private(set) var isAvailable = false

    private lazy var controller = SPUStandardUpdaterController(
        startingUpdater: false,
        updaterDelegate: self,
        userDriverDelegate: self
    )

    private static let releasesURL = URL(string: "https://github.com/rana-gmbh/filefluss/releases/latest")!

    var lastUpdateCheckDate: Date? {
        isAvailable ? controller.updater.lastUpdateCheckDate : nil
    }

    func start() {
        guard !isAvailable else { return }
        let bundle = Bundle.main
        let publicKey = bundle.object(forInfoDictionaryKey: "SUPublicEDKey") as? String ?? ""
        // A build without the key can never verify an update. Starting the
        // updater anyway would mean checking, downloading and then refusing
        // every single time.
        guard bundle.bundleURL.pathExtension == "app", !publicKey.isEmpty else { return }

        let updater = controller.updater
        // Our own preference drives Sparkle's schedule. Setting it explicitly
        // also stops Sparkle from asking for permission on the second launch.
        updater.automaticallyChecksForUpdates = Self.automaticChecksPreference
        do {
            try updater.start()
        } catch {
            NSLog("FileFluss: Sparkle updater failed to start: \(error.localizedDescription)")
            return
        }
        isAvailable = true
        updater.publisher(for: \.canCheckForUpdates)
            .receive(on: DispatchQueue.main)
            .assign(to: &$canCheckForUpdates)
    }

    /// Mirrors Settings → "Check for updates automatically".
    func applyAutomaticChecksPreference() {
        guard isAvailable else { return }
        let enabled = Self.automaticChecksPreference
        // Guarded: Sparkle persists this in UserDefaults, and every defaults
        // write comes back here via UserDefaults.didChangeNotification.
        if controller.updater.automaticallyChecksForUpdates != enabled {
            controller.updater.automaticallyChecksForUpdates = enabled
        }
    }

    @objc func checkForUpdates(_ sender: Any?) {
        guard isAvailable else {
            NSWorkspace.shared.open(Self.releasesURL)
            return
        }
        NSApp.activate(ignoringOtherApps: true)
        controller.checkForUpdates(sender)
    }

    private static var automaticChecksPreference: Bool {
        UserDefaults.standard.bool(forKey: "automaticUpdateChecksEnabled")
    }
}

extension AppUpdater: NSMenuItemValidation {
    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        guard menuItem.action == #selector(checkForUpdates(_:)) else { return true }
        return !isAvailable || canCheckForUpdates
    }
}

extension AppUpdater: SPUUpdaterDelegate {}

// Sparkle calls its user-driver delegate on the main thread.
extension AppUpdater: @preconcurrency SPUStandardUserDriverDelegate {
    var supportsGentleScheduledUpdateReminders: Bool { true }

    func standardUserDriverShouldHandleShowingScheduledUpdate(
        _ update: SUAppcastItem,
        andInImmediateFocus immediateFocus: Bool
    ) -> Bool {
        true
    }

    func standardUserDriverWillHandleShowingUpdate(
        _ handleShowingUpdate: Bool,
        forUpdate update: SUAppcastItem,
        state: SPUUserUpdateState
    ) {
        // A scheduled check can fire while FileFluss is in the background;
        // bring it forward so Sparkle's window isn't buried.
        if handleShowingUpdate, !state.userInitiated {
            NSApp.activate(ignoringOtherApps: true)
        }
    }
}
