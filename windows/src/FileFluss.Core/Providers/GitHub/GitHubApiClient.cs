// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.GitHub;

/// <summary>
/// GitHub REST API v3, read-only (port of the macOS <c>GitHubAPIClient</c>). Thread-safe: one
/// instance serves panels, transfers and the indexer concurrently.
/// </summary>
internal sealed class GitHubApiClient
{
    internal const string LogCategory = "GitHub";

    /// <summary>GitHub pins behaviour per API version; without it the account would silently follow whatever the current default is.</summary>
    internal const string ApiVersion = "2022-11-28";

    private const string JsonMediaType = "application/vnd.github+json";
    private const string RawMediaType = "application/vnd.github.raw";

    /// <summary>
    /// The repository list changes rarely and is read on every navigation back to the account
    /// root, so it is cached briefly. GitHub allows 5000 requests an hour; this keeps ordinary
    /// browsing nowhere near it.
    /// </summary>
    internal static readonly TimeSpan RepositoryCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>100 is GitHub's maximum page size; 20 pages (2000 repositories) is a safety belt against an API that never ends.</summary>
    private const int PageSize = 100;
    private const int MaxPages = 20;

    private readonly HttpClient _http;
    private readonly Lock _gate = new();
    private (DateTimeOffset FetchedAt, IReadOnlyList<GitHubRepository> Repositories)? _cachedRepositories;

    /// <summary>Trees keyed by repository and last-push time, so a push invalidates the entry and nothing else does.</summary>
    private readonly ConcurrentDictionary<string, GitHubTree> _cachedTrees = new(StringComparer.Ordinal);

    public GitHubApiClient(HttpClient http, GitHubCredentials credentials)
    {
        _http = http;
        Credentials = credentials;
    }

    public GitHubCredentials Credentials { get; }

    public string Login => Credentials.Login;

    // MARK: - Authentication

    /// <summary>
    /// Verifies a token and resolves the login it belongs to. A token that cannot read
    /// <c>/user</c> is not worth storing.
    /// </summary>
    public static async Task<GitHubCredentials> VerifyAsync(HttpClient http, string token, CancellationToken cancellationToken)
    {
        var probe = new GitHubApiClient(http, new GitHubCredentials(token, string.Empty));
        var body = await probe.GetAsync("/user", [], cancellationToken).ConfigureAwait(false);
        var login = GitHubResponseParser.Login(body);
        if (string.IsNullOrEmpty(login))
        {
            throw CloudProviderException.InvalidResponse();
        }

        SupportLogger.Shared.Log($"[GitHub] token verified for {login}", LogCategory);
        return new GitHubCredentials(token, login, GitHubCredentials.PublicApiBaseUrl, GitHubCredentials.PublicWebBaseUrl);
    }

    // MARK: - Repositories

