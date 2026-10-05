// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Security;

namespace FileFluss.Core.Providers.GoogleDrive;

/// <summary>
/// A folder the user granted access to through the Google Picker. With the
/// <c>drive.file</c> scope these are the only items the app can see; a picked folder grants
/// access to what FileFluss puts in it.
/// </summary>
internal sealed record GoogleDrivePickedRoot(string Id, string Name);

/// <summary>Persisted credentials of a "Google Drive (Selected Folders)" account, with its picked roots.</summary>
internal sealed record GoogleDrivePickerCredentials(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string UserEmail,
    string DisplayName,
    IReadOnlyList<GoogleDrivePickedRoot> Roots);

/// <summary>
/// "Google Drive (Selected Folders)" — Project B, the non-sensitive <c>drive.file</c> scope
/// (see <see cref="GoogleOAuthClient"/>). The user signs in, then picks folders in the Google
/// Picker; those folders become the account's top-level entries. Kept entirely apart from
/// <see cref="GoogleDriveProvider"/> so existing full-access accounts are untouched.
///
/// <para>Deliberately cache-free: every listing and every path resolution is fetched live and
/// keyed off Drive ids, so a change made outside FileFluss (web UI, another device) shows on
/// the next refresh. That was the fix for an earlier attempt's stale listings.</para>
/// </summary>
[CloudProvider(CloudProviderType.GoogleDrivePicker)]
public sealed class GoogleDrivePickerProvider : CloudProvider
{
    /// <summary>API key the Picker needs (<c>setDeveloperKey</c>). Public by design.</summary>
    internal const string PickerApiKey = "AIzaSyDu59GsA1aYauH7lziUSoWkmWj-COWzjis";

    /// <summary>The Cloud project number (the client id's numeric prefix), for the Picker's <c>setAppId</c>.</summary>
    internal const string PickerAppId = "368567371288";

    private const string LogCategory = "googleDrivePicker";

