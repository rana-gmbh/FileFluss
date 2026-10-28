import Testing
import Foundation
import FileFlussCore

/// Pins the two things a GitHub account gets wrong invisibly: the path
/// grammar that maps a panel location onto owner / repository / file, and
/// the JSON shapes behind the repository overview.
///
/// Both are silent failure modes. A path that parses one level off browses
/// the wrong repository; a `private` flag that stops decoding turns every
/// repository public in the overview, which is exactly the thing the user
/// is looking at that column to find out.
@Suite("GitHub account")
struct GitHubProviderTests {

    // MARK: - Paths

    @Test("Each level of the hierarchy parses to its own case")
    func pathLevels() {
        #expect(GitHubPath.parse("/") == .root)
        #expect(GitHubPath.parse("") == .root)
        #expect(GitHubPath.parse("/rana-gmbh") == .owner("rana-gmbh"))
        #expect(GitHubPath.parse("/rana-gmbh/FileFluss") == .repository(owner: "rana-gmbh", repo: "FileFluss"))
        #expect(GitHubPath.parse("/rana-gmbh/FileFluss/Source") == .tree(owner: "rana-gmbh", repo: "FileFluss", path: ""))
        #expect(
            GitHubPath.parse("/rana-gmbh/FileFluss/Source/FileFluss/App/AppState.swift")
                == .tree(owner: "rana-gmbh", repo: "FileFluss", path: "FileFluss/App/AppState.swift")
        )
        #expect(
            GitHubPath.parse("/rana-gmbh/FileFluss/FileFluss-main.zip")
                == .archive(owner: "rana-gmbh", repo: "FileFluss", filename: "FileFluss-main.zip")
        )
    }

    /// Anything that doesn't name a real location has to come back nil so
    /// the provider reports "not found" instead of browsing somewhere else.
    @Test("Paths that name nothing are rejected")
    func unparseablePaths() {
        #expect(GitHubPath.parse("/rana-gmbh/FileFluss/README.md") == nil)
        #expect(GitHubPath.parse("/rana-gmbh/FileFluss/Archives/old.zip") == nil)
        // A zip is only an archive directly inside the repository.
        #expect(GitHubPath.parse("/rana-gmbh/FileFluss/sub/thing.zip") == nil)
    }

    /// A file inside `Source` keeps its own `.zip` name rather than being
    /// mistaken for the repository's archive entry.
    @Test("A zip committed to the repository stays a tree entry")
    func committedZipIsNotTheArchive() {
        #expect(
            GitHubPath.parse("/owner/repo/Source/Fixtures/sample.zip")
                == .tree(owner: "owner", repo: "repo", path: "Fixtures/sample.zip")
        )
    }

    /// A default branch like `release/1.5` would otherwise read as two path
    /// components and make the archive unreachable.
    @Test("A slashed branch name is flattened for the archive filename")
    func archiveFilenameFlattensBranch() {
        #expect(GitHubPath.archiveFilename(repo: "FileFluss", branch: "main") == "FileFluss-main.zip")
        #expect(GitHubPath.archiveFilename(repo: "FileFluss", branch: "release/1.5") == "FileFluss-release-1.5.zip")

        let roundTrip = "/owner/FileFluss/" + GitHubPath.archiveFilename(repo: "FileFluss", branch: "release/1.5")
        #expect(
            GitHubPath.parse(roundTrip)
                == .archive(owner: "owner", repo: "FileFluss", filename: "FileFluss-release-1.5.zip")
        )
    }

    // MARK: - Repository decoding

    private static let repositoriesJSON = """
    [
      {
        "name": "FileFluss",
        "owner": { "login": "rana-gmbh" },
        "private": true,
        "archived": false,
        "default_branch": "main",
        "pushed_at": "2026-10-20T08:15:00Z",
        "updated_at": "2026-10-20T09:00:00Z",
        "created_at": "2024-02-01T10:00:00Z",
        "size": 20480
      },
      {
        "name": "homebrew-filefluss",
        "owner": { "login": "rana-gmbh" },
        "private": false,
        "archived": true,
        "default_branch": "release/1.5",
        "pushed_at": "2026-09-01T12:00:00Z",
        "size": 12
      }
    ]
    """

    @Test("Visibility, branch, push date and size survive decoding")
    func repositoryDecoding() throws {
        let repositories = try GitHubResponseParser.repositories(from: Data(Self.repositoriesJSON.utf8))
        #expect(repositories.count == 2)

        let app = repositories[0]
        #expect(app.fullName == "rana-gmbh/FileFluss")
        #expect(app.isPrivate)
        #expect(!app.isArchived)
        #expect(app.defaultBranch == "main")
        #expect(app.pushedAt == ISO8601DateFormatter().date(from: "2026-10-20T08:15:00Z"))
        // GitHub reports kilobytes; the estimate the UI shows is bytes.
        #expect(app.estimatedBytes == 20480 * 1024)

        let tap = repositories[1]
        #expect(!tap.isPrivate)
        #expect(tap.isArchived)
        #expect(tap.defaultBranch == "release/1.5")
    }

    /// A repository created but never pushed to has a null `pushed_at`.
    /// Left unhandled it would date to 1970, which a date-based sync reads
    /// as "ancient" rather than "empty".
    @Test("A repository with no commits falls back to a real date")
    func emptyRepositoryDate() throws {
        let json = """
        [{
          "name": "fresh",
          "owner": { "login": "robert" },
          "private": true,
          "pushed_at": null,
          "updated_at": null,
          "created_at": "2026-10-27T07:00:00Z"
        }]
        """
        let repository = try GitHubResponseParser.repositories(from: Data(json.utf8))[0]
        #expect(repository.pushedAt == ISO8601DateFormatter().date(from: "2026-10-27T07:00:00Z"))
        #expect(repository.defaultBranch == "main")
        #expect(repository.sizeKB == 0)
    }

    @Test("Malformed JSON is an invalid response, not a crash")
    func malformedRepositoryJSON() {
        #expect(throws: CloudProviderError.self) {
            try GitHubResponseParser.repositories(from: Data("{ not json".utf8))
        }
    }

    // MARK: - Tree decoding

    private static let treeJSON = """
    {
      "sha": "abc123",
      "tree": [
        { "path": "README.md",            "type": "blob", "size": 1024, "sha": "b1" },
        { "path": "FileFluss",            "type": "tree", "sha": "t1" },
        { "path": "FileFluss/App",        "type": "tree", "sha": "t2" },
        { "path": "FileFluss/App/main.swift", "type": "blob", "size": 2048, "sha": "b2" },
        { "path": "FileFluss/logo.png",   "type": "blob", "size": 4096, "sha": "b3" },
        { "path": "Vendor/sparkle",       "type": "commit", "sha": "c1" },
        { "path": "latest",               "type": "symlink", "size": 7, "sha": "s1" }
      ],
      "truncated": false
    }
    """

    /// Submodules and symlinks have no content the Contents API will serve,
    /// so listing them would offer a download that could only fail.
    @Test("Submodules and symlinks are left out of the tree")
    func treeSkipsUnfetchableEntries() throws {
        let tree = try GitHubResponseParser.tree(from: Data(Self.treeJSON.utf8))
        #expect(!tree.isTruncated)
        #expect(tree.entries.count == 5)
        #expect(!tree.entries.contains { $0.path == "Vendor/sparkle" })
        #expect(!tree.entries.contains { $0.path == "latest" })
    }

    @Test("A directory listing comes out of the flat recursive tree")
    func treeChildren() throws {
        let tree = try GitHubResponseParser.tree(from: Data(Self.treeJSON.utf8))

        let top = tree.children(of: "").map(\.name).sorted()
        #expect(top == ["FileFluss", "README.md"])

        let inside = tree.children(of: "FileFluss").map(\.name).sorted()
        #expect(inside == ["App", "logo.png"])

        // Grandchildren must not leak into a parent's listing.
        #expect(!tree.children(of: "FileFluss").contains { $0.name == "main.swift" })
        #expect(tree.children(of: "FileFluss/App").map(\.name) == ["main.swift"])
        #expect(tree.children(of: "Missing").isEmpty)
    }

    @Test("Folder sizes add up every blob underneath")
    func treeRecursiveSize() throws {
        let tree = try GitHubResponseParser.tree(from: Data(Self.treeJSON.utf8))
        #expect(tree.recursiveSize(of: "") == 1024 + 2048 + 4096)
        #expect(tree.recursiveSize(of: "FileFluss") == 2048 + 4096)
        #expect(tree.recursiveSize(of: "FileFluss/App") == 2048)
        #expect(tree.recursiveSize(of: "Missing") == 0)
    }

    /// Above roughly 100k entries GitHub cuts the recursive listing short.
    /// The flag has to survive decoding, because it's what makes the
    /// provider fall back to per-directory listings instead of showing a
    /// repository that silently stops part way.
    @Test("A truncated tree reports itself as truncated")
    func truncatedTree() throws {
        let json = """
        { "sha": "x", "tree": [ { "path": "a", "type": "blob", "size": 1, "sha": "b" } ], "truncated": true }
        """
        #expect(try GitHubResponseParser.tree(from: Data(json.utf8)).isTruncated)
    }

    // MARK: - Capabilities

    /// The only thing a delete could mean on GitHub is removing a
    /// repository or rewriting a branch, so the affordance is withheld
    /// rather than offered and then refused. GoPro, the other read-only
    /// provider, deliberately keeps it — you clear a camera down after
    /// copying the footage off it.
    @Test("A GitHub account is read-only and offers no delete")
    func capabilities() {
        #expect(CloudProviderType.github.isReadOnly)
        #expect(!CloudProviderType.github.allowsDeletion)
        #expect(CloudProviderType.gopro.isReadOnly)
        #expect(CloudProviderType.gopro.allowsDeletion)
        #expect(CloudProviderType.dropbox.allowsDeletion)
    }

    // MARK: - Kind column

    /// The repository overview says private or public in the Kind column,
    /// which only works because an item may override the text otherwise
    /// derived from its file extension.
    @Test("A kind override replaces the derived kind")
    func kindOverride() {
        let repository = CloudFileItem(
            id: "github:repo:rana-gmbh/FileFluss",
            name: "FileFluss",
            path: "/rana-gmbh/FileFluss",
            isDirectory: true,
            size: 0,
            modificationDate: Date(),
            checksum: nil,
            role: .driveRoot,
            kindOverride: "Private repository"
        )
        #expect(repository.kind == "Private repository")

        let archive = CloudFileItem(
            id: "github:archive:rana-gmbh/FileFluss@main",
            name: "FileFluss-main.zip",
            path: "/rana-gmbh/FileFluss/FileFluss-main.zip",
            isDirectory: false,
            size: 1024,
            modificationDate: Date(),
            checksum: nil,
            kindOverride: "Snapshot archive (no history)"
        )
        #expect(archive.kind == "Snapshot archive (no history)")

        // Without an override the existing extension-derived behaviour stands.
        let plain = CloudFileItem(
            id: "plain",
            name: "notes.txt",
            path: "/notes.txt",
            isDirectory: false,
            size: 10,
            modificationDate: Date(),
            checksum: nil
        )
        #expect(plain.kind != "Snapshot archive (no history)")
    }
}
