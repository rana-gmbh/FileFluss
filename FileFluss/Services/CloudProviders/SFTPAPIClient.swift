import Foundation
import os

private let sftpLog = Logger(subsystem: "com.rana.FileFluss", category: "sftp")

struct SFTPCredentials: Codable, Sendable {
    let host: String
    let port: Int
    let username: String
    let password: String
}

actor SFTPAPIClient {
    let credentials: SFTPCredentials
    private let controlPath: String
    private let passwordScriptPath: String

    init(credentials: SFTPCredentials) {
        self.credentials = credentials
        let id = UUID().uuidString.prefix(8)
        self.controlPath = NSTemporaryDirectory() + "filefluss-sftp-\(id)"
        self.passwordScriptPath = NSTemporaryDirectory() + "filefluss-sftp-askpass-\(id)"

        // Create SSH_ASKPASS script that echoes the password
        let escaped = credentials.password.replacingOccurrences(of: "'", with: "'\\''")
        let script = "#!/bin/sh\necho '\(escaped)'\n"
        FileManager.default.createFile(atPath: passwordScriptPath, contents: Data(script.utf8), attributes: [.posixPermissions: 0o700])
    }

    // MARK: - Authentication

    static func authenticate(host: String, port: Int, username: String, password: String) async throws -> SFTPCredentials {
        let creds = SFTPCredentials(host: host, port: port, username: username, password: password)
        let client = SFTPAPIClient(credentials: creds)

        // Verify connection by listing root
        let output = try await client.runBatch(commands: ["ls /"])
        sftpLog.info("[SFTP] Authenticated as \(username)@\(host):\(port)")
        _ = output  // Just verify it didn't throw
        return creds
    }

    func userDisplayName() -> String {
        "\(credentials.username)@\(credentials.host)"
    }

    // MARK: - File Operations

    func listFolder(path: String) async throws -> [CloudFileItem] {
        let output = try await runBatch(commands: ["ls -la \(shellEscape(path))"])
        return parseListing(output: output, basePath: path)
    }

    func downloadFile(remotePath: String, to localURL: URL) async throws {
        _ = try await runBatch(commands: ["get \(shellEscape(remotePath)) \(shellEscape(localURL.path))"])
    }

    func uploadFile(from localURL: URL, to remotePath: String) async throws {
        _ = try await runBatch(commands: ["put \(shellEscape(localURL.path)) \(shellEscape(remotePath))"])
    }

    func deleteItem(at path: String, isDirectory: Bool) async throws {
        if isDirectory {
            // Remove directory contents recursively, then the directory
            let items = try await listFolder(path: path)
            for item in items {
                try await deleteItem(at: item.path, isDirectory: item.isDirectory)
            }
            _ = try await runBatch(commands: ["rmdir \(shellEscape(path))"])
        } else {
            _ = try await runBatch(commands: ["rm \(shellEscape(path))"])
        }
    }

    func createFolder(at path: String) async throws {
        _ = try await runBatch(commands: ["mkdir \(shellEscape(path))"])
    }

    func renameItem(at path: String, to newName: String) async throws {
        let parentPath = (path as NSString).deletingLastPathComponent
        let destinationPath: String
        if parentPath == "/" {
            destinationPath = "/\(newName)"
        } else {
            destinationPath = "\(parentPath)/\(newName)"
        }
        _ = try await runBatch(commands: ["rename \(shellEscape(path)) \(shellEscape(destinationPath))"])
    }

    func getFileInfo(at path: String) async throws -> CloudFileItem {
        let parentPath = (path as NSString).deletingLastPathComponent
        let fileName = (path as NSString).lastPathComponent
        let output = try await runBatch(commands: ["ls -la \(shellEscape(parentPath))"])
        let items = parseListing(output: output, basePath: parentPath)
        guard let item = items.first(where: { $0.name == fileName }) else {
            throw CloudProviderError.notFound(path)
        }
        return item
    }

    func folderSize(path: String) async throws -> Int64 {
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

    func searchFiles(query: String, path: String?) async throws -> [CloudFileItem] {
        let searchPath = path ?? "/"
        let allItems = try await listAllRecursively(path: searchPath)
        let lowered = query.lowercased()
        return allItems.filter { $0.name.lowercased().contains(lowered) }
    }

    private func listAllRecursively(path: String) async throws -> [CloudFileItem] {
        let items = try await listFolder(path: path)
        var result = items
        for item in items where item.isDirectory {
            let children = try await listAllRecursively(path: item.path)
            result.append(contentsOf: children)
        }
        return result
    }

    // MARK: - Process Execution

    private func runBatch(commands: [String]) async throws -> String {
        let batchContent = commands.joined(separator: "\n") + "\nbye\n"
        let batchPath = NSTemporaryDirectory() + "filefluss-sftp-batch-\(UUID().uuidString.prefix(8))"
        defer { try? FileManager.default.removeItem(atPath: batchPath) }

        guard FileManager.default.createFile(atPath: batchPath, contents: Data(batchContent.utf8)) else {
            throw CloudProviderError.invalidResponse
        }

        return try await withCheckedThrowingContinuation { continuation in
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/sftp")
            process.arguments = [
                "-oBatchMode=no",
                "-oStrictHostKeyChecking=accept-new",
                "-oControlMaster=auto",
                "-oControlPath=\(controlPath)",
                "-oControlPersist=600",
                "-oConnectTimeout=10",
                "-P", "\(credentials.port)",
                "-b", batchPath,
                "\(credentials.username)@\(credentials.host)"
            ]
            process.environment = [
                "SSH_ASKPASS": passwordScriptPath,
                "SSH_ASKPASS_REQUIRE": "force",
                "DISPLAY": ":0",
                "PATH": "/usr/bin:/bin:/usr/sbin:/sbin",
                "HOME": NSHomeDirectory()
            ]

            let stdoutPipe = Pipe()
            let stderrPipe = Pipe()
            process.standardOutput = stdoutPipe
            process.standardError = stderrPipe
            process.standardInput = FileHandle.nullDevice

            process.terminationHandler = { proc in
                let stdoutData = stdoutPipe.fileHandleForReading.readDataToEndOfFile()
                let stderrData = stderrPipe.fileHandleForReading.readDataToEndOfFile()
                let stdout = String(data: stdoutData, encoding: .utf8) ?? ""
                let stderr = String(data: stderrData, encoding: .utf8) ?? ""

                // Debug: write to log file
                let debugLine = "[SFTP] exit=\(proc.terminationStatus) stdout=\(stdout.prefix(500)) stderr=\(stderr.prefix(500))\n"
                let logPath = "/tmp/filefluss-sftp.log"
                if let fh = FileHandle(forWritingAtPath: logPath) {
                    fh.seekToEndOfFile()
                    fh.write(Data(debugLine.utf8))
                    fh.closeFile()
                } else {
                    FileManager.default.createFile(atPath: logPath, contents: Data(debugLine.utf8))
                }

                if proc.terminationStatus != 0 {
                    let errMsg = stderr.trimmingCharacters(in: .whitespacesAndNewlines)
                    // Also check stdout for error messages (sftp reports some errors there)
                    let combinedMsg = (errMsg + " " + stdout).trimmingCharacters(in: .whitespacesAndNewlines)

                    if combinedMsg.contains("Permission denied") || combinedMsg.contains("Authentication failed") {
                        continuation.resume(throwing: CloudProviderError.invalidCredentials)
                    } else if combinedMsg.contains("Connection refused") || combinedMsg.contains("No route to host") || combinedMsg.contains("Connection timed out") {
                        continuation.resume(throwing: CloudProviderError.serverError(-1))
                    } else if combinedMsg.contains("No such file") || combinedMsg.contains("not found") {
                        continuation.resume(throwing: CloudProviderError.notFound(errMsg))
                    } else if combinedMsg.contains("Couldn't") || combinedMsg.contains("failure") {
                        // sftp batch errors like "Couldn't create directory"
                        continuation.resume(throwing: CloudProviderError.notFound(combinedMsg))
                    } else {
                        continuation.resume(throwing: CloudProviderError.serverError(Int(proc.terminationStatus)))
                    }
                    return
                }
                continuation.resume(returning: stdout)
            }

            do {
                try process.run()
            } catch {
                continuation.resume(throwing: CloudProviderError.networkError(error))
            }
        }
    }

    // MARK: - Parsing

    private func parseListing(output: String, basePath: String) -> [CloudFileItem] {
        var items: [CloudFileItem] = []
        let lines = output.components(separatedBy: "\n")

        for line in lines {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard !trimmed.isEmpty else { continue }

            // Skip sftp prompt lines and non-listing lines
            guard trimmed.first == "-" || trimmed.first == "d" || trimmed.first == "l" || trimmed.first == "c" || trimmed.first == "b" || trimmed.first == "p" || trimmed.first == "s" else { continue }

            // Parse: permissions links owner group size month day time/year name
            // Use regex for robust parsing with variable whitespace
            let pattern = #"^([dlcbps-][rwxsStT@+-]{9,})\s+(\d+)\s+(\S+)\s+(\S+)\s+(\d+)\s+(\w+)\s+(\d{1,2})\s+([\d:]+|\d{4})\s+(.+)$"#
            guard let regex = try? NSRegularExpression(pattern: pattern),
                  let match = regex.firstMatch(in: trimmed, range: NSRange(trimmed.startIndex..., in: trimmed)) else {
                continue
            }

            guard let permsRange = Range(match.range(at: 1), in: trimmed),
                  let sizeRange = Range(match.range(at: 5), in: trimmed),
                  let monthRange = Range(match.range(at: 6), in: trimmed),
                  let dayRange = Range(match.range(at: 7), in: trimmed),
                  let timeRange = Range(match.range(at: 8), in: trimmed),
                  let nameRange = Range(match.range(at: 9), in: trimmed) else {
                continue
            }

            let perms = String(trimmed[permsRange])
            let size = Int64(trimmed[sizeRange]) ?? 0
            let month = String(trimmed[monthRange])
            let day = String(trimmed[dayRange])
            let timeOrYear = String(trimmed[timeRange])
            var name = Self.unescapeOctal(String(trimmed[nameRange]))

            // Skip . and ..
            if name == "." || name == ".." { continue }

            // Handle symlinks: "name -> target"
            if perms.first == "l", let arrowRange = name.range(of: " -> ") {
                name = String(name[name.startIndex..<arrowRange.lowerBound])
            }

            let isDirectory = perms.first == "d"
            let modDate = Self.parseDate(month: month, day: day, timeOrYear: timeOrYear)

            let itemPath: String
            if basePath == "/" {
                itemPath = "/\(name)"
            } else {
                itemPath = "\(basePath)/\(name)"
            }

            let item = CloudFileItem(
                id: isDirectory ? "d\(itemPath.hashValue)" : "f\(itemPath.hashValue)",
                name: name,
                path: itemPath,
                isDirectory: isDirectory,
                size: isDirectory ? 0 : size,
                modificationDate: modDate,
                checksum: nil
            )
            items.append(item)
        }

        return items
    }

    private static func parseDate(month: String, day: String, timeOrYear: String) -> Date {
        let calendar = Calendar.current
        let now = Date()
        let currentYear = calendar.component(.year, from: now)

        let months = ["Jan": 1, "Feb": 2, "Mar": 3, "Apr": 4, "May": 5, "Jun": 6,
                       "Jul": 7, "Aug": 8, "Sep": 9, "Oct": 10, "Nov": 11, "Dec": 12]
        guard let monthNum = months[month], let dayNum = Int(day) else {
            return .distantPast
        }

        var components = DateComponents()
        components.month = monthNum
        components.day = dayNum

        if timeOrYear.contains(":") {
            // Time format: HH:MM — assume current year
            let parts = timeOrYear.split(separator: ":")
            components.year = currentYear
            components.hour = Int(parts[0]) ?? 0
            components.minute = parts.count > 1 ? Int(parts[1]) ?? 0 : 0
        } else {
            // Year format
            components.year = Int(timeOrYear) ?? currentYear
        }

        return calendar.date(from: components) ?? .distantPast
    }

    // MARK: - Helpers

    private func shellEscape(_ path: String) -> String {
        // Wrap in double quotes and escape special chars
        let escaped = path
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
            .replacingOccurrences(of: "$", with: "\\$")
            .replacingOccurrences(of: "`", with: "\\`")
        return "\"\(escaped)\""
    }

    /// Decode octal escape sequences (e.g. `\314\210`) in filenames from `ls -la` output.
    private static func unescapeOctal(_ input: String) -> String {
        var bytes: [UInt8] = []
        var i = input.startIndex
        while i < input.endIndex {
            if input[i] == "\\" {
                let next = input.index(after: i)
                // Check for 3 octal digits after backslash
                if next < input.endIndex,
                   let end = input.index(next, offsetBy: 3, limitedBy: input.endIndex),
                   let value = UInt8(input[next..<end], radix: 8) {
                    bytes.append(value)
                    i = end
                } else {
                    bytes.append(contentsOf: Array(String(input[i]).utf8))
                    i = input.index(after: i)
                }
            } else {
                bytes.append(contentsOf: Array(String(input[i]).utf8))
                i = input.index(after: i)
            }
        }
        return String(bytes: bytes, encoding: .utf8) ?? input
    }
}
