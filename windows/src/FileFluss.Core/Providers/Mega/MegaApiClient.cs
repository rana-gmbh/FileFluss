// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

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

namespace FileFluss.Core.Providers.Mega;

/// <summary>
/// MEGA's JSON command API (<c>POST https://g.api.mega.co.nz/cs</c>, a batch of commands per
/// request) plus the account's decrypted node tree. MEGA addresses everything by handle and
/// stores names encrypted, so the whole tree is fetched once (<c>a: f</c>), decrypted with the
/// master key and cached; paths resolve against that cache.
/// </summary>
internal sealed class MegaApiClient
{
    internal const string ApiUrl = "https://g.api.mega.co.nz/cs";

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly byte[] _masterKey;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _fetchGate = new(1, 1);

    // Guarded by _gate. Handle → node, and handle → decrypted node key (8 words for files —
    // [k0^n0, k1^n1, k2^m0, k3^m1, n0, n1, m0, m1] — or 4 words for folders).
    private Dictionary<string, MegaNode> _nodes = new(StringComparer.Ordinal);
    private Dictionary<string, uint[]> _nodeKeys = new(StringComparer.Ordinal);
    private string? _rootHandle;

    public MegaApiClient(HttpClient http, MegaCredentials credentials)
    {
        _http = http;
        Credentials = credentials;
        _masterKey = MegaCrypto.ToBytes(credentials.MasterKey);
    }

    public MegaCredentials Credentials { get; }

    /// <summary>Back-off unit between EAGAIN (-3) retries; tests shorten it.</summary>
    internal TimeSpan RetryDelayUnit { get; set; } = TimeSpan.FromMilliseconds(500);

    // MARK: - Authentication

