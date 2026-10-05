// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.WordPress;

/// <summary>
/// A WordPress site's media library over the REST API with an Application Password — port
/// of the macOS <c>WordPressProvider</c>. The library is shown as virtual year/month folders;
/// folders cannot be created (<see cref="CloudProviderTypeExtensions.CanCreateFolders"/>),
/// rename changes the attachment title, and a share link is the media file's public URL.
/// </summary>
[CloudProvider(CloudProviderType.WordPress)]
public sealed class WordPressProvider : CloudProvider
{
    internal const string SiteUrlField = "siteUrl";
    internal const string UsernameField = "username";
    internal const string PasswordField = "password";

    private readonly HttpClient _http;
    private volatile WordPressApiClient? _client;

    public WordPressProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal WordPressProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        // Redirects are followed by hand so the Basic header and a POST body survive a
        // same-host redirect (see WordPressApiClient.SendAsync).
        _http = handler is null
            ? CloudHttp.Create(allowAutoRedirect: false)
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        if (credentials.Load<WordPressCredentials>(CredentialKey) is { } stored)
        {
            _client = new WordPressApiClient(_http, stored);
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.WordPress;

    protected override string CredentialKeyPrefix => "wordpress";

    public override bool IsAuthenticated => _client is not null;

    // MARK: - Connecting

    public override ConnectSpec ConnectSpec { get; } = new(
        ConnectField.Info("Enter your WordPress site URL and an Application Password. Create one in WordPress under Users → Profile → Application Passwords."),
        new ConnectField(SiteUrlField, ConnectFieldKind.Url, "Site URL") { Placeholder = "Site URL (e.g. https://example.com)" },
        new ConnectField(UsernameField, ConnectFieldKind.Text, "Username"),
        new ConnectField(PasswordField, ConnectFieldKind.Password, "Application Password"));

    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var credentials = await WordPressApiClient.VerifyAsync(
            _http,
            values.GetTrimmed(SiteUrlField),
            values.GetTrimmed(UsernameField),
            values.Get(PasswordField),
            cancellationToken).ConfigureAwait(false);

        // Only a verified sign-in replaces what was stored.
        Credentials.Save(CredentialKey, credentials);
        var client = new WordPressApiClient(_http, credentials);
        _client = client;
        return new ConnectResult(client.UserDisplayName());
    }

    public override IReadOnlyDictionary<string, string> GetEditableConnectValues()
        => _client?.Credentials is { } credentials
            ? new Dictionary<string, string> { [SiteUrlField] = credentials.SiteUrl, [UsernameField] = credentials.Username }
            : new Dictionary<string, string>();

    public override Task DisconnectAsync()
    {
        _client = null;
        return base.DisconnectAsync();
    }

    private WordPressApiClient Client => _client ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - File operations

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Client.ListFolderAsync(path, cancellationToken);

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Client.DownloadAsync(remotePath, localPath, onBytes, cancellationToken);

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Client.UploadAsync(localPath, remotePath, onBytes, cancellationToken);

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Client.DeleteAsync(path, cancellationToken);

    /// <summary>
    /// The media library files uploads under auto-generated date folders; arbitrary folders
    /// cannot be created. macOS answers with HTTP 405 as well.
    /// </summary>
    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => throw CloudProviderException.ServerError(405);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Client.RenameAsync(path, newName, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var client = Client;
        var components = CloudPath.Components(path);

        // The virtual root, year and month folders have no attachment behind them.
        if (components.Length <= 2 && components.All(c => c.Length > 0 && c.All(char.IsAsciiDigit)))
        {
            var normalized = CloudPath.Normalize(path);
            return Task.FromResult(new CloudFileItem(normalized, CloudPath.Name(normalized), normalized, true, 0, CloudFileItem.UnknownDate));
        }

        return client.GetFileInfoAsync(path, cancellationToken);
    }

    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
        => await Client.SearchAsync(query, cancellationToken).ConfigureAwait(false);

    // MARK: - Share links

    /// <summary>The media file already has a public URL: nothing to create, so no password, expiry or unshare either.</summary>
    public override ShareLinkCapabilities ShareLinkCapabilities { get; } = new() { CanCreate = true };

    public override async Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
    {
        var url = await Client.PublicMediaUrlAsync(path, cancellationToken).ConfigureAwait(false);
        return new CloudShareLink(url)
        {
            DirectDownloadUrl = url,
            Note = Localization.L("This file was already public — WordPress media has no private state."),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
        }

        base.Dispose(disposing);
    }
}
