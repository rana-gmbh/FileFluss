// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.GitHub;

/// <summary>
/// Read-only GitHub account (port of the macOS <c>GitHubProvider</c>). Repositories are
/// browsable, so a single file can be pulled out of one, and each offers a snapshot archive of
/// its default branch — which is what makes "copy my repositories to another cloud" an
/// ordinary panel-to-panel transfer: <see cref="DownloadFileAsync"/> hands the transfer
/// machinery a zip, and nothing above this layer needs to know where it came from.
///
/// <para>Nothing writes back (<see cref="CloudProviderTypeExtensions.IsReadOnly"/>, and
/// <see cref="CloudProviderTypeExtensions.AllowsDeletion"/> is false). GitHub is a source
/// here, not a destination — a delete on a repository path would mean deleting a repository,
/// which a file manager must not be able to do by accident.</para>
/// </summary>
[CloudProvider(CloudProviderType.GitHub)]
public sealed class GitHubProvider : CloudProvider
{
    internal const string TokenKey = "token";
    internal const string TokenSettingsUrl = "https://github.com/settings/tokens";

    private readonly HttpClient _http;
    private volatile GitHubApiClient? _client;

    public GitHubProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal GitHubProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _http = handler is null
            ? CloudHttp.Create()
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        if (credentials.Load<GitHubCredentials>(CredentialKey) is { Token.Length: > 0 } stored)
        {
            _client = new GitHubApiClient(_http, stored);
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.GitHub;

    protected override string CredentialKeyPrefix => "github";

    public override bool IsAuthenticated => _client is not null;

    // MARK: - Connecting

    /// <summary>
    /// Add: the macOS token form. Edit: macOS offers nothing to change — a different token
    /// means removing the account and adding it again — so the form is that hint alone, and
    /// saving it keeps the stored token.
    /// </summary>
    public override ConnectSpec ConnectSpec => IsAuthenticated
        ? new ConnectSpec(ConnectField.Info("To use a different personal access token, remove this account and add it again."))
        : new ConnectSpec(
            ConnectField.Info("Create a personal access token on GitHub, then paste it below. A fine-grained token needs read access to Contents and Metadata; a classic token needs the repo scope to see private repositories."),
            ConnectField.Link("Open GitHub token settings", TokenSettingsUrl),
            new ConnectField(TokenKey, ConnectFieldKind.Password, "Personal Access Token"),
            ConnectField.Info("FileFluss only reads from GitHub. Repositories can be browsed, single files pulled out, and each repository offers a snapshot archive of its default branch to copy to another account."));

    /// <summary>
    /// Verifies the pasted token against <c>/user</c> and stores it; the login names the
    /// account ("GitHub (login)"). Nothing is stored unless the token works.
    /// </summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var token = values.GetTrimmed(TokenKey);
        if (token.Length == 0 && _client is { } existing)
        {
            // The edit form has no token field: saving it changes nothing.
            return new ConnectResult(existing.Login);
        }

        if (token.Length == 0)
        {
            throw CloudProviderException.InvalidCredentials();
        }

        var credentials = await GitHubApiClient.VerifyAsync(_http, token, cancellationToken).ConfigureAwait(false);
        Credentials.Save(CredentialKey, credentials);
        _client = new GitHubApiClient(_http, credentials);
        SupportLogger.Shared.Log($"[GitHub] connected as {credentials.Login}", GitHubApiClient.LogCategory);
        return new ConnectResult(credentials.Login);
    }

    public override Task DisconnectAsync()
    {
        _client = null;
        return base.DisconnectAsync();
    }

    // MARK: - Listing

