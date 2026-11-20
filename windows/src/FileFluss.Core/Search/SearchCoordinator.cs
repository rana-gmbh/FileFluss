// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Threading.Channels;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Services;

namespace FileFluss.Core.Search;

/// <summary>A started search: the sources it queries, and their results as they arrive.</summary>
public sealed class SearchRun(IReadOnlyList<SearchSourceInfo> sources, IAsyncEnumerable<SearchResultBatch> batches)
{
    /// <summary>Every source, known before any answers, so the UI can list them as "searching".</summary>
    public IReadOnlyList<SearchSourceInfo> Sources { get; } = sources;

    /// <summary>
    /// Batches from all sources, interleaved as each responds. Every source ends with one
    /// batch marked complete (carrying its error message if it failed). Cancelling the token
    /// passed to <see cref="SearchCoordinator.Start"/> ends the stream with an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public IAsyncEnumerable<SearchResultBatch> Batches { get; } = batches;
}

/// <summary>
/// Universal search (macOS <c>SearchCoordinator</c>): the active local folder, every mounted
/// drive, every connected account, and — when asked — the offline index of unplugged drives
/// and disconnected accounts, all at once. Results stream in per source as each responds.
/// </summary>
public sealed class SearchCoordinator
{
    /// <summary>Per offline source, as macOS limits the offline query.</summary>
    public const int OfflineResultLimit = 300;

    private const string LogCategory = "Search";

    private readonly SearchIndex _index;
    private readonly ProviderHub _providers;
    private readonly LocalSearcher _local;
    private readonly CloudSearcher _cloud;

    public SearchCoordinator(SearchIndex index, ProviderHub providers, LocalSearcher? localSearcher = null)
    {
        _index = index;
        _providers = providers;
        _local = localSearcher ?? new LocalSearcher();
        _cloud = new CloudSearcher(index);
    }

    /// <summary>The sources a request queries, in the order the results list shows them.</summary>
    public static IReadOnlyList<SearchSourceInfo> PlanSources(SearchRequest request)
    {
        var sources = new List<SearchSourceInfo>();
        var cloudActive = request.CloudAccountId is not null;

        // The local folder: always for "all sources"; for the narrower scopes only when the
        // active panel is local.
        if (request.LocalDirectory is { } local && (request.Scope == SearchScope.AllSources || !cloudActive))
        {
            sources.Add(new SearchSourceInfo("local", SearchSourceKind.LocalFolder, Localization.L("Local Files")) { Path = local });
        }

        // Mounted drives only fan out for "all sources", not when the search is scoped to the
        // active panel — and not the drive the local folder is on, which is already covered.
        if (request.Scope == SearchScope.AllSources)
        {
            foreach (var drive in request.MountedDrives)
            {
                if (drive.RootPath is null || (request.LocalDirectory is { } folder && IsOnDrive(folder, drive.RootPath)))
                {
                    continue;
                }

                sources.Add(new SearchSourceInfo("drive:" + drive.Id, SearchSourceKind.MountedDrive, drive.Name) { DriveId = drive.Id, Path = drive.RootPath });
            }
        }

        switch (request.Scope)
        {
            case SearchScope.CurrentFolder when cloudActive:
                if (request.Accounts.FirstOrDefault(a => a.Id == request.CloudAccountId) is { } folderAccount)
                {
                    sources.Add(AccountSource(folderAccount, SearchSourceKind.CloudFolder, request.CloudPath ?? "/"));
                }

                break;
            case SearchScope.CurrentSource when cloudActive:
                if (request.Accounts.FirstOrDefault(a => a.Id == request.CloudAccountId) is { } activeAccount)
                {
                    sources.Add(AccountSource(activeAccount, SearchSourceKind.CloudAccount, request.CloudPath));
                }

                break;
            case SearchScope.AllSources:
                foreach (var account in request.Accounts)
                {
                    sources.Add(AccountSource(account, SearchSourceKind.CloudAccount, null));
                }

                break;
        }

        if (request.IncludeOffline)
        {
            foreach (var drive in request.OfflineDrives)
            {
                sources.Add(new SearchSourceInfo("offline:" + drive.Id, SearchSourceKind.OfflineDrive, drive.Name) { DriveId = drive.Id });
            }

            foreach (var account in request.OfflineAccounts)
            {
                sources.Add(new SearchSourceInfo("offline:" + SearchIndex.SourceIdFor(account.Id), SearchSourceKind.OfflineCloudAccount, account.Name)
                {
                    AccountId = account.Id,
                    ProviderType = account.ProviderType,
                });
            }
        }

        return sources;
    }

