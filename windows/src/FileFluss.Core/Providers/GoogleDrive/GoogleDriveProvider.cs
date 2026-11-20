// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Net;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.GoogleDrive;

/// <summary>Persisted credentials of a full-access Google Drive account (macOS <c>GoogleDriveCredentials</c>).</summary>
internal sealed record GoogleDriveCredentials(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string UserEmail, string DisplayName);

/// <summary>
/// "Google Drive" — full <c>drive</c> scope (Project A, see <see cref="GoogleOAuthClient"/>).
///
/// <para>Drive addresses items by id, not path, so paths are resolved by walking the tree
/// one name query per component and cached (path → id, mimeType, shared-drive id). Drive
/// allows duplicate names in one folder; the first match wins, as on macOS.</para>
///
/// <para>The user's shared drives appear under a synthetic top-level <c>/Shared drives</c>
/// folder, mirroring Drive's own web UI; My Drive stays at the root so existing
/// <c>/Foo</c> paths (favourites, sync rules) keep resolving.</para>
/// </summary>
[CloudProvider(CloudProviderType.GoogleDrive)]
public sealed class GoogleDriveProvider : CloudProvider
{
    /// <summary>Name of the synthetic container that groups the user's shared drives.</summary>
    internal const string SharedDrivesFolderName = "Shared drives";

    /// <summary>Id of that container. Never sent to the API — operations on virtual nodes are refused first.</summary>
    internal const string SharedDrivesSentinelId = "filefluss-shared-drives-root";

    private const string LogCategory = "googleDrive";
    private const string SharedDrivesPath = "/" + SharedDrivesFolderName;

