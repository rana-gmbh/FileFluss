// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.CompilerServices;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Search;

/// <summary>
/// Searches one connected account (macOS <c>CloudSearcher</c>): first what the index already
/// has — instantly — then the provider's own search where it has one, whose fresh results
/// also go into the index. A provider without server-side search is answered from the index
/// alone.
/// </summary>
public sealed class CloudSearcher(SearchIndex index)
{
    /// <summary>
    /// Batches of results; the server's batch leaves out what the index already returned. A
    /// failing server search throws after the cached batch has been yielded, so the caller
    /// keeps those results and can still report the source as failed.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<SearchResultItem>> SearchAsync(
        string query,
        FileProvider? provider,
        SearchAccount account,
        string? rootPath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 1. Cached results, at once.
        var cached = await Task.Run(() => index.Search(query, account.Id), cancellationToken).ConfigureAwait(false);
        if (cached.Count > 0)
        {
            yield return cached.Select(c => SearchResultItem.Cloud(c.Item, account.Id, account.Name, account.ProviderType)).ToList();
        }

        if (provider is null)
        {
            yield break;
        }

        // 2. The provider's native search, when it has one.
        var found = await provider.SearchFilesAsync(query, rootPath, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            yield break;
        }

        index.UpsertItemsLogging(found, account.Id, "search");

        var cachedPaths = new HashSet<string>(cached.Select(c => c.Item.Path), StringComparer.Ordinal);
        var fresh = found
            .Where(item => !cachedPaths.Contains(item.Path))
            .Select(item => SearchResultItem.Cloud(item, account.Id, account.Name, account.ProviderType))
            .ToList();
        if (fresh.Count > 0)
        {
            yield return fresh;
        }
    }

    /// <summary>
    /// The current-folder scope on a cloud panel: one listing of <paramref name="folder"/>,
    /// filtered by name.
    /// </summary>
    public static async IAsyncEnumerable<IReadOnlyList<SearchResultItem>> SearchFolderAsync(
        string query,
        FileProvider provider,
        SearchAccount account,
        string folder,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var items = await provider.ListDirectoryAsync(folder, cancellationToken).ConfigureAwait(false);
        var matches = items
            .Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(i => SearchResultItem.Cloud(i, account.Id, account.Name, account.ProviderType))
            .ToList();
        if (matches.Count > 0)
        {
            yield return matches;
        }
    }
}
