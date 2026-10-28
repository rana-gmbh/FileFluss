import Foundation

/// Where a path inside a GitHub account points.
///
/// The account presents a three-level hierarchy — owner, repository, then
/// either the repository's files or a snapshot archive of them:
///
///     /                               owners the token can see
///     /rana-gmbh                      that owner's repositories
///     /rana-gmbh/FileFluss            one repository
///     /rana-gmbh/FileFluss/Source     its files, browsable
///     /rana-gmbh/FileFluss/FileFluss-main.zip
///                                     a snapshot of the default branch
///
/// `Source` is a fixed, unlocalized path segment on purpose: these paths are
/// persisted in favourites, the offline index and sync rules, so they have
/// to mean the same thing in every language and every future release.
public enum GitHubPath: Equatable, Sendable {
    case root
    case owner(String)
    case repository(owner: String, repo: String)
    /// The synthetic archive file that sits directly inside a repository.
    case archive(owner: String, repo: String, filename: String)
    /// A path inside the repository's file tree. An empty `path` is the
    /// `Source` folder itself.
    case tree(owner: String, repo: String, path: String)

    public static let sourceFolderName = "Source"

    /// Parses an account-relative path. Returns nil for anything that can't
    /// name something in a GitHub account, which callers report as "not
    /// found" rather than guessing.
    public static func parse(_ path: String) -> GitHubPath? {
        let parts = path.split(separator: "/").map(String.init)
        switch parts.count {
        case 0:
            return .root
        case 1:
            return .owner(parts[0])
        case 2:
            return .repository(owner: parts[0], repo: parts[1])
        default:
            let owner = parts[0]
            let repo = parts[1]
            if parts[2] == sourceFolderName {
                return .tree(owner: owner, repo: repo, path: parts.dropFirst(3).joined(separator: "/"))
            }
            if parts.count == 3, parts[2].lowercased().hasSuffix(".zip") {
                return .archive(owner: owner, repo: repo, filename: parts[2])
            }
            return nil
        }
    }

    /// The archive filename for a repository. A branch name may contain
    /// slashes ("release/1.5"), which would otherwise read as extra path
    /// components, so they are flattened.
    public static func archiveFilename(repo: String, branch: String) -> String {
        let flattened = branch.replacingOccurrences(of: "/", with: "-")
        return "\(repo)-\(flattened).zip"
    }
}

/// Read-only `CloudProvider` for a GitHub account.
///
/// Repositories are browsable (so a single file can be pulled out of one)
/// and each offers a snapshot archive of its default branch, which is what
/// makes "copy my repositories to another cloud" an ordinary panel-to-panel
/// transfer: `downloadFile` hands the existing transfer machinery a zip, and
/// nothing above this layer needs to know where it came from.
///
/// Nothing writes back. GitHub is a source here, not a destination — and
/// `deleteItem` on a repository path would mean deleting a repository, which
/// is not something a file manager should be able to do by accident.
public final class GitHubProvider: CloudProvider, @unchecked Sendable {
    public let providerType: CloudProviderType = .github

    private let keychainKey: String
    private var client: GitHubAPIClient?

    public var isAuthenticated: Bool {
        get async { client != nil }
    }

    public init(accountId: UUID = UUID()) {
        self.keychainKey = "github.\(accountId.uuidString)"
        if let credentials = KeychainService.load(key: keychainKey, as: GitHubCredentials.self) {
            self.client = GitHubAPIClient(credentials: credentials)
        }
    }

    // MARK: - Authentication

    /// Verifies a personal access token and stores it. Returns the login it
    /// belongs to, which the add-account flow uses to name the account.
    @discardableResult
    public func authenticate(token: String) async throws -> String {
        let credentials = try await GitHubAPIClient.verify(token: token)
        self.client = GitHubAPIClient(credentials: credentials)
        try KeychainService.save(key: keychainKey, value: credentials)
        return credentials.login
    }

    public func authenticate() async throws {
        // GitHub accounts are established by pasting a token in the
        // add-account flow, not by a credential prompt from here.
        throw CloudProviderError.notAuthenticated
    }

    public func disconnect() async throws {
        client = nil
        try KeychainService.delete(key: keychainKey)
    }

    public func userDisplayName() async throws -> String {
        guard let client else { throw CloudProviderError.notAuthenticated }
        return await client.login
    }

    // MARK: - Listing

