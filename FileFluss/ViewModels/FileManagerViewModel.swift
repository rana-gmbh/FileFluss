import SwiftUI
import FileFlussCore
import QuickLookUI


/// Counts and total size of a listing, computed in one pass and cached.
/// The panel footer reads all three on every redraw — including every
/// arrow-key press, since it also depends on the selection — and three
/// separate filters over a 10k-file folder per redraw is what made
/// holding arrow-down feel heavy.
struct ListingSummary: Equatable {
    var fileCount: Int = 0
    var folderCount: Int = 0
    var totalSize: Int64 = 0
}

@Observable @MainActor
final class FileManagerViewModel {
    var currentDirectory: URL
    var items: [FileItem] = [] { didSet { _filteredItemsCache = nil; _listingSummaryCache = nil } }
    var selectedItemIDs: Set<String> = []
    var sortOrder: SortOrder = .name { didSet { _filteredItemsCache = nil; _listingSummaryCache = nil } }
    var sortAscending: Bool = true { didSet { _filteredItemsCache = nil; _listingSummaryCache = nil } }
    var showHiddenFiles: Bool = false
    var isLoading: Bool = false
    var error: String?
    var pathHistory: [URL] = []
    var pathHistoryIndex: Int = -1
    var searchText: String = "" { didSet { _filteredItemsCache = nil; _listingSummaryCache = nil } }

    /// Memoised result of `filteredItems`. Recomputed lazily and invalidated
    /// (via `didSet` above) only when an input that affects it changes —
    /// items, search text, or sort. `selectedItemIDs` is deliberately *not*
    /// an input, so selecting rows no longer re-sorts the whole listing.
    /// `@ObservationIgnored` so writing the cache never itself triggers a
    /// SwiftUI invalidation.
    @ObservationIgnored private var _filteredItemsCache: [FileItem]?
    @ObservationIgnored private var _listingSummaryCache: ListingSummary?

    /// Bumped by every navigation, so a listing that finishes after the
    /// panel has gone somewhere else can tell it has been superseded.
    ///
    /// Without it the folder on screen and the rows below it can disagree:
    /// the first listing at launch is the home folder, and on a large or
    /// slow one it was still running when the user clicked somewhere else —
    /// then landed and replaced that folder's rows with home's, under the
    /// new folder's name. Opening an archive makes it easy to hit, because
    /// the archive's own listing comes back at once.
    ///
    /// Only a navigation bumps it. A refresh — `loadDirectory()` with no
    /// argument, which is also how the initialiser lists the home folder —
    /// can be superseded but never supersedes, so the listing the user asked
    /// for is always the one that wins.
    @ObservationIgnored private var loadGeneration = 0

    /// One pass over the listing, cached alongside `filteredItems`.
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
    /// Toggled by the Edit → Quick Filter… command; when true, the panel
    /// shows an inline filter bar bound to `searchText`.
    var isFilterBarVisible: Bool = false

    // MARK: - Archive browsing

    /// Non-nil while this panel is showing the inside of an archive. The
    /// listing then comes from the archive's entry list rather than from the
    /// file system, and every write action is withheld — see
    /// `ArchiveBrowseSession` for why the rows still carry file URLs.
    var archiveSession: ArchiveBrowseSession?

    var isBrowsingArchive: Bool { archiveSession != nil }

    /// An archive operation that failed, for the panel to show.
    ///
    /// Kept apart from `error` because that one has no presenter: the panel
    /// has always set it and never displayed it. The first thing a user is
    /// likely to meet here is a password-protected RAR, and "nothing
    /// happened" is the one answer that can't be acted on.
    var archiveError: String?

    // Drag & Drop
    var draggedItems: [FileItem] = []
    var pendingDrop: PendingDrop?
    // Drag & Drop pending state

    struct PendingDrop {
        let items: [FileItem]
        let destinationFolder: URL
    }

    // MARK: - Per-item conflict resolution

    var conflictDirection: ConflictDirection = .leftToRight
    var pendingConflict: PendingConflict?
    private var conflictContinuation: CheckedContinuation<ConflictResolution, Never>?

    func resolveConflict(_ resolution: ConflictResolution) {
        conflictContinuation?.resume(returning: resolution)
        conflictContinuation = nil
        pendingConflict = nil
    }

