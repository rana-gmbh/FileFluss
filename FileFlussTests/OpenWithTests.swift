import Testing
import AppKit
import UniformTypeIdentifiers
@testable import FileFluss

/// The rules behind right-click → Open With: which apps are offered, in
/// what order, and that the real LaunchServices lookup answers for a type
/// every Mac can open.
@Suite("Open With")
struct OpenWithTests {

    private let preview = URL(fileURLWithPath: "/System/Applications/Preview.app")
    private let textEdit = URL(fileURLWithPath: "/System/Applications/TextEdit.app")
    private let safari = URL(fileURLWithPath: "/Applications/Safari.app")
    private let oldSafari = URL(fileURLWithPath: "/Volumes/Backup/Applications/Safari.app")

    private func name(_ url: URL) -> String { OpenWithCandidates.displayName(of: url) }

    @Test("The default comes first and the rest follow by name")
    func defaultLeads() {
        let apps = OpenWithCandidates.rank(
            perItem: [[textEdit, safari, preview]], defaultApp: safari, name: name
        )
        #expect(apps.map(\.name) == ["Safari", "Preview", "TextEdit"])
        #expect(apps.map(\.isDefault) == [true, false, false])
    }

    @Test("A multi-selection offers only the apps that open every item")
    func intersectsSelection() {
        let apps = OpenWithCandidates.rank(
            perItem: [[preview, safari, textEdit], [safari, preview]], defaultApp: textEdit, name: name
        )
        // TextEdit is the first file's default but can't open the second,
        // so it isn't offered at all — and nothing is marked default.
        #expect(apps.map(\.name) == ["Preview", "Safari"])
        #expect(!apps.contains { $0.isDefault })
    }

    @Test("A second copy of the same app is listed once")
    func dedupesByName() {
        let apps = OpenWithCandidates.rank(
            perItem: [[oldSafari, safari, preview]], defaultApp: safari, name: name
        )
        #expect(apps.map(\.name) == ["Safari", "Preview"])
        #expect(apps.first?.url == safari.standardizedFileURL)
    }

    @Test("Nothing to offer gives an empty list rather than a crash")
    func emptySelection() {
        #expect(OpenWithCandidates.rank(perItem: [], defaultApp: nil, name: name).isEmpty)
        #expect(OpenWithCandidates.apps(for: .files([])).isEmpty)
    }

    @Test("A plain-text file has apps to open it, with a default first")
    func realLookup() {
        // Every Mac ships TextEdit, so a text type always has a handler —
        // which proves the lookup is wired up, without depending on what
        // else this machine has installed.
        let apps = OpenWithCandidates.apps(for: .types([.plainText]))
        #expect(!apps.isEmpty)
        #expect(apps.first?.isDefault == true)
        #expect(apps.filter(\.isDefault).count == 1)
    }
}