    /// <summary>
    /// Workspace-native files have no byte stream; they are exported on download and the local
    /// file gets the extension. This account type exports Docs and Slides as PDF (macOS).
    /// </summary>
    private static readonly Dictionary<string, (string MimeType, string Extension)> ExportFormats = new(StringComparer.Ordinal)
    {
        ["application/vnd.google-apps.document"] = ("application/pdf", "pdf"),
        ["application/vnd.google-apps.spreadsheet"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx"),
        ["application/vnd.google-apps.presentation"] = ("application/pdf", "pdf"),
        ["application/vnd.google-apps.drawing"] = ("application/pdf", "pdf"),
    };

    private readonly HttpClient _http;
    private readonly Lock _rootsGate = new();
    private volatile GoogleTokenSession? _session;
    private IReadOnlyList<GoogleDrivePickedRoot> _roots = [];

    public GoogleDrivePickerProvider(Guid accountId, ICredentialStore credentials)
        : this(accountId, credentials, null)
    {
    }

    internal GoogleDrivePickerProvider(Guid accountId, ICredentialStore credentials, HttpMessageHandler? handler)
        : base(accountId, credentials)
    {
        _http = handler is null ? CloudHttp.Create() : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        if (Credentials.Load<GoogleDrivePickerCredentials>(CredentialKey) is { } stored)
        {
            _roots = stored.Roots ?? [];
            _session = CreateSession(stored.AccessToken, stored.RefreshToken, stored.ExpiresAt, stored.UserEmail, stored.DisplayName);
        }
    }

    public override CloudProviderType ProviderType => CloudProviderType.GoogleDrivePicker;

    protected override string CredentialKeyPrefix => "googleDrivePicker";

    public override bool IsAuthenticated => _session is not null;

    /// <summary>Test seam: the browser OAuth round trip.</summary>
    internal OAuthRunner Authenticator { get; set; } = (url, ct) => OAuthLoopback.AuthenticateAsync(url, ct);

    /// <summary>Test seam: shows the Picker for an access token and returns the chosen folders (empty = cancelled).</summary>
    internal Func<string, CancellationToken, Task<IReadOnlyList<GoogleDrivePickedRoot>>> PickFolders { get; set; }
        = (accessToken, ct) => new GoogleDrivePickerServer().PickFoldersAsync(accessToken, ct);

    /// <summary>The picked folders, in the order they were stored.</summary>
    internal IReadOnlyList<GoogleDrivePickedRoot> Roots
    {
        get
        {
            lock (_rootsGate)
            {
                return _roots;
            }
        }
    }

    // MARK: - Connecting

    /// <summary>
    /// Add: the "no user limit" note and what this access mode exposes. Edit: macOS offers no
    /// re-authorization here — changing folders means removing the account and adding it
    /// again — so the edit form is that hint alone and doesn't open the browser.
    /// </summary>
    public override ConnectSpec ConnectSpec => IsAuthenticated
        ? new ConnectSpec(
            ConnectField.Info("To change which folders FileFluss can access, remove this account and add it again, then pick the folders you want."))
        : ConnectSpec.Browser(
            ConnectField.Info("For new Google Drive accounts — no user limit"),
            ConnectField.Info("Sign in with Google and select the folders FileFluss may use. You can add and manage files in those folders. Files already in them aren't listed — this access mode only exposes what FileFluss adds or you pick."));

    /// <summary>
    /// Step 1: OAuth in the system browser. Step 2: the Google Picker, also in the system
    /// browser (it needs the user's signed-in Google session), which hands back the chosen
    /// folders. Nothing is stored unless both succeed; a cancelled Picker leaves the form open.
    /// </summary>
    public override async Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
    {
        try
        {
            prompt.ShowStatus(Localization.L("Waiting for sign-in in browser…"));
            var signIn = await GoogleOAuthClient.SelectedFolders.SignInAsync(_http, Authenticator, cancellationToken).ConfigureAwait(false);

            prompt.ShowStatus(Localization.L("Select the folders for FileFluss in your browser…"));
            var roots = await PickFolders(signIn.Token.AccessToken, cancellationToken).ConfigureAwait(false);
            if (roots.Count == 0)
            {
                throw CloudProviderException.Cancelled();
            }

            // Swap the session first so a refresh still in flight on the old one can't
            // overwrite what is saved next.
            var credentials = new GoogleDrivePickerCredentials(
                signIn.Token.AccessToken, signIn.Token.RefreshToken, signIn.Token.ExpiresAt, signIn.UserEmail, signIn.DisplayName, roots);
            lock (_rootsGate)
            {
                _roots = roots;
            }

            _session = CreateSession(credentials.AccessToken, credentials.RefreshToken, credentials.ExpiresAt, credentials.UserEmail, credentials.DisplayName);
            Credentials.Save(CredentialKey, credentials);
            SupportLogger.Shared.Log($"Connected with {roots.Count} folder(s)", LogCategory);

            // macOS names these accounts "Google Drive (<name>)", not by the type's long name.
            return new ConnectResult(signIn.DisplayName) { DisplayName = $"Google Drive ({signIn.DisplayName})" };
        }
        finally
        {
            prompt.ShowStatus(null);
        }
    }

    public override Task DisconnectAsync()
    {
        _session = null;
        lock (_rootsGate)
        {
            _roots = [];
        }

        return base.DisconnectAsync();
    }

    private GoogleTokenSession CreateSession(string accessToken, string refreshToken, DateTimeOffset expiresAt, string userEmail, string displayName)
        => new(_http, GoogleOAuthClient.SelectedFolders, new GoogleToken(accessToken, refreshToken, expiresAt), userEmail, displayName, PersistCredentials);

    /// <summary>
    /// Saves the current token and roots, so a refreshed token and a root renamed or removed
    /// through FileFluss survive a relaunch. Skipped when the account was disconnected or
    /// re-signed in the meantime.
    /// </summary>
    private void PersistCredentials(GoogleTokenSession session)
    {
        if (!ReferenceEquals(_session, session))
        {
            return;
        }

        var token = session.Token;
        Credentials.Save(CredentialKey, new GoogleDrivePickerCredentials(token.AccessToken, token.RefreshToken, token.ExpiresAt, session.UserEmail, session.DisplayName, Roots));
    }

    private GoogleTokenSession RequireSession() => _session ?? throw CloudProviderException.NotAuthenticated();

    // MARK: - Listing

    public override async Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        RequireSession();
        path = GoogleDriveApi.Trim(path);
        if (path == "/")
        {
            return await ListRootsAsync(cancellationToken).ConfigureAwait(false);
        }

        var folder = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        var children = await ListChildrenAsync(folder.Id, path, cancellationToken).ConfigureAwait(false);
        return children.Select(c => c.ToItem(path)).ToList();
    }