    /// <summary>
    /// Starts every source of <paramref name="request"/> in parallel. Cancel
    /// <paramref name="cancellationToken"/> to stop them all (the search window does when the
    /// query changes).
    /// </summary>
    public SearchRun Start(SearchRequest request, CancellationToken cancellationToken)
    {
        var sources = PlanSources(request);
        var channel = Channel.CreateUnbounded<SearchResultBatch>(new UnboundedChannelOptions { SingleReader = true });
        var tasks = sources.Select(source => Task.Run(() => RunSourceAsync(source, request, channel.Writer, cancellationToken), CancellationToken.None)).ToArray();
        _ = Task.WhenAll(tasks).ContinueWith(_ => channel.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new SearchRun(sources, channel.Reader.ReadAllAsync(cancellationToken));
    }

    private async Task RunSourceAsync(SearchSourceInfo source, SearchRequest request, ChannelWriter<SearchResultBatch> writer, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var items in Produce(source, request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (items.Count > 0)
                {
                    writer.TryWrite(new SearchResultBatch(source.Key, items, IsComplete: false));
                }
            }

            writer.TryWrite(new SearchResultBatch(source.Key, [], IsComplete: true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Superseded by a newer query; nobody is listening for this source any more.
        }
        catch (Exception e)
        {
            SupportLogger.Shared.Log($"Search in {source.Name} failed: {e.Message}", LogCategory, LogLevel.Notice);
            writer.TryWrite(new SearchResultBatch(source.Key, [], IsComplete: true, ErrorMessage: e.Message));
        }
    }

    private IAsyncEnumerable<IReadOnlyList<SearchResultItem>> Produce(SearchSourceInfo source, SearchRequest request, CancellationToken cancellationToken)
    {
        var query = request.Query;
        switch (source.Kind)
        {
            case SearchSourceKind.LocalFolder:
                return Map(
                    _local.SearchAsync(query, source.Path!, recursive: request.Scope != SearchScope.CurrentFolder, cancellationToken),
                    item => SearchResultItem.Local(item),
                    cancellationToken);

            case SearchSourceKind.MountedDrive:
                return Map(
                    _local.SearchAsync(query, source.Path!, recursive: true, cancellationToken),
                    item => SearchResultItem.Local(item, source.Name),
                    cancellationToken);

            case SearchSourceKind.CloudAccount:
                return _cloud.SearchAsync(query, _providers.Get(source.AccountId!.Value), Account(source), source.Path, cancellationToken);

            case SearchSourceKind.CloudFolder:
                var provider = _providers.Get(source.AccountId!.Value);
                return provider is null
                    ? Once([])
                    : CloudSearcher.SearchFolderAsync(query, provider, Account(source), source.Path ?? "/", cancellationToken);

            case SearchSourceKind.OfflineDrive:
                return OnceAsync(() => _index.SearchIndexed(query, [source.DriveId!], OfflineResultLimit)
                    .Select(f => SearchResultItem.Offline(f.ToCloudFileItem(), OfflineSourceKind.Drive, f.SourceId, source.Name))
                    .ToList(), cancellationToken);

            case SearchSourceKind.OfflineCloudAccount:
            default:
                var accountId = source.AccountId!.Value;
                return OnceAsync(() => _index.SearchCloudCached(query, [accountId], OfflineResultLimit)
                    .Select(r => SearchResultItem.Offline(r.Item, OfflineSourceKind.Cloud, SearchIndex.SourceIdFor(r.AccountId), source.Name))
                    .ToList(), cancellationToken);
        }
    }

    private static SearchSourceInfo AccountSource(SearchAccount account, SearchSourceKind kind, string? path)
        => new("cloud:" + account.Id, kind, account.Name) { AccountId = account.Id, ProviderType = account.ProviderType, Path = path };

    private static SearchAccount Account(SearchSourceInfo source) => new(source.AccountId!.Value, source.Name, source.ProviderType);

    /// <summary>"E:\Photos" is on the drive mounted at "E:\"; "E:\" itself is too.</summary>
    private static bool IsOnDrive(string folder, string driveRoot)
    {
        var root = driveRoot.TrimEnd('\\', '/');
        var path = folder.TrimEnd('\\', '/');
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path.Length > root.Length && path[root.Length] is '\\' or '/');
    }

    private static async IAsyncEnumerable<IReadOnlyList<SearchResultItem>> Map<T>(
        IAsyncEnumerable<IReadOnlyList<T>> batches,
        Func<T, SearchResultItem> map,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var batch in batches.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return batch.Select(map).ToList();
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<SearchResultItem>> OnceAsync(
        Func<IReadOnlyList<SearchResultItem>> query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await Task.Run(query, cancellationToken).ConfigureAwait(false);
    }

#pragma warning disable CS1998 // An iterator that yields one ready value needs no await.
    private static async IAsyncEnumerable<IReadOnlyList<SearchResultItem>> Once(IReadOnlyList<SearchResultItem> items)
    {
        yield return items;
    }
#pragma warning restore CS1998
}
