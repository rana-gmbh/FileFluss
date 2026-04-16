import Foundation
import AppKit

/// Dev-only smoke test that exercises every connected cloud account end-to-end:
/// create folder → upload files → replace files → delete files → cleanup folder.
/// Writes a Markdown report into Testfiles/ and verifies each step against the
/// provider's listDirectory/getFileMetadata response.
@MainActor
enum VersionTestRunner {
    private static let testFolderName = "FileFluss version test"
    private static let logCategory = "version-test"
    // Providers whose storage model doesn't map to arbitrary folders (e.g.
    // WordPress Media Library uses auto-generated date folders). Running the
    // create/upload/delete flow against these accounts produces misleading
    // failures, so we skip them and note that in the report.
    private static let skippedProviders: Set<CloudProviderType> = [.wordpress]

    static func run(appState: AppState) async {
        guard let testfilesURL = locateTestfilesURL() else {
            presentSimpleAlert(title: "Version Test",
                               message: "Couldn't locate a Testfiles folder next to the project source.")
            return
        }
        let localFiles = enumerateLocalFiles(root: testfilesURL)
        guard !localFiles.isEmpty else {
            presentSimpleAlert(title: "Version Test",
                               message: "Testfiles folder is empty.")
            return
        }

        let accounts = appState.syncManager.accounts
        guard !accounts.isEmpty else {
            presentSimpleAlert(title: "Version Test",
                               message: "No cloud accounts connected.")
            return
        }

        SupportLogger.shared.log(
            "Version test starting — \(localFiles.count) file(s), \(accounts.count) account(s)",
            category: logCategory, level: .notice
        )
        let startDate = Date()

        var accountResults: [AccountResult] = []
        for account in accounts {
            if Self.skippedProviders.contains(account.providerType) {
                let note = StepResult(
                    label: "skipped (\(account.providerType.displayName) has no folder concept)",
                    ok: true, durationMs: 0, error: nil, diagnostics: nil
                )
                accountResults.append(AccountResult(
                    accountDisplayName: account.displayName,
                    provider: account.providerType.displayName,
                    createFolder: note, uploads: [], replaces: [], deletes: [],
                    cleanupFolder: note
                ))
                continue
            }
            let result = await runForAccount(
                account: account,
                localFiles: localFiles
            )
            accountResults.append(result)
        }

        let report = Report(
            startDate: startDate,
            endDate: Date(),
            testfilesURL: testfilesURL,
            files: localFiles,
            accounts: accountResults
        )
        if let reportURL = writeReport(report, to: testfilesURL) {
            SupportLogger.shared.log(
                "Version test complete — report at \(reportURL.path)",
                category: logCategory, level: .notice
            )
            showCompletionAlert(report: report, path: reportURL)
        } else {
            SupportLogger.shared.log(
                "Version test complete, but report write failed",
                category: logCategory, level: .error
            )
        }
    }

    // MARK: - Models

    private struct LocalFile {
        let relativePath: String   // e.g. "Documents/foo.pdf"
        let url: URL
        let size: Int64
    }

    private struct StepResult {
        let label: String
        let ok: Bool
        let durationMs: Int
        let error: String?
        /// Extra context captured at failure time (e.g. the parent directory
        /// listing when a delete's verification failed). Rendered under the
        /// table in the Markdown report so we can diagnose without re-running.
        let diagnostics: String?
    }

    private struct AccountResult {
        let accountDisplayName: String
        let provider: String
        let createFolder: StepResult
        let uploads: [StepResult]
        let replaces: [StepResult]
        let deletes: [StepResult]
        let cleanupFolder: StepResult

        var allSteps: [StepResult] {
            [createFolder] + uploads + replaces + deletes + [cleanupFolder]
        }
        var passCount: Int { allSteps.filter { $0.ok }.count }
        var totalCount: Int { allSteps.count }
    }

    private struct Report {
        let startDate: Date
        let endDate: Date
        let testfilesURL: URL
        let files: [LocalFile]
        let accounts: [AccountResult]
    }

    private enum VersionTestError: Error, LocalizedError {
        case verificationFailed(String)
        case providerUnavailable
        var errorDescription: String? {
            switch self {
            case .verificationFailed(let msg): return "Verification failed: \(msg)"
            case .providerUnavailable: return "Provider unavailable"
            }
        }
    }

    // MARK: - Discovery

    private static func locateTestfilesURL() -> URL? {
        // #filePath points to this source file; walk up to the project root
        // (FileFluss/Services/VersionTestRunner.swift → two levels up).
        let here = URL(fileURLWithPath: #filePath)
        let projectRoot = here
            .deletingLastPathComponent() // Services
            .deletingLastPathComponent() // FileFluss
            .deletingLastPathComponent() // project root
        let candidate = projectRoot.appendingPathComponent("Testfiles", isDirectory: true)
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: candidate.path, isDirectory: &isDir), isDir.boolValue else {
            return nil
        }
        return candidate
    }

