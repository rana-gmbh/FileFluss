#if os(macOS)
import Foundation
import os

private let ftpLog = Logger(subsystem: "com.rana.FileFluss", category: "ftp")

public struct FTPCredentials: Codable, Sendable {
    public let host: String
    public let port: Int
    public let username: String
    public let password: String
    /// Initial directory the panel lands in. "/" means the login home dir.
    public let remotePath: String
    /// Use FTPS (explicit AUTH TLS over the control channel).
    public let useTLS: Bool
    /// Accept self-signed / otherwise untrusted TLS certificates (FTPS only).
    public let allowInvalidCertificate: Bool

    public init(
        host: String,
        port: Int = 21,
        username: String,
        password: String,
        remotePath: String = "/",
        useTLS: Bool = false,
        allowInvalidCertificate: Bool = false
    ) {
        self.host = host
        self.port = port
        self.username = username
        self.password = password
        self.remotePath = remotePath
        self.useTLS = useTLS
        self.allowInvalidCertificate = allowInvalidCertificate
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        host = try c.decode(String.self, forKey: .host)
        port = try c.decodeIfPresent(Int.self, forKey: .port) ?? 21
        username = try c.decode(String.self, forKey: .username)
        password = try c.decodeIfPresent(String.self, forKey: .password) ?? ""
        remotePath = try c.decodeIfPresent(String.self, forKey: .remotePath) ?? "/"
        useTLS = try c.decodeIfPresent(Bool.self, forKey: .useTLS) ?? false
        allowInvalidCertificate = try c.decodeIfPresent(Bool.self, forKey: .allowInvalidCertificate) ?? false
    }
}

