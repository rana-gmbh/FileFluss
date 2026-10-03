import Foundation
import FileFlussCore

/// Indexes a drive's filesystem or a cloud account's tree into `SearchIndex`
/// so the files remain searchable while the source is offline.
///
/// Each call creates a tracked `Job` exposed in `jobs`; the UI can show
/// progress and offer cancellation. Indexing runs in a `Task.detached` so
/// the recursive walk doesn't block the main thread.
@Observable @MainActor
final class IndexingService {
    static let shared = IndexingService()

    /// Rows per write. Large enough that the per-transaction cost is noise
    /// against a million-file drive, small enough that memory stays flat
    /// however big the source is.
    static let writeBatchSize = 10_000

    @Observable @MainActor
    final class Job: Identifiable {
        let id = UUID()
        let sourceId: String
        let displayName: String
        let kind: Kind

        enum Kind: String { case drive, cloud }

        var filesProcessed: Int = 0
        var currentPath: String = ""
        var isComplete: Bool = false
        var errorMessage: String?
        var task: Task<Void, Never>?

        init(sourceId: String, displayName: String, kind: Kind) {
            self.sourceId = sourceId
            self.displayName = displayName
            self.kind = kind
        }
    }

    private(set) var jobs: [Job] = []

    private init() {}

    // MARK: - Public

    /// Start (or restart) indexing for a mounted external/network drive.
    /// If a job for the same sourceId is already running it is cancelled
    /// first so the new walk replaces it.
    func indexDrive(_ drive: Drive, mountURL: URL) {
        cancel(sourceId: drive.id)
        let job = Job(sourceId: drive.id, displayName: drive.displayName, kind: .drive)
        jobs.append(job)
        let sourceId = drive.id
        let displayName = drive.displayName
        let rootPath = mountURL.path
        job.task = Task.detached(priority: .utility) { [weak self] in
            await Self.walkLocal(
                sourceId: sourceId,
                displayName: displayName,
                kind: "drive-\(drive.kind.rawValue)",
                rootPath: rootPath,
                jobId: job.id,
                service: self
            )
        }
    }

    /// Index a connected cloud account into the cloud_files cache, so it
    /// remains searchable while the account is disconnected.
    func indexCloudAccount(_ account: CloudAccount) {
        cancel(sourceId: account.id.uuidString)
        let job = Job(
            sourceId: account.id.uuidString,
            displayName: account.displayName,
            kind: .cloud
        )
        jobs.append(job)
        let accountId = account.id
        job.task = Task.detached(priority: .utility) { [weak self] in
            await Self.walkCloud(
                accountId: accountId,
                rootPath: account.rootPath.isEmpty ? "/" : account.rootPath,
                jobId: job.id,
                service: self
            )
        }
    }

    /// Cancel any running job for the given sourceId. The job stays in the
    /// list so the UI can surface the cancelled state until removed.
    func cancel(sourceId: String) {
        for job in jobs where job.sourceId == sourceId && !job.isComplete {
            job.task?.cancel()
        }
    }

    func remove(jobId: UUID) {
        if let idx = jobs.firstIndex(where: { $0.id == jobId }) {
            jobs[idx].task?.cancel()
            jobs.remove(at: idx)
        }
    }

    func activeJob(sourceId: String) -> Job? {
        jobs.first { $0.sourceId == sourceId && !$0.isComplete }
    }

    // MARK: - Local walk

