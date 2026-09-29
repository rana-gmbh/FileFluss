import Foundation
import FileFlussCore
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

        let allAccounts = appState.syncManager.accounts
        guard !allAccounts.isEmpty else {
            presentSimpleAlert(title: "Version Test",
                               message: "No cloud accounts connected.")
            return
        }

        // A full pass over every account takes minutes; when chasing one
        // provider's failure you want just that provider. Remembering the
        // last choice means a repeated single-provider run is two clicks.
        guard let accounts = selectAccounts(from: allAccounts), !accounts.isEmpty else {
            return
        }

        SupportLogger.shared.log(
            "Version test starting — \(localFiles.count) file(s), \(accounts.count) account(s)",
            category: logCategory, level: .notice
        )
        let startDate = Date()

        // Per account: create folder + upload/replace/delete per file +
        // up to two share-link steps + cleanup.
        let stepsPerAccount = 3 * localFiles.count + 4
        let progress = ProgressPanel(total: accounts.count * stepsPerAccount)
        defer { progress.close() }

        var accountResults: [AccountResult] = []
        for (index, account) in accounts.enumerated() {
            progress.beginAccount(account.displayName, index: index + 1, of: accounts.count)
            if progress.isCancelled { break }
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
                localFiles: localFiles,
                progress: progress
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
        /// The step couldn't be carried out for a reason outside the app —
        /// a server that wouldn't answer at all. Counted as passing, since
        /// it says nothing about FileFluss, but reported as SKIP so it
        /// isn't mistaken for a clean result.
        var skipped: Bool = false
    }

    private struct AccountResult {
        let accountDisplayName: String
        let provider: String
        let createFolder: StepResult
        let uploads: [StepResult]
        let replaces: [StepResult]
        /// Share-link steps: creating the link, then fetching it without any
        /// credentials. Empty for providers with no sharing API.
        var shareLink: [StepResult] = []
        /// The created link, recorded so a human can eyeball it. It dies with
        /// the test file during the delete phase a moment later.
        var shareLinkURL: String?
        /// The provider's own remark about the link — notably Box saying an
        /// admin downgraded it from public, which is exactly what explains a
        /// failing anonymous fetch.
        var shareLinkNote: String?
        let deletes: [StepResult]
        let cleanupFolder: StepResult

        var allSteps: [StepResult] {
            [createFolder] + uploads + replaces + shareLink + deletes + [cleanupFolder]
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

    private static func runForAccount(
        account: CloudAccount,
        localFiles: [LocalFile],
        progress: ProgressPanel? = nil
    ) async -> AccountResult {
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

        // S3-style providers expose the root as a list of buckets, not
        // ordinary folders — `createDirectory(at: "/Foo")` would attempt
        // to create a bucket called "Foo", which fails for many reasons
        // (illegal characters, region/quota restrictions). Pick an
        // existing bucket and run the test inside it instead.
        let testRoot = await resolveTestRoot(provider: provider, account: account)
        let testFolderPath = testRoot == "/" ? "/" + testFolderName : testRoot + "/" + testFolderName

        // Phase 1 — create folder
        let createStep = await measure(
            name: "create \(testFolderName)",
            progress: progress,
            diagnostics: { await listingDiagnostic(provider: provider, parent: testRoot, expectedName: testFolderName) }
        ) {
            try await provider.createDirectory(at: testFolderPath)
            // Poll with backoff like verifyPresent/verifyAbsent — pCloud and
            // Internxt serve listfolder from an eventually-consistent view, so
            // a just-created folder occasionally isn't in the immediate next
            // listing even though the create succeeded.
            try await verifyPresent(destPath: testFolderPath, on: provider, expectedDirectory: true)
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
        /// Files that actually uploaded. Deleting one that never arrived
        /// reports "item not found", which turns a single server error into
        /// a cascade of failures and buries the real cause.
        var uploadedFiles: Set<String> = []
        for local in localFiles {
            let destPath = testFolderPath + "/" + local.relativePath
            let parentPath = (destPath as NSString).deletingLastPathComponent
            let filename = (destPath as NSString).lastPathComponent
            let step = await measure(
                name: "upload \(local.relativePath)",
                progress: progress,
                diagnostics: { await listingDiagnostic(provider: provider, parent: parentPath, expectedName: filename) }
            ) {
                if parentPath != testFolderPath {
                    try await provider.createDirectory(at: parentPath)
                }
                try await provider.uploadFile(from: local.url, to: destPath)
                try await verifyPresent(destPath: destPath, on: provider, expectedDirectory: false)
            }
            uploads.append(step)
            if step.ok { uploadedFiles.insert(local.relativePath) }
        }

        // Cancelling stops new work but never skips phases 4 and 5 — the
        // test folder must not be left behind on the user's account.
        let cancelled = { progress?.isCancelled == true }

        // Phase 3 — replace (upload again)
        var replaces: [StepResult] = []
        for local in localFiles where !cancelled() && uploadedFiles.contains(local.relativePath) {
            let destPath = testFolderPath + "/" + local.relativePath
            let parentPath = (destPath as NSString).deletingLastPathComponent
            let filename = (destPath as NSString).lastPathComponent
            let step = await measure(
                name: "replace \(local.relativePath)",
                progress: progress,
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

        // Phase 3.5 — share link. Runs against the first uploaded test file,
        // never a real file of the user's, and the file is deleted moments
        // later in phase 4, which takes the link down with it.
        var shareSteps: [StepResult] = []
        var shareURL: String?
        var shareNote: String?
        let shareCaps = provider.shareLinkCapabilities
        if let firstFile = localFiles.first, uploads.first?.ok == true, !cancelled() {
            let sharePath = testFolderPath + "/" + firstFile.relativePath
            if shareCaps.canCreate {
                var created: CloudShareLink?
                let createLinkStep = await measure(name: "create share link (\(firstFile.relativePath))", progress: progress) {
                    // Deliberately no password and no expiry unless the
                    // provider insists: both are paid-plan features almost
                    // everywhere, and a plan rejection would look like a bug.
                    let options = ShareLinkOptions(
                        expiry: shareCaps.requiresExpiry ? Date().addingTimeInterval(3600) : nil
                    )
                    let link = try await provider.createShareLink(at: sharePath, options: options)
                    created = link
                }
                shareSteps.append(createLinkStep)

                if let link = created {
                    shareNote = link.note
                    // The real proof: fetch the link with no credentials at
                    // all. A link that only works while signed in is useless
                    // to the person it was sent to.
                    //
                    // Mirror the app's fallback: when a provider refuses to
                    // serve its own direct-download URL (Box does so on plans
                    // without direct links), the app copies the share page
                    // instead, so that is what has to be reachable.
                    var candidate = link.preferredURL
                    if let direct = link.directDownloadURL, direct != link.url {
                        let directResult = await PublicURLCheck.isReachable(direct)
                        // Mirrors the app: a check that never reached the
                        // server is not the provider refusing direct links.
                        if !directResult.ok, !directResult.couldNotCheck {
                            candidate = link.url
                            let reason = directResult.detail ?? "no detail"
                            shareNote = [shareNote, "Direct download link rejected by the provider, fell back to the share page — \(reason)"]
                                .compactMap { $0 }
                                .joined(separator: " · ")
                        }
                    }
                    shareURL = candidate.absoluteString
                    let fetchURL = candidate
                    shareSteps.append(await shareFetchStep(fetchURL, progress: progress))
                }
            } else {
                shareSteps.append(StepResult(
                    label: "share link not supported by this provider",
                    ok: true, durationMs: 0, error: nil, diagnostics: nil
                ))
            }
        }

        // Phase 4 — delete
        var deletes: [StepResult] = []
        for local in localFiles {
            guard uploadedFiles.contains(local.relativePath) else {
                deletes.append(StepResult(
                    label: "delete \(local.relativePath) — skipped, upload had failed",
                    ok: true, durationMs: 0, error: nil, diagnostics: nil
                ))
                continue
            }
            let destPath = testFolderPath + "/" + local.relativePath
            let parentPath = (destPath as NSString).deletingLastPathComponent
            let filename = (destPath as NSString).lastPathComponent
            let step = await measure(
                name: "delete \(local.relativePath)",
                progress: progress,
                diagnostics: { await listingDiagnostic(provider: provider, parent: parentPath, expectedName: filename) }
            ) {
                try await provider.deleteItem(at: destPath)
                // Use the same retry-with-backoff helper the cleanup step
                // uses. S3-flavoured providers (notably Synology C2) hit
                // list-after-delete eventual consistency: the DELETE call
                // returns 2xx but the very next LIST still shows the
                // object for a few hundred ms. A single immediate listing
                // intermittently false-fails the test.
                try await verifyAbsent(parent: parentPath, name: filename, on: provider, kindLabel: "file")
            }
            deletes.append(step)
        }

        // Phase 5 — cleanup (always attempted)
        let cleanup = await measure(
            name: "cleanup \(testFolderName)",
            progress: progress,
            diagnostics: { await listingDiagnostic(provider: provider, parent: "/", expectedName: testFolderName) }
        ) {
            try await provider.deleteItem(at: testFolderPath)
            try await verifyAbsent(parent: "/", name: testFolderName, on: provider, kindLabel: "folder")
        }

        return AccountResult(
            accountDisplayName: displayName, provider: providerName,
            createFolder: createStep, uploads: uploads, replaces: replaces,
            shareLink: shareSteps, shareLinkURL: shareURL, shareLinkNote: shareNote,
            deletes: deletes, cleanupFolder: cleanup
        )
    }

    /// Anonymous reachability check, shared with the app's own share action
    /// so the test exercises exactly what users get.
    ///
    /// Not built on `measure`, because this step has three outcomes rather
    /// than two: a server that never answered — an untrusted certificate on
    /// a self-hosted box, say — tells us nothing about the share link, and
    /// reporting that as a failure is how a passing build looks broken.
    private static func shareFetchStep(_ url: URL, progress: ProgressPanel?) async -> StepResult {
        let name = "fetch share link anonymously"
        progress?.beginStep(name)
        defer { progress?.finishStep() }
        let start = Date()
        let result = await PublicURLCheck.isReachable(url)
        let ms = Int(Date().timeIntervalSince(start) * 1000)

        switch result.outcome {
        case .reachable:
            SupportLogger.shared.log("OK  \(name) [\(ms)ms]", category: logCategory)
            return StepResult(label: name, ok: true, durationMs: ms, error: nil, diagnostics: nil)
        case .unverified:
            let msg = "not checked — \(result.detail ?? "the server could not be reached")"
            SupportLogger.shared.log("SKIP \(name) — \(msg) [\(ms)ms]", category: logCategory)
            return StepResult(label: name, ok: true, durationMs: ms, error: msg, diagnostics: nil, skipped: true)
        case .notPublic:
            let msg = "share link is not reachable anonymously — \(result.detail ?? "no detail")"
            SupportLogger.shared.log("FAIL \(name) — \(msg) [\(ms)ms]", category: logCategory, level: .error)
            return StepResult(label: name, ok: false, durationMs: ms, error: msg, diagnostics: nil)
        }
    }

    private static func verifyAbsent(
        parent: String,
        name: String,
        on provider: any CloudProvider,
        kindLabel: String = "folder"
    ) async throws {
        // Mirror of verifyPresent for deletion. Two known offenders:
        //   - pCloud's listfolder occasionally still includes a just-deleted
        //     folder on the immediate next call.
        //   - Synology C2 (S3 API) is eventually consistent for list-after-
        //     delete: the DELETE returns 2xx but the next LIST briefly still
        //     shows the object.
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
        throw VersionTestError.verificationFailed("\(kindLabel) still present after delete")
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

    /// Returns the path under which the version test should run for the
    /// given account. For most providers this is `"/"` (the account
    /// root). S3-flavoured providers expose the root as a list of
    /// buckets — we can't create a folder there directly, so we list the
    /// root once and nest the test inside the first existing bucket.
    /// If listing fails or there are no buckets, fall through to `"/"`
    /// and let the create step surface the underlying error.
    private static func resolveTestRoot(provider: any CloudProvider, account: CloudAccount) async -> String {
        // Library-/bucket-rooted providers: the root listing is the set of
        // top-level containers (S3 buckets, Seafile libraries) rather than a
        // navigable file tree. Creating a folder at "/" fails on those, so
        // nest the test inside the first existing container instead.
        let bucketRooted: Set<CloudProviderType> = [.s3, .s3Compatible, .synologyC2, .seafile]
        guard bucketRooted.contains(account.providerType) else { return "/" }
        // An account scoped to one bucket already says which: use it rather
        // than listing the account's buckets, which such a key may not be
        // allowed to do (see issue #53).
        if account.rootPath != "/" && !account.rootPath.isEmpty {
            return account.rootPath
        }
        do {
            let buckets = try await provider.listDirectory(at: "/")
                .filter { $0.isDirectory }
                .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
            guard let first = buckets.first else { return "/" }
            return "/\(first.name)"
        } catch {
            return "/"
        }
    }

    private static func measure(
        name: String,
        progress: ProgressPanel? = nil,
        diagnostics: (() async -> String?)? = nil,
        work: () async throws -> Void
    ) async -> StepResult {
        progress?.beginStep(name)
        defer { progress?.finishStep() }
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
                let res = step.skipped ? "SKIP" : (step.ok ? "OK" : "FAIL")
                let err = (step.error ?? "").replacingOccurrences(of: "|", with: "\\|")
                let label = step.label.replacingOccurrences(of: "|", with: "\\|")
                return "| \(phase) | \(label) | \(res) | \(step.durationMs) ms | \(err) |\n"
            }
            out += row(phase: "Create folder", step: account.createFolder)
            for step in account.uploads { out += row(phase: "Upload", step: step) }
            for step in account.replaces { out += row(phase: "Replace", step: step) }
            for step in account.shareLink { out += row(phase: "Share link", step: step) }
            for step in account.deletes { out += row(phase: "Delete", step: step) }
            out += row(phase: "Cleanup folder", step: account.cleanupFolder)
            out += "\n"

            if let note = account.shareLinkNote {
                out += "Share link note from the provider: \(note)\n\n"
            }
            if let link = account.shareLinkURL {
                out += "Share link created: `\(link)`\n\n"
                out += "_The shared file is deleted in the delete phase above, so this link is already dead._\n\n"
            }

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

    // MARK: - Progress

    /// Floating progress panel for the run. A full pass is minutes of work
    /// across every account, and without this the app looks hung.
    @MainActor
    final class ProgressPanel {
        private let window: NSWindow
        private let accountLabel: NSTextField
        private let stepLabel: NSTextField
        private let bar: NSProgressIndicator
        private let cancelButton: NSButton
        private var completed = 0
        private var total = 1
        private(set) var isCancelled = false

        init(total: Int) {
            self.total = max(1, total)

            accountLabel = NSTextField(labelWithString: "Starting…")
            accountLabel.font = .systemFont(ofSize: 13, weight: .semibold)
            accountLabel.lineBreakMode = .byTruncatingTail

            stepLabel = NSTextField(labelWithString: "")
            stepLabel.font = .systemFont(ofSize: 11)
            stepLabel.textColor = .secondaryLabelColor
            stepLabel.lineBreakMode = .byTruncatingMiddle

            bar = NSProgressIndicator()
            bar.isIndeterminate = false
            bar.minValue = 0
            bar.maxValue = Double(self.total)
            bar.doubleValue = 0
            bar.controlSize = .regular

            cancelButton = NSButton(title: "Cancel", target: nil, action: nil)
            cancelButton.bezelStyle = .rounded

            let stack = NSStackView(views: [accountLabel, bar, stepLabel, cancelButton])
            stack.orientation = .vertical
            stack.alignment = .leading
            stack.spacing = 8
            stack.edgeInsets = NSEdgeInsets(top: 16, left: 16, bottom: 16, right: 16)
            stack.translatesAutoresizingMaskIntoConstraints = false

            window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 440, height: 130),
                styleMask: [.titled],
                backing: .buffered,
                defer: false
            )
            window.title = "Version Test"
            window.isReleasedWhenClosed = false
            window.level = .floating
            window.center()
            window.contentView = stack

            NSLayoutConstraint.activate([
                bar.widthAnchor.constraint(equalToConstant: 408),
                accountLabel.widthAnchor.constraint(equalToConstant: 408),
                stepLabel.widthAnchor.constraint(equalToConstant: 408),
            ])

            cancelButton.target = self
            cancelButton.action = #selector(cancelPressed)
            window.makeKeyAndOrderFront(nil)
        }

        @objc private func cancelPressed() {
            isCancelled = true
            cancelButton.isEnabled = false
            stepLabel.stringValue = "Cancelling — finishing the current step and cleaning up…"
        }

        func beginAccount(_ name: String, index: Int, of count: Int) {
            accountLabel.stringValue = "\(name)  (\(index) of \(count))"
        }

        func beginStep(_ label: String) {
            stepLabel.stringValue = label
        }

        func finishStep() {
            completed += 1
            // The step count is an estimate (share-link phases vary per
            // provider), so never let the bar run past its own end.
            bar.doubleValue = Double(min(completed, total))
        }

        func close() {
            window.orderOut(nil)
        }
    }

    // MARK: - Alerts

    /// Checkbox list of the connected accounts. Returns nil when cancelled.
    /// Skipped when there is only one account — nothing to choose.
    private static func selectAccounts(from accounts: [CloudAccount]) -> [CloudAccount]? {
        guard accounts.count > 1 else { return accounts }

        let remembered = Set(UserDefaults.standard.stringArray(forKey: selectionDefaultsKey) ?? [])

        let alert = NSAlert()
        alert.messageText = "Run Version Test"
        alert.informativeText = "Choose the accounts to test. Each one runs the full create/upload/replace/share/delete pass."
        alert.addButton(withTitle: "Run Test")
        alert.addButton(withTitle: "Cancel")

        let rowHeight: CGFloat = 22
        let stack = NSStackView()
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 4

        var boxes: [(NSButton, CloudAccount)] = []
        for account in accounts {
            let box = NSButton(checkboxWithTitle: "\(account.displayName) — \(account.providerType.displayName)", target: nil, action: nil)
            // First run has nothing remembered: test everything.
            box.state = (remembered.isEmpty || remembered.contains(account.id.uuidString)) ? .on : .off
            stack.addArrangedSubview(box)
            boxes.append((box, account))
        }

        let toggleAll = NSButton(title: "Select All / None", target: AllToggler.shared, action: #selector(AllToggler.toggle(_:)))
        toggleAll.bezelStyle = .rounded
        AllToggler.shared.boxes = boxes.map { $0.0 }
        stack.addArrangedSubview(toggleAll)

        let contentHeight = rowHeight * CGFloat(accounts.count + 1) + 8
        let documentView = NSView(frame: NSRect(x: 0, y: 0, width: 360, height: contentHeight))
        stack.frame = documentView.bounds
        documentView.addSubview(stack)

        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 360, height: min(contentHeight, 420)))
        scroll.hasVerticalScroller = contentHeight > 420
        scroll.documentView = documentView
        alert.accessoryView = scroll

        guard alert.runModal() == .alertFirstButtonReturn else { return nil }

        let chosen = boxes.filter { $0.0.state == .on }.map { $0.1 }
        UserDefaults.standard.set(chosen.map { $0.id.uuidString }, forKey: selectionDefaultsKey)
        if chosen.isEmpty {
            presentSimpleAlert(title: "Version Test", message: "No accounts selected.")
        }
        return chosen
    }

    private static let selectionDefaultsKey = "versionTestSelectedAccountIds"

    /// Target for the "Select All / None" button — NSButton needs an
    /// Objective-C target, and the alert is built inside a static func.
    @MainActor
    private final class AllToggler: NSObject {
        static let shared = AllToggler()
        var boxes: [NSButton] = []

        @objc func toggle(_ sender: NSButton) {
            let turningOn = boxes.contains { $0.state == .off }
            for box in boxes { box.state = turningOn ? .on : .off }
        }
    }

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