/// FTP / FTPS provider implemented by shelling out to the system `curl`
/// (same approach as the SFTP provider, which drives `/usr/bin/sftp`). curl
/// ships with macOS and speaks FTP, FTPS, passive mode, and TLS, so we get a
/// dependency-free client. Directory listings come back as Unix `ls -l` text,
/// which we parse with the same parser the SFTP client uses.
public actor FTPAPIClient {
    let credentials: FTPCredentials

    public init(credentials: FTPCredentials) {
        self.credentials = credentials
    }

    // MARK: - Authentication

    public static func authenticate(
        host: String,
        port: Int,
        username: String,
        password: String,
        remotePath: String = "/",
        useTLS: Bool = false,
        allowInvalidCertificate: Bool = false
    ) async throws -> FTPCredentials {
        let creds = FTPCredentials(
            host: host, port: port, username: username, password: password,
            remotePath: remotePath, useTLS: useTLS, allowInvalidCertificate: allowInvalidCertificate
        )
        let client = FTPAPIClient(credentials: creds)
        // Verify by listing the landing directory.
        _ = try await client.listFolder(path: remotePath.isEmpty ? "/" : remotePath)
        ftpLog.info("[FTP] Authenticated as \(username)@\(host):\(port) (TLS: \(useTLS))")
        return creds
    }

    public func userDisplayName() -> String {
        "\(credentials.username)@\(credentials.host)"
    }

    // MARK: - File operations

    public func listFolder(path: String) async throws -> [CloudFileItem] {
        // A trailing slash tells curl to LIST the directory (full `ls -l`
        // style output) rather than retrieve a file of that name.
        let output = try await runCurl([directoryURL(for: path)])
        return try parseListing(output: output, basePath: normalized(path))
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        // curl writes as it goes, so it gets a name of its own: a drop part
        // way must not leave a short file, or cost the file already there.
        let partial = localURL.deletingLastPathComponent()
            .appendingPathComponent(FileSystemService.temporaryReplacementName(for: localURL.lastPathComponent))
        do {
            _ = try await runCurl(["--output", partial.path, fileURL(for: remotePath)])
            try FileSystemService.placeDownload(from: partial, at: localURL)
        } catch {
            try? FileManager.default.removeItem(at: partial)
            throw error
        }
        if let onBytes,
           let size = try? FileManager.default.attributesOfItem(atPath: localURL.path)[.size] as? Int64 {
            onBytes(size)
        }
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        let size = (try? FileManager.default.attributesOfItem(atPath: localURL.path))?[.size] as? Int64 ?? 0
        _ = try await runCurl(["--upload-file", localURL.path, fileURL(for: remotePath)])
        onBytes?(size)
    }

    public func createFolder(at path: String) async throws {
        do {
            _ = try await runCurl(["-Q", "MKD \(serverPath(path))", rootURL()])
        } catch {
            // MKD returns 550 when the directory already exists, and curl
            // reports that as a fatal "QUOT command failed". Treat creating an
            // existing folder as success (idempotent "ensure folder" — needed
            // when uploading several files into the same subfolder, or copying
            // a folder tree). Only rethrow when the folder really isn't there.
            if (try? await listFolder(path: path)) != nil {
                return
            }
            throw error
        }
    }

    public func deleteItem(at path: String, isDirectory: Bool) async throws {
        if isDirectory {
            // RMD only removes empty directories — clear contents first.
            let items = try await listFolder(path: path)
            for item in items {
                try await deleteItem(at: item.path, isDirectory: item.isDirectory)
            }
            _ = try await runCurl(["-Q", "RMD \(serverPath(path))", rootURL()])
        } else {
            _ = try await runCurl(["-Q", "DELE \(serverPath(path))", rootURL()])
        }
    }

    public func renameItem(at path: String, to newName: String) async throws {
        let parent = (path as NSString).deletingLastPathComponent
        let destination = parent == "/" ? "/\(newName)" : "\(parent)/\(newName)"
        // RNFR then RNTO, in order — curl runs -Q commands in the order given.
        _ = try await runCurl([
            "-Q", "RNFR \(serverPath(path))",
            "-Q", "RNTO \(serverPath(destination))",
            rootURL(),
        ])
    }

    public func getFileInfo(at path: String) async throws -> CloudFileItem {
        let parent = (path as NSString).deletingLastPathComponent
        let name = (path as NSString).lastPathComponent
        let items = try await listFolder(path: parent.isEmpty ? "/" : parent)
        guard let item = items.first(where: { $0.name == name }) else {
            throw CloudProviderError.notFound(path)
        }
        return item
    }

    public func folderSize(path: String) async throws -> Int64 {
        let items = try await listFolder(path: path)
        var total: Int64 = 0
        for item in items {
            if item.isDirectory {
                total += try await folderSize(path: item.path)
            } else {
                total += item.size
            }
        }
        return total
    }

    public func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
        let root = path ?? "/"
        let all = try await listAllRecursively(path: root)
        let lowered = query.lowercased()
        return all.filter { $0.name.lowercased().contains(lowered) }
    }

    private func listAllRecursively(path: String) async throws -> [CloudFileItem] {
        let items = try await listFolder(path: path)
        var result = items
        for item in items where item.isDirectory {
            result.append(contentsOf: try await listAllRecursively(path: item.path))
        }
        return result
    }

    // MARK: - URL building
    //
    // The account view is rooted at the FTP login home directory, so app paths
    // are mapped relative to it (the leading "/" is stripped). curl's default
    // multicwd FTP method then CWDs through each path component.

    private func scheme() -> String { "ftp" }

    private func normalized(_ path: String) -> String {
        path.isEmpty ? "/" : path
    }

    /// App path → server path relative to the login dir (no leading slash).
    private func serverPath(_ path: String) -> String {
        var p = path
        while p.hasPrefix("/") { p.removeFirst() }
        return p
    }

    private func encodedPath(_ path: String) -> String {
        serverPath(path)
            .split(separator: "/", omittingEmptySubsequences: true)
            .map { String($0).addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? String($0) }
            .joined(separator: "/")
    }

    private func base() -> String { "\(scheme())://\(credentials.host):\(credentials.port)/" }

    private func rootURL() -> String { base() }

    private func directoryURL(for path: String) -> String {
        let enc = encodedPath(path)
        if enc.isEmpty { return base() }
        return base() + enc + "/"
    }

    private func fileURL(for path: String) -> String {
        base() + encodedPath(path)
    }

    // MARK: - curl execution

    private func runCurl(_ extraArgs: [String]) async throws -> String {
        // Credentials go in a 0600 config file so the password never appears
        // in the process argument list (visible via `ps`).
        let cfgPath = NSTemporaryDirectory() + "filefluss-ftp-\(UUID().uuidString.prefix(8)).cfg"
        let escapedUser = Self.escapeForConfig(credentials.username)
        let escapedPass = Self.escapeForConfig(credentials.password)
        let cfg = "user = \"\(escapedUser):\(escapedPass)\"\n"
        FileManager.default.createFile(atPath: cfgPath, contents: Data(cfg.utf8), attributes: [.posixPermissions: 0o600])
        defer { try? FileManager.default.removeItem(atPath: cfgPath) }

        var args = [
            "--config", cfgPath,
            "--silent", "--show-error",
            "--connect-timeout", "15",
            // Give up on a stalled connection, not a slow one: a time limit
            // on the whole command would kill any transfer over ten minutes.
            "--speed-limit", "1",
            "--speed-time", "120",
        ]
        if credentials.useTLS {
            args.append("--ssl-reqd")
            if credentials.allowInvalidCertificate { args.append("--insecure") }
        }
        args.append(contentsOf: extraArgs)

        let cancellation = ProcessCancellation()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                let process = Process()
                process.executableURL = URL(fileURLWithPath: "/usr/bin/curl")
                process.arguments = args

                let stdoutPipe = Pipe()
                let stderrPipe = Pipe()
                process.standardOutput = stdoutPipe
                process.standardError = stderrPipe
                process.standardInput = FileHandle.nullDevice

                let stdoutDrain = PipeDrain(stdoutPipe)
                let stderrDrain = PipeDrain(stderrPipe)

                process.terminationHandler = { proc in
                    let stdout = String(data: stdoutDrain.collected(), encoding: .utf8) ?? ""
                    let stderr = String(data: stderrDrain.collected(), encoding: .utf8) ?? ""
                    if proc.terminationStatus != 0 {
                        continuation.resume(throwing: Self.mapCurlError(exit: Int(proc.terminationStatus), stderr: stderr))
                        return
                    }
                    continuation.resume(returning: stdout)
                }

                do {
                    try process.run()
                    cancellation.started(process)
                } catch {
                    // Never started, so nothing will close these; the drains
                    // would otherwise wait on them forever.
                    try? stdoutPipe.fileHandleForWriting.close()
                    try? stderrPipe.fileHandleForWriting.close()
                    continuation.resume(throwing: CloudProviderError.networkError(error))
                }
            }
        } onCancel: {
            cancellation.cancel()
        }
    }

    /// Escape a value for inclusion in a double-quoted curl config entry.
    private static func escapeForConfig(_ value: String) -> String {
        value.replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
    }

    /// Map curl's documented FTP exit codes to useful errors.
    private static func mapCurlError(exit: Int, stderr: String) -> CloudProviderError {
        let trimmed = stderr.trimmingCharacters(in: .whitespacesAndNewlines)
        switch exit {
        case 67: return .invalidCredentials // login denied
        case 6: return .commandFailed(L10n.text("Could not resolve host. Check the server address."))
        case 7: return .commandFailed(L10n.text("Could not connect — check the host, port, and that FTP is reachable."))
        case 28: return .commandFailed(L10n.text("Connection timed out."))
        case 9: return .commandFailed(trimmed.isEmpty ? L10n.text("Access denied to that path on the server.") : trimmed)
        case 19, 78: return .notFound(trimmed.isEmpty ? L10n.text("File or directory not found.") : trimmed)
        case 60, 35, 58, 59, 64, 77: // TLS/cert errors
            return .commandFailed(L10n.format("TLS/SSL error: %@", trimmed.isEmpty ? L10n.text("the server's certificate could not be verified.") : trimmed))
        default:
            return .commandFailed(trimmed.isEmpty ? L10n.format("FTP command failed (curl exit %d).", exit) : trimmed)
        }
    }

    // MARK: - Listing parse

    /// FTP `LIST` output is the server's directory listing — usually Unix
    /// `ls -l` style, which the SFTP client already parses, and otherwise the
    /// MS-DOS style of IIS and other Windows servers (`parseWindowsListingLine`).
    private func parseListing(output: String, basePath: String) throws -> [CloudFileItem] {
        try Self.parseListing(output: output, basePath: basePath)
    }

    static func parseListing(output: String, basePath: String) throws -> [CloudFileItem] {
        var items: [CloudFileItem] = []
        for line in output.components(separatedBy: "\n") {
            guard let parsed = SFTPAPIClient.parseListingLine(line) ?? parseWindowsListingLine(line) else {
                // A Windows-style row nobody could read would leave the
                // folder looking emptier than it is — and Mirror deletes
                // what a folder seems to lack. `validateListing` catches the
                // Unix-style ones.
                if looksLikeWindowsListingRow(line) {
                    throw CloudProviderError.unreadableListing
                }
                continue
            }
            let itemPath = basePath == "/" ? "/\(parsed.name)" : "\(basePath)/\(parsed.name)"
            items.append(CloudFileItem(
                id: parsed.isDirectory ? "d\(itemPath.hashValue)" : "f\(itemPath.hashValue)",
                name: parsed.name,
                path: itemPath,
                isDirectory: parsed.isDirectory,
                size: parsed.isDirectory ? 0 : parsed.size,
                modificationDate: parsed.modDate,
                checksum: nil
            ))
        }
        if items.isEmpty, SFTPAPIClient.outputLooksLikeListing(output) {
            ftpLog.error("[FTP] Folder \(basePath, privacy: .public) parsed 0 items from listing-shaped output; unrecognised format. Sample: \(output.prefix(500), privacy: .public)")
        }
        try SFTPAPIClient.validateListing(
            output: output,
            itemCount: items.count,
            everyLineIsARow: true,
            alsoUnderstood: { parseWindowsListingLine($0) != nil }
        )
        return items
    }

    // MARK: - Windows-style listings

    /// `11-21-26  12:41PM       <DIR>          Photos` or
    /// `11-21-26  12:41PM                  775 config.json`: the MS-DOS
    /// style IIS and other Windows FTP servers send. Some write a
    /// four-digit year or a 24-hour time.
    private static let windowsListingRegex = try? NSRegularExpression(
        pattern: #"^(\d{1,2})-(\d{1,2})-(\d{4}|\d{2})\s+(\d{1,2}):(\d{2})\s*([AaPp][Mm])?\s+(<DIR>|\d+)\s+(.+)$"#
    )

    /// A row that starts like a Windows-style listing row: a date.
    private static let windowsRowStartRegex = try? NSRegularExpression(pattern: #"^\d{1,2}-\d{1,2}-\d{2,4}\s"#)

    /// One row of a Windows-style listing, or nil if the line isn't one.
    static func parseWindowsListingLine(_ line: String) -> SFTPAPIClient.ParsedListingLine? {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        let range = NSRange(trimmed.startIndex..., in: trimmed)
        guard let regex = windowsListingRegex,
              let match = regex.firstMatch(in: trimmed, range: range) else { return nil }
        func text(_ group: Int) -> String? {
            Range(match.range(at: group), in: trimmed).map { String(trimmed[$0]) }
        }
        guard let month = text(1).flatMap(Int.init), let day = text(2).flatMap(Int.init),
              let yearText = text(3), var year = Int(yearText),
              var hour = text(4).flatMap(Int.init), let minute = text(5).flatMap(Int.init),
              let sizeOrDir = text(7), let name = text(8),
              (1...12).contains(month), (1...31).contains(day), name != ".", name != ".." else {
            return nil
        }
        if yearText.count == 2 {
            // As Windows itself reads two-digit years.
            year += year < 70 ? 2000 : 1900
        }
        if let meridiem = text(6)?.uppercased() {
            guard (1...12).contains(hour) else { return nil }
            if meridiem == "AM", hour == 12 { hour = 0 }
            if meridiem == "PM", hour != 12 { hour += 12 }
        }
        let date = Calendar.current.date(from: DateComponents(
            year: year, month: month, day: day, hour: hour, minute: minute
        )) ?? .distantPast
        let isDirectory = sizeOrDir.uppercased() == "<DIR>"
        return SFTPAPIClient.ParsedListingLine(
            perms: isDirectory ? "d" : "-",
            size: isDirectory ? 0 : Int64(sizeOrDir) ?? 0,
            isDirectory: isDirectory,
            modDate: date,
            name: name
        )
    }

    static func looksLikeWindowsListingRow(_ line: String) -> Bool {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let regex = windowsRowStartRegex else { return false }
        return regex.firstMatch(in: trimmed, range: NSRange(trimmed.startIndex..., in: trimmed)) != nil
    }
}
#endif
