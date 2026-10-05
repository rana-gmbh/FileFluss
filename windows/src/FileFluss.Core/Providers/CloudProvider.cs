// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers;

/// <summary>
/// A connected (or connectable) cloud account: the macOS <c>CloudProvider</c> protocol plus
/// the sign-in plumbing each macOS provider repeated by hand.
///
/// <para><b>Lifecycle.</b> The registry constructs a provider with the account's id and the
/// credential store. The constructor restores stored credentials (no network);
/// <see cref="IsAuthenticated"/> then says whether there were any. A new account is created
/// the same way with a fresh id, after which <see cref="ConnectAsync"/> signs in and persists
/// credentials under <see cref="CredentialKey"/>. Editing an account calls
/// <see cref="ConnectAsync"/> again on a provider for the same id, which overwrites them.</para>
///
/// <para><b>Concurrency.</b> Panels, transfers and indexing call one provider instance
/// concurrently. Implementations must be thread-safe — in particular token refresh must be
/// single-flight (concurrent callers await the same refresh), because providers such as
/// Dropbox rotate refresh tokens and a parallel refresh invalidates the other.</para>
/// </summary>
public abstract class CloudProvider : FileProvider, IDisposable
{
    protected CloudProvider(Guid accountId, ICredentialStore credentials)
    {
        AccountId = accountId;
        Credentials = credentials;
    }

    public Guid AccountId { get; }

    public abstract CloudProviderType ProviderType { get; }

    protected ICredentialStore Credentials { get; }

    /// <summary>The macOS keychain key prefix, e.g. "dropbox" → "dropbox.&lt;uuid&gt;".</summary>
    protected abstract string CredentialKeyPrefix { get; }

    protected string CredentialKey => $"{CredentialKeyPrefix}.{AccountId}";

    /// <summary>Stored credentials were found (or <see cref="ConnectAsync"/> succeeded).</summary>
    public abstract bool IsAuthenticated { get; }

    // MARK: - Connecting

    /// <summary>The Add/Edit Account form.</summary>
    public abstract ConnectSpec ConnectSpec { get; }

    /// <summary>
    /// Signs in with what the user entered, persists the credentials under
    /// <see cref="CredentialKey"/>, and returns the label the account is shown with. Throws
    /// <see cref="CloudProviderException"/> with a message fit for the form on failure, and
    /// must leave previously stored credentials untouched when it fails.
    /// </summary>
    public abstract Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default);

    /// <summary>Non-secret values to prefill the Edit Account form with (server URL, user name). Never passwords or tokens.</summary>
    public virtual IReadOnlyDictionary<string, string> GetEditableConnectValues() => new Dictionary<string, string>();

    /// <summary>Forgets the stored credentials.</summary>
    public virtual Task DisconnectAsync()
    {
        Credentials.Delete(CredentialKey);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Re-reads the account's user-facing name after launch (Dropbox heals a stuck "Unknown").
    /// Null when nothing changed or the lookup failed — a failure must never replace a good name.
    /// </summary>
    public virtual Task<string?> RefreshAccountLabelAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    // MARK: - Limits and quota

    /// <summary>Documented per-file upload cap, checked before uploading; null when there is none.</summary>
    public virtual long? MaxUploadFileSize => null;

    /// <summary>Account-wide usage; null when the provider has no quota API. Do not cache here.</summary>
    public virtual Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<CloudStorageQuota?>(null);

    public override async Task<long?> AvailableSpaceAsync(string path, CancellationToken cancellationToken = default)
        => (await StorageQuotaAsync(cancellationToken).ConfigureAwait(false))?.FreeBytes;

    // MARK: - Share links

    public virtual ShareLinkCapabilities ShareLinkCapabilities => ShareLinkCapabilities.Unsupported;

    /// <summary>Creates a public link. Surface the server's own message on failure (disabled sharing, paid-plan options).</summary>
    public virtual Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    /// <summary>The link the file already has, or null. Only when <see cref="ShareLinkCapabilities.CanQueryExisting"/>.</summary>
    public virtual Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    /// <summary>Changes password/expiry/download permission in place, keeping the URL where possible.</summary>
    public virtual Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    /// <summary>Withdraws the public link.</summary>
    public virtual Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
