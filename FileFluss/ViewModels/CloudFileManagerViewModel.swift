import SwiftUI
import FileFlussCore
import QuickLookUI
import os

private let cloudFileVMLog = Logger(subsystem: "com.rana.FileFluss", category: "cloudFileVM")

@Observable @MainActor
final class CloudFileManagerViewModel {
    let accountId: UUID
    let providerType: CloudProviderType?
    var currentPath: String = "/"
    var items: [CloudFileItem] = [] { didSet { _filteredItemsCache = nil; _listingSummaryCache = nil } }
    var selectedItemIDs: Set<String> = []
    var isLoading = false
    var error: String?
    /// True when the latest load failed because the stored credentials are no
    /// longer valid (`CloudProviderError.notAuthenticated`/`.unauthorized`).
    /// The panel's error view uses this to surface a "Sign In Again" button.
    var needsReAuth: Bool = false
    var searchText: String = "" { didSet { _filteredItemsCache = nil; _listingSummaryCache = nil } }
    /// Toggled by the Edit → Quick Filter… command; when true, the panel
    /// shows an inline filter bar bound to `searchText`.
    var isFilterBarVisible: Bool = false
    var sortOrder: SortOrder = .name { didSet { _filteredItemsCache = nil; _listingSummaryCache = nil } }
    var sortAscending: Bool = true { didSet { _filteredItemsCache = nil; _listingSummaryCache = nil } }
    /// When false (default), dot-prefixed entries (`.gitignore`, `.ssh`,
    /// SFTP server-side `lost+found`-style dotfiles) are hidden from the
    /// listing. Mirrored to the local `FileManagerViewModel.showHiddenFiles`
    /// flag by the toolbar toggle.
    var showHiddenFiles: Bool = false { didSet { _filteredItemsCache = nil; _listingSummaryCache = nil } }

    /// Memoised result of `filteredItems` — recomputed lazily and invalidated
    /// (via the `didSet`s above) only when an input that affects it changes:
    /// items, search text, sort, or hidden-files. `selectedItemIDs` is not an
    /// input, so selecting rows no longer re-filters and re-sorts the listing.
    /// `@ObservationIgnored` so writing the cache never triggers a SwiftUI
    /// invalidation on its own.
    @ObservationIgnored private var _filteredItemsCache: [CloudFileItem]?
    @ObservationIgnored private var _listingSummaryCache: ListingSummary?

    /// See `FileManagerViewModel.listingSummary` — same reasoning.
    var listingSummary: ListingSummary {
        let items = filteredItems
        if let cached = _listingSummaryCache { return cached }
        var summary = ListingSummary()
        for item in items {
            if item.isDirectory {
                summary.folderCount += 1
            } else {
                summary.fileCount += 1
                summary.totalSize += item.size
            }
        }
        _listingSummaryCache = summary
        return summary
    }
    /// Cached storage quota snapshot for the panel's status bar. nil
    /// either because the provider doesn't support quota (S3, SFTP,
    /// WordPress) or because the first probe hasn't completed.
    var storageQuota: CloudStorageQuota?
    /// Known public-link state per remote path: a link when the file is
    /// shared, nil when it is known not to be. A path absent from the
    /// dictionary simply hasn't been checked. Populated on demand (when a
    /// single file is selected) and after every share action, because the
    /// context menu is built synchronously and can't await a lookup.
    var shareStates: [String: CloudShareLink?] = [:]

    /// Share options this account has already rejected. Providers bill
    /// passwords and expiry dates, and the API answers a request that uses
    /// one with a flat refusal ("Forbidden"), so the only reliable way to
    /// know is to be told once — after which the sheet stops offering it.
    var rejectedPassword: Bool {
        get { UserDefaults.standard.bool(forKey: "shareOptionRejected.password.\(accountId.uuidString)") }
        set { UserDefaults.standard.set(newValue, forKey: "shareOptionRejected.password.\(accountId.uuidString)") }
    }
    var rejectedExpiry: Bool {
        get { UserDefaults.standard.bool(forKey: "shareOptionRejected.expiry.\(accountId.uuidString)") }
        set { UserDefaults.standard.set(newValue, forKey: "shareOptionRejected.expiry.\(accountId.uuidString)") }
    }

    /// Set once this account has produced a link that turned out to demand
    /// a sign-in. Warning up front beats letting the user send a dead link
    /// and find out from the recipient.
    var linksAreNotPublic: Bool {
        get { UserDefaults.standard.bool(forKey: "shareLinksNotPublic.\(accountId.uuidString)") }
        set { UserDefaults.standard.set(newValue, forKey: "shareLinksNotPublic.\(accountId.uuidString)") }
    }

    /// Last share failure. Deliberately separate from `error`, which
    /// replaces the whole panel with an error view — a share that fails
    /// must not take the file listing down with it.
    var shareError: String?

    /// What this account's provider can do with public share links.
    /// Refreshed with each directory load; `.unsupported` until then, so
    /// the menu simply omits the entries for providers without sharing.
    var shareCapabilities: ShareLinkCapabilities = .unsupported

    let quickLookController = QuickLookController()

    // MARK: - Per-item conflict resolution

    var conflictDirection: ConflictDirection = .leftToRight
    var pendingConflict: PendingConflict?
    private var conflictContinuation: CheckedContinuation<ConflictResolution, Never>?

    func resolveConflict(_ resolution: ConflictResolution) {
        conflictContinuation?.resume(returning: resolution)
        conflictContinuation = nil
        pendingConflict = nil
    }


    private var pathHistory: [String] = ["/"]
    private var pathHistoryIndex: Int = 0
    private var tempDownloadDir: URL
    private var quickLookTask: Task<Void, Never>?

    enum SortOrder: String, CaseIterable {
        case name, date, size, kind

        var label: String {
            switch self {
            case .name: return "Name"
            case .date: return "Date Modified"
            case .size: return "Size"
            case .kind: return "Kind"
            }
        }
    }

    init(accountId: UUID, providerType: CloudProviderType? = nil) {
        self.accountId = accountId
        self.providerType = providerType
        self.tempDownloadDir = StagingLocation.base()
            .appendingPathComponent("FileFluss-cloud-\(accountId.uuidString)", isDirectory: true)
        // The cache survives across navigations so Quick Look / downloads
        // don't have to redo the network round-trip on every open and
        // Settings → Storage shows a non-zero value. Stale-content safety
        // is enforced at the read path below: a cached entry is only
        // returned when both size AND modification-date match the remote
        // item, so a re-uploaded file invalidates its cached copy.
        try? FileManager.default.createDirectory(at: tempDownloadDir, withIntermediateDirectories: true)
    }

    var filteredItems: [CloudFileItem] {
        // Read every input up front so SwiftUI observation keeps tracking
        // them as dependencies even on a cache hit — otherwise the view
        // would stop re-rendering when they change.
        let items = self.items
        let query = self.searchText
        let showHidden = self.showHiddenFiles
        _ = self.sortOrder
        _ = self.sortAscending
        if let cached = _filteredItemsCache { return cached }
        var filtered = items
        if !showHidden {
            filtered = filtered.filter { !$0.name.hasPrefix(".") }
        }
        if !query.isEmpty {
            filtered = filtered.filter { $0.name.localizedCaseInsensitiveContains(query) }
        }
        let result = sorted(filtered)
        _filteredItemsCache = result
        return result
    }

    var selectedItems: [CloudFileItem] {
        items.filter { selectedItemIDs.contains($0.id) }
    }

    // MARK: - Selection Size (for footer)

