// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.OneDrive;

/// <summary>
/// OneDrive (personal and business) through Microsoft Graph, signed in with Microsoft's
/// loopback OAuth + PKCE flow — the macOS <c>OneDriveProvider</c>.
///
/// <para>Like macOS, no server-side move or copy: Graph's copy is an asynchronous job the
/// Mac app never wired up, so callers fall back to download + upload.</para>
/// </summary>
[CloudProvider(CloudProviderType.OneDrive)]
public sealed class OneDriveProvider : CloudProvider
{
    private readonly HttpClient _http;
    private volatile OneDriveApiClient? _client;

    public OneDriveProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal OneDriveProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _http = handler is null ? CloudHttp.Create() : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        if (Credentials.Load<OneDriveCredentials>(CredentialKey) is { } stored)
        {
            _client = CreateClient(stored);
        }
    }

    /// <summary>
    /// Runs the browser half of sign-in: opens the authorize URL (built from the redirect
    /// URI) and returns the callback. Tests replace it; the default is the loopback listener.
    /// </summary>
    internal Func<Func<string, Uri>, CancellationToken, Task<OAuthCallback>> BrowserSignIn { get; set; }
        = (authorizeUrl, cancellationToken) => OAuthLoopback.AuthenticateAsync(authorizeUrl, cancellationToken);

    public override CloudProviderType ProviderType => CloudProviderType.OneDrive;

    protected override string CredentialKeyPrefix => "onedrive";

    public override bool IsAuthenticated => _client is not null;

    /// <summary>The account label: the display name (or mail / UPN) Graph's /me returned at sign-in.</summary>
    public string? UserEmail => _client?.Credentials.UserEmail;

    // MARK: - Connecting

    public override ConnectSpec ConnectSpec { get; } = ConnectSpec.Browser(
        ConnectField.Info("Click Connect to sign in with your Microsoft account. Your browser will open for authentication."));

    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        prompt.ShowStatus(Localization.L("Waiting for sign-in in browser…"));
        try
        {
            var verifier = Pkce.CreateVerifier();
            var state = Pkce.CreateState();
            var callback = await BrowserSignIn(
                redirectUri => OneDriveApiClient.AuthorizeUrl(redirectUri, Pkce.Challenge(verifier), state),
                cancellationToken).ConfigureAwait(false);
            var code = callback.RequireCode(state);
            var credentials = await OneDriveApiClient.ExchangeCodeAsync(_http, code, verifier, callback.RedirectUri, cancellationToken).ConfigureAwait(false);

            // Only now, with tokens in hand, is the previous sign-in replaced.
            Credentials.Save(CredentialKey, credentials);
            _client = CreateClient(credentials);
            return new ConnectResult(credentials.UserEmail);
        }
        finally
        {
            prompt.ShowStatus(null);
        }
    }

    public override async Task DisconnectAsync()
    {
        _client = null;
        await base.DisconnectAsync().ConfigureAwait(false);
    }

    // MARK: - File operations

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Client.ListFolderAsync(path, cancellationToken);

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Client.DownloadAsync(remotePath, localPath, onBytes, cancellationToken);

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Client.UploadAsync(localPath, remotePath, onBytes, cancellationToken);

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Client.DeleteAsync(path, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Client.CreateFolderAsync(path, cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Client.RenameAsync(path, newName, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => Client.GetMetadataAsync(path, cancellationToken);

    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => Client.SetModificationDateAsync(path, date, cancellationToken);

    public override Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
        => Client.FolderSizeAsync(path, cancellationToken);

    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
        => await Client.SearchAsync(query, path, cancellationToken).ConfigureAwait(false);

    public override async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
        => _client is { } client ? await client.StorageQuotaAsync(cancellationToken).ConfigureAwait(false) : null;

    // MARK: - Share links

    /// <summary>
    /// Graph's createLink takes a password and an expiry but has no download toggle. These
    /// are only the API's capabilities: an administrator can forbid anonymous links, and on
    /// consumer OneDrive password and expiry need Microsoft 365 — creation then fails with
    /// Graph's own message.
    /// </summary>
    public override ShareLinkCapabilities ShareLinkCapabilities { get; } = new()
    {
        CanCreate = true,
        CanQueryExisting = true,
        CanUpdate = true,
        CanRemove = true,
        SupportsPassword = true,
        SupportsExpiry = true,
        SupportsDownloadToggle = false,
        PasswordRequiresPaidPlan = true,
        ExpiryRequiresPaidPlan = true,
    };

    public override Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => Client.CreateShareLinkAsync(path, options, cancellationToken);

    public override Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => Client.ExistingShareLinkAsync(path, cancellationToken);

    /// <summary>Graph can't edit a link in place: the old permission is revoked and a new one created, so the URL changes (the note says so).</summary>
    public override Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => Client.UpdateShareLinkAsync(path, options, cancellationToken);

    public override Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => Client.RemoveShareLinkAsync(path, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
        }

        base.Dispose(disposing);
    }

    // MARK: - Private

    private OneDriveApiClient Client => _client ?? throw CloudProviderException.NotAuthenticated();

    private OneDriveApiClient CreateClient(OneDriveCredentials credentials)
        => new(_http, credentials, PersistRefreshed);

    /// <summary>
    /// Saves rotated tokens — unless the client that refreshed has since been replaced by a
    /// new sign-in, whose tokens must not be overwritten by the old session's.
    /// </summary>
    private void PersistRefreshed(OneDriveApiClient client, OneDriveCredentials credentials)
    {
        if (ReferenceEquals(client, _client))
        {
            Credentials.Save(CredentialKey, credentials);
        }
    }
}