    public func listDirectory(at path: String) async throws -> [CloudFileItem] {
        let (client, parsed) = try resolve(path)

        switch parsed {
        case .root:
            let owners = try await client.owners()
            return owners.map(Self.item(forOwner:))

        case .owner(let owner):
            let repositories = try await client.repositories()
                .filter { $0.owner.caseInsensitiveCompare(owner) == .orderedSame }
            guard !repositories.isEmpty else { throw CloudProviderError.notFound(path) }
            return repositories
                .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
                .map(Self.item(forRepository:))

        case .repository(let owner, let repo):
            let repository = try await client.repository(owner: owner, name: repo)
            let tree = try await client.tree(for: repository)
            var items = [Self.sourceFolderItem(for: repository)]
            // A repository with no commits has nothing to archive; offering
            // a zip that GitHub would refuse to produce is worse than
            // offering none.
            if !tree.entries.isEmpty {
                items.append(Self.archiveItem(for: repository))
            }
            return items

        case .tree(let owner, let repo, let treePath):
            let repository = try await client.repository(owner: owner, name: repo)
            let entries = try await client.listing(for: repository, directory: treePath)
            return entries
                .sorted { lhs, rhs in
                    if lhs.isDirectory != rhs.isDirectory { return lhs.isDirectory }
                    return lhs.name.localizedCaseInsensitiveCompare(rhs.name) == .orderedAscending
                }
                .map { Self.item(forEntry: $0, in: repository) }

        case .archive:
            // A file, not a directory.
            throw CloudProviderError.notFound(path)
        }
    }

    public func getFileMetadata(at path: String) async throws -> CloudFileItem {
        let (client, parsed) = try resolve(path)

        switch parsed {
        case .root:
            return CloudFileItem(
                id: "github:root",
                name: "/",
                path: "/",
                isDirectory: true,
                size: 0,
                modificationDate: .distantPast,
                checksum: nil,
                role: .virtualContainer
            )

        case .owner(let owner):
            guard let match = try await client.owners()
                .first(where: { $0.login.caseInsensitiveCompare(owner) == .orderedSame }) else {
                throw CloudProviderError.notFound(path)
            }
            return Self.item(forOwner: match)

        case .repository(let owner, let repo):
            return Self.item(forRepository: try await client.repository(owner: owner, name: repo))

        case .archive(let owner, let repo, _):
            return Self.archiveItem(for: try await client.repository(owner: owner, name: repo))

        case .tree(let owner, let repo, let treePath):
            let repository = try await client.repository(owner: owner, name: repo)
            guard !treePath.isEmpty else { return Self.sourceFolderItem(for: repository) }

            let parent = (treePath as NSString).deletingLastPathComponent
            let entries = try await client.listing(for: repository, directory: parent)
            guard let entry = entries.first(where: { $0.path == treePath }) else {
                throw CloudProviderError.notFound(path)
            }
            return Self.item(forEntry: entry, in: repository)
        }
    }

    public func folderSize(at path: String) async throws -> Int64 {
        let (client, parsed) = try resolve(path)

        switch parsed {
        case .root:
            return try await client.repositories().reduce(0) { $0 + $1.estimatedBytes }

        case .owner(let owner):
            return try await client.repositories()
                .filter { $0.owner.caseInsensitiveCompare(owner) == .orderedSame }
                .reduce(0) { $0 + $1.estimatedBytes }

        case .repository(let owner, let repo):
            // What a recursive copy of this folder would actually fetch: the
            // whole tree plus the archive sitting beside it.
            let repository = try await client.repository(owner: owner, name: repo)
            let tree = try await client.tree(for: repository)
            let treeBytes = tree.isTruncated ? repository.estimatedBytes : tree.recursiveSize(of: "")
            return treeBytes + repository.estimatedBytes

        case .tree(let owner, let repo, let treePath):
            let repository = try await client.repository(owner: owner, name: repo)
            let tree = try await client.tree(for: repository)
            // A truncated tree carries no reliable total; the repository's
            // own size is the only figure available.
            guard !tree.isTruncated else { return repository.estimatedBytes }
            return tree.recursiveSize(of: treePath)

        case .archive(let owner, let repo, _):
            return try await client.repository(owner: owner, name: repo).estimatedBytes
        }
    }

    // MARK: - Download

    public func downloadFile(remotePath: String, to localURL: URL) async throws {
        try await downloadFile(remotePath: remotePath, to: localURL, onBytes: nil)
    }

    public func downloadFile(remotePath: String, to localURL: URL, onBytes: ByteProgressHandler?) async throws {
        let (client, parsed) = try resolve(remotePath)

        switch parsed {
        case .archive(let owner, let repo, _):
            let repository = try await client.repository(owner: owner, name: repo)
            try await client.downloadArchive(for: repository, to: localURL, onBytes: onBytes)

        case .tree(let owner, let repo, let treePath) where !treePath.isEmpty:
            let repository = try await client.repository(owner: owner, name: repo)
            try await client.downloadFile(for: repository, path: treePath, to: localURL, onBytes: onBytes)

        default:
            throw CloudProviderError.notFound(remotePath)
        }
    }

    // MARK: - Storage

    /// GitHub has no storage quota to report, so this is a usage figure with
    /// no upper bound — the sum of what every visible repository takes up.
    /// `CloudStorageQuota` already models exactly that case.
    public func storageQuota() async throws -> CloudStorageQuota? {
        guard let client else { return nil }
        let used = try await client.repositories().reduce(0) { $0 + $1.estimatedBytes }
        return CloudStorageQuota(usedBytes: used, totalBytes: nil)
    }

