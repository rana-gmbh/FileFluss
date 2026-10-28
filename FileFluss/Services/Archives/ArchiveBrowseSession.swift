import Foundation
import UniformTypeIdentifiers
import FileFlussCore

/// A panel's view into one open archive.
///
/// The archive is listed once — opening a 200 MB zip reads its central
/// directory, not its contents — and everything after that is served from
/// `tree`. Nothing is unpacked until the user asks for a specific entry, by
/// dragging it out, copying it to the other panel, opening it, or previewing
/// it; those entries are written to a staging folder that goes away with the
/// session.
///
/// Rows inside the archive carry the path they *would* have on disk
/// (`…/photos.zip/holiday/beach.jpg`), so the breadcrumb, the back/forward
/// history, sorting, filtering and the selection all work without knowing
/// anything about archives. The rule that keeps that honest: no code may
/// touch such a URL on the file system, which is why the panel hides every
/// write action while a session is open.
@MainActor
final class ArchiveBrowseSession {
    let archiveURL: URL
    let format: ArchiveFormat
    let tree: ArchiveTree

    /// Where inside the archive the panel currently is; `""` is its root.
    private(set) var innerPath: String = ""

    /// Created on the first extraction, so browsing alone leaves no folders
    /// behind.
    private var stagingRoot: URL?

    /// Set once an entry has been handed to another application.
    ///
    /// Staging is otherwise scoped to the session and deleted when the panel
    /// leaves the archive — but a file the user double-clicked is now open in
    /// TextEdit or Preview, and pulling it out from under them would be a
    /// bug. Those folders are left for `CacheManager` to reclaim by age on a
    /// later launch instead.
    private var hasOpenedExternally = false

    /// Resolved once per extension rather than once per row: `UTType`
    /// look-ups go through the type database, and an archive with 40,000
    /// entries would otherwise pay for 40,000 of them to draw one screen.
    private var contentTypeByExtension: [String: UTType?] = [:]

    init(archiveURL: URL, format: ArchiveFormat, tree: ArchiveTree) {
        self.archiveURL = archiveURL
        self.format = format
        self.tree = tree
    }

    deinit {
        if let stagingRoot, !hasOpenedExternally {
            try? FileManager.default.removeItem(at: stagingRoot)
        }
    }

    var archiveName: String { archiveURL.lastPathComponent }

    // MARK: - Virtual paths

    /// Where inside the archive `url` points, or nil when it points outside
    /// — which is how the panel knows a navigation has left the archive.
    func innerPath(for url: URL) -> String? {
        let root = Self.canonicalPath(archiveURL)
        let candidate = Self.canonicalPath(url)
        if candidate == root { return "" }
        let rootWithSlash = root.hasSuffix("/") ? root : root + "/"
        guard candidate.hasPrefix(rootWithSlash) else { return nil }
        return String(candidate.dropFirst(rootWithSlash.count))
    }

    /// A file path spelled one way, whichever way the URL arrived.
    ///
    /// `standardizedFileURL` consults the file system for the `/private`
    /// prefix: it strips it from a path that exists and leaves it on one that
    /// doesn't. Inside an archive that is every other path — the archive file
    /// exists, nothing under it does — so the archive under `/var/folders`
    /// came back as `/var/…` while a folder inside it came back as
    /// `/private/var/…`, and comparing the two said the panel had left an
    /// archive it was still in. Stripping the prefix on both sides applies
    /// Foundation's own rule without the lookup that makes it disagree with
    /// itself. The three paths listed are the symlinks that rule exists for.
    private static func canonicalPath(_ url: URL) -> String {
        let path = url.standardizedFileURL.path
        for symlinked in ["/private/var/", "/private/tmp/", "/private/etc/"]
        where path.hasPrefix(symlinked) {
            return String(path.dropFirst("/private".count))
        }
        return path
    }

    func virtualURL(forInnerPath path: String) -> URL {
        guard !path.isEmpty else { return archiveURL }
        var url = archiveURL
        for component in path.split(separator: "/") {
            url.appendPathComponent(String(component))
        }
        return url
    }

    // MARK: - Listing

    /// The entries directly inside `path`, with `innerPath` moved there.
    /// The cloud panel builds its own rows from these; the local panel uses
    /// `items(at:showHidden:)`.
    func entries(at path: String, showHidden: Bool) -> [ArchiveEntry] {
        innerPath = path
        return tree.children(of: path).filter { showHidden || !$0.name.hasPrefix(".") }
    }

