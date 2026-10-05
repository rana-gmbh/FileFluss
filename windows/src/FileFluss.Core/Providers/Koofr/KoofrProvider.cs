// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.Koofr;

/// <summary>
/// Koofr, signed in with the account email and an app password. As on macOS there is no
/// server-side move/copy, search or modification-date call here; those fall back to the
/// generic paths.
/// </summary>
[CloudProvider(CloudProviderType.Koofr)]
public sealed class KoofrProvider : CloudProvider
{
    internal const string EmailKey = "email";
    internal const string AppPasswordKey = "appPassword";

    private readonly HttpClient _http;
    private volatile KoofrApiClient? _client;

    public KoofrProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal KoofrProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _http = handler is null
            ? CloudHttp.Create()
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        if (credentials.Load<KoofrCredentials>(CredentialKey) is { } stored)
        {
            _client = new KoofrApiClient(_http, stored);
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.Koofr;

    protected override string CredentialKeyPrefix => "koofr";

    public override bool IsAuthenticated => _client is not null;

    private KoofrApiClient Require() => _client ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - Connecting

    public override ConnectSpec ConnectSpec => IsAuthenticated
        ? new ConnectSpec(
            new ConnectField(EmailKey, ConnectFieldKind.Email, "Email"),
            new ConnectField(AppPasswordKey, ConnectFieldKind.Password, "App Password"),
            ConnectField.Info("Password is not stored locally and is re-entered each time you edit."))
        : new ConnectSpec(
            ConnectField.Info("Create an app password at koofr.net → Preferences → Password, then enter your credentials below."),
            new ConnectField(EmailKey, ConnectFieldKind.Email, "Email"),
            new ConnectField(AppPasswordKey, ConnectFieldKind.Password, "App Password"));

    public override IReadOnlyDictionary<string, string> GetEditableConnectValues()
        => _client?.Credentials is { } c
            ? new Dictionary<string, string> { [EmailKey] = c.Email }
            : new Dictionary<string, string>();

    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var credentials = await KoofrApiClient.AuthenticateAsync(_http, values.GetTrimmed(EmailKey), values.Get(AppPasswordKey), cancellationToken).ConfigureAwait(false);
        Credentials.Save(CredentialKey, credentials);
        _client = new KoofrApiClient(_http, credentials);
        SupportLogger.Shared.Log($"Authenticated as {credentials.DisplayName}", KoofrApiClient.LogCategory);
        return new ConnectResult(credentials.DisplayName);
    }

    public override Task DisconnectAsync()
    {
        _client = null;
        return base.DisconnectAsync();
    }

    // MARK: - File operations

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Require().ListFolderAsync(path, cancellationToken);

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Require().DownloadFileAsync(remotePath, localPath, onBytes, cancellationToken);

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var target = CloudPath.Normalize(remotePath);
        return Require().UploadFileAsync(localPath, CloudPath.Parent(target), CloudPath.Name(target), onBytes, cancellationToken);
    }

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Require().DeleteItemAsync(path, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Require().CreateFolderAsync(path, cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Require().RenameItemAsync(path, newName, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => Require().GetFileInfoAsync(path, cancellationToken);

    // FolderSizeAsync: macOS walks the tree with listings — the base implementation.

    public override Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
        => _client is { } client ? client.StorageQuotaAsync(cancellationToken) : Task.FromResult<CloudStorageQuota?>(null);

    // MARK: - Share links

    /// <summary>
    /// Koofr's link API takes a path and nothing else: no password, no expiry, no view-only
    /// mode. Links can be created, listed back and deleted; there's no verified way to change
    /// a live link, so <see cref="ShareLinkCapabilities.CanUpdate"/> stays false.
    /// </summary>
    public override ShareLinkCapabilities ShareLinkCapabilities { get; } = new()
    {
        CanCreate = true,
        CanQueryExisting = true,
        CanUpdate = false,
        CanRemove = true,
    };

    public override Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => Require().CreateShareLinkAsync(CloudPath.Normalize(path), cancellationToken);

    public override Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => Require().ExistingShareLinkAsync(CloudPath.Normalize(path), cancellationToken);

    public override Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => Require().RemoveShareLinkAsync(CloudPath.Normalize(path), cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
        }

        base.Dispose(disposing);
    }
}
