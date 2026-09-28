import Testing
import Foundation
import FileFlussCore

/// A remote file name is attacker-controlled: a hostile server, or anyone
/// who shares a file with the user, picks it. These pin the guard that
/// keeps such a name from writing outside the folder the user chose.
@Suite("Safe local paths")
struct SafeLocalPathTests {

    private let downloads = URL(fileURLWithPath: "/Users/someone/Downloads", isDirectory: true)

    @Test("traversal attempts stay inside the target directory")
    func traversalIsContained() throws {
        let hostile = [
            "../../../Library/LaunchAgents/evil.plist",
            "..",
            "../",
            "/etc/passwd",
            "sub/dir/file.txt",
            "..\\..\\windows-style.txt",
        ]
        for name in hostile {
            let url = try SafeLocalPath.destination(for: name, in: downloads)
            #expect(
                url.deletingLastPathComponent().standardizedFileURL.path == downloads.standardizedFileURL.path,
                "\(name) escaped to \(url.path)"
            )
            #expect(!url.lastPathComponent.contains("/"))
        }
    }

    @Test("ordinary names are left recognisable")
    func ordinaryNamesSurvive() throws {
        let url = try SafeLocalPath.destination(for: "Quarterly Report 2026.pdf", in: downloads)
        #expect(url.lastPathComponent == "Quarterly Report 2026.pdf")
    }

    @Test("names that reduce to nothing still produce a usable file name")
    func emptyNameFallsBack() {
        #expect(SafeLocalPath.fileName(from: "") == "unnamed")
        #expect(SafeLocalPath.fileName(from: "   ") == "unnamed")
        #expect(SafeLocalPath.fileName(from: "/") == "-")
    }

    @Test("unicode and dots inside a name are preserved")
    func unicodeAndDotsSurvive() throws {
        let url = try SafeLocalPath.destination(for: "Ünïcødé …file.tar.gz", in: downloads)
        #expect(url.lastPathComponent == "Ünïcødé …file.tar.gz")
    }
}
