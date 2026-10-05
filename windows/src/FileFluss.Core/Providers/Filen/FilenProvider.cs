// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.Filen;

/// <summary>
/// What a Filen sign-in leaves behind (the macOS <c>FilenCredentials</c>). Email and password
/// are exchanged once for an API key and the master keys; the password itself is never stored.
/// </summary>
/// <param name="ApiKey">Bearer token for every gateway call.</param>
/// <param name="MasterKeys">64-char hex master keys, freshest first. A new one appears with every
/// password change; all are kept because older items may still be encrypted with an older one.</param>
/// <param name="RootFolderUuid">The "Cloud Drive" root from <c>/v3/user/baseFolder</c>; listings start here.</param>
internal sealed record FilenCredentials(string Email, string ApiKey, string[] MasterKeys, string RootFolderUuid);

/// <summary>
/// Filen (filen.io). End-to-end encrypted, auth version 2 accounts: the password derives the
/// master key locally (no browser OAuth); names and contents are AES-256-GCM encrypted
/// client-side.
/// </summary>
[CloudProvider(CloudProviderType.Filen)]
public sealed class FilenProvider : CloudProvider
{
    private static readonly ConnectSpec Spec = new(
        new ConnectField("email", ConnectFieldKind.Email, "Email"),
        new ConnectField("password", ConnectFieldKind.Password, "Password"),
        new ConnectField("code", ConnectFieldKind.OneTimeCode, "Two-factor code (only if enabled)") { Required = false },
        ConnectField.Info("Filen is end-to-end encrypted, so there's no browser OAuth — the password is used locally to derive your master key, then discarded. Only v2 accounts are supported in this release; new Filen accounts default to v2."));

    private readonly HttpClient _http;
    private volatile FilenApiClient? _client;

    public FilenProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal FilenProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _http = handler is null ? CloudHttp.Create() : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        if (Credentials.Load<FilenCredentials>(CredentialKey) is { ApiKey.Length: > 0, MasterKeys.Length: > 0, RootFolderUuid.Length: > 0 } stored)
        {
            _client = new FilenApiClient(_http, stored);
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.Filen;

    protected override string CredentialKeyPrefix => "filen";

    public override bool IsAuthenticated => _client is not null;

    public override ConnectSpec ConnectSpec => Spec;

    private FilenApiClient Api => _client ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - Connecting

    /// <summary>
    /// Runs the v2 PBKDF2 login. When the account has 2FA and no (or a wrong) code was entered,
    /// the code is asked for without retyping the password.
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
        FilenCredentials credentials;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                credentials = await FilenApiClient.LoginAsync(_http, email, password, code, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.TwoFactorRequired && attempt < 5)
            {
                var entered = await prompt.RequestCodeAsync(
                    CloudProviderType.Filen.DisplayName(),
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
        _client = new FilenApiClient(_http, credentials);
        SupportLogger.Shared.Log($"[Filen] Authenticated as {credentials.Email}", "Filen");
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

    /// <summary>Moves the item to Filen's trash, as macOS does.</summary>
    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Api.TrashAsync(CloudPath.Normalize(path), cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Api.CreateFolderAsync(CloudPath.Normalize(path), cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Api.RenameAsync(CloudPath.Normalize(path), newName, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = CloudPath.Normalize(path);
        return CloudPath.IsRoot(normalized)
            ? Task.FromResult(new CloudFileItem(Api.Credentials.RootFolderUuid, string.Empty, "/", true, 0, CloudFileItem.UnknownDate))
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
