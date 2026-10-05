// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.WebDav;

/// <summary>
/// GMX Cloud (MediaCenter) has no public API; it speaks WebDAV. This provider is a preset
/// around <see cref="WebDavClient"/> so users only enter their GMX email and password.
/// Unlike generic WebDAV there is no PROPPATCH mtime support (the macOS provider never
/// offered it).
/// </summary>
[CloudProvider(CloudProviderType.GmxCloud)]
public sealed class GmxCloudProvider : WebDavProviderBase
{
    /// <summary>
    /// The endpoint the GMX MediaCenter web UI and MailCheck apps use. Subject to change by
    /// 1&amp;1 / United Internet — if it stops working, try <c>https://webdav.mediacenter.gmx.net/</c>.
    /// </summary>
    internal const string ServerUrl = "https://webdav.mc.gmx.net/";

    internal const string EmailKey = "email";
    internal const string PasswordKey = "password";

    private volatile Session? _session;

    public GmxCloudProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal GmxCloudProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials, handler)
    {
        if (credentials.Load<WebDavCredentials>(CredentialKey) is { } stored)
        {
            _session = new Session(stored, new WebDavClient(Http, stored.ServerUrl, stored.Username, stored.Password, WebDavDialect.Gmx));
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.GmxCloud;

    protected override string CredentialKeyPrefix => "gmxCloud";

    private protected override WebDavClient? Dav => _session?.Client;

    /// <summary>
    /// GMX MediaCenter rejects single-PUT uploads at the 4 GiB / 32-bit Content-Length
    /// boundary with HTTP 422. Pre-flight larger files so the upload isn't sent at all.
    /// </summary>
    public override long? MaxUploadFileSize => 4_000_000_000;

    // MARK: - Connecting

    public override ConnectSpec ConnectSpec => IsAuthenticated
        ? new ConnectSpec(
            new ConnectField(EmailKey, ConnectFieldKind.Email, "GMX Email"),
            new ConnectField(PasswordKey, ConnectFieldKind.Password, "Password"),
            ConnectField.Info("Password is not stored locally and is re-entered each time you edit."))
        : new ConnectSpec(
            ConnectField.Info("Sign in with your GMX email and password. GMX Cloud is accessed over WebDAV; this may not work with GMX accounts that have two-factor authentication enabled."),
            new ConnectField(EmailKey, ConnectFieldKind.Email, "GMX Email"),
            new ConnectField(PasswordKey, ConnectFieldKind.Password, "Password"));

    /// <summary>GMX presents as email + password, not server URL + user name.</summary>
    public override IReadOnlyDictionary<string, string> GetEditableConnectValues()
        => _session?.Credentials is { } c
            ? new Dictionary<string, string> { [EmailKey] = c.Username }
            : new Dictionary<string, string>();

    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var credentials = await WebDavClient.AuthenticateAsync(
            Http,
            ServerUrl,
            values.GetTrimmed(EmailKey),
            values.Get(PasswordKey),
            WebDavDialect.Gmx.LogCategory,
            cancellationToken).ConfigureAwait(false);

        Credentials.Save(CredentialKey, credentials);
        _session = new Session(credentials, new WebDavClient(Http, credentials.ServerUrl, credentials.Username, credentials.Password, WebDavDialect.Gmx));
        SupportLogger.Shared.Log($"Authenticated as {credentials.DisplayName}", WebDavDialect.Gmx.LogCategory);
        return new ConnectResult(credentials.DisplayName);
    }

    public override Task DisconnectAsync()
    {
        _session = null;
        return base.DisconnectAsync();
    }

    // MARK: - Optional operations

    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
        => await RequireDav().SearchByWalkingAsync(query, path, cancellationToken).ConfigureAwait(false);

    public override Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
        => Dav is { } dav ? dav.StorageQuotaAsync(cancellationToken) : Task.FromResult<CloudStorageQuota?>(null);

    private sealed record Session(WebDavCredentials Credentials, WebDavClient Client);
}
