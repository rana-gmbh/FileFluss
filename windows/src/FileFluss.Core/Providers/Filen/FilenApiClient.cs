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

namespace FileFluss.Core.Providers.Filen;

/// <summary>
/// Filen's gateway API, transliterated (like the macOS client) from filen-sdk-rs.
///
/// <list type="bullet">
/// <item><c>POST /v3/auth/info {email}</c> → <c>{authVersion, salt}</c></item>
/// <item><c>POST /v3/login {email, password, twoFactorCode, authVersion}</c> → <c>{apiKey, masterKeys}</c></item>
/// <item><c>GET /v3/user/baseFolder</c> → <c>{uuid}</c> of the Cloud Drive root</item>
/// <item><c>POST /v3/dir/content {uuid}</c> → <c>{folders, uploads}</c> with encrypted metadata</item>
/// <item>Chunks: <c>GET {egest}/{region}/{bucket}/{uuid}/{index}</c>,
/// <c>POST {ingest}/v3/upload?uuid&amp;index&amp;parent&amp;uploadKey&amp;hash</c></item>
/// </list>
/// Every JSON answer is wrapped in <c>{status, message, code, data}</c>.
/// </summary>
internal sealed class FilenApiClient
{
    internal const string BaseUrl = "https://gateway.filen.io";
    internal const string EgestUrl = "https://egest.filen.io";
    internal const string IngestUrl = "https://ingest.filen.io";

    /// <summary>Plaintext bytes per chunk; on the wire each chunk is 28 bytes longer (nonce + tag).</summary>
    internal const int PlaintextChunkSize = 1024 * 1024;

    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly byte[][] _masterAesKeys;

    /// <summary>
    /// Per-file data the download and rename paths need, harvested while listing (keyed by
    /// file UUID), so neither has to re-decrypt metadata. A fresh launch re-lists the parent.
    /// </summary>
    private readonly ConcurrentDictionary<string, FileChunkInfo> _fileChunkInfo = new(StringComparer.Ordinal);

    public FilenApiClient(HttpClient http, FilenCredentials credentials)
    {
        _http = http;
        Credentials = credentials;
        _masterAesKeys = credentials.MasterKeys.Select(FilenCrypto.AesKeyFromV2MasterKey).ToArray();
    }

    public FilenCredentials Credentials { get; }

    internal sealed record FileChunkInfo(string Key, string Region, string Bucket, int Chunks, long Size, string Mime);

    // MARK: - Authentication

    /// <summary>
    /// The full login. The password only lives for this call; what's returned (API key, master
    /// keys, root UUID) is what gets stored.
    /// </summary>
    public static async Task<FilenCredentials> LoginAsync(HttpClient http, string email, string password, string? twoFactorCode, CancellationToken cancellationToken)
    {
        var info = await PostAsync(http, null, "/v3/auth/info", new JsonObject { ["email"] = email }, cancellationToken).ConfigureAwait(false);
        if (GetString(info, "salt") is not { } salt || GetInt64(info, "authVersion") is not { } authVersion)
        {
            throw CloudProviderException.CommandFailed(Localization.L("Filen /v3/auth/info returned an unexpected payload."));
        }

        if (authVersion != 2)
        {
            throw CloudProviderException.CommandFailed(Localization.L(
                "Filen account uses auth version {0}. FileFluss currently supports v2 accounts only — Filen v1 / v3 (Argon2) are not yet implemented.",
                authVersion));
        }

        var derivation = await Task.Run(() => FilenCrypto.DeriveV2Login(password, salt), cancellationToken).ConfigureAwait(false);

        var login = await PostAsync(
            http,
            null,
            "/v3/login",
            new JsonObject
            {
                ["email"] = email,
                ["password"] = derivation.LoginPasswordHex,
                ["twoFactorCode"] = string.IsNullOrEmpty(twoFactorCode) ? "XXXXXX" : twoFactorCode,
                ["authVersion"] = 2,
            },
            cancellationToken).ConfigureAwait(false);
        var apiKey = GetString(login, "apiKey")
            ?? throw CloudProviderException.CommandFailed(Localization.L("Filen login succeeded but the server didn't return an apiKey."));

        // The master-keys blob is one "002" envelope under the derived key: "key1|key2|…".
        // Accounts that never logged in elsewhere may not have one; the derived key is then
        // the only key. The derived key goes first — it's what new metadata is written with.
        var masterKeys = new List<string> { derivation.MasterKeyHex };
        if (GetString(login, "masterKeys") is { Length: > 0 } blob)
        {
            var decoded = FilenCrypto.DecryptMetadata(blob, FilenCrypto.AesKeyFromV2MasterKey(derivation.MasterKeyHex));
            foreach (var key in decoded.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!masterKeys.Contains(key))
                {
                    masterKeys.Add(key);
                }
            }
        }

