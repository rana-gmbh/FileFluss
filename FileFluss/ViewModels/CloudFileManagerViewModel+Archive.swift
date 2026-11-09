import Foundation
import FileFlussCore

extension CloudFileManagerViewModel {

    /// Packs a cloud selection into an archive in the same cloud folder.
    ///
    /// No provider can build an archive on its own servers, so this is the
    /// same three steps a person would take: download the selection to a
    /// staging folder, pack it there, upload the archive back beside the
    /// originals. The transfer row runs it as a two-phase job — download,
    /// then upload — the way a cloud-to-cloud copy already shows, with the
    /// packing step in between under the archive's own name.
    func compress(
        _ selection: [CloudFileItem],
        format: ArchiveCreationFormat,
        progress: TransferProgress? = nil
    ) async {
        guard !selection.isEmpty else { return }
        let staging = StagingLocation.stagingDirectory(prefix: "compress")
        let source = staging.appendingPathComponent("source", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: staging) }

        let takenNames = Set(items.map(\.name))
        let archiveName = format.archiveName(forItemNames: selection.map(\.name)) {
            takenNames.contains($0)
        }

        func finish(failure: String? = nil) {
            if let failure {
                progress?.errorMessage = failure
                progress?.recordFailure(archiveName, error: failure)
                SupportLogger.shared.log("cloud compress failed: \(failure)", category: "archive", level: .error)
            }
            progress?.endTime = Date()
            progress?.isComplete = true
        }

        do {
            try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        } catch {
            finish(failure: error.localizedDescription)
            return
        }

        // Phase 1: download. `isCloudToCloud` is what keeps `downloadItems`
        // and `uploadFiles` from each marking the job finished when only
        // their own half is done.
        progress?.isCloudToCloud = true
        progress?.currentPhase = .downloading
        progress?.downloadStartTime = Date()
        progress?.expectedBytesDownload = await expectedSize(of: selection)
        await downloadItems(selection, to: source, progress: progress, skipConflictCheck: true)
        progress?.downloadEndTime = Date()
        if progress?.isCancelled == true || Task.isCancelled {
            finish()
            return
        }

        // Read back what actually arrived rather than predicting names: a
        // provider may sanitise a name on the way down, or append an
        // extension to a converted Google document.
        let downloaded = (try? FileManager.default.contentsOfDirectory(atPath: source.path)) ?? []
        guard downloaded.count == selection.count else {
            finish(failure: L10n.text("Not every selected item could be downloaded, so no archive was made."))
            return
        }

        // Phase 2: pack.
        progress?.beginFile(archiveName)
        let inbox = progress?.byteInbox
        let archive: URL
        do {
            archive = try await ArchiveService.createArchive(
                names: downloaded.sorted(),
                in: source,
                archiveName: archiveName,
                format: format,
                onFileAdded: { name in inbox?.addFile(named: name) }
            )
        } catch is CancellationError {
            finish()
            return
        } catch {
            finish(failure: error.localizedDescription)
            return
        }

        // Phase 3: upload the archive alone, next to the originals.
        let archiveSize = (try? archive.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0
        progress?.currentPhase = .uploading
        progress?.uploadStartTime = Date()
        progress?.expectedBytesUpload = Int64(archiveSize)
        await uploadFiles(from: [archive], progress: progress, skipConflictCheck: true)
        progress?.uploadEndTime = Date()

        if progress?.isCancelled != true, progress?.failureCount == 0 {
            progress?.recordSuccess(archiveName)
        }
        finish()
    }

    /// Bytes the download phase will move, so its half of the bar is
    /// weighted by size. A folder's size costs the provider a walk; one that
    /// can't say contributes nothing and the bar falls back to counts.
    private func expectedSize(of selection: [CloudFileItem]) async -> Int64 {
        guard let provider = await SyncEngine.shared.provider(for: accountId) else { return 0 }
        var total: Int64 = 0
        for item in selection {
            if item.isDirectory {
                total += (try? await provider.folderSize(at: item.path)) ?? 0
            } else {
                total += item.size
            }
        }
        return total
    }
}

// MARK: - Browsing an archive stored on the account

extension CloudFileManagerViewModel {

    var isBrowsingArchive: Bool { archiveSession != nil }

    /// Where inside the open archive `remotePath` points, or nil when it
    /// points outside it.
    func archiveInnerPath(for remotePath: String) -> String? {
        guard let root = archiveRemotePath else { return nil }
        if remotePath == root { return "" }
        guard remotePath.hasPrefix(root + "/") else { return nil }
        return String(remotePath.dropFirst(root.count + 1))
    }

    /// The panel's rows for a folder inside the archive.
    func archiveRows(at inner: String) -> [CloudFileItem] {
        guard let session = archiveSession, let root = archiveRemotePath else { return [] }
        return session.entries(at: inner, showHidden: showHiddenFiles).map { entry in
            let path = "\(root)/\(entry.path)"
            return CloudFileItem(
                id: path,
                name: entry.name,
                path: path,
                isDirectory: entry.isDirectory,
                // A folder's row shows what unpacking it would cost.
                size: entry.isDirectory ? session.tree.recursiveSize(of: entry.path) : entry.size,
                modificationDate: entry.modificationDate,
                // No checksum: it would have to be comparable with what
                // providers report, and nothing inside an archive is.
                checksum: nil
            )
        }
    }

