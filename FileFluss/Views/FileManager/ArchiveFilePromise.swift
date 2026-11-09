import AppKit
import FileFlussCore

/// What a drag out of an archive carries, snapshot when the drag begins.
///
/// A value type on purpose: the promise is fulfilled on an operation queue,
/// well away from the main actor and possibly after the panel has moved on,
/// so nothing here may reach back into the session that created it.
struct ArchiveDragPlan: Sendable {
    let archiveURL: URL
    let format: ArchiveFormat
    let tree: ArchiveTree
    /// The folder inside the archive the dragged rows came from.
    let innerPath: String

    func entryPath(forName name: String) -> String {
        innerPath.isEmpty ? name : "\(innerPath)/\(name)"
    }
}

/// Identifies one dragged entry. Rides along in the promise provider's
/// `userInfo`.
struct ArchiveEntryPromise: Sendable {
    let entryPath: String
    let name: String
}

/// Fulfils a drag out of an archive by unpacking the dragged entry into
/// wherever the drop asked for it.
///
/// Unlike the cloud panel's promise delegate — a marker whose real download
/// happens on the receiving side — this one does the work. That keeps the
/// drop end of it free of archive knowledge: the other panel, Finder and any
/// other app that accepts files all get a real file written for them.
final class ArchiveFilePromiseDelegate: NSObject, NSFilePromiseProviderDelegate, @unchecked Sendable {
    nonisolated(unsafe) var plan: ArchiveDragPlan?

    private let queue: OperationQueue = {
        let queue = OperationQueue()
        queue.qualityOfService = .userInitiated
        // Entries are unpacked one at a time: each one streams through the
        // same archive, and for a solid rar or 7z two concurrent readers
        // would both decompress from the beginning and finish no sooner.
        queue.maxConcurrentOperationCount = 1
        return queue
    }()

    nonisolated func filePromiseProvider(
        _ filePromiseProvider: NSFilePromiseProvider, fileNameForType fileType: String
    ) -> String {
        (filePromiseProvider.userInfo as? ArchiveEntryPromise)?.name ?? "item"
    }

    nonisolated func filePromiseProvider(
        _ filePromiseProvider: NSFilePromiseProvider,
        writePromiseTo url: URL,
        completionHandler: @escaping ((any Error)?) -> Void
    ) {
        guard let plan,
              let promise = filePromiseProvider.userInfo as? ArchiveEntryPromise else {
            completionHandler(nil)
            return
        }
        // Already off the main thread: AppKit calls this on the operation
        // queue below, which is why the work is done inline rather than
        // handed to a task.
        do {
            try Self.write(promise, from: plan, to: url)
            completionHandler(nil)
        } catch {
            SupportLogger.shared.log(
                "archive drag failed for \(promise.entryPath): \(error.localizedDescription)",
                category: "archive", level: .error
            )
            completionHandler(error)
        }
    }

    /// Unpacks into a staging folder and then moves the result to `url`.
    ///
    /// Extracting straight to `url`'s parent would be shorter but wrong: when
    /// the drop folder already holds a file of that name, AppKit hands over a
    /// de-duplicated URL ("report 2.pdf"), and the entry's own name is no
    /// longer where the file has to end up.
    nonisolated private static func write(
        _ promise: ArchiveEntryPromise, from plan: ArchiveDragPlan, to url: URL
    ) throws {
        let staging = StagingLocation.stagingDirectory(prefix: "drag")
        defer { try? FileManager.default.removeItem(at: staging) }

        let entryPaths = Set(plan.tree.subtree(at: promise.entryPath).map(\.path))
        _ = try ArchiveService.extractSync(
            entryPaths: entryPaths,
            from: plan.archiveURL,
            format: plan.format,
            strippingPrefix: ArchiveEntry.parentPath(of: promise.entryPath),
            into: staging,
            onBytes: nil
        )
        let written = staging.appendingPathComponent(promise.name)
        let fm = FileManager.default
        guard fm.fileExists(atPath: written.path) else {
            throw ArchiveError.unreadable(
                L10n.format("\"%@\" is not in the archive.", promise.name)
            )
        }
        if fm.fileExists(atPath: url.path) { try fm.removeItem(at: url) }
        try fm.moveItem(at: written, to: url)
    }

    /// Every callback above runs off the main thread — AppKit asks for this
    /// queue from a file-coordination queue, and writes the promise on the
    /// queue it gets back — so all of them are `nonisolated`. The protocol
    /// is main-actor in Swift 6, and without that the runtime's isolation
    /// check stopped the app the moment a drag left the panel.
    nonisolated func operationQueue(for filePromiseProvider: NSFilePromiseProvider) -> OperationQueue {
        queue
    }
}

/// Counts outstanding file promises so a drop reloads the panel once.
/// Only ever touched on the main queue, which the receivers hop to before
/// counting — hence the unchecked conformance.
final class PromiseCountdown: @unchecked Sendable {
    var remaining: Int
    /// Files written so far, for a receiver that hands them on together.
    var staged: [URL] = []
    init(_ remaining: Int) { self.remaining = remaining }
}
