import Foundation
import os

private let gitHubLog = Logger(subsystem: "com.rana.FileFluss", category: "githubProvider")

/// Everything needed to talk to GitHub on the user's behalf.
///
/// Phase 1 authenticates with a personal access token — classic (needs the
/// `repo` scope to see private repositories) or fine-grained (needs
/// "Contents: Read-only"). `apiBaseURL` is persisted even though it is
/// always github.com today: GitHub Enterprise Server differs from
/// github.com only in that URL, so storing it now means an Enterprise
/// account later needs no keychain migration.
public struct GitHubCredentials: Codable, Sendable, Equatable {
    public static let publicAPIBaseURL = "https://api.github.com"
    public static let publicWebBaseURL = "https://github.com"

    public var token: String
    /// The authenticated user's GitHub login, resolved at connect time so
    /// the sidebar can label the account without a round-trip.
    public var login: String
    public var apiBaseURL: String
    public var webBaseURL: String

    public init(
        token: String,
        login: String,
        apiBaseURL: String = Self.publicAPIBaseURL,
        webBaseURL: String = Self.publicWebBaseURL
    ) {
        self.token = token
        self.login = login
        self.apiBaseURL = apiBaseURL
        self.webBaseURL = webBaseURL
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        token = try c.decode(String.self, forKey: .token)
        login = try c.decode(String.self, forKey: .login)
        apiBaseURL = (try? c.decode(String.self, forKey: .apiBaseURL)) ?? Self.publicAPIBaseURL
        webBaseURL = (try? c.decode(String.self, forKey: .webBaseURL)) ?? Self.publicWebBaseURL
    }
}

/// One repository as the account overview needs it. `sizeKB` is GitHub's own
/// figure for the packed git repository — useful as an order-of-magnitude
/// estimate, not as a byte count (see `GitHubRepository.estimatedBytes`).
public struct GitHubRepository: Sendable, Equatable {
    public let owner: String
    public let name: String
    public let isPrivate: Bool
    public let isArchived: Bool
    public let defaultBranch: String
    /// Last push to any branch. Used as the modification date of both the
    /// repository folder and its archive entry, which is what lets an
    /// incremental sync skip repositories nobody has touched.
    public let pushedAt: Date
    public let sizeKB: Int64

    public var fullName: String { "\(owner)/\(name)" }

    /// GitHub reports repository size in kilobytes of packed git objects.
    /// A zipball of one branch is a different thing altogether — usually
    /// smaller (no history) but occasionally larger (no delta compression).
    /// Treated strictly as an estimate: it feeds the free-space pre-flight
    /// and the size column, never a correctness decision.
    public var estimatedBytes: Int64 { sizeKB * 1024 }
}

/// One entry in a repository's file tree. `size` is only meaningful for
/// blobs; GitHub reports no size for subtrees.
public struct GitHubTreeEntry: Sendable, Equatable {
    public let path: String
    public let isDirectory: Bool
    public let size: Int64
    public let sha: String

    public var name: String {
        path.split(separator: "/").last.map(String.init) ?? path
    }
}

/// A repository's whole tree in one object. `isTruncated` mirrors GitHub's
/// own flag: above roughly 100k entries the recursive listing is cut short,
/// and a backup tool that quietly showed a partial repository would be
/// worse than useless — so callers fall back to per-directory listings.
public struct GitHubTree: Sendable {
    public let entries: [GitHubTreeEntry]
    public let isTruncated: Bool

    /// Entries directly inside `directory` ("" for the repository root).
    public func children(of directory: String) -> [GitHubTreeEntry] {
        let prefix = directory.isEmpty ? "" : directory + "/"
        return entries.filter { entry in
            guard entry.path.hasPrefix(prefix) else { return false }
            let remainder = entry.path.dropFirst(prefix.count)
            return !remainder.isEmpty && !remainder.contains("/")
        }
    }

    /// Total bytes of every blob at or below `directory`. Exact, because the
    /// recursive tree already carries every blob's size — no extra requests.
    public func recursiveSize(of directory: String) -> Int64 {
        let prefix = directory.isEmpty ? "" : directory + "/"
        return entries.reduce(into: Int64(0)) { total, entry in
            guard !entry.isDirectory, entry.path.hasPrefix(prefix) else { return }
            total += entry.size
        }
    }
}

