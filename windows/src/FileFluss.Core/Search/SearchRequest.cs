// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;

namespace FileFluss.Core.Search;

/// <summary>An account to search, as the request names it.</summary>
public sealed record SearchAccount(Guid Id, string Name, CloudProviderType? ProviderType = null);

/// <summary>A drive to search: a mounted one by its root, an unplugged one by its index.</summary>
public sealed record SearchDrive(string Id, string Name, string? RootPath = null);

/// <summary>What the app knows when a search starts: where the active panel is, and the accounts.</summary>
/// <param name="ActiveLocation">The active panel's location; null when unknown.</param>
public sealed record SearchContext(PanelLocation? ActiveLocation, IReadOnlyList<CloudAccount> Accounts)
{
    /// <summary>
    /// The local folder to search when the active panel shows a cloud account — the panel's
    /// last local folder, if the app keeps one (macOS searched the panel's local folder whatever
    /// it showed). Ignored when the active location is local.
    /// </summary>
    public string? LocalFolder { get; init; }
}

/// <summary>Everything one search queries (macOS <c>SearchCoordinator.SearchRequest</c>).</summary>
public sealed record SearchRequest(string Query)
{
    public SearchScope Scope { get; init; } = SearchScope.AllSources;

    /// <summary>The local folder to search (native path), or null.</summary>
    public string? LocalDirectory { get; init; }

    /// <summary>The active cloud account and folder, for the narrower scopes.</summary>
    public Guid? CloudAccountId { get; init; }

    public string? CloudPath { get; init; }

    /// <summary>Connected, online accounts: searched live.</summary>
    public IReadOnlyList<SearchAccount> Accounts { get; init; } = [];

    /// <summary>
    /// Mounted drives searched alongside the local folder (scope <see cref="SearchScope.AllSources"/>),
    /// so files on them are found without navigating there first. A drive holding the local
    /// folder is skipped — the folder search already covers it.
    /// </summary>
    public IReadOnlyList<SearchDrive> MountedDrives { get; init; } = [];

    /// <summary>Also search the offline index of disconnected accounts and unplugged drives.</summary>
    public bool IncludeOffline { get; init; }

    public IReadOnlyList<SearchAccount> OfflineAccounts { get; init; } = [];

    public IReadOnlyList<SearchDrive> OfflineDrives { get; init; } = [];

    /// <summary>
    /// The request the search window makes (macOS <c>SearchPopupView.triggerSearch</c>):
    /// online accounts are searched live, forced-offline and disconnected ones from the index,
    /// mounted drives on disk and unplugged indexed drives from the index.
    /// </summary>
    public static SearchRequest Build(
        string query,
        SearchContext context,
        IEnumerable<KnownDrive> drives,
        bool includeOffline,
        SearchScope scope = SearchScope.AllSources)
    {
        var active = context.ActiveLocation;
        string? localDirectory;
        Guid? cloudAccountId = null;
        string? cloudPath = null;
        if (active is { IsCloud: true })
        {
            cloudAccountId = active.AccountId;
            cloudPath = active.Path;
            localDirectory = context.LocalFolder;
        }
        else
        {
            localDirectory = active?.Path;
        }

        var driveList = drives.ToList();
        return new SearchRequest(query)
        {
            Scope = scope,
            LocalDirectory = string.IsNullOrEmpty(localDirectory) ? null : localDirectory,
            CloudAccountId = cloudAccountId,
            CloudPath = cloudPath,
            Accounts = context.Accounts
                .Where(a => !a.IsEffectivelyOffline)
                .Select(a => new SearchAccount(a.Id, a.DisplayName, a.ProviderType))
                .ToList(),
            OfflineAccounts = context.Accounts
                .Where(a => a.IsEffectivelyOffline)
                .Select(a => new SearchAccount(a.Id, a.DisplayName, a.ProviderType))
                .ToList(),
            MountedDrives = driveList
                .Where(d => d.IsOnline && d.RootPath is not null)
                .Select(d => new SearchDrive(d.Id, d.DisplayName, d.RootPath))
                .ToList(),
            OfflineDrives = driveList
                .Where(d => !d.IsOnline && d.IsIndexed)
                .Select(d => new SearchDrive(d.Id, d.DisplayName))
                .ToList(),
            IncludeOffline = includeOffline,
        };
    }
}
