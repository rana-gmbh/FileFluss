// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Formatting;
using FileFluss.Core.IO;
using FileFluss.Core.Models;

namespace FileFluss.Core.Search;

/// <summary>How far a search reaches (macOS <c>SearchScope</c>). The search window uses <see cref="AllSources"/>.</summary>
public enum SearchScope
{
    /// <summary>The active panel's folder, not its subfolders.</summary>
    CurrentFolder,

    /// <summary>Everything in the active panel's source: the local folder tree, or the active cloud account.</summary>
    CurrentSource,

    /// <summary>The local folder tree, every mounted drive, every connected account, and — when enabled — the offline index.</summary>
    AllSources,
}

public static class SearchScopes
{
    public static string Label(this SearchScope scope) => scope switch
    {
        SearchScope.CurrentFolder => Localization.L("Current Folder"),
        SearchScope.CurrentSource => Localization.L("This Source"),
        _ => Localization.L("All Sources"),
    };
}

public enum SearchResultKind
{
    /// <summary>Found on disk: the active folder's tree or a mounted drive. Native Windows path.</summary>
    Local,

    /// <summary>A connected account: its server-side search, or its cache. POSIX path.</summary>
    Cloud,

    /// <summary>The offline index of an unplugged drive or a disconnected account. POSIX path.</summary>
    Offline,
}

/// <summary>Where an offline result came from.</summary>
public enum OfflineSourceKind
{
    Drive,
    Cloud,
}

/// <summary>What the panel has to show to reveal a result.</summary>
public enum SearchLocationKind
{
    /// <summary>A local folder: <see cref="PanelLocation.Local"/>.</summary>
    Local,

    /// <summary>A connected account's folder: <see cref="PanelLocation.Cloud"/>.</summary>
    Cloud,

    /// <summary>An offline account's folder, browsed from the index (<see cref="OfflineIndexProvider.ForCloudAccount(SearchIndex, CloudAccount)"/>).</summary>
    OfflineCloud,

    /// <summary>An unplugged drive's folder, browsed from the index (<see cref="OfflineIndexProvider.ForDrive(SearchIndex, KnownDrive)"/>).</summary>
    OfflineDrive,
}

/// <summary>
/// Everything the UI needs to "navigate the active panel to the result's folder": which kind
/// of place, which account or drive, the folder to open (the result itself when it is a
/// folder, its parent otherwise) and the file to select once there.
/// </summary>
public sealed record SearchNavigationTarget(SearchLocationKind Kind, string FolderPath, string ItemPath)
{
    public Guid? AccountId { get; init; }

    /// <summary>The drive's "vol:…" id, for <see cref="SearchLocationKind.OfflineDrive"/>.</summary>
    public string? DriveId { get; init; }

    /// <summary>The path of the file to select after navigating; null for folders, which are opened.</summary>
    public string? SelectPath { get; init; }

    /// <summary>
    /// The panel location for local and cloud targets (an offline account is still a cloud
    /// location — the panel shows it from the index because the account is offline). Null for
    /// an offline drive, which <see cref="PanelLocation"/> cannot express yet.
    /// </summary>
    public PanelLocation? ToPanelLocation() => Kind switch
    {
        SearchLocationKind.Local => PanelLocation.Local(FolderPath),
        SearchLocationKind.Cloud or SearchLocationKind.OfflineCloud when AccountId is { } id => PanelLocation.Cloud(id, FolderPath),
        _ => null,
    };
}

/// <summary>One search hit (macOS <c>SearchResultItem</c>). Equality is by value; de-duplicate by <see cref="Id"/>.</summary>
public sealed record SearchResultItem
{
    private SearchResultItem(SearchResultKind kind, CloudFileItem item, string sourceName, string id)
    {
        Kind = kind;
        Item = item;
        SourceName = sourceName;
        Id = id;
    }

    public SearchResultKind Kind { get; }

    /// <summary>The item as a panel would list it.</summary>
    public CloudFileItem Item { get; }

    /// <summary>Stable across batches and sources: "local:&lt;path&gt;", "cloud:&lt;account&gt;:&lt;id&gt;", "offline:&lt;source&gt;:&lt;path&gt;".</summary>
    public string Id { get; }

    /// <summary>The account for cloud results and offline account results.</summary>
    public Guid? AccountId { get; private init; }

    /// <summary>The offline source: the account's id string or the drive's "vol:…" id.</summary>
    public string? SourceId { get; private init; }

    public OfflineSourceKind? OfflineKind { get; private init; }

    /// <summary>The account or drive name; for local results the drive searched, or empty.</summary>
    public string SourceName { get; }

    public CloudProviderType? ProviderType { get; private init; }

    public static SearchResultItem Local(CloudFileItem item, string sourceName = "")
        => new(SearchResultKind.Local, item, sourceName, "local:" + item.Path);

    public static SearchResultItem Cloud(CloudFileItem item, Guid accountId, string accountName, CloudProviderType? providerType = null)
        => new(SearchResultKind.Cloud, item, accountName, $"cloud:{accountId}:{item.Id}")
        {
            AccountId = accountId,
            ProviderType = providerType,
        };

    public static SearchResultItem Offline(CloudFileItem item, OfflineSourceKind kind, string sourceId, string sourceName)
        => new(SearchResultKind.Offline, item, sourceName, $"offline:{sourceId}:{item.Path}")
        {
            SourceId = sourceId,
            OfflineKind = kind,
            AccountId = kind == OfflineSourceKind.Cloud && Guid.TryParse(sourceId, out var id) ? id : null,
        };

    public string Name => Item.Name;

    public string Path => Item.Path;

    public bool IsDirectory => Item.IsDirectory;

    public long Size => Item.Size;

