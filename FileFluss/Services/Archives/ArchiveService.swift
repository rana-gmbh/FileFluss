import Foundation
import FileFlussCore

enum ArchiveError: LocalizedError {
    /// libarchive refused the file. Carries its own message, which names the
    /// real reason far better than anything this layer could invent.
    case unreadable(String)
    /// The entry list was readable but the data is encrypted, and FileFluss
    /// has no passphrase prompt yet.
    case encrypted
    /// `zip` or `ditto` exited non-zero.
    case toolFailed(tool: String, status: Int32, output: String)
    case toolMissing(String)
    case alreadyExists(String)
    /// An entry's path would have written outside the destination.
    case unsafeEntry(String)

    var errorDescription: String? {
        switch self {
        case .unreadable(let message):
            return L10n.format("The archive could not be read: %@", message)
        case .encrypted:
            return L10n.text("This archive is password-protected, which FileFluss cannot open yet.")
        case .toolFailed(let tool, let status, let output):
            let detail = output.trimmingCharacters(in: .whitespacesAndNewlines)
            return detail.isEmpty
                ? L10n.format("%@ failed with status %d.", tool, Int(status))
                : L10n.format("%@ failed: %@", tool, detail)
        case .toolMissing(let tool):
            return L10n.format("%@ is missing from this system.", tool)
        case .alreadyExists(let name):
            return L10n.format("\"%@\" already exists.", name)
        case .unsafeEntry(let name):
            return L10n.format("The archive contains an unsafe entry name (\"%@\"), so it was skipped.", name)
        }
    }
}

/// Reads archives through the system libarchive and writes zips through
/// `/usr/bin/zip`.
///
/// Free functions rather than an actor, deliberately. Every call builds its
/// own libarchive handle and shares nothing, so there is no state to protect
/// — and an actor would serialise calls, which is the wrong trade here:
/// unpacking a 3 GB archive in one panel must not stall listing a small one
/// in the other. Each `async` entry point hands its work to a detached task,
/// so no caller can put this on the main actor by accident.
enum ArchiveService {

    /// 256 KB: big enough that the per-call overhead disappears, small enough
    /// that progress keeps moving and a cancel is noticed promptly.
    private static let blockSize = 256 * 1024

    // MARK: - Listing

    /// Reads the archive's entry list. One pass over the headers: for a zip
    /// that is a seek to the central directory, for rar/7z/tar a walk through
    /// the file.
    static func list(_ url: URL, format: ArchiveFormat) async throws -> ArchiveTree {
        try await offMainActor { try listSync(url, format: format) }
    }

    static func listSync(_ url: URL, format: ArchiveFormat) throws -> ArchiveTree {
        var raw: [ArchiveEntry] = []
        try withReader(url, format: format) { handle in
            while let next = try nextEntry(handle, format: format, archiveURL: url) {
                if let entry = next.entry { raw.append(entry) }
                archive_read_data_skip(handle)
            }
        }
        return ArchiveTree(rawEntries: raw)
    }

    // MARK: - Extraction

    /// Writes the named entries into `destination`, dropping `prefix` from
    /// the front of each path — so a folder extracted out of
    /// `docs/reports/2026` arrives as `2026`, not as four nested folders.
    /// Returns the top-level items written, which is what a drag has to drop
    /// and a copy has to report.
    ///
    /// One pass over the archive however many entries were asked for:
    /// libarchive is a stream reader, and in a solid rar or 7z there is no
    /// cheaper way to reach the tenth entry than to walk the first nine.
    @discardableResult
    static func extract(
        entryPaths: Set<String>,
        from url: URL,
        format: ArchiveFormat,
        strippingPrefix prefix: String,
        into destination: URL,
        onBytes: (@Sendable (Int64) -> Void)? = nil
    ) async throws -> [URL] {
        try await offMainActor {
            try extractSync(
                entryPaths: entryPaths, from: url, format: format,
                strippingPrefix: prefix, into: destination, onBytes: onBytes
            )
        }
    }