    var selectionSize: Int64? = nil
    var isCalculatingSelectionSize = false
    private var selectionSizeTask: Task<Void, Never>?

    func recalculateSelectionSize() {
        selectionSizeTask?.cancel()

        let selected = selectedItems
        guard !selected.isEmpty else {
            selectionSize = nil
            isCalculatingSelectionSize = false
            return
        }

        let files = selected.filter { !$0.isDirectory }
        let folders = selected.filter { $0.isDirectory }
        let fileSize = files.reduce(Int64(0)) { $0 + $1.size }

        if folders.isEmpty {
            selectionSize = fileSize
            isCalculatingSelectionSize = false
            return
        }

        isCalculatingSelectionSize = true
        selectionSize = fileSize

        selectionSizeTask = Task {
            var total = fileSize
            guard let provider = await SyncEngine.shared.provider(for: accountId) else { return }
            for folder in folders {
                guard !Task.isCancelled else { return }
                do {
                    let size = try await provider.folderSize(at: folder.path)
                    total += size
                } catch {
                    // skip folders that fail
                }
            }
            guard !Task.isCancelled else { return }
            self.selectionSize = total
            self.isCalculatingSelectionSize = false
        }
    }

    var canGoBack: Bool { pathHistoryIndex > 0 }
    var canGoForward: Bool { pathHistoryIndex < pathHistory.count - 1 }

    // MARK: - Navigation

    func loadDirectory(at path: String? = nil) async {
        let targetPath = path ?? currentPath
        // Explicit navigation (caller passed a path that's not where we
        // already are) flips the panel to the destination *immediately*:
        // path bar, items, selection all reset before the network call
        // begins. Without this, switching between cloud accounts — or any
        // navigation that goes through the sidebar — leaves the panel
        // showing the previous folder's items while the new path bar has
        // already moved on, which looks broken (the bug that surfaced in
        // testing: cloud A panel shows folder X but breadcrumb is at root
        // after coming back from cloud B). For pure refreshes
        // (path == nil) we keep showing the current items so a re-list
        // doesn't blink the panel.
        if let path, path != self.currentPath {
            self.currentPath = path
            pushToHistory(path)
            self.items = []
            self.selectedItemIDs.removeAll()
        }
        isLoading = true
        error = nil
        needsReAuth = false

        do {
            let provider = await SyncEngine.shared.provider(for: accountId)
            guard let provider else {
                error = "Cloud account not connected"
                isLoading = false
                return
            }

            let loadedItems = try await provider.listDirectory(at: targetPath)

            // Cheap (it's a static property) but only reachable through the
            // provider actor, so cache it here: the context menu is built
            // synchronously and can't await anything.
            self.shareCapabilities = provider.shareLinkCapabilities

            // A racing load might have already moved the panel elsewhere
            // (the user clicked rapidly through folders). Drop the result
            // if the user has navigated past us — items must always match
            // currentPath, not whatever was current when we started.
            guard self.currentPath == targetPath else { return }

            // Feed into search index (fire-and-forget)
            let accId = self.accountId
            Task.detached(priority: .utility) {
                await SearchIndex.shared.upsertItems(loadedItems, accountId: accId)
            }

            self.items = loadedItems
            self.selectedItemIDs.removeAll()
            self.isLoading = false
        } catch {
            // Same staleness guard for the error path — if the user has
            // moved on, the failure was for the *previous* folder and
            // shouldn't clobber the current panel state.
            guard self.currentPath == targetPath else { return }
            self.error = error.localizedDescription
            if let cpe = error as? CloudProviderError {
                switch cpe {
                case .notAuthenticated, .unauthorized, .invalidCredentials:
                    self.flagNeedsReAuthentication()
                default:
                    break
                }
            }
            self.isLoading = false
        }
    }

    /// Navigate to `path`. Unlike the previous "skip when the path didn't
    /// change" guard, this always issues a fresh load — clicking the
    /// current cloud account again in the sidebar should re-fetch the
    /// folder, since the sidebar is the user's "take me back here" lever.
    /// Pure no-op short-circuiting moves to `navigateTo(_:forceRefresh:)`
    /// when we need it.
    func navigateTo(_ path: String) async {
        if path == currentPath {
            await loadDirectory()
        } else {
            await loadDirectory(at: path)
        }
    }

    func navigateUp() async {
        guard currentPath != "/" else { return }
        let parent = (currentPath as NSString).deletingLastPathComponent
        await navigateTo(parent.isEmpty ? "/" : parent)
    }

    func navigateBack() async {
        guard canGoBack else { return }
        pathHistoryIndex -= 1
        let path = pathHistory[pathHistoryIndex]
        // Funnel through loadDirectory so the items + path bar reset
        // happens atomically — see the comment on loadDirectory. The
        // history-position bookkeeping is already done by the index
        // adjustment above; pushToHistory inside loadDirectory is a
        // no-op for a value that matches the current cursor.
        await loadDirectory(at: path)
    }

    func navigateForward() async {
        guard canGoForward else { return }
        pathHistoryIndex += 1
        let path = pathHistory[pathHistoryIndex]
        await loadDirectory(at: path)
    }

    func refresh() async {
        await loadDirectory()
    }

    func openItem(_ item: CloudFileItem) async {
        if item.isDirectory {
            await navigateTo(item.path)
        }
    }

    // MARK: - Create & Rename