    /// <summary>
    /// The picked folders. Named by the <b>stored</b> name — the same string
    /// <see cref="ResolveAsync"/> matches a path against; a live-fetched name here would break
    /// navigation. Only the modification date comes from live metadata.
    /// </summary>
    private async Task<IReadOnlyList<CloudFileItem>> ListRootsAsync(CancellationToken cancellationToken)
    {
        var items = await Task.WhenAll(Roots.Select(async root =>
        {
            var modified = CloudFileItem.UnknownDate;
            try
            {
                if (await FetchFileAsync(root.Id, cancellationToken).ConfigureAwait(false) is { } live)
                {
                    modified = live.ModificationDate;
                }
            }
            catch (CloudProviderException)
            {
                // A root that can't be read still lists; opening it reports the problem.
            }

            return RootItem(root, modified);
        })).ConfigureAwait(false);

        return items.OrderBy(i => i.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
    }

    private static CloudFileItem RootItem(GoogleDrivePickedRoot root, DateTimeOffset modified)
        => new("d:" + root.Id, root.Name, "/" + root.Name, true, 0, modified)
        {
            IsHidden = CloudFileItem.IsDotHidden(root.Name),
        };

    /// <summary>
    /// A folder's children, live. Empty is normal in this access model (folders expose only
    /// what FileFluss added or the user picked), so it is logged, not treated as an error.
    /// </summary>
    private async Task<List<GoogleDriveFile>> ListChildrenAsync(string folderId, string parentPath, CancellationToken cancellationToken)
    {
        var files = new List<GoogleDriveFile>();
        string? pageToken = null;
        try
        {
            do
            {
                var query = new List<(string, string)>
                {
                    ("q", $"'{folderId}' in parents and trashed = false"),
                    ("fields", "nextPageToken,files(id,name,mimeType,size,modifiedTime,md5Checksum)"),
                    ("pageSize", "1000"),
                    ("orderBy", "folder,name"),
                    ("supportsAllDrives", "true"),
                    ("includeItemsFromAllDrives", "true"),
                };
                if (pageToken is not null)
                {
                    query.Add(("pageToken", pageToken));
                }

                var page = await ApiAsync<GoogleFileList>(HttpMethod.Get, "/files", query, null, parentPath, cancellationToken).ConfigureAwait(false);
                files.AddRange(page.Files ?? []);
                pageToken = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
            }
            while (pageToken is not null);

            SupportLogger.Shared.Log($"listChildren {parentPath} [parentId={folderId}] → {files.Count} item(s)", LogCategory);
        }
        catch (CloudProviderException e)
        {
            SupportLogger.Shared.Log($"listChildren {parentPath} [parentId={folderId}] FAILED: {e.Message}", LogCategory, LogLevel.Error);
            throw;
        }

        return files;
    }

    // MARK: - Path resolution (live, no cache)

    /// <summary>
    /// "/RootName/sub/leaf" → Drive id, walking from the matching picked root and listing
    /// children at each step. Intermediate components prefer a folder; the last one takes
    /// any match. Keeps the real mimeType (macOS reduced it to folder/octet-stream, which
    /// left Workspace documents never exported).
    /// </summary>
    private async Task<Resolved> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        var components = GoogleDriveApi.Components(path);
        if (components.Length == 0)
        {
            throw CloudProviderException.NotFound(path);
        }

        var root = Roots.FirstOrDefault(r => r.Name == components[0]) ?? throw CloudProviderException.NotFound(path);
        var current = new Resolved(root.Id, GoogleDriveApi.FolderMimeType, root);
        var walked = "/" + root.Name;
        foreach (var component in components.Skip(1))
        {
            var children = await ListChildrenAsync(current.Id, walked, cancellationToken).ConfigureAwait(false);
            var match = children.FirstOrDefault(c => c.Name == component && c.IsFolder)
                ?? children.FirstOrDefault(c => c.Name == component)
                ?? throw CloudProviderException.NotFound(path);
            current = new Resolved(match.Id, match.MimeType, null);
            walked = GoogleDriveApi.Child(walked, match.Name);
        }

        return current;
    }

