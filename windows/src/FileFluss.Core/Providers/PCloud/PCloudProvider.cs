// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.PCloud;

/// <summary>
/// pCloud (US and EU regions) through its JSON API — the macOS <c>PCloudProvider</c>.
/// Signs in with email + password (digest login) or, because pCloud now withholds tokens
/// from third-party apps, with the <c>pcauth</c> cookie value pasted from a browser.
/// </summary>
[CloudProvider(CloudProviderType.PCloud)]
public sealed class PCloudProvider : CloudProvider
{
    internal const string PasswordMode = "password";
    internal const string TokenMode = "token";
    internal const string EmailField = "email";
    internal const string PasswordField = "password";
    internal const string TokenField = "token";

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

    public override CloudProviderType ProviderType => CloudProviderType.PCloud;

    protected override string CredentialKeyPrefix => "pcloud";

    public override bool IsAuthenticated => _client is not null;

    // MARK: - Connecting

    /// <summary>
    /// The macOS form shows both ways at once and uses the token when one was pasted; here
    /// they are two modes. Email + password comes first as on macOS, with the warning that
    /// most accounts will need the token.
    /// </summary>
    public override ConnectSpec ConnectSpec { get; } = new(
    [
        new ConnectMode(PasswordMode, "Email & Password",
        [
            ConnectField.Info("pCloud no longer issues auth tokens via password login for third-party apps. You can still try email + password below, but most accounts will need to paste an access token."),
            new ConnectField(EmailField, ConnectFieldKind.Email, "Email"),
            new ConnectField(PasswordField, ConnectFieldKind.Password, "Password"),
        ]),
        new ConnectMode(TokenMode, "Access Token",
        [
            ConnectField.Info("How to get your token:"),
            ConnectField.Info("1. Sign in at my.pcloud.com in your browser"),
            // macOS says ⌥⌘I; every Windows browser opens DevTools with F12. No Safari here.
            ConnectField.Info("2. Open DevTools (F12 in Chrome/Edge/Firefox)"),
            ConnectField.Info("3. Chrome/Edge: Application tab → Storage → Cookies → https://my.pcloud.com"),
            ConnectField.Info("   Firefox: Storage tab → Cookies → https://my.pcloud.com"),
            ConnectField.Info("4. Copy the Value of the cookie named \"pcauth\" and paste it below"),
            ConnectField.Link("Open my.pcloud.com", "https://my.pcloud.com"),
            new ConnectField(TokenField, ConnectFieldKind.Password, "Access Token (pcauth cookie value)"),
        ]),
    ]);

    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var token = values.GetTrimmed(TokenField);
        var useToken = values.Mode == TokenMode || (values.Mode != PasswordMode && token.Length > 0);

        PCloudCredentials credentials;
        if (useToken)
        {
            if (token.Length == 0)
            {
                throw CloudProviderException.InvalidCredentials();
            }

            credentials = await PCloudApiClient.ValidateTokenAsync(_http, token, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            credentials = await PCloudApiClient.LoginWithPasswordAsync(
                _http,
                values.GetTrimmed(EmailField),
                values.Get(PasswordField),
                cancellationToken).ConfigureAwait(false);
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
