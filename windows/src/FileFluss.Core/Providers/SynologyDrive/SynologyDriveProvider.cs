// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.SynologyDrive;

/// <summary>
/// A Synology NAS through DSM's File Station web API (port of the macOS
/// <c>SynologyDriveProvider</c>). Works for any NAS reachable on the network or by its DDNS
/// name; most home units use a self-signed certificate, which the user can allow for exactly
/// the host they typed.
///
/// <para>No quota: DSM's <c>SYNO.Core.Quota</c> needs admin rights or endpoints that differ
/// between DSM versions, so the status bar segment stays hidden, as on macOS.</para>
/// </summary>
[CloudProvider(CloudProviderType.SynologyDrive)]
public sealed class SynologyDriveProvider : CloudProvider
{
    internal const string ServerUrlKey = "serverUrl";
    internal const string UsernameKey = "username";
    internal const string PasswordKey = "password";
    internal const string OtpKey = "otp";
    internal const string AllowSelfSignedKey = "allowSelfSigned";

    private readonly HttpMessageHandler? _handler;
    private readonly Lock _clientsGate = new();
    private readonly List<HttpClient> _httpClients = [];
    private SynologyDriveApiClient? _client;
    private TimeSpan? _pollInterval;

    public SynologyDriveProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal SynologyDriveProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _handler = handler;
        if (credentials.Load<SynologyDriveCredentials>(CredentialKey) is { } stored)
        {
            _client = CreateClient(stored);
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.SynologyDrive;

    protected override string CredentialKeyPrefix => "synologyDrive";

    public override bool IsAuthenticated => Volatile.Read(ref _client) is not null;

    /// <summary>
    /// File Station's sharing API takes a password and an expiry date, and has no view-only
    /// mode. <c>list</c> finds an existing link by path; <c>edit</c>/<c>delete</c> change and
    /// withdraw it by id, keeping its URL. Links other DSM users own aren't visible.
    /// </summary>
    public override ShareLinkCapabilities ShareLinkCapabilities { get; } = new()
    {
        CanCreate = true,
        CanQueryExisting = true,
        CanUpdate = true,
        CanRemove = true,
        SupportsPassword = true,
        SupportsExpiry = true,
    };

    /// <summary>Test seam: shortens the copy/move and search polling.</summary>
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

    private SynologyDriveApiClient Client => Volatile.Read(ref _client) ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - Connecting

    public override ConnectSpec ConnectSpec => new(
        ConnectField.Info("Enter your NAS hostname (or QuickConnect URL) and DSM credentials. The default port is 5001 for HTTPS."),
        new ConnectField(ServerUrlKey, ConnectFieldKind.Url, "Server URL") { Placeholder = "Server URL (e.g. https://my-nas.local:5001)" },
        new ConnectField(UsernameKey, ConnectFieldKind.Text, "Username"),
        new ConnectField(PasswordKey, ConnectFieldKind.Password, "Password"),
        new ConnectField(OtpKey, ConnectFieldKind.OneTimeCode, "OTP Code (only if 2-factor auth is on)") { Required = false },
        new ConnectField(AllowSelfSignedKey, ConnectFieldKind.Checkbox, "Allow self-signed certificate")
        {
            Required = false,
            DefaultValue = "true",
            Help = "Most home Synology NAS units use a self-signed certificate. Turn this off only if you've installed a real CA-signed cert (e.g. Let's Encrypt).",
        });

    /// <summary>
    /// Logs in to DSM. When the account turns out to need a 2-factor code and none was typed,
    /// asks for one and tries again. Named "Synology Drive (user@nas-host)", as on macOS.
    /// </summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var serverUrl = SynologyDriveApiClient.NormalizeServerUrl(values.GetTrimmed(ServerUrlKey));
        var username = values.GetTrimmed(UsernameKey);
        var otp = SynologyDriveApiClient.NormalizeOtp(values.Get(OtpKey));

        // Carry a device token this account already has into the new login, so editing a
        // 2-factor account — a changed password, a toggled certificate setting — doesn't
        // demand a fresh OTP or throw the token away. Only when the server and user are
        // unchanged: a token belongs to one DSM account and must not be sent to another host.
        var reusableDeviceId = Credentials.Load<SynologyDriveCredentials>(CredentialKey) is { } stored
                               && stored.Username == username
                               && SynologyDriveApiClient.NormalizeServerUrl(stored.ServerUrl) == serverUrl
            ? stored.DeviceId
            : null;

        var credentials = new SynologyDriveCredentials(
            serverUrl,
            username,
            values.Get(PasswordKey),
            values.GetBool(AllowSelfSignedKey, fallback: true),
            $"{username}@{SynologyDriveApiClient.HostFrom(serverUrl)}",
            reusableDeviceId);

        var client = CreateClient(credentials, out var http);
        try
        {
            var failure = await client.TryLoginAsync(otp, cancellationToken).ConfigureAwait(false);
            if (failure == 403 && otp.Length == 0)
            {
                var code = SynologyDriveApiClient.NormalizeOtp(await prompt.RequestCodeAsync(
                    CloudProviderType.SynologyDrive.DisplayName(),
                    Localization.L("Two-factor authentication required. Enter your 6-digit code and try again."),
                    cancellationToken).ConfigureAwait(false));
                if (code.Length > 0)
                {
                    failure = await client.TryLoginAsync(code, cancellationToken).ConfigureAwait(false);
                }
            }

            if (failure is { } authError)
            {
                throw SynologyDriveApiClient.MapAuthError(authError);
            }
        }
        catch
        {
            // Stored credentials and the live client stay as they were.
            ReleaseHttpClient(http);
            throw;
        }

        // The logged-in client's credentials carry the device token its OTP login obtained.
        // Keep that client too: it already holds the session, so the first listing doesn't
        // trigger a second login without an OTP.
        var signedIn = client.Credentials;
        Credentials.Save(CredentialKey, signedIn);
        Volatile.Write(ref _client, client);
        SupportLogger.Shared.Log($"[Synology] Authenticated as {signedIn.DisplayName}", "Synology");
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
        => CopyMoveAsync(path, newPath, removeSource: true, cancellationToken);

    public override Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => CopyMoveAsync(path, newPath, removeSource: false, cancellationToken);

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

    public override Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => Client.UpdateShareLinkAsync(path, options, cancellationToken);

    public override Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => Client.RemoveShareLinkAsync(path, cancellationToken);

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

    /// <summary>
    /// CopyMove always lands the item in the destination folder under its <i>source</i> name;
    /// a different target name is a rename afterwards. As on macOS, a failed rename is not
    /// reported — the item has arrived, under its old name.
    /// </summary>
    private async Task CopyMoveAsync(string path, string newPath, bool removeSource, CancellationToken cancellationToken)
    {
        var client = Client;
        var destinationFolder = GetParentPath(newPath);
        var destinationName = GetName(newPath);
        var sourceName = GetName(path);
        if (destinationName != sourceName && await LandingSpotTakenAsync(client, path, destinationFolder, sourceName, cancellationToken).ConfigureAwait(false))
        {
            // CopyMove runs with overwrite on and lands under the source name, so a renamed
            // copy ("Keep both", "a (2).txt") would first overwrite the very file the user chose
            // to keep — or the source itself, copying within its folder. Let the caller copy
            // the bytes instead.
            throw CloudProviderException.NotImplemented();
        }

        await client.CopyMoveAsync(path, destinationFolder, removeSource, cancellationToken).ConfigureAwait(false);
        if (destinationName != sourceName)
        {
            try
            {
                await client.RenameItemAsync(CombinePath(destinationFolder, sourceName), destinationName, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException e)
            {
                SupportLogger.Shared.Log($"[Synology] Rename after copy/move failed: {e.Message}", "Synology", LogLevel.Error);
            }
        }
    }

    private async Task<bool> LandingSpotTakenAsync(SynologyDriveApiClient client, string path, string destinationFolder, string sourceName, CancellationToken cancellationToken)
    {
        if (string.Equals(IO.CloudPath.Normalize(GetParentPath(path)), IO.CloudPath.Normalize(destinationFolder), StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            await client.GetFileInfoAsync(CombinePath(destinationFolder, sourceName), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// An HttpClient per credential set: with "Allow self-signed certificate" on, an invalid
    /// certificate is accepted for the NAS host the user typed and no other.
    /// </summary>
    private SynologyDriveApiClient CreateClient(SynologyDriveCredentials credentials) => CreateClient(credentials, out _);

    private SynologyDriveApiClient CreateClient(SynologyDriveCredentials credentials, out HttpClient http)
    {
        http = _handler is not null
            ? new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }
            : CloudHttp.Create(trustedSelfSignedHost: credentials.AllowSelfSignedCertificate ? SynologyDriveApiClient.HostFrom(credentials.ServerUrl) : null);
        lock (_clientsGate)
        {
            _httpClients.Add(http);
        }

        var client = new SynologyDriveApiClient(http, credentials);
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
}
