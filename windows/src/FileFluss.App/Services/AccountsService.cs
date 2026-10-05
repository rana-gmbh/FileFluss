// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.ObjectModel;
using FileFluss.App.Mvvm;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Security;
using FileFluss.Core.Services;

namespace FileFluss.App.Services;

/// <summary>
/// The connected accounts and their live providers — the account half of the macOS
/// <c>SyncViewModel</c>. Lives on the UI thread; <see cref="Accounts"/> binds directly.
/// </summary>
public sealed class AccountsService : Observable
{
    private static readonly TimeSpan QuotaTtl = TimeSpan.FromSeconds(120);

    private readonly AccountStore _store;
    private readonly ProviderHub _hub;
    private readonly ICredentialStore _credentials;
    private readonly Dictionary<Guid, CloudStorageQuota> _quotaCache = [];
    private readonly HashSet<Guid> _staleQuotas = [];
    private readonly Dictionary<Guid, Task<CloudStorageQuota?>> _quotaRequests = [];

    public AccountsService(AccountStore store, ProviderHub hub, ICredentialStore credentials)
    {
        _store = store;
        _hub = hub;
        _credentials = credentials;
        foreach (var account in store.Load())
        {
            // Nothing is connected until its provider has been restored.
            Accounts.Add(account with { IsConnected = false });
        }
    }

    public ObservableCollection<CloudAccount> Accounts { get; } = [];

    public ICredentialStore Credentials => _credentials;

    public ProviderHub Hub => _hub;

    /// <summary>Raised when an account is added, removed, renamed, connected or disconnected.</summary>
    public event EventHandler<Guid>? AccountChanged;

    public CloudAccount? Find(Guid id) => Accounts.FirstOrDefault(a => a.Id == id);

    public CloudProvider? ProviderFor(Guid id) => _hub.Get(id);

    /// <summary>A provider for a brand-new account, to run <see cref="CloudProvider.ConnectAsync"/> on.</summary>
    public static CloudProvider CreateProvider(CloudProviderType type, Guid accountId, ICredentialStore credentials)
        => CloudProviderRegistry.Create(type, accountId, credentials);

    public CloudProvider CreateProvider(CloudProviderType type, Guid accountId) => CreateProvider(type, accountId, _credentials);

    /// <summary>Restores every stored account's provider from its credentials (no network for most).</summary>
    public async Task ReconnectSavedAccountsAsync()
    {
        // Making a provider reads and decrypts its stored credentials (DPAPI, files): on the
        // thread pool, so a dozen accounts don't hold up the UI thread at startup.
        var saved = Accounts.Where(a => CloudProviderRegistry.IsAvailable(a.ProviderType)).ToList();
        var restored = await Task.Run(() =>
        {
            var providers = new List<(CloudAccount Account, CloudProvider Provider)>();
            foreach (var account in saved)
            {
                try
                {
                    var provider = CreateProvider(account.ProviderType, account.Id);
                    if (provider.IsAuthenticated)
                    {
                        providers.Add((account, provider));
                    }
                    else
                    {
                        provider.Dispose();
                    }
                }
                catch (Exception e)
                {
                    SupportLogger.Shared.Log($"Restoring {account.DisplayName} failed: {e.Message}", "Accounts", LogLevel.Error);
                }
            }

            return providers;
        });

        foreach (var (account, provider) in restored)
        {
            // Removed while its provider was being made: nothing to reconnect.
            if (Find(account.Id) is not { } current)
            {
                provider.Dispose();
                continue;
            }

            _hub.Register(account.Id, provider);
            Replace(current with { IsConnected = true });
            _ = RefreshLabelAsync(account.Id, provider);
        }
    }

    /// <summary>Dropbox heals a stuck "Unknown" name this way; a failed lookup never replaces a good name.</summary>
    private async Task RefreshLabelAsync(Guid id, CloudProvider provider)
    {
        try
        {
            var label = await provider.RefreshAccountLabelAsync();
            if (!string.IsNullOrWhiteSpace(label) && Find(id) is { } account)
            {
                Replace(account with { DisplayName = CloudAccount.ComposeDisplayName(account.ProviderType, label) });
                Save();
            }
        }
        catch (Exception e)
        {
            SupportLogger.Shared.Log($"Refreshing an account name failed: {e.Message}", "Accounts", LogLevel.Notice);
        }
    }

    /// <summary>Adds an account whose provider just connected successfully.</summary>
    public CloudAccount Add(CloudProvider provider, ConnectResult result)
    {
        var account = new CloudAccount
        {
            Id = provider.AccountId,
            ProviderType = provider.ProviderType,
            DisplayName = result.DisplayName ?? CloudAccount.ComposeDisplayName(provider.ProviderType, result.AccountLabel),
            IsConnected = true,
            RootPath = string.IsNullOrEmpty(result.RootPath) ? "/" : result.RootPath,
        };
        _hub.Register(account.Id, provider);
        Accounts.Add(account);
        Save();
        AccountChanged?.Invoke(this, account.Id);
        return account;
    }

    /// <summary>Edited credentials for an existing account: same id, so favourites and panels survive.</summary>
    public void Update(Guid id, CloudProvider provider, ConnectResult result)
    {
        if (Find(id) is not { } account)
        {
            return;
        }

        _hub.Register(id, provider);
        var name = result.DisplayName ?? (string.IsNullOrWhiteSpace(result.AccountLabel) ? account.DisplayName : CloudAccount.ComposeDisplayName(account.ProviderType, result.AccountLabel));
        Replace(account with
        {
            IsConnected = true,
            DisplayName = name,
            RootPath = string.IsNullOrEmpty(result.RootPath) ? account.RootPath : result.RootPath,
        });
        InvalidateQuota(id);
        Save();
    }

