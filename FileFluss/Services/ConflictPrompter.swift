import SwiftUI

/// Asks the user what to do about a name clash, one question at a time.
///
/// Lives at app level because the question has to reach the window whatever
/// the panels are showing. Each file list used to host its own sheet, so a
/// conflict on a view model with no list on screen, such as a cloud file
/// pasted into a local folder, asked nobody and the transfer waited forever.
/// Two transfers on one view model also shared a single slot for the answer,
/// which left the first waiting forever too.
@Observable @MainActor
final class ConflictPrompter {
    static let shared = ConflictPrompter()

    struct Question: Identifiable {
        let id = UUID()
        let conflict: PendingConflict
        fileprivate let answer: CheckedContinuation<ConflictResolution, Never>
    }

    /// The question on screen.
    private(set) var current: Question?
    private var waiting: [Question] = []

    /// Waits for the user's answer. A cancelled transfer gets `.stop`, so
    /// Cancel works even while the question is up.
    func ask(_ conflict: PendingConflict) async -> ConflictResolution {
        let id = UUID()
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                guard !Task.isCancelled else {
                    continuation.resume(returning: Self.stop)
                    return
                }
                enqueue(Question(conflict: conflict, answer: continuation), id: id)
            }
        } onCancel: {
            Task { @MainActor in self.withdraw(id) }
        }
    }

    func resolve(_ resolution: ConflictResolution) {
        current?.answer.resume(returning: resolution)
        current = nil
        advance()
    }

    private static let stop = ConflictResolution(choice: .stop, applyToAll: false)

    /// By the id `ask` chose, since the question's own id doesn't exist yet
    /// when the cancellation handler is set up.
    private var questionIDs: [UUID: UUID] = [:]

    private func enqueue(_ question: Question, id: UUID) {
        questionIDs[id] = question.id
        waiting.append(question)
        if current == nil { advance() }
    }

    private func advance() {
        guard current == nil, !waiting.isEmpty else { return }
        current = waiting.removeFirst()
    }

    private func withdraw(_ id: UUID) {
        guard let questionID = questionIDs.removeValue(forKey: id) else { return }
        if current?.id == questionID {
            resolve(Self.stop)
        } else if let index = waiting.firstIndex(where: { $0.id == questionID }) {
            waiting.remove(at: index).answer.resume(returning: Self.stop)
        }
    }
}
