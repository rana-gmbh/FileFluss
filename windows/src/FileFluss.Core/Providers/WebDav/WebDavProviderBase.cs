// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.WebDav;

/// <summary>
/// The file operations WebDAV, GMX Cloud and NextCloud share: on macOS three providers
/// forwarding to the same WebDAV calls. Derived providers own their credentials and connect
/// form, and add search, quota, share links and modification dates where their macOS
/// counterpart has them.
/// </summary>
public abstract class WebDavProviderBase : CloudProvider
{
    private protected WebDavProviderBase(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        // Redirects are followed by WebDavHttp so the Authorization header survives them.
        Http = handler is null
            ? CloudHttp.Create(allowAutoRedirect: false)
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private protected HttpClient Http { get; }

    /// <summary>The signed-in DAV client, or null before sign-in.</summary>
    private protected abstract WebDavClient? Dav { get; }

    public override bool IsAuthenticated => Dav is not null;

    private protected WebDavClient RequireDav() => Dav ?? throw CloudProviderException.NotAuthenticated();

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => RequireDav().ListFolderAsync(path, cancellationToken);

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => RequireDav().DownloadFileAsync(remotePath, localPath, onBytes, cancellationToken);

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var dav = RequireDav();
        CloudProviderException.EnforceUploadSizeLimit(localPath, MaxUploadFileSize);
        return dav.UploadFileAsync(localPath, remotePath, onBytes, cancellationToken);
    }

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => RequireDav().DeleteItemAsync(path, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => RequireDav().CreateFolderAsync(path, cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => RequireDav().RenameItemAsync(path, newName, cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => RequireDav().GetFileInfoAsync(path, cancellationToken);

    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => RequireDav().MoveItemAsync(path, newPath, cancellationToken);

    public override Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => RequireDav().CopyItemAsync(path, newPath, cancellationToken);

    // FolderSizeAsync: the macOS clients walk the tree with Depth-1 PROPFINDs, which is
    // exactly the base implementation.

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Http.Dispose();
        }

        base.Dispose(disposing);
    }
}
