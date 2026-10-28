import Foundation

/// One entry inside an archive. `path` is always archive-relative, slash
/// separated, with no leading `./` and no trailing slash — so a directory and
/// the file of the same name are spelled identically and can be looked up the
/// same way.
struct ArchiveEntry: Sendable, Hashable {
    let path: String
    let isDirectory: Bool
    let isSymlink: Bool
    /// Uncompressed size. 0 for directories, and for the (rare) entries
    /// whose header declares no size.
    let size: Int64
    let modificationDate: Date
    /// The entry's data is encrypted, so extracting it needs a passphrase.
    let isEncrypted: Bool

    var name: String {
        guard let slash = path.lastIndex(of: "/") else { return path }
        return String(path[path.index(after: slash)...])
    }

    /// The directory holding this entry; `""` for a top-level entry.
    var parentPath: String { Self.parentPath(of: path) }

    /// The directory holding `path`; `""` when `path` is top level.
    static func parentPath(of path: String) -> String {
        guard let slash = path.lastIndex(of: "/") else { return "" }
        return String(path[path.startIndex..<slash])
    }

    /// Normalises a path as an archive declares it, or returns nil when the
    /// entry must not be trusted.
    ///
    /// This is the one place that stands between a hostile archive and the
    /// file system: an entry named `../../../.ssh/authorized_keys` extracts
    /// outside the destination on any tool that joins paths naively (the
    /// "zip slip" family of bugs). Any `..` component is refused outright
    /// rather than resolved, because an entry that needs to climb out of the
    /// archive is never something the user asked for. A leading `/` is
    /// stripped instead of refused — absolute paths are common in archives
    /// made with `tar -P`, and once the root is gone the path is ordinary.
    static func normalise(path raw: String) -> String? {
        var components: [String] = []
        for component in raw.split(separator: "/", omittingEmptySubsequences: true) {
            if component == "." { continue }
            if component == ".." { return nil }
            components.append(String(component))
        }
        guard !components.isEmpty else { return nil }
        return components.joined(separator: "/")
    }
}

/// An archive's entry list arranged for browsing: children by directory and
/// recursive sizes, both resolved once when the archive is listed.
///
/// Built eagerly on purpose. Walking the entry list per navigation is fine
/// for the hundred-entry archive and visibly not fine for the hundred
/// thousand-entry one, and the panel redraws this on every keystroke of the
/// filter bar.
struct ArchiveTree: Sendable {
    /// Every entry, including the directories synthesised below.
    let entries: [ArchiveEntry]
    private let byPath: [String: ArchiveEntry]
    private let childrenByParent: [String: [ArchiveEntry]]
    private let recursiveSizeByDirectory: [String: Int64]

    /// True when at least one entry's data is encrypted. FileFluss has no
    /// passphrase prompt yet, so this is how the caller explains why
    /// extraction will fail before the user watches it fail.
    let containsEncryptedEntries: Bool

    /// Whether the archive's own entry list was readable but empty.
    var isEmpty: Bool { entries.isEmpty }

    var topLevel: [ArchiveEntry] { childrenByParent["", default: []] }

    /// Entries the archive actually declared, with AppleDouble sidecars and
    /// unsafe paths already dropped by the reader.
    init(rawEntries: [ArchiveEntry]) {
        // A zip may legitimately carry the same name twice; the later entry
        // is the one that would win on extraction, so it wins here too.
        var byPath: [String: ArchiveEntry] = [:]
        byPath.reserveCapacity(rawEntries.count * 2)
        for entry in rawEntries {
            byPath[entry.path] = entry
        }

        // Many archives (and most zips) declare files without declaring the
        // directories above them. Those directories still have to be
        // navigable, so stand them up from the paths that mention them.
        for entry in rawEntries {
            var parent = entry.parentPath
            while !parent.isEmpty, byPath[parent] == nil {
                byPath[parent] = ArchiveEntry(
                    path: parent,
                    isDirectory: true,
                    isSymlink: false,
                    size: 0,
                    modificationDate: entry.modificationDate,
                    isEncrypted: false
                )
                parent = ArchiveEntry.parentPath(of: parent)
            }
        }

        var children: [String: [ArchiveEntry]] = [:]
        // Sizes roll up to every ancestor, so a folder's row can show what
        // extracting it would cost without walking the list again.
        var sizes: [String: Int64] = [:]
        for entry in byPath.values {
            children[entry.parentPath, default: []].append(entry)
            guard !entry.isDirectory else { continue }
            var parent = entry.parentPath
            while !parent.isEmpty {
                sizes[parent, default: 0] += entry.size
                parent = ArchiveEntry.parentPath(of: parent)
            }
        }

        self.entries = Array(byPath.values)
        self.byPath = byPath
        self.childrenByParent = children
        self.recursiveSizeByDirectory = sizes
        self.containsEncryptedEntries = rawEntries.contains(where: \.isEncrypted)
    }

    /// Direct children of `directory` (`""` for the archive root).
    func children(of directory: String) -> [ArchiveEntry] {
        childrenByParent[directory, default: []]
    }

    func entry(at path: String) -> ArchiveEntry? {
        byPath[path]
    }

    /// Total uncompressed bytes of the files anywhere beneath `directory`.
    func recursiveSize(of directory: String) -> Int64 {
        recursiveSizeByDirectory[directory, default: 0]
    }

    /// Every entry at or beneath `path`, which is what extracting a selected
    /// folder has to pull out of the archive.
    func subtree(at path: String) -> [ArchiveEntry] {
        guard let root = byPath[path] else { return [] }
        guard root.isDirectory else { return [root] }
        let prefix = path + "/"
        return [root] + entries.filter { $0.path.hasPrefix(prefix) }
    }

    /// Total uncompressed bytes of the whole archive.
    var totalSize: Int64 {
        entries.lazy.filter { !$0.isDirectory }.reduce(0) { $0 + $1.size }
    }

    var fileCount: Int {
        entries.lazy.filter { !$0.isDirectory }.count
    }
}