actor GitHubAPIClient {
    private let credentials: GitHubCredentials
    private let session: URLSession

    /// The repository list changes rarely and is read on every navigation
    /// back to the account root, so it is cached briefly. GitHub allows
    /// 5000 requests/hour; this keeps ordinary browsing nowhere near it.
    private var cachedRepositories: (fetchedAt: Date, repositories: [GitHubRepository])?
    private static let repositoryCacheTTL: TimeInterval = 300

    /// Trees keyed by repository and last-push time, so a push invalidates
    /// the entry and nothing else does.
    private var cachedTrees: [String: GitHubTree] = [:]

    init(credentials: GitHubCredentials, session: URLSession = .shared) {
        self.credentials = credentials
        self.session = session
    }

    var login: String { credentials.login }

    // MARK: - Authentication

    /// Verifies a token and resolves the login it belongs to. Used by the
    /// add-account flow: a token that cannot read `/user` is not worth
    /// storing.
    static func verify(
        token: String,
        apiBaseURL: String = GitHubCredentials.publicAPIBaseURL,
        session: URLSession = .shared
    ) async throws -> GitHubCredentials {
        let probe = GitHubCredentials(token: token, login: "", apiBaseURL: apiBaseURL)
        let client = GitHubAPIClient(credentials: probe, session: session)
        let data = try await client.get(path: "/user")

        struct User: Decodable { let login: String }
        guard let user = try? JSONDecoder().decode(User.self, from: data), !user.login.isEmpty else {
            throw CloudProviderError.invalidResponse
        }
        gitHubLog.info("[GitHub] token verified for \(user.login, privacy: .public)")
        return GitHubCredentials(token: token, login: user.login, apiBaseURL: apiBaseURL)
    }

    // MARK: - Repositories

    /// Every repository the token can see, newest push first.
    func repositories() async throws -> [GitHubRepository] {
        if let cached = cachedRepositories,
           Date().timeIntervalSince(cached.fetchedAt) < Self.repositoryCacheTTL {
            return cached.repositories
        }

        var collected: [GitHubRepository] = []
        // 100 is GitHub's maximum page size. The page cap is a safety belt:
        // 20 pages is 2000 repositories, well past any personal account, and
        // it means a misbehaving API can't spin here forever.
        for page in 1...20 {
            let data = try await get(path: "/user/repos", query: [
                URLQueryItem(name: "per_page", value: "100"),
                URLQueryItem(name: "page", value: String(page)),
                URLQueryItem(name: "affiliation", value: "owner,collaborator,organization_member"),
                URLQueryItem(name: "sort", value: "pushed"),
            ])
            let batch = try GitHubResponseParser.repositories(from: data)
            collected.append(contentsOf: batch)
            if batch.count < 100 { break }
        }

        cachedRepositories = (Date(), collected)
        gitHubLog.info("[GitHub] listed \(collected.count) repositories")
        return collected
    }

    func repository(owner: String, name: String) async throws -> GitHubRepository {
        let all = try await repositories()
        if let match = all.first(where: {
            $0.owner.caseInsensitiveCompare(owner) == .orderedSame
                && $0.name.caseInsensitiveCompare(name) == .orderedSame
        }) {
            return match
        }
        // Not in the user's own list — it may still be readable (a repository
        // reached by path rather than by browsing), so ask directly.
        let data = try await get(path: "/repos/\(owner)/\(name)")
        guard let repo = try? GitHubResponseParser.repository(from: data) else {
            throw CloudProviderError.notFound("\(owner)/\(name)")
        }
        return repo
    }

    /// Repository owners, each with the most recent push across their
    /// repositories — used as the modification date of the owner folder.
    func owners() async throws -> [(login: String, lastPush: Date, repositoryCount: Int)] {
        let repositories = try await repositories()
        var byOwner: [String: (lastPush: Date, count: Int)] = [:]
        for repository in repositories {
            let existing = byOwner[repository.owner]
            byOwner[repository.owner] = (
                lastPush: max(existing?.lastPush ?? .distantPast, repository.pushedAt),
                count: (existing?.count ?? 0) + 1
            )
        }
        return byOwner
            .map { (login: $0.key, lastPush: $0.value.lastPush, repositoryCount: $0.value.count) }
            .sorted { $0.login.localizedCaseInsensitiveCompare($1.login) == .orderedAscending }
    }

    // MARK: - Trees

    /// The repository's whole tree at its default branch.
    ///
    /// An empty repository (created but never pushed to) has no tree at all
    /// and GitHub answers 404 — reported here as an empty tree, because an
    /// empty repository is a legitimate thing to browse into, not an error.
    func tree(for repository: GitHubRepository) async throws -> GitHubTree {
        let key = "\(repository.fullName)@\(repository.pushedAt.timeIntervalSince1970)"
        if let cached = cachedTrees[key] { return cached }

        let data: Data
        do {
            data = try await get(
                path: "/repos/\(repository.fullName)/git/trees/\(repository.defaultBranch)",
                query: [URLQueryItem(name: "recursive", value: "1")]
            )
        } catch CloudProviderError.notFound {
            let empty = GitHubTree(entries: [], isTruncated: false)
            cachedTrees[key] = empty
            return empty
        }

        let tree = try GitHubResponseParser.tree(from: data)
        if tree.isTruncated {
            gitHubLog.info("[GitHub] tree for \(repository.fullName, privacy: .public) is truncated; per-directory listing will be used")
        }
        cachedTrees[key] = tree
        return tree
    }

    /// Entries directly inside `directory` of a repository, falling back to
    /// the Contents API when the recursive tree came back truncated.
    func listing(for repository: GitHubRepository, directory: String) async throws -> [GitHubTreeEntry] {
        let tree = try await tree(for: repository)
        guard tree.isTruncated else { return tree.children(of: directory) }
        return try await contentsListing(for: repository, directory: directory)
    }

    /// One directory straight from the Contents API. Used only for
    /// repositories too large for a recursive tree: one request per folder
    /// the user opens, with exact per-file sizes.
    private func contentsListing(for repository: GitHubRepository, directory: String) async throws -> [GitHubTreeEntry] {
        let data = try await get(
            path: "/repos/\(repository.fullName)/contents/\(Self.escapePath(directory))",
            query: [URLQueryItem(name: "ref", value: repository.defaultBranch)]
        )

        struct Entry: Decodable {
            let path: String
            let size: Int64?
            let type: String
            let sha: String?
        }
        guard let entries = try? JSONDecoder().decode([Entry].self, from: data) else {
            // A single file answers with an object rather than an array; that
            // means the caller asked for a blob as if it were a folder.
            throw CloudProviderError.notFound(directory)
        }
        return entries.map {
            GitHubTreeEntry(
                path: $0.path,
                isDirectory: $0.type == "dir",
                size: $0.size ?? 0,
                sha: $0.sha ?? $0.path
            )
        }
    }

    // MARK: - Downloads

    /// Streams a zipball of the repository's default branch to `localURL`.
    ///
    /// The API endpoint answers 302 with a short-lived signed codeload URL;
    /// URLSession follows it. Worth being explicit about what this artifact
    /// is: a snapshot of one branch's working tree. It carries no history,
    /// no LFS objects (only their pointer files), no submodule contents and
    /// nothing outside the git repository — issues, releases and wikis live
    /// elsewhere. The UI says so too; see the archive entry's "Kind".
    func downloadArchive(for repository: GitHubRepository, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let request = try self.request(
            path: "/repos/\(repository.fullName)/zipball/\(repository.defaultBranch)",
            accept: "application/vnd.github+json"
        )
        try await download(request, to: localURL, describing: repository.fullName, onBytes: onBytes)
    }

    /// Streams one file's bytes. The `raw` media type returns the blob
    /// itself rather than base64 JSON, which keeps memory flat and works up
    /// to GitHub's 100 MB per-file ceiling.
    func downloadFile(for repository: GitHubRepository, path: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let request = try self.request(
            path: "/repos/\(repository.fullName)/contents/\(Self.escapePath(path))",
            query: [URLQueryItem(name: "ref", value: repository.defaultBranch)],
            accept: "application/vnd.github.raw"
        )
        try await download(request, to: localURL, describing: path, onBytes: onBytes)
    }

    private func download(_ request: URLRequest, to localURL: URL, describing subject: String, onBytes: ByteProgressHandler?) async throws {
        let (tempURL, response) = try await session.downloadReportingProgress(for: request, onBytes: onBytes)
        do {
            try Self.check(response, path: subject)
        } catch {
            try? FileManager.default.removeItem(at: tempURL)
            throw error
        }
        if FileManager.default.fileExists(atPath: localURL.path) {
            try? FileManager.default.removeItem(at: localURL)
        }
        try FileManager.default.moveItem(at: tempURL, to: localURL)
    }

    // MARK: - Requests

    private func request(path: String, query: [URLQueryItem] = [], accept: String = "application/vnd.github+json") throws -> URLRequest {
        guard var components = URLComponents(string: credentials.apiBaseURL + path) else {
            throw CloudProviderError.invalidResponse
        }
        if !query.isEmpty { components.queryItems = query }
        guard let url = components.url else { throw CloudProviderError.invalidResponse }

        var request = URLRequest(url: url)
        request.setValue("Bearer \(credentials.token)", forHTTPHeaderField: "Authorization")
        request.setValue(accept, forHTTPHeaderField: "Accept")
        // GitHub pins behaviour per API version; without this header the
        // account would silently follow whatever the current default is.
        request.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
        request.setValue("FileFluss", forHTTPHeaderField: "User-Agent")
        return request
    }

    private func get(path: String, query: [URLQueryItem] = []) async throws -> Data {
        let request = try self.request(path: path, query: query)
        let (data, response): (Data, URLResponse)
        do {
            (data, response) = try await session.data(for: request)
        } catch {
            throw CloudProviderError.networkError(error)
        }
        try Self.check(response, path: path)
        return data
    }

    private static func check(_ response: URLResponse, path: String) throws {
        guard let http = response as? HTTPURLResponse else { throw CloudProviderError.invalidResponse }
        switch http.statusCode {
        case 200..<300:
            return
        case 401:
            throw CloudProviderError.unauthorized
        case 403, 429:
            // 403 covers both "rate limited" and "this token may not see
            // that" — the remaining-requests header is what separates them.
            let remaining = http.value(forHTTPHeaderField: "x-ratelimit-remaining")
            if remaining == "0" { throw CloudProviderError.rateLimited }
            throw CloudProviderError.serverError(http.statusCode)
        case 404:
            throw CloudProviderError.notFound(path)
        default:
            throw CloudProviderError.serverError(http.statusCode)
        }
    }

    /// Percent-escapes a repository-relative path for use in a URL path.
    /// Slashes stay literal — they are path structure, not data.
    private static func escapePath(_ path: String) -> String {
        path.split(separator: "/")
            .map { $0.addingPercentEncoding(withAllowedCharacters: .alphanumerics.union(CharacterSet(charactersIn: "-._~"))) ?? String($0) }
            .joined(separator: "/")
    }
}

