import Testing
import Foundation
@testable import FileFluss

@Suite("Spring-loaded folder timing")
@MainActor
struct SpringLoadTimerTests {

    /// A little past the spring delay, so a fire that is going to happen has
    /// happened by the time we look.
    private static let settle = Duration.milliseconds(900)

    @Test("Resting on a target opens it")
    func firesForHoveredTarget() async throws {
        let timer = SpringLoadTimer()
        var opened = 0
        timer.arm(1) { opened += 1 }
        try await Task.sleep(for: Self.settle)
        #expect(opened == 1)
    }

    @Test("Leaving before the delay opens nothing")
    func cancelledByLeaving() async throws {
        let timer = SpringLoadTimer()
        var opened = 0
        timer.arm(1) { opened += 1 }
        try await Task.sleep(for: .milliseconds(100))
        timer.disarm()
        try await Task.sleep(for: Self.settle)
        #expect(opened == 0)
    }

    @Test("Crossing rows only opens the one rested on")
    func onlyTheLastTargetFires() async throws {
        let timer = SpringLoadTimer()
        var opened: [Int] = []
        for row in 1...3 {
            timer.arm(row) { opened.append(row) }
            try await Task.sleep(for: .milliseconds(80))
        }
        try await Task.sleep(for: Self.settle)
        #expect(opened == [3])
    }

    /// The rule that keeps a motionless cursor from diving through one
    /// folder after another: opening replaces the listing, so the same row
    /// must not spring again until the cursor has been somewhere else.
    @Test("A target that fired doesn't fire again on its own")
    func doesNotRepeatForSameTarget() async throws {
        let timer = SpringLoadTimer()
        var opened = 0
        timer.arm(1) { opened += 1 }
        try await Task.sleep(for: Self.settle)
        timer.arm(1) { opened += 1 }
        try await Task.sleep(for: Self.settle)
        #expect(opened == 1)
    }

    @Test("Coming back to a row after leaving re-arms it")
    func reArmsAfterLeaving() async throws {
        let timer = SpringLoadTimer()
        var opened = 0
        timer.arm(1) { opened += 1 }
        try await Task.sleep(for: Self.settle)
        timer.arm(2) { opened += 1 }
        timer.arm(1) { opened += 1 }
        try await Task.sleep(for: Self.settle)
        #expect(opened == 2)
    }
}