    /// <summary>
    /// Workspace documents have no bytes of their own; they are exported on download and the
    /// local file gets the matching extension ("Report" → "Report.docx").
    /// </summary>
    private static readonly Dictionary<string, (string MimeType, string Extension)> ExportFormats = new(StringComparer.Ordinal)
    {
        ["application/vnd.google-apps.document"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "docx"),
        ["application/vnd.google-apps.spreadsheet"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx"),
        ["application/vnd.google-apps.presentation"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", "pptx"),
        ["application/vnd.google-apps.drawing"] = ("application/pdf", "pdf"),
    };

    private static readonly CachedFile RootEntry = new("root", GoogleDriveApi.FolderMimeType);

    private readonly HttpClient _http;

    /// <summary>
    /// Path → (id, mimeType, shared-drive id). Not pruned against listings
    /// (<c>RetainPathChildren</c>) on purpose: a listing right after an upload can miss the
    /// new file through Drive's index lag, and dropping its id here would make the next
    /// replace POST a duplicate instead of PATCHing it.
    /// </summary>
    private readonly ConcurrentDictionary<string, CachedFile> _pathCache = new(StringComparer.Ordinal);

    /// <summary>The user's shared drives. Rarely change: the root listing reuses this, the "Shared drives" listing refreshes it.</summary>
    private volatile IReadOnlyList<SharedDrive>? _sharedDrives;

    private volatile GoogleTokenSession? _session;

    public GoogleDriveProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal GoogleDriveProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _http = handler is null ? CloudHttp.Create() : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _pathCache["/"] = RootEntry;
        if (Credentials.Load<GoogleDriveCredentials>(CredentialKey) is { } stored)
        {
            _session = CreateSession(stored);
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.GoogleDrive;

    protected override string CredentialKeyPrefix => "googledrive";

    public override bool IsAuthenticated => _session is not null;

    /// <summary>Test seam: the browser round trip. Defaults to the loopback listener.</summary>
    internal OAuthRunner Authenticator { get; set; } = (url, ct) => OAuthLoopback.AuthenticateAsync(url, ct);

    /// <summary>Test seam: base delay of the upload 404 retry and the new-folder poll.</summary>
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    internal TimeSpan FolderPollDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    // MARK: - Connecting

    /// <summary>
    /// Add: the browser explanation plus the orange notice that this full-access option is
    /// at Google's 100-user cap. Edit (stored credentials): Re-authorize, as on macOS.
    /// </summary>
    public override ConnectSpec ConnectSpec => IsAuthenticated
        ? ConnectSpec.Browser(
            ConnectField.Info("Click Re-authorize to sign in again. Your browser will open and the existing token is replaced once you complete the flow."))
        : ConnectSpec.Browser(
            ConnectField.Info("Click Connect to sign in with your Google account. Your browser will open for authentication."),
            ConnectField.Warning("This full-access Google Drive option has reached its user limit. New users should use “Google Drive (Selected Folders)” instead."));

    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        prompt.ShowStatus(Localization.L("Waiting for sign-in in browser…"));
        try
        {
            var signIn = await GoogleOAuthClient.FullDrive.SignInAsync(_http, Authenticator, cancellationToken).ConfigureAwait(false);
            var credentials = new GoogleDriveCredentials(
                signIn.Token.AccessToken, signIn.Token.RefreshToken, signIn.Token.ExpiresAt, signIn.UserEmail, signIn.DisplayName);

            // Only now, with a working token in hand, replace what was stored. The session is
            // swapped first so a refresh still in flight on the old one can't overwrite it.
            _session = CreateSession(credentials);
            ResetCaches();
            Credentials.Save(CredentialKey, credentials);
            SupportLogger.Shared.Log("Signed in to Google Drive", LogCategory);
            return new ConnectResult(credentials.DisplayName);
        }
        finally
        {
            prompt.ShowStatus(null);
        }
    }

    public override Task DisconnectAsync()
    {
        _session = null;
        ResetCaches();
        return base.DisconnectAsync();
    }

    private GoogleTokenSession CreateSession(GoogleDriveCredentials stored)
        => new(
            _http,
            GoogleOAuthClient.FullDrive,
            new GoogleToken(stored.AccessToken, stored.RefreshToken, stored.ExpiresAt),
            stored.UserEmail,
            stored.DisplayName,
            PersistRefreshed);

    /// <summary>Saves a refreshed token at once — unless the account was disconnected or re-signed meanwhile.</summary>
    private void PersistRefreshed(GoogleTokenSession session)
    {
        if (!ReferenceEquals(_session, session))
        {
            return;
        }

        var token = session.Token;
        Credentials.Save(CredentialKey, new GoogleDriveCredentials(token.AccessToken, token.RefreshToken, token.ExpiresAt, session.UserEmail, session.DisplayName));
    }

    private void ResetCaches()
    {
        _pathCache.Clear();
        _pathCache["/"] = RootEntry;
        _sharedDrives = null;
    }

    private GoogleTokenSession RequireSession() => _session ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - Listing

    public override async Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        RequireSession();
        path = GoogleDriveApi.Trim(path);

        // The virtual container lists the shared drives rather than querying the files API.
        if (path == SharedDrivesPath)
        {
            return await ListSharedDrivesAsFoldersAsync(cancellationToken).ConfigureAwait(false);
        }

        var folder = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        var files = new List<GoogleDriveFile>();
        string? pageToken = null;
        var guard = new PageGuard("Google Drive listing");
        do
        {
            var query = new List<(string, string)>
            {
                ("q", $"'{folder.Id}' in parents and trashed = false"),
                ("fields", "nextPageToken,files(id,name,mimeType,size,modifiedTime,md5Checksum)"),
                ("pageSize", "1000"),
                ("orderBy", "folder,name"),
            };
            query.AddRange(DriveQuery(folder.DriveId));
            if (pageToken is not null)
            {
                query.Add(("pageToken", pageToken));
            }

            var page = await ApiAsync<GoogleFileList>(HttpMethod.Get, "/files", query, null, cancellationToken).ConfigureAwait(false);
            files.AddRange(page.Files ?? []);
            pageToken = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
            if (pageToken is not null)
            {
                guard.Follow(pageToken);
            }
        }
        while (pageToken is not null);

        // Children inherit the folder's drive scope so their own listings stay corpus-scoped.
        foreach (var file in files)
        {
            _pathCache[GoogleDriveApi.Child(path, file.Name)] = new CachedFile(file.Id, file.MimeType, folder.DriveId);
        }

        var items = files.Select(f => f.ToItem(path)).ToList();

        // "Shared drives" appears at the My Drive root only when the user belongs to one.
        if (path == "/")
        {
            try
            {
                if ((await ListSharedDrivesAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false)).Count > 0)
                {
                    items.Add(SharedDrivesContainerItem());
                }
            }
            catch (CloudProviderException e)
            {
                SupportLogger.Shared.Log($"Listing shared drives failed: {e.Message}", LogCategory, LogLevel.Notice);
            }
        }

        return items;
    }

