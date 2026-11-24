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

    @Test("a folder under /private takes new files, and still refuses a way out")
    func privateFolders() throws {
        // The temporary folder is /var/folders/…, really /private/var/folders/….
        let temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("SafeLocalPath-\(UUID().uuidString)", isDirectory: true)
            .standardizedFileURL
        let root = URL(fileURLWithPath: "/private" + temporary.path, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        #expect(root.path.hasPrefix("/private/"))

        let nested = try SafeLocalPath.destination(forRelativePath: "sub/README.md", in: root)
        #expect(nested.path == root.path + "/sub/README.md")
        let single = try SafeLocalPath.destination(for: "README.md", in: root)
        #expect(single.path == root.path + "/README.md")

        #expect(throws: (any Error).self) {
            try SafeLocalPath.destination(forRelativePath: "../escape.txt", in: root)
        }
        #expect(throws: (any Error).self) {
            try SafeLocalPath.destination(forRelativePath: "sub/../../escape.txt", in: root)
        }
    }
}
