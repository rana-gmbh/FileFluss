// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.PCloud;

/// <summary>
/// pCloud (US and EU regions) through its JSON API — the macOS <c>PCloudProvider</c>.
/// Signs in through pCloud's own page in the browser (OAuth 2.0 code flow), like Box: pCloud
/// stopped issuing tokens to third-party apps through password login, which had left only
/// copying the <c>pcauth</c> cookie out of the browser. Accounts added either of those ways
/// keep working with the <c>auth</c> token they hold.
/// </summary>
[CloudProvider(CloudProviderType.PCloud)]
public sealed class PCloudProvider : CloudProvider
{
    private readonly HttpClient _http;
    private volatile PCloudApiClient? _client;

    public PCloudProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal PCloudProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _http = handler is null ? CloudHttp.Create() : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        if (Credentials.Load<PCloudCredentials>(CredentialKey) is { } stored)
        {
            _client = new PCloudApiClient(_http, stored);
        }
    }

    /// <summary>
    /// The browser round trip, on the first free one of the redirect ports registered with
    /// pCloud. Tests replace it so no listener or browser is involved.
    /// </summary>
    internal Func<Func<string, Uri>, CancellationToken, Task<OAuthCallback>> Authenticator { get; set; }
        = (authorizeUrl, cancellationToken) => OAuthLoopback.AuthenticateAsync(authorizeUrl, cancellationToken, fixedPorts: PCloudApiClient.RedirectPorts);

    public override CloudProviderType ProviderType => CloudProviderType.PCloud;

    protected override string CredentialKeyPrefix => "pcloud";

    public override bool IsAuthenticated => _client is not null;

    // MARK: - Connecting

    /// <summary>Add: Connect opens the browser. Edit (stored credentials): Re-authorize, as on macOS.</summary>
    public override ConnectSpec ConnectSpec => IsAuthenticated
        ? ConnectSpec.Browser(
            ConnectField.Info("Click Re-authorize to sign in again. Your browser will open and the existing token is replaced once you complete the flow."))
        : ConnectSpec.Browser(
            ConnectField.Info("Click Connect to sign in with your pCloud account. Your browser will open for authentication."));

    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        prompt.ShowStatus(Localization.L("Waiting for sign-in in browser…"));
        PCloudCredentials credentials;
        try
        {
            // CSRF defence: the authorization server must round-trip the same state.
            var state = Pkce.CreateState();
            var callback = await Authenticator(
                redirectUri => PCloudApiClient.BuildAuthorizeUri(redirectUri, state),
                cancellationToken).ConfigureAwait(false);
            var code = callback.RequireCode(state);
            if (code.Length == 0)
            {
                throw CloudProviderException.Unauthorized();
            }

            // The redirect also names the API host of the account's region.
            credentials = await PCloudApiClient.ExchangeCodeAsync(_http, code, callback.Get("hostname"), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            prompt.ShowStatus(null);
        }

        // Only now, with a working token, is the previous sign-in replaced.
        Credentials.Save(CredentialKey, credentials);
        var client = new PCloudApiClient(_http, credentials);
        _client = client;

        // The label is the account's email, looked up separately as on macOS; a failed
        // lookup leaves the account named just "pCloud".
        string? email = null;
        try
        {
            email = (await client.UserInfoAsync(cancellationToken).ConfigureAwait(false)).Email;
        }
        catch (CloudProviderException)
        {
        }

        return new ConnectResult(string.IsNullOrEmpty(email) ? null : email);
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
        => Client.UploadAsync(localPath, CloudPath.Parent(remotePath), CloudPath.Name(remotePath), onBytes, cancellationToken);

    /// <summary>Tries a file first; pCloud's "file not found" means it may be a folder.</summary>
    public override async Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
    {
        var client = Client;
        try
        {
            await client.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            await client.DeleteFolderAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Client.CreateFolderAsync(path, cancellationToken);

    public override async Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
    {
        var client = Client;
        var target = CloudPath.Combine(CloudPath.Parent(path), newName);
        var metadata = await client.StatAsync(path, cancellationToken).ConfigureAwait(false);
        await (metadata.IsDirectory
            ? client.RenameFolderAsync(path, target, cancellationToken)
            : client.RenameFileAsync(path, target, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>renamefile/renamefolder with a full destination path are pCloud's server-side move.</summary>
    public override async Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        var client = Client;
        var metadata = await client.StatAsync(path, cancellationToken).ConfigureAwait(false);
        await (metadata.IsDirectory
            ? client.RenameFolderAsync(path, newPath, cancellationToken)
            : client.RenameFileAsync(path, newPath, cancellationToken)).ConfigureAwait(false);
    }

    public override async Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        var client = Client;
        var metadata = await client.StatAsync(path, cancellationToken).ConfigureAwait(false);
        await (metadata.IsDirectory
            ? client.CopyFolderAsync(path, newPath, cancellationToken)
            : client.CopyFileAsync(path, newPath, cancellationToken)).ConfigureAwait(false);
    }

    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => Client.SetModificationDateAsync(path, date, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => Client.StatAsync(path, cancellationToken);

    public override Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
        => Client.FolderSizeAsync(path, cancellationToken);

    /// <summary>From userinfo; a quota of 0 means pCloud reported no upper bound.</summary>
    public override async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not { } client)
        {
            return null;
        }

        var info = await client.UserInfoAsync(cancellationToken).ConfigureAwait(false);
        return new CloudStorageQuota(info.UsedQuota, info.Quota > 0 ? info.Quota : null);
    }

    // MARK: - Share links

    /// <summary>
    /// Public links work on any plan, but linkpassword and expire are Premium-only — the
    /// server rejects them on a free account and its message is shown verbatim. There is no
    /// view-without-download switch. listpublinks finds an existing link, and
    /// changepublink/deletepublink edit and withdraw it in place, keeping its URL.
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

    private PCloudApiClient Client => _client ?? throw CloudProviderException.NotAuthenticated();
}