    public override async Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var (client, parsed) = Resolve(path);
        switch (parsed.Kind)
        {
            case GitHubPathKind.Root:
                return (await client.OwnersAsync(cancellationToken).ConfigureAwait(false)).Select(OwnerItem).ToList();

            case GitHubPathKind.Owner:
            {
                var repositories = (await client.RepositoriesAsync(cancellationToken).ConfigureAwait(false))
                    .Where(r => string.Equals(r.Owner, parsed.Owner, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (repositories.Count == 0)
                {
                    throw CloudProviderException.NotFound(path);
                }

                return repositories.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(RepositoryItem).ToList();
            }

            case GitHubPathKind.Repository:
            {
                var repository = await client.RepositoryAsync(parsed.Owner, parsed.Repo, cancellationToken).ConfigureAwait(false);
                var tree = await client.TreeAsync(repository, cancellationToken).ConfigureAwait(false);
                var items = new List<CloudFileItem> { SourceFolderItem(repository) };

                // A repository with no commits has nothing to archive; offering a zip GitHub
                // would refuse to produce is worse than offering none.
                if (tree.Entries.Count > 0)
                {
                    items.Add(ArchiveItem(repository));
                }

                return items;
            }

            case GitHubPathKind.Tree:
            {
                var repository = await client.RepositoryAsync(parsed.Owner, parsed.Repo, cancellationToken).ConfigureAwait(false);
                var entries = await client.ListingAsync(repository, parsed.TreePath, cancellationToken).ConfigureAwait(false);
                return entries
                    .OrderBy(e => e.IsDirectory ? 0 : 1)
                    .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(e => EntryItem(e, repository))
                    .ToList();
            }

            default:
                // The archive is a file, not a directory.
                throw CloudProviderException.NotFound(path);
        }
    }

    public override async Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var (client, parsed) = Resolve(path);
        switch (parsed.Kind)
        {
            case GitHubPathKind.Root:
                return new CloudFileItem("github:root", "/", "/", true, 0, CloudFileItem.UnknownDate) { Role = CloudItemRole.VirtualContainer };

            case GitHubPathKind.Owner:
            {
                var match = (await client.OwnersAsync(cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault(o => string.Equals(o.Login, parsed.Owner, StringComparison.OrdinalIgnoreCase));
                return match is null ? throw CloudProviderException.NotFound(path) : OwnerItem(match);
            }

            case GitHubPathKind.Repository:
                return RepositoryItem(await client.RepositoryAsync(parsed.Owner, parsed.Repo, cancellationToken).ConfigureAwait(false));

            case GitHubPathKind.Archive:
                return ArchiveItem(await client.RepositoryAsync(parsed.Owner, parsed.Repo, cancellationToken).ConfigureAwait(false));

            default:
            {
                var repository = await client.RepositoryAsync(parsed.Owner, parsed.Repo, cancellationToken).ConfigureAwait(false);
                if (parsed.TreePath.Length == 0)
                {
                    return SourceFolderItem(repository);
                }

                var slash = parsed.TreePath.LastIndexOf('/');
                var parent = slash < 0 ? string.Empty : parsed.TreePath[..slash];
                var entries = await client.ListingAsync(repository, parent, cancellationToken).ConfigureAwait(false);
                var entry = entries.FirstOrDefault(e => e.Path == parsed.TreePath);
                return entry is null ? throw CloudProviderException.NotFound(path) : EntryItem(entry, repository);
            }
        }
    }

    public override async Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        var (client, parsed) = Resolve(path);
        switch (parsed.Kind)
        {
            case GitHubPathKind.Root:
                return (await client.RepositoriesAsync(cancellationToken).ConfigureAwait(false)).Sum(r => r.EstimatedBytes);

            case GitHubPathKind.Owner:
                return (await client.RepositoriesAsync(cancellationToken).ConfigureAwait(false))
                    .Where(r => string.Equals(r.Owner, parsed.Owner, StringComparison.OrdinalIgnoreCase))
                    .Sum(r => r.EstimatedBytes);

            case GitHubPathKind.Repository:
            {
                // What a recursive copy of this folder would fetch: the whole tree plus the
                // archive sitting beside it.
                var repository = await client.RepositoryAsync(parsed.Owner, parsed.Repo, cancellationToken).ConfigureAwait(false);
                var tree = await client.TreeAsync(repository, cancellationToken).ConfigureAwait(false);
                var treeBytes = tree.IsTruncated ? repository.EstimatedBytes : tree.RecursiveSize(string.Empty);
                return treeBytes + repository.EstimatedBytes;
            }

            case GitHubPathKind.Tree:
            {
                var repository = await client.RepositoryAsync(parsed.Owner, parsed.Repo, cancellationToken).ConfigureAwait(false);
                var tree = await client.TreeAsync(repository, cancellationToken).ConfigureAwait(false);

                // A truncated tree carries no reliable total; the repository's own size is the
                // only figure available.
                return tree.IsTruncated ? repository.EstimatedBytes : tree.RecursiveSize(parsed.TreePath);
            }

            default:
                return (await client.RepositoryAsync(parsed.Owner, parsed.Repo, cancellationToken).ConfigureAwait(false)).EstimatedBytes;
        }
    }

    // MARK: - Download

    public override async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var (client, parsed) = Resolve(remotePath);
        switch (parsed.Kind)
        {
            case GitHubPathKind.Archive:
            {
                var repository = await client.RepositoryAsync(parsed.Owner, parsed.Repo, cancellationToken).ConfigureAwait(false);
                await client.DownloadArchiveAsync(repository, localPath, onBytes, cancellationToken).ConfigureAwait(false);
                return;
            }

            case GitHubPathKind.Tree when parsed.TreePath.Length > 0:
            {
                var repository = await client.RepositoryAsync(parsed.Owner, parsed.Repo, cancellationToken).ConfigureAwait(false);
                await client.DownloadFileAsync(repository, parsed.TreePath, localPath, onBytes, cancellationToken).ConfigureAwait(false);
                return;
            }

            default:
                throw CloudProviderException.NotFound(remotePath);
        }
    }