    // Quick Look — controlled directly, no SwiftUI binding
    let quickLookController = QuickLookController()

    enum SortOrder: String, CaseIterable {
        case name, date, dateCreated, size, kind

        var label: String {
            switch self {
            case .name: return "Name"
            case .date: return "Date Modified"
            case .dateCreated: return "Date Created"
            case .size: return "Size"
            case .kind: return "Kind"
            }
        }
    }

    init() {
        self.currentDirectory = Foundation.FileManager.default.homeDirectoryForCurrentUser
        Task { await loadDirectory() }
    }

    var filteredItems: [FileItem] {
        // Read every input up front so SwiftUI observation keeps tracking
        // them as dependencies even on a cache hit — otherwise the view
        // would stop re-rendering when they change.
        let items = self.items
        let query = self.searchText
        _ = self.sortOrder
        _ = self.sortAscending
        if let cached = _filteredItemsCache { return cached }
        let filtered = query.isEmpty
            ? items
            : items.filter { $0.name.localizedCaseInsensitiveContains(query) }
        let result = sorted(filtered)
        _filteredItemsCache = result
        return result
    }

    func loadDirectory(at url: URL? = nil) async {
        let targetURL = url ?? currentDirectory
        if url != nil { loadGeneration += 1 }
        let generation = loadGeneration

        if let session = archiveSession {
            if let inner = session.innerPath(for: targetURL) {
                // Still inside the archive: served from the entry list, with
                // no file system access and nothing to wait for.
                items = session.items(at: inner, showHidden: showHiddenFiles)
                error = nil
                isLoading = false
                if let url, url != currentDirectory {
                    currentDirectory = url
                    pushToHistory(url)
                }
                selectedItemIDs.removeAll()
                return
            }
            // The navigation left the archive — back out to the real folder.
            closeArchive()
        }

        isLoading = true
        error = nil

        do {
            let loadedItems = try await FileSystemService.shared.listDirectory(
                at: targetURL,
                showHidden: showHiddenFiles
            )

            await MainActor.run {
                guard self.isCurrent(generation: generation, target: targetURL, wasNavigation: url != nil) else { return }
                self.items = loadedItems
                if let url, url != self.currentDirectory {
                    self.currentDirectory = url
                    self.pushToHistory(url)
                }
                self.selectedItemIDs.removeAll()
                self.isLoading = false
            }
        } catch {
            await MainActor.run {
                guard self.isCurrent(generation: generation, target: targetURL, wasNavigation: url != nil) else { return }
                self.error = error.localizedDescription
                self.isLoading = false
            }
        }
    }

    /// Whether a listing that has just come back is still the one the panel
    /// wants.
    ///
    /// Two ways it may not be. A newer navigation has been asked for, which
    /// the generation catches. Or this was a refresh that started while a
    /// navigation was already in flight — it then shares the navigation's
    /// generation, because a refresh never takes one of its own, and would
    /// win simply by finishing second. So a refresh also has to still be
    /// about the folder on screen.
    private func isCurrent(generation: Int, target: URL, wasNavigation: Bool) -> Bool {
        guard generation == loadGeneration else { return false }
        return wasNavigation || target == currentDirectory
    }

    func navigateTo(_ url: URL) async {
        if url != currentDirectory {
            await loadDirectory(at: url)
        }
    }

    func navigateUp() async {
        // `deletingLastPathComponent()` doesn't stop at the filesystem root —
        // on `file:///` it returns `file:///../`, which escapes root and feeds
        // the breadcrumb an endless `/../..` chain (a main-thread hang).
        // Standardize and bail out once we're already at root.
        let current = currentDirectory.standardizedFileURL
        let parent = current.deletingLastPathComponent().standardizedFileURL
        guard parent.path() != current.path() else { return }
        await navigateTo(parent)
    }

    func navigateBack() async {
        guard pathHistoryIndex > 0 else { return }
        pathHistoryIndex -= 1
        let url = pathHistory[pathHistoryIndex]
        currentDirectory = url
        await loadDirectory()
    }

    func navigateForward() async {
        guard pathHistoryIndex < pathHistory.count - 1 else { return }
        pathHistoryIndex += 1
        let url = pathHistory[pathHistoryIndex]
        currentDirectory = url
        await loadDirectory()
    }