    /// Opens the archive `item` in the panel. Downloads it once into the
    /// preview cache, where a second visit finds it already. Returns false
    /// when it can't be read as an archive, so the caller can open it the
    /// ordinary way.
    @discardableResult
    func openArchive(_ item: CloudFileItem, format: ArchiveFormat, progress: TransferProgress? = nil) async -> Bool {
        progress?.beginFile(item.name, size: item.size)
        guard let local = await downloadToTemp(item) else {
            archiveError = L10n.format("\"%@\" could not be downloaded.", item.name)
            return true
        }
        do {
            let tree = try await ArchiveService.list(local, format: format)
            guard !tree.isEmpty else { return false }
            closeArchive()
            archiveSession = ArchiveBrowseSession(archiveURL: local, format: format, tree: tree)
            archiveRemotePath = item.path
            currentPath = item.path
            pushToHistory(item.path)
            items = archiveRows(at: "")
            selectedItemIDs.removeAll()
            error = nil
            SupportLogger.shared.log(
                "opened cloud archive \(item.name) — \(tree.entries.count) entries",
                category: "archive"
            )
            return true
        } catch {
            if let archiveError = error as? ArchiveError, case .unreadable = archiveError {
                return false
            }
            self.archiveError = error.localizedDescription
            return true
        }
    }

    func closeArchive() {
        archiveSession?.close()
        archiveSession = nil
        archiveRemotePath = nil
    }

    /// Unpacks a selection from the open archive to real files.
    func materialiseFromArchive(_ selection: [CloudFileItem], progress: TransferProgress? = nil) async -> [URL] {
        guard let session = archiveSession else { return [] }
        let inner = selection.compactMap { archiveInnerPath(for: $0.path) }
        progress?.expectedBytesSingle = selection.reduce(0) { $0 + $1.size }
        let inbox = progress?.byteInbox
        do {
            return try await session.materialise(innerPaths: inner, onBytes: { inbox?.addDownload($0) })
        } catch {
            if !(error is CancellationError) { archiveError = error.localizedDescription }
            return []
        }
    }
}

// MARK: - Extracting an archive stored on the account

extension CloudFileManagerViewModel {

    /// Where an extracted archive should end up.
    enum ExtractDestination {
        /// Back into this account, beside the archive.
        case here
        /// A folder on this Mac.
        case local(URL)
        /// Another cloud panel's folder.
        case cloud(CloudFileManagerViewModel)
    }

    /// Downloads `archive`, unpacks it by Finder's rule, and puts the result
    /// where it was asked to go. Cloud destinations get the unpacked
    /// contents uploaded; a local one gets them moved straight in.
    func extract(_ archive: CloudFileItem, to destination: ExtractDestination, progress: TransferProgress? = nil) async {
        guard let format = ArchiveFormat.detect(fileName: archive.name) else { return }
        let staging = StagingLocation.stagingDirectory(prefix: "extract")
        defer { try? FileManager.default.removeItem(at: staging) }

        func finish(failure: String? = nil) {
            if let failure {
                progress?.errorMessage = failure
                progress?.recordFailure(archive.name, error: failure)
                archiveError = failure
            }
            progress?.endTime = Date()
            progress?.isComplete = true
        }

        // Phase 1: the archive itself.
        progress?.isCloudToCloud = true
        progress?.currentPhase = .downloading
        progress?.downloadStartTime = Date()
        progress?.expectedBytesDownload = archive.size
        let download = staging.appendingPathComponent("archive", isDirectory: true)
        try? FileManager.default.createDirectory(at: download, withIntermediateDirectories: true)
        await downloadItems([archive], to: download, progress: progress, skipConflictCheck: true)
        progress?.downloadEndTime = Date()
        if progress?.isCancelled == true || Task.isCancelled { finish(); return }
        guard let local = (try? FileManager.default.contentsOfDirectory(
            at: download, includingPropertiesForKeys: nil
        ))?.first else {
            finish(failure: L10n.format("\"%@\" could not be downloaded.", archive.name))
            return
        }

        // Phase 2: unpack.
        progress?.beginFile(archive.name)
        let inbox = progress?.byteInbox
        let unpackDir: URL
        let isTaken: @Sendable (String) -> Bool
        switch destination {
        case .local(let directory):
            unpackDir = directory
            isTaken = { FileManager.default.fileExists(atPath: directory.appendingPathComponent($0).path) }
        case .here:
            unpackDir = staging.appendingPathComponent("out", isDirectory: true)
            let taken = Set(items.map(\.name))
            isTaken = { taken.contains($0) }
        case .cloud(let other):
            unpackDir = staging.appendingPathComponent("out", isDirectory: true)
            let taken = Set(other.items.map(\.name))
            isTaken = { taken.contains($0) }
        }
        let result: URL
        do {
            try FileManager.default.createDirectory(at: unpackDir, withIntermediateDirectories: true)
            result = try await FileManagerViewModel.unpack(
                archive: local, named: archive.name, format: format,
                into: unpackDir, isTaken: isTaken,
                onBytes: { inbox?.addUpload($0) }
            )
        } catch is CancellationError {
            finish(); return
        } catch {
            finish(failure: error.localizedDescription); return
        }

        // Phase 3: upload, unless it already landed where it belongs.
        progress?.currentPhase = .uploading
        progress?.uploadStartTime = Date()
        switch destination {
        case .local:
            progress?.recordSuccess(result.lastPathComponent)
        case .here:
            progress?.expectedBytesUpload = Self.localSizeRecursively(url: result)
            await uploadFiles(from: [result], progress: progress, skipConflictCheck: true)
        case .cloud(let other):
            progress?.expectedBytesUpload = Self.localSizeRecursively(url: result)
            await other.uploadFiles(from: [result], progress: progress, skipConflictCheck: true)
        }
        progress?.uploadEndTime = Date()
        finish()
    }
}