    /// Unpacks the whole archive into `destination`, which must already exist.
    static func extractAll(
        from url: URL,
        format: ArchiveFormat,
        into destination: URL,
        onBytes: (@Sendable (Int64) -> Void)? = nil
    ) async throws {
        // Listed first, whichever tool unpacks it: an encrypted archive has
        // to be reported as one. ditto's own failure on a locked zip says
        // nothing about a password, and that is the one failure a user can
        // actually do something about.
        let tree = try await list(url, format: format)
        guard !tree.containsEncryptedEntries else { throw ArchiveError.encrypted }

        // A zip then goes through ditto, because a zip made on a Mac carries
        // resource forks and Finder metadata in `__MACOSX` AppleDouble
        // entries and ditto is the only tool that folds them back onto the
        // files. libarchive would leave a stray `__MACOSX` folder behind and
        // lose Finder tags and custom icons on a round trip.
        if format == .zip {
            try await runTool("/usr/bin/ditto", ["-x", "-k", url.path, destination.path], currentDirectory: nil)
            return
        }
        _ = try await extract(
            entryPaths: Set(tree.entries.map(\.path)),
            from: url, format: format, strippingPrefix: "",
            into: destination, onBytes: onBytes
        )
    }

    /// The blocking form. Callers that are already on a background queue of
    /// their own — the file-promise provider, which AppKit invokes on the
    /// queue it was handed — use this and skip the detour through a task.
    static func extractSync(
        entryPaths: Set<String>,
        from url: URL,
        format: ArchiveFormat,
        strippingPrefix prefix: String,
        into destination: URL,
        onBytes: (@Sendable (Int64) -> Void)?
    ) throws -> [URL] {
        let fm = FileManager.default
        try fm.createDirectory(at: destination, withIntermediateDirectories: true)

        var written: [URL] = []
        var remaining = entryPaths
        var buffer = [UInt8](repeating: 0, count: blockSize)
        // Directory dates are applied at the end: writing into a directory
        // bumps its mtime, so one stamped at creation would be wrong again by
        // the time its contents land.
        var directoryDates: [(URL, Date)] = []

        try withReader(url, format: format) { handle in
            while let next = try nextEntry(handle, format: format, archiveURL: url) {
                try Task.checkCancellation()
                guard let entry = next.entry, remaining.contains(entry.path) else {
                    archive_read_data_skip(handle)
                    continue
                }
                remaining.remove(entry.path)
                let target = try Self.targetURL(
                    for: entry.path, strippingPrefix: prefix, in: destination
                )
                if ArchiveEntry.parentPath(of: entry.path) == prefix {
                    written.append(target)
                }

                if entry.isDirectory {
                    try fm.createDirectory(at: target, withIntermediateDirectories: true)
                    directoryDates.append((target, entry.modificationDate))
                } else if entry.isSymlink {
                    try fm.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
                    try writeSymlink(for: next.pointer, at: target, entryPath: entry.path)
                } else if entry.isEncrypted {
                    throw ArchiveError.encrypted
                } else {
                    try fm.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
                    try writeFile(
                        from: handle, to: target, entry: entry, buffer: &buffer, onBytes: onBytes
                    )
                }

                archive_read_data_skip(handle)
                if remaining.isEmpty { break }
            }
        }

        for (directory, date) in directoryDates {
            try? fm.setAttributes([.modificationDate: date], ofItemAtPath: directory.path)
        }
        return written
    }

