import Foundation

/// The archive kinds FileFluss can look inside.
///
/// Recognition is by file extension, deliberately: the context menu and the
/// double-click handler both have to decide "is this an archive?" for a row
/// the user just clicked, and opening every clicked file to sniff its magic
/// bytes would put I/O on the main actor's critical path. A file whose
/// extension lies is caught when the archive is actually opened — listing
/// fails, and the caller falls back to handing the file to its default app.
///
/// Reading only. `.zip` is the one format FileFluss creates (see
/// `ArchiveService.createZip`); there is no free RAR encoder and libarchive
/// has no RAR writer, so "Compress" never offers it.
enum ArchiveFormat: String, Sendable, CaseIterable {
    case zip
    case rar
    case sevenZip
    case tar
    case tarGzip
    case tarBzip2
    case tarXZ
    case gzip
    case bzip2
    case xz

    /// Whether the file holds one compressed stream rather than a directory
    /// of entries (`foo.txt.gz`). libarchive needs its "raw" reader for
    /// these, and the single entry carries no name of its own — see
    /// `ArchiveService`.
    var isSingleStream: Bool {
        switch self {
        case .gzip, .bzip2, .xz: return true
        default: return false
        }
    }

    /// Extensions that map to this format, longest first within each case so
    /// `.tar.gz` is preferred over `.gz`.
    private static let extensionTable: [(suffix: String, format: ArchiveFormat)] = [
        (".tar.gz", .tarGzip), (".tgz", .tarGzip),
        (".tar.bz2", .tarBzip2), (".tbz", .tarBzip2), (".tbz2", .tarBzip2),
        (".tar.xz", .tarXZ), (".txz", .tarXZ),
        (".zip", .zip), (".cbz", .zip),
        (".rar", .rar), (".cbr", .rar),
        (".7z", .sevenZip),
        (".tar", .tar),
        (".gz", .gzip),
        (".bz2", .bzip2),
        (".xz", .xz),
    ]

    /// The format `name` claims to be, or nil if it claims none.
    ///
    /// Container formats that happen to be zips underneath — `.docx`,
    /// `.ipa`, `.epub` — are **not** listed. Double-clicking a Word document
    /// has to open Word; a user who genuinely wants to see inside one can
    /// rename it.
    static func detect(fileName name: String) -> ArchiveFormat? {
        let lower = name.lowercased()
        for (suffix, format) in extensionTable where lower.hasSuffix(suffix) {
            // ".zip" on its own is a hidden file called "zip", not an archive.
            guard lower.count > suffix.count else { continue }
            return format
        }
        return nil
    }

    static func detect(_ url: URL) -> ArchiveFormat? {
        detect(fileName: url.lastPathComponent)
    }

    /// The name the extracted contents should go under: the archive's name
    /// with the archive extension taken off. `photos.tar.gz` → `photos`.
    static func baseName(ofArchive name: String) -> String {
        let lower = name.lowercased()
        for (suffix, _) in extensionTable where lower.hasSuffix(suffix) {
            guard lower.count > suffix.count else { continue }
            return String(name.dropLast(suffix.count))
        }
        return name
    }
}

/// The kinds of archive FileFluss can *write*.
///
/// A shorter list than `ArchiveFormat`. ZIP goes through `/usr/bin/zip`, and
/// 7-Zip and the three tarballs through `/usr/bin/bsdtar`, both of which ship
/// with macOS. RAR is the odd one out: its compression is proprietary, there
/// is no free encoder, and libarchive can read RAR but not write it. The only
/// way to make one is RARLAB's own `rar` tool, so the format is offered when
/// that tool is installed and explained when it isn't.
enum ArchiveCreationFormat: String, CaseIterable, Sendable {
    case zip
    case sevenZip
    case tarGzip
    case tarBzip2
    case tarXZ
    case rar

    var fileExtension: String {
        switch self {
        case .zip: return "zip"
        case .sevenZip: return "7z"
        case .tarGzip: return "tar.gz"
        case .tarBzip2: return "tar.bz2"
        case .tarXZ: return "tar.xz"
        case .rar: return "rar"
        }
    }

    /// Menu wording. Format names, so not translated.
    var displayName: String {
        switch self {
        case .zip: return "ZIP"
        case .sevenZip: return "7-Zip (.7z)"
        case .tarGzip: return "TAR.GZ"
        case .tarBzip2: return "TAR.BZ2"
        case .tarXZ: return "TAR.XZ"
        case .rar: return "RAR"
        }
    }

    /// Where RARLAB's command-line tool lives, if it is installed: the copy
    /// FileFluss downloads itself (see `RarInstaller`) first, then the places
    /// a user who installed it by hand usually puts it.
    static var rarTool: String? {
        [RarInstaller.installedTool.path, "/opt/homebrew/bin/rar", "/usr/local/bin/rar"]
            .first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    var isAvailable: Bool {
        self == .rar ? Self.rarTool != nil : true
    }

    /// The archive name for compressing `items` — one item gives
    /// `<name>.<ext>`, several give `Archive.<ext>`, like Finder — made
    /// unique against `isTaken` as `Archive 2.tar.gz` rather than the
    /// `Archive.tar 2.gz` a single-extension rule would produce.
    func archiveName(forItemNames items: [String], isTaken: (String) -> Bool) -> String {
        let base = items.count == 1 ? items[0] : "Archive"
        let first = "\(base).\(fileExtension)"
        guard isTaken(first) else { return first }
        var counter = 2
        while isTaken("\(base) \(counter).\(fileExtension)") { counter += 1 }
        return "\(base) \(counter).\(fileExtension)"
    }
}
