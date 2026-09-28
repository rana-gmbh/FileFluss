import Foundation
import SwiftUI
import FileFlussCore

/// Where a transfer is going. A destination is a folder, not a panel: a
/// sidebar drop targets a favourite or an account that may not be open in
/// any panel, and the panel is only needed to decide where progress is
/// shown and what to refresh afterwards.
enum TransferDestination {
    case local(directory: URL, panel: PanelSide)
    case cloud(accountId: UUID, path: String, panel: PanelSide)

    var panel: PanelSide {
        switch self {
        case .local(_, let panel): return panel
        case .cloud(_, _, let panel): return panel
        }
    }
}

/// A drop onto a sidebar row that still needs the user to say copy or move.
struct PendingSidebarDrop: Identifiable {
    let id = UUID()
    let localURLs: [URL]
    let cloudURLs: [URL]
    let destination: TransferDestination
    let destinationName: String
}

extension AppState {

    /// Copies or moves items into a destination.
    ///
    /// The single place this happens. Paste and sidebar drops both route
    /// here rather than assembling their own version: the rules that make a
    /// move safe — delete only what actually arrived, never on a failure,
    /// skip or cancellation — were previously written out at each call site,
    /// and every divergent copy was a way to lose files.
    ///
    /// `cloudURLs` use the `filefluss-cloud://<accountId>/<path>` form the
    /// clipboard already speaks, so both callers share one representation.
    /// Entry point for a sidebar drop. Honours the drag-and-drop mode: the
    /// explicit Copy/Move toolbar modes act immediately, while the default
    /// "ask" mode prompts — same contract as a drop between panels, so the
    /// sidebar doesn't quietly decide something the panels would have asked
    /// about.
    func handleSidebarDrop(
        localURLs: [URL],
        cloudURLs: [URL],
        to destination: TransferDestination,
        destinationName: String,
        forcedMove: Bool?
    ) {
        if let forcedMove {
            Task { await performTransfer(localURLs: localURLs, cloudURLs: cloudURLs, to: destination, isMove: forcedMove) }
            return
        }
        switch dragDropMode {
        case .copy:
            Task { await performTransfer(localURLs: localURLs, cloudURLs: cloudURLs, to: destination, isMove: false) }
        case .move:
            Task { await performTransfer(localURLs: localURLs, cloudURLs: cloudURLs, to: destination, isMove: true) }
        case .ask:
            pendingSidebarDrop = PendingSidebarDrop(
                localURLs: localURLs,
                cloudURLs: cloudURLs,
                destination: destination,
                destinationName: destinationName
            )
        }
    }

    /// Runs a drop the user has just answered the prompt for.
    func resolvePendingSidebarDrop(isMove: Bool) {
        guard let pending = pendingSidebarDrop else { return }
        pendingSidebarDrop = nil
        Task {
            await performTransfer(
                localURLs: pending.localURLs,
                cloudURLs: pending.cloudURLs,
                to: pending.destination,
                isMove: isMove
            )
        }
    }

    func performTransfer(
        localURLs: [URL],
        cloudURLs: [URL],
        to destination: TransferDestination,
        isMove: Bool
    ) async {
        switch destination {
        case .cloud(let destAccountId, let destPath, let panel):
            await transferIntoCloud(
                localURLs: localURLs,
                cloudURLs: cloudURLs,
                destAccountId: destAccountId,
                destPath: destPath,
                panel: panel,
                isMove: isMove
            )
        case .local(let directory, let panel):
            await transferIntoLocal(
                localURLs: localURLs,
                cloudURLs: cloudURLs,
                destDir: directory,
                panel: panel,
                isMove: isMove
            )
        }
    }

    // MARK: - Into a cloud folder

