import Foundation

/// MainActor-bound state machine for the *manual* update check started
/// from the About window or Help menu. Background checks use
/// `UpdateNotifier` instead.
@MainActor
final class UpdateChecker: ObservableObject {

    enum State: Equatable {
        case idle
        case checking
        case upToDate
        case available(AvailableUpdate)
        case failed(String)
    }

    @Published var state: State = .idle

    let currentVersion: String

    init(currentVersion: String = UpdateLookup.currentVersion()) {
        self.currentVersion = currentVersion
    }

    func check() {
        Task { await performCheck() }
    }

    func performCheck() async {
        state = .checking
        do {
            if let update = try await UpdateLookup.fetchLatestUpdate(currentVersion: currentVersion) {
                state = .available(update)
            } else {
                state = .upToDate
            }
        } catch {
            state = .failed(error.localizedDescription)
        }
    }
}
