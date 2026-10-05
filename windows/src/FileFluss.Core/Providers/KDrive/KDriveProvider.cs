// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers.WebDav;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.KDrive;

/// <summary>
/// Infomaniak kDrive with a personal API token. An organisation token reaches several drives
/// (a personal drive plus shared workspaces), each addressed by its own drive id, so adding an
/// account asks which one; editing keeps the drive the account was added with.
/// </summary>
[CloudProvider(CloudProviderType.KDrive)]
public sealed class KDriveProvider : CloudProvider
{
    internal const string ApiTokenKey = "apiToken";

    /// <summary>Free-plan managers don't show the token UI in the sidebar; this URL works for every plan.</summary>
    internal const string TokenPageUrl = "https://manager.infomaniak.com/v3/ng/profile/user/token/list";

    private readonly HttpClient _http;
    private volatile KDriveApiClient? _client;

    public KDriveProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal KDriveProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _http = handler is null
            ? CloudHttp.Create()
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        // The root folder id is looked up lazily on first use (no network here).
        if (credentials.Load<KDriveCredentials>(CredentialKey) is { } stored)
        {
            _client = new KDriveApiClient(_http, stored);
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.KDrive;

    protected override string CredentialKeyPrefix => "kdrive";

    public override bool IsAuthenticated => _client is not null;

    internal KDriveApiClient? Client => _client;

    private KDriveApiClient Require() => _client ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - Connecting

    public override ConnectSpec ConnectSpec => IsAuthenticated
        ? new ConnectSpec(
            new ConnectField(ApiTokenKey, ConnectFieldKind.Password, "API Token"),
            ConnectField.Info("Paste a new Infomaniak API token. The existing workspace selection is preserved."))
        : new ConnectSpec(
            ConnectField.Info("Create an API token in your Infomaniak account, then paste it below."),
            ConnectField.Link("Open Infomaniak token page", TokenPageUrl),
            new ConnectField(ApiTokenKey, ConnectFieldKind.Password, "API Token"));

    /// <summary>
    /// Probes the token, picks the drive and signs in. A new account with several drives asks
    /// which one to add ("add the account again to use another"); editing an account keeps
    /// its drive — the workspace can't be changed from Edit.
    /// </summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        var token = values.GetTrimmed(ApiTokenKey);
        var existingDriveId = _client?.Credentials.DriveId;

        Profile profile;
        IReadOnlyList<KDriveDrive> drives;
        prompt.ShowStatus(Localization.L("Looking up your drives…"));
        try
        {
            profile = await FetchProfileAsync(token, cancellationToken).ConfigureAwait(false);
            drives = await FetchDrivesAsync(token, profile.AccountId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            prompt.ShowStatus(null);
        }

        KDriveDrive chosen;
        if (existingDriveId is { } keep && drives.FirstOrDefault(d => d.Id == keep) is { } existing)
        {
            chosen = existing;
        }
        else if (existingDriveId is null && drives.Count > 1)
        {
            var index = await prompt.ChooseAsync(
                Localization.L("Choose a drive to add"),
                Localization.L("To use more than one drive, add the kDrive account again with the same token and pick a different drive."),
                drives.Select(d => d.Name ?? d.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList(),
                cancellationToken).ConfigureAwait(false);
            if (index is not { } picked || picked < 0 || picked >= drives.Count)
            {
                throw CloudProviderException.Cancelled();
            }

            chosen = drives[picked];
        }
        else
        {
            chosen = drives.FirstOrDefault() ?? throw CloudProviderException.NotFound("No kDrive found for this account");
        }

        SupportLogger.Shared.Log($"Using drive: id={chosen.Id}", KDriveApiClient.LogCategory);

        var userName = profile.DisplayName.Length > 0 ? profile.DisplayName : profile.Email;
        var credentials = new KDriveCredentials(token, chosen.Id, userName);
        var client = new KDriveApiClient(_http, credentials);

        // Resolve and cache the root folder id; 1 is the usual default when the lookup fails.
        long rootId;
        try
        {
            rootId = await client.FetchRootFileIdAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException)
        {
            rootId = 1;
        }

        client.SetRootId(rootId);
        Credentials.Save(CredentialKey, credentials);
        _client = client;

        // "kDrive (me · Common Documents)" tells workspaces of one token apart.
        var label = (userName.Length > 0, string.IsNullOrEmpty(chosen.Name)) switch
        {
            (true, false) => $"{userName} · {chosen.Name}",
            (true, true) => userName,
            (false, false) => chosen.Name!,
            _ => null,
        };
        return new ConnectResult(label);
    }

    public override Task DisconnectAsync()
    {
        _client = null;
        return base.DisconnectAsync();
    }

    private sealed record Profile(string Email, string DisplayName, long AccountId);

    /// <summary><c>/1/profile</c>: who the token belongs to and the account whose drives to list.</summary>
    private async Task<Profile> FetchProfileAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{KDriveApiClient.ApiBase}/1/profile") { Headers = { Authorization = CloudHttp.Bearer(token) } };
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        SupportLogger.Shared.Log($"/1/profile → HTTP {(int)response.StatusCode}", KDriveApiClient.LogCategory);
        if (!WebDavHttp.IsSuccess(response))
        {
            throw (int)response.StatusCode == 401 ? CloudProviderException.InvalidCredentials() : CloudProviderException.InvalidResponse();
        }

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var data = document.RootElement.GetProperty("data");
            string? Text(string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long? Number(string name) => data.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;
            return new Profile(
                Text("email") ?? "Unknown",
                Text("display_name") ?? string.Empty,
                Number("current_account_id") ?? Number("id") ?? 0);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    /// <summary><c>/2/drive?account_id=…</c>: every drive (workspace) of the account.</summary>
    private async Task<IReadOnlyList<KDriveDrive>> FetchDrivesAsync(string token, long accountId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{KDriveApiClient.ApiBase}/2/drive?account_id={accountId}") { Headers = { Authorization = CloudHttp.Bearer(token) } };
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        SupportLogger.Shared.Log($"/2/drive?account_id={accountId} → HTTP {status}", KDriveApiClient.LogCategory);
        if (status == 401)
        {
            throw CloudProviderException.InvalidCredentials();
        }

        if (!WebDavHttp.IsSuccess(response))
        {
            throw CloudProviderException.ServerError(status);
        }

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return document.RootElement.GetProperty("data").Deserialize<List<KDriveDrive>>(CloudHttp.Json) ?? [];
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    // MARK: - File operations

    public override async Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var client = Require();
        var folder = CloudPath.Normalize(path);
        var folderId = await client.ResolvePathToIdAsync(folder, cancellationToken).ConfigureAwait(false);
        var children = await client.ListFolderAsync(folderId, cancellationToken).ConfigureAwait(false);

        var items = new List<CloudFileItem>(children.Count);
        foreach (var child in children)
        {
            var item = child.ToItem(folder);
            client.CachePath(item.Path, child.Id);
            items.Add(item);
        }

        // Complete listing (every page): children that vanished elsewhere leave the cache.
        client.RetainChildren(folder, children.Select(c => c.Name).ToHashSet(StringComparer.Ordinal));
        return items;
    }

    public override async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var client = Require();
        var path = CloudPath.Normalize(remotePath);
        var fileId = await client.ResolvePathToIdAsync(path, cancellationToken).ConfigureAwait(false);
        await client.DownloadFileAsync(fileId, localPath, onBytes, path, cancellationToken).ConfigureAwait(false);
    }

    public override async Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var client = Require();
        var target = CloudPath.Normalize(remotePath);
        var folderId = await client.ResolvePathToIdAsync(CloudPath.Parent(target), cancellationToken).ConfigureAwait(false);
        await client.UploadFileAsync(localPath, folderId, CloudPath.Name(target), onBytes, target, cancellationToken).ConfigureAwait(false);
    }

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
        => Require().DeleteAsync(path, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Require().CreateFolderAsync(path, cancellationToken);

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => Require().RenameAsync(path, newName, cancellationToken);

    // MoveItemAsync / CopyItemAsync: not offered. The v2 move endpoint answered 404 against a
    // live account on macOS and the shape Infomaniak accepts now isn't documented, so the
    // default NotImplemented makes callers fall back to download + upload. The client keeps
    // MoveAsync/DuplicateAsync for when the endpoint is confirmed.

    public override async Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var client = Require();
        var target = CloudPath.Normalize(path);
        var fileId = await client.ResolvePathToIdAsync(target, cancellationToken).ConfigureAwait(false);
        var metadata = await client.StatAsync(fileId, target, cancellationToken).ConfigureAwait(false);
        return metadata.ToItem(CloudPath.Parent(target));
    }

    public override async Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        var client = Require();
        var folderId = await client.ResolvePathToIdAsync(path, cancellationToken).ConfigureAwait(false);
        return await client.FolderSizeAsync(folderId, cancellationToken).ConfigureAwait(false);
    }

    public override Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
        => _client is { } client ? client.StorageQuotaAsync(cancellationToken) : Task.FromResult<CloudStorageQuota?>(null);

    // MARK: - Share links

    /// <summary>
    /// The full set, but expiry (<c>valid_until</c>) is a paid kSuite feature that free
    /// workspaces reject. The control is still offered and the server explains a refusal —
    /// the only place that distinction is knowable.
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
        ExpiryRequiresPaidPlan = true,
    };

    public override Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => Require().CreateShareLinkAsync(path, options, cancellationToken);

    public override Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => Require().ExistingShareLinkAsync(path, cancellationToken);

    public override Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => Require().UpdateShareLinkAsync(path, options, cancellationToken);

    public override Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken = default)
        => Require().RemoveShareLinkAsync(path, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
        }

        base.Dispose(disposing);
    }
}