    var canGoBack: Bool { pathHistoryIndex > 0 }
    var canGoForward: Bool { pathHistoryIndex < pathHistory.count - 1 }

    func openItem(_ item: FileItem) async {
        if item.isDirectory {
            await navigateTo(item.url)
            return
        }
        if let session = archiveSession {
            await openEntry(item, from: session)
            return
        }
        // An archive opens into the panel rather than into Archive Utility.
        // If it turns out not to be one after all — an extension that lies —
        // `enterArchive` says so and the file goes to its own app.
        if let format = ArchiveFormat.detect(item.url),
           await enterArchive(at: item.url, format: format) {
            return
        }
        NSWorkspace.shared.open(item.url)
    }

    /// Opens the archive at `url` in this panel. Returns false when the file
    /// could not be read as an archive, so the caller can fall back.
    @discardableResult
    func enterArchive(at url: URL, format: ArchiveFormat) async -> Bool {
        isLoading = true
        do {
            let tree = try await ArchiveService.list(url, format: format)
            // An archive that lists nothing is nearly always a file whose
            // extension lies. Showing an empty panel would be a worse answer
            // than letting the file open in its own app.
            guard !tree.isEmpty else {
                isLoading = false
                return false
            }
            archiveSession?.close()
            // Opening an archive is a navigation: a folder listing still in
            // flight must not land on top of it.
            loadGeneration += 1
            let session = ArchiveBrowseSession(archiveURL: url, format: format, tree: tree)
            archiveSession = session
            items = session.items(at: "", showHidden: showHiddenFiles)
            currentDirectory = url
            pushToHistory(url)
            selectedItemIDs.removeAll()
            error = nil
            isLoading = false
            SupportLogger.shared.log(
                "opened archive \(url.lastPathComponent) — \(tree.entries.count) entries, format \(format.rawValue)",
                category: "archive"
            )
            return true
        } catch {
            isLoading = false
            // A file that isn't readable as an archive is not an error worth
            // reporting; a readable one that is locked or broken is.
            if let archiveError = error as? ArchiveError, case .unreadable = archiveError {
                return false
            }
            self.archiveError = error.localizedDescription
            return true
        }
    }

    /// Leaves the archive and lets the staging folder go.
    func closeArchive() {
        archiveSession?.close()
        archiveSession = nil
    }

    /// Opens one entry from inside an archive with its own app, unpacking it
    /// first.
    ///
    /// An archive inside an archive is treated the same way, rather than
    /// opened in the panel: a nested session's breadcrumb would have to show
    /// the staging folder the inner archive was unpacked to, and a path into
    /// FileFluss's cache is not something to put in front of the user.
    private func openEntry(_ item: FileItem, from session: ArchiveBrowseSession) async {
        do {
            let staged = try await session.materialise([item], openedExternally: true)
            guard let url = staged.first else { return }
            NSWorkspace.shared.open(url)
        } catch {
            self.archiveError = error.localizedDescription
        }
    }

