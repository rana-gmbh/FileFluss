// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.Internxt;

/// <summary>
/// Post-login secrets (the macOS <c>InternxtCredentials</c>). The mnemonic is stored because
/// every transfer re-derives per-file keys from it.
/// </summary>
/// <param name="Token">The <c>newToken</c> JWT — bearer for every Drive call.</param>
/// <param name="Mnemonic">The decrypted BIP39 mnemonic, root of all file-content keys.</param>
/// <param name="Bucket">The user's storage bucket (hex); all file content lives there.</param>
/// <param name="BridgeUser">Network basic-auth user name.</param>
/// <param name="UserId">Network basic-auth password (sent SHA-256'd).</param>
internal sealed record InternxtCredentials(
    string Email,
    string Token,
    string Mnemonic,
    string Bucket,
    string RootFolderUuid,
    string BridgeUser,
    string UserId);

/// <summary>
/// Internxt's two services, transliterated (like the macOS client) from @internxt/cli and
/// @internxt/sdk.
/// <list type="bullet">
/// <item>Drive (<c>https://gateway.internxt.com/drive</c>, Bearer): login, folder/file listing,
/// create, delete, rename (<c>PUT …/meta</c>), move (<c>PATCH</c>), usage.</item>
/// <item>Network (<c>https://gateway.internxt.com/network</c>, Basic): encrypted content —
/// download links per file, and the start → PUT → finish upload flow.</item>
/// </list>
/// </summary>
internal sealed class InternxtApiClient
{
    internal const string DriveApi = "https://gateway.internxt.com/drive";
    internal const string NetworkApi = "https://gateway.internxt.com/network";
    private const string ClientName = "drive-web";
    private const string ClientVersion = "1.0.0";
    private const int PageSize = 50;

    private readonly HttpClient _http;
    private readonly Lazy<byte[]> _seed;

    /// <summary>Internxt is UUID-addressed; resolved nodes are cached by path so navigating doesn't re-walk the tree.</summary>
    private readonly ConcurrentDictionary<string, Node> _nodeCache = new(StringComparer.Ordinal);

    public InternxtApiClient(HttpClient http, InternxtCredentials credentials)
    {
        _http = http;
        Credentials = credentials;
        _seed = new Lazy<byte[]>(() => InternxtCrypto.MnemonicToSeed(credentials.Mnemonic));
        _nodeCache["/"] = new Node(credentials.RootFolderUuid, true, null, null, 0, null);
    }

    internal sealed record Node(string Uuid, bool IsFolder, string? FileId, string? Bucket, long Size, string? Type);

    public InternxtCredentials Credentials { get; }

    /// <summary>Back-off unit for transient-failure retries (0.6 s, 1.2 s); tests shorten it.</summary>
    internal TimeSpan RetryDelayUnit { get; set; } = TimeSpan.FromMilliseconds(600);

    // MARK: - Authentication

