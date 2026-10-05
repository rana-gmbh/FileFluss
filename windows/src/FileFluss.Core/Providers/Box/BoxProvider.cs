// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.Box;

/// <summary>
/// Box (port of the macOS <c>BoxProvider</c>): browser OAuth with a loopback redirect,
/// id-addressed API behind a path cache, chunked upload sessions above 50 MiB, server-side
/// move/copy, search, the full share-link set, quota.
/// </summary>
[CloudProvider(CloudProviderType.Box)]
public sealed class BoxProvider : CloudProvider
{
    private readonly HttpClient _http;
    private volatile BoxApiClient? _client;

    public BoxProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    /// <summary>Test seam: tests pass a FakeHttpHandler; null → <see cref="CloudHttp.Create"/>.</summary>
    internal BoxProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _http = handler is null ? CloudHttp.Create() : CreateClient(handler);

        // Restore only; the first API call refreshes the access token when it has expired.
        // Credentials live under "box.<account uuid>" — never "box.<login>", the key the
        // deprecated macOS initializer used, which the rest of the app never read.
        if (Credentials.Load<BoxCredentials>(CredentialKey) is { } stored)
        {
            _client = NewClient(stored);
        }
    }

    /// <summary>The browser round trip. Tests replace it so no listener or browser is involved.</summary>
    internal Func<Func<string, Uri>, CancellationToken, Task<OAuthCallback>> Authenticator { get; set; }
        = (authorizeUrl, cancellationToken) => OAuthLoopback.AuthenticateAsync(authorizeUrl, cancellationToken);

    public override CloudProviderType ProviderType => CloudProviderType.Box;

    /// <summary>Names are case-insensitive here ("a.txt" and "A.txt" are one file).</summary>
    public override bool IsCaseSensitive => false;

    protected override string CredentialKeyPrefix => "box";

    public override bool IsAuthenticated => _client is not null;

    // The macOS key verbatim, so its translations apply. Its last sentence predates chunked
    // uploads and is no longer true; the wording wants a Windows.strings override.
    public override ConnectSpec ConnectSpec { get; } = ConnectSpec.Browser(
        ConnectField.Info("Click Connect to sign in with your Box account. Your browser will open for authentication. Uploads are capped at 50 MB per file in this version."));

    /// <summary>
    /// Box's documented ceiling for chunked uploads. The account's own plan limit (e.g.
    /// 250 MB on free) is enforced by Box when the upload session is opened.
    /// </summary>
    public override long? MaxUploadFileSize => BoxApiClient.MaxChunkedUploadBytes;

    internal BoxApiClient? ApiClient => _client;

    private BoxApiClient Client => _client ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - Connecting

    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        prompt.ShowStatus(Localization.L("Waiting for sign-in in browser…"));
        try
        {
            // CSRF defence: the authorization server must round-trip the same state.
            var state = Pkce.CreateState();
            var callback = await Authenticator(
                redirectUri => BoxApiClient.BuildAuthorizeUri(redirectUri, state),
                cancellationToken).ConfigureAwait(false);
            var code = callback.RequireCode(state);
            var credentials = await BoxApiClient.ExchangeCodeAsync(_http, code, callback.RedirectUri, cancellationToken).ConfigureAwait(false);

            // Only now, with a working token in hand, is anything stored replaced.
            Credentials.Save(CredentialKey, credentials);
            _client = NewClient(credentials);
            return new ConnectResult(credentials.DisplayName);
        }
        finally
        {
            prompt.ShowStatus(null);
        }
    }

    public override async Task DisconnectAsync()
    {
        _client = null;
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

    /// <summary>Server-side: Box accepts a parent change on PUT /files/{id} and /folders/{id}.</summary>
    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => Client.MoveItemAsync(path, newPath, cancellationToken);

    /// <summary>Server-side via POST /files/{id}/copy (or the folder equivalent for trees).</summary>
    public override Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => Client.CopyItemAsync(path, newPath, cancellationToken);

    /// <summary>
    /// Box's public API can't set <c>content_modified_at</c> on an existing file (the field is
    /// missing from PUT /files/{id}). Dates still survive a copy because uploads carry them in
    /// their attributes; only a standalone "stamp this file" lands here, and callers treat
    /// NotImplemented as best-effort.
    /// </summary>
    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
        => throw CloudProviderException.NotImplemented();

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => Client.GetMetadataAsync(path, cancellationToken);

    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
        => await Client.SearchAsync(query, path, cancellationToken).ConfigureAwait(false);

    // FolderSizeAsync: macOS walks the listing recursively — exactly the inherited default.

    // MARK: - Quota

    public override Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
        => _client is { } client ? client.StorageQuotaAsync(cancellationToken) : Task.FromResult<CloudStorageQuota?>(null);

    // MARK: - Share links

    /// <summary>
    /// The full set on PUT /files/{id}: read back, edit in place (the PUT is an upsert),
    /// withdraw by nulling the field. Only the API's ceiling — password and expiry need a paid
    /// plan, and an enterprise admin can restrict or disable public links, in which case
    /// creation fails with Box's own message or comes back downgraded (see the link's Note).
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
        PasswordRequiresPaidPlan = true,
        ExpiryRequiresPaidPlan = true,
    };

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
            _http.Dispose();
        }

        base.Dispose(disposing);
    }

    // MARK: - Private

    /// <summary>
    /// Every refresh reaches the credential store at once — above all the rotated refresh
    /// token, which is worthless if it only ever lives in memory (issue #46). Only while the
    /// client is still this provider's: a refresh that lands after Disconnect or a re-sign-in
    /// must not resurrect or overwrite what replaced it.
    /// </summary>
    private BoxApiClient NewClient(BoxCredentials credentials)
    {
        BoxApiClient? client = null;
        client = new BoxApiClient(_http, credentials, updated =>
        {
            if (ReferenceEquals(_client, client))
            {
                Credentials.Save(CredentialKey, updated);
            }
        });
        return client;
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CloudHttp.UserAgent);
        return client;
    }
}