    // MARK: - Storage

    /// <summary>
    /// GitHub has no storage quota, so this is a usage figure with no upper bound — the sum of
    /// what every visible repository takes up.
    /// </summary>
    public override async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not { } client)
        {
            return null;
        }

        var used = (await client.RepositoriesAsync(cancellationToken).ConfigureAwait(false)).Sum(r => r.EstimatedBytes);
        return new CloudStorageQuota(used, null);
    }

    // MARK: - Writes

    // Deliberately unimplemented. A GitHub account is a source: the way to change a
    // repository is git, not a file manager.

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    // MARK: - Items

    private static CloudFileItem OwnerItem(GitHubOwner owner) => new(
        $"github:owner:{owner.Login}", owner.Login, "/" + owner.Login, true, 0, owner.LastPush)
    {
        Role = CloudItemRole.VirtualContainer,
        KindOverride = owner.RepositoryCount == 1
            ? Localization.L("1 repository")
            : Localization.L("{0} repositories", owner.RepositoryCount),
    };

    private static CloudFileItem RepositoryItem(GitHubRepository repository) => new(
        $"github:repo:{repository.FullName}", repository.Name, "/" + repository.FullName, true, 0, repository.PushedAt)
    {
        Role = CloudItemRole.DriveRoot,

        // Whether a repository is private is the point of the overview: readable at a glance
        // in the (sortable) Kind column, not two clicks away.
        KindOverride = (repository.IsPrivate, repository.IsArchived) switch
        {
            (true, true) => Localization.L("Private repository (archived)"),
            (false, true) => Localization.L("Public repository (archived)"),
            (true, false) => Localization.L("Private repository"),
            (false, false) => Localization.L("Public repository"),
        },
    };

    private static CloudFileItem SourceFolderItem(GitHubRepository repository) => new(
        $"github:source:{repository.FullName}",
        GitHubPath.SourceFolderName,
        $"/{repository.FullName}/{GitHubPath.SourceFolderName}",
        true,
        0,
        repository.PushedAt)
    {
        KindOverride = Localization.L("Branch: {0}", repository.DefaultBranch),
    };

    private static CloudFileItem ArchiveItem(GitHubRepository repository)
    {
        var filename = GitHubPath.ArchiveFilename(repository.Name, repository.DefaultBranch);

        // Size: GitHub's repository size (packed git objects), not this zip — close enough for
        // the size column and the free-space pre-flight, never treated as exact. Date: the last
        // push, so an incremental backup re-copies a repository only after someone pushed to it.
        return new CloudFileItem(
            $"github:archive:{repository.FullName}@{repository.DefaultBranch}",
            filename,
            $"/{repository.FullName}/{filename}",
            false,
            repository.EstimatedBytes,
            repository.PushedAt)
        {
            // Says what the file is rather than "ZIP archive": one branch with no history, no
            // LFS content and no submodules, and a backup is the wrong place to leave that implicit.
            KindOverride = Localization.L("Snapshot archive (no history)"),
        };
    }

    /// <summary>
    /// Git stores no per-file modification time, and asking for the last commit that touched
    /// each file would be one request per row. The repository's last push is the honest
    /// approximation, and it errs towards re-copying rather than skipping a change.
    /// </summary>
    private static CloudFileItem EntryItem(GitHubTreeEntry entry, GitHubRepository repository) => new(
        $"github:blob:{repository.FullName}:{entry.Sha}:{entry.Path}",
        entry.Name,
        $"/{repository.FullName}/{GitHubPath.SourceFolderName}/{entry.Path}",
        entry.IsDirectory,
        entry.IsDirectory ? 0 : entry.Size,
        repository.PushedAt)
    {
        IsHidden = CloudFileItem.IsDotHidden(entry.Name),
    };

    // MARK: - Private

    private (GitHubApiClient Client, GitHubPath Path) Resolve(string path)
    {
        var client = _client ?? throw CloudProviderException.NotAuthenticated();
        var parsed = GitHubPath.Parse(path) ?? throw CloudProviderException.NotFound(path);
        return (client, parsed);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
        }

        base.Dispose(disposing);
    }
}