    /// The rows for `path`, with `innerPath` moved there.
    func items(at path: String, showHidden: Bool) -> [FileItem] {
        innerPath = path
        return tree.children(of: path).compactMap { entry in
            let hidden = entry.name.hasPrefix(".")
            guard showHidden || !hidden else { return nil }
            return FileItem(
                url: virtualURL(forInnerPath: entry.path),
                name: entry.name,
                isDirectory: entry.isDirectory,
                // A folder's row shows what unpacking it would cost. The
                // panel can't walk an archive the way it walks a disk, and
                // the figure is already paid for by the listing.
                size: entry.isDirectory ? tree.recursiveSize(of: entry.path) : entry.size,
                modificationDate: entry.modificationDate,
                isHidden: hidden,
                isSymlink: entry.isSymlink,
                contentType: entry.isDirectory ? nil : contentType(forName: entry.name)
            )
        }
    }

    private func contentType(forName name: String) -> UTType? {
        let ext = (name as NSString).pathExtension.lowercased()
        guard !ext.isEmpty else { return nil }
        if let cached = contentTypeByExtension[ext] { return cached }
        let resolved = UTType(filenameExtension: ext)
        contentTypeByExtension[ext] = resolved
        return resolved
    }

    // MARK: - Extraction

    /// Unpacks `items` into the session's staging folder and returns the real
    /// files, in the order the items were given.
    ///
    /// A selected folder comes out as a folder with its whole subtree, which
    /// is what dropping it on the other panel has to produce.
    func materialise(
        _ items: [FileItem],
        openedExternally: Bool = false,
        onBytes: (@Sendable (Int64) -> Void)? = nil
    ) async throws -> [URL] {
        try await materialise(
            innerPaths: items.compactMap { innerPath(for: $0.url) },
            openedExternally: openedExternally,
            onBytes: onBytes
        )
    }

    /// The same, for entries named by their path inside the archive — which
    /// is how the cloud panel, whose rows carry no file URL, asks.
    func materialise(
        innerPaths requested: [String],
        openedExternally: Bool = false,
        onBytes: (@Sendable (Int64) -> Void)? = nil
    ) async throws -> [URL] {
        guard !requested.isEmpty else { return [] }
        if openedExternally { hasOpenedExternally = true }
        guard !tree.containsEncryptedEntries else { throw ArchiveError.encrypted }

        var entryPaths: Set<String> = []
        for path in requested {
            for entry in tree.subtree(at: path) {
                entryPaths.insert(entry.path)
            }
        }
        guard !entryPaths.isEmpty else { return [] }

        // Every requested entry sits in one folder of the archive — the one
        // on screen — and that folder is what gets stripped from the front.
        let prefix = ArchiveEntry.parentPath(of: requested[0])
        let staging = try stagingDirectory()
        _ = try await ArchiveService.extract(
            entryPaths: entryPaths,
            from: archiveURL,
            format: format,
            strippingPrefix: prefix,
            into: staging,
            onBytes: onBytes
        )
        // Mapped from the request rather than taken from the extractor's
        // return value, so the caller gets the items back in the order it
        // asked for them even when the archive stores them in another.
        return requested.compactMap { path in
            let url = try? ArchiveService.targetURL(
                for: path, strippingPrefix: prefix, in: staging
            )
            guard let url, FileManager.default.fileExists(atPath: url.path) else { return nil }
            return url
        }
    }

    /// Total uncompressed bytes of a selection — what the space check needs
    /// before a transfer starts.
    func uncompressedSize(of items: [FileItem]) -> Int64 {
        items.reduce(0) { total, item in
            guard let path = innerPath(for: item.url) else { return total }
            guard let entry = tree.entry(at: path) else { return total }
            return total + (entry.isDirectory ? tree.recursiveSize(of: path) : entry.size)
        }
    }

    private func stagingDirectory() throws -> URL {
        if let stagingRoot { return stagingRoot }
        let directory = StagingLocation.stagingDirectory(prefix: "archive")
        stagingRoot = directory
        return directory
    }

    /// Drops the unpacked files. Called when the panel leaves the archive.
    func close() {
        if let stagingRoot, !hasOpenedExternally {
            try? FileManager.default.removeItem(at: stagingRoot)
        }
        stagingRoot = nil
    }
}