    /// <summary>
    /// Two steps: fetch the encrypted salt (and whether 2FA is on), hash the password against
    /// it, exchange that for a token and the user record, then decrypt the mnemonic locally
    /// with the plain password.
    /// </summary>
    public static async Task<InternxtCredentials> LoginAsync(HttpClient http, string email, string password, string? twoFactorCode, CancellationToken cancellationToken)
    {
        // 1. Security details: the encrypted salt.
        byte[] saltData;
        using (var request = BuildRequest(HttpMethod.Post, DriveApi + "/auth/login", new JsonObject { ["email"] = email }.ToJsonString(), null))
        using (var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw CloudProviderException.InvalidCredentials();
            }

            saltData = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        string sKey;
        bool tfa;
        using (var document = ParseJson(saltData))
        {
            sKey = GetString(document.RootElement, "sKey") ?? throw CloudProviderException.InvalidResponse();
            tfa = document.RootElement.TryGetProperty("tfa", out var tfaValue) && tfaValue.ValueKind == JsonValueKind.True;
        }

        // 2. The wrapped password hash.
        var passwordHash = await Task.Run(() => InternxtCrypto.EncryptPasswordHash(password, sKey), cancellationToken).ConfigureAwait(false)
            ?? throw CloudProviderException.InvalidCredentials();

        // 3. Token + user. The code is required when the security details say 2FA is on.
        var access = new JsonObject { ["email"] = email, ["password"] = passwordHash };
        var code = twoFactorCode?.Trim() ?? string.Empty;
        if (tfa && code.Length == 0)
        {
            throw CloudProviderException.TwoFactorRequired();
        }

        if (code.Length > 0)
        {
            access["tfa"] = code;
        }

        byte[] accessData;
        using (var request = BuildRequest(HttpMethod.Post, DriveApi + "/auth/login/access", access.ToJsonString(), null))
        using (var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                // macOS read every 401 as "2FA required"; without 2FA on the account a 401 can
                // only mean the password was wrong.
                if (response.StatusCode == HttpStatusCode.Unauthorized && tfa)
                {
                    throw CloudProviderException.TwoFactorRequired();
                }

                SupportLogger.Shared.Log($"[Internxt] login/access HTTP {(int)response.StatusCode}", "Internxt", LogLevel.Error);
                throw CloudProviderException.InvalidCredentials();
            }

            accessData = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        string token, userEmail, encryptedMnemonic, bucket, bridgeUser, userId, rootFolderId;
        using (var document = ParseJson(accessData))
        {
            var root = document.RootElement;
            var user = root.TryGetProperty("user", out var u) ? u : default;
            token = GetString(root, "newToken") ?? throw CloudProviderException.InvalidResponse();
            userEmail = GetString(user, "email") ?? throw CloudProviderException.InvalidResponse();
            encryptedMnemonic = GetString(user, "mnemonic") ?? throw CloudProviderException.InvalidResponse();
            bucket = GetString(user, "bucket") ?? throw CloudProviderException.InvalidResponse();
            bridgeUser = GetString(user, "bridgeUser") ?? throw CloudProviderException.InvalidResponse();
            userId = GetString(user, "userId") ?? throw CloudProviderException.InvalidResponse();
            rootFolderId = GetString(user, "rootFolderId") ?? throw CloudProviderException.InvalidResponse();
        }

        // 4. The mnemonic, decrypted with the plain password.
        var mnemonic = InternxtCrypto.DecryptMnemonic(encryptedMnemonic, password);
        if (mnemonic is null)
        {
            SupportLogger.Shared.Log("[Internxt] mnemonic decrypt failed", "Internxt", LogLevel.Error);
            throw CloudProviderException.InvalidCredentials();
        }

        SupportLogger.Shared.Log($"[Internxt] Authenticated as {userEmail}", "Internxt");
        return new InternxtCredentials(userEmail, token, mnemonic, bucket, rootFolderId, bridgeUser, userId);
    }

    // MARK: - Listing

    public async Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        var node = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        if (!node.IsFolder)
        {
            throw CloudProviderException.NotFound(path);
        }

        var items = new List<CloudFileItem>();

        // Names seen in this listing, to drop cached children that have since gone.
        var listedNames = new HashSet<string>(StringComparer.Ordinal);

        for (var offset = 0; ; offset += PageSize)
        {
            using var document = ParseJson(await DriveSendAsync($"/folders/content/{node.Uuid}/folders/?offset={offset}&limit={PageSize}", HttpMethod.Get, null, cancellationToken).ConfigureAwait(false));
            var page = Items(document.RootElement, "folders");
            foreach (var folder in page)
            {
                if (GetString(folder, "uuid") is not { } uuid)
                {
                    continue;
                }

                var name = GetString(folder, "plainName") ?? GetString(folder, "name") ?? "Untitled";
                var childPath = ChildPath(path, name);
                listedNames.Add(name);
                _nodeCache[childPath] = new Node(uuid, true, null, null, 0, null);
                items.Add(new CloudFileItem("d" + uuid, name, childPath, true, 0, ParseDate(GetString(folder, "updatedAt"))));
            }

            if (page.Count < PageSize)
            {
                break;
            }
        }

        for (var offset = 0; ; offset += PageSize)
        {
            using var document = ParseJson(await DriveSendAsync($"/folders/content/{node.Uuid}/files/?offset={offset}&limit={PageSize}", HttpMethod.Get, null, cancellationToken).ConfigureAwait(false));
            var page = Items(document.RootElement, "files");
            foreach (var file in page)
            {
                if (GetString(file, "uuid") is not { } uuid)
                {
                    continue;
                }

                var baseName = GetString(file, "plainName") ?? GetString(file, "name") ?? "Untitled";
                var extension = GetString(file, "type") ?? string.Empty;
                var name = extension.Length == 0 ? baseName : $"{baseName}.{extension}";
                var childPath = ChildPath(path, name);
                var size = GetInt64(file, "size") ?? 0;
                listedNames.Add(name);
                _nodeCache[childPath] = new Node(uuid, false, GetString(file, "fileId"), GetString(file, "bucket"), size, extension);
                items.Add(new CloudFileItem("f" + uuid, name, childPath, false, size, ParseDate(GetString(file, "updatedAt"))));
            }

            if (page.Count < PageSize)
            {
                break;
            }
        }

