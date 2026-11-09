import Foundation
import FileFlussCore

/// Executes a SyncPlan by performing the requested operations against the two endpoints.
/// Reports progress by mutating the supplied TransferProgress on the main actor.
@MainActor
enum SyncExecutor {
    static func execute(plan: SyncPlan,
                        source: SyncEndpoint,
                        destination: SyncEndpoint,
                        progress: TransferProgress) async {
        progress.totalFiles = plan.filesToAdd + plan.filesToReplace
        progress.totalBytes = plan.totalBytes
        progress.expectedBytesSingle = plan.downloadBytes + plan.uploadBytes > 0
            ? plan.downloadBytes + plan.uploadBytes
            : plan.totalBytes
        progress.isCloudDownload = source.isCloud && !destination.isCloud
        progress.isCloudUpload = !source.isCloud && destination.isCloud
        progress.isCloudToCloud = source.isCloud && destination.isCloud
        if progress.isCloudToCloud {
            progress.expectedBytesDownload = plan.downloadBytes
            progress.expectedBytesUpload = plan.uploadBytes
            progress.expectedBytesSingle = 0
        }

        // Posted straight to the inbox, which the transfer drains a few
        // times a second — one main-actor hop per network chunk was costing
        // more than the number is worth.
        let inbox = progress.byteInbox
        let onDownloadBytes: @Sendable (Int64) -> Void = { bytes in
            inbox.addDownload(bytes)
        }
        let onUploadBytes: @Sendable (Int64) -> Void = { bytes in
            inbox.addUpload(bytes)
        }

        // Checked here as well as in the planner window: a GoPro's delete
        // works and its upload doesn't, so a sync toward one would only ever
        // destroy footage.
        if case .cloud(let accountId, _) = destination,
           let provider = await SyncEngine.shared.provider(for: accountId),
           provider.providerType.isReadOnly {
            let error = SyncPlannerError.destinationReadOnly(provider.providerType.displayName)
            progress.errorMessage = error.localizedDescription
            progress.endTime = Date()
            progress.isComplete = true
            return
        }

        for op in plan.operations {
            if progress.isCancelled || Task.isCancelled { break }
            let opName = displayName(for: op)
            progress.beginFile(opName)
            do {
                try await perform(op, modificationDates: plan.sourceModificationDates,
                                  source: source, destination: destination,
                                  onDownloadBytes: onDownloadBytes, onUploadBytes: onUploadBytes)
                progress.completedItems += 1
                if case .add(_, let isDir, _) = op, !isDir {
                    progress.transferredFileNames.append(opName)
                    progress.recordSuccess(opName)
                } else if case .replace = op {
                    progress.transferredFileNames.append(opName)
                    progress.recordSuccess(opName)
                } else if case .addRenamed = op {
                    progress.transferredFileNames.append(opName)
                    progress.recordSuccess(opName)
                } else {
                    // Folder creation / deletion — counts as success but
                    // doesn't get listed under transferred file names.
                    progress.recordSuccess(opName)
                }
            } catch {
                progress.recordFailure(opName, error: error.localizedDescription)
            }
        }

        progress.completedItems = plan.operations.count
        progress.clearCurrentFile()
        progress.endTime = Date()
        progress.isComplete = true
    }

    private static func displayName(for op: SyncOperation) -> String {
        switch op {
        case .add(let p, _, _): return p
        case .replace(let p, _): return p
        case .addRenamed(_, let dest, _): return dest
        case .delete(let p, _, _): return p
        }
    }

    private static func perform(_ op: SyncOperation,
                                modificationDates: [String: Date],
                                source: SyncEndpoint,
                                destination: SyncEndpoint,
                                onDownloadBytes: @escaping @Sendable (Int64) -> Void,
                                onUploadBytes: @escaping @Sendable (Int64) -> Void) async throws {
        switch op {
        case .add(let rel, let isDir, _):
            if isDir {
                try await createDirectory(at: rel, on: destination)
            } else {
                try await copyFile(sourceRelative: rel, destRelative: rel,
                                   modificationDate: modificationDates[rel],
                                   source: source, destination: destination,
                                   onDownloadBytes: onDownloadBytes, onUploadBytes: onUploadBytes)
            }
        case .replace(let rel, _):
            try await replaceFile(at: rel, modificationDate: modificationDates[rel],
                                  source: source, destination: destination,
                                  onDownloadBytes: onDownloadBytes, onUploadBytes: onUploadBytes)
        case .addRenamed(let srcRel, let destRel, _):
            try await copyFile(sourceRelative: srcRel, destRelative: destRel,
                               modificationDate: modificationDates[srcRel],
                               source: source, destination: destination,
                               onDownloadBytes: onDownloadBytes, onUploadBytes: onUploadBytes)
        case .delete(let rel, let isDir, _):
            if isDir {
                try await deleteDirectory(at: rel, on: destination)
            } else {
                try await deleteFile(at: rel, on: destination)
            }
        }
    }