    private static func walkLocal(
        sourceId: String,
        displayName: String,
        kind: String,
        rootPath: String,
        jobId: UUID,
        service: IndexingService?
    ) async {
        let rootURL = URL(fileURLWithPath: rootPath, isDirectory: true)
        // Rows go to the index in batches as the walk finds them, rather
        // than being held until the end: a 179,000-file share was keeping
        // every entry in memory for eight minutes before any of it was
        // written. Staged rows are invisible until the whole walk is in,
        // so an interrupted index never replaces a good one.
        var batch: [SearchIndex.IndexedFile] = []
        batch.reserveCapacity(Self.writeBatchSize)
        var requested = 0
        var rejected = 0
        var writeFailure: String?

        await SearchIndex.shared.discardStagedFiles(sourceId: sourceId)

        let keys: Set<URLResourceKey> = [
            .isDirectoryKey, .fileSizeKey, .contentModificationDateKey, .isPackageKey
        ]
        let enumerator = FileManager.default.enumerator(
            at: rootURL,
            includingPropertiesForKeys: Array(keys),
            options: [.skipsHiddenFiles, .producesRelativePathURLs],
            errorHandler: { _, _ in true }
        )

        var processed = 0
        var lastReport = 0

        while let url = enumerator?.nextObject() as? URL {
            if Task.isCancelled { break }
            let values = try? url.resourceValues(forKeys: keys)
            let isDir = values?.isDirectory ?? false
            let size = Int64(values?.fileSize ?? 0)
            let modDate = values?.contentModificationDate ?? .distantPast

            // Store paths relative to the root so they're stable across
            // remounts (mount path can change on macOS — /Volumes/Foo vs
            // /Volumes/Foo 1 when the same name conflicts).
            let absolute = url.path
            guard absolute.hasPrefix(rootPath) else { continue }
            var rel = String(absolute.dropFirst(rootPath.count))
            if !rel.hasPrefix("/") { rel = "/" + rel }
            let parent = (rel as NSString).deletingLastPathComponent
            let name = (rel as NSString).lastPathComponent

            batch.append(SearchIndex.IndexedFile(
                sourceId: sourceId,
                path: rel,
                parentPath: parent.isEmpty ? "/" : parent,
                name: name,
                isDirectory: isDir,
                size: size,
                modificationDate: modDate
            ))
            processed += 1

            if batch.count >= Self.writeBatchSize {
                let toWrite = batch
                batch.removeAll(keepingCapacity: true)
                do {
                    rejected += try await SearchIndex.shared.stageFiles(toWrite, sourceId: sourceId)
                    requested += toWrite.count
                } catch {
                    writeFailure = error.localizedDescription
                    break
                }
            }
            if processed - lastReport >= 250 {
                lastReport = processed
                let snapshot = processed
                let pathSnap = rel
                await MainActor.run {
                    if let job = service?.jobs.first(where: { $0.id == jobId }) {
                        job.filesProcessed = snapshot
                        job.currentPath = pathSnap
                    }
                }
            }
        }

        let cancelled = Task.isCancelled
        var stored: Int?

        // Whatever the walk ended with, still unwritten.
        if !cancelled, writeFailure == nil, !batch.isEmpty {
            do {
                rejected += try await SearchIndex.shared.stageFiles(batch, sourceId: sourceId)
                requested += batch.count
            } catch {
                writeFailure = error.localizedDescription
            }
        }

        if cancelled || writeFailure != nil {
            // Nothing staged becomes the index: a cancelled or failed run
            // leaves the previous one exactly as it was.
            await SearchIndex.shared.discardStagedFiles(sourceId: sourceId)
        } else {
            do {
                let result = try await SearchIndex.shared.promoteStagedFiles(
                    sourceId: sourceId,
                    kind: kind,
                    displayName: displayName,
                    requested: requested,
                    rejected: rejected
                )
                stored = result.stored
                SupportLogger.shared.log(
                    "Indexed \(displayName): \(result.stored) of \(result.requested) entries stored" +
                    (result.rejected > 0 ? ", \(result.rejected) rejected" : ""),
                    category: "Indexing"
                )
                // The count the index actually holds, not the count we
                // walked. They differ when rows are rejected, and the
                // sidebar should say what is searchable.
                if !result.isComplete {
                    SupportLogger.shared.log(
                        "Index for \(displayName) is short of the walk: walked \(result.requested), stored \(result.stored)",
                        category: "Indexing",
                        level: .error
                    )
                }
            } catch {
                writeFailure = error.localizedDescription
                await SearchIndex.shared.discardStagedFiles(sourceId: sourceId)
            }
        }

        if let writeFailure {
            SupportLogger.shared.log(
                "Indexing \(displayName) failed to write: \(writeFailure)",
                category: "Indexing",
                level: .error
            )
        }

        let finalCount = processed
        let failure = writeFailure
        let storedCount = stored
        await MainActor.run {
            guard let job = service?.jobs.first(where: { $0.id == jobId }) else { return }
            job.filesProcessed = finalCount
            job.isComplete = true
            if cancelled {
                job.errorMessage = "Cancelled"
                return
            }
            if let failure {
                // Nothing was stored, so nothing is claimed: the drive keeps
                // whatever it said before. Reporting "indexed N files" after
                // a failed write is what sent a user looking for a database
                // that had never been written.
                job.errorMessage = "Could not save the index: \(failure)"
                return
            }
            // Update the drive's persisted metadata, with the number the
            // index holds.
            if var d = DriveMonitor.shared.drives.first(where: { $0.id == sourceId }) {
                d.lastIndexed = Date()
                d.totalFiles = storedCount ?? finalCount
                DriveMonitor.shared.upsert(d)
            }
        }
    }