    public async Task RemoveAsync(Guid id)
    {
        if (Find(id) is not { } account)
        {
            return;
        }

        if (_hub.Get(id) is { } provider)
        {
            try
            {
                await provider.DisconnectAsync();
            }
            catch (Exception e)
            {
                SupportLogger.Shared.Log($"Disconnecting {account.DisplayName}: {e.Message}", "Accounts", LogLevel.Notice);
            }
        }
        else
        {
            try
            {
                using var dormant = CreateProvider(account.ProviderType, id);
                await dormant.DisconnectAsync();
            }
            catch (Exception)
            {
                // The type may no longer be available; the stored list entry still goes.
            }
        }

        _hub.Remove(id);
        Accounts.Remove(account);
        Save();
        AccountRemoved?.Invoke(this, id);
        AccountChanged?.Invoke(this, id);
    }

    /// <summary>Lets the search index drop the account's rows, and panels move away from it.</summary>
    public event EventHandler<Guid>? AccountRemoved;

    public void Rename(Guid id, string name)
    {
        if (Find(id) is { } account && !string.IsNullOrWhiteSpace(name))
        {
            Replace(account with { DisplayName = name.Trim() });
            Save();
        }
    }

    public void SetOfflineMode(Guid id, bool offline)
    {
        if (Find(id) is { } account && account.IsOfflineMode != offline)
        {
            Replace(account with { IsOfflineMode = offline });
            Save();
        }
    }

    /// <summary>The credentials stopped working: the sidebar must stop showing it as connected (issue #46).</summary>
    public void MarkNeedsReauthentication(Guid id)
    {
        if (Find(id) is { IsConnected: true } account)
        {
            Replace(account with { IsConnected = false });
            Save();
        }
    }

    /// <summary>Re-registers a provider after an inline re-authentication from the panel banner.</summary>
    public void MarkConnected(Guid id, CloudProvider provider)
    {
        _hub.Register(id, provider);
        if (Find(id) is { } account)
        {
            Replace(account with { IsConnected = true });
            Save();
        }
    }

    /// <summary>
    /// Moves an account to the gap <paramref name="insertionIndex"/> of the list as it is now
    /// (macOS: drag to reorder in the sidebar). The order is the stored order, so it persists.
    /// </summary>
    public void Move(Guid id, int insertionIndex)
    {
        var from = Accounts.ToList().FindIndex(a => a.Id == id);
        if (from < 0)
        {
            return;
        }

        var to = Reordering.TargetIndex(from, insertionIndex, Accounts.Count);
        if (to == from)
        {
            return;
        }

        Accounts.Move(from, to);
        Save();
    }

    // MARK: - Quota

    /// <summary>
    /// Raised on the UI thread when an account's quota was fetched afresh or went stale — the
    /// sidebar tooltips follow it (macOS refreshes them through the same cache).
    /// </summary>
    public event EventHandler<Guid>? QuotaChanged;

    /// <summary>The last known quota, however old; null when none was ever fetched.</summary>
    public CloudStorageQuota? CachedQuota(Guid id) => _quotaCache.GetValueOrDefault(id);

    /// <summary>
    /// Cached for two minutes; on failure the last known figure stays rather than blanking.
    /// Callers asking at the same time share one request.
    /// </summary>
    public Task<CloudStorageQuota?> QuotaAsync(Guid id)
    {
        if (!_staleQuotas.Contains(id) && _quotaCache.TryGetValue(id, out var cached) && DateTimeOffset.Now - cached.FetchedAt < QuotaTtl)
        {
            return Task.FromResult<CloudStorageQuota?>(cached);
        }

        if (_quotaRequests.TryGetValue(id, out var running))
        {
            return running;
        }

        var request = FetchQuotaAsync(id);
        if (!request.IsCompleted)
        {
            _quotaRequests[id] = request;
        }

        return request;
    }

    private async Task<CloudStorageQuota?> FetchQuotaAsync(Guid id)
    {
        try
        {
            // Started on the thread pool: a provider's synchronous first steps must not hold up the UI.
            if (_hub.Get(id) is { } provider && await Task.Run(() => provider.StorageQuotaAsync()) is { } fresh)
            {
                _quotaCache[id] = fresh;
                _staleQuotas.Remove(id);
                QuotaChanged?.Invoke(this, id);
                return fresh;
            }
        }
        catch (Exception)
        {
            // Best-effort.
        }
        finally
        {
            _quotaRequests.Remove(id);
        }

        return _quotaCache.GetValueOrDefault(id);
    }

    /// <summary>
    /// The account's contents changed (a transfer or delete): the next ask fetches afresh. The
    /// old figure stays as the fallback until then.
    /// </summary>
    public void InvalidateQuota(Guid id)
    {
        _staleQuotas.Add(id);
        QuotaChanged?.Invoke(this, id);
    }

    // MARK: - Persistence

    public void Save() => _store.Save(Accounts.ToList());

    private void Replace(CloudAccount updated)
    {
        var index = Accounts.ToList().FindIndex(a => a.Id == updated.Id);
        if (index >= 0)
        {
            Accounts[index] = updated;
            AccountChanged?.Invoke(this, updated.Id);
        }
    }
}