    // MARK: - Endpoint primitives

    /// Copies the source's version beside the destination's and swaps it in
    /// once it's complete. Deleting first meant a failed copy, for any reason
    /// from a lost connection to a file over the provider's size limit, lost
    /// the destination's version for nothing.
    private static func replaceFile(at rel: String,
                                    modificationDate: Date?,
                                    source: SyncEndpoint,
                                    destination: SyncEndpoint,
                                    onDownloadBytes: @escaping @Sendable (Int64) -> Void,
                                    onUploadBytes: @escaping @Sendable (Int64) -> Void) async throws {
        /// `rel`, in the same folder but under another name.
        func sibling(named name: String) -> String {
            let parent = (rel as NSString).deletingLastPathComponent
            return parent.isEmpty ? name : "\(parent)/\(name)"
        }

        switch destination {
        case .local(let root):
            let target = try SafeLocalPath.destination(forRelativePath: rel, in: root)
            let temporaryRel = sibling(named: FileSystemService.temporaryReplacementName(for: target.lastPathComponent))
            do {
                try await copyFile(sourceRelative: rel, destRelative: temporaryRel,
                                   modificationDate: modificationDate,
                                   source: source, destination: destination,
                                   onDownloadBytes: onDownloadBytes, onUploadBytes: onUploadBytes)
                let temporary = try SafeLocalPath.destination(forRelativePath: temporaryRel, in: root)
                try await FileSystemService.shared.installReplacement(temporary, at: target)
            } catch {
                try? await deleteFile(at: temporaryRel, on: destination)
                throw error
            }
        case .cloud(let accountId, let rootPath):
            guard let provider = await SyncEngine.shared.provider(for: accountId) else {
                throw SyncPlannerError.providerUnavailable
            }
            try await provider.replacingItem(at: cloudJoin(rootPath, rel)) { temporaryPath in
                try await copyFile(sourceRelative: rel,
                                   destRelative: sibling(named: (temporaryPath as NSString).lastPathComponent),
                                   modificationDate: modificationDate,
                                   source: source, destination: destination,
                                   onDownloadBytes: onDownloadBytes, onUploadBytes: onUploadBytes)
            }
        case .offlineIndexed:
            throw SyncPlannerError.offlineEndpointNotWritable
        }
    }

    private static func createDirectory(at rel: String, on endpoint: SyncEndpoint) async throws {
        switch endpoint {
        case .local(let root):
            let url = try SafeLocalPath.destination(forRelativePath: rel, in: root)
            try await FileSystemService.shared.createDirectory(at: url)
        case .cloud(let accountId, let rootPath):
            guard let provider = await SyncEngine.shared.provider(for: accountId) else {
                throw SyncPlannerError.providerUnavailable
            }
            try await provider.createDirectory(at: cloudJoin(rootPath, rel))
        case .offlineIndexed:
            throw SyncPlannerError.offlineEndpointNotWritable
        }
    }

    private static func deleteFile(at rel: String, on endpoint: SyncEndpoint) async throws {
        switch endpoint {
        case .local(let root):
            let url = try SafeLocalPath.destination(forRelativePath: rel, in: root)
            if await FileSystemService.shared.itemExists(at: url) {
                try await FileSystemService.shared.deleteItem(at: url)
            }
        case .cloud(let accountId, let rootPath):
            guard let provider = await SyncEngine.shared.provider(for: accountId) else {
                throw SyncPlannerError.providerUnavailable
            }
            do {
                try await provider.deleteItem(at: cloudJoin(rootPath, rel))
            } catch CloudProviderError.notFound {
                // Already gone, which is what was wanted. Mirror meets this
                // when the file is inside a folder it has just replaced.
            }
        case .offlineIndexed:
            throw SyncPlannerError.offlineEndpointNotWritable
        }
    }

    private static func deleteDirectory(at rel: String, on endpoint: SyncEndpoint) async throws {
        // Same semantics as deleteFile for both backends.
        try await deleteFile(at: rel, on: endpoint)
    }