/// Turns GitHub's JSON into the shapes the provider uses.
///
/// Separate from the client, and public, so the response formats can be
/// pinned by tests without a network round-trip — GitHub's payloads are
/// stable but large, and the fields that matter here (private, default
/// branch, push date, entry type) are easy to regress on silently.
public enum GitHubResponseParser {

    private struct RepositoryPayload: Decodable {
        struct Owner: Decodable { let login: String }
        let name: String
        let owner: Owner
        let `private`: Bool
        let archived: Bool?
        let default_branch: String?
        let pushed_at: String?
        let updated_at: String?
        let created_at: String?
        let size: Int64?
    }

    /// A value-type parser rather than an `ISO8601DateFormatter`: the
    /// formatter classes are not `Sendable`, and this is read from every
    /// decode.
    private static let iso8601 = Date.ISO8601FormatStyle()

    private static func repository(from payload: RepositoryPayload) -> GitHubRepository {
        // A repository with no commits has no push date; falling back keeps
        // date-based sync comparisons from seeing a 1970 timestamp and
        // deciding everything is ancient.
        let timestamp = [payload.pushed_at, payload.updated_at, payload.created_at]
            .compactMap { $0 }
            .compactMap { try? iso8601.parse($0) }
            .first
        return GitHubRepository(
            owner: payload.owner.login,
            name: payload.name,
            isPrivate: payload.private,
            isArchived: payload.archived ?? false,
            defaultBranch: payload.default_branch ?? "main",
            pushedAt: timestamp ?? .distantPast,
            sizeKB: payload.size ?? 0
        )
    }