    /// <summary>The shared drives as folders under "/Shared drives", priming the path cache so descending is one hop.</summary>
    private async Task<IReadOnlyList<CloudFileItem>> ListSharedDrivesAsFoldersAsync(CancellationToken cancellationToken)
    {
        var drives = await ListSharedDrivesAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
        var items = new List<CloudFileItem>(drives.Count);
        foreach (var drive in drives)
        {
            var drivePath = GoogleDriveApi.Child(SharedDrivesPath, drive.Name);
            _pathCache[drivePath] = new CachedFile(drive.Id, GoogleDriveApi.FolderMimeType, drive.Id);
            items.Add(new CloudFileItem("d" + drive.Id, drive.Name, drivePath, true, 0, CloudFileItem.UnknownDate)
            {
                IconOverride = "drive",
                Role = CloudItemRole.DriveRoot,
            });
        }

        return items;
    }

    private static CloudFileItem SharedDrivesContainerItem()
        => new("d" + SharedDrivesSentinelId, SharedDrivesFolderName, SharedDrivesPath, true, 0, CloudFileItem.UnknownDate)
        {
            IconOverride = "drive",
            Role = CloudItemRole.VirtualContainer,
        };

    /// <summary>The shared drives the user is a member of; cached unless <paramref name="forceRefresh"/>.</summary>
    private async Task<IReadOnlyList<SharedDrive>> ListSharedDrivesAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _sharedDrives is { } cached)
        {
            return cached;
        }

        var drives = new List<SharedDrive>();
        string? pageToken = null;
        var guard = new PageGuard("Google Drive listing");
        do
        {
            var query = new List<(string, string)> { ("pageSize", "100") };
            if (pageToken is not null)
            {
                query.Add(("pageToken", pageToken));
            }

            var page = await ApiAsync<SharedDriveList>(HttpMethod.Get, "/drives", query, null, cancellationToken).ConfigureAwait(false);
            drives.AddRange((page.Drives ?? []).Select(d => new SharedDrive(d.Id, d.Name)));
            pageToken = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
            if (pageToken is not null)
            {
                guard.Follow(pageToken);
            }
        }
        while (pageToken is not null);

        _sharedDrives = drives;
        return drives;
    }

    /// <summary>
    /// <c>supportsAllDrives</c> declares the client understands shared drives (otherwise
    /// shared-drive items are rejected). Inside a shared drive the request also scopes the
    /// corpus to it; My Drive keeps the default corpus so shared-drive items don't leak in.
    /// </summary>
    private static IEnumerable<(string, string)> DriveQuery(string? driveId)
    {
        yield return ("supportsAllDrives", "true");
        if (driveId is not null)
        {
            yield return ("includeItemsFromAllDrives", "true");
            yield return ("corpora", "drive");
            yield return ("driveId", driveId);
        }
    }

    /// <summary>
    /// "/Shared drives" and "/Shared drives/&lt;name&gt;": neither can be created, renamed or
    /// deleted through the files API. Refusing them up front turns a would-be attempt to
    /// operate on (or destroy) a whole shared drive into a clear error.
    /// </summary>
    private static bool IsVirtualSharedDriveNode(string path)
    {
        var components = GoogleDriveApi.Components(path);
        return components.Length is >= 1 and <= 2 && components[0] == SharedDrivesFolderName;
    }

    // MARK: - Path resolution

    /// <summary>
    /// Resolves a path to its cached entry by walking each component with a name query
    /// inside its parent. Below "/Shared drives/&lt;name&gt;" (whose id is the shared drive's
    /// id, doubling as its root folder) entries carry that drive id.
    /// </summary>
    private async Task<CachedFile> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        path = GoogleDriveApi.Trim(path);
        if (_pathCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var components = GoogleDriveApi.Components(path);
        if (components.Length == 0)
        {
            return RootEntry;
        }

        if (components[0] == SharedDrivesFolderName)
        {
            return await ResolveSharedDrivePathAsync(components, cancellationToken).ConfigureAwait(false);
        }

        return await WalkAsync(components, 0, RootEntry, string.Empty, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CachedFile> ResolveSharedDrivePathAsync(string[] components, CancellationToken cancellationToken)
    {
        if (components.Length == 1)
        {
            return new CachedFile(SharedDrivesSentinelId, GoogleDriveApi.FolderMimeType);
        }

        var driveName = components[1];
        var drivePath = GoogleDriveApi.Child(SharedDrivesPath, driveName);
        if (!_pathCache.TryGetValue(drivePath, out var driveEntry))
        {
            var drive = (await ListSharedDrivesAsync(false, cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.Name == driveName)
                // The cached list may predate a newly added drive.
                ?? (await ListSharedDrivesAsync(true, cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.Name == driveName)
                ?? throw CloudProviderException.NotFound(drivePath);
            driveEntry = new CachedFile(drive.Id, GoogleDriveApi.FolderMimeType, drive.Id);
            _pathCache[drivePath] = driveEntry;
        }

        return components.Length == 2
            ? driveEntry
            : await WalkAsync(components, 2, driveEntry, drivePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CachedFile> WalkAsync(string[] components, int start, CachedFile parent, string basePath, CancellationToken cancellationToken)
    {
        var current = parent;
        var currentPath = basePath;
        for (var i = start; i < components.Length; i++)
        {
            var name = components[i];
            currentPath += "/" + name;
            if (_pathCache.TryGetValue(currentPath, out var hit))
            {
                current = hit;
                continue;
            }

            var query = new List<(string, string)>
            {
                ("q", $"'{current.Id}' in parents and name = '{GoogleDriveApi.QueryLiteral(name)}' and trashed = false"),
                ("fields", "files(id,name,mimeType)"),
                ("pageSize", "1"),
            };
            query.AddRange(DriveQuery(current.DriveId));
            var page = await ApiAsync<GoogleFileList>(HttpMethod.Get, "/files", query, null, cancellationToken).ConfigureAwait(false);
            var file = page.Files?.FirstOrDefault() ?? throw CloudProviderException.NotFound(currentPath);
            current = new CachedFile(file.Id, file.MimeType, current.DriveId);
            _pathCache[currentPath] = current;
        }

        return current;
    }

    // MARK: - Download

    /// <summary>The export extension of a Workspace document (the listing that produced the item filled the path cache).</summary>
    public override async ValueTask<string?> ExportExtensionAsync(CloudFileItem item, CancellationToken cancellationToken = default)
    {
        if (item.IsDirectory || IsVirtualSharedDriveNode(item.Path))
        {
            return null;
        }

        var file = await ResolveAsync(item.Path, cancellationToken).ConfigureAwait(false);
        return ExportFormats.TryGetValue(file.MimeType, out var export) ? export.Extension : null;
    }

    public override async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var session = RequireSession();
        var file = await ResolveAsync(remotePath, cancellationToken).ConfigureAwait(false);

        string url;
        var target = localPath;
        if (ExportFormats.TryGetValue(file.MimeType, out var export))
        {
            url = $"{GoogleDriveApi.ApiBase}/files/{CloudHttp.Escape(file.Id)}/export?mimeType={CloudHttp.Escape(export.MimeType)}&supportsAllDrives=true";
            target = localPath + "." + export.Extension;
        }
        else
        {
            url = $"{GoogleDriveApi.ApiBase}/files/{CloudHttp.Escape(file.Id)}?alt=media&supportsAllDrives=true";
        }

        var accessToken = await session.AccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = CloudHttp.Bearer(accessToken);
        using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.ReadBodySnippetAsync(cancellationToken).ConfigureAwait(false);
            SupportLogger.Shared.Log($"Download failed: HTTP {(int)response.StatusCode}: {body}", LogCategory, LogLevel.Error);
            throw MapError((int)response.StatusCode, GoogleDriveApi.ParseErrorMessage(body), remotePath);
        }

        await HttpTransfer.SaveToFileAsync(response, target, onBytes, cancellationToken).ConfigureAwait(false);
    }

    // MARK: - Upload

    public override async Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var session = RequireSession();
        remotePath = GoogleDriveApi.Trim(remotePath);
        var parentPath = GetParentPath(remotePath);
        var fileName = GetName(remotePath);

        // Files belong inside a specific shared drive, not in the synthetic container.
        if (parentPath == SharedDrivesPath)
        {
            throw CloudProviderException.NotImplemented();
        }

        var parent = await ResolveAsync(parentPath, cancellationToken).ConfigureAwait(false);

        // Prefer the cached id (from a listing or an earlier upload): a Drive query for a
        // just-uploaded file can miss through index lag, and then the next replace would POST
        // a duplicate. Query only when the file isn't cached at all.
        string? existingId;
        if (_pathCache.TryGetValue(remotePath, out var cached))
        {
            existingId = cached.Id;
        }
        else
        {
            try
            {
                existingId = await FindFileIdAsync(parent, fileName, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException)
            {
                existingId = null;
            }
        }

        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(localPath));
        var created = new DateTimeOffset(File.GetCreationTimeUtc(localPath));

        var uploader = new GoogleDriveUploader(_http, session.AccessTokenAsync, MapUploadError, LogCategory)
        {
            RetryNotFound = true,
            RetryDelay = RetryDelay,
        };
        var uploaded = await uploader.UploadAsync(localPath, fileName, parent.Id, existingId, modified, created, onBytes, cancellationToken).ConfigureAwait(false);

        // Cache the id so a following replace/delete needn't query (index lag again), in the
        // parent's drive scope.
        _pathCache[remotePath] = new CachedFile(uploaded.Id, uploaded.MimeType, parent.DriveId);

        // A replacement couldn't carry the timestamps (403 on the upload PATCH); stamp them now.
        if (existingId is not null)
        {
            try
            {
                await PatchTimestampsAsync(uploaded.Id, modified, created, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException e)
            {
                SupportLogger.Shared.Log($"Timestamp PATCH failed: {e.Message}", LogCategory, LogLevel.Notice);
            }
        }
    }

    /// <summary>A file's id by name within a parent, without populating the folder cache; null when absent.</summary>
    private async Task<string?> FindFileIdAsync(CachedFile parent, string name, CancellationToken cancellationToken)
    {
        var query = new List<(string, string)>
        {
            ("q", $"'{parent.Id}' in parents and name = '{GoogleDriveApi.QueryLiteral(name)}' and trashed = false"),
            ("fields", "files(id)"),
            ("pageSize", "1"),
        };
        query.AddRange(DriveQuery(parent.DriveId));
        var page = await ApiAsync<GoogleFileList>(HttpMethod.Get, "/files", query, null, cancellationToken).ConfigureAwait(false);
        return page.Files?.FirstOrDefault()?.Id;
    }

    /// <summary>Path-keyed timestamp stamping, so a cross-account copy keeps the source's mtime.</summary>
    public override async Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
    {
        RequireSession();
        var file = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        await PatchTimestampsAsync(file.Id, date, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Metadata-only PATCH of modifiedTime/createdTime. A refusal is logged and ignored, as on macOS.</summary>
    private async Task PatchTimestampsAsync(string fileId, DateTimeOffset? modified, DateTimeOffset? created, CancellationToken cancellationToken)
    {
        var accessToken = await RequireSession().AccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{GoogleDriveApi.ApiBase}/files/{CloudHttp.Escape(fileId)}?supportsAllDrives=true")
        {
            Content = new StringContent(GoogleDriveApi.BuildMetadata(null, null, modified, created), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = CloudHttp.Bearer(accessToken);
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"Timestamp PATCH ignored: HTTP {(int)response.StatusCode}", LogCategory, LogLevel.Notice);
        }
    }

    // MARK: - Mutations

    public override async Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
    {
        RequireSession();
        path = GoogleDriveApi.Trim(path);
        if (IsVirtualSharedDriveNode(path))
        {
            throw CloudProviderException.NotImplemented();
        }

        var file = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        await ApiVoidAsync(HttpMethod.Delete, $"/files/{CloudHttp.Escape(file.Id)}", path, cancellationToken).ConfigureAwait(false);

        // A folder takes its subtree with it: evicting only the exact key would leave the
        // children mapped to dead ids, and a later op at a re-created path would get
        // "File not found: <id>".
        _pathCache.RemovePathSubtree(path);
    }

    public override async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        RequireSession();
        path = GoogleDriveApi.Trim(path);
        var parentPath = GetParentPath(path);

        // Creating a shared drive is out of scope for a folder-create.
        if (IsVirtualSharedDriveNode(path) || parentPath == SharedDrivesPath)
        {
            throw CloudProviderException.NotImplemented();
        }

        // Idempotent: Drive allows duplicate folder names, so a naive create would multiply
        // folders on every retry. Done when something already resolves at this path.
        try
        {
            await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
        }

        var parent = await ResolveAsync(parentPath, cancellationToken).ConfigureAwait(false);
        var created = await ApiAsync<GoogleDriveFile>(
            HttpMethod.Post,
            "/files",
            [],
            new { name = GetName(path), mimeType = GoogleDriveApi.FolderMimeType, parents = new[] { parent.Id } },
            cancellationToken).ConfigureAwait(false);

        // Inherit the parent's drive scope (macOS dropped it here, so a folder created in a
        // shared drive listed empty until the cache was rebuilt).
        _pathCache[path] = new CachedFile(created.Id, created.MimeType, parent.DriveId);

        // Drive occasionally hands back a folder id that isn't usable as an upload parent yet
        // (the upload then answers 404 "File not found: <id>"). Poll until it is queryable,
        // about 2.4 s at most.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(FolderPollDelay * attempt, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await ApiAsync<GoogleDriveFile>(HttpMethod.Get, $"/files/{CloudHttp.Escape(created.Id)}", [("fields", "id,name,mimeType")], null, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
            {
            }
        }
    }

    public override async Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
    {
        RequireSession();
        path = GoogleDriveApi.Trim(path);
        if (IsVirtualSharedDriveNode(path))
        {
            throw CloudProviderException.NotImplemented();
        }

        var file = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        await ApiAsync<GoogleDriveFile>(HttpMethod.Patch, $"/files/{CloudHttp.Escape(file.Id)}", [], new { name = newName }, cancellationToken, itemPath: path).ConfigureAwait(false);

        // Every descendant's path changed with a folder rename, so the old subtree goes. The
        // item keeps its id: re-key just that entry; descendants re-resolve lazily.
        _pathCache.RemovePathSubtree(path);
        _pathCache[GoogleDriveApi.Child(GetParentPath(path), newName)] = file;
    }

    public override async Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        RequireSession();
        path = GoogleDriveApi.Trim(path);

        // The sentinel id is never sent to the API.
        if (path == SharedDrivesPath)
        {
            return SharedDrivesContainerItem();
        }

        var file = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        var metadata = await ApiAsync<GoogleDriveFile>(
            HttpMethod.Get,
            $"/files/{CloudHttp.Escape(file.Id)}",
            [("fields", "id,name,mimeType,size,modifiedTime,md5Checksum")],
            null,
            cancellationToken,
            itemPath: path).ConfigureAwait(false);
        return metadata.ToItem(GetParentPath(path));
    }

    // MARK: - Search

    /// <summary>
    /// Name search, scoped to a folder's direct children when <paramref name="path"/> is
    /// given. Capped at about 500 results.
    /// </summary>
    public override async Task<IReadOnlyList<CloudFileItem>?> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken = default)
    {
        RequireSession();
        var literal = GoogleDriveApi.QueryLiteral(query);
        var q = $"name contains '{literal}' and trashed = false";
        string? driveId = null;
        var scope = path is null ? "/" : GoogleDriveApi.Trim(path);
        if (scope != "/")
        {
            // The synthetic container has nothing searchable of its own.
            if (scope == SharedDrivesPath)
            {
                return [];
            }

            var folder = await ResolveAsync(scope, cancellationToken).ConfigureAwait(false);
            driveId = folder.DriveId;
            q = $"'{folder.Id}' in parents and name contains '{literal}' and trashed = false";
        }

        var files = new List<GoogleDriveFile>();
        string? pageToken = null;
        var guard = new PageGuard("Google Drive listing");
        do
        {
            var items = new List<(string, string)>
            {
                ("q", q),
                ("fields", "nextPageToken,files(id,name,mimeType,size,modifiedTime,md5Checksum,parents)"),
                ("pageSize", "100"),
            };
            items.AddRange(DriveQuery(driveId));
            if (pageToken is not null)
            {
                items.Add(("pageToken", pageToken));
            }

            var page = await ApiAsync<GoogleFileList>(HttpMethod.Get, "/files", items, null, cancellationToken).ConfigureAwait(false);
            files.AddRange(page.Files ?? []);
            pageToken = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
            if (pageToken is not null)
            {
                guard.Follow(pageToken);
            }
        }
        while (pageToken is not null && files.Count < 500);

        // A scoped search only matches direct children, so their folder is the scope. A
        // global hit's folder is taken from the path cache when known; macOS placed every
        // hit at "/".
        return files.Select(f => f.ToItem(scope != "/" ? scope : CachedPathOf(f.Parents?.FirstOrDefault()) ?? "/")).ToList();
    }

    private string? CachedPathOf(string? id)
        => id is null ? null : _pathCache.FirstOrDefault(entry => entry.Value.Id == id).Key;

    // MARK: - Quota

    public override async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not { } session)
        {
            return null;
        }

        var accessToken = await session.AccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return await GoogleDriveApi.StorageQuotaAsync(_http, accessToken, cancellationToken).ConfigureAwait(false);
    }

    // MARK: - Share links

    /// <summary>
    /// Drive's "anyone with the link" permission has no password, no expiry (those exist only
    /// on permissions for a named person) and no view-without-download mode, so there is
    /// nothing an update could change. Creating, reading back and revoking work; a Workspace
    /// admin can still forbid sharing, which surfaces as Google's own message.
    /// </summary>
    public override ShareLinkCapabilities ShareLinkCapabilities { get; } = new()
    {
        CanCreate = true,
        CanQueryExisting = true,
        CanUpdate = false,
        CanRemove = true,
    };

    /// <summary>Grants the <c>anyone</c>/<c>reader</c> permission and reads back Drive's two link forms. <paramref name="options"/> has nothing Drive could honour.</summary>
    public override async Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
    {
        var fileId = await ShareableFileIdAsync(path, cancellationToken).ConfigureAwait(false);

        // Drive doesn't support concurrent permission changes on one file: the metadata read
        // starts only after the permission write returned.
        await ApiAsync<PermissionBody>(
            HttpMethod.Post,
            $"/files/{CloudHttp.Escape(fileId)}/permissions",
            [("supportsAllDrives", "true")],
            new { role = "reader", type = "anyone" },
            cancellationToken,
            surfaceServerMessage: true).ConfigureAwait(false);

        return await ShareLinkAsync(fileId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The link, when the file carries the public <c>anyone</c> permission. <c>webViewLink</c>
    /// exists whether or not a file is shared, so only the permissions list can answer.
    /// </summary>
    public override async Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken = default)
    {
        var fileId = await ShareableFileIdAsync(path, cancellationToken).ConfigureAwait(false);
        return await AnyonePermissionIdAsync(fileId, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await ShareLinkAsync(fileId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Revokes the public permission only; permissions for named people stay.</summary>
    public override async Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken = default)
    {
        var fileId = await ShareableFileIdAsync(path, cancellationToken).ConfigureAwait(false);
        var permissionId = await AnyonePermissionIdAsync(fileId, cancellationToken).ConfigureAwait(false)
            ?? throw CloudProviderException.CommandFailed(Localization.L("This file isn't shared with anyone who has the link."));
        await ApiVoidAsync(HttpMethod.Delete, $"/files/{CloudHttp.Escape(fileId)}/permissions/{CloudHttp.Escape(permissionId)}", path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ShareableFileIdAsync(string path, CancellationToken cancellationToken)
    {
        RequireSession();
        path = GoogleDriveApi.Trim(path);
        if (IsVirtualSharedDriveNode(path))
        {
            throw CloudProviderException.NotImplemented();
        }

        return (await ResolveAsync(path, cancellationToken).ConfigureAwait(false)).Id;
    }

    /// <summary>The id of the file's <c>type: anyone</c> permission, or null. 403/404 stay errors with Google's message.</summary>
    private async Task<string?> AnyonePermissionIdAsync(string fileId, CancellationToken cancellationToken)
    {
        var list = await ApiAsync<PermissionList>(
            HttpMethod.Get,
            $"/files/{CloudHttp.Escape(fileId)}/permissions",
            [("supportsAllDrives", "true"), ("fields", "permissions(id,type,role)")],
            null,
            cancellationToken,
            surfaceServerMessage: true).ConfigureAwait(false);
        return (list.Permissions ?? []).FirstOrDefault(p => string.Equals(p.Type, "anyone", StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>
    /// <c>webContentLink</c> exists only for files with bytes behind them; Docs/Sheets/Slides
    /// come back with the landing page alone.
    /// </summary>
    private async Task<CloudShareLink> ShareLinkAsync(string fileId, CancellationToken cancellationToken)
    {
        var links = await ApiAsync<FileLinks>(
            HttpMethod.Get,
            $"/files/{CloudHttp.Escape(fileId)}",
            [("supportsAllDrives", "true"), ("fields", "webViewLink,webContentLink")],
            null,
            cancellationToken,
            surfaceServerMessage: true).ConfigureAwait(false);

        if (!Uri.TryCreate(links.WebViewLink, UriKind.Absolute, out var view))
        {
            throw CloudProviderException.InvalidResponse();
        }

        return new CloudShareLink(view)
        {
            DirectDownloadUrl = Uri.TryCreate(links.WebContentLink, UriKind.Absolute, out var direct) ? direct : null,
        };
    }

    // MARK: - HTTP

    /// <summary>
    /// A Drive API call. Every <c>/files</c> call declares shared-drive support. A 401 forces
    /// one token refresh and a retry — Google can revoke an access token before its expiry.
    /// </summary>
    /// <param name="surfaceServerMessage">
    /// Throw <see cref="CloudErrorKind.CommandFailed"/> with Google's own message. The
    /// sharing path uses it: "your admin has disabled link sharing" is only explicable
    /// server-side.
    /// </param>
    private async Task<T> ApiAsync<T>(
        HttpMethod method,
        string apiPath,
        IEnumerable<(string Key, string Value)> query,
        object? body,
        CancellationToken cancellationToken,
        bool surfaceServerMessage = false,
        string? itemPath = null)
    {
        using var response = await SendAsync(method, apiPath, query, body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, method, apiPath, surfaceServerMessage, itemPath, cancellationToken).ConfigureAwait(false);
        }

        return await response.ReadJsonAsync<T>(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApiVoidAsync(HttpMethod method, string apiPath, string? itemPath, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, apiPath, [], null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, method, apiPath, false, itemPath, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string apiPath, IEnumerable<(string Key, string Value)> query, object? body, CancellationToken cancellationToken)
    {
        var session = RequireSession();
        var items = query.ToList();
        if (apiPath.StartsWith("/files", StringComparison.Ordinal) && !items.Any(i => i.Key == "supportsAllDrives"))
        {
            items.Add(("supportsAllDrives", "true"));
        }

        var url = GoogleDriveApi.ApiBase + apiPath + (items.Count > 0 ? "?" + GoogleDriveApi.QueryString(items) : string.Empty);
        var accessToken = await session.AccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendOnceAsync(method, url, body, accessToken, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            SupportLogger.Shared.Log($"{method} {apiPath} → 401, force-refreshing and retrying once", LogCategory);
            response.Dispose();
            accessToken = await session.RefreshAfterRejectionAsync(accessToken, cancellationToken).ConfigureAwait(false);
            response = await SendOnceAsync(method, url, body, accessToken, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string url, object? body, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = CloudHttp.Bearer(accessToken);
        if (body is not null)
        {
            request.Content = CloudHttp.JsonContent(body);
        }

        return await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CloudProviderException> FailureAsync(
        HttpResponseMessage response, HttpMethod method, string apiPath, bool surfaceServerMessage, string? itemPath, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var body = await response.ReadBodySnippetAsync(cancellationToken, 1000).ConfigureAwait(false);
        SupportLogger.Shared.Log($"{method} {apiPath} → HTTP {status}: {body}", LogCategory, LogLevel.Error);
        var message = GoogleDriveApi.ParseErrorMessage(body);
        if (surfaceServerMessage && !string.IsNullOrEmpty(message))
        {
            return CloudProviderException.CommandFailed(message);
        }

        return MapError(status, message, itemPath);
    }

    private static CloudProviderException MapUploadError(int status, string body)
        => MapError(status, GoogleDriveApi.ParseErrorMessage(body), null);

    /// <summary>
    /// The macOS mapping. A 403 that carries Google's explanation ("Drive API not enabled",
    /// "insufficient permissions") is a server error, a bare 403 an authorization problem.
    /// </summary>
    internal static CloudProviderException MapError(int status, string? googleMessage, string? itemPath) => status switch
    {
        401 => CloudProviderException.Unauthorized(),
        403 => googleMessage is not null ? CloudProviderException.ServerError(403) : CloudProviderException.Unauthorized(),
        404 => CloudProviderException.NotFound(itemPath ?? googleMessage ?? "Resource not found"),
        429 => CloudProviderException.RateLimited(),
        507 => CloudProviderException.QuotaExceeded(),
        _ => CloudProviderException.ServerError(status),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
        }

        base.Dispose(disposing);
    }

    // MARK: - Types

    /// <param name="DriveId">The shared drive the entry lives in; null for My Drive. Threaded through resolution so queries scope to the right corpus.</param>
    private readonly record struct CachedFile(string Id, string MimeType, string? DriveId = null);

    private sealed record SharedDrive(string Id, string Name);

    private sealed class SharedDriveList
    {
        public IReadOnlyList<DriveBody>? Drives { get; init; }

        public string? NextPageToken { get; init; }

        internal sealed class DriveBody
        {
            public string Id { get; init; } = string.Empty;

            public string Name { get; init; } = string.Empty;
        }
    }

    private sealed class PermissionBody
    {
        public string? Id { get; init; }
    }

    private sealed class PermissionList
    {
        /// <summary>Absent rather than empty on some responses.</summary>
        public IReadOnlyList<Permission>? Permissions { get; init; }

        internal sealed class Permission
        {
            public string? Id { get; init; }

            public string? Type { get; init; }

            public string? Role { get; init; }
        }
    }

    private sealed class FileLinks
    {
        public string? WebViewLink { get; init; }

        public string? WebContentLink { get; init; }
    }
}
