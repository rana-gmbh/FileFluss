// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.WebDav;

/// <summary>Any WebDAV server, signed in with server URL, user name and password (HTTP Basic).</summary>
[CloudProvider(CloudProviderType.WebDav)]
public sealed class WebDavProvider : WebDavProviderBase
{
    internal const string ServerUrlKey = "serverUrl";
    internal const string UsernameKey = "username";
    internal const string PasswordKey = "password";

    private volatile Session? _session;

    public WebDavProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal WebDavProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials, handler)
    {
        if (credentials.Load<WebDavCredentials>(CredentialKey) is { } stored)
        {
            _session = new Session(stored, new WebDavClient(Http, stored.ServerUrl, stored.Username, stored.Password, WebDavDialect.Generic));
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.WebDav;

    protected override string CredentialKeyPrefix => "webdav";

    private protected override WebDavClient? Dav => _session?.Client;

    /// <summary>
    /// Generic WebDAV uploads with a single PUT. Most servers (PHP-backed in particular) cap
    /// one request at the 32-bit Content-Length boundary, so anything ≥ 4 GiB is rejected;
    /// pre-flight just under that.
    /// </summary>
    public override long? MaxUploadFileSize => 4_000_000_000;

    // MARK: - Connecting

    public override ConnectSpec ConnectSpec => IsAuthenticated
        ? new ConnectSpec(
            new ConnectField(ServerUrlKey, ConnectFieldKind.Url, "Server URL"),
            new ConnectField(UsernameKey, ConnectFieldKind.Text, "Username"),
            new ConnectField(PasswordKey, ConnectFieldKind.Password, "Password"))
        : new ConnectSpec(
            ConnectField.Info("Enter your WebDAV server URL, username, and password."),
            new ConnectField(ServerUrlKey, ConnectFieldKind.Url, "Server URL (e.g. https://dav.example.com/files)"),
            new ConnectField(UsernameKey, ConnectFieldKind.Text, "Username"),
            new ConnectField(PasswordKey, ConnectFieldKind.Password, "Password"));

    public override IReadOnlyDictionary<string, string> GetEditableConnectValues()
        => _session?.Credentials is { } c
            ? new Dictionary<string, string> { [ServerUrlKey] = c.ServerUrl, [UsernameKey] = c.Username }
            : new Dictionary<string, string>();

    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var credentials = await WebDavClient.AuthenticateAsync(
            Http,
            values.GetTrimmed(ServerUrlKey),
            values.GetTrimmed(UsernameKey),
            values.Get(PasswordKey),
            WebDavDialect.Generic.LogCategory,
            cancellationToken).ConfigureAwait(false);

        Credentials.Save(CredentialKey, credentials);
        _session = new Session(credentials, new WebDavClient(Http, credentials.ServerUrl, credentials.Username, credentials.Password, WebDavDialect.Generic));
        SupportLogger.Shared.Log($"Authenticated as {credentials.DisplayName}", WebDavDialect.Generic.LogCategory);
        return new ConnectResult(credentials.DisplayName);
    }

    public override Task DisconnectAsync()
    {
        _session = null;
        return base.DisconnectAsync();
    }

    // MARK: - Optional operations

    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => RequireDav().SetModificationDateAsync(path, date, cancellationToken);

    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
        => await RequireDav().SearchByWalkingAsync(query, path, cancellationToken).ConfigureAwait(false);

    public override Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
        => Dav is { } dav ? dav.StorageQuotaAsync(cancellationToken) : Task.FromResult<CloudStorageQuota?>(null);

    private sealed record Session(WebDavCredentials Credentials, WebDavClient Client);
}
