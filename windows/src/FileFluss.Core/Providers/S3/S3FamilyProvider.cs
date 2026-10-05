// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.S3;

/// <summary>
/// What AWS S3, S3-compatible storage and Synology C2 share: one <see cref="S3ApiClient"/>
/// over SigV4, presigned share links, the 5 GiB single-PUT limit. The three macOS providers
/// were copies of each other apart from how they turn the form into credentials; here only
/// that part (<see cref="CloudProvider.ConnectSpec"/>, <see cref="CloudProvider.ConnectAsync"/>)
/// lives in the subclasses.
/// </summary>
public abstract class S3FamilyProvider : CloudProvider
{
    /// <summary>S3 caps a single PUT at 5 GiB. Multipart upload would lift it but isn't implemented (as on macOS).</summary>
    public const long SinglePutLimit = 5L * 1024 * 1024 * 1024;

    internal const string AccessKeyIdKey = "accessKeyId";
    internal const string SecretAccessKeyKey = "secretAccessKey";
    internal const string RegionKey = "region";
    internal const string EndpointKey = "endpoint";
    internal const string DisplayNameKey = "displayName";
    internal const string PathKey = "path";

    private readonly HttpClient _http;
    private S3ApiClient? _client;

    private protected S3FamilyProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        // Redirects are handled by the client: a 301/307 names the bucket's real region and
        // the request has to be signed again for it.
        _http = handler is null
            ? CloudHttp.Create(allowAutoRedirect: false)
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Test seam for deterministic signatures; the wall clock otherwise.</summary>
    internal Func<DateTimeOffset>? Clock { get; set; }

    public override bool IsAuthenticated => Volatile.Read(ref _client) is not null;

    /// <summary>The bucket/folder the account is scoped to, so "Jump to root" stays inside it.</summary>
    public override string RootPath => Volatile.Read(ref _client)?.Credentials.RootPath ?? "/";

    public override long? MaxUploadFileSize => SinglePutLimit;

    /// <summary>
    /// S3 has no server-side "share" concept: the link is a presigned GET signed locally with
    /// the account's own credentials, so it always expires (7 days at most, sooner with
    /// temporary credentials) and cannot carry a password.
    /// </summary>
    public override ShareLinkCapabilities ShareLinkCapabilities { get; } = new()
    {
        CanCreate = true,
        SupportsExpiry = true,
        RequiresExpiry = true,
        MaximumExpiry = SigV4.MaximumPresignedExpiry,
    };

    internal S3ApiClient Client => Volatile.Read(ref _client) ?? throw CloudProviderException.NotAuthenticated();

    private protected S3Credentials? StoredCredentials => Volatile.Read(ref _client)?.Credentials;

    // MARK: - File operations

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Client.ListFolderAsync(path, cancellationToken);

    public override Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        => Client.DownloadFileAsync(remotePath, localPath, onBytes, cancellationToken);