    // MARK: - Cloud walk

    private static func walkCloud(
        accountId: UUID,
        rootPath: String,
        jobId: UUID,
        service: IndexingService?
    ) async {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else {
            await MainActor.run {
                if let job = service?.jobs.first(where: { $0.id == jobId }) {
                    job.isComplete = true
                    job.errorMessage = "Account not connected"
                }
            }
            return
        }

        var pending: [String] = [rootPath]
        var processed = 0
        var lastReport = 0

        while !pending.isEmpty {
            if Task.isCancelled { break }
            let path = pending.removeFirst()
            let items: [CloudFileItem]
            do {
                items = try await provider.listDirectory(at: path)
            } catch {
                continue
            }
            if items.isEmpty { continue }
            do {
                try await SearchIndex.shared.upsertItems(items, accountId: accountId)
            } catch {
                SupportLogger.shared.log(
                    "Indexing failed to write \(items.count) entries: \(error.localizedDescription)",
                    category: "Indexing",
                    level: .error
                )
                await MainActor.run {
                    if let job = service?.jobs.first(where: { $0.id == jobId }) {
                        job.isComplete = true
                        job.errorMessage = "Could not save the index: \(error.localizedDescription)"
                    }
                }
                return
            }
            for item in items {
                if item.isDirectory { pending.append(item.path) }
                processed += 1
            }
            if processed - lastReport >= 100 {
                lastReport = processed
                let snapshot = processed
                let pathSnap = path
                await MainActor.run {
                    if let job = service?.jobs.first(where: { $0.id == jobId }) {
                        job.filesProcessed = snapshot
                        job.currentPath = pathSnap
                    }
                }
            }
        }

        let cancelled = Task.isCancelled
        let finalCount = processed
        if !cancelled {
            // Mirror per-account state into indexed_sources so the Settings
            // panel and the right-click context menu have a single source
            // of truth for "what's been indexed and when."
            if let summary = await SearchIndex.shared.cloudAccountSummary(accountId: accountId) {
                let displayName = await MainActor.run {
                    service?.jobs.first(where: { $0.id == jobId })?.displayName ?? "Cloud Account"
                }
                await SearchIndex.shared.recordCloudSource(
                    accountId: accountId,
                    displayName: displayName,
                    summary: summary
                )
            }
        }
        let finalAccountId = accountId
        await MainActor.run {
            if let job = service?.jobs.first(where: { $0.id == jobId }) {
                job.filesProcessed = finalCount
                job.isComplete = true
                if cancelled {
                    job.errorMessage = "Cancelled"
                }
            }
        }
        if !cancelled {
            // Refresh AppState's cached cloudIndexInfo via the singleton
            // observer pattern. We can't reach AppState directly from this
            // service, so we post a notification the app listens for.
            await MainActor.run {
                NotificationCenter.default.post(
                    name: .indexingDidFinish,
                    object: nil,
                    userInfo: ["accountId": finalAccountId]
                )
            }
        }
    }
}

extension Notification.Name {
    static let indexingDidFinish = Notification.Name("FileFluss.indexingDidFinish")
}