        // Both listings are in, so this level is complete: whatever is still cached here and
        // wasn't listed no longer exists.
        _nodeCache.RetainPathChildren(path, listedNames);
        return items;
    }

    public async Task<CloudFileItem> FindAsync(string path, CancellationToken cancellationToken)
    {
        var name = CloudPath.Name(path);
        var entries = await ListFolderAsync(CloudPath.Parent(path), cancellationToken).ConfigureAwait(false);
        return entries.FirstOrDefault(e => e.Name == name) ?? throw CloudProviderException.NotFound(path);
    }

    // MARK: - Download

    /// <summary>Asks the Network for the file's shard links, then streams them in order through one AES-256-CTR keystream.</summary>
    public async Task DownloadAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var node = await ResolveAsync(remotePath, cancellationToken).ConfigureAwait(false);
        if (node.IsFolder || node.FileId is not { } fileId || node.Bucket is not { } bucket)
        {
            throw CloudProviderException.NotFound(remotePath);
        }

        string indexHex;
        List<string> shardUrls;
        using (var document = ParseJson(await NetworkRequestAsync($"/buckets/{bucket}/files/{fileId}/info", HttpMethod.Get, null, true, cancellationToken).ConfigureAwait(false)))
        {
            indexHex = GetString(document.RootElement, "index") ?? throw CloudProviderException.InvalidResponse();
            shardUrls = Items(document.RootElement, "shards")
                .Select(s => (Index: GetInt64(s, "index") ?? 0, Url: GetString(s, "url") ?? throw CloudProviderException.InvalidResponse()))
                .OrderBy(s => s.Index)
                .Select(s => s.Url)
                .ToList();
        }

        var index = InternxtCrypto.HexDecode(indexHex);
        if (index is not { Length: >= 16 } || InternxtCrypto.GenerateFileKey(_seed.Value, bucket, index) is not { } key)
        {
            throw CloudProviderException.InvalidResponse();
        }

        using var plain = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new InternxtShardStream(_http, shardUrls, key, index.AsSpan(0, 16)), HttpTransfer.BufferSize),
        };
        await HttpTransfer.SaveToFileAsync(plain, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    // MARK: - Upload

    /// <summary>
    /// Encrypts locally (AES-256-CTR) through the three-step Network flow, then registers the
    /// file in Drive. Replaces an existing file at the path. The transfer steps are idempotent
    /// (re-reserving a slot, re-PUTting the same bytes, finishing the same shard), so they are
    /// retried through Internxt's occasional gateway 5xx.
    /// </summary>
    public async Task UploadAsync(string localPath, string remotePath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var parentPath = CloudPath.Parent(remotePath);
        var fileName = CloudPath.Name(remotePath);
        var parent = await ResolveAsync(parentPath, cancellationToken).ConfigureAwait(false);
        if (!parent.IsFolder)
        {
            throw CloudProviderException.NotFound(parentPath);
        }

        var bucket = Credentials.Bucket;

        // A file already at this path is replaced once the new one has landed.
        Node? existing = null;
        try
        {
            existing = await ResolveAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException)
        {
        }

        var size = new FileInfo(localPath).Length;

        // 1. Per-file key material.
        var index = RandomNumberGenerator.GetBytes(32);
        var key = InternxtCrypto.GenerateFileKey(_seed.Value, bucket, index) ?? throw CloudProviderException.InvalidResponse();

        // 2. Reserve an upload slot (CTR ciphertext is as long as the plaintext).
        var startBody = new JsonObject { ["uploads"] = new JsonArray(new JsonObject { ["index"] = 0, ["size"] = size }) }.ToJsonString();
        var startData = await RetryingTransientAsync(
            () => NetworkRequestAsync($"/v2/buckets/{bucket}/files/start?multiparts=1", HttpMethod.Post, startBody, false, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        string uploadUrl, uploadUuid;
        using (var document = ParseJson(startData))
        {
            var upload = Items(document.RootElement, "uploads").FirstOrDefault();
            uploadUrl = GetString(upload, "url") ?? throw CloudProviderException.InvalidResponse();
            uploadUuid = GetString(upload, "uuid") ?? throw CloudProviderException.InvalidResponse();
        }

        // 3. PUT the ciphertext to the presigned URL, encrypting as it streams. Progress across
        // retries is reported against a high-water mark so it never counts twice.
        long reported = 0;
        var sha256 = await RetryingTransientAsync(
            () =>
            {
                long sent = 0;
                return PutAsync(localPath, size, key, index.AsSpan(0, 16).ToArray(), uploadUrl, delta =>
                {
                    sent += delta;
                    if (sent > reported)
                    {
                        onBytes?.Invoke(sent - reported);
                        reported = sent;
                    }
                }, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);
        var shardHash = InternxtCrypto.ShardHashHex(sha256);

        // 4. Finish → the Network file id.
        var finishBody = new JsonObject
        {
            ["index"] = InternxtCrypto.HexEncode(index),
            ["shards"] = new JsonArray(new JsonObject { ["hash"] = shardHash, ["uuid"] = uploadUuid }),
        }.ToJsonString();
        var finishData = await RetryingTransientAsync(
            () => NetworkRequestAsync($"/v2/buckets/{bucket}/files/finish", HttpMethod.Post, finishBody, false, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        string networkFileId;
        using (var document = ParseJson(finishData))
        {
            networkFileId = GetString(document.RootElement, "id") ?? throw CloudProviderException.InvalidResponse();
        }

        // 5. Register the file in Drive. Drive rejects a plainName+type that already exists in
        // the folder (409), so a replacement is registered under a temporary name first; only
        // once it exists does the old entry go, and the new one takes the name. Deleting first
        // (as before) lost both whenever the registration then failed.
        var (baseName, extension) = SplitExtension(fileName);
        var replacing = existing is { IsFolder: false };
        var registeredBase = replacing ? $"{baseName}.fileflusspart-{Guid.NewGuid().ToString("N")[..8]}" : baseName;
        var registeredPath = CloudPath.Combine(parentPath, extension.Length > 0 ? $"{registeredBase}.{extension}" : registeredBase);
        var entry = new JsonObject
        {
            ["plainName"] = registeredBase,
            ["type"] = extension,
            ["size"] = size,
            ["folderUuid"] = parent.Uuid,
            ["fileId"] = networkFileId,
            ["bucket"] = bucket,
            ["encryptVersion"] = "03-aes",
        };
        try
        {
            entry["modificationTime"] = InternetDate(File.GetLastWriteTimeUtc(localPath));
            entry["creationTime"] = InternetDate(File.GetCreationTimeUtc(localPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        // Retried like the transfer steps, for a sharper reason: the ciphertext is already in
        // the bucket, so a gateway failure here would lose a fully uploaded file. A retry that
        // turns out to be a duplicate lands in the 409 branch, which resolves it against the
        // live listing.
        var entryBody = entry.ToJsonString();
        var created = await RetryingTransientAsync(
            () => RegisterDriveEntryAsync(entryBody, registeredPath, parentPath, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        string? createdUuid = null;
        try
        {
            using var document = ParseJson(created);
            createdUuid = GetString(document.RootElement, "uuid");
        }
        catch (CloudProviderException)
        {
        }

        if (replacing)
        {
            // Without the new entry's uuid it can't take the name; the old file stays as it was.
            var newUuid = createdUuid ?? throw CloudProviderException.InvalidResponse();
            try
            {
                await DriveSendAsync($"/files/{existing!.Uuid}", HttpMethod.Delete, null, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException)
            {
            }

            _nodeCache.TryRemove(remotePath, out _);
            await TakeNameAsync(newUuid, baseName, cancellationToken).ConfigureAwait(false);
        }

        if (createdUuid is not null)
        {
            _nodeCache[remotePath] = new Node(createdUuid, false, networkFileId, bucket, size, extension);
        }
    }

    /// <summary>
    /// Renames the freshly registered replacement to its real name. The old entry's delete can
    /// take a moment to propagate (409 meanwhile), so a few tries; if it never works the file is
    /// still there under its temporary name, which the error says.
    /// </summary>
    private async Task TakeNameAsync(string uuid, string plainName, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["plainName"] = plainName }.ToJsonString();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DriveSendAsync($"/files/{uuid}/meta", HttpMethod.Put, body, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (CloudProviderException e) when (attempt < 4 && e.Kind is CloudErrorKind.ServerError or CloudErrorKind.RateLimited or CloudErrorKind.NetworkError)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Registers the uploaded file in Drive, resolving the one conflict the API raises: a 409
    /// for a name that still exists — the delete hasn't propagated, or the cached entry pointed
    /// at an already-gone uuid. Re-list the parent for the live entry, delete it, create again.
    /// </summary>
    private async Task<byte[]> RegisterDriveEntryAsync(string body, string remotePath, string parentPath, CancellationToken cancellationToken)
    {
        try
        {
            return await DriveSendAsync("/files", HttpMethod.Post, body, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.ServerError && e.StatusCode == 409)
        {
            try
            {
                await ListFolderAsync(parentPath, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException)
            {
            }

            if (_nodeCache.TryGetValue(remotePath, out var conflict) && !conflict.IsFolder)
            {
                try
                {
                    await DriveSendAsync($"/files/{conflict.Uuid}", HttpMethod.Delete, null, cancellationToken).ConfigureAwait(false);
                }
                catch (CloudProviderException)
                {
                }

                _nodeCache.TryRemove(remotePath, out _);
            }

            return await DriveSendAsync("/files", HttpMethod.Post, body, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>PUTs the encrypted file and returns the SHA-256 of the ciphertext that went out.</summary>
    private async Task<byte[]> PutAsync(string localPath, long size, byte[] key, byte[] iv, string url, ByteProgress onBytes, CancellationToken cancellationToken)
    {
        using var content = new InternxtEncryptingContent(localPath, size, key, iv, onBytes);
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CloudProviderException.ServerError((int)response.StatusCode);
        }

        return content.CiphertextSha256 ?? throw CloudProviderException.InvalidResponse();
    }

    // MARK: - Mutations

    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
            return; // idempotent
        }
        catch (CloudProviderException)
        {
        }

        var parent = await ResolveAsync(CloudPath.Parent(path), cancellationToken).ConfigureAwait(false);
        var body = new JsonObject { ["plainName"] = CloudPath.Name(path), ["parentFolderUuid"] = parent.Uuid }.ToJsonString();
        var data = await DriveSendAsync("/folders", HttpMethod.Post, body, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = ParseJson(data);
            if (GetString(document.RootElement, "uuid") is { } uuid)
            {
                _nodeCache[path] = new Node(uuid, true, null, null, 0, null);
            }
        }
        catch (CloudProviderException)
        {
        }
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var node = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        await DeleteRetryingTransientAsync(node.IsFolder ? $"/folders/{node.Uuid}" : $"/files/{node.Uuid}", cancellationToken).ConfigureAwait(false);

        // Its children went with it. Without this, CreateFolder is answered from the cache for
        // a folder that no longer exists and silently creates nothing.
        _nodeCache.RemovePathSubtree(path);
    }

    public async Task RenameAsync(string path, string newName, CancellationToken cancellationToken)
    {
        var node = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);

        // Rename sends the bare name; the gateway keeps a file's `type` (extension).
        var plainName = node.IsFolder ? newName : SplitExtension(newName).BaseName;
        var endpoint = node.IsFolder ? $"/folders/{node.Uuid}/meta" : $"/files/{node.Uuid}/meta";
        await DriveSendAsync(endpoint, HttpMethod.Put, new JsonObject { ["plainName"] = plainName }.ToJsonString(), cancellationToken).ConfigureAwait(false);
        _nodeCache.RemovePathSubtree(path);
        _nodeCache[CloudPath.Combine(CloudPath.Parent(path), newName)] = node;
    }

    /// <summary>
    /// Server-side move into another folder (<c>PATCH {destinationFolder}</c>). The API can't
    /// rename on the way, so a move that also changes the name falls back to copy + delete.
    /// </summary>
    public async Task MoveAsync(string path, string newPath, CancellationToken cancellationToken)
    {
        if (CloudPath.Name(path) != CloudPath.Name(newPath))
        {
            throw CloudProviderException.NotImplemented();
        }

        var node = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        var destination = await ResolveAsync(CloudPath.Parent(newPath), cancellationToken).ConfigureAwait(false);
        var endpoint = node.IsFolder ? $"/folders/{node.Uuid}" : $"/files/{node.Uuid}";
        await DriveSendAsync(endpoint, HttpMethod.Patch, new JsonObject { ["destinationFolder"] = destination.Uuid }.ToJsonString(), cancellationToken).ConfigureAwait(false);
        _nodeCache.RemovePathSubtree(path);
    }

    // MARK: - Quota

    /// <summary><c>/users/usage</c> for the used bytes; the total from it or from <c>/users/limit</c>. Null when either fails.</summary>
    public async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken)
    {
        long used;
        long? total;
        try
        {
            using var usage = ParseJson(await DriveSendAsync("/users/usage", HttpMethod.Get, null, cancellationToken).ConfigureAwait(false));
            used = GetInt64(usage.RootElement, "used") ?? GetInt64(usage.RootElement, "drive") ?? 0;
            total = GetInt64(usage.RootElement, "total");
        }
        catch (CloudProviderException)
        {
            return null;
        }

        if (total is null)
        {
            try
            {
                using var limit = ParseJson(await DriveSendAsync("/users/limit", HttpMethod.Get, null, cancellationToken).ConfigureAwait(false));
                total = GetInt64(limit.RootElement, "maxSpaceBytes");
            }
            catch (CloudProviderException)
            {
            }
        }

        return new CloudStorageQuota(used, total);
    }

    // MARK: - Path resolution

    private async Task<Node> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = path.Length == 0 ? "/" : path;
        if (_nodeCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        // Walk down by listing each level, which populates the cache for its children.
        var currentPath = "/";
        foreach (var component in CloudPath.Components(normalized))
        {
            await ListFolderAsync(currentPath, cancellationToken).ConfigureAwait(false);
            currentPath = CloudPath.Combine(currentPath, component);
            if (!_nodeCache.ContainsKey(currentPath))
            {
                throw CloudProviderException.NotFound(currentPath);
            }
        }

        return _nodeCache.TryGetValue(normalized, out var node) ? node : throw CloudProviderException.NotFound(normalized);
    }

    // MARK: - Retries

    /// <summary>Retries an idempotent operation through transient gateway failures (HTTP 500/502/503/504, rate limits).</summary>
    private async Task<T> RetryingTransientAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken, int attempts = 3)
    {
        CloudProviderException? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (CloudProviderException e) when (IsTransient(e))
            {
                last = e;
                if (attempt < attempts - 1)
                {
                    await Task.Delay(RetryDelayUnit * (attempt + 1), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw last ?? CloudProviderException.InvalidResponse();
    }

    /// <summary>
    /// A delete through the same transient failures. Retrying one is safe in a way a create
    /// isn't: if the first attempt landed and only its answer was lost, the retry finds the
    /// item gone — the outcome that was asked for. NotFound is only swallowed after a transient
    /// failure, so deleting something that genuinely isn't there still says so.
    /// </summary>
    private async Task DeleteRetryingTransientAsync(string endpoint, CancellationToken cancellationToken, int attempts = 3)
    {
        var sawTransient = false;
        CloudProviderException? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                await DriveSendAsync(endpoint, HttpMethod.Delete, null, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound && sawTransient)
            {
                return;
            }
            catch (CloudProviderException e) when (IsTransient(e))
            {
                sawTransient = true;
                last = e;
                if (attempt < attempts - 1)
                {
                    await Task.Delay(RetryDelayUnit * (attempt + 1), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw last ?? CloudProviderException.InvalidResponse();
    }

    private static bool IsTransient(CloudProviderException error)
        => error.Kind == CloudErrorKind.RateLimited ||
           (error.Kind == CloudErrorKind.ServerError && error.StatusCode is 500 or 502 or 503 or 504);

    // MARK: - HTTP

    private async Task<byte[]> DriveSendAsync(string endpoint, HttpMethod method, string? body, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(method, DriveApi + endpoint, body, Credentials.Token);
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        Check(response, data);
        return data;
    }

    /// <summary>Network calls authenticate with HTTP Basic: the bridge user, and the hex SHA-256 of the user id.</summary>
    private async Task<byte[]> NetworkRequestAsync(string endpoint, HttpMethod method, string? body, bool apiVersion2, CancellationToken cancellationToken)
    {
        var password = InternxtCrypto.HexEncode(SHA256.HashData(Encoding.UTF8.GetBytes(Credentials.UserId)));
        using var request = BuildRequest(method, NetworkApi + endpoint, body, null);
        request.Headers.Authorization = CloudHttp.Basic(Credentials.BridgeUser, password);
        if (apiVersion2)
        {
            request.Headers.TryAddWithoutValidation("x-api-version", "2");
        }

        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        Check(response, data);
        return data;
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, string? body, string? bearer)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        request.Headers.TryAddWithoutValidation("internxt-version", ClientVersion);
        request.Headers.TryAddWithoutValidation("internxt-client", ClientName);
        if (bearer is not null)
        {
            request.Headers.Authorization = CloudHttp.Bearer(bearer);
        }

        return request;
    }

    private static void Check(HttpResponseMessage response, byte[] data)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        var snippet = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 300));
        SupportLogger.Shared.Log($"[Internxt] HTTP {status}: {snippet}", "Internxt", LogLevel.Error);
        throw status switch
        {
            401 => CloudProviderException.Unauthorized(),
            403 => CloudProviderException.NotAuthenticated(),
            404 => CloudProviderException.NotFound("Resource not found"),
            429 => CloudProviderException.RateLimited(),
            _ => CloudProviderException.ServerError(status),
        };
    }

    // MARK: - Helpers

    private static JsonDocument ParseJson(byte[] data)
    {
        try
        {
            return JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    private static List<JsonElement> Items(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToList()
            : [];

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Internxt sends numbers (sizes especially) as JSON numbers or strings; accept either.</summary>
    private static long? GetInt64(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            JsonValueKind.Number => (long)value.GetDouble(),
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    internal static DateTimeOffset ParseDate(string? value)
        => value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : CloudFileItem.UnknownDate;

    private static string InternetDate(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string ChildPath(string directory, string name) => directory == "/" ? "/" + name : directory + "/" + name;

    /// <summary>Splits like NSString's <c>pathExtension</c>: "a.tar.gz" → ("a.tar", "gz"); ".bashrc" and "file." have none.</summary>
    internal static (string BaseName, string Extension) SplitExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? (name[..dot], name[(dot + 1)..]) : (name, string.Empty);
    }
}

/// <summary>Streams the file through AES-256-CTR as it is sent, hashing the ciphertext (SHA-256) for the shard hash.</summary>
internal sealed class InternxtEncryptingContent : HttpContent
{
    private readonly string _localPath;
    private readonly long _size;
    private readonly byte[] _key;
    private readonly byte[] _iv;
    private readonly ByteProgress _onBytes;

    public InternxtEncryptingContent(string localPath, long size, byte[] key, byte[] iv, ByteProgress onBytes)
    {
        _localPath = localPath;
        _size = size;
        _key = key;
        _iv = iv;
        _onBytes = onBytes;
        Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    }

    /// <summary>SHA-256 of the ciphertext of the last complete serialization.</summary>
    public byte[]? CiphertextSha256 { get; private set; }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        CiphertextSha256 = null;
        using var ctr = new InternxtAesCtr(_key, _iv);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var file = HttpTransfer.OpenForUpload(_localPath);
        var buffer = new byte[HttpTransfer.BufferSize];
        var remaining = _size;
        while (remaining > 0)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            ctr.Transform(buffer.AsSpan(0, read));
            hash.AppendData(buffer, 0, read);
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
            _onBytes(read);
        }

        CiphertextSha256 = hash.GetHashAndReset();
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        length = _size;
        return true;
    }
}

/// <summary>Streams a file's shards in order, decrypting them through one continuous AES-CTR keystream.</summary>
internal sealed class InternxtShardStream : Stream
{
    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _urls;
    private readonly InternxtAesCtr _ctr;
    private int _next;
    private HttpResponseMessage? _response;
    private Stream? _current;

    public InternxtShardStream(HttpClient http, IReadOnlyList<string> urls, byte[] key, ReadOnlySpan<byte> iv)
    {
        _http = http;
        _urls = urls;
        _ctr = new InternxtAesCtr(key, iv);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_current is null)
            {
                if (_next >= _urls.Count)
                {
                    return 0;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, _urls[_next++]);
                _response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
                if (!_response.IsSuccessStatusCode)
                {
                    throw CloudProviderException.ServerError((int)_response.StatusCode);
                }

                _current = await _response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }

            var read = await _current.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                _ctr.Transform(buffer.Span[..read]);
                return read;
            }

            CloseShard();
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseShard();
            _ctr.Dispose();
        }

        base.Dispose(disposing);
    }

    private void CloseShard()
    {
        _current?.Dispose();
        _current = null;
        _response?.Dispose();
        _response = null;
    }
}
