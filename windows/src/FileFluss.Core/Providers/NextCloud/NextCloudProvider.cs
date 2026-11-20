// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers.WebDav;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.NextCloud;

/// <summary>
/// Nextcloud: files over WebDAV (<c>/remote.php/dav/files/&lt;user&gt;</c>), sign-in by
/// Login Flow v2 in the browser or with an app password, quota from the OCS user endpoint,
/// public links through the OCS share API.
/// </summary>
[CloudProvider(CloudProviderType.NextCloud)]
public sealed class NextCloudProvider : WebDavProviderBase
{
    internal const string BrowserMode = "browser";
    internal const string AppPasswordMode = "appPassword";
    internal const string ServerUrlKey = "serverUrl";
    internal const string UsernameKey = "username";
    internal const string AppPasswordKey = "appPassword";

    private volatile NextCloudApi? _api;

    public NextCloudProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal NextCloudProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials, handler)
    {
        if (credentials.Load<NextCloudCredentials>(CredentialKey) is { } stored)
        {
            _api = new NextCloudApi(Http, stored);
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.NextCloud;

    protected override string CredentialKeyPrefix => "nextcloud";

    private protected override WebDavClient? Dav => _api?.Dav;

    /// <summary>Test seam: opens the Login Flow v2 page. Defaults to the system browser.</summary>
    internal Action<Uri> OpenBrowser { get; set; } = BrowserLauncher.Open;

    /// <summary>Test seam: how often Login Flow v2 is polled (macOS: every 3 s).</summary>
    internal TimeSpan LoginPollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Test seam: when Login Flow v2 gives up (macOS: 5 minutes — the user has wandered off).</summary>
    internal TimeSpan LoginDeadline { get; set; } = TimeSpan.FromMinutes(5);

    private NextCloudApi RequireApi() => _api ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - Connecting

    public override ConnectSpec ConnectSpec
    {
        get
        {
            // Editing an account (credentials exist) shows the Edit sheet's wording.
            var editing = IsAuthenticated;
            var serverUrl = new ConnectField(ServerUrlKey, ConnectFieldKind.Url, editing ? "Server URL" : "Server URL (e.g. https://cloud.example.com)");
            var username = new ConnectField(UsernameKey, ConnectFieldKind.Text, "Username");
            var appPassword = new ConnectField(AppPasswordKey, ConnectFieldKind.Password, "App Password");

            var browser = new ConnectMode(
                BrowserMode,
                "Browser Login",
                [
                    serverUrl,
                    ConnectField.Info(editing
                        ? "Click Re-authorize to open your server's login page in the browser. A new app password is issued and replaces the stored one."
                        : "Click Connect to open your Nextcloud server's login page in the browser. Sign in there with your normal username and password (2FA supported); FileFluss receives an app-specific password back automatically."),
                ])
            {
                OpensBrowser = true,
            };

            ConnectField[] manualFields = editing
                ? [serverUrl, username, appPassword]
                : [serverUrl, ConnectField.Info("Or enter your username and an app-specific password manually. Create one at Settings → Security → Devices & sessions."), username, appPassword];

            return new ConnectSpec([browser, new ConnectMode(AppPasswordMode, "App Password", manualFields)]);
        }
    }

    public override IReadOnlyDictionary<string, string> GetEditableConnectValues()
        => _api?.Credentials is { } c
            ? new Dictionary<string, string> { [ServerUrlKey] = c.ServerUrl, [UsernameKey] = c.Username }
            : new Dictionary<string, string>();

    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var serverUrl = values.GetTrimmed(ServerUrlKey);
        var credentials = values.Mode == AppPasswordMode
            ? await NextCloudApi.AuthenticateAsync(Http, serverUrl, values.GetTrimmed(UsernameKey), values.Get(AppPasswordKey), cancellationToken).ConfigureAwait(false)
            : await NextCloudApi.LoginFlowV2Async(Http, serverUrl, OpenBrowser, prompt, LoginPollInterval, LoginDeadline, cancellationToken).ConfigureAwait(false);

        Credentials.Save(CredentialKey, credentials);
        _api = new NextCloudApi(Http, credentials);
        SupportLogger.Shared.Log($"Authenticated as {credentials.DisplayName}", NextCloudApi.LogCategory);
        return new ConnectResult(credentials.DisplayName);
    }

    public override Task DisconnectAsync()
    {
        _api = null;
        return base.DisconnectAsync();
    }

    // MARK: - Optional operations

    /// <summary>
    /// Nextcloud/ownCloud accept a PROPPATCH of <c>getlastmodified</c> — mtime preservation
    /// for files that didn't arrive through the X-OC-Mtime upload path (server-side copies).
    /// </summary>
    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => RequireDav().SetModificationDateAsync(path, date, cancellationToken);

    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
        => await RequireDav().SearchAsync(query, path, cancellationToken).ConfigureAwait(false);

    public override Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
        => _api is { } api ? api.StorageQuotaAsync(cancellationToken) : Task.FromResult<CloudStorageQuota?>(null);

    // MARK: - Share links

    /// <summary>
    /// The full set — but only as the server's ceiling: an admin can switch link sharing off
    /// or enforce a password or expiry, and creation then fails with the server's own message.
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
    };

    public override Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => RequireApi().CreateShareLinkAsync(path, options, cancellationToken);

    public override Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => RequireApi().ExistingShareLinkAsync(path, cancellationToken);

    public override Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => RequireApi().UpdateShareLinkAsync(path, options, cancellationToken);

    public override Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => RequireApi().RemoveShareLinkAsync(path, cancellationToken);
}