    private static func copyFile(sourceRelative: String,
                                 destRelative: String,
                                 modificationDate: Date?,
                                 source: SyncEndpoint,
                                 destination: SyncEndpoint,
                                 onDownloadBytes: @escaping @Sendable (Int64) -> Void,
                                 onUploadBytes: @escaping @Sendable (Int64) -> Void) async throws {
        switch (source, destination) {
        case let (.local(srcRoot), .local(dstRoot)):
            let src = try SafeLocalPath.destination(forRelativePath: sourceRelative, in: srcRoot)
            let dst = try SafeLocalPath.destination(forRelativePath: destRelative, in: dstRoot)
            try await ensureParentDirectory(for: dst)
            try await FileSystemService.shared.copyItem(from: src, to: dst)
            // Local-to-local has no byte stream; emit full size once as progress.
            let size = (try? FileManager.default.attributesOfItem(atPath: dst.path)[.size] as? Int64) ?? 0
            if size > 0 { onUploadBytes(size) }

        case let (.local(srcRoot), .cloud(accountId, rootPath)):
            guard let provider = await SyncEngine.shared.provider(for: accountId) else {
                throw SyncPlannerError.providerUnavailable
            }
            let src = try SafeLocalPath.destination(forRelativePath: sourceRelative, in: srcRoot)
            let destPath = cloudJoin(rootPath, destRelative)
            try await ensureCloudParentDirectory(for: destPath, on: provider)
            try await CloudProviderError.enforceUploadSizeLimit(src, provider: provider)
            try await provider.uploadFile(from: src, to: destPath, onBytes: onUploadBytes)
            if let modificationDate {
                try? await provider.setModificationDate(at: destPath, to: modificationDate)
            }

        case let (.cloud(accountId, rootPath), .local(dstRoot)):
            guard let provider = await SyncEngine.shared.provider(for: accountId) else {
                throw SyncPlannerError.providerUnavailable
            }
            let dst = try SafeLocalPath.destination(forRelativePath: destRelative, in: dstRoot)
            try await ensureParentDirectory(for: dst)
            try await provider.downloadFile(remotePath: cloudJoin(rootPath, sourceRelative), to: dst, onBytes: onDownloadBytes)
            if let modificationDate {
                try? FileManager.default.setAttributes([.modificationDate: modificationDate], ofItemAtPath: dst.path)
            }
            Quarantine.mark(dst)

        case let (.cloud(srcAccountId, srcRoot), .cloud(dstAccountId, dstRoot)):
            guard let srcProvider = await SyncEngine.shared.provider(for: srcAccountId),
                  let dstProvider = await SyncEngine.shared.provider(for: dstAccountId) else {
                throw SyncPlannerError.providerUnavailable
            }
            let tempURL = StagingLocation.base().appendingPathComponent("filefluss-sync-\(UUID().uuidString)")
            defer { try? FileManager.default.removeItem(at: tempURL) }
            try await srcProvider.downloadFile(remotePath: cloudJoin(srcRoot, sourceRelative), to: tempURL, onBytes: onDownloadBytes)
            // On the staged file too: some servers take the date from the
            // upload itself.
            if let modificationDate {
                try? FileManager.default.setAttributes([.modificationDate: modificationDate], ofItemAtPath: tempURL.path)
            }
            let dstPath = cloudJoin(dstRoot, destRelative)
            try await ensureCloudParentDirectory(for: dstPath, on: dstProvider)
            try await CloudProviderError.enforceUploadSizeLimit(tempURL, provider: dstProvider)
            try await dstProvider.uploadFile(from: tempURL, to: dstPath, onBytes: onUploadBytes)
            if let modificationDate {
                try? await dstProvider.setModificationDate(at: dstPath, to: modificationDate)
            }

        case (.offlineIndexed, _), (_, .offlineIndexed):
            throw SyncPlannerError.offlineEndpointNotWritable
        }
    }

    private static func ensureParentDirectory(for url: URL) async throws {
        let parent = url.deletingLastPathComponent()
        let exists = await FileSystemService.shared.itemExists(at: parent)
        if !exists {
            try await FileSystemService.shared.createDirectory(at: parent)
        }
    }

    private static func ensureCloudParentDirectory(for path: String, on provider: any CloudProvider) async throws {
        let parent = (path as NSString).deletingLastPathComponent
        if parent.isEmpty || parent == "/" { return }
        // Best-effort: provider's createDirectory already handles existing-dir in most implementations.
        try? await provider.createDirectory(at: parent)
    }

    private static func cloudJoin(_ root: String, _ rel: String) -> String {
        if rel.isEmpty { return root }
        let normalisedRoot = root.hasSuffix("/") ? String(root.dropLast()) : root
        let normalisedRel = rel.hasPrefix("/") ? String(rel.dropFirst()) : rel
        if normalisedRoot.isEmpty { return "/" + normalisedRel }
        return normalisedRoot + "/" + normalisedRel
    }
}

