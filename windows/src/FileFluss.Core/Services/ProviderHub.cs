// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Services;

/// <summary>
/// The live provider for each connected account — macOS <c>SyncEngine.shared</c>. Panels,
/// transfers, search and indexing all resolve providers here, so re-authenticating an
/// account (a new provider instance for the same id) takes effect everywhere at once.
/// </summary>
public sealed class ProviderHub
{
    private readonly ConcurrentDictionary<Guid, CloudProvider> _providers = new();

    public event EventHandler<Guid>? ProviderChanged;

    public void Register(Guid accountId, CloudProvider provider)
    {
        if (_providers.TryGetValue(accountId, out var previous) && !ReferenceEquals(previous, provider))
        {
            previous.Dispose();
        }

        _providers[accountId] = provider;
        ProviderChanged?.Invoke(this, accountId);
    }

    public void Remove(Guid accountId)
    {
        if (_providers.TryRemove(accountId, out var provider))
        {
            provider.Dispose();
            ProviderChanged?.Invoke(this, accountId);
        }
    }

    public CloudProvider? Get(Guid accountId) => _providers.GetValueOrDefault(accountId);

    /// <summary>The provider for a location: the local disk, or the account's provider (null when not connected, and for offline drives, which the app serves from the index).</summary>
    public FileProvider? For(Models.PanelLocation location)
        => location.IsCloud ? Get(location.AccountId!.Value) : location.IsLocal ? LocalFileProvider.Shared : null;

    public IReadOnlyCollection<Guid> ConnectedAccountIds => _providers.Keys.ToList();
}