    /// <summary>Every repository the token can see, newest push first.</summary>
    public async Task<IReadOnlyList<GitHubRepository>> RepositoriesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cachedRepositories is { } cached && DateTimeOffset.UtcNow - cached.FetchedAt < RepositoryCacheTtl)
            {
                return cached.Repositories;
            }
        }

        var collected = new List<GitHubRepository>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var body = await GetAsync("/user/repos",
            [
                ("per_page", PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("page", page.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("affiliation", "owner,collaborator,organization_member"),
                ("sort", "pushed"),
            ], cancellationToken).ConfigureAwait(false);
            var batch = GitHubResponseParser.Repositories(body);
            collected.AddRange(batch);
            if (batch.Count < PageSize)
            {
                break;
            }
        }

        lock (_gate)
        {
            _cachedRepositories = (DateTimeOffset.UtcNow, collected);
        }

        SupportLogger.Shared.Log($"[GitHub] listed {collected.Count} repositories", LogCategory);
        return collected;
    }

    public async Task<GitHubRepository> RepositoryAsync(string owner, string name, CancellationToken cancellationToken)
    {
        var all = await RepositoriesAsync(cancellationToken).ConfigureAwait(false);
        var match = all.FirstOrDefault(r =>
            string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }

        // Not in the user's own list — it may still be readable (a repository reached by path
        // rather than by browsing), so ask directly.
        var body = await GetAsync($"/repos/{CloudHttp.Escape(owner)}/{CloudHttp.Escape(name)}", [], cancellationToken).ConfigureAwait(false);
        try
        {
            return GitHubResponseParser.Repository(body);
        }
        catch (CloudProviderException)
        {
            throw CloudProviderException.NotFound($"{owner}/{name}");
        }
    }

    /// <summary>Repository owners, each with the latest push across their repositories (the owner folder's date).</summary>
    public async Task<IReadOnlyList<GitHubOwner>> OwnersAsync(CancellationToken cancellationToken)
    {
        var repositories = await RepositoriesAsync(cancellationToken).ConfigureAwait(false);
        return repositories
            .GroupBy(r => r.Owner, StringComparer.Ordinal)
            .Select(group => new GitHubOwner(group.Key, group.Max(r => r.PushedAt), group.Count()))
            .OrderBy(o => o.Login, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // MARK: - Trees

    /// <summary>
    /// The repository's whole tree at its default branch. An empty repository (created but
    /// never pushed to) has no tree and GitHub answers 404 — reported as an empty tree,
    /// because an empty repository is a legitimate thing to browse into, not an error.
    /// </summary>
    public async Task<GitHubTree> TreeAsync(GitHubRepository repository, CancellationToken cancellationToken)
    {
        var key = $"{repository.FullName}@{repository.PushedAt.UtcTicks}";
        if (_cachedTrees.TryGetValue(key, out var cached))
        {
            return cached;
        }

        string body;
        try
        {
            body = await GetAsync($"{RepositoryPath(repository)}/git/trees/{CloudHttp.EscapePath(repository.DefaultBranch)}", [("recursive", "1")], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            _cachedTrees[key] = GitHubTree.Empty;
            return GitHubTree.Empty;
        }

        var tree = GitHubResponseParser.Tree(body);
        if (tree.IsTruncated)
        {
            SupportLogger.Shared.Log($"[GitHub] tree for {repository.FullName} is truncated; per-directory listing will be used", LogCategory);
        }

        _cachedTrees[key] = tree;
        return tree;
    }

    /// <summary>Entries directly inside <paramref name="directory"/>, from the Contents API when the recursive tree came back truncated.</summary>
    public async Task<IReadOnlyList<GitHubTreeEntry>> ListingAsync(GitHubRepository repository, string directory, CancellationToken cancellationToken)
    {
        var tree = await TreeAsync(repository, cancellationToken).ConfigureAwait(false);
        return tree.IsTruncated
            ? await ContentsListingAsync(repository, directory, cancellationToken).ConfigureAwait(false)
            : tree.Children(directory);
    }

    /// <summary>
    /// One directory straight from the Contents API. Used only for repositories too large for
    /// a recursive tree: one request per folder the user opens, with exact per-file sizes.
    /// </summary>
    private async Task<IReadOnlyList<GitHubTreeEntry>> ContentsListingAsync(GitHubRepository repository, string directory, CancellationToken cancellationToken)
    {
        var body = await GetAsync($"{RepositoryPath(repository)}/contents/{CloudHttp.EscapePath(directory)}", [("ref", repository.DefaultBranch)], cancellationToken)
            .ConfigureAwait(false);
        return GitHubResponseParser.ContentsListing(body) ?? throw CloudProviderException.NotFound(directory);
    }

    // MARK: - Downloads

    /// <summary>
    /// Streams a zipball of the default branch. The endpoint answers 302 with a short-lived
    /// signed codeload URL; HttpClient follows it and drops the Authorization header on the
    /// way, which the signed URL doesn't need. What this artifact is: a snapshot of one
    /// branch's working tree — no history, no LFS objects (only their pointer files), no
    /// submodule contents, nothing outside the git repository. The archive's Kind says so.
    /// </summary>
    public Task DownloadArchiveAsync(GitHubRepository repository, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
        => DownloadAsync($"{RepositoryPath(repository)}/zipball/{CloudHttp.EscapePath(repository.DefaultBranch)}", [], JsonMediaType, localPath, repository.FullName, onBytes, cancellationToken);

    /// <summary>
    /// Streams one file. The raw media type returns the blob itself rather than base64 JSON,
    /// which keeps memory flat and works up to GitHub's 100 MB per-file ceiling.
    /// </summary>
    public Task DownloadFileAsync(GitHubRepository repository, string path, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
        => DownloadAsync($"{RepositoryPath(repository)}/contents/{CloudHttp.EscapePath(path)}", [("ref", repository.DefaultBranch)], RawMediaType, localPath, path, onBytes, cancellationToken);

    private async Task DownloadAsync(
        string path, (string Name, string Value)[] query, string accept, string localPath, string subject, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        using var request = Request(path, query, accept);
        using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        Check(response, subject);
        await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    // MARK: - Requests

    private static string RepositoryPath(GitHubRepository repository)
        => $"/repos/{CloudHttp.Escape(repository.Owner)}/{CloudHttp.Escape(repository.Name)}";

    private HttpRequestMessage Request(string path, (string Name, string Value)[] query, string accept = JsonMediaType)
    {
        var url = Credentials.EffectiveApiBaseUrl + path;
        if (query.Length > 0)
        {
            url += "?" + string.Join('&', query.Select(q => $"{CloudHttp.Escape(q.Name)}={CloudHttp.Escape(q.Value)}"));
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = CloudHttp.Bearer(Credentials.Token);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        return request;
    }

    private async Task<string> GetAsync(string path, (string Name, string Value)[] query, CancellationToken cancellationToken)
    {
        using var request = Request(path, query);
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        Check(response, path);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void Check(HttpResponseMessage response, string path)
    {
        var status = (int)response.StatusCode;
        switch (status)
        {
            case >= 200 and < 300:
                return;
            case 401:
                throw CloudProviderException.Unauthorized();
            case 403 or 429:
                // 403 covers both "rate limited" and "this token may not see that" — the
                // remaining-requests header is what separates them.
                if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0")
                {
                    throw CloudProviderException.RateLimited();
                }

                throw CloudProviderException.ServerError(status);
            case 404:
                throw CloudProviderException.NotFound(path);
            default:
                throw CloudProviderException.ServerError(status);
        }
    }
}

/// <summary>A repository owner as the account root lists it.</summary>
internal sealed record GitHubOwner(string Login, DateTimeOffset LastPush, int RepositoryCount);