    // MARK: - Writes

    // Everything below is deliberately unimplemented. A GitHub account is a
    // source: the way to change a repository is git, not a file manager.

    public func uploadFile(from localURL: URL, to remotePath: String) async throws {
        throw CloudProviderError.notImplemented
    }

    public func uploadFile(from localURL: URL, to remotePath: String, onBytes: ByteProgressHandler?) async throws {
        throw CloudProviderError.notImplemented
    }

    public func deleteItem(at path: String) async throws {
        throw CloudProviderError.notImplemented
    }

    public func createDirectory(at path: String) async throws {
        throw CloudProviderError.notImplemented
    }

    public func renameItem(at path: String, to newName: String) async throws {
        throw CloudProviderError.notImplemented
    }

    // MARK: - Items

    private static func item(forOwner owner: (login: String, lastPush: Date, repositoryCount: Int)) -> CloudFileItem {
        CloudFileItem(
            id: "github:owner:\(owner.login)",
            name: owner.login,
            path: "/\(owner.login)",
            isDirectory: true,
            size: 0,
            modificationDate: owner.lastPush,
            checksum: nil,
            symbolIconOverride: "person.crop.circle",
            role: .virtualContainer,
            kindOverride: owner.repositoryCount == 1
                ? L10n.text("1 repository")
                : L10n.format("%d repositories", owner.repositoryCount)
        )
    }

    private static func item(forRepository repository: GitHubRepository) -> CloudFileItem {
        CloudFileItem(
            id: "github:repo:\(repository.fullName)",
            name: repository.name,
            path: "/\(repository.fullName)",
            isDirectory: true,
            size: 0,
            modificationDate: repository.pushedAt,
            checksum: nil,
            // The lock is the point of the overview: whether a repository is
            // private should be readable at a glance, not two clicks away.
            symbolIconOverride: repository.isPrivate ? "lock.fill" : "globe",
            role: .driveRoot,
            kindOverride: Self.repositoryKind(repository)
        )
    }

    private static func repositoryKind(_ repository: GitHubRepository) -> String {
        switch (repository.isPrivate, repository.isArchived) {
        case (true, true): return L10n.text("Private repository (archived)")
        case (false, true): return L10n.text("Public repository (archived)")
        case (true, false): return L10n.text("Private repository")
        case (false, false): return L10n.text("Public repository")
        }
    }

    private static func sourceFolderItem(for repository: GitHubRepository) -> CloudFileItem {
        CloudFileItem(
            id: "github:source:\(repository.fullName)",
            name: GitHubPath.sourceFolderName,
            path: "/\(repository.fullName)/\(GitHubPath.sourceFolderName)",
            isDirectory: true,
            size: 0,
            modificationDate: repository.pushedAt,
            checksum: nil,
            kindOverride: L10n.format("Branch: %@", repository.defaultBranch)
        )
    }

    private static func archiveItem(for repository: GitHubRepository) -> CloudFileItem {
        let filename = GitHubPath.archiveFilename(repo: repository.name, branch: repository.defaultBranch)
        return CloudFileItem(
            id: "github:archive:\(repository.fullName)@\(repository.defaultBranch)",
            name: filename,
            path: "/\(repository.fullName)/\(filename)",
            isDirectory: false,
            // GitHub's repository size, which is the packed git objects
            // rather than this zip. Close enough for the size column and the
            // free-space pre-flight; never treated as exact.
            size: repository.estimatedBytes,
            // The last push, so an incremental backup re-copies a repository
            // only after someone has actually pushed to it.
            modificationDate: repository.pushedAt,
            checksum: nil,
            // Says what the file is rather than "ZIP archive": a snapshot of
            // one branch carries no history, no LFS content and no
            // submodules, and a backup is the wrong place to leave that
            // implicit.
            kindOverride: L10n.text("Snapshot archive (no history)")
        )
    }

    private static func item(forEntry entry: GitHubTreeEntry, in repository: GitHubRepository) -> CloudFileItem {
        CloudFileItem(
            id: "github:blob:\(repository.fullName):\(entry.sha):\(entry.path)",
            name: entry.name,
            path: "/\(repository.fullName)/\(GitHubPath.sourceFolderName)/\(entry.path)",
            isDirectory: entry.isDirectory,
            size: entry.size,
            // Git stores no per-file modification time, and asking for the
            // last commit that touched each file would be one request per
            // row. The repository's last push is the honest approximation,
            // and it errs towards re-copying rather than skipping a change.
            modificationDate: repository.pushedAt,
            checksum: nil
        )
    }

    // MARK: - Private

    private func resolve(_ path: String) throws -> (GitHubAPIClient, GitHubPath) {
        guard let client else { throw CloudProviderError.notAuthenticated }
        guard let parsed = GitHubPath.parse(path) else { throw CloudProviderError.notFound(path) }
        return (client, parsed)
    }
}
