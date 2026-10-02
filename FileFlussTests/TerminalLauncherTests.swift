import Testing
import Foundation
import FileFlussCore
@testable import FileFluss

/// "Open in Terminal" (issue #61): which folder it means, and which
/// terminal it hands that folder to.
@Suite("Open in Terminal")
@MainActor
struct TerminalLauncherTests {

    private let here = URL(fileURLWithPath: "/Users/someone/Projects", isDirectory: true)

    private func url(_ name: String) -> URL { here.appendingPathComponent(name) }

    /// Decides what counts as a folder without touching the disk, so the
    /// rule can be tested on paths that don't exist.
    private func isDirectory(_ folders: Set<String>) -> (URL) -> Bool {
        { folders.contains($0.lastPathComponent) }
    }

    // MARK: Which folder

    @Test("One selected folder is the folder")
    func singleFolderWins() {
        let folder = url("Sources")

        let target = TerminalLauncher.targetDirectory(
            selection: [folder], isDirectory: isDirectory(["Sources"]), currentDirectory: here
        )

        #expect(target == folder)
    }

    /// Finder's rule, and the one people expect: a selected *file* means
    /// the folder it is in, which is the folder on screen.
    @Test("A selected file means the folder you are looking at")
    func selectedFileUsesCurrentDirectory() {
        let target = TerminalLauncher.targetDirectory(
            selection: [url("README.md")], isDirectory: isDirectory([]), currentDirectory: here
        )

        #expect(target == here)
    }

    @Test("Nothing selected means the folder you are looking at")
    func emptySelectionUsesCurrentDirectory() {
        #expect(TerminalLauncher.targetDirectory(
            selection: [], isDirectory: isDirectory([]), currentDirectory: here
        ) == here)
    }

    /// Opening one terminal per selected folder is rarely what was meant,
    /// so several folders fall back to the one on screen.
    @Test("Several selected folders mean the folder you are looking at")
    func multiSelectionUsesCurrentDirectory() {
        let target = TerminalLauncher.targetDirectory(
            selection: [url("Sources"), url("Tests")],
            isDirectory: isDirectory(["Sources", "Tests"]),
            currentDirectory: here
        )

        #expect(target == here)
    }

    /// The convenience the local panel actually calls, against a real
    /// folder — `FileItem` reads `isDirectory` off the disk.
    @Test("The panel's own overload agrees, on a real folder")
    func fileItemOverloadMatches() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("terminal-test-\(UUID().uuidString)", isDirectory: true)
        let sub = root.appendingPathComponent("Sub", isDirectory: true)
        try FileManager.default.createDirectory(at: sub, withIntermediateDirectories: true)
        let file = root.appendingPathComponent("note.txt")
        try "x".write(to: file, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: root) }

        #expect(TerminalLauncher.targetDirectory(
            selection: [FileItem(url: sub)], currentDirectory: root
        ) == sub)
        #expect(TerminalLauncher.targetDirectory(
            selection: [FileItem(url: file)], currentDirectory: root
        ) == root)
    }

    @Test("The same rule applies to a mounted cloud panel's remote paths")
    func cloudPathsFollowTheSameRule() {
        let folder = (path: "/Backups/2026", isDirectory: true)
        let file = (path: "/Backups/notes.txt", isDirectory: false)

        #expect(TerminalLauncher.targetDirectory(selection: [folder], currentPath: "/Backups") == "/Backups/2026")
        #expect(TerminalLauncher.targetDirectory(selection: [file], currentPath: "/Backups") == "/Backups")
        #expect(TerminalLauncher.targetDirectory(selection: [], currentPath: "/Backups") == "/Backups")
    }

    // MARK: Which terminal

    @Test("The configured terminal is used when it is installed")
    func usesConfiguredTerminal() {
        let chosen = URL(fileURLWithPath: "/Applications/Ghostty.app")
        let resolved = TerminalLauncher.terminalURL(bundleID: "com.mitchellh.ghostty") { id in
            id == "com.mitchellh.ghostty" ? chosen : nil
        }

        #expect(resolved == chosen)
    }

    /// A terminal the user chose and later deleted must not leave the
    /// command dead — it falls back to the one every Mac has.
    @Test("A terminal that is gone falls back to Terminal.app")
    func fallsBackWhenChosenTerminalIsMissing() {
        let terminal = URL(fileURLWithPath: "/System/Applications/Utilities/Terminal.app")
        let resolved = TerminalLauncher.terminalURL(bundleID: "com.example.deleted") { id in
            id == TerminalLauncher.defaultBundleID ? terminal : nil
        }

        #expect(resolved == terminal)
    }

    @Test("No configured terminal means Terminal.app")
    func unsetPreferenceUsesDefault() {
        let terminal = URL(fileURLWithPath: "/System/Applications/Utilities/Terminal.app")

        #expect(TerminalLauncher.terminalURL(bundleID: nil) { _ in terminal } == terminal)
        #expect(TerminalLauncher.terminalURL(bundleID: "") { _ in terminal } == terminal)
    }

    @Test("Nothing installed is reported, not guessed at")
    func noTerminalAtAll() {
        #expect(TerminalLauncher.terminalURL(bundleID: "com.example.deleted") { _ in nil } == nil)
    }

    /// Terminal.app is the default and is always offered: it ships with
    /// macOS, so the list is never empty.
    @Test("Terminal.app is among the terminals offered by name")
    func terminalIsOffered() {
        #expect(TerminalLauncher.knownTerminals.contains { $0.bundleID == TerminalLauncher.defaultBundleID })
        #expect(TerminalLauncher.installedTerminals().contains { $0.bundleID == TerminalLauncher.defaultBundleID })
    }
}
