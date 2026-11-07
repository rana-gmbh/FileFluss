#if DEBUG
import AppKit
import FileFlussCore

/// Dev-only: saves the connected test accounts, with their credentials, to
/// one file and brings them back from it. Adding every cloud account again
/// before each version test took a long time, and the list had a way of
/// losing accounts.
///
/// The file sits next to the debug credential folder in Application
/// Support, readable by the owner only, never in the repository: it holds
/// passwords and tokens in the clear, just like that folder does.
@MainActor
enum TestAccountSnapshot {
    struct Snapshot: Codable {
        var version = 1
        var savedAt: Date
        var accounts: [CloudAccount]
        /// Debug credential store entries, by key ("<provider>.<account id>").
        var credentials: [String: Data]
    }

    static var fileURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("com.rana-gmbh.FileFluss/test-accounts.json")
    }

    // MARK: - Menu actions

    static func saveFromMenu(appState: AppState) {
        let accounts = appState.syncManager.accounts
        guard !accounts.isEmpty else {
            alert("Nothing to save", "There are no cloud accounts in this build.")
            return
        }
        if let existing = load(), existing.accounts.count > accounts.count {
            let missing = Set(existing.accounts.map(\.id)).subtracting(accounts.map(\.id)).count
            guard confirm("Replace the saved test accounts?",
                          "The saved set has \(existing.accounts.count) accounts, this build has \(accounts.count). The \(missing) not here any more would be dropped from it.",
                          button: "Replace") else { return }
        }
        do {
            let snapshot = try write(accounts: accounts, merging: nil)
            alert("Test accounts saved",
                  "\(snapshot.accounts.count) accounts and \(snapshot.credentials.count) credentials saved to \(fileURL.path).")
        } catch {
            alert("Couldn't save the test accounts", error.localizedDescription)
        }
    }

    static func restoreFromMenu(appState: AppState) {
        guard let snapshot = load() else {
            alert("No saved test accounts", "Save them first with File → Support Log → Save Test Accounts. Looked in \(fileURL.path).")
            return
        }
        let present = Set(appState.syncManager.accounts.map(\.id))
        let missing = snapshot.accounts.filter { !present.contains($0.id) }
        guard !missing.isEmpty else {
            alert("Nothing to restore", "All \(snapshot.accounts.count) saved accounts are already here.")
            return
        }
        let names = missing.map(\.displayName).sorted().joined(separator: "\n")
        guard confirm("Restore \(missing.count) test accounts?",
                      "Saved \(snapshot.savedAt.formatted(date: .abbreviated, time: .shortened)). Accounts already here stay as they are.\n\n\(names)",
                      button: "Restore") else { return }

        let missingIDs = missing.map(\.id.uuidString)
        var failed: [String] = []
        for (key, data) in snapshot.credentials where missingIDs.contains(where: key.contains) {
            do { try KeychainService.save(key: key, data: data) } catch { failed.append(key) }
        }
        Task {
            let added = await appState.syncManager.restoreTestAccounts(missing)
            SupportLogger.shared.log("Restored \(added) test account(s) from \(fileURL.path)", category: "version-test", level: .notice)
            if !failed.isEmpty {
                alert("Some credentials couldn't be written", failed.joined(separator: "\n"))
            }
        }
    }

    /// After a version test: tokens some services rotate on every refresh
    /// (Box, for one) are stale in the file by now. Updates the accounts
    /// that are here and keeps the ones that aren't, so an account removed
    /// by accident doesn't vanish from the saved set as well.
    static func refreshAfterVersionTest(appState: AppState) {
        guard let existing = load() else { return }
        do {
            _ = try write(accounts: appState.syncManager.accounts, merging: existing)
        } catch {
            SupportLogger.shared.log("Test account snapshot not refreshed: \(error.localizedDescription)", category: "version-test", level: .error)
        }
    }

    // MARK: - File

    static func load() -> Snapshot? {
        guard let data = try? Data(contentsOf: fileURL) else { return nil }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try? decoder.decode(Snapshot.self, from: data)
    }

    @discardableResult
    static func write(accounts: [CloudAccount], merging existing: Snapshot?) throws -> Snapshot {
        let snapshot = merged(current: accounts, credentials: currentCredentials(for: accounts),
                              into: existing, at: Date())
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(snapshot)

        let fm = FileManager.default
        try fm.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true,
                               attributes: [.posixPermissions: 0o700])
        // One generation back, in case a save went wrong.
        let previous = fileURL.deletingPathExtension().appendingPathExtension("previous.json")
        if fm.fileExists(atPath: fileURL.path) {
            try? fm.removeItem(at: previous)
            try? fm.copyItem(at: fileURL, to: previous)
        }
        try data.write(to: fileURL, options: .atomic)
        try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: fileURL.path)
        if fm.fileExists(atPath: previous.path) {
            try? fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: previous.path)
        }
        return snapshot
    }

    /// The current accounts and credentials laid over an earlier snapshot:
    /// what's here wins, what's only in the snapshot stays. Without an
    /// earlier snapshot, just what's here.
    nonisolated static func merged(current: [CloudAccount], credentials: [String: Data],
                                   into existing: Snapshot?, at date: Date) -> Snapshot {
        guard let existing else {
            return Snapshot(savedAt: date, accounts: current, credentials: credentials)
        }
        let currentIDs = Set(current.map(\.id))
        let kept = existing.accounts.filter { !currentIDs.contains($0.id) }
        let keptIDs = kept.map(\.id.uuidString)
        var mergedCredentials = existing.credentials.filter { key, _ in keptIDs.contains(where: key.contains) }
        mergedCredentials.merge(credentials) { _, new in new }
        return Snapshot(savedAt: date, accounts: current + kept, credentials: mergedCredentials)
    }

    private static func currentCredentials(for accounts: [CloudAccount]) -> [String: Data] {
        let ids = accounts.map(\.id.uuidString)
        var result: [String: Data] = [:]
        for key in KeychainService.devCredentialKeys() where ids.contains(where: key.contains) {
            if let data = KeychainService.load(key: key) { result[key] = data }
        }
        return result
    }

    // MARK: - Alerts

    private static func alert(_ title: String, _ message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.runModal()
    }

    private static func confirm(_ title: String, _ message: String, button: String) -> Bool {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.addButton(withTitle: button)
        alert.addButton(withTitle: "Cancel")
        return alert.runModal() == .alertFirstButtonReturn
    }
}
#endif
