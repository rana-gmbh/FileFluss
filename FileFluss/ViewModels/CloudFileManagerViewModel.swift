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

    /// Non-nil while the panel shows the inside of an archive stored on this
    /// account. The archive is downloaded once to the preview cache and
    /// listed from there; `archiveRemotePath` is where it lives on the
    /// account, and the panel's paths inside it are that path plus the
    /// entry's — `/Photos/holiday.zip/beach` — so the breadcrumb and history
    /// read like any other folder.
    var archiveSession: ArchiveBrowseSession?
    var archiveRemotePath: String?
    /// An archive operation that failed, for the panel to show.
    var archiveError: String?
    var items: [CloudFileItem] = [] { didSet { _filteredItemsCache = nil; _listingSummaryCache = nil } }
    /// The tags of the items on screen that have any, by path. Filled in
    /// after the listing, from FileFluss's own record for the account — or,
    /// for iCloud Drive, from the files themselves.
    var tags: [String: [FileTag]] = [:]
    /// Bumped whenever the tags on screen are about to be replaced, so a
    /// read that finishes late can tell it has been overtaken.
    @ObservationIgnored private var tagGeneration = 0
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
    var showHiddenFiles: Bool = UserDefaults.standard.bool(forKey: "showHiddenFiles") { didSet { _filteredItemsCache = nil; _listingSummaryCache = nil } }

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

    /// This view model told the app the sign-in stopped working, so a listing
    /// that succeeds has to say otherwise.
    private var reportedSignInProblem = false

    /// Reads the account's saved sign-in again when it has no provider —
    /// `SyncViewModel.restoreSavedSignIn(accountId:)`, handed in by
    /// `AppState`, which owns both. True when a provider exists afterwards.
    @ObservationIgnored var restoreSavedSignIn: (@MainActor (UUID) async -> Bool)?

    /// A failed action, for an alert. Kept apart from `error`, which replaces
    /// the file list and belongs to listing failures: a rename that clashes
    /// shouldn't hide the folder it happened in.
    var actionError: String?

    private var pathHistory: [String] = ["/"]
    private var pathHistoryIndex: Int = 0
    private var tempDownloadDir: URL
    private var quickLookTask: Task<Void, Never>?

    /// An edited copy that was kept rather than overwritten, so the panel
    /// can tell the user where their work went.
    struct PreservedEdit: Identifiable, Equatable {
        let id = UUID()
        let fileName: String
        let url: URL
    }

    var preservedEdit: PreservedEdit?

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

        if archiveSession != nil {
            if let inner = archiveInnerPath(for: targetPath) {
                // Inside the archive: served from its entry list, with no
                // provider call and nothing to wait for.
                if let path, path != currentPath {
                    currentPath = path
                    pushToHistory(path)
                }
                items = archiveRows(at: inner)
                reloadTags()
                selectedItemIDs.removeAll()
                error = nil
                isLoading = false
                return
            }
            // The navigation left the archive.
            closeArchive()
        }
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
            if !self.tags.isEmpty { self.tags = [:] }
            self.selectedItemIDs.removeAll()
        }
        isLoading = true
        error = nil
        needsReAuth = false

        do {
            var provider = await SyncEngine.shared.provider(for: accountId)
            if provider == nil, let restoreSavedSignIn, await restoreSavedSignIn(accountId) {
                provider = await SyncEngine.shared.provider(for: accountId)
            }
            guard let provider else {
                // Nothing saved that works. Saying "not connected" with only
                // a Try Again button left people stuck: trying again can't
                // conjure up credentials. As a sign-in problem the panel
                // offers the way back for this kind of account instead.
                guard self.currentPath == targetPath else { return }
                error = providerType == .iCloud
                    ? L10n.text("iCloud Drive isn't available. Check that this Mac is signed in to iCloud with iCloud Drive turned on.")
                    : L10n.text("FileFluss couldn't use the saved sign-in for this account. Reconnect it to see your files again.")
                flagNeedsReAuthentication()
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
                await SearchIndex.shared.upsertItemsLogging(loadedItems, accountId: accId, context: "browsing")
            }

            self.items = loadedItems
            self.reloadTags()
            // A refresh keeps the selection, less anything now gone. A
            // navigation has already cleared it.
            self.selectedItemIDs.formIntersection(loadedItems.map(\.id))
            self.isLoading = false
            if reportedSignInProblem {
                reportedSignInProblem = false
                NotificationCenter.default.post(name: .cloudAccountSignInWorks, object: nil, userInfo: ["accountId": accountId])
            }
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

    // MARK: - Tags

    /// iCloud Drive's items are files on this Mac and carry Finder tags;
    /// every other account's tags are kept by FileFluss.
    private var tagsLiveInFiles: Bool { providerType?.keepsTagsInFiles == true }

    /// Looks up the tags of the folder on screen again.
    func reloadTags() {
        tagGeneration += 1
        guard archiveSession == nil else {
            // Rows inside an archive aren't items on the account.
            if !tags.isEmpty { tags = [:] }
            return
        }
        let generation = tagGeneration
        let listed = items
        let account = accountId
        if tagsLiveInFiles {
            let files = listed.map { (key: $0.path, path: ICloudProvider.localURL(forRemotePath: $0.path).path) }
            Task {
                let loaded = await FinderTags.readAll(files)
                guard generation == self.tagGeneration else { return }
                if loaded != self.tags { self.tags = loaded }
            }
        } else {
            let lasting = providerType?.hasLastingItemIDs ?? false
            let references = listed.map { (id: $0.id, path: $0.path) }
            let folder = currentPath
            let standard = SystemTagNames.standard
            Task {
                let record = await TagStore.shared.resolveDetailed(account: account, items: references, stableIDs: lasting)
                var loaded = record.tags
                var toSend: [(path: String, tags: [FileTag])] = []

                // A service with labels of its own is asked for them, and
                // the two sides are brought into step (see NativeTagSync).
                // If it can't be asked, FileFluss's record is shown as it is.
                let provider = await SyncEngine.shared.provider(for: account)
                if let provider, provider.nativeTagSupport != .none {
                    let support = provider.nativeTagSupport
                    do {
                        let native = try await provider.nativeTags(inFolder: folder)
                        for item in listed where item.role == .normal {
                            let stored = loaded[item.path] ?? []
                            let outcome = NativeTagSync.reconcile(
                                stored: stored,
                                native: native[item.path] ?? [],
                                support: support,
                                pending: record.newlySeen.contains(item.path),
                                standard: standard
                            )
                            if outcome.tags != stored {
                                await TagStore.shared.setTags(outcome.tags, account: account, path: item.path, itemID: item.id)
                                loaded[item.path] = outcome.tags.isEmpty ? nil : outcome.tags
                            }
                            if let push = outcome.push {
                                toSend.append((item.path, push))
                            }
                        }
                    } catch {
                        SupportLogger.shared.log("labels of \(folder) could not be read — \(error.localizedDescription)", category: self.cloudCategory, level: .error)
                    }
                }

                if generation == self.tagGeneration, loaded != self.tags {
                    self.tags = loaded
                }

                // What a transfer brought and the service hasn't been told.
                // Sent even if the panel has moved on: next time the item is
                // listed it would no longer count as just written.
                if let provider {
                    for item in toSend {
                        do {
                            try await provider.setNativeTags(item.tags, at: item.path)
                        } catch {
                            SupportLogger.shared.log("labels of \(item.path) could not be written — \(error.localizedDescription)", category: self.cloudCategory, level: .error)
                        }
                    }
                }
            }
        }
    }

    /// Adds or removes a tag on the given items.
    func changeTags(of items: [CloudFileItem], _ change: TagChange) {
        guard archiveSession == nil, !items.isEmpty else { return }
        var shown = tags
        for item in items {
            let updated = change.applied(to: shown[item.path] ?? [])
            shown[item.path] = updated.isEmpty ? nil : updated
        }
        tags = shown
        tagGeneration += 1
        let generation = tagGeneration

        if tagsLiveInFiles {
            let files = items.map { (key: $0.path, url: ICloudProvider.localURL(forRemotePath: $0.path)) }
            Task {
                let outcome = await FinderTags.applyAll(change, to: files)
                if let failure = outcome.failure {
                    self.actionError = L10n.format("The tags of “%@” couldn't be changed: %@", failure.name, failure.message)
                }
                guard generation == self.tagGeneration else { return }
                var actual = self.tags
                for (key, value) in outcome.tags {
                    actual[key] = value.isEmpty ? nil : value
                }
                if actual != self.tags { self.tags = actual }
            }
        } else {
            let lasting = providerType?.hasLastingItemIDs ?? false
            let account = accountId
            let writes = items.map { (path: $0.path, name: $0.name, tags: shown[$0.path] ?? [], id: lasting ? $0.id : nil) }
            Task {
                for write in writes {
                    await TagStore.shared.setTags(write.tags, account: account, path: write.path, itemID: write.id)
                }
                // And on the service itself, where it has labels of its own.
                guard let provider = await SyncEngine.shared.provider(for: account),
                      provider.nativeTagSupport != .none else { return }
                var failed = false
                for write in writes {
                    do {
                        try await provider.setNativeTags(
                            NativeTagSync.nativeForm(write.tags, support: provider.nativeTagSupport),
                            at: write.path
                        )
                    } catch {
                        failed = true
                        self.actionError = L10n.format("The tags of “%@” couldn't be changed: %@", write.name, error.localizedDescription)
                    }
                }
                // What the service has is what counts; show that again.
                if failed { self.reloadTags() }
            }
        }
    }

    /// Tells the tag record that FileFluss moved or renamed an item, so its
    /// tags — and those of everything inside it — go with it.
    private func tagsFollowMove(from oldPath: String, to newPath: String) async {
        guard !tagsLiveInFiles else { return }
        await TagStore.shared.moveSubtree(account: accountId, from: oldPath, to: newPath)
    }

    private func tagsFollowCopy(from oldPath: String, to newPath: String) async {
        guard !tagsLiveInFiles else { return }
        await TagStore.shared.copySubtree(fromAccount: accountId, path: oldPath, toAccount: accountId, to: newPath)
    }

    private func tagsForgetDeleted(_ path: String) async {
        guard !tagsLiveInFiles else { return }
        await TagStore.shared.removeSubtree(account: accountId, path: path)
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
            self.actionError = L10n.format("Failed to create folder: %@", error.localizedDescription)
        }
    }

    func renameItem(_ item: CloudFileItem, to newName: String) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return }
        do {
            try await provider.renameItem(at: item.path, to: newName)
            SupportLogger.shared.log("renameItem \(item.path) → \(newName)", category: cloudCategory)
            let parent = (item.path as NSString).deletingLastPathComponent
            await tagsFollowMove(from: item.path, to: parent == "/" ? "/\(newName)" : "\(parent)/\(newName)")
            await loadDirectory()
        } catch {
            SupportLogger.shared.log("renameItem FAILED \(item.path) → \(newName) — \(error.localizedDescription)", category: cloudCategory, level: .error)
            self.actionError = L10n.format("Failed to rename: %@", error.localizedDescription)
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
                await tagsForgetDeleted(item.path)
            } catch {
                SupportLogger.shared.log("deleteItem FAILED \(item.path) — \(error.localizedDescription)", category: cloudCategory, level: .error)
                self.actionError = L10n.format("Failed to delete %@: %@", item.name, error.localizedDescription)
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
                await tagsForgetDeleted(item.path)
            } catch {
                SupportLogger.shared.log("deleteItem FAILED \(item.path) — \(error.localizedDescription)", category: cloudCategory, level: .error)
                self.actionError = L10n.format("Failed to delete %@: %@", item.name, error.localizedDescription)
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
                let resolution = await ConflictPrompter.shared.ask(PendingConflict(
                    source: ConflictFileInfo(name: item.name, date: item.modificationDate, size: item.size, fileExtension: (item.name as NSString).pathExtension, localURL: nil),
                    destination: ConflictFileInfo(name: existing.name, date: existing.modificationDate, size: existing.size, fileExtension: (existing.name as NSString).pathExtension, localURL: nil),
                    remainingConflicts: remaining,
                    direction: self.conflictDirection
                ))
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

    /// Returns the names of the items that downloaded in full. A folder whose
    /// walk failed part way leaves a partial copy on disk and is not among
    /// them, so nothing may treat "it exists" as "it arrived".
    @discardableResult
    func downloadItems(_ items: [CloudFileItem], to localDirectory: URL, progress: TransferProgress? = nil, skipConflictCheck: Bool = false) async -> Set<String> {
        let landed = await downloadItemsLocating(items, to: localDirectory, progress: progress, skipConflictCheck: skipConflictCheck)
        return Set(items.filter { landed[$0.id] != nil }.map(\.name))
    }

    /// Where a downloaded item ended up. `renamedForClash` is set when its
    /// name was taken by a sibling the disk treats as the same name, and it
    /// landed as "name (2)" instead.
    struct LandedDownload: Equatable {
        let url: URL
        let renamedForClash: Bool
    }

    /// The same as `downloadItems`, keyed by item id and saying where each
    /// item landed. Callers that go on to use the files (a cloud-to-cloud
    /// copy staged on this Mac) must ask this rather than rebuild the path
    /// from the item's name: on a disk that ignores case, `Readme.md` names
    /// `README.md` too, and the name alone would hand over the wrong file.
    func downloadItemsLocating(_ items: [CloudFileItem], to localDirectory: URL, progress: TransferProgress? = nil, skipConflictCheck: Bool = false) async -> [String: LandedDownload] {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else {
            SupportLogger.shared.log("downloadItems aborted: provider unavailable", category: cloudCategory, level: .error)
            return [:]
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

        var applyToAllChoice: ConflictChoice?
        var downloadedCount = 0
        var landed: [String: LandedDownload] = [:]
        // Asked once: every name below lands on the same volume.
        let caseSensitive = LocalNameClaims.isCaseSensitive(localDirectory)
        // Siblings the disk would store under one name get distinct ones.
        let localNames = LocalNameClaims.localNames(for: items.map(\.name), caseSensitive: caseSensitive)
        let providerType = provider.providerType

        for (index, item) in items.enumerated() {
            if progress?.isCancelled == true || Task.isCancelled {
                if !(progress?.isCloudToCloud ?? false) {
                    progress?.endTime = Date(); progress?.isComplete = true
                }
                return landed
            }
            let localName = localNames[index]
            let renamedForClash = localName != SafeLocalPath.fileName(from: item.name)
            let exportExtensions = Self.exportExtensions(for: item, providerType: providerType)
            // Server-supplied name: sanitise and confine before writing. A
            // name that escapes the target directory is refused rather than
            // downloaded; the rest of the batch continues.
            guard let base = try? SafeLocalPath.destination(for: localName, in: localDirectory) else {
                let msg = L10n.text("Unsafe file name from the server — download refused.")
                self.actionError = msg
                progress?.recordFailure(item.name, error: msg)
                continue
            }
            let existingURL = Self.findExistingFile(base: base, convertedExtensions: exportExtensions)
            let exists = existingURL != nil
            var keepBoth = false

            if exists && !skipConflictCheck {
                let choice: ConflictChoice
                if let saved = applyToAllChoice {
                    choice = saved
                } else {
                    let remaining = items[(index + 1)...].filter { nextItem in
                        let nextBase = localDirectory.appendingPathComponent(nextItem.name)
                        return Self.findExistingFile(base: nextBase, convertedExtensions: Self.exportExtensions(for: nextItem, providerType: providerType)) != nil
                    }.count
                    let destInfo = FileManagerViewModel.localFileInfo(at: existingURL!)
                    let resolution = await ConflictPrompter.shared.ask(PendingConflict(
                        source: ConflictFileInfo(name: item.name, date: item.modificationDate, size: item.size, fileExtension: (item.name as NSString).pathExtension, localURL: nil),
                        destination: destInfo,
                        remainingConflicts: remaining,
                        direction: self.conflictDirection
                    ))
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
                    return landed
                case .keepBoth:
                    keepBoth = true
                case .replace: break
                }
            }

            do {
                let arrived: URL
                if exists, let existingURL, !skipConflictCheck {
                    arrived = try await downloadOverExisting(
                        item: item,
                        localName: localName,
                        existing: existingURL,
                        exportExtensions: exportExtensions,
                        keepBoth: keepBoth,
                        in: localDirectory,
                        provider: provider,
                        progress: progress,
                        caseSensitive: caseSensitive,
                        downloadedCount: &downloadedCount
                    )
                } else {
                    if exists {
                        // A caller that settled conflicts already asked for
                        // this item to be replaced. Its earlier export
                        // (`Doc.pdf` beside the `Doc.docx` about to land)
                        // goes to the Trash — and only for a Workspace
                        // document: for anything else these extensions name
                        // unrelated files.
                        for ext in exportExtensions {
                            let converted = base.appendingPathExtension(ext)
                            if FileManager.default.fileExists(atPath: converted.path) {
                                try? await FileSystemService.shared.trashItem(at: converted)
                            }
                        }
                    }
                    arrived = try await downloadRecursively(item: item, localName: localName, to: localDirectory, provider: provider, progress: progress, caseSensitive: caseSensitive, downloadedCount: &downloadedCount)
                }
                landed[item.id] = LandedDownload(url: arrived, renamedForClash: renamedForClash)
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
                // Cancelling isn't a failure to report. The loop stops at
                // its next pass.
                if Self.wasCancelled(progress) {
                    progress?.recordSkip(item.name)
                    continue
                }
                self.actionError = L10n.format("Failed to download %@: %@", item.name, error.localizedDescription)
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
        return landed
    }

    /// Downloads an item whose name is already taken here. It goes to a
    /// staging folder on the same volume first, so the existing item is only
    /// touched once the new one is complete. Returns where it landed.
    private func downloadOverExisting(
        item: CloudFileItem,
        localName: String,
        existing: URL,
        exportExtensions: [String],
        keepBoth: Bool,
        in localDirectory: URL,
        provider: any CloudProvider,
        progress: TransferProgress?,
        caseSensitive: Bool,
        downloadedCount: inout Int
    ) async throws -> URL {
        let fm = FileManager.default
        let staging = localDirectory.appendingPathComponent(".filefluss-download-\(UUID().uuidString)", isDirectory: true)
        try fm.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: staging) }

        try await downloadRecursively(item: item, localName: localName, to: staging, provider: provider, progress: progress, caseSensitive: caseSensitive, downloadedCount: &downloadedCount)

        // The name can differ from `item.name`: Google Docs gain an extension.
        guard let arrived = try fm.contentsOfDirectory(at: staging, includingPropertiesForKeys: nil).first else {
            throw CloudProviderError.notFound(item.path)
        }
        let target = localDirectory.appendingPathComponent(arrived.lastPathComponent)
        if keepBoth {
            let unique = FileManagerViewModel.uniqueDestination(for: target)
            try await FileSystemService.shared.moveItem(from: arrived, to: unique)
            return unique
        }
        try await FileSystemService.shared.replaceItem(at: target, withMoved: arrived)
        // What was found under another name goes too — but only when it is
        // this same item's earlier export (`Doc.pdf` replaced by the
        // `Doc.docx` that just arrived), and to the Trash rather than for
        // good. `Report.pdf` beside a folder called `Report` is an unrelated
        // file, and Replace used to delete it.
        if existing.standardizedFileURL != target.standardizedFileURL,
           Self.isExport(existing, ofLocalName: localName, in: localDirectory, extensions: exportExtensions) {
            do {
                try await FileSystemService.shared.trashItem(at: existing)
            } catch {
                SupportLogger.shared.log("kept the earlier export \(existing.lastPathComponent): \(error.localizedDescription)", category: cloudCategory)
            }
        }
        return target
    }

    private static func wasCancelled(_ progress: TransferProgress?) -> Bool {
        progress?.isCancelled == true || Task.isCancelled
    }

    /// `localName` is the name the item takes on this disk, which differs
    /// from its own when a sibling already claimed that name (see
    /// `LocalNameClaims`). Returns where the item landed — for a Google
    /// Workspace document, under its exported name.
    @discardableResult
    private func downloadRecursively(item: CloudFileItem, localName: String? = nil, to localDirectory: URL, provider: any CloudProvider, progress: TransferProgress?, caseSensitive: Bool, downloadedCount: inout Int, knownTags: [FileTag]? = nil) async throws -> URL {
        // Some providers make no call that notices a cancel, so a folder
        // would otherwise go on fetching its remaining files.
        if Self.wasCancelled(progress) { throw CancellationError() }
        if item.isDirectory {
            let folderURL = try SafeLocalPath.destination(for: localName ?? item.name, in: localDirectory)
            try FileManager.default.createDirectory(at: folderURL, withIntermediateDirectories: true)
            await TagTransfer.downloaded(fromAccount: accountId, providerType: providerType, remotePath: item.path, to: folderURL, known: knownTags)
            let contents = try await provider.listDirectory(at: item.path)
            // A case-sensitive server can hold README.md and Readme.md side
            // by side, a Drive folder two files of one name, and any server
            // the NFC and NFD spellings of Café. Written here under their
            // own names, the second silently replaced the first, the folder
            // still counted as downloaded, and a cut then deleted the
            // original. Each child gets a name of its own instead.
            let childNames = LocalNameClaims.localNames(for: contents.map(\.name), caseSensitive: caseSensitive)
            // A folder never opened in a panel has no record yet. Where the
            // service has labels of its own, they are asked for here so the
            // files inside come down tagged all the same.
            let support = provider.nativeTagSupport
            let native = support == .none ? nil : try? await provider.nativeTags(inFolder: item.path)
            let standard = SystemTagNames.standard
            for (child, childName) in zip(contents, childNames) {
                var childTags: [FileTag]?
                if let native {
                    childTags = NativeTagSync.reconcile(
                        stored: await TagStore.shared.tags(account: accountId, path: child.path),
                        native: native[child.path] ?? [],
                        support: support,
                        pending: false,
                        standard: standard
                    ).tags
                }
                try await downloadRecursively(item: child, localName: childName, to: folderURL, provider: provider, progress: progress, caseSensitive: caseSensitive, downloadedCount: &downloadedCount, knownTags: childTags)
            }
            return folderURL
        } else {
            let localURL = try SafeLocalPath.destination(for: localName ?? item.name, in: localDirectory)
            progress?.beginFile(item.name, size: item.size)
            // Straight into the inbox: this fires per network chunk, and a
            // main-actor hop each time costs more than the figure is worth.
            let inbox = progress?.byteInbox
            try await provider.downloadFile(remotePath: item.path, to: localURL, onBytes: { bytes in
                inbox?.addDownload(bytes)
            })
            // Preserve original cloud modification date on the local file
            // so conflict dialogs and file listings show the correct date.
            // A Google Workspace document lands under its exported name;
            // for anything else a `Name.pdf` beside it is a sibling, which
            // must not be stamped, marked or tagged in its place.
            let finalURL = Self.exportExtensions(for: item, providerType: provider.providerType)
                .lazy.map { localURL.appendingPathExtension($0) }
                .first { FileManager.default.fileExists(atPath: $0.path) } ?? localURL
            try? FileManager.default.setAttributes(
                [.modificationDate: item.modificationDate],
                ofItemAtPath: finalURL.path
            )
            Quarantine.mark(finalURL)
            // The item's tags come down with it, as Finder tags on the file.
            await TagTransfer.downloaded(fromAccount: accountId, providerType: providerType, remotePath: item.path, to: finalURL, known: knownTags)
            progress?.totalBytes += item.size
            downloadedCount += 1
            progress?.totalFiles = downloadedCount
            progress?.completedItems = downloadedCount
            return finalURL
        }
    }

    // MARK: - Google Workspace exports

    /// The extensions a Google Workspace document can come down with. It
    /// has no bytes of its own, so the provider exports it and writes `Doc`
    /// as `Doc.docx` (or `.xlsx`, `.pptx`, `.pdf`).
    nonisolated static let googleExportExtensions = ["docx", "xlsx", "pptx", "pdf"]

    /// The extensions `item` may gain on the way down: none for anything
    /// but a Google Workspace document.
    ///
    /// Applied to every item, a folder or plain file called `Report`
    /// "already existed" whenever `Report.pdf` or `Report.docx` sat in the
    /// destination, and Replace then deleted that unrelated file. The
    /// listing carries no MIME type, but a Workspace document is the one
    /// kind of Drive file without an MD5 checksum — Drive keeps one for
    /// every file with content of its own — and is never a folder.
    nonisolated static func exportExtensions(for item: CloudFileItem, providerType: CloudProviderType?) -> [String] {
        guard providerType == .googleDrive || providerType == .googleDrivePicker,
              !item.isDirectory, item.checksum == nil else { return [] }
        return googleExportExtensions
    }

    /// Whether `url` is the exported form of the item that lands as
    /// `localName` in `directory` — `Doc.pdf` for `Doc` — rather than some
    /// other file that merely shares the stem.
    nonisolated static func isExport(_ url: URL, ofLocalName localName: String, in directory: URL, extensions: [String]) -> Bool {
        guard url.deletingLastPathComponent().standardizedFileURL == directory.standardizedFileURL else { return false }
        let safe = SafeLocalPath.fileName(from: localName)
        return extensions.contains { url.lastPathComponent == "\(safe).\($0)" }
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
        if result.couldNotCheck {
            // The check never reached the server — a self-signed
            // certificate on a self-hosted box, a blocked port, no network.
            // Declaring the link private on that basis would be a guess,
            // and `linksAreNotPublic` would make it a sticky one, so the
            // link is handed over as the provider described it with a note
            // saying plainly what didn't happen.
            cloudFileVMLog.info("[Share] Link could not be checked for account \(self.accountId, privacy: .public)")
            return CloudShareLink(
                url: link.url,
                directDownloadURL: link.directDownloadURL,
                expiresAt: link.expiresAt,
                hasPassword: link.hasPassword,
                isPublic: link.isPublic,
                note: [link.note, L10n.format("The link couldn't be checked from here: %@", result.detail ?? L10n.text("the server didn't answer."))]
                    .compactMap { $0 }
                    .joined(separator: " ")
            )
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
        // An unanswered check is not evidence the provider refuses direct
        // links, and this verdict is written to disk and never revisited —
        // one certificate error would downgrade the account's links for
        // good.
        if result.couldNotCheck { return link }
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
        reportedSignInProblem = true
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
        // Inside an archive the file is unpacked rather than downloaded —
        // the provider has never heard of this path. Everything that wants a
        // local copy (opening, Quick Look, the drag-out fallback) comes
        // through here, so this one branch covers them all.
        if let session = archiveSession, let inner = archiveInnerPath(for: item.path) {
            do {
                return try await session.materialise(innerPaths: [inner], openedExternally: true).first
            } catch {
                archiveError = error.localizedDescription
                return nil
            }
        }
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
                // A copy cached by an earlier version went unmarked.
                Quarantine.mark(localURL)
                return localURL
            }
            try? FileManager.default.removeItem(at: localURL)
        }
        // Only a Google Workspace document comes down under another name.
        // For anything else `Name.pdf` in this folder is a sibling, and
        // opening `Name` must not open that instead.
        let exportExtensions = Self.exportExtensions(for: item, providerType: provider.providerType)
        for ext in exportExtensions {
            let converted = localURL.appendingPathExtension(ext)
            if FileManager.default.fileExists(atPath: converted.path) {
                Quarantine.mark(converted)
                return converted
            }
        }

        do {
            try await provider.downloadFile(remotePath: item.path, to: localURL)
            // Google Workspace files get written with an appended extension
            let finalURL = exportExtensions
                .lazy.map { localURL.appendingPathExtension($0) }
                .first { FileManager.default.fileExists(atPath: $0.path) } ?? localURL
            try? FileManager.default.setAttributes(
                [.modificationDate: item.modificationDate],
                ofItemAtPath: finalURL.path
            )
            // Everything that opens a cloud file comes through here — a
            // double-click, Open With, Quick Look, browsing a cloud archive —
            // and the copy is as much a download as one from a browser.
            // Without the mark Gatekeeper never looks at it, and a `.dmg`,
            // a `.terminal` file or an app in a shared folder would run
            // unchecked.
            Quarantine.mark(finalURL)
            return finalURL
        } catch {
            cloudFileVMLog.error("QuickLook download failed for \(item.path, privacy: .public): \(error.localizedDescription, privacy: .public)")
            return nil
        }
    }

    // MARK: - Upload

    /// `replacing` names, by upload name, items a caller has already decided
    /// to overwrite. It's how a transfer that resolved its conflicts up front
    /// gets the same safe replacement as the dialog here.
    func uploadFiles(from urls: [URL], toPath: String? = nil, progress: TransferProgress? = nil, skipConflictCheck: Bool = false, replacing: [String: CloudFileItem] = [:]) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else {
            SupportLogger.shared.log("uploadFiles aborted: provider unavailable", category: cloudCategory, level: .error)
            self.actionError = "Cloud account not connected"
            progress?.isComplete = true
            return
        }
        SupportLogger.shared.log("uploadFiles start: \(urls.count) item(s) → \(toPath ?? currentPath)", category: cloudCategory)

        let remoteBase = toPath ?? currentPath
        let existingItems: [CloudFileItem]
        if skipConflictCheck {
            existingItems = []
        } else if let toPath, toPath != currentPath {
            do {
                existingItems = try await provider.listDirectory(at: toPath)
            } catch {
                // Not knowing what's there isn't the same as nothing being
                // there: going on would overwrite without asking.
                let message = L10n.format("Couldn't check the destination for items with the same name: %@", error.localizedDescription)
                self.actionError = message
                progress?.failAll(urls.map(\.lastPathComponent), error: message)
                return
            }
        } else {
            existingItems = items
        }
        let existingByName = Dictionary(existingItems.map { ($0.name, $0) }, uniquingKeysWith: { first, _ in first })

        progress?.transferredFileNames = urls.map(\.lastPathComponent)
        progress?.isCloudUpload = true

        // Which items are folders, and the bytes to send for byte-weighted
        // progress. Sizing a folder walks every file in it, and done here
        // on the main actor a 100,000-file folder froze the app for seconds
        // before the first byte went out. The walk runs alongside the
        // upload instead; until it reports, the bar counts files.
        if let progress {
            let wantsBytes = progress.isCloudToCloud != true
            let toInspect = urls
            Task {
                let inspected = await Task.detached(priority: .userInitiated) {
                    Self.inspectUploadSources(toInspect, measuringBytes: wantsBytes)
                }.value
                progress.transferredFolderNames = inspected.folderNames
                if wantsBytes, !progress.isComplete {
                    progress.expectedBytesSingle = inspected.bytes
                }
            }
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
            var itemToReplace = replacing[name]
            if let existing = existingByName[name], !skipConflictCheck {
                let choice: ConflictChoice
                if let saved = applyToAllChoice {
                    choice = saved
                } else {
                    let remaining = urls[(index + 1)...].filter { existingByName[$0.lastPathComponent] != nil }.count
                    let sourceInfo = FileManagerViewModel.localFileInfo(at: url)
                    let resolution = await ConflictPrompter.shared.ask(PendingConflict(
                        source: sourceInfo,
                        destination: ConflictFileInfo(name: existing.name, date: existing.modificationDate, size: existing.size, fileExtension: (existing.name as NSString).pathExtension, localURL: nil),
                        remainingConflicts: remaining,
                        direction: self.conflictDirection
                    ))
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
                        let msg = L10n.format("Couldn't stage a copy for \"Keep Both\": %@", error.localizedDescription)
                        self.actionError = msg
                        progress?.recordFailure(name, error: msg)
                        continue
                    }
                    uploadURL = tempCopy
                case .replace:
                    itemToReplace = existing
                }
            }

            do {
                if let itemToReplace {
                    let source = uploadURL
                    try await provider.replacingItem(at: itemToReplace.path, finalName: source.lastPathComponent) { temporaryPath in
                        try await uploadRecursively(urls: [source], toRemotePath: remoteBase, remoteItemPath: temporaryPath, provider: provider, progress: progress, uploadedCount: &uploadedCount)
                    }
                } else {
                    try await uploadRecursively(urls: [uploadURL], toRemotePath: remoteBase, provider: provider, progress: progress, uploadedCount: &uploadedCount)
                }
                // The Finder tags of what was uploaded become the tags of
                // the items on the account. Read from the original, under
                // the name the upload ended up with.
                let uploadedName = uploadURL.lastPathComponent
                await TagTransfer.uploaded(
                    from: url,
                    toAccount: accountId,
                    providerType: providerType,
                    remotePath: remoteBase == "/" ? "/\(uploadedName)" : "\(remoteBase)/\(uploadedName)"
                )
                if uploadURL != url { try? FileManager.default.removeItem(at: uploadURL) }
                progress?.recordSuccess(name)
            } catch {
                if Self.wasCancelled(progress) {
                    progress?.recordSkip(name)
                    continue
                }
                self.actionError = error.localizedDescription
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

    /// `remoteItemPath` puts a single item somewhere other than under its own
    /// name, for a replacement that's first written beside the original.
    private func uploadRecursively(urls: [URL], toRemotePath remotePath: String, remoteItemPath: String? = nil, provider: CloudProvider, progress: TransferProgress?, uploadedCount: inout Int) async throws {
        for url in urls {
            if Self.wasCancelled(progress) { throw CancellationError() }
            // Stat and list off the main actor; see `inspectLocalUploadItem`.
            guard let local = try await Self.inspectLocalUploadItem(url) else {
                cloudFileVMLog.debug("Upload: file not found, skipping \(url.path, privacy: .public)")
                continue
            }

            let itemRemotePath = remoteItemPath
                ?? (remotePath == "/" ? "/\(url.lastPathComponent)" : "\(remotePath)/\(url.lastPathComponent)")

            if local.isDirectory {
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
                let contents = local.children
                cloudFileVMLog.debug("Upload: directory \(url.lastPathComponent, privacy: .public) has \(contents.count) items")
                try await uploadRecursively(urls: contents, toRemotePath: itemRemotePath, provider: provider, progress: progress, uploadedCount: &uploadedCount)
            } else {
                let fileBytes = local.size
                progress?.beginFile(url.lastPathComponent, size: fileBytes)

                // Pre-flight: reject oversized files locally before sending
                // any bytes. Saves the user from a multi-gigabyte upload that
                // the server would refuse anyway.
                try await CloudProviderError.enforceUploadSizeLimit(url, provider: provider)

                cloudFileVMLog.debug("Upload: \(url.lastPathComponent, privacy: .public) (\(fileBytes) bytes) → \(itemRemotePath, privacy: .public)")
                let inbox = progress?.byteInbox
                try await provider.uploadFile(from: url, to: itemRemotePath, onBytes: { bytes in
                    inbox?.addUpload(bytes)
                })
                cloudFileVMLog.debug("Upload: success \(url.lastPathComponent, privacy: .public)")
                // Preserve the source file's modification date on the
                // newly-uploaded remote so cross-source transfers behave
                // like Finder — only content changes update "Date Modified".
                // For cloud→local→cloud transfers the local mtime was set
                // to the original source mtime during download (see
                // downloadRecursively), so this carries it through.
                if let mtime = local.modificationDate {
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

        let targetSiblings: [CloudFileItem]
        do {
            targetSiblings = try await provider.listDirectory(at: targetPath)
        } catch {
            // Not knowing what's there isn't the same as nothing being there:
            // going on would overwrite without asking.
            let message = L10n.format("Couldn't check the destination for items with the same name: %@", error.localizedDescription)
            self.actionError = message
            progress?.failAll(sourceItems.map(\.name), error: message)
            return
        }
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

        // Only what downloaded in full goes on. A failed folder still has a
        // partial copy in staging, which must not upload and pass for it.
        // Where each item landed, as the download reports it: on a disk that
        // ignores case the path rebuilt from `Readme.md` would also find a
        // staged `README.md`, and upload that in its place.
        let staged = await downloadItemsLocating(itemsToTransfer, to: tempDir, progress: progress, skipConflictCheck: true)
        progress?.downloadEndTime = Date()

        let existingNames = Set(targetSiblings.map(\.name))
        var clashRenamed: Set<String> = []
        let stagedItems: [(item: CloudFileItem, url: URL)] = itemsToTransfer.compactMap { item -> (item: CloudFileItem, url: URL)? in
            guard let landed = staged[item.id] else { return nil }
            let url = landed.url
            // Staged as "name (2)" next to a sibling of the same name: it
            // goes up under that name, and replaces nothing.
            if landed.renamedForClash {
                clashRenamed.insert(item.id)
                return (item, url)
            }

            if resolutionByName[item.name] == .keepBoth {
                let uniqueName = Self.uniqueCloudName(for: url.lastPathComponent, existing: existingNames)
                let renamed = tempDir.appendingPathComponent(uniqueName)
                do {
                    try FileManager.default.moveItem(at: url, to: renamed)
                    return (item, renamed)
                } catch {
                    return (item, url)
                }
            }
            return (item, url)
        }

        if let progress {
            progress.currentPhase = .uploading
            progress.completedItems = 0
            progress.clearCurrentFile()
            progress.uploadStartTime = Date()
        }

        var uploadedCount = 0
        var landed: Set<String> = []
        for (item, url) in stagedItems {
            if progress?.isCancelled == true || Task.isCancelled { break }
            do {
                if resolutionByName[item.name] == .replace, !clashRenamed.contains(item.id),
                   let existing = targetSiblings.first(where: { $0.name == item.name }) {
                    try await provider.replacingItem(at: existing.path, finalName: url.lastPathComponent) { temporaryPath in
                        try await uploadRecursively(urls: [url], toRemotePath: targetPath, remoteItemPath: temporaryPath, provider: provider, progress: progress, uploadedCount: &uploadedCount)
                    }
                } else {
                    try await uploadRecursively(urls: [url], toRemotePath: targetPath, provider: provider, progress: progress, uploadedCount: &uploadedCount)
                }
                landed.insert(item.name)
                progress?.recordSuccess(item.name)
            } catch {
                self.actionError = error.localizedDescription
                progress?.recordFailure(item.name, error: error.localizedDescription)
            }
        }

        progress?.uploadEndTime = Date()
        progress?.totalBytes = (progress?.downloadBytes ?? 0) + (progress?.uploadBytes ?? 0)

        if deleteFromSource {
            // A move may only delete what arrived.
            let transferred = itemsToTransfer.filter { landed.contains($0.name) }
            if !transferred.isEmpty {
                await deleteItems(transferred)
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
        // Previewing means downloading, so only do it while the preview is
        // open: otherwise clicking a 4 GB video would fetch 4 GB.
        // `toggleQuickLook` fetches for itself when the panel opens.
        guard quickLookController.isPanelVisible else { return }

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

        // A copy of a folder, to a provider whose server-side copy handles
        // one file at a time, has to be declined now rather than part way
        // through. Falling back mid-batch would hand the whole list to the
        // download-and-upload path, including the items already copied.
        if !deleteFromSource,
           items.contains(where: { $0.isDirectory }),
           await provider.canCopyDirectoriesServerSide == false {
            SupportLogger.shared.log(
                "server-side copy can't take a folder on this provider — falling back before copying anything",
                category: cloudCategory
            )
            return false
        }

        for (index, item) in items.enumerated() {
            if progress?.isCancelled == true || Task.isCancelled { return providerSupports }
            progress?.beginFile(item.name, size: item.size)

            // Determine the destination path. KeepBoth → unique name.
            let resolution = resolutionByName[item.name] ?? .transfer
            var destName = item.name
            if resolution == .keepBoth {
                destName = Self.uniqueCloudName(for: item.name, existing: existingNames)
            }
            let destPath = targetPath == "/" ? "/\(destName)" : "\(targetPath)/\(destName)"
            let existing = resolution == .replace ? targetSiblings.first(where: { $0.name == item.name }) : nil

            do {
                // Replace sets the old item aside rather than deleting it, so
                // a move that fails, or a provider that turns out not to
                // support moves at all, can put it back.
                var setAside: String?
                if let existing {
                    let name = FileSystemService.temporaryReplacementName(for: item.name)
                    try await provider.renameItem(at: existing.path, to: name)
                    setAside = targetPath == "/" ? "/\(name)" : "\(targetPath)/\(name)"
                }
                do {
                    if deleteFromSource {
                        SupportLogger.shared.log("server-side move \(item.path) → \(destPath)", category: cloudCategory)
                        try await provider.moveItem(at: item.path, toPath: destPath)
                    } else {
                        SupportLogger.shared.log("server-side copy \(item.path) → \(destPath)", category: cloudCategory)
                        try await provider.copyItem(at: item.path, toPath: destPath)
                    }
                } catch {
                    if let setAside { try? await provider.renameItem(at: setAside, to: item.name) }
                    throw error
                }
                if let setAside { try? await provider.deleteItem(at: setAside) }
                if deleteFromSource {
                    await tagsFollowMove(from: item.path, to: destPath)
                } else {
                    await tagsFollowCopy(from: item.path, to: destPath)
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
                // Mid-batch: the items before this one are already moved,
                // so handing the batch back to the fallback would redo
                // them. Report this one and carry on with the rest.
                progress?.recordFailure(
                    item.name,
                    error: deleteFromSource
                        ? L10n.text("The server wouldn't move this item — try copying it and deleting the original.")
                        : L10n.text("The server wouldn't copy this item.")
                )
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

    /// What `uploadFiles` shows before the bytes move: which of the sources
    /// are folders, and (when asked) how many bytes all of them hold. Walks
    /// the disk, so it is called off the main actor.
    nonisolated static func inspectUploadSources(_ urls: [URL], measuringBytes: Bool) -> (folderNames: Set<String>, bytes: Int64) {
        let fm = FileManager.default
        var folders: Set<String> = []
        var bytes: Int64 = 0
        for url in urls {
            var isDir: ObjCBool = false
            guard fm.fileExists(atPath: url.path, isDirectory: &isDir) else { continue }
            if isDir.boolValue { folders.insert(url.lastPathComponent) }
            if measuringBytes { bytes += localSizeRecursively(url: url) }
        }
        return (folders, bytes)
    }

    /// One local item as an upload needs it. `nil` when it isn't there.
    struct LocalUploadItem: Sendable {
        let isDirectory: Bool
        let size: Int64
        let modificationDate: Date?
        /// A folder's contents; empty for a file.
        let children: [URL]
    }

    /// Reads what `uploadRecursively` needs to know about `url` — whether it
    /// exists, is a folder, its size and date, a folder's contents — in one
    /// go off the main actor. Asked once per item, these stats and listings
    /// on the main actor added up to seconds of frozen UI for a big folder.
    nonisolated static func inspectLocalUploadItem(_ url: URL) async throws -> LocalUploadItem? {
        try await Task.detached(priority: .userInitiated) { () throws -> LocalUploadItem? in
            let fm = FileManager.default
            var isDir: ObjCBool = false
            guard fm.fileExists(atPath: url.path, isDirectory: &isDir) else { return nil }
            if isDir.boolValue {
                let children = try fm.contentsOfDirectory(at: url, includingPropertiesForKeys: [.fileSizeKey, .contentModificationDateKey, .isDirectoryKey])
                return LocalUploadItem(isDirectory: true, size: 0, modificationDate: nil, children: children)
            }
            let values = try? url.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey])
            return LocalUploadItem(
                isDirectory: false,
                size: Int64(values?.fileSize ?? 0),
                modificationDate: values?.contentModificationDate,
                children: []
            )
        }.value
    }

    /// Recursively sums up the total byte size of a local file or directory.
    nonisolated static func localSizeRecursively(url: URL) -> Int64 {
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

    func pushToHistory(_ path: String) {
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