    private static func enumerateLocalFiles(root: URL) -> [LocalFile] {
        let fm = FileManager.default
        guard let enumerator = fm.enumerator(
            at: root,
            includingPropertiesForKeys: [.isRegularFileKey, .fileSizeKey],
            options: [.skipsHiddenFiles]
        ) else {
            return []
        }
        let rootPrefixLen = root.path.count + 1
        var files: [LocalFile] = []
        while let url = enumerator.nextObject() as? URL {
            let values = try? url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey])
            guard values?.isRegularFile == true else { continue }
            let rel = String(url.path.dropFirst(rootPrefixLen))
            // Skip prior report files so repeated runs don't cascade.
            if rel.hasPrefix("version-test-report-") { continue }
            let size = Int64(values?.fileSize ?? 0)
            files.append(LocalFile(relativePath: rel, url: url, size: size))
        }
        return files.sorted { $0.relativePath < $1.relativePath }
    }

    // MARK: - Per-account

    private static func runForAccount(account: CloudAccount, localFiles: [LocalFile]) async -> AccountResult {
        let displayName = account.displayName
        let providerName = account.providerType.displayName
        SupportLogger.shared.log(
            "--- Account: \(displayName) (\(providerName)) ---",
            category: logCategory, level: .notice
        )

        guard let provider = await SyncEngine.shared.provider(for: account.id) else {
            let miss = StepResult(label: "resolve provider", ok: false, durationMs: 0,
                                  error: VersionTestError.providerUnavailable.localizedDescription,
                                  diagnostics: nil)
            return AccountResult(
                accountDisplayName: displayName, provider: providerName,
                createFolder: miss, uploads: [], replaces: [], deletes: [], cleanupFolder: miss
            )
        }

        let testFolderPath = "/" + testFolderName

        // Phase 1 — create folder
        let createStep = await measure(
            name: "create \(testFolderName)",
            diagnostics: { await listingDiagnostic(provider: provider, parent: "/", expectedName: testFolderName) }
        ) {
            try await provider.createDirectory(at: testFolderPath)
            let rootContents = try await provider.listDirectory(at: "/")
            guard rootContents.contains(where: { $0.name == testFolderName && $0.isDirectory }) else {
                throw VersionTestError.verificationFailed("folder not present after create")
            }
        }

        if !createStep.ok {
            // Still attempt cleanup so we leave nothing behind.
            let cleanup = await measure(name: "cleanup \(testFolderName)") {
                try? await provider.deleteItem(at: testFolderPath)
            }
            return AccountResult(
                accountDisplayName: displayName, provider: providerName,
                createFolder: createStep, uploads: [], replaces: [], deletes: [],
                cleanupFolder: cleanup
            )
        }

        // Phase 2 — upload
        var uploads: [StepResult] = []
        for local in localFiles {
            let destPath = testFolderPath + "/" + local.relativePath
            let parentPath = (destPath as NSString).deletingLastPathComponent
            let filename = (destPath as NSString).lastPathComponent
            let step = await measure(
                name: "upload \(local.relativePath)",
                diagnostics: { await listingDiagnostic(provider: provider, parent: parentPath, expectedName: filename) }
            ) {
                if parentPath != testFolderPath {
                    try await provider.createDirectory(at: parentPath)
                }
                try await provider.uploadFile(from: local.url, to: destPath)
                try await verifyPresent(destPath: destPath, on: provider, expectedDirectory: false)
            }
            uploads.append(step)
        }

        // Phase 3 — replace (upload again)
        var replaces: [StepResult] = []
        for local in localFiles {
            let destPath = testFolderPath + "/" + local.relativePath
            let parentPath = (destPath as NSString).deletingLastPathComponent
            let filename = (destPath as NSString).lastPathComponent
            let step = await measure(
                name: "replace \(local.relativePath)",
                diagnostics: { await listingDiagnostic(provider: provider, parent: parentPath, expectedName: filename) }
            ) {
                try await provider.uploadFile(from: local.url, to: destPath)
                try await verifyPresent(destPath: destPath, on: provider, expectedDirectory: false)
                let meta = try await provider.getFileMetadata(at: destPath)
                if meta.size == 0 && local.size > 0 {
                    throw VersionTestError.verificationFailed("size 0 after replace (expected \(local.size))")
                }
            }
            replaces.append(step)
        }

        // Phase 4 — delete
        var deletes: [StepResult] = []
        for local in localFiles {
            let destPath = testFolderPath + "/" + local.relativePath
            let parentPath = (destPath as NSString).deletingLastPathComponent
            let filename = (destPath as NSString).lastPathComponent
            let step = await measure(
                name: "delete \(local.relativePath)",
                diagnostics: { await listingDiagnostic(provider: provider, parent: parentPath, expectedName: filename) }
            ) {
                try await provider.deleteItem(at: destPath)
                let contents = (try? await provider.listDirectory(at: parentPath)) ?? []
                if contents.contains(where: { $0.name == filename && !$0.isDirectory }) {
                    throw VersionTestError.verificationFailed("file still present after delete")
                }
            }
            deletes.append(step)
        }

        // Phase 5 — cleanup (always attempted)
        let cleanup = await measure(
            name: "cleanup \(testFolderName)",
            diagnostics: { await listingDiagnostic(provider: provider, parent: "/", expectedName: testFolderName) }
        ) {
            try await provider.deleteItem(at: testFolderPath)
            try await verifyAbsent(parent: "/", name: testFolderName, on: provider)
        }

        return AccountResult(
            accountDisplayName: displayName, provider: providerName,
            createFolder: createStep, uploads: uploads, replaces: replaces, deletes: deletes,
            cleanupFolder: cleanup
        )
    }

    private static func verifyAbsent(parent: String, name: String, on provider: any CloudProvider) async throws {
        // Mirror of verifyPresent for deletion — pCloud's listfolder occasionally
        // still includes a just-deleted folder on the immediate next call.
        let delaysMs: [UInt64] = [0, 250, 500, 1000]
        for delayMs in delaysMs {
            if delayMs > 0 {
                try? await Task.sleep(nanoseconds: delayMs * 1_000_000)
            }
            let contents = (try? await provider.listDirectory(at: parent)) ?? []
            if !contents.contains(where: { $0.name == name }) {
                return
            }
        }
        throw VersionTestError.verificationFailed("folder still present after cleanup")
    }

    private static func verifyPresent(destPath: String, on provider: any CloudProvider, expectedDirectory: Bool) async throws {
        let parent = (destPath as NSString).deletingLastPathComponent
        let filename = (destPath as NSString).lastPathComponent
        // Some providers (seen on pCloud) serve listfolder from an eventually
        // consistent view — a freshly uploaded file occasionally doesn't show
        // up on the immediate next list. Poll with short backoff so the test
        // reflects real cross-provider behavior instead of that racey moment.
        let delaysMs: [UInt64] = [0, 250, 500, 1000]
        var lastError: Error = VersionTestError.verificationFailed("not present in \(parent) listing")
        for delayMs in delaysMs {
            if delayMs > 0 {
                try? await Task.sleep(nanoseconds: delayMs * 1_000_000)
            }
            do {
                let contents = try await provider.listDirectory(at: parent)
                if contents.contains(where: { $0.name == filename && $0.isDirectory == expectedDirectory }) {
                    return
                }
            } catch {
                lastError = error
            }
        }
        throw lastError
    }

    // MARK: - Helpers

    private static func measure(
        name: String,
        diagnostics: (() async -> String?)? = nil,
        work: () async throws -> Void
    ) async -> StepResult {
        let start = Date()
        do {
            try await work()
            let ms = Int(Date().timeIntervalSince(start) * 1000)
            SupportLogger.shared.log("OK  \(name) [\(ms)ms]", category: logCategory)
            return StepResult(label: name, ok: true, durationMs: ms, error: nil, diagnostics: nil)
        } catch {
            let ms = Int(Date().timeIntervalSince(start) * 1000)
            let msg = error.localizedDescription
            SupportLogger.shared.log("FAIL \(name) — \(msg) [\(ms)ms]", category: logCategory, level: .error)
            let diag = await diagnostics?()
            if let diag, !diag.isEmpty {
                SupportLogger.shared.log("     diag: \(diag)", category: logCategory, level: .error)
            }
            return StepResult(label: name, ok: false, durationMs: ms, error: msg, diagnostics: diag)
        }
    }

    /// Produces a compact, deterministic dump of a parent directory's
    /// contents, used as diagnostic context when a step fails. Marks whether
    /// the expected filename appears, since that's usually the crux.
    private static func listingDiagnostic(
        provider: any CloudProvider,
        parent: String,
        expectedName: String?
    ) async -> String {
        do {
            let contents = try await provider.listDirectory(at: parent)
            let total = contents.count
            let matches = contents.filter { $0.name == expectedName }
            let header: String
            if let expectedName {
                header = "listDirectory(\(parent)) → \(total) item(s); matches for '\(expectedName)': \(matches.count)"
            } else {
                header = "listDirectory(\(parent)) → \(total) item(s)"
            }
            let lines = contents
                .sorted { $0.name < $1.name }
                .prefix(40)
                .map { item -> String in
                    let kind = item.isDirectory ? "d" : "f"
                    return "  [\(kind)] \(item.name) (size=\(item.size), id=\(item.id))"
                }
            let more = contents.count > 40 ? "\n  … \(contents.count - 40) more" : ""
            return ([header] + lines).joined(separator: "\n") + more
        } catch {
            return "listDirectory(\(parent)) threw: \(error.localizedDescription)"
        }
    }

    // MARK: - Report

    private static func writeReport(_ report: Report, to dir: URL) -> URL? {
        let stamp = fileNameStamp.string(from: report.startDate)
        let url = dir.appendingPathComponent("version-test-report-\(stamp).md")
        let text = renderMarkdown(report)
        do {
            try text.write(to: url, atomically: true, encoding: .utf8)
            return url
        } catch {
            return nil
        }
    }

    private static let fileNameStamp: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyyMMdd-HHmmss"
        return f
    }()

    private static func renderMarkdown(_ report: Report) -> String {
        let info = Bundle.main.infoDictionary
        let appVersion = info?["CFBundleShortVersionString"] as? String ?? "?"
        let build = info?["CFBundleVersion"] as? String ?? "?"
        let started = ISO8601DateFormatter().string(from: report.startDate)
        let duration = Int(report.endDate.timeIntervalSince(report.startDate))

        var out = "# FileFluss Version Test Report\n\n"
        out += "- **App version**: \(appVersion) (build \(build))\n"
        out += "- **Started**: \(started)\n"
        out += "- **Duration**: \(duration)s\n"
        out += "- **Test files**: \(report.files.count) file(s) from `\(report.testfilesURL.path)`\n\n"

        let totalPass = report.accounts.reduce(0) { $0 + $1.passCount }
        let totalCount = report.accounts.reduce(0) { $0 + $1.totalCount }
        let overall = totalPass == totalCount
            ? "PASS"
            : "FAIL (\(totalCount - totalPass) failure\(totalCount - totalPass == 1 ? "" : "s"))"
        out += "## Overall: \(overall) — \(totalPass)/\(totalCount) steps\n\n"

        for account in report.accounts {
            let mark = account.passCount == account.totalCount ? "PASS" : "FAIL"
            out += "## \(mark) — \(account.accountDisplayName) (\(account.provider))\n\n"
            out += "\(account.passCount)/\(account.totalCount) steps\n\n"
            out += "| Phase | Step | Result | Duration | Error |\n"
            out += "|-------|------|--------|----------|-------|\n"

            func row(phase: String, step: StepResult) -> String {
                let res = step.ok ? "OK" : "FAIL"
                let err = (step.error ?? "").replacingOccurrences(of: "|", with: "\\|")
                let label = step.label.replacingOccurrences(of: "|", with: "\\|")
                return "| \(phase) | \(label) | \(res) | \(step.durationMs) ms | \(err) |\n"
            }
            out += row(phase: "Create folder", step: account.createFolder)
            for step in account.uploads { out += row(phase: "Upload", step: step) }
            for step in account.replaces { out += row(phase: "Replace", step: step) }
            for step in account.deletes { out += row(phase: "Delete", step: step) }
            out += row(phase: "Cleanup folder", step: account.cleanupFolder)
            out += "\n"

            // Diagnostics section: emit the listing captured at each failure.
            let failing = account.allSteps.filter { !$0.ok && ($0.diagnostics?.isEmpty == false) }
            if !failing.isEmpty {
                out += "<details><summary>Diagnostics for failing steps</summary>\n\n"
                for step in failing {
                    out += "**\(step.label)**\n\n```\n\(step.diagnostics ?? "")\n```\n\n"
                }
                out += "</details>\n\n"
            }
        }

        return out
    }

    // MARK: - Alerts

    private static func presentSimpleAlert(title: String, message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.runModal()
    }

    private static func showCompletionAlert(report: Report, path: URL) {
        let totalPass = report.accounts.reduce(0) { $0 + $1.passCount }
        let totalCount = report.accounts.reduce(0) { $0 + $1.totalCount }
        let alert = NSAlert()
        alert.messageText = totalPass == totalCount
            ? "Version Test — All Passed"
            : "Version Test — \(totalCount - totalPass) Failure(s)"
        alert.informativeText = "\(totalPass) of \(totalCount) steps passed.\n\nReport:\n\(path.lastPathComponent)"
        alert.addButton(withTitle: "Open Report")
        alert.addButton(withTitle: "Reveal in Finder")
        alert.addButton(withTitle: "Close")
        let response = alert.runModal()
        switch response {
        case .alertFirstButtonReturn:
            NSWorkspace.shared.open(path)
        case .alertSecondButtonReturn:
            NSWorkspace.shared.activateFileViewerSelecting([path])
        default:
            break
        }
    }
}