    private func transferIntoCloud(
        localURLs: [URL],
        cloudURLs: [URL],
        destAccountId: UUID,
        destPath: String,
        panel: PanelSide,
        isMove: Bool
    ) async {
        let destVM = cloudFileManager(for: destAccountId, side: panel)
        let leftFM = leftFileManager
        let rightFM = rightFileManager

        if !localURLs.isEmpty {
            await gateTransfer(
                destAccountId: destAccountId, destLocalDir: nil,
                localSources: localURLs, isMove: isMove, verb: isMove ? "Move" : "Upload"
            ) {
                let transfer = TransferProgress(
                    operation: isMove ? "Moving" : "Uploading",
                    totalItems: localURLs.count
                )
                self.addTransfer(transfer, panel: panel)
                transfer.task = Task { [destVM] in
                    await destVM.uploadFiles(from: localURLs, toPath: destPath, progress: transfer)
                    if isMove {
                        // Only the files that actually uploaded. Deleting is
                        // permanent (no Trash), and a batch can end with
                        // failures, with items skipped in the conflict
                        // dialog, or cancelled part-way.
                        let landed = transfer.succeededNames
                        for url in localURLs where landed.contains(url.lastPathComponent) {
                            try? await FileSystemService.shared.deleteItem(at: url)
                        }
                        await leftFM.refresh()
                        await rightFM.refresh()
                    }
                    await destVM.refresh()
                }
            }
        }

        // Cloud-to-cloud uses raw provider calls so one TransferProgress is
        // the only place items and bytes are recorded; going through the
        // view model's download + upload double-counts them.
        guard !cloudURLs.isEmpty else { return }
        for (sourceAccountId, sourceItems) in Self.groupCloudURLsByAccount(cloudURLs) {
            await runCloudToCloudPaste(
                sourceAccountId: sourceAccountId,
                destAccountId: destAccountId,
                destPath: destPath,
                sourceItems: sourceItems,
                isCut: isMove,
                destPanel: panel
            )
        }
    }

    // MARK: - Into a local folder

    private func transferIntoLocal(
        localURLs: [URL],
        cloudURLs: [URL],
        destDir: URL,
        panel: PanelSide,
        isMove: Bool
    ) async {
        let destFM = fileManager(for: panel)

        if !localURLs.isEmpty {
            await gateTransfer(
                destAccountId: nil, destLocalDir: destDir,
                localSources: localURLs, isMove: isMove, verb: isMove ? "Move" : "Copy"
            ) {
                let transfer = TransferProgress(
                    operation: isMove ? "Moving" : "Copying",
                    totalItems: localURLs.count
                )
                self.addTransfer(transfer, panel: panel)
                transfer.task = Task { [destFM] in
                    let items = localURLs.map { FileItem(url: $0) }
                    if isMove {
                        await destFM.performMove(items: items, to: destDir, progress: transfer)
                    } else {
                        await destFM.performCopy(items: items, to: destDir, progress: transfer)
                    }
                    await destFM.refresh()
                }
            }
        }

        guard !cloudURLs.isEmpty else { return }
        for (sourceAccountId, sourceItems) in Self.groupCloudURLsByAccount(cloudURLs) {
            let sourceVM = cloudFileManager(for: sourceAccountId, side: panel == .left ? .right : .left)
            let items = sourceItems.map { source in
                CloudFileItem(
                    id: source.remotePath,
                    name: source.name,
                    path: source.remotePath,
                    isDirectory: false,
                    size: 0,
                    modificationDate: Date(),
                    checksum: nil
                )
            }
            await gateTransfer(
                destAccountId: nil, destLocalDir: destDir,
                cloudSources: items.map { (sourceAccountId, $0) },
                isMove: isMove, verb: isMove ? "Move" : "Download"
            ) {
                let transfer = TransferProgress(
                    operation: isMove ? "Moving" : "Downloading",
                    totalItems: items.count
                )
                self.addTransfer(transfer, panel: panel)
                transfer.task = Task { [sourceVM, destFM] in
                    await sourceVM.downloadItems(items, to: destDir, progress: transfer)
                    if isMove {
                        // Same rule as everywhere else: only what arrived.
                        let landed = transfer.succeededNames
                        let transferred = items.filter { landed.contains($0.name) }
                        if !transferred.isEmpty {
                            await sourceVM.deleteItems(transferred)
                            await sourceVM.refresh()
                        }
                    }
                    await destFM.refresh()
                }
            }
        }
    }

    // MARK: - Helpers

    /// Splits `filefluss-cloud://<accountId>/<path>` URLs by source account.
    static func groupCloudURLsByAccount(_ urls: [URL]) -> [(UUID, [(remotePath: String, name: String)])] {
        var byAccount: [UUID: [(remotePath: String, name: String)]] = [:]
        for url in urls {
            guard let host = url.host, let accountId = UUID(uuidString: host) else { continue }
            let remotePath = url.path
            byAccount[accountId, default: []].append(
                (remotePath, (remotePath as NSString).lastPathComponent)
            )
        }
        return byAccount.map { ($0.key, $0.value) }
    }

    /// The `filefluss-cloud://` form for items being dragged out of a cloud
    /// panel, so a drag and a clipboard paste look identical downstream.
    static func cloudURLs(for items: [CloudFileItem], accountId: UUID) -> [URL] {
        items.compactMap { item in
            var components = URLComponents()
            components.scheme = "filefluss-cloud"
            components.host = accountId.uuidString
            components.path = item.path.hasPrefix("/") ? item.path : "/" + item.path
            return components.url
        }
    }
}
