// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.Dropbox;

/// <summary>
/// Dropbox (port of the macOS <c>DropboxProvider</c>): browser OAuth with PKCE and a loopback
/// redirect, path-based API, server-side move/copy, search, the full share-link set, quota.
/// </summary>
[CloudProvider(CloudProviderType.Dropbox)]
public sealed class DropboxProvider : CloudProvider
{
    private readonly HttpClient _http;
    private volatile DropboxApiClient? _client;

    public DropboxProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    /// <summary>Test seam: tests pass a FakeHttpHandler; null → <see cref="CloudHttp.Create"/>.</summary>
    internal DropboxProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _http = handler is null ? CloudHttp.Create() : CreateClient(handler);

        // Restore only; the first API call refreshes the access token when it has expired.
        if (Credentials.Load<DropboxCredentials>(CredentialKey) is { } stored)
        {
            _client = NewClient(stored);
        }
    }

    /// <summary>The browser round trip. Tests replace it so no listener or browser is involved.</summary>
    internal Func<Func<string, Uri>, CancellationToken, Task<OAuthCallback>> Authenticator { get; set; }
        = (authorizeUrl, cancellationToken) => OAuthLoopback.AuthenticateAsync(authorizeUrl, cancellationToken);

    public override CloudProviderType ProviderType => CloudProviderType.Dropbox;

    /// <summary>Names are case-insensitive here ("a.txt" and "A.txt" are one file).</summary>
    public override bool IsCaseSensitive => false;

    protected override string CredentialKeyPrefix => "dropbox";

    public override bool IsAuthenticated => _client is not null;

    public override ConnectSpec ConnectSpec { get; } = ConnectSpec.Browser(
        ConnectField.Info("Click Connect to sign in with your Dropbox account. Your browser will open for authentication."));

    internal DropboxApiClient? ApiClient => _client;

    private DropboxApiClient Client => _client ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - Connecting

    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        prompt.ShowStatus(Localization.L("Waiting for sign-in in browser…"));
        try
        {
            var verifier = Pkce.CreateVerifier();
            var challenge = Pkce.Challenge(verifier);
            var state = Pkce.CreateState();
            var callback = await Authenticator(
                redirectUri => DropboxApiClient.BuildAuthorizeUri(redirectUri, challenge, state),
                cancellationToken).ConfigureAwait(false);
            var code = callback.RequireCode(state);
            var credentials = await DropboxApiClient.ExchangeCodeAsync(_http, code, verifier, callback.RedirectUri, cancellationToken).ConfigureAwait(false);

            // Only now, with a working token in hand, is anything stored replaced.
            Credentials.Save(CredentialKey, credentials);
            _client = NewClient(credentials);
            SupportLogger.Shared.Log($"[Dropbox] Authenticated as {credentials.DisplayName}", "Dropbox");
            return new ConnectResult(credentials.DisplayName);
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

    /// <summary>
    /// Re-fetches the live account name and persists it. Heals accounts linked before the
    /// get_current_account Content-Type fix, whose name is stuck at "Unknown". A failed lookup
    /// returns null and never replaces a good name.
    /// </summary>
    public override async Task<string?> RefreshAccountLabelAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not { } client)
        {
            return null;
        }

        try
        {
            var updated = await client.RefreshDisplayNameAsync(cancellationToken).ConfigureAwait(false);
            return updated?.DisplayName;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SupportLogger.Shared.Log($"[Dropbox] Account name refresh failed: {e.Message}", "Dropbox", LogLevel.Notice);
            return null;
        }
    }

    // MARK: - File operations

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Client.ListFolderAsync(path, cancellationToken);

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Client.DownloadFileAsync(remotePath, localPath, onBytes, cancellationToken);

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Client.UploadFileAsync(localPath, remotePath, onBytes, cancellationToken);

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Client.DeleteItemAsync(path, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Client.CreateFolderAsync(path, cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Client.RenameItemAsync(path, newName, cancellationToken);

    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => Client.MoveItemAsync(path, newPath, cancellationToken);

    public override Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => Client.CopyItemAsync(path, newPath, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => Client.GetMetadataAsync(path, cancellationToken);

    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
        => await Client.SearchAsync(query, path, cancellationToken).ConfigureAwait(false);

    // FolderSizeAsync: macOS walks list_folder recursively — exactly the inherited default.

    // MARK: - Quota

    public override Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
        => _client is { } client ? client.StorageQuotaAsync(cancellationToken) : Task.FromResult<CloudStorageQuota?>(null);

    // MARK: - Share links

    /// <summary>
    /// The full set. Password and expiry are Professional/Business features though, so a free
    /// account's request for either comes back as an error — creation can fail even where
    /// these say yes.
    /// </summary>
    public override ShareLinkCapabilities ShareLinkCapabilities { get; } = new()
    {
        CanCreate = true,
        CanQueryExisting = true,
        CanUpdate = true,
        CanRemove = true,
        SupportsPassword = true,
        SupportsExpiry = true,
        SupportsDownloadToggle = true,
        PasswordRequiresPaidPlan = true,
        ExpiryRequiresPaidPlan = true,
    };

    public override Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => Client.CreateShareLinkAsync(path, options, cancellationToken);

    public override Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => Client.ExistingShareLinkAsync(path, cancellationToken);

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

    /// <summary>
    /// Every refresh and rename reaches the credential store at once — but only while the
    /// client is still this provider's: a refresh that lands after Disconnect or a re-sign-in
    /// must not resurrect or overwrite what replaced it.
    /// </summary>
    private DropboxApiClient NewClient(DropboxCredentials credentials)
    {
        DropboxApiClient? client = null;
        client = new DropboxApiClient(_http, credentials, updated =>
        {
            if (ReferenceEquals(_client, client))
            {
                Credentials.Save(CredentialKey, updated);
            }
        });
        return client;
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CloudHttp.UserAgent);
        return client;
    }
}
