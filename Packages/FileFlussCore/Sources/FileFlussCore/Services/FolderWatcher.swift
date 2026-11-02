import Foundation

/// Reports when the contents of a folder change, from any source: Finder, a
/// browser finishing a download, a script.
///
/// Stops when released. The folder is opened with `O_EVTONLY`, so watching it
/// doesn't keep a drive from being ejected.
public final class FolderWatcher: Sendable {
    private let source: any DispatchSourceFileSystemObject

    /// Nil when the folder can't be opened for watching, in which case the
    /// panel simply goes on refreshing by hand.
    public init?(url: URL, onChange: @escaping @Sendable () -> Void) {
        let descriptor = open(url.path, O_EVTONLY)
        guard descriptor >= 0 else { return nil }
        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: descriptor,
            // `write` covers anything added, removed or renamed inside; the
            // others cover the folder itself going away.
            eventMask: [.write, .delete, .rename],
            queue: .global(qos: .utility)
        )
        source.setEventHandler(handler: onChange)
        source.setCancelHandler { close(descriptor) }
        source.resume()
        self.source = source
    }

    deinit {
        source.cancel()
    }
}