    private static func writeFile(
        from handle: OpaquePointer,
        to target: URL,
        entry: ArchiveEntry,
        buffer: inout [UInt8],
        onBytes: (@Sendable (Int64) -> Void)?
    ) throws {
        let fm = FileManager.default
        // Replace rather than append: extracting twice into the same staging
        // folder must not double a file's contents.
        if fm.fileExists(atPath: target.path) { try fm.removeItem(at: target) }
        guard fm.createFile(atPath: target.path, contents: nil) else {
            throw ArchiveError.unreadable(
                L10n.format("Could not create \"%@\".", target.lastPathComponent)
            )
        }
        let fileHandle = try FileHandle(forWritingTo: target)
        defer { try? fileHandle.close() }

        while true {
            try Task.checkCancellation()
            let count = buffer.withUnsafeMutableBytes {
                archive_read_data(handle, $0.baseAddress, size_t($0.count))
            }
            if count == 0 { break }
            guard count > 0 else { throw ArchiveError.unreadable(errorMessage(handle)) }
            let chunk = buffer.withUnsafeBytes { Data($0[0..<count]) }
            try fileHandle.write(contentsOf: chunk)
            onBytes?(Int64(count))
        }
        try? fileHandle.close()
        try? fm.setAttributes([.modificationDate: entry.modificationDate], ofItemAtPath: target.path)
    }

    /// Where `entryPath` lands under `root`.
    ///
    /// `ArchiveEntry.normalise` has already refused any `..`, so this is the
    /// second of two independent checks — the same belt-and-braces rule
    /// `SafeLocalPath` applies to names that arrive over a network.
    static func targetURL(
        for entryPath: String, strippingPrefix prefix: String, in root: URL
    ) throws -> URL {
        let relative: String
        if prefix.isEmpty {
            relative = entryPath
        } else if entryPath.hasPrefix(prefix + "/") {
            relative = String(entryPath.dropFirst(prefix.count + 1))
        } else {
            // Not under the prefix at all. Keep its own name rather than
            // write it somewhere it was never meant to go.
            relative = String(entryPath.split(separator: "/").last ?? "")
        }
        var url = root
        for component in relative.split(separator: "/") {
            url.appendPathComponent(String(component))
        }
        let rootPath = root.standardizedFileURL.path
        let resolved = url.standardizedFileURL.path
        guard resolved.hasPrefix(rootPath.hasSuffix("/") ? rootPath : rootPath + "/") else {
            throw ArchiveError.unsafeEntry(entryPath)
        }
        return url
    }

    /// Recreates a symlink entry.
    ///
    /// Absolute targets, and targets containing `..`, are refused. A symlink
    /// is otherwise a two-step escape from the destination — write
    /// `link -> /etc`, then write `link/passwd` — and nothing FileFluss
    /// offers needs a link that points out of the archive.
    private static func writeSymlink(
        for pointer: OpaquePointer, at target: URL, entryPath: String
    ) throws {
        guard let raw = archive_entry_symlink(pointer) else { return }
        let linkTarget = String(cString: raw)
        guard !linkTarget.isEmpty,
              !linkTarget.hasPrefix("/"),
              !linkTarget.split(separator: "/").contains("..") else {
            throw ArchiveError.unsafeEntry(entryPath)
        }
        let fm = FileManager.default
        if fm.fileExists(atPath: target.path) { try? fm.removeItem(at: target) }
        try fm.createSymbolicLink(atPath: target.path, withDestinationPath: linkTarget)
    }

    // MARK: - Creation