    func createNewFolder(named name: String) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return }
        let folderPath = currentPath == "/" ? "/\(name)" : "\(currentPath)/\(name)"
        do {
            try await provider.createDirectory(at: folderPath)
            SupportLogger.shared.log("createDirectory \(folderPath)", category: cloudCategory)
            await loadDirectory()
        } catch {
            SupportLogger.shared.log("createDirectory FAILED \(folderPath) — \(error.localizedDescription)", category: cloudCategory, level: .error)
            self.error = "Failed to create folder: \(error.localizedDescription)"
        }
    }

    func renameItem(_ item: CloudFileItem, to newName: String) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return }
        do {
            try await provider.renameItem(at: item.path, to: newName)
            SupportLogger.shared.log("renameItem \(item.path) → \(newName)", category: cloudCategory)
            await loadDirectory()
        } catch {
            SupportLogger.shared.log("renameItem FAILED \(item.path) → \(newName) — \(error.localizedDescription)", category: cloudCategory, level: .error)
            self.error = "Failed to rename: \(error.localizedDescription)"
        }
    }

    private var cloudCategory: String {
        let short = accountId.uuidString.prefix(8)
        if let providerType {
            return "cloud[\(providerType.rawValue):\(short)]"
        }
        return "cloud[\(short)]"
    }

    // MARK: - Delete

    func deleteSelectedItems() async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return }

        for item in selectedItems {
            do {
                try await provider.deleteItem(at: item.path)
                SupportLogger.shared.log("deleteItem \(item.path)", category: cloudCategory)
            } catch {
                SupportLogger.shared.log("deleteItem FAILED \(item.path) — \(error.localizedDescription)", category: cloudCategory, level: .error)
                self.error = "Failed to delete \(item.name): \(error.localizedDescription)"
                return
            }
        }
        selectedItemIDs.removeAll()
        await loadDirectory()
    }

    func deleteItems(_ items: [CloudFileItem]) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return }

        for item in items {
            do {
                try await provider.deleteItem(at: item.path)
                SupportLogger.shared.log("deleteItem \(item.path)", category: cloudCategory)
            } catch {
                SupportLogger.shared.log("deleteItem FAILED \(item.path) — \(error.localizedDescription)", category: cloudCategory, level: .error)
                self.error = "Failed to delete \(item.name): \(error.localizedDescription)"
                return
            }
        }
        await loadDirectory()
    }

    // MARK: - Pre-flight Conflict Resolution (for cloud-to-cloud)

    enum PreFlightResult: Equatable {
        case transfer          // download + upload normally
        case replace           // delete existing on target, then transfer
        case keepBoth          // upload with a unique name
        case skip              // don't transfer this item
    }

    /// Check source items against this VM's current items and resolve conflicts
    /// before any downloads happen. Returns a resolution for each source item.
    func preFlightConflictCheck(sourceItems: [CloudFileItem], against existingItems: [CloudFileItem]? = nil) async -> [(CloudFileItem, PreFlightResult)] {
        let target = existingItems ?? items
        let existingByName = Dictionary(target.map { ($0.name, $0) }, uniquingKeysWith: { first, _ in first })
        var results: [(CloudFileItem, PreFlightResult)] = []
        var applyToAllChoice: ConflictChoice?

        for (index, item) in sourceItems.enumerated() {
            guard let existing = existingByName[item.name] else {
                results.append((item, .transfer))
                continue
            }

            let choice: ConflictChoice
            if let saved = applyToAllChoice {
                choice = saved
            } else {
                let remaining = sourceItems[(index + 1)...].filter { existingByName[$0.name] != nil }.count
                let resolution = await withCheckedContinuation { continuation in
                    self.conflictContinuation = continuation
                    self.pendingConflict = PendingConflict(
                        source: ConflictFileInfo(name: item.name, date: item.modificationDate, size: item.size, fileExtension: (item.name as NSString).pathExtension, localURL: nil),
                        destination: ConflictFileInfo(name: existing.name, date: existing.modificationDate, size: existing.size, fileExtension: (existing.name as NSString).pathExtension, localURL: nil),
                        remainingConflicts: remaining,
                        direction: self.conflictDirection
                    )
                }
                choice = resolution.choice
                if resolution.applyToAll { applyToAllChoice = choice }
            }
            switch choice {
            case .skip: results.append((item, .skip))
            case .stop:
                // Mark remaining items as skip
                for remaining in sourceItems[index...] {
                    results.append((remaining, .skip))
                }
                return results
            case .keepBoth: results.append((item, .keepBoth))
            case .replace: results.append((item, .replace))
            }
        }
        return results
    }

    // MARK: - Download

    func downloadItems(_ items: [CloudFileItem], to localDirectory: URL, progress: TransferProgress? = nil, skipConflictCheck: Bool = false) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else {
            SupportLogger.shared.log("downloadItems aborted: provider unavailable", category: cloudCategory, level: .error)
            return
        }
        SupportLogger.shared.log("downloadItems start: \(items.count) item(s) → \(localDirectory.path)", category: cloudCategory)

        progress?.transferredFileNames = items.map(\.name)
        progress?.transferredFolderNames = Set(items.filter(\.isDirectory).map(\.name))
        progress?.isCloudDownload = true

        // Pre-compute expected bytes for byte-weighted progress.
        // For cloud-to-cloud transfers, expectedBytesDownload is already set by the caller.
        if progress?.isCloudToCloud != true {
            var expected: Int64 = 0
            for item in items {
                if item.isDirectory {
                    expected += (try? await provider.folderSize(at: item.path)) ?? 0
                } else {
                    expected += item.size
                }
            }
            progress?.expectedBytesSingle = expected
        }

        let convertedExtensions = ["docx", "xlsx", "pptx", "pdf"]
        var applyToAllChoice: ConflictChoice?
        var downloadedCount = 0

        for (index, item) in items.enumerated() {
            if progress?.isCancelled == true || Task.isCancelled {
                if !(progress?.isCloudToCloud ?? false) {
                    progress?.endTime = Date(); progress?.isComplete = true
                }
                return
            }
            // Server-supplied name: sanitise and confine before writing. A
            // name that escapes the target directory is refused rather than
            // downloaded; the rest of the batch continues.
            guard var base = try? SafeLocalPath.destination(for: item.name, in: localDirectory) else {
                let msg = "Unsafe file name from the server — download refused."
                self.error = msg
                progress?.recordFailure(item.name, error: msg)
                continue
            }
            let existingURL = Self.findExistingFile(base: base, convertedExtensions: convertedExtensions)
            let exists = existingURL != nil

            if exists && !skipConflictCheck {
                let choice: ConflictChoice
                if let saved = applyToAllChoice {
                    choice = saved
                } else {
                    let remaining = items[(index + 1)...].filter { nextItem in
                        let nextBase = localDirectory.appendingPathComponent(nextItem.name)
                        return Self.findExistingFile(base: nextBase, convertedExtensions: convertedExtensions) != nil
                    }.count
                    let destInfo = FileManagerViewModel.localFileInfo(at: existingURL!)
                    let resolution = await withCheckedContinuation { continuation in
                        self.conflictContinuation = continuation
                        self.pendingConflict = PendingConflict(
                            source: ConflictFileInfo(name: item.name, date: item.modificationDate, size: item.size, fileExtension: (item.name as NSString).pathExtension, localURL: nil),
                            destination: destInfo,
                            remainingConflicts: remaining,
                            direction: self.conflictDirection
                        )
                    }
                    choice = resolution.choice
                    if resolution.applyToAll { applyToAllChoice = choice }
                }
                switch choice {
                case .skip:
                    progress?.recordSkip(item.name)
                    progress?.completedItems = index + 1
                    continue
                case .stop:
                    if !(progress?.isCloudToCloud ?? false) { progress?.endTime = Date(); progress?.isComplete = true }
                    return
                case .keepBoth:
                    base = FileManagerViewModel.uniqueDestination(for: base)
                case .replace: break
                }
            }

            do {
                if exists {
                    if !skipConflictCheck, let existing = existingURL {
                        try FileManager.default.removeItem(at: existing)
                    }
                    for ext in convertedExtensions {
                        let converted = base.appendingPathExtension(ext)
                        if FileManager.default.fileExists(atPath: converted.path) { try FileManager.default.removeItem(at: converted) }
                    }
                }
                try await downloadRecursively(item: item, to: localDirectory, provider: provider, progress: progress, downloadedCount: &downloadedCount)
                progress?.completedItems = index + 1
                // In a cloud-to-cloud transfer the upload phase is the
                // canonical place to record per-item success — recording
                // here too would double-write to `itemResults` (the
                // "Items (2)" duplicate-row artefact users saw on
                // drag-and-drop between two cloud accounts).
                if progress?.isCloudToCloud != true {
                    progress?.recordSuccess(item.name)
                }
            } catch {
                self.error = "Failed to download \(item.name): \(error.localizedDescription)"
                // Failures must always be recorded so cloud-to-cloud
                // download errors aren't silently dropped — the upload
                // phase won't see those items.
                progress?.recordFailure(item.name, error: error.localizedDescription)
                // Continue with remaining items so one failure doesn't
                // abort the whole batch.
            }
        }

        if !(progress?.isCloudToCloud ?? false) {
            progress?.endTime = Date()
            progress?.isComplete = true
        }
    }

    private func downloadRecursively(item: CloudFileItem, to localDirectory: URL, provider: any CloudProvider, progress: TransferProgress?, downloadedCount: inout Int) async throws {
        if item.isDirectory {
            let folderURL = try SafeLocalPath.destination(for: item.name, in: localDirectory)
            try FileManager.default.createDirectory(at: folderURL, withIntermediateDirectories: true)
            let contents = try await provider.listDirectory(at: item.path)
            for child in contents {
                try await downloadRecursively(item: child, to: folderURL, provider: provider, progress: progress, downloadedCount: &downloadedCount)
            }
        } else {
            let localURL = try SafeLocalPath.destination(for: item.name, in: localDirectory)
            progress?.currentFileName = item.name
            let progressRef = progress
            try await provider.downloadFile(remotePath: item.path, to: localURL, onBytes: { bytes in
                Task { @MainActor in progressRef?.addDownloadBytes(bytes) }
            })
            // Preserve original cloud modification date on the local file
            // so conflict dialogs and file listings show the correct date
            let finalURL: URL
            // Google Workspace files may get an extension appended
            let convertedExts = ["docx", "xlsx", "pptx", "pdf"]
            if let converted = convertedExts.lazy.map({ localURL.appendingPathExtension($0) }).first(where: { FileManager.default.fileExists(atPath: $0.path) }) {
                finalURL = converted
            } else {
                finalURL = localURL
            }
            try? FileManager.default.setAttributes(
                [.modificationDate: item.modificationDate],
                ofItemAtPath: finalURL.path
            )
            progress?.totalBytes += item.size
            downloadedCount += 1
            progress?.totalFiles = downloadedCount
            progress?.completedItems = downloadedCount
        }
    }

    // MARK: - Share links

    /// UserDefaults key recording that this account's provider refuses to
    /// serve its own direct-download URLs.
    private var directLinkBlockedKey: String { "shareLinkDirectBlocked.\(accountId.uuidString)" }

    /// Confirms the provider's direct-download URL actually serves the file
    /// to someone without an account, and drops back to the share page when
    /// it doesn't.
    ///
    /// Box hands back a `download_url` on every plan but refuses to serve it
    /// on plans without the direct-links feature ("This user is not allowed
    /// to use direct links"), so copying it unchecked would hand the user a
    /// link that fails for the recipient. The verdict is remembered per
    /// account, so this costs one extra request once, not on every share.
    /// Confirms the link works for a stranger. Where the provider offers a
    /// direct-download URL this checks that; otherwise it checks the share
    /// page itself, because a link that lands on a sign-in page is the one
    /// failure the user must not discover from the recipient.
    private func verifiedShareLink(_ link: CloudShareLink) async -> CloudShareLink {
        guard link.directDownloadURL == nil || link.directDownloadURL == link.url else {
            return await verifiedDirectLink(link)
        }
        let result = await PublicURLCheck.isReachable(link.url)
        if result.ok {
            // A public link proves the account can make them after all.
            if linksAreNotPublic { linksAreNotPublic = false }
            return link
        }
        linksAreNotPublic = true
        // Deliberately not logging `result.detail`: it carries the final
        // URL, and for S3-style accounts that is a presigned URL — a
        // working download credential. It reaches the user in the banner
        // instead, which is where it is useful and where it doesn't end up
        // in a sysdiagnose.
        cloudFileVMLog.info("[Share] Link is not publicly reachable for account \(self.accountId, privacy: .public)")
        return CloudShareLink(
            url: link.url,
            directDownloadURL: link.directDownloadURL,
            expiresAt: link.expiresAt,
            hasPassword: link.hasPassword,
            isPublic: false,
            note: [link.note, L10n.format("Checked just now: %@", result.detail ?? L10n.text("the link is not reachable without signing in."))]
                .compactMap { $0 }
                .joined(separator: " ")
        )
    }

    private func verifiedDirectLink(_ link: CloudShareLink) async -> CloudShareLink {
        guard let direct = link.directDownloadURL, direct != link.url else { return link }

        if UserDefaults.standard.bool(forKey: directLinkBlockedKey) {
            return withoutDirectDownload(link, note: L10n.text("This account can't serve direct download links, so the share page link was copied instead."))
        }

        let result = await PublicURLCheck.isReachable(direct)
        guard result.ok else {
            UserDefaults.standard.set(true, forKey: directLinkBlockedKey)
            cloudFileVMLog.info("[Share] Direct link rejected for account \(self.accountId, privacy: .public) — falling back to the share page")
            return withoutDirectDownload(link, note: L10n.text("This account can't serve direct download links, so the share page link was copied instead."))
        }
        return link
    }

    private func withoutDirectDownload(_ link: CloudShareLink, note: String) -> CloudShareLink {
        CloudShareLink(
            url: link.url,
            directDownloadURL: nil,
            expiresAt: link.expiresAt,
            hasPassword: link.hasPassword,
            note: [link.note, note].compactMap { $0 }.joined(separator: " ")
        )
    }

    /// Creates a public link for `item` and puts it on the clipboard.
    /// Returns the link on success, nil on failure — in which case
    /// `error` carries the server's own message, which is what the user
    /// needs: the usual reasons are an admin disabling link sharing or a
    /// password/expiry needing a paid plan.
    func createShareLink(
        for item: CloudFileItem,
        options: ShareLinkOptions,
        preferDirectDownload: Bool,
        reuseExisting: Bool = true
    ) async -> CloudShareLink? {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else {
            error = L10n.text("Cloud account not connected")
            return nil
        }

        do {
            // A file that is already shared should hand back its existing
            // link rather than failing — that is what the user asked for.
            // `try?` flattens both "not shared" and a failed lookup to nil,
            // and either way creating is the right next step.
            var link: CloudShareLink
            if reuseExisting,
               provider.shareLinkCapabilities.canQueryExisting,
               let existing = try? await provider.existingShareLink(at: item.path) {
                link = existing
            } else {
                // Applying settings the user just chose must never hand back
                // an old link that ignores them.
                link = try await provider.createShareLink(at: item.path, options: options)
            }
            // Only remember a link for providers that can read one back.
            // S3-style accounts have no stored link at all — every share is
            // a freshly signed URL — so recording one would make the menu
            // offer "copy the existing link" for something that can't be
            // looked up.
            if provider.shareLinkCapabilities.canQueryExisting {
                shareStates[item.path] = link
            }
            noteShareOptionsAccepted(options)
            link = await verifiedShareLink(link)
            copyToPasteboard(link, preferDirectDownload: preferDirectDownload)
            return link
        } catch CloudProviderError.notAuthenticated {
            // Only a genuine 401 means the session is over. A provider that
            // refuses the *action* (403) must not send the user off to sign
            // in again while browsing and downloading plainly still work —
            // the Dropbox missing-scope case maps itself to this deliberately.
            needsReAuth = true
            shareError = CloudProviderError.unauthorized.localizedDescription
            return nil
        } catch {
            noteShareOptionsRejected(options, error: error)
            shareError = L10n.format("Couldn't create the share link: %@", shareFailureMessage(error, options: options))
            return nil
        }
    }

    /// Applies the settings chosen in the sheet. Whether that means
    /// creating or changing is decided by asking the provider *now*, not by
    /// the cached state: the cache can say "shared" for a link that was
    /// since removed elsewhere, or for a provider that can't read its own
    /// links back, and trying to change a link that isn't there fails with
    /// a message about a limitation the user never hit.
    func applyShareOptions(
        for item: CloudFileItem,
        options: ShareLinkOptions,
        preferDirectDownload: Bool
    ) async -> CloudShareLink? {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else {
            shareError = L10n.text("Cloud account not connected")
            return nil
        }
        let capabilities = provider.shareLinkCapabilities

        var alreadyShared = false
        if capabilities.canQueryExisting {
            if let existing = try? await provider.existingShareLink(at: item.path) {
                shareStates[item.path] = existing
                alreadyShared = true
            } else {
                shareStates[item.path] = CloudShareLink?.none
            }
        }

        if alreadyShared && capabilities.canUpdate {
            return await updateShareLink(for: item, options: options, preferDirectDownload: preferDirectDownload)
        }
        return await createShareLink(
            for: item,
            options: options,
            preferDirectDownload: preferDirectDownload,
            reuseExisting: false
        )
    }

    /// Marks the panel as needing a sign-in and tells the rest of the app,
    /// so the sidebar's connection dot stops claiming everything is fine
    /// while this panel says otherwise (issue #46).
    private func flagNeedsReAuthentication() {
        needsReAuth = true
        NotificationCenter.default.post(
            name: .cloudAccountNeedsReAuthentication,
            object: nil,
            userInfo: ["accountId": accountId]
        )
    }

    /// Re-copies the link a file already has.
    func copyExistingShareLink(for item: CloudFileItem, preferDirectDownload: Bool) async -> CloudShareLink? {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return nil }
        // A provider that can't read its links back can still make one, and
        // that is what the user asked for.
        guard provider.shareLinkCapabilities.canQueryExisting else {
            return await createShareLink(
                for: item,
                options: .default,
                preferDirectDownload: preferDirectDownload
            )
        }
        do {
            guard let link = try await provider.existingShareLink(at: item.path) else {
                // Someone removed it elsewhere in the meantime.
                shareStates[item.path] = CloudShareLink?.none
                shareError = L10n.text("This file is no longer shared.")
                return nil
            }
            shareStates[item.path] = link
            let checked = await verifiedShareLink(link)
            copyToPasteboard(checked, preferDirectDownload: preferDirectDownload)
            return checked
        } catch {
            shareError = L10n.format("Couldn't read this file's share link: %@", error.localizedDescription)
            return nil
        }
    }

    /// Applies new settings (password, expiry, download permission) to an
    /// existing link.
    func updateShareLink(
        for item: CloudFileItem,
        options: ShareLinkOptions,
        preferDirectDownload: Bool
    ) async -> CloudShareLink? {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return nil }
        do {
            var link = try await provider.updateShareLink(at: item.path, options: options)
            shareStates[item.path] = link
            noteShareOptionsAccepted(options)
            link = await verifiedShareLink(link)
            copyToPasteboard(link, preferDirectDownload: preferDirectDownload)
            return link
        } catch {
            noteShareOptionsRejected(options, error: error)
            shareError = L10n.format("Couldn't change sharing: %@", shareFailureMessage(error, options: options))
            return nil
        }
    }

    /// Records that an option this account refused before now works — plans
    /// get upgraded, and the sheet shouldn't keep hiding it.
    private func noteShareOptionsAccepted(_ options: ShareLinkOptions) {
        if options.password?.isEmpty == false, rejectedPassword { rejectedPassword = false }
        if options.expiry != nil, rejectedExpiry { rejectedExpiry = false }
    }

    /// Remembers an option the provider refused, but only for refusals that
    /// look like a plan or policy limit — a typo'd password or a network
    /// blip must not permanently hide the control.
    private func noteShareOptionsRejected(_ options: ShareLinkOptions, error: Error) {
        let text = error.localizedDescription.lowercased()
        let planRefusal = ["forbidden", "not_authorized", "not authorized", "settings_error",
                           "403", "payment", "upgrade", "premium", "plan", "subscription"]
        guard planRefusal.contains(where: text.contains) else { return }
        if options.password?.isEmpty == false { rejectedPassword = true }
        if options.expiry != nil { rejectedExpiry = true }
    }

    /// Adds the missing "why" to a bare refusal like Box's "Forbidden".
    private func shareFailureMessage(_ error: Error, options: ShareLinkOptions) -> String {
        let base = error.localizedDescription
        let usedPassword = options.password?.isEmpty == false
        let usedExpiry = options.expiry != nil
        guard usedPassword || usedExpiry else { return base }
        let text = base.lowercased()
        let planRefusal = ["forbidden", "not_authorized", "not authorized", "settings_error",
                           "403", "payment", "upgrade", "premium", "plan", "subscription"]
        guard planRefusal.contains(where: text.contains) else { return base }
        let what: String
        switch (usedPassword, usedExpiry) {
        case (true, true): what = L10n.text("a password and an expiry date")
        case (true, false): what = L10n.text("a password")
        default: what = L10n.text("an expiry date")
        }
        return base + "\n\n" + L10n.format(
            "This account refused %@ for the link. Most providers only offer these on paid plans. Sharing without them will still work, and these options are now hidden for this account.",
            what
        )
    }

    /// Withdraws the public link. Returns true on success.
    @discardableResult
    func stopSharing(_ item: CloudFileItem) async -> Bool {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return false }
        do {
            try await provider.removeShareLink(at: item.path)
            shareStates[item.path] = CloudShareLink?.none
            return true
        } catch {
            shareError = L10n.format("Couldn't stop sharing: %@", error.localizedDescription)
            return false
        }
    }

    /// Looks up whether `item` is shared, so the context menu can offer the
    /// right actions. Cheap to call repeatedly: a path already known is not
    /// asked about again.
    func refreshShareState(for item: CloudFileItem) async {
        guard !shareStates.keys.contains(item.path) else { return }
        guard let provider = await SyncEngine.shared.provider(for: accountId),
              provider.shareLinkCapabilities.canQueryExisting else { return }
        // `try?` flattens a lookup failure and "not shared" into the same
        // nil, and both mean the menu should offer to create a link.
        let link = try? await provider.existingShareLink(at: item.path)
        shareStates[item.path] = link
    }

    private func copyToPasteboard(_ link: CloudShareLink, preferDirectDownload: Bool) {
        let url = preferDirectDownload ? link.preferredURL : link.url
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(url.absoluteString, forType: .string)
    }

    func downloadToTemp(_ item: CloudFileItem) async -> URL? {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else {
            cloudFileVMLog.debug("QuickLook: no provider for account \(self.accountId, privacy: .public)")
            return nil
        }

        // Use the parent path as subdirectory to avoid collisions between
        // files with the same name in different folders
        let parentPath = (item.path as NSString).deletingLastPathComponent
        let safeParent = parentPath
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: ":", with: "_")
        let cacheDir = tempDownloadDir.appendingPathComponent(safeParent, isDirectory: true)
        try? FileManager.default.createDirectory(at: cacheDir, withIntermediateDirectories: true)
        // Quick Look / Open staging: same untrusted name, same guard.
        guard let localURL = try? SafeLocalPath.destination(for: item.name, in: cacheDir) else { return nil }

        // Use cached version if size AND modification date match — size alone
        // isn't enough, since a re-uploaded file with identical bytes would
        // return stale-decrypted content from a prior build.
        if FileManager.default.fileExists(atPath: localURL.path) {
            let attrs = try? FileManager.default.attributesOfItem(atPath: localURL.path)
            let cachedSize = attrs?[.size] as? Int64 ?? -1
            let cachedModDate = attrs?[.modificationDate] as? Date
            let sizeOK = item.size == 0 || cachedSize == item.size
            let dateOK = cachedModDate.map { abs($0.timeIntervalSince(item.modificationDate)) < 1.0 } ?? false
            if sizeOK && dateOK {
                return localURL
            }
            try? FileManager.default.removeItem(at: localURL)
        }
        for ext in ["docx", "xlsx", "pptx", "pdf"] {
            let converted = localURL.appendingPathExtension(ext)
            if FileManager.default.fileExists(atPath: converted.path) {
                return converted
            }
        }

        do {
            try await provider.downloadFile(remotePath: item.path, to: localURL)
            // Google Workspace files get written with an appended extension
            for ext in ["docx", "xlsx", "pptx", "pdf"] {
                let converted = localURL.appendingPathExtension(ext)
                if FileManager.default.fileExists(atPath: converted.path) {
                    try? FileManager.default.setAttributes(
                        [.modificationDate: item.modificationDate],
                        ofItemAtPath: converted.path
                    )
                    return converted
                }
            }
            try? FileManager.default.setAttributes(
                [.modificationDate: item.modificationDate],
                ofItemAtPath: localURL.path
            )
            return localURL
        } catch {
            cloudFileVMLog.error("QuickLook download failed for \(item.path, privacy: .public): \(error.localizedDescription, privacy: .public)")
            return nil
        }
    }

    // MARK: - Upload

    func uploadFiles(from urls: [URL], toPath: String? = nil, progress: TransferProgress? = nil, skipConflictCheck: Bool = false) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else {
            SupportLogger.shared.log("uploadFiles aborted: provider unavailable", category: cloudCategory, level: .error)
            self.error = "Cloud account not connected"
            progress?.isComplete = true
            return
        }
        SupportLogger.shared.log("uploadFiles start: \(urls.count) item(s) → \(toPath ?? currentPath)", category: cloudCategory)

        let remoteBase = toPath ?? currentPath
        let existingItems: [CloudFileItem]
        if let toPath, toPath != currentPath {
            existingItems = (try? await provider.listDirectory(at: toPath)) ?? []
        } else {
            existingItems = items
        }
        let existingByName = Dictionary(existingItems.map { ($0.name, $0) }, uniquingKeysWith: { first, _ in first })

        progress?.transferredFileNames = urls.map(\.lastPathComponent)
        let fm = FileManager.default
        progress?.transferredFolderNames = Set(urls.filter { var isDir: ObjCBool = false; return fm.fileExists(atPath: $0.path, isDirectory: &isDir) && isDir.boolValue }.map(\.lastPathComponent))
        progress?.isCloudUpload = true

        // Pre-compute expected upload bytes for byte-weighted progress.
        if progress?.isCloudToCloud != true {
            var expected: Int64 = 0
            for url in urls {
                expected += Self.localSizeRecursively(url: url)
            }
            progress?.expectedBytesSingle = expected
        }

        var applyToAllChoice: ConflictChoice?
        var uploadedCount = 0

        for (index, url) in urls.enumerated() {
            if progress?.isCancelled == true || Task.isCancelled {
                if !(progress?.isCloudToCloud ?? false) {
                    progress?.endTime = Date(); progress?.isComplete = true
                }
                return
            }
            var uploadURL = url
            let name = url.lastPathComponent
            if let existing = existingByName[name], !skipConflictCheck {
                let choice: ConflictChoice
                if let saved = applyToAllChoice {
                    choice = saved
                } else {
                    let remaining = urls[(index + 1)...].filter { existingByName[$0.lastPathComponent] != nil }.count
                    let sourceInfo = FileManagerViewModel.localFileInfo(at: url)
                    let resolution = await withCheckedContinuation { continuation in
                        self.conflictContinuation = continuation
                        self.pendingConflict = PendingConflict(
                            source: sourceInfo,
                            destination: ConflictFileInfo(name: existing.name, date: existing.modificationDate, size: existing.size, fileExtension: (existing.name as NSString).pathExtension, localURL: nil),
                            remainingConflicts: remaining,
                            direction: self.conflictDirection
                        )
                    }
                    choice = resolution.choice
                    if resolution.applyToAll { applyToAllChoice = choice }
                }
                switch choice {
                case .skip:
                    progress?.recordSkip(name)
                    continue
                case .stop:
                    if !(progress?.isCloudToCloud ?? false) { progress?.endTime = Date(); progress?.isComplete = true }
                    await loadDirectory(); return
                case .keepBoth:
                    let uniqueName = Self.uniqueCloudName(for: name, existing: Set(existingByName.keys))
                    let tempCopy = StagingLocation.base().appendingPathComponent(uniqueName)
                    try? FileManager.default.removeItem(at: tempCopy)
                    do {
                        try FileManager.default.copyItem(at: url, to: tempCopy)
                    } catch {
                        // `break` here would only leave the switch and then
                        // upload under the ORIGINAL name — overwriting the
                        // very file the user asked to keep. Skip the item
                        // and say so instead.
                        let msg = "Couldn't stage a copy for \"Keep Both\": \(error.localizedDescription)"
                        self.error = msg
                        progress?.recordFailure(name, error: msg)
                        continue
                    }
                    uploadURL = tempCopy
                case .replace:
                    do { try await provider.deleteItem(at: existing.path) } catch {
                        let msg = "Failed to overwrite: \(error.localizedDescription)"
                        self.error = "Failed to overwrite \(name): \(error.localizedDescription)"
                        progress?.recordFailure(name, error: msg)
                        continue
                    }
                }
            }

            do {
                try await uploadRecursively(urls: [uploadURL], toRemotePath: remoteBase, provider: provider, progress: progress, uploadedCount: &uploadedCount)
                if uploadURL != url { try? FileManager.default.removeItem(at: uploadURL) }
                progress?.recordSuccess(name)
            } catch {
                self.error = error.localizedDescription
                progress?.recordFailure(name, error: error.localizedDescription)
                // Continue with the remaining items so a single bad file
                // (e.g. a >4 GiB upload that the provider rejects) doesn't
                // cancel the rest of the batch.
            }
        }

        if !(progress?.isCloudToCloud ?? false) {
            progress?.endTime = Date()
            progress?.isComplete = true
        }
        await loadDirectory()
    }

    private func uploadRecursively(urls: [URL], toRemotePath remotePath: String, provider: CloudProvider, progress: TransferProgress?, uploadedCount: inout Int) async throws {
        let fm = FileManager.default
        for url in urls {
            var isDir: ObjCBool = false
            guard fm.fileExists(atPath: url.path, isDirectory: &isDir) else {
                cloudFileVMLog.debug("Upload: file not found, skipping \(url.path, privacy: .public)")
                continue
            }

            let itemRemotePath = remotePath == "/" ? "/\(url.lastPathComponent)" : "\(remotePath)/\(url.lastPathComponent)"

            if isDir.boolValue {
                cloudFileVMLog.debug("Upload: creating directory \(itemRemotePath, privacy: .public)")
                do {
                    try await provider.createDirectory(at: itemRemotePath)
                } catch let error as CloudProviderError {
                    switch error {
                    case .notAuthenticated, .unauthorized:
                        throw error // Auth errors must propagate
                    default:
                        // Folder may already exist — continue uploading contents
                        cloudFileVMLog.debug("Upload: create directory failed (may already exist) \(itemRemotePath, privacy: .public): \(error.localizedDescription, privacy: .public)")
                    }
                }
                let contents = try fm.contentsOfDirectory(at: url, includingPropertiesForKeys: [.fileSizeKey])
                cloudFileVMLog.debug("Upload: directory \(url.lastPathComponent, privacy: .public) has \(contents.count) items")
                try await uploadRecursively(urls: contents, toRemotePath: itemRemotePath, provider: provider, progress: progress, uploadedCount: &uploadedCount)
            } else {
                progress?.currentFileName = url.lastPathComponent
                let fileSize = (try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0
                let fileBytes = Int64(fileSize)

                // Pre-flight: reject oversized files locally before sending
                // any bytes. Saves the user from a multi-gigabyte upload that
                // the server would refuse anyway.
                try await CloudProviderError.enforceUploadSizeLimit(url, provider: provider)

                cloudFileVMLog.debug("Upload: \(url.lastPathComponent, privacy: .public) (\(fileSize) bytes) → \(itemRemotePath, privacy: .public)")
                let progressRef = progress
                try await provider.uploadFile(from: url, to: itemRemotePath, onBytes: { bytes in
                    Task { @MainActor in progressRef?.addUploadBytes(bytes) }
                })
                cloudFileVMLog.debug("Upload: success \(url.lastPathComponent, privacy: .public)")
                // Preserve the source file's modification date on the
                // newly-uploaded remote so cross-source transfers behave
                // like Finder — only content changes update "Date Modified".
                // For cloud→local→cloud transfers the local mtime was set
                // to the original source mtime during download (see
                // downloadRecursively), so this carries it through.
                if let mtime = (try? url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate {
                    try? await provider.setModificationDate(at: itemRemotePath, to: mtime)
                }
                progress?.totalBytes += fileBytes
                uploadedCount += 1
                progress?.totalFiles = uploadedCount
                progress?.completedItems = uploadedCount
            }
        }
    }

    // MARK: - Intra-cloud transfer (drag/drop onto subfolder of same account)

    /// Move or copy items from the current folder into another folder of the same cloud account.
    /// Tries a server-side move/copy first (no bytes touch the user's machine);
    /// falls back to download+upload+delete when the provider doesn't support
    /// it. Pre-flight conflict resolution runs in both cases.
    func transferItemsToSubfolder(
        _ sourceItems: [CloudFileItem],
        targetPath: String,
        deleteFromSource: Bool,
        progress: TransferProgress?
    ) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else {
            SupportLogger.shared.log("transferItemsToSubfolder aborted: provider unavailable", category: cloudCategory, level: .error)
            progress?.endTime = Date(); progress?.isComplete = true; return
        }
        SupportLogger.shared.log("transferItemsToSubfolder \(sourceItems.count) item(s) → \(targetPath) (deleteSource=\(deleteFromSource))", category: cloudCategory)

        let targetSiblings = (try? await provider.listDirectory(at: targetPath)) ?? []
        let resolutions = await preFlightConflictCheck(sourceItems: sourceItems, against: targetSiblings)
        let itemsToTransfer = resolutions.filter { $0.1 != .skip }.map(\.0)
        for (item, result) in resolutions where result == .skip {
            progress?.recordSkip(item.name)
        }
        guard !itemsToTransfer.isEmpty else {
            progress?.endTime = Date(); progress?.isComplete = true; return
        }
        let resolutionByName = Dictionary(resolutions.map { ($0.0.name, $0.1) }, uniquingKeysWith: { _, last in last })

        // Fast path: ask the provider to do the move/copy server-side. This
        // is what the user actually wants for an intra-cloud relocation —
        // nothing leaves the cloud. If the provider returns .notImplemented
        // we fall through to the legacy download+upload path below.
        if await tryServerSideTransfer(
            items: itemsToTransfer,
            targetPath: targetPath,
            targetSiblings: targetSiblings,
            resolutionByName: resolutionByName,
            deleteFromSource: deleteFromSource,
            provider: provider,
            progress: progress
        ) {
            await loadDirectory()
            progress?.endTime = Date()
            progress?.isComplete = true
            return
        }

        var expectedBytes: Int64 = 0
        for item in itemsToTransfer {
            if item.isDirectory {
                expectedBytes += (try? await provider.folderSize(at: item.path)) ?? 0
            } else {
                expectedBytes += item.size
            }
        }

        let tempDir = StagingLocation.stagingDirectory(prefix: "intra-cloud")
        defer { try? FileManager.default.removeItem(at: tempDir) }

        if let progress {
            progress.isCloudToCloud = true
            progress.currentPhase = .downloading
            progress.downloadStartTime = Date()
            progress.expectedBytesDownload = expectedBytes
            progress.expectedBytesUpload = expectedBytes
        }

        await downloadItems(itemsToTransfer, to: tempDir, progress: progress, skipConflictCheck: true)
        progress?.downloadEndTime = Date()

        // Delete items flagged as replace on the target
        for item in itemsToTransfer {
            if resolutionByName[item.name] == .replace,
               let existing = targetSiblings.first(where: { $0.name == item.name }) {
                try? await provider.deleteItem(at: existing.path)
            }
        }

        let existingNames = Set(targetSiblings.map(\.name))
        // Names of the items that actually reached the staging directory.
        // Anything missing here failed to download, and must not be deleted
        // from the source afterwards.
        var downloadedNames: Set<String> = []
        let localURLs: [URL] = itemsToTransfer.compactMap { item -> URL? in
            guard let expected = try? SafeLocalPath.destination(for: item.name, in: tempDir) else { return nil }
            var localURL: URL?
            if FileManager.default.fileExists(atPath: expected.path) {
                localURL = expected
            } else {
                for ext in ["docx", "xlsx", "pptx", "pdf"] {
                    let converted = expected.appendingPathExtension(ext)
                    if FileManager.default.fileExists(atPath: converted.path) {
                        localURL = converted
                        break
                    }
                }
            }
            guard let url = localURL else { return nil }
            downloadedNames.insert(item.name)

            if resolutionByName[item.name] == .keepBoth {
                let uniqueName = Self.uniqueCloudName(for: url.lastPathComponent, existing: existingNames)
                let renamed = tempDir.appendingPathComponent(uniqueName)
                do {
                    try FileManager.default.moveItem(at: url, to: renamed)
                    return renamed
                } catch {
                    return url
                }
            }
            return url
        }

        if let progress {
            progress.currentPhase = .uploading
            progress.completedItems = 0
            progress.currentFileName = ""
            progress.uploadStartTime = Date()
        }

        var uploadedCount = 0
        var uploadFailed = false
        do {
            try await uploadRecursively(urls: localURLs, toRemotePath: targetPath, provider: provider, progress: progress, uploadedCount: &uploadedCount)
        } catch {
            uploadFailed = true
            self.error = error.localizedDescription
            progress?.errorMessage = error.localizedDescription
        }

        progress?.uploadEndTime = Date()
        progress?.totalBytes = (progress?.downloadBytes ?? 0) + (progress?.uploadBytes ?? 0)

        if deleteFromSource {
            // A move may only delete what arrived. `uploadRecursively`
            // throws on the first failure, so a failed upload means the
            // remainder never landed — delete nothing in that case — and
            // items that failed to download aren't in `downloadedNames`.
            if uploadFailed {
                SupportLogger.shared.log(
                    "Move kept the source files: the upload to \(targetPath) failed",
                    category: "transfer",
                    level: .error
                )
            } else {
                let transferred = itemsToTransfer.filter { downloadedNames.contains($0.name) }
                if !transferred.isEmpty {
                    await deleteItems(transferred)
                }
            }
        }

        await loadDirectory()

        progress?.endTime = Date()
        progress?.isComplete = true
    }

    // MARK: - Quick Look

    func toggleQuickLook() {
        // If panel is already visible, close it (instant toggle)
        if let panel = QLPreviewPanel.shared(), panel.isVisible {
            panel.orderOut(nil)
            return
        }

        let fileItems = selectedItems.filter { !$0.isDirectory }
        guard !fileItems.isEmpty else { return }

        quickLookTask?.cancel()
        quickLookTask = Task {
            var urls: [URL] = []
            for item in fileItems {
                guard !Task.isCancelled else { return }
                if let url = await downloadToTemp(item) {
                    urls.append(url)
                }
            }
            guard !Task.isCancelled, !urls.isEmpty else { return }
            quickLookController.urls = urls
            quickLookController.toggle()
        }
    }

    func updateQuickLookSelection() {
        let fileItems = selectedItems.filter { !$0.isDirectory }
        guard !fileItems.isEmpty else {
            quickLookController.updateAndReload(urls: [])
            return
        }

        quickLookTask?.cancel()
        quickLookTask = Task {
            var urls: [URL] = []
            for item in fileItems {
                guard !Task.isCancelled else { return }
                if let url = await downloadToTemp(item) {
                    urls.append(url)
                }
            }
            guard !Task.isCancelled else { return }
            quickLookController.updateAndReload(urls: urls)
        }
    }

    // MARK: - Server-side transfer (fast path for intra-account move/copy)

    /// Try to perform the move/copy entirely on the cloud side. Returns
    /// `true` if it succeeded for at least one item — in which case all
    /// items were attempted and per-item results were recorded on
    /// `progress`. Returns `false` if the provider doesn't support
    /// server-side moves at all (`.notImplemented` on the very first item),
    /// signalling the caller to fall back to download+upload.
    private func tryServerSideTransfer(
        items: [CloudFileItem],
        targetPath: String,
        targetSiblings: [CloudFileItem],
        resolutionByName: [String: PreFlightResult],
        deleteFromSource: Bool,
        provider: any CloudProvider,
        progress: TransferProgress?
    ) async -> Bool {
        // Probe the provider on the first item. If it throws .notImplemented
        // we bail before doing anything visible. Any other error is reported
        // per-item and we keep going (rest of the batch may still succeed).
        progress?.expectedBytesSingle = 0
        progress?.expectedBytesDownload = 0
        progress?.expectedBytesUpload = 0
        progress?.isCloudToCloud = false

        var providerSupports = true
        let existingNames = Set(targetSiblings.map(\.name))

        for (index, item) in items.enumerated() {
            if progress?.isCancelled == true || Task.isCancelled { return providerSupports }
            progress?.currentFileName = item.name

            // Determine the destination path. KeepBoth → unique name, Replace → delete first.
            let resolution = resolutionByName[item.name] ?? .transfer
            var destName = item.name
            if resolution == .keepBoth {
                destName = Self.uniqueCloudName(for: item.name, existing: existingNames)
            } else if resolution == .replace,
                      let existing = targetSiblings.first(where: { $0.name == item.name }) {
                try? await provider.deleteItem(at: existing.path)
            }
            let destPath = targetPath == "/" ? "/\(destName)" : "\(targetPath)/\(destName)"

            do {
                if deleteFromSource {
                    SupportLogger.shared.log("server-side move \(item.path) → \(destPath)", category: cloudCategory)
                    try await provider.moveItem(at: item.path, toPath: destPath)
                } else {
                    SupportLogger.shared.log("server-side copy \(item.path) → \(destPath)", category: cloudCategory)
                    try await provider.copyItem(at: item.path, toPath: destPath)
                }
                progress?.recordSuccess(item.name)
                progress?.completedItems = index + 1
                progress?.totalBytes += item.size
            } catch CloudProviderError.notImplemented {
                if index == 0 {
                    // Provider can't do server-side at all — let caller fall back.
                    SupportLogger.shared.log("server-side move not supported, falling back to download+upload", category: cloudCategory)
                    providerSupports = false
                    return false
                }
                // Mid-batch .notImplemented shouldn't happen in practice, but
                // record it as a failure for that item and keep going.
                progress?.recordFailure(item.name, error: "Server-side move not supported for this item.")
            } catch {
                SupportLogger.shared.log(
                    "server-side \(deleteFromSource ? "move" : "copy") FAILED \(item.path) → \(destPath) — \(error.localizedDescription)",
                    category: cloudCategory,
                    level: .error
                )
                progress?.recordFailure(item.name, error: error.localizedDescription)
            }
        }

        return true
    }

    // MARK: - Helpers

    static func uniqueCloudName(for name: String, existing: Set<String>) -> String {
        let nsName = name as NSString
        let ext = nsName.pathExtension
        let base = nsName.deletingPathExtension
        var counter = 2
        while true {
            let candidate = ext.isEmpty ? "\(base) \(counter)" : "\(base) \(counter).\(ext)"
            if !existing.contains(candidate) { return candidate }
            counter += 1
        }
    }

    /// Recursively sums up the total byte size of a local file or directory.
    static func localSizeRecursively(url: URL) -> Int64 {
        let fm = FileManager.default
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: url.path, isDirectory: &isDir) else { return 0 }
        if isDir.boolValue {
            var total: Int64 = 0
            guard let enumerator = fm.enumerator(at: url, includingPropertiesForKeys: [.isRegularFileKey, .fileSizeKey]) else { return 0 }
            for case let fileURL as URL in enumerator {
                let values = try? fileURL.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey])
                if values?.isRegularFile == true {
                    total += Int64(values?.fileSize ?? 0)
                }
            }
            return total
        } else {
            let values = try? url.resourceValues(forKeys: [.fileSizeKey])
            return Int64(values?.fileSize ?? 0)
        }
    }

    static func findExistingFile(base: URL, convertedExtensions: [String]) -> URL? {
        if FileManager.default.fileExists(atPath: base.path) { return base }
        for ext in convertedExtensions {
            let converted = base.appendingPathExtension(ext)
            if FileManager.default.fileExists(atPath: converted.path) { return converted }
        }
        return nil
    }

    // MARK: - Private

    private func pushToHistory(_ path: String) {
        // No-op when the cursor is already on this path — navigateBack /
        // navigateForward funnel through loadDirectory(at:), which calls
        // pushToHistory after they've already moved the cursor. Without
        // this guard a back-navigation would truncate the forward stack
        // and re-append, breaking go-forward.
        if pathHistoryIndex >= 0,
           pathHistoryIndex < pathHistory.count,
           pathHistory[pathHistoryIndex] == path {
            return
        }
        if pathHistoryIndex < pathHistory.count - 1 {
            pathHistory.removeSubrange((pathHistoryIndex + 1)...)
        }
        pathHistory.append(path)
        pathHistoryIndex = pathHistory.count - 1
    }

    private func sorted(_ items: [CloudFileItem]) -> [CloudFileItem] {
        // `kind` resolves a UTType from the extension and asks for its
        // localized description — a type-database lookup. Inside a
        // comparator that runs ~n·log n times with two accesses each, a
        // 10k-file listing means hundreds of thousands of lookups on the
        // main actor. Resolve it once per item instead, and memoise by
        // extension so a folder of 10k photos does one lookup, not 10k.
        if sortOrder == .kind {
            var kindByExtension: [String: String] = [:]
            let decorated: [(item: CloudFileItem, kind: String)] = items.map { item in
                let ext = (item.name as NSString).pathExtension.lowercased()
                if item.isDirectory { return (item, item.kind) }
                if let cached = kindByExtension[ext] { return (item, cached) }
                let kind = item.kind
                kindByExtension[ext] = kind
                return (item, kind)
            }
            let sorted = decorated.sorted { a, b in
                if a.item.isDirectory != b.item.isDirectory {
                    return a.item.isDirectory
                }
                let result = a.kind.localizedStandardCompare(b.kind) == .orderedAscending
                return sortAscending ? result : !result
            }
            return sorted.map(\.item)
        }

        return items.sorted { a, b in
            if a.isDirectory != b.isDirectory {
                return a.isDirectory
            }
            let result: Bool
            switch sortOrder {
            case .name:
                result = a.name.localizedStandardCompare(b.name) == .orderedAscending
            case .date:
                result = a.modificationDate < b.modificationDate
            case .size:
                result = a.size < b.size
            case .kind:
                result = a.kind.localizedStandardCompare(b.kind) == .orderedAscending
            }
            return sortAscending ? result : !result
        }
    }
}
