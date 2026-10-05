// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.Mega;

/// <summary>
/// MEGA (mega.nz). End-to-end encrypted: the password derives a key locally that unlocks the
/// account's master key; names and contents are encrypted client-side. There is no OAuth —
/// the session id and master key are what gets stored.
/// </summary>
[CloudProvider(CloudProviderType.Mega)]
public sealed class MegaProvider : CloudProvider
{
    private static readonly ConnectSpec Spec = new(
        ConnectField.Info("Sign in with your Mega email and password."),
        new ConnectField("email", ConnectFieldKind.Email, "Email"),
        new ConnectField("password", ConnectFieldKind.Password, "Password"),
        new ConnectField("code", ConnectFieldKind.OneTimeCode, "Two-factor code (only if enabled)") { Required = false });

    private readonly HttpClient _http;
    private volatile MegaApiClient? _client;

    public MegaProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal MegaProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _http = handler is null ? CloudHttp.Create() : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        if (Credentials.Load<MegaCredentials>(CredentialKey) is { SessionId.Length: > 0, MasterKey.Length: 4 } stored)
        {
            _client = new MegaApiClient(_http, stored);
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.Mega;

    protected override string CredentialKeyPrefix => "mega";

    public override bool IsAuthenticated => _client is not null;

    public override ConnectSpec ConnectSpec => Spec;

    internal MegaApiClient? Client => _client;

    private MegaApiClient Api => _client ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - Connecting

    /// <summary>
    /// Signs in with email and password. When the account has 2FA and no code was entered (or
    /// it was wrong), MEGA answers EMFAREQUIRED and the code is asked for — the password is not
    /// typed again.
    /// </summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var email = values.GetTrimmed("email");
        var password = values.Get("password");
        if (email.Length == 0 || password.Length == 0)
        {
            throw CloudProviderException.InvalidCredentials();
        }

        MegaCredentials credentials;
        try
        {
            credentials = await WithTwoFactorPromptAsync(
                code => MegaApiClient.LoginAsync(_http, email, password, code, prompt.ShowStatus, cancellationToken),
                values.GetOptional("code"),
                prompt,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            prompt.ShowStatus(null);
        }

        Credentials.Save(CredentialKey, credentials);
        _client = new MegaApiClient(_http, credentials);
        SupportLogger.Shared.Log($"[Mega] Authenticated as {email}", "Mega");
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
    {
        var path = CloudPath.Normalize(remotePath);
        return Api.UploadAsync(localPath, CloudPath.Parent(path), CloudPath.Name(path), onBytes, cancellationToken);
    }

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Api.DeleteAsync(CloudPath.Normalize(path), cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Api.CreateFolderAsync(CloudPath.Normalize(path), cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Api.RenameAsync(CloudPath.Normalize(path), newName, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => Api.StatAsync(CloudPath.Normalize(path), cancellationToken);

    public override Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
        => Api.FolderSizeAsync(CloudPath.Normalize(path), cancellationToken);

    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
        => await Api.SearchAsync(query, path is null ? null : CloudPath.Normalize(path), cancellationToken).ConfigureAwait(false);

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

    /// <summary>Runs a sign-in, asking for a one-time code whenever the server says one is needed, until it succeeds or the user cancels.</summary>
    private static async Task<T> WithTwoFactorPromptAsync<T>(Func<string?, Task<T>> signIn, string? code, IConnectPrompt prompt, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await signIn(code).ConfigureAwait(false);
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.TwoFactorRequired && attempt < 5)
            {
                var entered = await prompt.RequestCodeAsync(
                    CloudProviderType.Mega.DisplayName(),
                    Localization.L("Two-factor authentication required. Enter your 6-digit code and try again."),
                    cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(entered))
                {
                    throw;
                }

                code = entered.Trim();
            }
        }
    }
}
