import Foundation

/// A tiny on-disk key/value store keyed by POSIX-style paths. Used by
/// `WebDAVServer` to absorb the AppleDouble / `.DS_Store` writes Finder
/// produces during browsing without pushing them to the user's cloud, while
/// still serving them back consistently so Finder's view state survives a
/// folder revisit.
///
/// The layout mirrors the path tree below `root`, so debugging is easy and
/// uninstall is just `rm -rf`.
public actor WebDAVSidecarStore {
    private let root: URL

    public init(root: URL) {
        self.root = root
        try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    public func put(path: String, contents: Data) throws {
        let url = fileURL(for: path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try contents.write(to: url, options: .atomic)
    }

    public func get(path: String) -> Data? {
        try? Data(contentsOf: fileURL(for: path))
    }

    public func delete(path: String) {
        try? FileManager.default.removeItem(at: fileURL(for: path))
    }

    public func metadata(at path: String) -> (size: Int64, mtime: Date)? {
        let url = fileURL(for: path)
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: url.path) else { return nil }
        let size = (attrs[.size] as? NSNumber)?.int64Value ?? 0
        let mtime = (attrs[.modificationDate] as? Date) ?? Date()
        return (size, mtime)
    }

    public func exists(at path: String) -> Bool {
        FileManager.default.fileExists(atPath: fileURL(for: path).path)
    }

    /// Best-effort cleanup of the whole store. Called when the mount the
    /// store backs is being unmounted permanently.
    public func clear() {
        try? FileManager.default.removeItem(at: root)
        try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    private func fileURL(for path: String) -> URL {
        var trimmed = path
        while trimmed.hasPrefix("/") { trimmed.removeFirst() }
        return root.appendingPathComponent(trimmed)
    }
}
