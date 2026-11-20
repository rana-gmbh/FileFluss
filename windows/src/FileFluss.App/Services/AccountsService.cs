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

#if DEBUG
    /// <summary>
    /// Debug builds' "Restore Test Accounts": adds saved accounts that aren't here (their
    /// credentials already written back) and connects them. Returns how many were added.
    /// </summary>
    public async Task<int> RestoreTestAccountsAsync(IReadOnlyList<CloudAccount> saved)
    {
        var added = saved.Where(a => Find(a.Id) is null).Select(a => a with { IsConnected = false }).ToList();
        foreach (var account in added)
        {
            Accounts.Add(account);
        }

        if (added.Count > 0)
        {
            Save();
            await ReconnectSavedAccountsAsync(added.Select(a => a.Id).ToHashSet());
        }

        return added.Count;
    }
#endif

    /// <summary>Restores every stored account's provider from its credentials (no network for most).</summary>
    /// <param name="only">Just these accounts; null for all.</param>
    public async Task ReconnectSavedAccountsAsync(IReadOnlySet<Guid>? only = null)
    {
        // Making a provider reads and decrypts its stored credentials (DPAPI, files): on the
        // thread pool, so a dozen accounts don't hold up the UI thread at startup.
        var saved = Accounts.Where(a => CloudProviderRegistry.IsAvailable(a.ProviderType) && (only is null || only.Contains(a.Id))).ToList();
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
            // Removed while its provider was being made: nothing to reconnect. Signed in or
            // restored meanwhile (a panel reading its saved sign-in again during startup): the
            // provider already there is at least as fresh as this one.
            if (Find(account.Id) is not { } current || _hub.Get(account.Id) is not null)
            {
                provider.Dispose();
                continue;
            }

            _hub.Register(account.Id, provider);
            Replace(current with { IsConnected = true });
            _ = RefreshLabelAsync(account.Id, provider);
        }
    }

    private readonly Dictionary<Guid, Task<bool>> _restoring = [];

    /// <summary>
    /// Reads the sign-in saved for one account again and puts it back to work, for an account
    /// that has no provider this session (macOS <c>restoreSavedSignIn</c>).
    ///
    /// <para>That happens when the saved credentials couldn't be read at launch — a file
    /// locked by a backup or virus scanner, a profile still being restored — and nothing ever
    /// tried again, so the account stayed "not connected" until the next launch although
    /// nothing about it had changed. Both panels asking at once share one attempt. True when
    /// the account has a provider afterwards.</para>
    /// </summary>
    public Task<bool> RestoreSavedSignInAsync(Guid id)
    {
        if (_hub.Get(id) is not null)
        {
            return Task.FromResult(true);
        }

        if (Find(id) is null)
        {
            return Task.FromResult(false);
        }

        if (_restoring.TryGetValue(id, out var running))
        {
            return running;
        }

        var restore = RestoreCoreAsync(id);
        if (!restore.IsCompleted)
        {
            _restoring[id] = restore;
        }

        return restore;
    }

    private async Task<bool> RestoreCoreAsync(Guid id)
    {
        try
        {
            await ReconnectSavedAccountsAsync(new HashSet<Guid> { id });
            if (_hub.Get(id) is null)
            {
                return false;
            }

            // Credentials to try count as signed in, as at launch; the panel flags the account
            // again if the provider turns them down.
            MarkSignInWorks(id);
            return true;
        }
        finally
        {
            _restoring.Remove(id);
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

    /// <summary>
    /// Edited credentials for an existing account: same id, so favourites and panels survive.
    /// False when the account has been removed meanwhile — the provider is then not taken.
    /// </summary>
    public bool Update(Guid id, CloudProvider provider, ConnectResult result)
    {
        if (Find(id) is not { } account || _removing.Contains(id))
        {
            return false;
        }

        _hub.Register(id, provider);
        _needingSignIn.Remove(id);
        if (_reconnectFailures.Remove(id))
        {
            ReconnectStateChanged?.Invoke(this, id);
        }

        var name = result.DisplayName ?? (string.IsNullOrWhiteSpace(result.AccountLabel) ? account.DisplayName : CloudAccount.ComposeDisplayName(account.ProviderType, result.AccountLabel));
        Replace(account with
        {
            IsConnected = true,
            DisplayName = name,
            RootPath = string.IsNullOrEmpty(result.RootPath) ? account.RootPath : result.RootPath,
        });
        InvalidateQuota(id);
        Save();
        return true;
    }

    public async Task RemoveAsync(Guid id)
    {
        // Remove pressed twice (sidebar and Settings, or a double click) while the first
        // disconnect is still out: one removal, one set of events.
        if (Find(id) is not { } account || !_removing.Add(id))
        {
            return;
        }

        try
        {
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
            _needingSignIn.Remove(id);
            _quotaCache.Remove(id);
            _staleQuotas.Remove(id);

            // Find again: the list may have been replaced entry by entry while disconnecting.
            if (Find(id) is { } current)
            {
                Accounts.Remove(current);
            }

            Save();
            AccountRemoved?.Invoke(this, id);
            AccountChanged?.Invoke(this, id);
        }
        finally
        {
            _removing.Remove(id);
        }
    }

    private readonly HashSet<Guid> _removing = [];

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

    private readonly HashSet<Guid> _needingSignIn = [];

    /// <summary>
    /// The credentials stopped working: the sidebar must stop showing it as connected (issue #46).
    ///
    /// <para>Kept apart from <see cref="CloudAccount.IsConnected"/> (macOS 1.6
    /// <c>accountsNeedingSignIn</c>): clearing that flag made the panel read-only and took
    /// the account's menus with it, for as long as the session lasted — even once a later
    /// listing proved the sign-in worked again. Session-only, never saved.</para>
    /// </summary>
    public void MarkNeedsReauthentication(Guid id)
    {
        if (Find(id) is not null && _needingSignIn.Add(id))
        {
            SupportLogger.Shared.Log("An account's sign-in stopped working", "Accounts", LogLevel.Notice);
            SignInChanged?.Invoke(this, id);
            AccountChanged?.Invoke(this, id);
        }
    }

    /// <summary>A listing or a sign-in succeeded: the account no longer needs signing in again.</summary>
    public void MarkSignInWorks(Guid id)
    {
        if (_needingSignIn.Remove(id))
        {
            SignInChanged?.Invoke(this, id);
            AccountChanged?.Invoke(this, id);
        }
    }

    /// <summary>Raised when an account starts or stops needing to sign in again (the sidebar's status dot).</summary>
    public event EventHandler<Guid>? SignInChanged;

    /// <summary>The account's sign-in failed this session and hasn't worked since.</summary>
    public bool NeedsSignIn(Guid id) => _needingSignIn.Contains(id);

    /// <summary>What the status dot shows: connected, not offline, and the sign-in still works.</summary>
    public bool IsSignedIn(CloudAccount account) => account.IsConnected && !account.IsOfflineMode && !_needingSignIn.Contains(account.Id);

    // MARK: - Reconnecting (macOS AppState.reconnect)

    private readonly Dictionary<Guid, string> _reconnectFailures = [];
    private CancellationTokenSource? _browserReconnect;

    /// <summary>
    /// The account waiting on a browser sign-in started from a panel or the sidebar, so every
    /// panel showing it says so and offers Cancel. One at a time: two loopback sign-ins would
    /// race for the browser's attention.
    /// </summary>
    public Guid? BrowserReconnectAccountId { get; private set; }

    /// <summary>Why the last browser sign-in for an account failed, shown under the panel's Sign In Again until the next attempt.</summary>
    public string? ReconnectFailure(Guid id) => _reconnectFailures.GetValueOrDefault(id);

    /// <summary>Raised when a browser sign-in starts or ends, or a failure is shown or cleared.</summary>
    public event EventHandler<Guid>? ReconnectStateChanged;

    /// <summary>
    /// Signs an account in again on the provider's own page in the browser — nothing to type,
    /// so no form opens. Every panel showing the account lists again once it worked (through
    /// <see cref="Update"/>). False when the provider has no such sign-in: the caller opens the
    /// account's form instead.
    /// </summary>
    public bool ReconnectInBrowser(Guid id)
    {
        if (BrowserReconnectAccountId is not null)
        {
            // Already waiting (for this account or another): Cancel comes first.
            return true;
        }

        if (Find(id) is not { } account)
        {
            return true;
        }

        CloudProvider provider;
        try
        {
            provider = CreateProvider(account.ProviderType, id);
        }
        catch (Exception e)
        {
            // Not available in this version, or its stored settings couldn't be read back.
            _reconnectFailures[id] = e is NotSupportedException ? Core.Localization.L("This account type isn't available in this version.") : e.Message;
            ReconnectStateChanged?.Invoke(this, id);
            return true;
        }

        if (provider.ConnectSpec.BrowserOnlyMode is not { } mode)
        {
            provider.Dispose();
            return false;
        }

        var attempt = _browserReconnect = new CancellationTokenSource();
        BrowserReconnectAccountId = id;
        _reconnectFailures.Remove(id);
        ReconnectStateChanged?.Invoke(this, id);
        _ = RunBrowserReconnectAsync(id, provider, mode, attempt);
        return true;
    }

    private async Task RunBrowserReconnectAsync(Guid id, CloudProvider provider, ConnectMode mode, CancellationTokenSource attempt)
    {
        string? failure = null;
        try
        {
            // A sign-in that completes although Cancel was pressed meanwhile is kept, as on
            // macOS: its tokens are saved already, and the old provider can't use them.
            var result = await provider.ConnectAsync(ConnectValues.Empty(mode.Id), SilentConnectPrompt.Instance, attempt.Token);
            if (!Update(id, provider, result))
            {
                // The account was removed while signing in.
                provider.Dispose();
            }
        }
        catch (Exception e) when (e is OperationCanceledException || e is CloudProviderException { Kind: CloudErrorKind.Cancelled })
        {
            // Cancel in a panel, or the browser flow given up: nothing to report.
            provider.Dispose();
        }
        catch (Exception e)
        {
            provider.Dispose();
            failure = e.Message;
            SupportLogger.Shared.Log($"Signing in to {provider.ProviderType.DisplayName()} again failed: {e.Message}", "Accounts", LogLevel.Error);
        }
        finally
        {
            // A cancelled attempt no longer drives the panels: Cancel already reset them.
            if (ReferenceEquals(_browserReconnect, attempt))
            {
                _browserReconnect = null;
                BrowserReconnectAccountId = null;
                if (failure is not null)
                {
                    _reconnectFailures[id] = failure;
                }

                ReconnectStateChanged?.Invoke(this, id);
            }

            attempt.Dispose();
        }
    }

    /// <summary>Ends the browser sign-in being waited on — its loopback listener stops, the panels offer Sign In Again again.</summary>
    public void CancelBrowserReconnect()
    {
        if (_browserReconnect is not { } attempt || BrowserReconnectAccountId is not { } id)
        {
            return;
        }

        _browserReconnect = null;
        BrowserReconnectAccountId = null;
        attempt.Cancel();
        ReconnectStateChanged?.Invoke(this, id);
    }

    /// <summary>Re-registers a provider after an inline re-authentication from the panel banner.</summary>
    public void MarkConnected(Guid id, CloudProvider provider)
    {
        if (Find(id) is null || _removing.Contains(id))
        {
            // Removed while signing in again: nothing to connect, and the hub must not hold it.
            provider.Dispose();
            return;
        }

        _hub.Register(id, provider);
        _needingSignIn.Remove(id);
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
