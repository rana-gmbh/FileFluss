// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.Qnap;

/// <summary>
/// A QNAP NAS through File Station, signed in to with the QTS account — including 2-step
/// verification — with share links (port of the macOS <c>QNAPProvider</c>). Most home units
/// use a self-signed certificate, which the user can allow for exactly the host they typed.
/// </summary>
[CloudProvider(CloudProviderType.Qnap)]
public sealed class QnapProvider : CloudProvider
{
    internal const string ServerUrlKey = "serverUrl";
    internal const string UsernameKey = "username";
    internal const string PasswordKey = "password";
    internal const string SecurityCodeKey = "securityCode";
    internal const string AllowSelfSignedKey = "allowSelfSigned";

    private readonly HttpMessageHandler? _handler;
    private readonly Lock _clientsGate = new();
    private readonly List<HttpClient> _httpClients = [];
    private QnapApiClient? _client;
    private TimeSpan? _pollInterval;

    public QnapProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal QnapProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _handler = handler;
        if (credentials.Load<QnapCredentials>(CredentialKey) is { } stored)
        {
            _client = CreateClient(stored, out _);
            AttachPersistence(_client);
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.Qnap;

    protected override string CredentialKeyPrefix => "qnap";

    public override bool IsAuthenticated => Volatile.Read(ref _client) is not null;

    /// <summary>
    /// File Station links take an access code and an expiry and can be looked up and deleted.
    /// Editing one in place needs settings this account can't read back reliably, so a change
    /// means a new link.
    /// </summary>
    public override ShareLinkCapabilities ShareLinkCapabilities { get; } = new()
    {
        CanCreate = true,
        CanQueryExisting = true,
        CanUpdate = false,
        CanRemove = true,
        SupportsPassword = true,
        SupportsExpiry = true,
    };

    /// <summary>Test seam: shortens the copy/move landing poll.</summary>
    internal TimeSpan? PollInterval
    {
        get => _pollInterval;
        set
        {
            _pollInterval = value;
            if (value is { } interval && Volatile.Read(ref _client) is { } client)
            {
                client.PollInterval = interval;
            }
        }
    }

    private QnapApiClient Client => Volatile.Read(ref _client) ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - Connecting

    public override ConnectSpec ConnectSpec => new(
        ConnectField.Info("Enter your NAS address and QTS account. HTTPS uses port 443 unless you add another one; plain HTTP uses 8080."),
        new ConnectField(ServerUrlKey, ConnectFieldKind.Url, "Server URL") { Placeholder = "Server URL (e.g. https://my-nas.local)" },
        new ConnectField(UsernameKey, ConnectFieldKind.Text, "Username"),
        new ConnectField(PasswordKey, ConnectFieldKind.Password, "Password"),
        new ConnectField(SecurityCodeKey, ConnectFieldKind.OneTimeCode, "2-step verification code (only if it's on)") { Required = false },
        new ConnectField(AllowSelfSignedKey, ConnectFieldKind.Checkbox, "Allow self-signed certificate")
        {
            Required = false,
            DefaultValue = "true",
            Help = "Most home NAS units use a self-signed certificate. Turn this off only if you've installed a real CA-signed cert (e.g. Let's Encrypt).",
        });

    /// <summary>
    /// Logs in to QTS. When the account turns out to need a 2-step code and none was typed,
    /// asks for one and tries again. Named "QNAP (user@nas-host)", as on macOS.
    /// </summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var serverUrl = QnapApiClient.NormalizeServerUrl(values.GetTrimmed(ServerUrlKey));
        var username = values.GetTrimmed(UsernameKey);
        var code = QnapApiClient.StripWhitespace(values.Get(SecurityCodeKey));

        // Tokens this account already has carry over to an edit of the same NAS and user, so
        // changing a setting doesn't demand a fresh code. They belong to that one account and
        // never go to another host. The qtoken stands in for the old password, so a password
        // change must not log in with it; the vtoken only skips the 2-step code and still
        // needs the right password.
        var previous = Credentials.Load<QnapCredentials>(CredentialKey) is { } stored
                       && stored.Username == username
                       && stored.ServerUrl == serverUrl
            ? stored
            : null;

        var credentials = new QnapCredentials(
            serverUrl,
            username,
            values.Get(PasswordKey),
            values.GetBool(AllowSelfSignedKey, fallback: true),
            $"{username}@{QnapApiClient.HostFrom(serverUrl)}",
            previous?.ClientId ?? Guid.NewGuid().ToString().ToUpperInvariant(),
            Qtoken: null,
            Vtoken: previous?.Vtoken);

        var client = CreateClient(credentials, out var http);
        try
        {
            try
            {
                await client.LoginAsync(code, allowToken: false, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.TwoFactorRequired && code.Length == 0)
            {
                var typed = await prompt.RequestCodeAsync(
                    CloudProviderType.Qnap.DisplayName(),
                    Localization.L("Two-factor authentication required. Enter your 6-digit code and try again."),
                    cancellationToken).ConfigureAwait(false);
                var second = QnapApiClient.StripWhitespace(typed);
                if (second.Length == 0)
                {
                    throw;
                }

                await client.LoginAsync(second, allowToken: false, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Stored credentials and the live client stay as they were.
            ReleaseHttpClient(http);
            throw;
        }

        // Keep the client that logged in: it holds the session, and the next login can't
        // repeat a 2-step code.
        var signedIn = client.Credentials;
        Credentials.Save(CredentialKey, signedIn);
        AttachPersistence(client);
        Volatile.Write(ref _client, client);
        SupportLogger.Shared.Log($"[QNAP] Authenticated as {signedIn.DisplayName}", QnapApiClient.Category);
        return new ConnectResult(signedIn.DisplayName);
    }

    /// <summary>Server, user and the certificate choice; the password is retyped, as on macOS.</summary>
    public override IReadOnlyDictionary<string, string> GetEditableConnectValues()
    {
        if (Volatile.Read(ref _client)?.Credentials is not { } stored)
        {
            return new Dictionary<string, string>();
        }

        return new Dictionary<string, string>
        {
            [ServerUrlKey] = stored.ServerUrl,
            [UsernameKey] = stored.Username,
            [AllowSelfSignedKey] = stored.AllowSelfSignedCertificate ? "true" : "false",
        };
    }

    public override async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _client, null) is { } client)
        {
            await client.LogoutAsync().ConfigureAwait(false);
        }

        await base.DisconnectAsync().ConfigureAwait(false);
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
        => CopyOrMoveAsync(path, newPath, move: true, cancellationToken);

    public override Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => CopyOrMoveAsync(path, newPath, move: false, cancellationToken);

    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => Client.SetModificationDateAsync(path, date, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => Client.GetFileInfoAsync(path, cancellationToken);

    public override Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
        => Client.FolderSizeAsync(path, cancellationToken);

    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
        => await Client.SearchFilesAsync(query, path, cancellationToken).ConfigureAwait(false);

    // MARK: - Share links

    public override Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => Client.CreateShareLinkAsync(path, options, cancellationToken);

    public override Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => Client.ExistingShareLinkAsync(path, cancellationToken);

    public override Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => Client.RemoveShareLinkAsync(path, cancellationToken);

    // MARK: - Private

    /// <summary>
    /// File Station copies and moves keep the source name, so a different destination name is
    /// a rename afterwards. They run with overwrite on (<c>mode=0</c>), so when the source
    /// name is already taken at the destination — or the copy stays in its own folder — a
    /// renamed copy ("Keep both", "a (2).txt") would first overwrite the very file the user
    /// chose to keep. Then the caller copies the bytes instead.
    /// </summary>
    private async Task CopyOrMoveAsync(string path, string newPath, bool move, CancellationToken cancellationToken)
    {
        var client = Client;
        var destinationFolder = QnapApiClient.Split(newPath).Parent;
        var destinationName = CloudPath.Name(newPath);
        var sourceName = CloudPath.Name(path);
        if (destinationName != sourceName && await LandingSpotTakenAsync(client, path, destinationFolder, sourceName, cancellationToken).ConfigureAwait(false))
        {
            throw CloudProviderException.NotImplemented();
        }

        await client.CopyMoveAsync(path, destinationFolder, move, cancellationToken).ConfigureAwait(false);
        if (destinationName != sourceName)
        {
            await client.RenameItemAsync(QnapApiClient.Join(destinationFolder, sourceName), destinationName, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> LandingSpotTakenAsync(QnapApiClient client, string path, string destinationFolder, string sourceName, CancellationToken cancellationToken)
    {
        if (string.Equals(QnapApiClient.Split(path).Parent, destinationFolder, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            await client.GetFileInfoAsync(QnapApiClient.Join(destinationFolder, sourceName), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            return false;
        }
    }

    /// <summary>A re-login that replaced a token saves the current ones, so the next launch uses them.</summary>
    private void AttachPersistence(QnapApiClient client)
        => client.CredentialsChanged = updated =>
        {
            // Only the client that is still this account's live one may write.
            if (ReferenceEquals(Volatile.Read(ref _client), client))
            {
                Credentials.Save(CredentialKey, updated);
            }
        };

    /// <summary>
    /// An HttpClient per credential set: with "Allow self-signed certificate" on, an invalid
    /// certificate is accepted for the NAS host the user typed and no other.
    /// </summary>
    private QnapApiClient CreateClient(QnapCredentials credentials, out HttpClient http)
    {
        http = _handler is not null
            ? new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }
            : CloudHttp.Create(trustedSelfSignedHost: credentials.AllowSelfSignedCertificate ? QnapApiClient.HostFrom(credentials.ServerUrl) : null);
        lock (_clientsGate)
        {
            _httpClients.Add(http);
        }

        var client = new QnapApiClient(http, credentials);
        if (PollInterval is { } interval)
        {
            client.PollInterval = interval;
        }

        return client;
    }

    private void ReleaseHttpClient(HttpClient http)
    {
        lock (_clientsGate)
        {
            _httpClients.Remove(http);
        }

        http.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_clientsGate)
            {
                foreach (var http in _httpClients)
                {
                    http.Dispose();
                }

                _httpClients.Clear();
            }
        }

        base.Dispose(disposing);
    }
}