    /// <summary>
    /// Pre-login (<c>us0</c>) tells the account version and salt; v2 derives the password key
    /// and user handle with PBKDF2, v1 with <c>prepare_key</c> + <c>stringhash</c>. The login
    /// (<c>us</c>) returns the encrypted master key and either an RSA-encrypted session id
    /// (<c>csid</c> + <c>privk</c>) or a temporary one (<c>tsid</c>).
    /// </summary>
    public static async Task<MegaCredentials> LoginAsync(
        HttpClient http,
        string email,
        string password,
        string? mfaCode,
        Action<string?>? status,
        CancellationToken cancellationToken)
    {
        var preLogin = await RequestAsync(http, null, Batch(new JsonObject { ["a"] = "us0", ["user"] = email }), status, cancellationToken).ConfigureAwait(false);
        var info = First(preLogin);
        if (info.ValueKind != JsonValueKind.Object || info.GetInt64("v") is not { } version)
        {
            throw CloudProviderException.InvalidResponse();
        }

        byte[] passwordKey;
        string userHandle;
        if (version == 2)
        {
            var salt = MegaCrypto.Base64UrlDecode(info.GetString("s")) ?? throw CloudProviderException.InvalidResponse();
            var derived = await Task.Run(() => MegaCrypto.DeriveV2(password, salt), cancellationToken).ConfigureAwait(false);
            passwordKey = derived[..16];
            userHandle = MegaCrypto.Base64UrlEncode(derived.AsSpan(16, 16));
        }
        else
        {
            passwordKey = await Task.Run(() => MegaCrypto.PrepareKeyV1(password), cancellationToken).ConfigureAwait(false);
            userHandle = MegaCrypto.StringHash(email, passwordKey);
        }

        var login = new JsonObject { ["a"] = "us", ["user"] = email, ["uh"] = userHandle };
        if (version == 2)
        {
            login["sek"] = null; // no session encryption key
        }

        if (!string.IsNullOrEmpty(mfaCode))
        {
            // The 6-digit TOTP goes in "mfa"; without it a 2FA account answers -26 (EMFAREQUIRED).
            login["mfa"] = mfaCode;
        }

        JsonElement loginResult;
        try
        {
            loginResult = await RequestAsync(http, null, Batch(login), status, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            // ENOENT (-9) from `us` means the email/password pair didn't match; "Item not
            // found" (what macOS showed) would be meaningless in the sign-in form.
            throw CloudProviderException.InvalidCredentials();
        }

        var data = First(loginResult);
        if (data.ValueKind != JsonValueKind.Object)
        {
            throw CloudProviderException.InvalidResponse();
        }

        var encryptedMasterKey = MegaCrypto.Base64UrlDecode(data.GetString("k"));
        if (encryptedMasterKey is not { Length: >= 16 })
        {
            throw CloudProviderException.InvalidResponse();
        }

        var masterKey = MegaCrypto.DecryptEcb(passwordKey, encryptedMasterKey.AsSpan(0, 16));

        string sessionId;
        if (data.GetString("tsid") is { } tsid && MegaCrypto.Base64UrlDecode(tsid) is { Length: >= 32 } tsidBytes)
        {
            // Temporary session: the first 16 bytes encrypted with the master key must equal the
            // last 16 (the web client's check; the Swift port decrypted instead and rejected
            // every tsid).
            var check = MegaCrypto.EncryptEcb(masterKey, tsidBytes.AsSpan(0, 16));
            if (!check.AsSpan().SequenceEqual(tsidBytes.AsSpan(tsidBytes.Length - 16)))
            {
                throw CloudProviderException.InvalidCredentials();
            }

            sessionId = tsid;
        }
        else if (data.GetString("csid") is { } csid && data.GetString("privk") is { } privk)
        {
            // RSA session: decrypt the private key (p, q, d, u as MPIs) with the master key,
            // then the session id with the private key. The id is its first 43 bytes.
            var privateKeyData = MegaCrypto.Base64UrlDecode(privk) ?? throw CloudProviderException.InvalidResponse();
            var privateKey = MegaCrypto.ReadMpiSequence(MegaCrypto.DecryptEcb(masterKey, privateKeyData), 4);
            var csidData = MegaCrypto.Base64UrlDecode(csid) ?? throw CloudProviderException.InvalidResponse();
            var encryptedSid = MegaCrypto.ReadMpi(csidData, out _) ?? [];
            var sid = MegaCrypto.RsaDecrypt(encryptedSid, privateKey);
            sessionId = MegaCrypto.Base64UrlEncode(sid.AsSpan(0, Math.Min(43, sid.Length)));
        }
        else
        {
            throw CloudProviderException.InvalidResponse();
        }

        SupportLogger.Shared.Log($"[Mega] Successfully authenticated as {email}", "Mega");
        return new MegaCredentials(sessionId, MegaCrypto.ToWords(masterKey), email);
    }

    // MARK: - Node tree

    public async Task FetchNodesAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync(new JsonObject { ["a"] = "f", ["c"] = 1 }, cancellationToken, FetchTimeout).ConfigureAwait(false);
        var payload = First(result);
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("f", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            throw CloudProviderException.InvalidResponse();
        }

        var nodes = new Dictionary<string, MegaNode>(StringComparer.Ordinal);
        var keys = new Dictionary<string, uint[]>(StringComparer.Ordinal);
        string? root = null;
        using var masterAes = MegaCrypto.CreateAes(_masterKey);
        foreach (var nodeData in files.EnumerateArray())
        {
            if (nodeData.GetString("h") is not { } handle || nodeData.GetInt64("t") is not { } typeValue)
            {
                continue;
            }

            var type = (int)typeValue;
            string name;
            switch (type)
            {
                case 2:
                    name = "Cloud Drive";
                    root = handle;
                    break;
                case 3:
                    name = "Inbox";
                    break;
                case 4:
                    name = "Trash";
                    break;
                default:
                    name = nodeData.GetString("a") is { } attributes && nodeData.GetString("k") is { } keyField
                        ? DecryptNodeName(masterAes, attributes, keyField, handle, keys) ?? "Unknown"
                        : "Unknown";
                    break;
            }

            nodes[handle] = new MegaNode(
                handle,
                nodeData.GetString("p") ?? string.Empty,
                name,
                type,
                nodeData.GetInt64("s") ?? 0,
                nodeData.GetDouble("ts") ?? 0);
        }

        lock (_gate)
        {
            _nodes = nodes;
            _nodeKeys = keys;
            _rootHandle = root;
        }

        SupportLogger.Shared.Log($"[Mega] Loaded {nodes.Count} nodes", "Mega");
    }

