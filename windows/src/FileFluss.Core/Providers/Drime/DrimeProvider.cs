// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.Drime;

/// <summary>
/// Drime, connected with a personal access token (port of the macOS <c>DrimeProvider</c>):
/// browse, download, upload (S3 multipart in parallel parts from 5 MB), server-side move and
/// copy, rename, new folders, delete to Drime's trash, modification dates, storage quota and
/// share links with password, expiry and download switch.
///
/// <para>Browser sign-in (OAuth with PKCE) exists on macOS but stays off until FileFluss has a
/// Drime client ID (<see cref="DrimeApiClient.IsOAuthAvailable"/>); the token refresh it would
/// need is ported, so stored OAuth credentials keep working once it is switched on.</para>
/// </summary>
[CloudProvider(CloudProviderType.Drime)]
public sealed class DrimeProvider : CloudProvider
{
    internal const string TokenKey = "token";

    private readonly HttpClient _http;
    private volatile DrimeApiClient? _client;

    public DrimeProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal DrimeProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _http = handler is null
            ? CloudHttp.Create()
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        if (credentials.Load<DrimeCredentials>(CredentialKey) is { AccessToken.Length: > 0 } stored)
        {
            _client = CreateClient(stored);
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.Drime;

    protected override string CredentialKeyPrefix => "drime";

    public override bool IsAuthenticated => _client is not null;

    internal DrimeApiClient? Client => _client;

    private DrimeApiClient Require() => _client ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - Connecting

    /// <summary>
    /// Add: the macOS token form (browser sign-in is not offered while there is no client ID).
    /// Edit: macOS offers nothing to change — a different token or account means removing
    /// the account and adding it again — so the form is that hint alone, and saving it keeps
    /// the stored token.
    /// </summary>
    public override ConnectSpec ConnectSpec => IsAuthenticated
        ? new ConnectSpec(ConnectField.Info("To connect Drime with a different token or account, remove this account and add it again."))
        : new ConnectSpec(
            ConnectField.Info("Create a personal access token in your Drime account under Settings → Developers, then paste it below."),
            ConnectField.Link("Open Drime token settings", DrimeApiClient.TokenSettingsUrl),
            new ConnectField(TokenKey, ConnectFieldKind.Password, "Personal Access Token"),
            ConnectField.Info("Give the token the “All” permission, or write access to Files, Folders and Sharing. It is stored in your keychain."));

    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var token = values.GetTrimmed(TokenKey);
        if (token.Length == 0 && _client is { } existing)
        {
            // The edit form has no token field: saving it changes nothing.
            return new ConnectResult(Label(existing.Credentials));
        }

        var credentials = await DrimeApiClient.ValidatePersonalTokenAsync(_http, token, cancellationToken).ConfigureAwait(false);
        Credentials.Save(CredentialKey, credentials);
        _client = CreateClient(credentials);
        SupportLogger.Shared.Log("[Drime] Connected (personalToken)", DrimeApiClient.LogCategory);
        return new ConnectResult(Label(credentials));
    }

    /// <summary>The account is named "Drime (email)" — the display name when there is no email, as on macOS.</summary>
    private static string? Label(DrimeCredentials credentials)
    {
        var name = credentials.Email.Length == 0 ? credentials.DisplayName : credentials.Email;
        return name.Length == 0 ? null : name;
    }

    public override async Task DisconnectAsync()
    {
        if (_client is { } client)
        {
            await client.RevokeAsync().ConfigureAwait(false);
        }

        _client = null;
        await base.DisconnectAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Drime rotates the refresh token on every refresh and revokes the whole grant if an old
    /// one is ever presented again, so each new one is stored the moment it exists.
    /// </summary>
    private DrimeApiClient CreateClient(DrimeCredentials credentials)
        => new(_http, credentials, updated => Credentials.Save(CredentialKey, updated));

    // MARK: - File operations

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Require().ListFolderAsync(path, cancellationToken);

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Require().DownloadFileAsync(remotePath, localPath, onBytes, cancellationToken);

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Require().UploadFileAsync(localPath, remotePath, onBytes, cancellationToken);

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Require().DeleteItemAsync(path, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Require().CreateFolderAsync(path, cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Require().RenameItemAsync(path, newName, cancellationToken);

    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => Require().MoveItemAsync(path, newPath, cancellationToken);

    public override Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => Require().CopyItemAsync(path, newPath, cancellationToken);

    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => Require().SetModificationDateAsync(path, date, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => Require().GetFileMetadataAsync(path, cancellationToken);

    // FolderSizeAsync: folder entries carry no reliable aggregate size, so macOS adds up the
    // listings — the base implementation.

    public override Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
        => _client is { } client ? client.StorageQuotaAsync(cancellationToken) : Task.FromResult<CloudStorageQuota?>(null);

    // MARK: - Share links

    /// <summary>Drime's links take a password, an expiry and a download switch, can be read back, edited in place and deleted.</summary>
    public override ShareLinkCapabilities ShareLinkCapabilities { get; } = new()
    {
        CanCreate = true,
        CanQueryExisting = true,
        CanUpdate = true,
        CanRemove = true,
        SupportsPassword = true,
        SupportsExpiry = true,
        SupportsDownloadToggle = true,
    };

    public override Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => Require().CreateShareLinkAsync(path, options, cancellationToken);

    public override Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => Require().ExistingShareLinkAsync(path, cancellationToken);

    public override Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => Require().UpdateShareLinkAsync(path, options, cancellationToken);

    public override Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => Require().RemoveShareLinkAsync(path, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
        }

        base.Dispose(disposing);
    }
}