    /// Packs `names` (plain file names, relative to `directory`) into a new
    /// archive in the same folder, and returns it.
    ///
    /// ZIP uses `/usr/bin/zip` rather than `ditto`: ditto takes a single
    /// source, so a multi-row selection would need a second code path.
    /// Info-ZIP takes the whole selection, recurses with `-r`, stores
    /// symlinks as symlinks with `-y` instead of following them out of the
    /// folder, and writes macOS metadata the same way Finder does. 7-Zip and
    /// the tarballs go through `bsdtar`, the libarchive front end that ships
    /// with macOS. RAR needs RARLAB's own tool (see `ArchiveCreationFormat`).
    ///
    /// Every tool runs verbose, because the one line per file it prints is
    /// the only progress there is to show.
    static func createArchive(
        names: [String],
        in directory: URL,
        archiveName: String,
        format: ArchiveCreationFormat,
        onFileAdded: (@Sendable (String) -> Void)? = nil
    ) async throws -> URL {
        let archiveURL = directory.appendingPathComponent(archiveName)
        // zip and rar *append* to an archive that already exists, which
        // would silently merge this selection into an older file.
        guard !FileManager.default.fileExists(atPath: archiveURL.path) else {
            throw ArchiveError.alreadyExists(archiveName)
        }

        let executable: String
        let arguments: [String]
        switch format {
        case .zip:
            executable = "/usr/bin/zip"
            arguments = ["-r", "-y", archiveName] + names
        case .sevenZip:
            executable = "/usr/bin/bsdtar"
            arguments = ["-c", "-v", "--format", "7zip", "-f", archiveName, "--"] + names
        case .tarGzip, .tarBzip2, .tarXZ:
            let filter = format == .tarGzip ? "-z" : format == .tarBzip2 ? "-j" : "-J"
            executable = "/usr/bin/bsdtar"
            arguments = ["-c", "-v", filter, "-f", archiveName, "--"] + names
        case .rar:
            guard let tool = ArchiveCreationFormat.rarTool else {
                throw ArchiveError.toolMissing("rar")
            }
            executable = tool
            // -r recurses, -ol stores symlinks as links, -y answers any
            // prompt with yes so the tool never waits on a terminal that
            // isn't there.
            arguments = ["a", "-r", "-ol", "-y", archiveName, "--"] + names
        }

        try await runTool(
            executable,
            arguments,
            currentDirectory: directory,
            onOutput: { line in
                if let name = addedFileName(from: line) {
                    onFileAdded?(name)
                } else {
                    SupportLogger.shared.log(
                        "\((executable as NSString).lastPathComponent): \(line)", category: "archive"
                    )
                }
            }
        )
        return archiveURL
    }

    /// The ZIP case of `createArchive`.
    static func createZip(
        names: [String],
        in directory: URL,
        archiveName: String,
        onFileAdded: (@Sendable (String) -> Void)? = nil
    ) async throws -> URL {
        try await createArchive(
            names: names, in: directory, archiveName: archiveName,
            format: .zip, onFileAdded: onFileAdded
        )
    }

    /// The file name out of a tool's progress line, or nil for anything
    /// else it has to say. zip prints `  adding: sub/file.txt (deflated 62%)`,
    /// bsdtar `a sub/file.txt`, and rar `Adding    sub/file.txt    OK`.
    static func addedFileName(from line: String) -> String? {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        for prefix in ["adding: ", "updating: "] where trimmed.hasPrefix(prefix) {
            var name = String(trimmed.dropFirst(prefix.count))
            if let paren = name.range(of: " (", options: .backwards) {
                name = String(name[name.startIndex..<paren.lowerBound])
            }
            return name.isEmpty ? nil : name
        }
        if trimmed.hasPrefix("a "), trimmed.count > 2 {
            return String(trimmed.dropFirst(2))
        }
        if trimmed.hasPrefix("Adding ") {
            var name = String(trimmed.dropFirst("Adding ".count))
                .trimmingCharacters(in: .whitespaces)
            // rar pads the name and appends a percentage and "OK".
            if name.hasSuffix("OK") { name = String(name.dropLast(2)) }
            name = name.trimmingCharacters(in: .whitespaces)
            while let last = name.last, last == "%" || last.isNumber || last == "\u{8}" {
                name.removeLast()
            }
            name = name.trimmingCharacters(in: .whitespaces)
            return name.isEmpty ? nil : name
        }
        return nil
    }

    // MARK: - libarchive plumbing