    public async Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        await EnsureNodesAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var parent = ResolveHandleLocked(path);
            return _nodes.Values
                .Where(n => n.ParentHandle == parent && n.Type <= 1)
                .Select(n => n.ToItem(ChildPath(path, n.Name)))
                .OrderBy(i => i.IsDirectory ? 0 : 1)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public async Task<CloudFileItem> StatAsync(string path, CancellationToken cancellationToken)
    {
        await EnsureNodesAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var handle = ResolveHandleLocked(path);
            return _nodes.TryGetValue(handle, out var node) ? node.ToItem(path) : throw CloudProviderException.NotFound(path);
        }
    }

    public async Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken)
    {
        await EnsureNodesAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var childrenByParent = _nodes.Values.ToLookup(n => n.ParentHandle, StringComparer.Ordinal);
            long total = 0;
            var pending = new Stack<string>();
            pending.Push(ResolveHandleLocked(path));
            while (pending.Count > 0)
            {
                foreach (var node in childrenByParent[pending.Pop()])
                {
                    if (node.Type == 1)
                    {
                        pending.Push(node.Handle);
                    }
                    else
                    {
                        total += node.Size;
                    }
                }
            }

            return total;
        }
    }

    /// <summary>Case-insensitive name search over the cached tree, below <paramref name="path"/> or the whole drive.</summary>
    public async Task<IReadOnlyList<CloudFileItem>> SearchAsync(string query, string? path, CancellationToken cancellationToken)
    {
        await EnsureNodesAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            string? searchRoot = null;
            if (path is not null && !CloudPath.IsRoot(path))
            {
                try
                {
                    searchRoot = ResolveHandleLocked(path);
                }
                catch (CloudProviderException)
                {
                    searchRoot = null;
                }
            }

            // Only results inside Cloud Drive: Inbox and Trash entries have no path a panel
            // can open (macOS returned them with "/Trash/…" paths that then failed to resolve).
            searchRoot ??= _rootHandle;
            return _nodes.Values
                .Where(n => n.Type <= 1 && n.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .Where(n => searchRoot is null || IsDescendantLocked(n.Handle, searchRoot))
                .Select(n => n.ToItem(BuildPathLocked(n.Handle)))
                .ToList();
        }
    }

    // MARK: - Transfers

    public async Task DownloadAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        await EnsureNodesAsync(cancellationToken).ConfigureAwait(false);
        string handle;
        bool hasKey;
        lock (_gate)
        {
            handle = ResolveHandleLocked(remotePath);
            if (!_nodes.TryGetValue(handle, out var node) || !node.IsFile)
            {
                throw CloudProviderException.NotFound(remotePath);
            }

            hasKey = _nodeKeys.ContainsKey(handle);
        }

        if (!hasKey)
        {
            SupportLogger.Shared.Log("[Mega] Key missing for a file, re-fetching node tree", "Mega");
            await RefetchNodesAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = await CallAsync(new JsonObject { ["a"] = "g", ["g"] = 1, ["n"] = handle }, cancellationToken).ConfigureAwait(false);
        var url = First(result).GetString("g") ?? throw CloudProviderException.InvalidResponse();

        uint[]? key;
        lock (_gate)
        {
            _nodeKeys.TryGetValue(handle, out key);
        }

        // macOS wrote the still-encrypted bytes when the key was missing; a file that looks
        // complete but is ciphertext is worse than an error.
        if (key is not { Length: >= 8 })
        {
            throw CloudProviderException.InvalidResponse();
        }

        var contentKey = MegaCrypto.AttributeKey(key);
        var counter = new byte[16];
        MegaCrypto.ToBytes([key[4], key[5]]).CopyTo(counter, 0);
        var storedMac = MegaCrypto.ToBytes([key[6], key[7]]);

        using var request = new HttpRequestMessage(HttpMethod.Get, ForceHttps(url));
        using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        CloudHttp.ThrowForStatus(response, remotePath);

        var cipher = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        // Files uploaded by FileFluss for macOS carry an all-zero MAC (it never computed one),
        // so only a real MAC is checked.
        var verify = storedMac.Any(b => b != 0) ? storedMac : null;
        using var plain = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MegaDecryptingStream(cipher, contentKey, counter, verify), HttpTransfer.BufferSize),
        };
        await HttpTransfer.SaveToFileAsync(plain, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Encrypts with a fresh random key and nonce (AES-CTR) while computing MEGA's file MAC, and
    /// posts the ciphertext chunk by chunk to <c>{uploadUrl}/{offset}</c> (the last chunk's
    /// answer is the completion handle); then creates the node with the key, nonce and
    /// meta-MAC folded into its 8-word key.
    /// <para>macOS read the whole file into memory, posted it in one request and left the MAC
    /// zero — which MEGA accepts, but its own apps then flag the file as corrupt.</para>
    /// </summary>
    public async Task UploadAsync(string localPath, string folderPath, string fileName, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        await EnsureNodesAsync(cancellationToken).ConfigureAwait(false);
        var parentHandle = ResolveHandle(folderPath);
        var fileSize = new FileInfo(localPath).Length;

        var uploadResult = await CallAsync(new JsonObject { ["a"] = "u", ["s"] = fileSize }, cancellationToken).ConfigureAwait(false);
        var uploadUrl = ForceHttps(First(uploadResult).GetString("p") ?? throw CloudProviderException.InvalidResponse());

        var contentKey = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(8);
        var counter = new byte[16];
        nonce.CopyTo(counter, 0);

        string? completionHandle = null;
        byte[] metaMac;
        using (var ctr = new MegaAesCtr(contentKey, counter))
        using (var mac = new MegaMac(contentKey, nonce))
        await using (var file = HttpTransfer.OpenForUpload(localPath))
        {
            if (fileSize == 0)
            {
                completionHandle = await PostChunkAsync(uploadUrl, 0, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            }

            var buffer = new byte[MegaMac.ChunkSize(8)];
            foreach (var (offset, length) in MegaMac.Chunks(fileSize))
            {
                await file.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                mac.Update(buffer.AsSpan(0, length));
                ctr.Transform(buffer.AsSpan(0, length));
                var answer = await PostChunkAsync(uploadUrl, offset, buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                if (answer.Length > 0)
                {
                    completionHandle = answer;
                }

                onBytes?.Invoke(length);
            }

            metaMac = mac.FinishMetaMac();
        }

        if (string.IsNullOrEmpty(completionHandle))
        {
            throw CloudProviderException.InvalidResponse();
        }

        var nodeKey = MegaCrypto.ComposeFileKey(contentKey, nonce, metaMac);
        var put = await CallAsync(
            new JsonObject
            {
                ["a"] = "p",
                ["t"] = parentHandle,
                ["n"] = new JsonArray(new JsonObject
                {
                    ["h"] = completionHandle,
                    ["t"] = 0,
                    ["a"] = MegaCrypto.EncryptAttributes(fileName, contentKey),
                    ["k"] = MegaCrypto.Base64UrlEncode(MegaCrypto.EncryptEcb(_masterKey, MegaCrypto.ToBytes(nodeKey))),
                }),
            },
            cancellationToken).ConfigureAwait(false);

        var newHandles = CacheCreatedNodes(put, fileName, fileSize, nodeKey);

        // MEGA's `a: p` always creates a new node even when a sibling has the same name, so a
        // "replace" would leave two files (and a delete would remove only one). Once the new
        // node is committed, remove older files with the same parent and name.
        List<string> stale;
        lock (_gate)
        {
            stale = _nodes
                .Where(e => !newHandles.Contains(e.Key) && e.Value.ParentHandle == parentHandle && e.Value.Name == fileName && e.Value.IsFile)
                .Select(e => e.Key)
                .ToList();
        }

        foreach (var handle in stale)
        {
            try
            {
                await CallAsync(new JsonObject { ["a"] = "d", ["n"] = handle }, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException)
            {
                // Best effort, as on macOS: the new file is in place either way.
            }

            lock (_gate)
            {
                _nodes.Remove(handle);
                _nodeKeys.Remove(handle);
            }
        }
    }

    // MARK: - Mutations

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        await EnsureNodesAsync(cancellationToken).ConfigureAwait(false);
        var handle = ResolveHandle(path);
        await CallAsync(new JsonObject { ["a"] = "d", ["n"] = handle }, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _nodes.Remove(handle);
            _nodeKeys.Remove(handle);
        }
    }

    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        await EnsureNodesAsync(cancellationToken).ConfigureAwait(false);

        // Idempotent: MEGA allows duplicate folder names, so a naive create produces many
        // siblings with the same name.
        if (TryResolveHandle(path) is not null)
        {
            return;
        }

        var folderName = CloudPath.Name(path);
        var parentHandle = ResolveHandle(CloudPath.Parent(path));
        var folderKey = RandomNumberGenerator.GetBytes(16);
        var result = await CallAsync(
            new JsonObject
            {
                ["a"] = "p",
                ["t"] = parentHandle,
                ["n"] = new JsonArray(new JsonObject
                {
                    ["h"] = "xxxxxxxx",
                    ["t"] = 1,
                    ["a"] = MegaCrypto.EncryptAttributes(folderName, folderKey),
                    ["k"] = MegaCrypto.Base64UrlEncode(MegaCrypto.EncryptEcb(_masterKey, folderKey)),
                }),
            },
            cancellationToken).ConfigureAwait(false);

        // The folder key is cached too (macOS didn't, so renaming a folder it had just created
        // encrypted the new name with the master key).
        CacheCreatedNodes(result, folderName, 0, MegaCrypto.ToWords(folderKey));
    }

    public async Task RenameAsync(string path, string newName, CancellationToken cancellationToken)
    {
        await EnsureNodesAsync(cancellationToken).ConfigureAwait(false);
        var handle = ResolveHandle(path);
        var key = NodeKey(handle);
        if (key is null)
        {
            await RefetchNodesAsync(cancellationToken).ConfigureAwait(false);
            key = NodeKey(handle) ?? throw CloudProviderException.InvalidResponse();
        }

        // The attribute command takes the new attributes as "at" (macOS sent "attr").
        var attributes = MegaCrypto.EncryptAttributes(newName, MegaCrypto.AttributeKey(key));
        await CallAsync(new JsonObject { ["a"] = "a", ["n"] = handle, ["at"] = attributes }, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (_nodes.TryGetValue(handle, out var node))
            {
                _nodes[handle] = node with { Name = newName };
            }
        }
    }

    /// <summary>
    /// The <c>uq</c> command: <c>cstrg</c> is the storage used, <c>mstrg</c> the plan's maximum.
    /// A missing or zero maximum surfaces as used-only.
    /// </summary>
    public async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync(new JsonObject { ["a"] = "uq", ["strg"] = 1, ["v"] = 1 }, cancellationToken).ConfigureAwait(false);
        var first = First(result);
        if (first.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var total = first.GetInt64("mstrg");
        return new CloudStorageQuota(first.GetInt64("cstrg") ?? 0, total is > 0 ? total : null);
    }

    // MARK: - HTTP

    /// <summary>One command, retried on EAGAIN (-3) with a growing back-off — MEGA sends it as a bare integer when overloaded.</summary>
    private async Task<JsonElement> CallAsync(JsonObject command, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var body = Batch(command);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await RequestAsync(_http, Credentials.SessionId, body, null, cancellationToken, timeout).ConfigureAwait(false);
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.ServerError && e.StatusCode == -3 && attempt < 4)
            {
                await Task.Delay(RetryDelayUnit * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<JsonElement> RequestAsync(
        HttpClient http,
        string? sessionId,
        string body,
        Action<string?>? status,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var sequence = Random.Shared.NextInt64(0, 0x1_0000_0000);
        var url = $"{ApiUrl}?id={sequence}" + (sessionId is null ? string.Empty : "&sid=" + Uri.EscapeDataString(sessionId));

        using var request = BuildRequest(url, body, null);
        using var response = await http.SendApiAsync(request, cancellationToken, timeout).ConfigureAwait(false);
        if ((int)response.StatusCode == 402)
        {
            // Anti-abuse proof-of-work: solve the X-Hashcash challenge and send the request again.
            var challenge = response.Headers.TryGetValues("X-Hashcash", out var values) ? values.FirstOrDefault() : null;
            if (challenge is null)
            {
                throw CloudProviderException.InvalidResponse();
            }

            SupportLogger.Shared.Log("[Mega] Solving hashcash challenge…", "Mega");
            status?.Invoke(Localization.L("Solving security challenge — may take a moment…"));
            var solution = await Task.Run(() => MegaCrypto.SolveHashcash(challenge, cancellationToken), cancellationToken).ConfigureAwait(false);
            SupportLogger.Shared.Log("[Mega] Hashcash solved", "Mega");

            using var retryRequest = BuildRequest(url, body, solution);
            using var retry = await http.SendApiAsync(retryRequest, cancellationToken, timeout).ConfigureAwait(false);
            if (!retry.IsSuccessStatusCode)
            {
                throw CloudProviderException.InvalidResponse();
            }

            return Parse(await retry.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw CloudProviderException.ServerError((int)response.StatusCode);
        }

        return Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The response is an array with one result per command — or, on a transient overload, a
    /// bare negative integer for the whole batch. A negative first result is that command's error.
    /// </summary>
    internal static JsonElement Parse(byte[] data)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(data);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }

        if (root.ValueKind == JsonValueKind.Number)
        {
            throw MapError(root.TryGetInt32(out var code) ? code : 0);
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw CloudProviderException.InvalidResponse();
        }

        if (root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Number && root[0].TryGetInt32(out var first) && first < 0)
        {
            throw MapError(first);
        }

        return root;
    }

    internal static CloudProviderException MapError(int code) => code switch
    {
        -2 => CloudProviderException.InvalidCredentials(),
        -4 => CloudProviderException.QuotaExceeded(),
        -6 => CloudProviderException.RateLimited(),
        -9 => CloudProviderException.NotFound("Object not found"),
        -11 => CloudProviderException.Unauthorized(),
        -14 => CloudProviderException.NotFound("Temporary not available"),

        // ESID: the session expired or was revoked. macOS reported a generic server error;
        // Unauthorized gives the panel its "sign in again" banner.
        -15 => CloudProviderException.Unauthorized(),
        -16 => CloudProviderException.RateLimited(),
        -17 => CloudProviderException.QuotaExceeded(), // over quota
        -26 => CloudProviderException.TwoFactorRequired(), // EMFAREQUIRED — 2FA code missing or wrong
        _ => CloudProviderException.ServerError(code),
    };

    private static HttpRequestMessage BuildRequest(string url, string body, string? hashcash)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (hashcash is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Hashcash", hashcash);
        }

        return request;
    }

    private static string Batch(JsonObject command) => new JsonArray(command).ToJsonString();

    private static JsonElement First(JsonElement result)
        => result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 0 ? result[0] : default;

    /// <summary>MEGA may hand out http:// transfer URLs; the content is encrypted either way, but stay on TLS as macOS does.</summary>
    private static string ForceHttps(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[7..] : url;

    /// <summary>
    /// Posts one encrypted chunk. The answer is empty for intermediate chunks, the completion
    /// handle for the last, or a negative error code. Chunks are idempotent per offset, so
    /// transient failures are retried.
    /// </summary>
    private async Task<string> PostChunkAsync(string uploadUrl, long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var retry = attempt < 2;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{uploadUrl}/{offset}")
                {
                    Content = new ReadOnlyMemoryContent(data),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
                var text = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    if (retry && status is 500 or 502 or 503 or 504)
                    {
                        await Task.Delay(RetryDelayUnit * (attempt + 1), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw CloudHttp.StatusError(response.StatusCode);
                }

                if (long.TryParse(text, out var code) && code < 0)
                {
                    if (retry && code == -3)
                    {
                        await Task.Delay(RetryDelayUnit * (attempt + 1), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw MapError((int)code);
                }

                return text;
            }
            catch (CloudProviderException e) when (retry && e.Kind == CloudErrorKind.NetworkError)
            {
                await Task.Delay(RetryDelayUnit * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // MARK: - Cache

    private async Task EnsureNodesAsync(CancellationToken cancellationToken)
    {
        if (HasNodes)
        {
            return;
        }

        await _fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!HasNodes)
            {
                await FetchNodesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    private async Task RefetchNodesAsync(CancellationToken cancellationToken)
    {
        await _fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FetchNodesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    private bool HasNodes
    {
        get
        {
            lock (_gate)
            {
                return _nodes.Count > 0;
            }
        }
    }

    private uint[]? NodeKey(string handle)
    {
        lock (_gate)
        {
            return _nodeKeys.TryGetValue(handle, out var key) ? key : null;
        }
    }

    private HashSet<string> CacheCreatedNodes(JsonElement result, string name, long size, uint[] key)
    {
        var handles = new HashSet<string>(StringComparer.Ordinal);
        var payload = First(result);
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("f", out var created) || created.ValueKind != JsonValueKind.Array)
        {
            return handles;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        lock (_gate)
        {
            foreach (var nodeData in created.EnumerateArray())
            {
                if (nodeData.GetString("h") is not { } handle || nodeData.GetInt64("t") is not { } type)
                {
                    continue;
                }

                _nodes[handle] = new MegaNode(handle, nodeData.GetString("p") ?? string.Empty, name, (int)type, size, now);
                _nodeKeys[handle] = key;
                handles.Add(handle);
            }
        }

        return handles;
    }

    private string ResolveHandle(string path)
    {
        lock (_gate)
        {
            return ResolveHandleLocked(path);
        }
    }

    private string? TryResolveHandle(string path)
    {
        try
        {
            return ResolveHandle(path);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            return null;
        }
    }

    private string ResolveHandleLocked(string path)
    {
        var current = _rootHandle ?? throw CloudProviderException.NotFound("Root folder not found");
        foreach (var component in CloudPath.Components(path))
        {
            var child = _nodes.Values.FirstOrDefault(n => n.ParentHandle == current && n.Name == component)
                ?? throw CloudProviderException.NotFound(path);
            current = child.Handle;
        }

        return current;
    }

    private bool IsDescendantLocked(string handle, string ancestor)
    {
        var current = handle;
        var steps = 0;
        while (_nodes.TryGetValue(current, out var node) && steps++ < 10_000)
        {
            if (node.ParentHandle == ancestor)
            {
                return true;
            }

            if (node.ParentHandle.Length == 0)
            {
                return false;
            }

            current = node.ParentHandle;
        }

        return false;
    }

    private string BuildPathLocked(string handle)
    {
        var components = new List<string>();
        var current = handle;
        while (_nodes.TryGetValue(current, out var node) && components.Count < 10_000)
        {
            if (node.Handle == _rootHandle)
            {
                break;
            }

            components.Insert(0, node.Name);
            current = node.ParentHandle;
        }

        return "/" + string.Join('/', components);
    }

    private static string ChildPath(string directory, string name) => directory == "/" ? "/" + name : directory + "/" + name;

    /// <summary>
    /// Decrypts a node's key with the master key (4 words for a folder, 8 for a file), caches it
    /// and decrypts the name with it. Null when the key or the attributes don't fit.
    /// </summary>
    private static string? DecryptNodeName(Aes masterAes, string attributes, string keyField, string handle, Dictionary<string, uint[]> keys)
    {
        var encryptedKey = MegaCrypto.FirstKeyPart(keyField);
        if (encryptedKey is null)
        {
            return null;
        }

        uint[] nodeKey;
        if (encryptedKey.Length / 4 == 4)
        {
            nodeKey = MegaCrypto.ToWords(masterAes.DecryptEcb(encryptedKey, PaddingMode.None));
        }
        else if (encryptedKey.Length / 4 >= 8)
        {
            nodeKey = MegaCrypto.ToWords(masterAes.DecryptEcb(encryptedKey.AsSpan(0, 32), PaddingMode.None));
        }
        else
        {
            return null;
        }

        keys[handle] = nodeKey;
        return MegaCrypto.DecryptName(attributes, MegaCrypto.AttributeKey(nodeKey));
    }
}

/// <summary>
/// Decrypts a MEGA download as it streams (AES-CTR) and, when the node carries a real MAC,
/// checks it at the end: a mismatch throws from the final read, so
/// <see cref="HttpTransfer.SaveToFileAsync"/> discards the staged file instead of keeping a
/// corrupted one.
/// </summary>
internal sealed class MegaDecryptingStream : Stream
{
    private readonly Stream _inner;
    private readonly MegaAesCtr _ctr;
    private readonly MegaMac? _mac;
    private readonly byte[]? _expectedMac;
    private bool _finished;

    public MegaDecryptingStream(Stream inner, ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> counter, byte[]? expectedMetaMac)
    {
        _inner = inner;
        _ctr = new MegaAesCtr(contentKey, counter);
        _expectedMac = expectedMetaMac;
        _mac = expectedMetaMac is null ? null : new MegaMac(contentKey, counter[..8]);
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

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        return Process(buffer[..read]);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Process(buffer.Span[..read]);
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
            _inner.Dispose();
            _ctr.Dispose();
            _mac?.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Process(Span<byte> chunk)
    {
        if (chunk.Length > 0)
        {
            _ctr.Transform(chunk);
            _mac?.Update(chunk);
            return chunk.Length;
        }

        if (!_finished && _mac is not null && _expectedMac is not null)
        {
            _finished = true;
            if (!_mac.FinishMetaMac().AsSpan().SequenceEqual(_expectedMac))
            {
                SupportLogger.Shared.Log("[Mega] Downloaded file failed the MAC check", "Mega", LogLevel.Error);
                throw CloudProviderException.CommandFailed(Localization.L("The downloaded file is corrupted: its MEGA integrity check (MAC) does not match."));
            }
        }

        return 0;
    }
}