    /// <summary>The Drive folder a new item at <paramref name="remotePath"/> goes into. New top-level entries can't be made: roots come only from the Picker.</summary>
    private async Task<string> ParentFolderIdAsync(string remotePath, CancellationToken cancellationToken)
    {
        var parent = GetParentPath(GoogleDriveApi.Trim(remotePath));
        if (parent == "/")
        {
            throw CloudProviderException.NotImplemented();
        }

        return (await ResolveAsync(parent, cancellationToken).ConfigureAwait(false)).Id;
    }

    private async Task<GoogleDriveFile?> FetchFileAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            return await ApiAsync<GoogleDriveFile>(
                HttpMethod.Get,
                $"/files/{CloudHttp.Escape(id)}",
                [("fields", "id,name,mimeType,size,modifiedTime,md5Checksum"), ("supportsAllDrives", "true")],
                null,
                null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            return null;
        }
    }

    // MARK: - Mutations

    public override async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        RequireSession();
        path = GoogleDriveApi.Trim(path);

        // The contract wants an existing folder to succeed quietly, and Drive would happily
        // create a same-named duplicate (macOS did here).
        try
        {
            if ((await ResolveAsync(path, cancellationToken).ConfigureAwait(false)).IsFolder)
            {
                return;
            }
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
        }

        var parentId = await ParentFolderIdAsync(path, cancellationToken).ConfigureAwait(false);
        await ApiAsync<GoogleDriveFile>(
            HttpMethod.Post,
            "/files",
            [],
            new { name = GetName(path), mimeType = GoogleDriveApi.FolderMimeType, parents = new[] { parentId } },
            path,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deleting a picked root deletes that folder in Drive, as on macOS, and also drops it from the account's roots.</summary>
    public override async Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
    {
        RequireSession();
        path = GoogleDriveApi.Trim(path);
        var item = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        await ApiVoidAsync(HttpMethod.Delete, $"/files/{CloudHttp.Escape(item.Id)}", path, cancellationToken).ConfigureAwait(false);

        if (item.Root is { } root)
        {
            UpdateRoots(roots => roots.Where(r => r.Id != root.Id).ToList());
        }
    }

    /// <summary>Renaming a picked root renames the Drive folder and the stored root, so its path follows.</summary>
    public override async Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
    {
        RequireSession();
        path = GoogleDriveApi.Trim(path);
        var item = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        await ApiAsync<GoogleDriveFile>(HttpMethod.Patch, $"/files/{CloudHttp.Escape(item.Id)}", [], new { name = newName }, path, cancellationToken).ConfigureAwait(false);

        if (item.Root is { } root)
        {
            UpdateRoots(roots => roots.Select(r => r.Id == root.Id ? r with { Name = newName } : r).ToList());
        }
    }

    public override async Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        RequireSession();
        var item = await ResolveAsync(GoogleDriveApi.Trim(path), cancellationToken).ConfigureAwait(false);
        var newParentId = await ParentFolderIdAsync(newPath, cancellationToken).ConfigureAwait(false);
        var oldParentId = await ParentFolderIdAsync(path, cancellationToken).ConfigureAwait(false);
        await ApiAsync<GoogleDriveFile>(
            HttpMethod.Patch,
            $"/files/{CloudHttp.Escape(item.Id)}",
            [("addParents", newParentId), ("removeParents", oldParentId), ("supportsAllDrives", "true"), ("fields", "id")],
            new { name = GetName(GoogleDriveApi.Trim(newPath)) },
            path,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Server-side copy of a file. Drive's <c>files.copy</c> can't copy folders, so a folder
    /// throws NotImplemented and the caller copies it item by item.
    /// </summary>
    public override async Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        RequireSession();
        var item = await ResolveAsync(GoogleDriveApi.Trim(path), cancellationToken).ConfigureAwait(false);
        if (item.IsFolder)
        {
            throw CloudProviderException.NotImplemented();
        }

        var newParentId = await ParentFolderIdAsync(newPath, cancellationToken).ConfigureAwait(false);
        await ApiAsync<GoogleDriveFile>(
            HttpMethod.Post,
            $"/files/{CloudHttp.Escape(item.Id)}/copy",
            [],
            new { name = GetName(GoogleDriveApi.Trim(newPath)), parents = new[] { newParentId } },
            path,
            cancellationToken).ConfigureAwait(false);
    }

    public override async Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        RequireSession();
        path = GoogleDriveApi.Trim(path);
        var item = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        var file = await FetchFileAsync(item.Id, cancellationToken).ConfigureAwait(false) ?? throw CloudProviderException.NotFound(path);

        // A root keeps its stored name, the one its path is built from.
        return item.Root is { } root ? RootItem(root, file.ModificationDate) : file.ToItem(GetParentPath(path));
    }

    private void UpdateRoots(Func<IReadOnlyList<GoogleDrivePickedRoot>, IReadOnlyList<GoogleDrivePickedRoot>> change)
    {
        lock (_rootsGate)
        {
            _roots = change(_roots);
        }

        if (_session is { } session)
        {
            PersistCredentials(session);
        }
    }

    // MARK: - Download / upload

    /// <summary>
    /// The export extension of a Workspace document. Paths resolve live here (no cache), so
    /// only items that look like one — no size and no checksum, which every binary file has —
    /// cost a lookup.
    /// </summary>
    public override async ValueTask<string?> ExportExtensionAsync(CloudFileItem item, CancellationToken cancellationToken = default)
    {
        if (item.IsDirectory || item.Size > 0 || item.Checksum is not null)
        {
            return null;
        }

        var resolved = await ResolveAsync(GoogleDriveApi.Trim(item.Path), cancellationToken).ConfigureAwait(false);
        return ExportFormats.TryGetValue(resolved.MimeType, out var export) ? export.Extension : null;
    }

    public override async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var session = RequireSession();
        var item = await ResolveAsync(GoogleDriveApi.Trim(remotePath), cancellationToken).ConfigureAwait(false);

        string url;
        var target = localPath;
        if (ExportFormats.TryGetValue(item.MimeType, out var export))
        {
            url = $"{GoogleDriveApi.ApiBase}/files/{CloudHttp.Escape(item.Id)}/export?mimeType={CloudHttp.Escape(export.MimeType)}";
            target = localPath + "." + export.Extension;
        }
        else
        {
            url = $"{GoogleDriveApi.ApiBase}/files/{CloudHttp.Escape(item.Id)}?alt=media&supportsAllDrives=true";
        }

        var accessToken = await session.AccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = CloudHttp.Bearer(accessToken);
        using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"Download failed: HTTP {(int)response.StatusCode}", LogCategory, LogLevel.Error);
            throw MapError((int)response.StatusCode, remotePath);
        }

        await HttpTransfer.SaveToFileAsync(response, target, onBytes, cancellationToken).ConfigureAwait(false);
    }

    public override async Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        var session = RequireSession();
        remotePath = GoogleDriveApi.Trim(remotePath);
        var parentId = await ParentFolderIdAsync(remotePath, cancellationToken).ConfigureAwait(false);
        var fileName = GetName(remotePath);

        string? existingId;
        try
        {
            existingId = await FindChildIdAsync(parentId, fileName, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException)
        {
            existingId = null;
        }

        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(localPath));
        var created = new DateTimeOffset(File.GetCreationTimeUtc(localPath));
        var uploader = new GoogleDriveUploader(_http, session.AccessTokenAsync, (status, _) => MapError(status, fileName), LogCategory);
        var uploaded = await uploader.UploadAsync(localPath, fileName, parentId, existingId, modified, created, onBytes, cancellationToken).ConfigureAwait(false);

        // Always stamp the source's dates so the file lists with its real date, not the upload
        // time: a replacement can't carry them and the resumable init doesn't always honour them.
        await PatchTimestampsAsync(uploaded.Id, modified, created, cancellationToken).ConfigureAwait(false);
    }

    public override async Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
    {
        RequireSession();
        var item = await ResolveAsync(GoogleDriveApi.Trim(path), cancellationToken).ConfigureAwait(false);
        await PatchTimestampsAsync(item.Id, date, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> FindChildIdAsync(string parentId, string name, CancellationToken cancellationToken)
    {
        var page = await ApiAsync<GoogleFileList>(
            HttpMethod.Get,
            "/files",
            [
                ("q", $"'{parentId}' in parents and name = '{GoogleDriveApi.QueryLiteral(name)}' and trashed = false"),
                ("fields", "files(id)"),
                ("pageSize", "1"),
                ("supportsAllDrives", "true"),
            ],
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return page.Files?.FirstOrDefault()?.Id;
    }

    /// <summary>Metadata-only PATCH of the dates. Best effort: any refusal or transport failure is ignored, as on macOS.</summary>
    private async Task PatchTimestampsAsync(string fileId, DateTimeOffset? modified, DateTimeOffset? created, CancellationToken cancellationToken)
    {
        var accessToken = await RequireSession().AccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{GoogleDriveApi.ApiBase}/files/{CloudHttp.Escape(fileId)}?supportsAllDrives=true")
        {
            Content = new StringContent(GoogleDriveApi.BuildMetadata(null, null, modified, created), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = CloudHttp.Bearer(accessToken);
        try
        {
            using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                SupportLogger.Shared.Log($"Timestamp PATCH ignored: HTTP {(int)response.StatusCode}", LogCategory, LogLevel.Notice);
            }
        }
        catch (CloudProviderException e)
        {
            SupportLogger.Shared.Log($"Timestamp PATCH failed: {e.Message}", LogCategory, LogLevel.Notice);
        }
    }

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

    // MARK: - HTTP

    /// <summary>
    /// A Drive API call with exactly the query given (no implicit <c>supportsAllDrives</c>,
    /// unlike the full provider). A 401 forces one token refresh and a retry; macOS signed
    /// the account out instead, and this account type has no Re-authorize.
    /// </summary>
    private async Task<T> ApiAsync<T>(
        HttpMethod method, string apiPath, IEnumerable<(string Key, string Value)> query, object? body, string? itemPath, CancellationToken cancellationToken)
    {
        var items = query.ToList();
        var url = GoogleDriveApi.ApiBase + apiPath + (items.Count > 0 ? "?" + GoogleDriveApi.QueryString(items) : string.Empty);
        using var response = await SendAsync(method, url, body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapError((int)response.StatusCode, itemPath ?? apiPath);
        }

        return await response.ReadJsonAsync<T>(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Calls without a response body always declare shared-drive support.</summary>
    private async Task ApiVoidAsync(HttpMethod method, string apiPath, string? itemPath, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, GoogleDriveApi.ApiBase + apiPath + "?supportsAllDrives=true", null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapError((int)response.StatusCode, itemPath ?? apiPath);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, CancellationToken cancellationToken)
    {
        var session = RequireSession();
        var accessToken = await session.AccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendOnceAsync(method, url, body, accessToken, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            accessToken = await session.RefreshAfterRejectionAsync(accessToken, cancellationToken).ConfigureAwait(false);
            response = await SendOnceAsync(method, url, body, accessToken, cancellationToken).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"{method} {new Uri(url).AbsolutePath} → HTTP {(int)response.StatusCode}", LogCategory, LogLevel.Error);
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

    /// <summary>The macOS mapping for this account type: 401 and 403 both mean "sign in again".</summary>
    internal static CloudProviderException MapError(int status, string itemPath) => status switch
    {
        401 => CloudProviderException.Unauthorized(),
        403 => CloudProviderException.NotAuthenticated(),
        404 => CloudProviderException.NotFound(itemPath),
        429 => CloudProviderException.RateLimited(),
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

    /// <param name="Root">The picked root when the path named one directly.</param>
    private sealed record Resolved(string Id, string MimeType, GoogleDrivePickedRoot? Root)
    {
        public bool IsFolder => MimeType == GoogleDriveApi.FolderMimeType;
    }
}