    public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var client = Client;
        CloudProviderException.EnforceUploadSizeLimit(localPath, MaxUploadFileSize);
        return client.UploadFileAsync(localPath, remotePath, onBytes, cancellationToken);
    }

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Client.DeleteItemAsync(path, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Client.CreateFolderAsync(path, cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => ObjectOnlyAsync(path, c => c.RenameItemAsync(path, newName, cancellationToken), cancellationToken);

    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => ObjectOnlyAsync(path, c => c.MoveItemAsync(path, newPath, cancellationToken), cancellationToken);

    public override Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
        => ObjectOnlyAsync(path, c => c.CopyItemAsync(path, newPath, cancellationToken), cancellationToken);

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
        => Client.GetFileInfoAsync(path, cancellationToken);

    public override Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
        => Client.FolderSizeAsync(path, cancellationToken);

    // MARK: - Share links

    public override async Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
    {
        var client = Client;
        var now = (Clock ?? DefaultClock)();

        // The sheet always supplies an expiry (RequiresExpiry); fall back to the maximum
        // rather than failing if it somehow didn't.
        var requested = options.Expiry is { } expiry ? expiry - now : SigV4.MaximumPresignedExpiry;
        var url = await client.PresignedDownloadUrlAsync(path, requested, cancellationToken).ConfigureAwait(false);
        var effective = TimeSpan.FromSeconds(Math.Max(1, Math.Min(Math.Round(requested.TotalSeconds), SigV4.MaximumPresignedExpiry.TotalSeconds)));
        return new CloudShareLink(url)
        {
            DirectDownloadUrl = url,
            ExpiresAt = now + effective,
            HasPassword = false,
            Note = Localization.L("Anyone with this link can download the file until it expires. The link cannot be revoked early."),
        };
    }

    // MARK: - Connecting (shared by the subclasses)

    /// <summary>"/my-bucket/folder" from whatever was typed ("my-bucket/folder/", " /my-bucket "); null for nothing.</summary>
    public static string? NormalizeRootPath(string? raw)
    {
        var trimmed = raw?.Trim().Trim('/');
        return string.IsNullOrEmpty(trimmed) ? null : "/" + trimmed;
    }

    /// <summary>The macOS account name with the scope appended: "AWS S3 (***6UDK) — my-bucket/team".</summary>
    private protected static string WithRoot(string name, string? rootPath)
        => rootPath is null ? name : $"{name} — {rootPath[1..]}";

    /// <summary>
    /// Probes <paramref name="credentials"/> (the configured bucket when there is one, the
    /// bucket list otherwise), lists the configured folder once so a typo surfaces now rather
    /// than as an empty panel later, and only then stores the credentials.
    /// </summary>
    private protected async Task ConnectWithAsync(S3Credentials credentials, CancellationToken cancellationToken)
    {
        var probe = CreateClient(credentials);
        await probe.VerifyAccessAsync(credentials.RootPath, cancellationToken).ConfigureAwait(false);
        if (credentials.RootPath is { } root)
        {
            await probe.ListFolderAsync(root, cancellationToken).ConfigureAwait(false);
        }

        Credentials.Save(CredentialKey, credentials);
        Volatile.Write(ref _client, probe);
        SupportLogger.Shared.Log($"[{ProviderType.DisplayName()}] Authenticated (region {credentials.Region})", "S3");
    }

    /// <summary>Restores stored credentials (no network). Called by each subclass constructor.</summary>
    private protected void RestoreCredentials()
    {
        if (Credentials.Load<S3Credentials>(CredentialKey) is { } stored)
        {
            _client = CreateClient(stored);
        }
    }

    /// <summary>
    /// The Secret Access Key field. The macOS edit sheet showed the stored secret; on Windows
    /// secrets never go back into a form, so when editing an account the field may stay
    /// empty and the stored secret is kept.
    /// </summary>
    private protected ConnectField SecretField()
        => new(SecretAccessKeyKey, ConnectFieldKind.Password, "Secret Access Key")
        {
            Required = !IsAuthenticated,
            Help = IsAuthenticated ? "Leave empty to keep the stored secret access key." : null,
        };

    /// <summary>The typed secret, or the stored one when the field was left empty while editing.</summary>
    private protected string SecretOrStored(ConnectValues values)
    {
        var typed = values.Get(SecretAccessKeyKey);
        if (typed.Length > 0)
        {
            return typed;
        }

        return StoredCredentials?.SecretAccessKey ?? throw CloudProviderException.InvalidCredentials();
    }

    private protected static ConnectField PathField()
        => new(PathKey, ConnectFieldKind.Text, "Path (optional)")
        {
            Required = false,
            Placeholder = "Path (optional, e.g. my-bucket or my-bucket/my-folder)",
            Help = "Leave blank to see all buckets. Otherwise the panel opens directly at this bucket or sub-folder.",
        };

    /// <summary>The stored scope without its leading slash, as the macOS edit sheet showed it.</summary>
    private protected string EditablePath() => StoredCredentials?.RootPath is { Length: > 1 } root ? root[1..] : string.Empty;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
        }

        base.Dispose(disposing);
    }

    private static DateTimeOffset DefaultClock() => DateTimeOffset.UtcNow;

    private S3ApiClient CreateClient(S3Credentials credentials)
        => new(_http, credentials, () => (Clock ?? DefaultClock)());

    /// <summary>
    /// Copy, move and rename work on objects. A "folder" is only a prefix, so the copy finds
    /// no source object and S3 answers 404 — which macOS showed as "item not found". Here a
    /// 404 on something that is a folder becomes NotImplemented, so callers fall back to
    /// copying item by item instead of reporting a folder that plainly exists as missing.
    /// </summary>
    private async Task ObjectOnlyAsync(string path, Func<S3ApiClient, Task> operation, CancellationToken cancellationToken)
    {
        var client = Client;
        try
        {
            await operation(client).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            var item = await client.GetFileInfoAsync(path, cancellationToken).ConfigureAwait(false);
            if (item.IsDirectory)
            {
                throw CloudProviderException.NotImplemented();
            }

            throw;
        }
    }
}
