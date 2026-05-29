import Foundation
import FileFlussCore

/// A copy/move the user must confirm because it would exceed the destination's
/// available space. `proceed` runs the actual transfer when they choose to
/// continue anyway.
struct SpaceWarning: Identifiable {
    let id = UUID()
    let impact: SpaceImpact
    let verb: String
    let proceed: () -> Void
}

/// A single item about to be transferred, reduced to what the space pre-flight
/// needs: a top-level name (to detect overwrites at the destination) and its
/// total size (folders already summed by the caller helpers below).
struct TransferItemSize: Sendable {
    let name: String
    let bytes: Int64
}

extension AppState {

    // MARK: - Gate

    /// Runs `proceed` immediately when the transfer fits (or space is
    /// unlimited/unknown); otherwise stashes a `SpaceWarning` so the root
    /// dialog can ask the user to cancel or continue anyway.
    func confirmSpace(_ impact: SpaceImpact?, verb: String, proceed: @escaping () -> Void) {
        if let impact, impact.wouldExceed {
            pendingSpaceWarning = SpaceWarning(impact: impact, verb: verb, proceed: proceed)
        } else {
            proceed()
        }
    }

    /// Convenience: compute the impact for a transfer, then gate it. Computing
    /// sizes can hit the network/disk, so this is async; call from the Task
    /// that would otherwise start the transfer.
    func gateTransfer(
        destAccountId: UUID?,
        destLocalDir: URL?,
        localSources: [URL] = [],
        cloudSources: [(accountId: UUID, item: CloudFileItem)] = [],
        isMove: Bool,
        verb: String,
        proceed: @escaping () -> Void
    ) async {
        // Off by default (Settings → General) so file operations stay fast —
        // the size calculation can be slow for large folders/accounts.
        guard SpaceCheck.isEnabled else {
            proceed()
            return
        }
        // Show the "checking…" HUD while sizes are summed; always clear it.
        isCheckingSpace = true
        let impact = await spaceImpact(
            destAccountId: destAccountId,
            destLocalDir: destLocalDir,
            localSources: localSources,
            cloudSources: cloudSources,
            isMove: isMove
        )
        isCheckingSpace = false
        confirmSpace(impact, verb: verb, proceed: proceed)
    }

    // MARK: - Pre-flight

    /// Projects how a transfer affects the destination's free space. Returns
    /// nil when there's nothing to check (no destination, no sources, or an
    /// unlimited/unknown quota), in which case the caller should just proceed.
    func spaceImpact(
        destAccountId: UUID?,
        destLocalDir: URL?,
        localSources: [URL],
        cloudSources: [(accountId: UUID, item: CloudFileItem)],
        isMove: Bool
    ) async -> SpaceImpact? {
        // Same-volume local move is a rename — no space consumed.
        if isMove, let destLocalDir, !localSources.isEmpty, cloudSources.isEmpty,
           localSources.allSatisfy({ SpaceCheck.sameVolume($0, destLocalDir) }) {
            return nil
        }

        // Gross sizes of everything being transferred.
        var items: [TransferItemSize] = []
        for url in localSources {
            items.append(TransferItemSize(name: url.lastPathComponent, bytes: await localSize(url)))
        }
        for source in cloudSources {
            let bytes = await cloudItemSize(accountId: source.accountId, item: source.item)
            items.append(TransferItemSize(name: source.item.name, bytes: bytes))
        }
        guard !items.isEmpty else { return nil }
        let gross = items.reduce(Int64(0)) { $0 + $1.bytes }

        // Available space + destination name + existing listing for overwrite netting.
        let available: Int64?
        let destinationName: String
        let isCloud: Bool
        var existing: [String: Int64] = [:]  // name → size of items already at destination

        if let destAccountId {
            isCloud = true
            destinationName = syncManager.accountFor(id: destAccountId)?.displayName
                ?? CloudProviderType.dropbox.displayName
            if let quota = await syncManager.storageQuota(for: destAccountId),
               let total = quota.totalBytes, total > 0 {
                available = max(0, total - quota.usedBytes)
            } else {
                available = nil  // unlimited / unknown
            }
            existing = await cloudDestinationSizes(accountId: destAccountId)
        } else if let destLocalDir {
            isCloud = false
            destinationName = destLocalDir.lastPathComponent.isEmpty ? destLocalDir.path : destLocalDir.lastPathComponent
            available = SpaceCheck.localVolumeFreeBytes(at: destLocalDir)
            existing = localDestinationSizes(destLocalDir)
        } else {
            return nil
        }

        guard available != nil else { return nil }  // nothing to warn about

        // Net = gross − size of top-level items being overwritten.
        var overwritten: Int64 = 0
        for item in items {
            if let prior = existing[item.name] { overwritten += prior }
        }
        let net = max(0, gross - overwritten)

        return SpaceImpact(destinationName: destinationName, isCloud: isCloud, available: available, addedBytes: net)
    }

    // MARK: - Size helpers

    private func localSize(_ url: URL) async -> Int64 {
        let isDir = (try? url.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory ?? false
        if isDir {
            return (try? await FileSystemService.shared.directorySize(at: url)) ?? 0
        }
        let size = (try? url.resourceValues(forKeys: [.totalFileSizeKey, .fileSizeKey]))
        return Int64(size?.totalFileSize ?? size?.fileSize ?? 0)
    }

    private func cloudItemSize(accountId: UUID, item: CloudFileItem) async -> Int64 {
        guard item.isDirectory else { return item.size }
        guard let provider = await syncManager.providerFor(accountId: accountId) else { return 0 }
        return (try? await provider.folderSize(at: item.path)) ?? 0
    }

    /// name → size for the items currently at a cloud destination folder, used
    /// to net out overwrites. Folder sizes are summed recursively. Best-effort:
    /// returns empty on any failure (then the check is simply gross).
    private func cloudDestinationSizes(accountId: UUID) async -> [String: Int64] {
        guard let provider = await syncManager.providerFor(accountId: accountId) else { return [:] }
        let path = syncManager.accountFor(id: accountId)?.rootPath ?? "/"
        guard let listing = try? await provider.listDirectory(at: path) else { return [:] }
        // Only file overwrites are netted out — recursively sizing every
        // destination subfolder is the dominant cost and rarely changes the
        // result (a transferred item seldom replaces a whole same-named
        // folder). Skipping it keeps the check to a single listing.
        var sizes: [String: Int64] = [:]
        for entry in listing where !entry.isDirectory {
            sizes[entry.name] = entry.size
        }
        return sizes
    }

    private func localDestinationSizes(_ dir: URL) -> [String: Int64] {
        let fm = FileManager.default
        guard let entries = try? fm.contentsOfDirectory(
            at: dir,
            includingPropertiesForKeys: [.isDirectoryKey, .totalFileSizeKey, .fileSizeKey],
            options: [.skipsSubdirectoryDescendants]
        ) else { return [:] }
        // File overwrites only — see cloudDestinationSizes for the rationale.
        var sizes: [String: Int64] = [:]
        for url in entries {
            let isDir = (try? url.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory ?? false
            guard !isDir else { continue }
            let v = try? url.resourceValues(forKeys: [.totalFileSizeKey, .fileSizeKey])
            sizes[url.lastPathComponent] = Int64(v?.totalFileSize ?? v?.fileSize ?? 0)
        }
        return sizes
    }
}