    var selectedItems: [FileItem] {
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
        selectionSize = fileSize // show file size immediately

        let folderURLs = folders.map(\.url)
        selectionSizeTask = Task {
            // The folder walk must NOT run on the MainActor: `Task { … }`
            // inside a `@MainActor` class inherits the actor, so without
            // `Task.detached` the enumerator blocks the UI thread — visible
            // as a beachball on every selection change when the folder lives
            // on a slow external/network volume.
            let total = await Task.detached(priority: .utility) { () -> Int64 in
                var running = fileSize
                let fm = Foundation.FileManager.default
                for folderURL in folderURLs {
                    if Task.isCancelled { return running }
                    let enumerator = fm.enumerator(
                        at: folderURL,
                        includingPropertiesForKeys: [.fileSizeKey, .isRegularFileKey],
                        options: [.skipsHiddenFiles]
                    )
                    while let fileURL = enumerator?.nextObject() as? URL {
                        if Task.isCancelled { return running }
                        if let values = try? fileURL.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey]),
                           values.isRegularFile == true {
                            running += Int64(values.fileSize ?? 0)
                        }
                    }
                }
                return running
            }.value
            guard !Task.isCancelled else { return }
            self.selectionSize = total
            self.isCalculatingSelectionSize = false
        }
    }

    func deleteSelectedItems() async {
        for item in selectedItems {
            do {
                try await FileSystemService.shared.deleteItem(at: item.url)
            } catch {
                self.error = "Failed to delete \(item.name): \(error.localizedDescription)"
                return
            }
        }
        selectedItemIDs.removeAll()
        await loadDirectory()
    }

    func deleteItems(_ items: [FileItem]) async {
        for item in items {
            do {
                try await FileSystemService.shared.deleteItem(at: item.url)
            } catch {
                self.error = "Failed to delete \(item.name): \(error.localizedDescription)"
                return
            }
        }
    }

    /// Items whose volume has no Trash, so they couldn't be moved there. When
    /// non-empty the view offers an immediate permanent delete (like Finder).
    var itemsNeedingPermanentDelete: [FileItem] = []
    /// Items macOS refused to let FileFluss move to the Trash. Drives a
    /// prompt that explains the refusal and offers the setting that fixes
    /// it, rather than a bare "failed".
    var itemsBlockedByPermission: [FileItem] = []

    func trashSelectedItems() async {
        var needPermanent: [FileItem] = []
        var needPermission: [FileItem] = []
        for item in selectedItems {
            do {
                try await FileSystemService.shared.trashItem(at: item.url)
            } catch {
                // Two failures are worth handling rather than reporting: a
                // volume with no Trash, where deleting outright is the only
                // way; and macOS refusing access, where the user has a
                // setting to change and no way of knowing it.
                if FileSystemService.isNoTrashOnVolumeError(error) {
                    needPermanent.append(item)
                } else if FileSystemService.isPermissionError(error) {
                    needPermission.append(item)
                } else {
                    self.error = "Failed to trash \(item.name): \(error.localizedDescription)"
                }
            }
        }
        selectedItemIDs.removeAll()
        await loadDirectory()
        if !needPermanent.isEmpty {
            SupportLogger.shared.log("trash fell back to permanent-delete prompt for \(needPermanent.count) item(s) on a no-Trash volume", category: "fs")
            itemsNeedingPermanentDelete = needPermanent
        }
        if !needPermission.isEmpty {
            SupportLogger.shared.log("trash refused for \(needPermission.count) item(s) — macOS withheld permission", category: "fs", level: .error)
            itemsBlockedByPermission = needPermission
        }
    }

    /// Permanently delete the items collected in `itemsNeedingPermanentDelete`
    /// (a volume without a Trash). Called after the user confirms.
    func permanentlyDeletePendingItems() async {
        let items = itemsNeedingPermanentDelete
        itemsNeedingPermanentDelete = []
        await permanentlyDelete(items)
    }

    /// Deletes items outright. Shared by the no-Trash fallback and the
    /// permission prompt, which offers it as a second choice.
    func permanentlyDelete(_ items: [FileItem]) async {
        for item in items {
            do {
                try await FileSystemService.shared.deleteItem(at: item.url)
            } catch {
                self.error = "Failed to delete \(item.name): \(error.localizedDescription)"
            }
        }
        await loadDirectory()
    }

    func createNewFolder(named name: String) async {
        let folderURL = currentDirectory.appendingPathComponent(name)
        do {
            try await FileSystemService.shared.createDirectory(at: folderURL)
            await loadDirectory()
        } catch {
            self.error = "Failed to create folder: \(error.localizedDescription)"
        }
    }

    func renameItem(_ item: FileItem, to newName: String) async {
        let newURL = item.url.deletingLastPathComponent().appendingPathComponent(newName)
        do {
            try await FileSystemService.shared.moveItem(from: item.url, to: newURL)
            await loadDirectory()
        } catch {
            self.error = "Failed to rename: \(error.localizedDescription)"
        }
    }

    func refresh() async {
        await loadDirectory()
    }

    // MARK: - Drag & Drop

    func performMove(items: [FileItem], to folder: URL, progress: TransferProgress? = nil) async {
        progress?.transferredFileNames = items.map(\.name)
        progress?.transferredFolderNames = Set(items.filter(\.isDirectory).map(\.name))

        var applyToAllChoice: ConflictChoice?
        for (index, item) in items.enumerated() {
            var dest = folder.appendingPathComponent(item.name)
            progress?.beginFile(item.name, size: item.size)

            // Pasting into the folder the file already lives in: source and
            // destination are the same file. "Replace" would delete it and
            // then copy from a path that no longer exists, destroying it for
            // good (deletion here is permanent, not Trash).
            if dest.standardizedFileURL == item.url.standardizedFileURL {
                let msg = "\"\(item.name)\" is already in this folder."
                self.error = msg
                progress?.recordSkip(item.name)
                progress?.completedItems = index + 1
                continue
            }

            if FileManager.default.fileExists(atPath: dest.path) {
                let choice: ConflictChoice
                if let saved = applyToAllChoice {
                    choice = saved
                } else {
                    let remaining = items[(index + 1)...].filter {
                        FileManager.default.fileExists(atPath: folder.appendingPathComponent($0.name).path)
                    }.count
                    let destInfo = Self.localFileInfo(at: dest)
                    let resolution = await withCheckedContinuation { continuation in
                        self.conflictContinuation = continuation
                        self.pendingConflict = PendingConflict(
                            source: ConflictFileInfo(name: item.name, date: item.modificationDate, size: item.size, fileExtension: item.url.pathExtension, localURL: item.url),
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
                    progress?.endTime = Date(); progress?.isComplete = true; await loadDirectory(); return
                case .keepBoth:
                    dest = Self.uniqueDestination(for: dest)
                case .replace: break
                }
            }

            do {
                if FileManager.default.fileExists(atPath: dest.path) {
                    try FileManager.default.removeItem(at: dest)
                }
                progress?.totalBytes += item.size
                try await FileSystemService.shared.moveItem(from: item.url, to: dest)
                progress?.completedItems = index + 1
                progress?.recordSuccess(item.name)
            } catch {
                self.error = "Failed to move \(item.name): \(error.localizedDescription)"
                progress?.recordFailure(item.name, error: error.localizedDescription)
            }
        }
        progress?.endTime = Date()
        progress?.isComplete = true
        await loadDirectory()
    }

    func performCopy(items: [FileItem], to folder: URL, progress: TransferProgress? = nil) async {
        progress?.transferredFileNames = items.map(\.name)
        progress?.transferredFolderNames = Set(items.filter(\.isDirectory).map(\.name))

        var applyToAllChoice: ConflictChoice?
        for (index, item) in items.enumerated() {
            var dest = folder.appendingPathComponent(item.name)
            progress?.beginFile(item.name, size: item.size)

            // Pasting into the folder the file already lives in: source and
            // destination are the same file. "Replace" would delete it and
            // then copy from a path that no longer exists, destroying it for
            // good (deletion here is permanent, not Trash).
            if dest.standardizedFileURL == item.url.standardizedFileURL {
                let msg = "\"\(item.name)\" is already in this folder."
                self.error = msg
                progress?.recordSkip(item.name)
                progress?.completedItems = index + 1
                continue
            }

            if FileManager.default.fileExists(atPath: dest.path) {
                let choice: ConflictChoice
                if let saved = applyToAllChoice {
                    choice = saved
                } else {
                    let remaining = items[(index + 1)...].filter {
                        FileManager.default.fileExists(atPath: folder.appendingPathComponent($0.name).path)
                    }.count
                    let destInfo = Self.localFileInfo(at: dest)
                    let resolution = await withCheckedContinuation { continuation in
                        self.conflictContinuation = continuation
                        self.pendingConflict = PendingConflict(
                            source: ConflictFileInfo(name: item.name, date: item.modificationDate, size: item.size, fileExtension: item.url.pathExtension, localURL: item.url),
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
                    progress?.endTime = Date(); progress?.isComplete = true; await loadDirectory(); return
                case .keepBoth:
                    dest = Self.uniqueDestination(for: dest)
                case .replace: break
                }
            }

            do {
                if FileManager.default.fileExists(atPath: dest.path) {
                    try FileManager.default.removeItem(at: dest)
                }
                progress?.totalBytes += item.size
                try await FileSystemService.shared.copyItem(from: item.url, to: dest)
                progress?.completedItems = index + 1
                progress?.recordSuccess(item.name)
            } catch {
                self.error = "Failed to copy \(item.name): \(error.localizedDescription)"
                progress?.recordFailure(item.name, error: error.localizedDescription)
            }
        }
        progress?.endTime = Date()
        progress?.isComplete = true
        await loadDirectory()
    }

    // MARK: - Archives

    /// Unpacks `items` out of the open archive and hands back real files,
    /// ready to be copied, dropped or previewed. Returns an empty array if
    /// it failed, with `error` set.
    func materialiseFromArchive(
        _ items: [FileItem], progress: TransferProgress? = nil
    ) async -> [URL] {
        guard let session = archiveSession else { return items.map(\.url) }
        progress?.expectedBytesSingle = session.uncompressedSize(of: items)
        let inbox = progress?.byteInbox
        do {
            return try await session.materialise(items, onBytes: { inbox?.addDownload($0) })
        } catch {
            if !(error is CancellationError) {
                self.archiveError = error.localizedDescription
            }
            return []
        }
    }

    /// Packs the selection into the folder on screen, Finder-style: one item
    /// becomes `<name>.<ext>`, several become `Archive.<ext>`.
    func compress(
        _ items: [FileItem],
        format: ArchiveCreationFormat = .zip,
        progress: TransferProgress? = nil
    ) async {
        guard !items.isEmpty, archiveSession == nil else { return }
        let directory = currentDirectory
        let fm = FileManager.default
        let name = format.archiveName(forItemNames: items.map(\.name)) {
            fm.fileExists(atPath: directory.appendingPathComponent($0).path)
        }
        let archiveURL = directory.appendingPathComponent(name)

        progress?.beginFile(archiveURL.lastPathComponent)
        // Counted in the background and applied when it arrives: the file
        // count is only there to drive the progress bar, and walking a
        // 50,000-file folder before the zip even starts would make the wait
        // longer than the job.
        let urls = items.map(\.url)
        Task { [weak progress] in
            let total = await Self.countEntries(in: urls)
            progress?.totalFiles = total
        }

        let inbox = progress?.byteInbox
        do {
            let created = try await ArchiveService.createArchive(
                names: items.map(\.name),
                in: directory,
                archiveName: archiveURL.lastPathComponent,
                format: format,
                onFileAdded: { name in inbox?.addFile(named: name) }
            )
            progress?.recordSuccess(created.lastPathComponent)
        } catch is CancellationError {
            // zip removes its own part-written archive on an interrupt; this
            // is for the case where it got as far as the final rename.
            try? FileManager.default.removeItem(at: archiveURL)
            progress?.recordSkip(archiveURL.lastPathComponent)
        } catch {
            self.archiveError = error.localizedDescription
            progress?.recordFailure(archiveURL.lastPathComponent, error: error.localizedDescription)
        }
        progress?.endTime = Date()
        progress?.isComplete = true
        await loadDirectory()
    }

    /// Unpacks an archive into `directory`, Finder-style: an archive holding
    /// a single top-level item leaves that item there, anything else goes
    /// into a folder named after the archive.
    ///
    /// Everything lands in a working folder first and is moved into place
    /// afterwards. A failure or a cancel halfway through then leaves nothing
    /// behind, and the move is a same-volume rename, so it costs nothing.
    func extractArchive(
        _ item: FileItem, into directory: URL, progress: TransferProgress? = nil
    ) async {
        guard let format = ArchiveFormat.detect(item.url) else { return }
        progress?.beginFile(item.name, size: item.size)
        do {
            let tree = try await ArchiveService.list(item.url, format: format)
            progress?.expectedBytesSingle = tree.totalSize
            progress?.totalFiles = tree.fileCount
            let inbox = progress?.byteInbox
            let destination = try await Self.unpack(
                archive: item.url, named: item.name, format: format, into: directory,
                isTaken: { FileManager.default.fileExists(atPath: directory.appendingPathComponent($0).path) },
                onBytes: { inbox?.addDownload($0) }
            )
            progress?.recordSuccess(destination.lastPathComponent)
            SupportLogger.shared.log(
                "extracted \(item.name) to \(destination.lastPathComponent)", category: "archive"
            )
        } catch {
            if error is CancellationError {
                progress?.recordSkip(item.name)
            } else {
                self.archiveError = error.localizedDescription
                progress?.recordFailure(item.name, error: error.localizedDescription)
            }
        }
        progress?.endTime = Date()
        progress?.isComplete = true
        await loadDirectory()
    }

    /// Unpacks `archive` into `directory` by Finder's rule — an archive
    /// holding one top-level item leaves that item there, anything else goes
    /// into a folder named after the archive — and returns what landed.
    ///
    /// Everything is written to a working folder first and moved into place
    /// afterwards, so a failure or a cancel halfway through leaves nothing
    /// behind, and the move is a same-volume rename that costs nothing.
    /// `isTaken` decides whether a name in `directory` is in use, which lets
    /// a caller that will upload the result check against a cloud folder
    /// rather than the disk.
    nonisolated static func unpack(
        archive: URL,
        named archiveName: String,
        format: ArchiveFormat,
        into directory: URL,
        isTaken: @escaping @Sendable (String) -> Bool,
        onBytes: (@Sendable (Int64) -> Void)? = nil
    ) async throws -> URL {
        let fm = FileManager.default
        let work = directory.appendingPathComponent(
            ".FileFluss-extract-\(UUID().uuidString)", isDirectory: true
        )
        do {
            try fm.createDirectory(at: work, withIntermediateDirectories: true)
            try await ArchiveService.extractAll(
                from: archive, format: format, into: work, onBytes: onBytes
            )
            let roots = try fm.contentsOfDirectory(at: work, includingPropertiesForKeys: nil)
            let preferred = roots.count == 1
                ? roots[0].lastPathComponent
                : ArchiveFormat.baseName(ofArchive: archiveName)
            let destination = directory.appendingPathComponent(
                availableName(preferred, isTaken: isTaken)
            )
            if roots.count == 1 {
                try fm.moveItem(at: roots[0], to: destination)
                try? fm.removeItem(at: work)
            } else {
                try fm.moveItem(at: work, to: destination)
            }
            return destination
        } catch {
            try? fm.removeItem(at: work)
            throw error
        }
    }

    /// `name` itself when `isTaken` says it is free, otherwise the first free
    /// " 2", " 3" … variant, numbered before the extension.
    nonisolated static func availableName(_ name: String, isTaken: (String) -> Bool) -> String {
        guard isTaken(name) else { return name }
        let ext = (name as NSString).pathExtension
        let stem = (name as NSString).deletingPathExtension
        var counter = 2
        while true {
            let candidate = ext.isEmpty ? "\(stem) \(counter)" : "\(stem) \(counter).\(ext)"
            if !isTaken(candidate) { return candidate }
            counter += 1
        }
    }

    /// Unpacks an archive into a staging folder and hands back its top-level
    /// items, for a destination FileFluss has to upload to rather than write
    /// to directly. The caller owns the staging folder and deletes it.
    func extractArchiveToStaging(
        _ item: FileItem, progress: TransferProgress? = nil
    ) async -> (items: [URL], staging: URL)? {
        guard let format = ArchiveFormat.detect(item.url) else { return nil }
        let staging = StagingLocation.stagingDirectory(prefix: "extract")
        progress?.beginFile(item.name, size: item.size)
        do {
            let tree = try await ArchiveService.list(item.url, format: format)
            progress?.expectedBytesDownload = tree.totalSize
            progress?.expectedBytesUpload = tree.totalSize
            let inbox = progress?.byteInbox
            try await ArchiveService.extractAll(
                from: item.url, format: format, into: staging,
                onBytes: { inbox?.addDownload($0) }
            )
            let contents = try FileManager.default.contentsOfDirectory(
                at: staging, includingPropertiesForKeys: nil
            )
            return (contents, staging)
        } catch {
            try? FileManager.default.removeItem(at: staging)
            if !(error is CancellationError) {
                self.archiveError = error.localizedDescription
                progress?.recordFailure(item.name, error: error.localizedDescription)
            }
            return nil
        }
    }

    /// Files and folders at or below `urls`, for a progress bar that needs a
    /// denominator. Folders count because every archiver reports a line for
    /// each folder it adds as well as each file. Metadata only — nothing is
    /// read.
    static func countEntries(in urls: [URL]) async -> Int {
        await Task.detached(priority: .utility) {
            let fm = Foundation.FileManager.default
            var count = 0
            for url in urls {
                if Task.isCancelled { return count }
                count += 1
                let values = try? url.resourceValues(forKeys: [.isDirectoryKey])
                guard values?.isDirectory == true else { continue }
                let walker = fm.enumerator(at: url, includingPropertiesForKeys: nil, options: [])
                while walker?.nextObject() != nil {
                    if Task.isCancelled { return count }
                    count += 1
                }
            }
            return count
        }.value
    }

    // MARK: - Quick Look

    func toggleQuickLook() {
        let files = selectedItems.filter { !$0.isDirectory }
        guard archiveSession != nil else {
            quickLookController.urls = files.map(\.url)
            quickLookController.toggle()
            return
        }
        // Nothing inside an archive exists on disk yet, and Quick Look can
        // only preview a real file.
        Task {
            quickLookController.urls = await materialiseFromArchive(files)
            quickLookController.toggle()
        }
    }

    func updateQuickLookSelection() {
        let files = selectedItems.filter { !$0.isDirectory }
        guard archiveSession != nil else {
            quickLookController.updateAndReload(urls: files.map(\.url))
            return
        }
        guard quickLookController.isPanelVisible else { return }
        Task {
            quickLookController.updateAndReload(urls: await materialiseFromArchive(files))
        }
    }

    // MARK: - Private

    private func pushToHistory(_ url: URL) {
        if pathHistoryIndex < pathHistory.count - 1 {
            pathHistory.removeSubrange((pathHistoryIndex + 1)...)
        }
        pathHistory.append(url)
        pathHistoryIndex = pathHistory.count - 1
    }

    static func localFileInfo(at url: URL) -> ConflictFileInfo {
        let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey, .totalFileSizeKey])
        return ConflictFileInfo(
            name: url.lastPathComponent,
            date: values?.contentModificationDate ?? .distantPast,
            size: Int64(values?.totalFileSize ?? values?.fileSize ?? 0),
            fileExtension: url.pathExtension,
            localURL: url
        )
    }

    /// `url` itself when nothing is there, otherwise the next free
    /// " 2", " 3" … variant.
    ///
    /// Distinct from `uniqueDestination`, which always skips straight to a
    /// variant because its callers have already found a conflict. A new
    /// archive or a freshly extracted folder has no conflict to start with,
    /// and naming the first one "Sample 2" is just wrong.
    static func availableDestination(for url: URL) -> URL {
        FileManager.default.fileExists(atPath: url.path) ? uniqueDestination(for: url) : url
    }

    static func uniqueDestination(for url: URL) -> URL {
        let fm = FileManager.default
        let dir = url.deletingLastPathComponent()
        let name = url.deletingPathExtension().lastPathComponent
        let ext = url.pathExtension
        var counter = 2
        while true {
            let candidate: URL
            if ext.isEmpty {
                candidate = dir.appendingPathComponent("\(name) \(counter)")
            } else {
                candidate = dir.appendingPathComponent("\(name) \(counter).\(ext)")
            }
            if !fm.fileExists(atPath: candidate.path) { return candidate }
            counter += 1
        }
    }

    private func sorted(_ items: [FileItem]) -> [FileItem] {
        // See the cloud twin: `kind` is a type-database lookup, and a
        // comparator calls it ~2·n·log n times. Resolve once per item,
        // memoised by extension.
        if sortOrder == .kind {
            var kindByExtension: [String: String] = [:]
            let decorated: [(item: FileItem, kind: String)] = items.map { item in
                if item.isDirectory { return (item, item.kind) }
                let ext = item.url.pathExtension.lowercased()
                if let cached = kindByExtension[ext] { return (item, cached) }
                let kind = item.kind
                kindByExtension[ext] = kind
                return (item, kind)
            }
            let sortedItems = decorated.sorted { a, b in
                if a.item.isDirectory != b.item.isDirectory {
                    return a.item.isDirectory
                }
                let result = a.kind.localizedStandardCompare(b.kind) == .orderedAscending
                return sortAscending ? result : !result
            }
            return sortedItems.map(\.item)
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
            case .dateCreated:
                result = a.creationDate < b.creationDate
            case .size:
                result = a.size < b.size
            case .kind:
                result = a.kind.localizedStandardCompare(b.kind) == .orderedAscending
            }
            return sortAscending ? result : !result
        }
    }
}