    public static func repositories(from data: Data) throws -> [GitHubRepository] {
        guard let payloads = try? JSONDecoder().decode([RepositoryPayload].self, from: data) else {
            throw CloudProviderError.invalidResponse
        }
        return payloads.map(repository(from:))
    }

    public static func repository(from data: Data) throws -> GitHubRepository {
        guard let payload = try? JSONDecoder().decode(RepositoryPayload.self, from: data) else {
            throw CloudProviderError.invalidResponse
        }
        return repository(from: payload)
    }

    public static func tree(from data: Data) throws -> GitHubTree {
        struct Payload: Decodable {
            struct Entry: Decodable {
                let path: String
                let type: String
                let size: Int64?
                let sha: String?
            }
            let tree: [Entry]?
            let truncated: Bool?
        }
        guard let payload = try? JSONDecoder().decode(Payload.self, from: data) else {
            throw CloudProviderError.invalidResponse
        }
        let entries = (payload.tree ?? []).compactMap { entry -> GitHubTreeEntry? in
            // Submodules ("commit") and symlinks have no content the Contents
            // API will serve; listing them would offer a download that could
            // only fail.
            guard entry.type == "blob" || entry.type == "tree" else { return nil }
            return GitHubTreeEntry(
                path: entry.path,
                isDirectory: entry.type == "tree",
                size: entry.size ?? 0,
                sha: entry.sha ?? entry.path
            )
        }
        return GitHubTree(entries: entries, isTruncated: payload.truncated ?? false)
    }
}