        var root = await FetchBaseFolderAsync(http, apiKey, cancellationToken).ConfigureAwait(false);
        return new FilenCredentials(email, apiKey, masterKeys.ToArray(), root);
    }

    /// <summary>
    /// <c>/v3/user/info</c>: <c>storage</c> used and <c>maxStorage</c>. A zero maximum surfaces
    /// as used-only rather than "0 of 0 bytes".
    /// </summary>
    public async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken)
    {
        var data = await PostAsync(_http, Credentials.ApiKey, "/v3/user/info", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var max = GetInt64(data, "maxStorage") ?? 0;
        return new CloudStorageQuota(GetInt64(data, "storage") ?? 0, max > 0 ? max : null);
    }

    // MARK: - Listing

    /// <summary>
    /// The children of a panel path. Filen is UUID-keyed, so each path segment means listing
    /// the parent and decrypting its children until the name matches.
    /// </summary>
    public async Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        var uuid = await ResolveUuidAsync(path, cancellationToken).ConfigureAwait(false);
        return await ListChildrenAsync(uuid, path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveUuidAsync(string path, CancellationToken cancellationToken)
    {
        var current = Credentials.RootFolderUuid;
        var currentPath = string.Empty;
        foreach (var segment in CloudPath.Components(path))
        {
            currentPath += "/" + segment;
            var children = await ListChildrenAsync(current, string.Empty, cancellationToken).ConfigureAwait(false);
            var match = children.FirstOrDefault(c => c.Name == segment && c.IsDirectory)
                ?? throw CloudProviderException.NotFound(currentPath);
            current = Uuid(match);
        }

        return current;
    }

    private async Task<List<CloudFileItem>> ListChildrenAsync(string parentUuid, string parentPath, CancellationToken cancellationToken)
    {
        var data = await PostAsync(_http, Credentials.ApiKey, "/v3/dir/content", new JsonObject { ["uuid"] = parentUuid }, cancellationToken).ConfigureAwait(false);
        var parentBase = parentPath.EndsWith('/') ? parentPath[..^1] : parentPath;
        var items = new List<CloudFileItem>();

        if (data.TryGetProperty("folders", out var folders) && folders.ValueKind == JsonValueKind.Array)
        {
            foreach (var folder in folders.EnumerateArray())
            {
                if (GetString(folder, "uuid") is not { } uuid || GetString(folder, "name") is not { } nameMeta)
                {
                    continue;
                }

                // Folders whose metadata doesn't decrypt (a key we no longer have) are skipped.
                if (TryDecryptItemMetadata(nameMeta) is not { } decoded || ParseFolderMeta(decoded) is not { } meta)
                {
                    continue;
                }

                var timestamp = GetDouble(folder, "timestamp") is { } ts ? TimestampToDate(ts) : CloudFileItem.UnknownDate;
                items.Add(new CloudFileItem($"d:{uuid}", meta.Name, ChildPath(parentBase, meta.Name), true, 0, meta.Created ?? timestamp));
            }
        }

        if (data.TryGetProperty("uploads", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                if (GetString(file, "uuid") is not { } uuid || GetString(file, "metadata") is not { } metadata)
                {
                    continue;
                }

                if (TryDecryptItemMetadata(metadata) is not { } decoded || ParseFileMeta(decoded) is not { } meta)
                {
                    continue;
                }

                _fileChunkInfo[uuid] = new FileChunkInfo(
                    meta.Key,
                    GetString(file, "region") ?? string.Empty,
                    GetString(file, "bucket") ?? string.Empty,
                    (int)(GetInt64(file, "chunks") ?? 0),
                    meta.Size,
                    meta.Mime);
                items.Add(new CloudFileItem($"f:{uuid}", meta.Name, ChildPath(parentBase, meta.Name), false, meta.Size, meta.LastModified, meta.Hash));
            }
        }

        return items;
    }

    // MARK: - Download

    /// <summary>
    /// Downloads the chunks in order and decrypts each with the file's key — the 32-char ASCII
    /// key string used as the raw AES-256 key (<c>FileKey::try_from</c>).
    /// </summary>
    public async Task DownloadAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        // Paths resolve to folders only, so list the parent and find the file by name; the
        // listing also refreshes the chunk info.
        var name = CloudPath.Name(remotePath);
        var entries = await ListFolderAsync(CloudPath.Parent(remotePath), cancellationToken).ConfigureAwait(false);
        var item = entries.FirstOrDefault(e => e.Name == name && !e.IsDirectory) ?? throw CloudProviderException.NotFound(remotePath);
        var uuid = Uuid(item);
        if (!_fileChunkInfo.TryGetValue(uuid, out var info))
        {
            throw CloudProviderException.CommandFailed(Localization.L("Filen file metadata not cached — listing didn't populate chunk info."));
        }

        var key = Encoding.UTF8.GetBytes(info.Key);
        if (key.Length != 32)
        {
            throw CloudProviderException.CommandFailed(Localization.L("Filen file key isn't 32 ASCII bytes — encrypted with an auth-version we don't support yet."));
        }

        // An empty file has no chunks; the staged write still produces the (empty) file.
        using var plain = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FilenChunkStream(this, uuid, info, key), HttpTransfer.BufferSize),
        };
        await HttpTransfer.SaveToFileAsync(plain, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<byte[]> DownloadChunkAsync(FileChunkInfo info, string uuid, int index, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{EgestUrl}/{info.Region}/{info.Bucket}/{uuid}/{index}");
        request.Headers.Authorization = CloudHttp.Bearer(Credentials.ApiKey);
        using var response = await _http.SendApiAsync(request, cancellationToken, ChunkTimeout).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CloudHttp.StatusError(response.StatusCode);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    // MARK: - Upload

    /// <summary>
    /// Encrypts 1 MiB chunks with a fresh per-file key and posts each to the ingest host, then
    /// publishes the file with <c>/v3/upload/done</c> (filen-sdk-rs <c>FileWriter</c>). Streams
    /// the file chunk by chunk (macOS read it whole into memory).
    /// </summary>
    public async Task UploadAsync(string localPath, string remotePath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var name = CloudPath.Name(remotePath);
        var parentUuid = await ResolveUuidAsync(CloudPath.Parent(remotePath), cancellationToken).ConfigureAwait(false);

        var fileInfo = new FileInfo(localPath);
        var size = fileInfo.Length;
        var mime = GuessMimeType(name);

        // The per-file key string: its raw 32 bytes encrypt the chunks; fed through the V2Key
        // derivation it encrypts the standalone name/size/mime fields.
        var fileKeyAscii = FilenCrypto.RandomString(32);
        var fileKey = Encoding.UTF8.GetBytes(fileKeyAscii);
        var fileMetaKey = FilenCrypto.AesKeyFromV2MasterKey(fileKeyAscii);
        var masterKey = ActiveMasterKey();
        var uploadKey = FilenCrypto.RandomString(32);
        var rmKey = FilenCrypto.RandomString(32);
        var fileUuid = Guid.NewGuid().ToString("D");

        // lastModified in milliseconds, as Filen's own clients write it (macOS wrote seconds,
        // which they show as 1970; the reader here accepts both).
        var metadata = new JsonObject
        {
            ["name"] = name,
            ["size"] = size,
            ["mime"] = mime,
            ["key"] = fileKeyAscii,
            ["lastModified"] = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
        };

        var body = new JsonObject
        {
            ["uuid"] = fileUuid,
            ["name"] = FilenCrypto.EncryptMetadata(name, fileMetaKey),
            ["nameHashed"] = FilenCrypto.V2HashName(name),
            ["size"] = FilenCrypto.EncryptMetadata(size.ToString(CultureInfo.InvariantCulture), fileMetaKey),
            ["parent"] = parentUuid,
            ["mime"] = FilenCrypto.EncryptMetadata(mime, fileMetaKey),
            ["metadata"] = FilenCrypto.EncryptMetadata(metadata.ToJsonString(), masterKey),
            ["version"] = 2,
        };

        // An empty file is published with /v3/upload/empty: metadata, no chunks.
        if (size == 0)
        {
            await PostAsync(_http, Credentials.ApiKey, "/v3/upload/empty", body, cancellationToken).ConfigureAwait(false);
            return;
        }

        var chunkCount = (int)((size + PlaintextChunkSize - 1) / PlaintextChunkSize);
        var buffer = new byte[PlaintextChunkSize];
        await using (var file = HttpTransfer.OpenForUpload(localPath))
        {
            for (var index = 0; index < chunkCount; index++)
            {
                var length = (int)Math.Min(PlaintextChunkSize, size - ((long)index * PlaintextChunkSize));
                await file.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                var encrypted = FilenCrypto.EncryptFileChunk(buffer.AsSpan(0, length), fileKey);
                var hash = Convert.ToHexStringLower(SHA512.HashData(encrypted));
                await UploadChunkAsync(fileUuid, parentUuid, uploadKey, index, hash, encrypted, cancellationToken).ConfigureAwait(false);
                onBytes?.Invoke(length);
            }
        }

        body["chunks"] = chunkCount;
        body["rm"] = rmKey;
        body["uploadKey"] = uploadKey;
        await PostAsync(_http, Credentials.ApiKey, "/v3/upload/done", body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The chunk body is the raw encrypted bytes; its SHA-512 (hex) goes in the URL, exactly as filen-sdk-rs builds it.</summary>
    private async Task UploadChunkAsync(string uuid, string parent, string uploadKey, int index, string hashHex, byte[] encrypted, CancellationToken cancellationToken)
    {
        var url = $"{IngestUrl}/v3/upload?uuid={uuid}&index={index}&parent={parent}&uploadKey={uploadKey}&hash={hashHex}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(encrypted) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Authorization = CloudHttp.Bearer(Credentials.ApiKey);
        using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var preview = await response.ReadBodySnippetAsync(cancellationToken, 200).ConfigureAwait(false);
            SupportLogger.Shared.Log($"[Filen] upload chunk {index} failed: HTTP {(int)response.StatusCode}: {preview}", "Filen", LogLevel.Error);
            throw CloudHttp.StatusError(response.StatusCode);
        }
    }

    // MARK: - Folders, trash, rename

    /// <summary>
    /// Creates a folder whose name is stored encrypted under the master key, with the v2 name
    /// hash for the server's index. Succeeds quietly when the folder exists (macOS created a
    /// duplicate).
    /// </summary>
    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        var name = CloudPath.Name(path);
        var parentUuid = await ResolveUuidAsync(CloudPath.Parent(path), cancellationToken).ConfigureAwait(false);
        var siblings = await ListChildrenAsync(parentUuid, string.Empty, cancellationToken).ConfigureAwait(false);
        if (siblings.Any(s => s.IsDirectory && s.Name == name))
        {
            return;
        }

        var folderMeta = new JsonObject { ["name"] = name, ["creation"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        await PostAsync(
            _http,
            Credentials.ApiKey,
            "/v3/dir/create",
            new JsonObject
            {
                ["uuid"] = Guid.NewGuid().ToString("D"),
                ["name"] = FilenCrypto.EncryptMetadata(folderMeta.ToJsonString(), ActiveMasterKey()),
                ["nameHashed"] = FilenCrypto.V2HashName(name),
                ["parent"] = parentUuid,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves a file or folder to Filen's trash (not a hard delete).</summary>
    public async Task TrashAsync(string path, CancellationToken cancellationToken)
    {
        var item = await FindAsync(path, cancellationToken).ConfigureAwait(false);
        var endpoint = item.IsDirectory ? "/v3/dir/trash" : "/v3/file/trash";
        await PostAsync(_http, Credentials.ApiKey, endpoint, new JsonObject { ["uuid"] = Uuid(item) }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Folders get a new encrypted folder-meta blob; files a new master-key metadata blob (so
    /// listings show the name) plus the standalone name under the file's own meta key.
    /// </summary>
    public async Task RenameAsync(string path, string newName, CancellationToken cancellationToken)
    {
        var item = await FindAsync(path, cancellationToken).ConfigureAwait(false);
        var uuid = Uuid(item);
        var masterKey = ActiveMasterKey();
        var nameHashed = FilenCrypto.V2HashName(newName);

        if (item.IsDirectory)
        {
            var folderMeta = new JsonObject { ["name"] = newName, ["creation"] = UnixMilliseconds(item.ModificationDate) };
            await PostAsync(
                _http,
                Credentials.ApiKey,
                "/v3/dir/metadata",
                new JsonObject
                {
                    ["uuid"] = uuid,
                    ["nameHashed"] = nameHashed,
                    ["name"] = FilenCrypto.EncryptMetadata(folderMeta.ToJsonString(), masterKey),
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_fileChunkInfo.TryGetValue(uuid, out var info))
        {
            throw CloudProviderException.CommandFailed(Localization.L("Filen file metadata not cached — re-open the folder and try again."));
        }

        // The original MIME type is kept (macOS reset it to application/octet-stream).
        var fileMeta = new JsonObject
        {
            ["name"] = newName,
            ["size"] = info.Size,
            ["mime"] = info.Mime,
            ["key"] = info.Key,
            ["lastModified"] = UnixMilliseconds(item.ModificationDate),
        };
        await PostAsync(
            _http,
            Credentials.ApiKey,
            "/v3/file/metadata",
            new JsonObject
            {
                ["uuid"] = uuid,
                ["name"] = FilenCrypto.EncryptMetadata(newName, FilenCrypto.AesKeyFromV2MasterKey(info.Key)),
                ["nameHashed"] = nameHashed,
                ["metadata"] = FilenCrypto.EncryptMetadata(fileMeta.ToJsonString(), masterKey),
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The item at <paramref name="path"/>, found in its parent's listing (which also refreshes its chunk info).</summary>
    public async Task<CloudFileItem> FindAsync(string path, CancellationToken cancellationToken)
    {
        var name = CloudPath.Name(path);
        var entries = await ListFolderAsync(CloudPath.Parent(path), cancellationToken).ConfigureAwait(false);
        return entries.FirstOrDefault(e => e.Name == name) ?? throw CloudProviderException.NotFound(path);
    }

    // MARK: - Keys and metadata

    /// <summary>The freshest master key (index 0, the one login derived) encrypts everything new.</summary>
    private byte[] ActiveMasterKey() => _masterAesKeys.Length > 0 ? _masterAesKeys[0] : throw CloudProviderException.NotAuthenticated();

    /// <summary>Tries every master key in order — older items may still be under an older key.</summary>
    private string? TryDecryptItemMetadata(string encrypted)
    {
        foreach (var key in _masterAesKeys)
        {
            try
            {
                return FilenCrypto.DecryptMetadata(encrypted, key);
            }
            catch (CloudProviderException)
            {
            }
        }

        return null;
    }

    private static (string Name, DateTimeOffset? Created)? ParseFolderMeta(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (GetString(root, "name") is not { } name)
            {
                return null;
            }

            return (name, GetDouble(root, "creation") is { } created ? TimestampToDate(created) : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string Name, long Size, string Mime, string Key, DateTimeOffset LastModified, string? Hash)? ParseFileMeta(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (GetString(root, "name") is not { } name || GetString(root, "key") is not { } key)
            {
                return null;
            }

            return (
                name,
                GetInt64(root, "size") ?? 0,
                GetString(root, "mime") ?? "application/octet-stream",
                key,
                GetDouble(root, "lastModified") is { } modified ? TimestampToDate(modified) : CloudFileItem.UnknownDate,
                GetString(root, "blake3"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // MARK: - HTTP

    /// <summary>Unlike the other v3 endpoints this one is a GET (filen-sdk-rs <c>get_auth</c>); a POST gets a "wrong method" envelope.</summary>
    private static async Task<string> FetchBaseFolderAsync(HttpClient http, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/v3/user/baseFolder");
        request.Headers.Authorization = CloudHttp.Bearer(apiKey);
        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CloudProviderException.CommandFailed(Localization.L("Filen /v3/user/baseFolder failed (HTTP {0})", (int)response.StatusCode));
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("data", out var data) &&
                GetString(data, "uuid") is { } uuid)
            {
                return uuid;
            }
        }
        catch (JsonException)
        {
        }

        throw CloudProviderException.CommandFailed(Localization.L("Filen /v3/user/baseFolder didn't return a root UUID."));
    }

    private static async Task<JsonElement> PostAsync(HttpClient http, string? apiKey, string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (apiKey is not null)
        {
            request.Headers.Authorization = CloudHttp.Bearer(apiKey);
        }

        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        return await ParseEnvelopeAsync(response, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Unwraps <c>{status, message, code, data}</c>: <c>data</c> on success, the server's own
    /// message as the error otherwise. Filen's "enter_2fa"/"wrong_2fa" codes become
    /// <see cref="CloudErrorKind.TwoFactorRequired"/> so sign-in can ask for the code.
    /// </summary>
    private static async Task<JsonElement> ParseEnvelopeAsync(HttpResponseMessage response, string endpoint, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var snippet = await response.ReadBodySnippetAsync(cancellationToken, 300).ConfigureAwait(false);
            SupportLogger.Shared.Log($"[Filen] {endpoint} HTTP {(int)response.StatusCode}: {snippet}", "Filen", LogLevel.Error);
            throw (int)response.StatusCode switch
            {
                401 => CloudProviderException.Unauthorized(),
                403 => CloudProviderException.NotAuthenticated(),
                404 => CloudProviderException.NotFound(endpoint),
                429 => CloudProviderException.RateLimited(),
                _ => CloudProviderException.ServerError((int)response.StatusCode),
            };
        }

        JsonElement envelope;
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            envelope = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }

        if (envelope.ValueKind != JsonValueKind.Object)
        {
            throw CloudProviderException.InvalidResponse();
        }

        if (envelope.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.False)
        {
            var code = GetString(envelope, "code");
            throw code switch
            {
                "enter_2fa" or "wrong_2fa" => CloudProviderException.TwoFactorRequired(),
                "api_key_not_found" => CloudProviderException.Unauthorized(),
                _ => CloudProviderException.CommandFailed(GetString(envelope, "message") ?? Localization.L("Filen API error")),
            };
        }

        return envelope.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : envelope;
    }

    // MARK: - Helpers

    private static string Uuid(CloudFileItem item) => item.Id.Length > 2 ? item.Id[2..] : item.Id;

    private static string ChildPath(string parentBase, string name) => parentBase.Length == 0 ? "/" + name : parentBase + "/" + name;

    private static long UnixMilliseconds(DateTimeOffset date)
        => date > CloudFileItem.UnknownDate ? date.ToUnixTimeMilliseconds() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Filen sends seconds or milliseconds depending on the endpoint; anything past the year 3000 in seconds is milliseconds.</summary>
    internal static DateTimeOffset TimestampToDate(double raw)
    {
        var seconds = raw > 32_503_680_000 ? raw / 1000 : raw;
        if (seconds <= 0 || double.IsNaN(seconds) || seconds > 253_402_300_799)
        {
            return CloudFileItem.UnknownDate;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
    }

    internal static string GuessMimeType(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        var extension = dot > 0 ? fileName[(dot + 1)..].ToLowerInvariant() : string.Empty;
        return extension switch
        {
            "txt" or "md" or "log" => "text/plain",
            "json" => "application/json",
            "xml" => "application/xml",
            "html" or "htm" => "text/html",
            "pdf" => "application/pdf",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "mp4" => "video/mp4",
            "mov" => "video/quicktime",
            "mp3" => "audio/mpeg",
            "zip" => "application/zip",
            _ => "application/octet-stream",
        };
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    private static double? GetDouble(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}

/// <summary>Streams a Filen file's plaintext: fetches and decrypts one chunk at a time, in order.</summary>
internal sealed class FilenChunkStream : Stream
{
    private readonly FilenApiClient _client;
    private readonly string _uuid;
    private readonly FilenApiClient.FileChunkInfo _info;
    private readonly byte[] _key;
    private byte[] _current = [];
    private int _offset;
    private int _nextChunk;

    public FilenChunkStream(FilenApiClient client, string uuid, FilenApiClient.FileChunkInfo info, byte[] key)
    {
        _client = client;
        _uuid = uuid;
        _info = info;
        _key = key;
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
        while (_offset >= _current.Length)
        {
            if (_nextChunk >= _info.Chunks)
            {
                return 0;
            }

            var encrypted = await _client.DownloadChunkAsync(_info, _uuid, _nextChunk, cancellationToken).ConfigureAwait(false);
            _current = FilenCrypto.DecryptFileChunk(encrypted, _key);
            _offset = 0;
            _nextChunk++;
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, count).CopyTo(buffer.Span);
        _offset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