    public DateTimeOffset ModificationDate => Item.ModificationDate;

    public bool IsOffline => Kind == SearchResultKind.Offline;

    public string FormattedSize => IsDirectory ? "--" : ByteFormatter.Format(Size);

    /// <summary>The containing folder, in the result's own path style.</summary>
    public string ParentPath => Kind == SearchResultKind.Local
        ? System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(Item.Path)) ?? string.Empty
        : CloudPath.Parent(Item.Path);

    /// <summary>The results list's second line: the folder, prefixed with the source for cloud and offline hits.</summary>
    public string LocationDescription => Kind switch
    {
        SearchResultKind.Local => ParentPath,
        SearchResultKind.Cloud => $"{SourceName}: {ParentPath}",
        _ => $"{Localization.L("{0} (offline)", SourceName)}: {ParentPath}",
    };

    /// <summary>The group the results list puts it in: local files together, then one group per account and per offline source.</summary>
    public string GroupKey => Kind switch
    {
        SearchResultKind.Local => "local",
        SearchResultKind.Cloud => $"cloud:{AccountId}",
        _ => $"offline:{SourceId}",
    };

    public string GroupTitle => Kind switch
    {
        SearchResultKind.Local => Localization.L("Local Files"),
        SearchResultKind.Cloud => SourceName,
        _ => Localization.L("{0} (offline)", SourceName),
    };

    public SearchNavigationTarget Navigation
    {
        get
        {
            var folder = IsDirectory ? Item.Path : ParentPath;
            var select = IsDirectory ? null : Item.Path;
            return Kind switch
            {
                SearchResultKind.Local => new SearchNavigationTarget(SearchLocationKind.Local, folder, Item.Path) { SelectPath = select },
                SearchResultKind.Cloud => new SearchNavigationTarget(SearchLocationKind.Cloud, folder, Item.Path) { AccountId = AccountId, SelectPath = select },
                _ when OfflineKind == OfflineSourceKind.Cloud => new SearchNavigationTarget(SearchLocationKind.OfflineCloud, folder, Item.Path) { AccountId = AccountId, SelectPath = select },
                _ => new SearchNavigationTarget(SearchLocationKind.OfflineDrive, folder, Item.Path) { DriveId = SourceId, SelectPath = select },
            };
        }
    }
}

public enum SearchStatus
{
    Idle,
    Searching,
    Complete,
}

/// <summary>What one searched source is.</summary>
public enum SearchSourceKind
{
    /// <summary>The active panel's local folder, walked on disk.</summary>
    LocalFolder,

    /// <summary>A mounted drive other than the one the active folder is on, walked on disk.</summary>
    MountedDrive,

    /// <summary>A connected account: server-side search where the provider has one, plus its cache.</summary>
    CloudAccount,

    /// <summary>The active cloud folder, listed and filtered (scope <see cref="SearchScope.CurrentFolder"/>).</summary>
    CloudFolder,

    /// <summary>An unplugged indexed drive, from the index.</summary>
    OfflineDrive,

    /// <summary>A disconnected or offline-mode account, from the index.</summary>
    OfflineCloudAccount,
}

/// <summary>One source a search queries, known before any of them answers.</summary>
public sealed record SearchSourceInfo(string Key, SearchSourceKind Kind, string Name)
{
    public Guid? AccountId { get; init; }

    public string? DriveId { get; init; }

    /// <summary>The folder or drive root searched, where the source has one.</summary>
    public string? Path { get; init; }

    public CloudProviderType? ProviderType { get; init; }

    public bool IsOffline => Kind is SearchSourceKind.OfflineDrive or SearchSourceKind.OfflineCloudAccount;
}

public enum SearchSourceState
{
    Searching,
    Done,
    Failed,
}

/// <summary>Results from one source as they arrive; the last batch of each source has <see cref="IsComplete"/> set.</summary>
public sealed record SearchResultBatch(string SourceKey, IReadOnlyList<SearchResultItem> Items, bool IsComplete, string? ErrorMessage = null);

public enum SearchGroupKind
{
    Local,
    Cloud,
    Offline,
}

/// <summary>A section of the results list (macOS <c>groupedResults</c>).</summary>
public sealed record SearchResultGroup(string Key, string Title, SearchGroupKind Kind, CloudProviderType? ProviderType, IReadOnlyList<SearchResultItem> Items);

public enum SearchSourceFilterKind
{
    All,
    Local,
    Cloud,
    Offline,
}

/// <summary>The results list's source chips: All, Local, one per account, one per offline source.</summary>
public sealed record SearchSourceFilter(SearchSourceFilterKind Kind, Guid? AccountId = null, string? SourceId = null)
{
    public static SearchSourceFilter All { get; } = new(SearchSourceFilterKind.All);

    public static SearchSourceFilter Local { get; } = new(SearchSourceFilterKind.Local);

    public static SearchSourceFilter Cloud(Guid accountId) => new(SearchSourceFilterKind.Cloud, accountId);

    public static SearchSourceFilter Offline(string sourceId) => new(SearchSourceFilterKind.Offline, null, sourceId);

    public bool Matches(SearchResultItem item) => Kind switch
    {
        SearchSourceFilterKind.Local => item.Kind == SearchResultKind.Local,
        SearchSourceFilterKind.Cloud => item.Kind == SearchResultKind.Cloud && item.AccountId == AccountId,
        SearchSourceFilterKind.Offline => item.Kind == SearchResultKind.Offline && item.SourceId == SourceId,
        _ => true,
    };
}

/// <summary>One chip of the filter bar.</summary>
public sealed record SearchFilterOption(string Label, SearchSourceFilter Filter, CloudProviderType? ProviderType = null);