    /// Opens `url`, runs `body`, and frees the handle whichever way `body`
    /// leaves.
    private static func withReader(
        _ url: URL, format: ArchiveFormat, _ body: (OpaquePointer) throws -> Void
    ) throws {
        guard let handle = archive_read_new() else {
            throw ArchiveError.unreadable(L10n.text("Out of memory."))
        }
        defer { archive_read_free(handle) }

        archive_read_support_filter_all(handle)
        archive_read_support_format_all(handle)
        // `.gz`, `.bz2` and `.xz` hold one compressed stream and no entry
        // list, which only libarchive's "raw" reader accepts. It is enabled
        // for those extensions alone: raw bids on *any* file, so enabling it
        // everywhere would make every double-clicked document look like a
        // one-entry archive.
        if format.isSingleStream {
            archive_read_support_format_raw(handle)
        }

        guard archive_read_open_filename(handle, url.path, size_t(blockSize)) == FF_ARCHIVE_OK else {
            throw ArchiveError.unreadable(errorMessage(handle))
        }
        try body(handle)
    }

    /// One header from the archive. `entry` is nil for a header that exists
    /// but should not be shown — AppleDouble metadata, or a path refused as
    /// unsafe.
    private struct RawEntry {
        let pointer: OpaquePointer
        let entry: ArchiveEntry?
    }

    private static func nextEntry(
        _ handle: OpaquePointer, format: ArchiveFormat, archiveURL: URL
    ) throws -> RawEntry? {
        var raw: OpaquePointer?
        let status = archive_read_next_header(handle, &raw)
        if status == FF_ARCHIVE_EOF { return nil }
        // A warning means the header was understood but something about it
        // wasn't; carrying on is what bsdtar does and loses nothing.
        if status < FF_ARCHIVE_WARN {
            throw ArchiveError.unreadable(errorMessage(handle))
        }
        guard let pointer = raw else { return nil }

        let declared = (archive_entry_pathname_utf8(pointer) ?? archive_entry_pathname(pointer))
            .map { String(cString: $0) }
        // The raw reader has no name to give — a `.gz` is a single nameless
        // stream, which libarchive calls "data" — so the archive's own name
        // minus its extension stands in.
        //
        // Checked against what was declared rather than against the
        // extension alone: a tarball misnamed `backup.gz` still reads as a
        // tar through the gzip filter, and naming all of its entries after
        // the archive would collapse them into one row.
        let isNameless = (declared?.isEmpty ?? true) || declared == "data"
        let rawName: String
        if format.isSingleStream, isNameless {
            rawName = ArchiveFormat.baseName(ofArchive: archiveURL.lastPathComponent)
        } else if let declared, !declared.isEmpty {
            rawName = declared
        } else {
            return RawEntry(pointer: pointer, entry: nil)
        }

        guard let path = ArchiveEntry.normalise(path: rawName) else {
            SupportLogger.shared.log(
                "skipped unsafe archive entry \"\(rawName)\" in \(archiveURL.lastPathComponent)",
                category: "archive", level: .error
            )
            return RawEntry(pointer: pointer, entry: nil)
        }
        // `__MACOSX/…` and `._name` hold the resource forks and Finder
        // metadata a Mac-made zip carries beside each file. They are not
        // files the user put there, and Finder doesn't show them either.
        if path == "__MACOSX" || path.hasPrefix("__MACOSX/") {
            return RawEntry(pointer: pointer, entry: nil)
        }
        if lastComponent(of: path).hasPrefix("._") {
            return RawEntry(pointer: pointer, entry: nil)
        }

        let fileType = archive_entry_filetype(pointer)
        let mask = mode_t(FF_AE_IFMT)
        let isDirectory = (fileType & mask) == mode_t(FF_AE_IFDIR)
        let isSymlink = (fileType & mask) == mode_t(FF_AE_IFLNK)
        let size = archive_entry_size_is_set(pointer) != 0 ? archive_entry_size(pointer) : 0
        let mtime = archive_entry_mtime(pointer)

        return RawEntry(pointer: pointer, entry: ArchiveEntry(
            path: path,
            isDirectory: isDirectory,
            isSymlink: isSymlink,
            size: isDirectory ? 0 : size,
            // An archive with no stored date reads as `distantPast`, which
            // the panel already renders as "--".
            modificationDate: mtime == 0 ? .distantPast : Date(timeIntervalSince1970: Double(mtime)),
            isEncrypted: archive_entry_is_encrypted(pointer) != 0
        ))
    }

