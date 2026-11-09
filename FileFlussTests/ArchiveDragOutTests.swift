import Testing
import AppKit
@testable import FileFluss

/// Dragging an entry out of an archive crashed the app the moment the drop
/// asked for the file: AppKit calls the promise delegate from its own
/// file-coordination queue, the delegate was main-actor isolated, and the
/// runtime's isolation check stopped the process. These call it the same
/// way — from a background queue — so a regression traps here instead.
///
/// (A full pasteboard round trip would be closer still, but macOS only
/// fulfils file promises inside a real drag session; outside one the
/// receiver waits forever.)
@Suite("Dragging out of an archive")
@MainActor
struct ArchiveDragOutTests {

    @Test("The promise is answered from a background queue, and writes the entry")
    func promiseFromBackground() async throws {
        let base = FileManager.default.temporaryDirectory
            .appendingPathComponent("FileFlussDragOut-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: base) }
        let root = base.resolvingSymlinksInPath()
        try FileManager.default.createDirectory(
            at: root.appendingPathComponent("Sample"), withIntermediateDirectories: true
        )
        try "dragged\n".write(
            to: root.appendingPathComponent("Sample/readme.txt"), atomically: true, encoding: .utf8
        )
        let archive = try await ArchiveService.createZip(
            names: ["Sample"], in: root, archiveName: "Sample.zip"
        )
        let tree = try await ArchiveService.list(archive, format: .zip)

        let delegate = ArchiveFilePromiseDelegate()
        delegate.plan = ArchiveDragPlan(archiveURL: archive, format: .zip, tree: tree, innerPath: "Sample")
        nonisolated(unsafe) let provider = NSFilePromiseProvider(fileType: "public.plain-text", delegate: delegate)
        provider.userInfo = ArchiveEntryPromise(entryPath: "Sample/readme.txt", name: "readme.txt")

        let drop = root.appendingPathComponent("Drop", isDirectory: true)
        try FileManager.default.createDirectory(at: drop, withIntermediateDirectories: true)
        let target = drop.appendingPathComponent("readme.txt")

        nonisolated(unsafe) let object: NSObject = delegate
        let result: (name: String, error: String?) = await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                // The three calls AppKit makes, in its order, off the main
                // thread — and through the Objective-C runtime, as AppKit
                // makes them. A direct Swift call would skip the @objc entry
                // point, which is where the isolation check that crashed
                // the app lives.
                let queue = ObjC.queue(object, provider)
                let name = ObjC.fileName(object, provider)
                queue.addOperation {
                    ObjC.write(object, provider, target) { error in
                        continuation.resume(returning: (name, error?.localizedDescription))
                    }
                }
            }
        }
        #expect(result.name == "readme.txt")
        #expect(result.error == nil)
        #expect(try String(contentsOf: target, encoding: .utf8) == "dragged\n")
    }
}

/// Calls the promise delegate by selector, the way AppKit does.
private enum ObjC {
    typealias QueueIMP = @convention(c) (AnyObject, Selector, AnyObject) -> OperationQueue
    typealias NameIMP = @convention(c) (AnyObject, Selector, AnyObject, NSString) -> NSString
    typealias WriteIMP = @convention(c) (
        AnyObject, Selector, AnyObject, NSURL, @convention(block) (NSError?) -> Void
    ) -> Void

    static let queueSelector = NSSelectorFromString("operationQueueForFilePromiseProvider:")
    static let nameSelector = NSSelectorFromString("filePromiseProvider:fileNameForType:")
    static let writeSelector = NSSelectorFromString("filePromiseProvider:writePromiseToURL:completionHandler:")

    static func queue(_ object: NSObject, _ provider: AnyObject) -> OperationQueue {
        let imp = unsafeBitCast(object.method(for: queueSelector), to: QueueIMP.self)
        return imp(object, queueSelector, provider)
    }

    static func fileName(_ object: NSObject, _ provider: AnyObject) -> String {
        let imp = unsafeBitCast(object.method(for: nameSelector), to: NameIMP.self)
        return imp(object, nameSelector, provider, "public.plain-text" as NSString) as String
    }

    static func write(
        _ object: NSObject, _ provider: AnyObject, _ url: URL,
        _ completion: @escaping @convention(block) (NSError?) -> Void
    ) {
        let imp = unsafeBitCast(object.method(for: writeSelector), to: WriteIMP.self)
        imp(object, writeSelector, provider, url as NSURL, completion)
    }
}
