import Foundation
import CryptoKit
import FileFlussCore

enum RarInstallerError: LocalizedError {
    case download(String)
    case checksumMismatch
    case missingBinary

    var errorDescription: String? {
        switch self {
        case .download(let message):
            return L10n.format("The rar tool could not be downloaded: %@", message)
        case .checksumMismatch:
            return L10n.text("The downloaded rar tool did not match its expected fingerprint, so it was discarded. You can download it from rarlab.com instead.")
        case .missingBinary:
            return L10n.text("The download did not contain the rar tool.")
        }
    }
}

/// Downloads RARLAB's command-line `rar` and keeps it in FileFluss's own
/// Application Support folder.
///
/// Homebrew used to be the easy route (`brew install --cask rar`), but it
/// disabled that cask on 2026-09-01 because RARLAB's binary isn't notarised
/// and fails its Gatekeeper check. Installing it here sidesteps that check
/// legitimately: the file is downloaded by FileFluss rather than a browser,
/// so it carries no quarantine flag, and it is ad-hoc signed, which is all
/// Apple Silicon requires of a command-line tool. Nothing is written outside
/// FileFluss's own folder and no administrator password is needed.
///
/// The download is pinned — version, URL and SHA-256 — rather than "whatever
/// rarlab.com offers today". A binary FileFluss is about to execute has to be
/// exactly the one that was checked, and the hashes are the ones Homebrew
/// published for RAR 7.23 before the cask was disabled. When RARLAB stops
/// serving 7.23, the download fails cleanly and the user is pointed at the
/// download page; bumping the version means bumping all three together.
enum RarInstaller {
    static let version = "7.23"
    static let downloadPage = URL(string: "https://www.rarlab.com/download.htm")!

    private struct Build {
        let url: URL
        let sha256: String
    }

    private static var build: Build {
        #if arch(arm64)
        Build(
            url: URL(string: "https://www.rarlab.com/rar/rarmacos-arm-723.tar.gz")!,
            sha256: "68b393c000758d477fde43c955ff7542f12f76f3f5e87cdda923152fc791bd4d"
        )
        #else
        Build(
            url: URL(string: "https://www.rarlab.com/rar/rarmacos-x64-723.tar.gz")!,
            sha256: "da1fb3c3d7748136c9b369b683d574b372cb1ed049a634a81f85d93918346d8f"
        )
        #endif
    }

    /// `~/Library/Application Support/FileFluss/Tools/rar`, holding the tool
    /// and the licence RARLAB ships beside it.
    static var installDirectory: URL {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return support.appendingPathComponent("FileFluss/Tools/rar", isDirectory: true)
    }

    static var installedTool: URL {
        installDirectory.appendingPathComponent("rar")
    }

    /// Downloads, verifies and installs the tool. Returns its path.
    @discardableResult
    static func install() async throws -> URL {
        let (downloaded, response) = try await URLSession.shared.download(from: build.url)
        defer { try? FileManager.default.removeItem(at: downloaded) }
        if let http = response as? HTTPURLResponse, http.statusCode != 200 {
            throw RarInstallerError.download(
                HTTPURLResponse.localizedString(forStatusCode: http.statusCode)
            )
        }

        // Checked before anything is unpacked, let alone run.
        let data = try Data(contentsOf: downloaded)
        let digest = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        guard digest == build.sha256 else {
            SupportLogger.shared.log(
                "rar install: checksum mismatch (\(digest))", category: "archive", level: .error
            )
            throw RarInstallerError.checksumMismatch
        }

        let fm = FileManager.default
        let staging = StagingLocation.stagingDirectory(prefix: "rar-install")
        defer { try? fm.removeItem(at: staging) }
        let archive = staging.appendingPathComponent("rar.tar.gz")
        try fm.moveItem(at: downloaded, to: archive)
        let unpacked = staging.appendingPathComponent("unpacked", isDirectory: true)
        try fm.createDirectory(at: unpacked, withIntermediateDirectories: true)
        try await ArchiveService.extractAll(from: archive, format: .tarGzip, into: unpacked)

        // The tarball holds a single `rar/` folder.
        let source = unpacked.appendingPathComponent("rar", isDirectory: true)
        guard fm.isExecutableFile(atPath: source.appendingPathComponent("rar").path) else {
            throw RarInstallerError.missingBinary
        }
        try fm.createDirectory(
            at: installDirectory.deletingLastPathComponent(), withIntermediateDirectories: true
        )
        if fm.fileExists(atPath: installDirectory.path) {
            try fm.removeItem(at: installDirectory)
        }
        try fm.moveItem(at: source, to: installDirectory)
        SupportLogger.shared.log("rar \(version) installed at \(installedTool.path)", category: "archive")
        return installedTool
    }
}