    private static func lastComponent(of path: String) -> String {
        guard let slash = path.lastIndex(of: "/") else { return path }
        return String(path[path.index(after: slash)...])
    }

    private static func errorMessage(_ handle: OpaquePointer) -> String {
        if let raw = archive_error_string(handle) {
            let message = String(cString: raw)
            if !message.isEmpty { return message }
        }
        return L10n.format("Error %d.", Int(archive_errno(handle)))
    }

    // MARK: - Executor plumbing

    /// Runs blocking work off the main actor, and passes a cancel on.
    ///
    /// `Task.detached` on its own would leave the work running after the
    /// user hit Cancel — a detached task inherits nothing from its parent,
    /// cancellation included — so the handler forwards it explicitly.
    private static func offMainActor<T: Sendable>(
        _ work: @escaping @Sendable () throws -> T
    ) async throws -> T {
        let task = Task.detached(priority: .userInitiated, operation: work)
        return try await withTaskCancellationHandler {
            try await task.value
        } onCancel: {
            task.cancel()
        }
    }

    /// Holds a running helper tool so the cancellation handler can reach it.
    /// `Process` is not `Sendable` and the handler runs on whichever thread
    /// cancelled; only `interrupt()` is called across that boundary, which
    /// Foundation permits.
    private final class ToolProcess: @unchecked Sendable {
        private let lock = NSLock()
        private var process: Process?
        private var interrupted = false

        func adopt(_ process: Process) -> Bool {
            lock.lock()
            defer { lock.unlock() }
            guard !interrupted else { return false }
            self.process = process
            return true
        }

        /// SIGINT rather than SIGTERM: zip handles an interrupt by deleting
        /// the archive it was part-way through writing, where a terminate
        /// would leave both that and its `ziXXXXXX` temp file behind.
        func interrupt() {
            lock.lock()
            interrupted = true
            let running = process
            lock.unlock()
            running?.interrupt()
        }
    }

    private static func runTool(
        _ executable: String,
        _ arguments: [String],
        currentDirectory: URL?,
        onOutput: (@Sendable (String) -> Void)? = nil
    ) async throws {
        guard FileManager.default.isExecutableFile(atPath: executable) else {
            throw ArchiveError.toolMissing((executable as NSString).lastPathComponent)
        }
        let box = ToolProcess()
        try await withTaskCancellationHandler {
            try await offMainActor {
                let process = Process()
                process.executableURL = URL(fileURLWithPath: executable)
                process.arguments = arguments
                if let currentDirectory { process.currentDirectoryURL = currentDirectory }
                guard box.adopt(process) else { throw CancellationError() }

                let pipe = Pipe()
                process.standardOutput = pipe
                process.standardError = pipe
                try process.run()

                // Drained while the tool runs: the pipe buffer fills on a large
                // archive and the tool then blocks on write for good.
                var collected = Data()
                var pending = ""
                while true {
                    let chunk = pipe.fileHandleForReading.availableData
                    if chunk.isEmpty { break }
                    collected.append(chunk)
                    guard let onOutput else { continue }
                    pending += String(decoding: chunk, as: UTF8.self)
                    while let newline = pending.firstIndex(of: "\n") {
                        let line = String(pending[pending.startIndex..<newline])
                        pending = String(pending[pending.index(after: newline)...])
                        if !line.isEmpty { onOutput(line) }
                    }
                }
                process.waitUntilExit()
                try Task.checkCancellation()

                guard process.terminationStatus == 0 else {
                    throw ArchiveError.toolFailed(
                        tool: (executable as NSString).lastPathComponent,
                        status: process.terminationStatus,
                        output: String(decoding: collected.suffix(2048), as: UTF8.self)
                    )
                }
            }
        } onCancel: {
            box.interrupt()
        }
    }
}
