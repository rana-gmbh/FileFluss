import Foundation

/// The delay-then-open behaviour of a spring-loaded folder: hold a drag over
/// a folder for a moment and it opens so you can carry on into it (issue #47,
/// matching Finder).
///
/// One of these per drop target. The target tells it which row the cursor is
/// over on every `draggingUpdated`; the timer only restarts when that answer
/// changes, so the stream of cursor events doesn't keep pushing the opening
/// back. Once a target has fired it stays armed to *it*, which is what stops
/// a motionless cursor from diving through one folder after another: opening
/// replaces the listing, and without this the same row index would spring
/// again immediately.
@MainActor
final class SpringLoadTimer {
    /// Close to Finder's own delay. Long enough that dragging across a
    /// folder on the way somewhere else doesn't open it, short enough that
    /// deliberately pausing feels answered.
    static let delay: Duration = .milliseconds(650)

    private var armedTarget: AnyHashable?
    private var generation = 0

    /// Arms for `target`, cancelling anything armed for a different one.
    /// Passing the target that is already armed is deliberately a no-op.
    func arm(_ target: AnyHashable?, action: @escaping @MainActor () -> Void) {
        guard target != armedTarget else { return }
        armedTarget = target
        // Invalidates any pending fire: the sleeping task checks this back.
        generation += 1
        guard target != nil else { return }
        let scheduled = generation
        Task { [weak self] in
            try? await Task.sleep(for: Self.delay)
            guard let self, self.generation == scheduled else { return }
            action()
        }
    }

    /// Cancels a pending open — the drag left, or was dropped.
    func disarm() {
        arm(nil, action: {})
    }
}
