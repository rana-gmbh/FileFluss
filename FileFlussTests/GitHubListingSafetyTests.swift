import Testing
import Foundation
@testable import FileFlussCore

/// A GitHub folder that can't be read must never list as empty: a sync
/// reading from it would take "empty" as "everything was deleted upstream"
/// and remove the local copy.
@Suite("GitHub listing safety")
struct GitHubListingSafetyTests {

    private static let treeJSON = """
    { "sha": "abc", "truncated": false, "tree": [
        { "path": "Docs", "type": "tree", "sha": "t1" },
        { "path": "Docs/guide.md", "type": "blob", "size": 10, "sha": "b1" },
        { "path": "Empty", "type": "tree", "sha": "t2" }
    ] }
    """

    private func repository(_ name: String) -> GitHubRepository {
        GitHubRepository(
            owner: "owner", name: name, isPrivate: false, isArchived: false,
            defaultBranch: "main", pushedAt: Date(timeIntervalSince1970: 1_700_000_000), sizeKB: 1
        )
    }

    private func client(host: String, handler: @escaping ProviderStubURLProtocol.Handler) -> GitHubAPIClient {
        GitHubAPIClient(
            credentials: GitHubCredentials(token: "t", login: "owner", apiBaseURL: "https://\(host)"),
            session: ProviderStubURLProtocol.session(host: host, handler: handler)
        )
    }

    @Test("A directory missing from the tree is not found, not empty")
    func missingDirectoryThrows() throws {
        let tree = try GitHubResponseParser.tree(from: Data(Self.treeJSON.utf8))
        #expect(throws: CloudProviderError.self) { try tree.listing(of: "Renamed") }
        // A file is not a directory either.
        #expect(throws: CloudProviderError.self) { try tree.listing(of: "Docs/guide.md") }
        // Real folders, empty or not, still list.
        #expect(try tree.listing(of: "Docs").map(\.name) == ["guide.md"])
        #expect(try tree.listing(of: "Empty").isEmpty)
        #expect(try tree.listing(of: "").count == 2)
    }

    @Test("Listing a renamed upstream folder through the client throws")
    func clientMissingDirectory() async throws {
        let client = client(host: "gh-missing.test") { _, _ in .json(Self.treeJSON) }
        await #expect(throws: CloudProviderError.self) {
            _ = try await client.listing(for: repository("r"), directory: "Renamed")
        }
    }

    /// A 404 for the tree means the branch or repository is gone (or the
    /// token lost access), not that the repository is empty.
    @Test("A tree 404 is an error and is not cached as an empty repository")
    func tree404IsAnError() async throws {
        let client = client(host: "gh-404.test") { _, _ in .status(404, #"{"message":"Not Found"}"#) }
        await #expect(throws: CloudProviderError.self) {
            _ = try await client.tree(for: repository("gone"))
        }
        await #expect(throws: CloudProviderError.self) {
            _ = try await client.listing(for: repository("gone"), directory: "")
        }
        // Not cached: the second call asked GitHub again.
        #expect(ProviderStubURLProtocol.requests(for: "gh-404.test").count == 2)
    }

    @Test("A never-pushed repository (409) is an empty tree")
    func emptyRepository409() async throws {
        let client = client(host: "gh-409.test") { _, _ in .status(409, #"{"message":"Git Repository is empty."}"#) }
        let tree = try await client.tree(for: repository("fresh"))
        #expect(tree.entries.isEmpty)
        #expect(try await client.listing(for: repository("fresh"), directory: "").isEmpty)
    }

    /// For truncated trees the Contents API is used; it caps a directory at
    /// 1000 entries and drops the rest without saying so.
    @Test("A Contents listing at the 1000-entry cap is refused, not truncated")
    func contentsListingAtCap() async throws {
        let entries = (0..<1000).map { #"{ "path": "big/f\#($0)", "type": "file", "size": 1, "sha": "s\#($0)" }"# }
        let contents = "[" + entries.joined(separator: ",") + "]"
        let client = client(host: "gh-cap.test") { request, _ in
            if request.url!.path.contains("/git/trees/") {
                return .json(#"{ "sha": "x", "truncated": true, "tree": [] }"#)
            }
            return .json(contents)
        }
        await #expect(throws: CloudProviderError.self) {
            _ = try await client.listing(for: repository("huge"), directory: "big")
        }
    }
}
