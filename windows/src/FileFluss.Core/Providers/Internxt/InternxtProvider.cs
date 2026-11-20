// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.Internxt;

/// <summary>
/// Internxt (internxt.com). End-to-end encrypted: login decrypts the account mnemonic locally,
/// and file content is encrypted/decrypted client-side through the Network service with keys
/// derived from it. No browser OAuth.
/// </summary>
[CloudProvider(CloudProviderType.Internxt)]
public sealed class InternxtProvider : CloudProvider
{
    private static readonly ConnectSpec Spec = new(
        new ConnectField("email", ConnectFieldKind.Email, "Email"),
        new ConnectField("password", ConnectFieldKind.Password, "Password"),
        new ConnectField("code", ConnectFieldKind.OneTimeCode, "Two-factor code (only if enabled)") { Required = false },
        ConnectField.Info("Internxt is end-to-end encrypted — there's no browser OAuth. Your password is used locally to unlock your account mnemonic and derive per-file keys, then discarded."));

    private readonly HttpClient _http;
    private volatile InternxtApiClient? _client;

    public InternxtProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal InternxtProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _http = handler is null ? CloudHttp.Create() : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        if (Credentials.Load<InternxtCredentials>(CredentialKey) is { Token.Length: > 0, Mnemonic.Length: > 0, RootFolderUuid.Length: > 0 } stored)
        {
            _client = new InternxtApiClient(_http, stored);
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.Internxt;

    protected override string CredentialKeyPrefix => "internxt";

    public override bool IsAuthenticated => _client is not null;

    public override ConnectSpec ConnectSpec => Spec;

    internal InternxtApiClient? Client => _client;

    private InternxtApiClient Api => _client ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - Connecting

    /// <summary>
    /// Signs in; when the account has 2FA and no (or a wrong) code was entered, the code is
    /// asked for without retyping the password. The account is labelled with the email the
    /// server reports.
    /// </summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var email = values.GetTrimmed("email");
        var password = values.Get("password");
        if (email.Length == 0 || password.Length == 0)
        {
            throw CloudProviderException.InvalidCredentials();
        }

        var code = values.GetOptional("code");
        InternxtCredentials credentials;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                credentials = await InternxtApiClient.LoginAsync(_http, email, password, code, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.TwoFactorRequired && attempt < 5)
            {
                var entered = await prompt.RequestCodeAsync(
                    CloudProviderType.Internxt.DisplayName(),
                    Localization.L("Two-factor authentication required. Enter your 6-digit code and try again."),
                    cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(entered))
                {
                    throw;
                }

                code = entered.Trim();
            }
        }

        Credentials.Save(CredentialKey, credentials);
        _client = new InternxtApiClient(_http, credentials);
        return new ConnectResult(credentials.Email);
    }

    public override IReadOnlyDictionary<string, string> GetEditableConnectValues()
        => _client is { } client
            ? new Dictionary<string, string> { ["email"] = client.Credentials.Email }
            : new Dictionary<string, string>();

    public override Task DisconnectAsync()
    {
        _client = null;
        return base.DisconnectAsync();
    }

    // MARK: - File operations

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Api.ListFolderAsync(CloudPath.Normalize(path), cancellationToken);

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Api.DownloadAsync(CloudPath.Normalize(remotePath), localPath, onBytes, cancellationToken);

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Api.UploadAsync(localPath, CloudPath.Normalize(remotePath), onBytes, cancellationToken);

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Api.DeleteAsync(CloudPath.Normalize(path), cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Api.CreateFolderAsync(CloudPath.Normalize(path), cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Api.RenameAsync(CloudPath.Normalize(path), newName, cancellationToken);

    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => Api.MoveAsync(CloudPath.Normalize(path), CloudPath.Normalize(newPath), cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = CloudPath.Normalize(path);
        return CloudPath.IsRoot(normalized)
            ? Task.FromResult(new CloudFileItem("d" + Api.Credentials.RootFolderUuid, string.Empty, "/", true, 0, CloudFileItem.UnknownDate))
            : Api.FindAsync(normalized, cancellationToken);
    }

    public override Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
        => _client is { } client ? client.StorageQuotaAsync(cancellationToken) : Task.FromResult<CloudStorageQuota?>(null);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
        }

        base.Dispose(disposing);
    }
}
