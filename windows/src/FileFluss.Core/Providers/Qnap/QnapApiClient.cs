// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers.WebDav;

namespace FileFluss.Core.Providers.Qnap;

/// <summary>What a QNAP account stores — the macOS <c>QNAPCredentials</c>, same JSON keys.</summary>
/// <param name="ClientId">Identifies this app to QTS, which ties the tokens to it and lists it under the account's login devices.</param>
/// <param name="Qtoken">"Remember me" token from <c>remme=1</c>. Replaying it logs in without the password or a 2-step code. QTS may not hand one out.</param>
/// <param name="Vtoken">Issued at a 2-step login with <c>dont_verify_2sv_again=1</c>. Sent with the password, it skips the code on later logins.</param>
internal sealed record QnapCredentials(
    [property: JsonPropertyName("serverURL")] string ServerUrl,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("allowSelfSignedCertificate")] bool AllowSelfSignedCertificate,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("qtoken")] string? Qtoken = null,
    [property: JsonPropertyName("vtoken")] string? Vtoken = null);

/// <summary>
/// QNAP's File Station HTTP API (<c>authLogin.cgi</c> + <c>filemanager/utilRequest.cgi</c>) on
/// the user's NAS — the API QNAP documents for third-party integrators and its own Qfile apps
/// use (port of the macOS <c>QNAPAPIClient</c>).
///
/// <para><b>Paths</b> are File Station's own: the first component is the shared folder
/// ("/Public/Photos/a.jpg"), and "/" lists the shared folders.</para>
///
/// <para><b>Sessions.</b> Every call carries the session id from <c>authLogin.cgi</c>. File
/// Station reports a lost session as status 3; the client then logs in once more, single-
/// flight. Never log request URLs: they carry the sid.</para>
/// </summary>
internal sealed class QnapApiClient
{
    internal const string Category = "QNAP";
    private const string ClientApp = "FileFluss";
    private const int ListPageSize = 1000;

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly Lock _credentialsGate = new();
    private QnapCredentials _credentials;
    private string? _sid;

    public QnapApiClient(HttpClient http, QnapCredentials credentials)
    {
        _http = http;
        _credentials = credentials;
    }

    public QnapCredentials Credentials
    {
        get
        {
            lock (_credentialsGate)
            {
                return _credentials;
            }
        }
    }

    /// <summary>Called when a login replaced a token, so the provider can persist the current ones.</summary>
    public Action<QnapCredentials>? CredentialsChanged { get; set; }

    /// <summary>Delay between checks that a server-side copy or move has landed; tests shorten it.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    // MARK: - Authentication

    /// <summary>
    /// Gets a session id. A stored qtoken is tried first (unless <paramref name="allowToken"/>
    /// is off — a sign-in from the form, where the password may just have changed); when QTS
    /// rejects it, the password login follows with the vtoken, so a 2-step account isn't
    /// asked for a code it can't give from here.
    /// </summary>
    public async Task LoginAsync(string? securityCode, bool allowToken, CancellationToken cancellationToken)
    {
        var code = StripWhitespace(securityCode);
        var credentials = Credentials;

        if (allowToken && code.Length == 0 && !string.IsNullOrEmpty(credentials.Qtoken))
        {
            var tokenReply = await AuthLoginAsync(
            [
                ("user", credentials.Username),
                ("qtoken", credentials.Qtoken),
                ("remme", "1"),
                ("client_id", credentials.ClientId),
                ("client_app", ClientApp),
            ], cancellationToken).ConfigureAwait(false);
            if (SessionId(tokenReply) is { } tokenSid)
            {
                Volatile.Write(ref _sid, tokenSid);
                return;
            }

            SupportLogger.Shared.Log("[QNAP] Stored login token was refused; signing in with the password", Category);
            credentials = UpdateCredentials(c => c with { Qtoken = null });
        }

        var fields = new List<(string, string)>
        {
            ("user", credentials.Username),
            ("pwd", EncodePassword(credentials.Password)),
            ("remme", "1"),
            ("serviceKey", "1"),
            ("client_id", credentials.ClientId),
            ("client_app", ClientApp),
        };
        if (code.Length > 0)
        {
            fields.Add(("security_code", code));
            fields.Add(("dont_verify_2sv_again", "1"));
        }
        else if (!string.IsNullOrEmpty(credentials.Vtoken))
        {
            fields.Add(("vtoken", credentials.Vtoken));
        }

        var reply = await AuthLoginAsync(fields, cancellationToken).ConfigureAwait(false);
        var sid = SessionId(reply) ?? throw LoginError(reply, sentCode: code.Length > 0);
        Volatile.Write(ref _sid, sid);

        var qtoken = reply.GetValueOrDefault("qtoken");
        var vtoken = reply.GetValueOrDefault("vtoken");
        if (!string.IsNullOrEmpty(qtoken) || !string.IsNullOrEmpty(vtoken))
        {
            UpdateCredentials(c => c with
            {
                Qtoken = string.IsNullOrEmpty(qtoken) ? c.Qtoken : qtoken,
                Vtoken = string.IsNullOrEmpty(vtoken) ? c.Vtoken : vtoken,
            });
        }

        SupportLogger.Shared.Log($"[QNAP] Logged in as {credentials.Username} at {HostFrom(credentials.ServerUrl)}", Category);
    }

    private QnapCredentials UpdateCredentials(Func<QnapCredentials, QnapCredentials> change)
    {
        QnapCredentials before, after;
        lock (_credentialsGate)
        {
            before = _credentials;
            after = _credentials = change(_credentials);
        }

        if (before.Qtoken != after.Qtoken || before.Vtoken != after.Vtoken)
        {
            CredentialsChanged?.Invoke(after);
        }

        return after;
    }

    /// <summary>
    /// POSTs the login form — keeping the password out of the URL, where proxies and server
    /// logs would keep it — and reads QTS's XML reply.
    /// </summary>
    private async Task<Dictionary<string, string>> AuthLoginAsync(IReadOnlyList<(string Key, string Value)> fields, CancellationToken cancellationToken)
    {
        var content = new StringContent(FormEncode(fields), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("/cgi-bin/authLogin.cgi", [])) { Content = content };
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureHttpSuccess(response);
        return TopLevelValues(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    internal static string? SessionId(IReadOnlyDictionary<string, string> reply)
        => reply.GetValueOrDefault("authPassed") == "1" && reply.GetValueOrDefault("authSid") is { Length: > 0 } sid ? sid : null;

    /// <summary>Why an <c>authLogin.cgi</c> reply carries no session. <paramref name="sentCode"/>: the login had a 2-step code.</summary>
    internal static CloudProviderException LoginError(IReadOnlyDictionary<string, string> reply, bool sentCode)
    {
        if (reply.GetValueOrDefault("need_2sv") == "1" || reply.GetValueOrDefault("need_2_step_verification") == "1")
        {
            return sentCode
                ? CloudProviderException.CommandFailed(Localization.L("The 2-step verification code is incorrect."))
                : CloudProviderException.TwoFactorRequired();
        }

        if (reply.GetValueOrDefault("errorValue") is "-3" or "-4")
        {
            return CloudProviderException.CommandFailed(Localization.L("The password has expired. Change it in QTS, then sign in again."));
        }

        return reply.GetValueOrDefault("user_enable") == "0"
            ? CloudProviderException.CommandFailed(Localization.L("This account is disabled on the NAS."))
            : CloudProviderException.InvalidCredentials();
    }

    /// <summary><c>ezEncode</c> in QNAP's own login page is plain Base64 of the UTF-8 bytes.</summary>
    internal static string EncodePassword(string password) => Convert.ToBase64String(Encoding.UTF8.GetBytes(password));

    /// <summary>Authenticator apps show the code in two groups and people paste it that way.</summary>
    internal static string StripWhitespace(string? code)
        => code is null ? string.Empty : string.Concat(code.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>Ends the session on the NAS. Best effort: the account is being removed either way.</summary>
    public async Task LogoutAsync()
    {
        if (Interlocked.Exchange(ref _sid, null) is not { } sid)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("/cgi-bin/authLogout.cgi", [("sid", sid), ("logout", "1")]));
            using var response = await _http.SendApiAsync(request, CancellationToken.None, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (CloudProviderException)
        {
        }
    }

    /// <summary>The current sid, logging in first when there is none — one login for all concurrent callers.</summary>
    private async Task<string> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _sid) is { } current)
        {
            return current;
        }

        await _loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _sid) is { } refreshed)
            {
                return refreshed;
            }

            try
            {
                await LoginAsync(null, allowToken: true, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException e) when (e.Kind is CloudErrorKind.InvalidCredentials or CloudErrorKind.TwoFactorRequired)
            {
                // Nobody is there to type a password or a code: the panel's "sign in again".
                SupportLogger.Shared.Log($"[QNAP] Re-login failed: {e.Kind}", Category, LogLevel.Error);
                throw CloudProviderException.Unauthorized();
            }

            return Volatile.Read(ref _sid) ?? throw CloudProviderException.NotAuthenticated();
        }
        finally
        {
            _loginGate.Release();
        }
    }

    private void InvalidateSession(string deadSid) => Interlocked.CompareExchange(ref _sid, null, deadSid);

    // MARK: - Listing

    public async Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        var cleaned = CleanPath(path);
        if (cleaned == "/")
        {
            return await ListSharesAsync(cancellationToken).ConfigureAwait(false);
        }

        var items = new List<CloudFileItem>();
        var start = 0;
        // A page count cap guards against a firmware that ignores `start`.
        for (var page = 0; page < 1000; page++)
        {
            var body = await CallAsync("get_list",
            [
                ("is_iso", "0"),
                ("list_mode", "all"),
                ("path", cleaned),
                ("dir", "ASC"),
                ("sort", "filename"),
                ("start", start.ToString(CultureInfo.InvariantCulture)),
                ("limit", ListPageSize.ToString(CultureInfo.InvariantCulture)),
                ("hidden_file", "1"),
                ("v", "1"),
            ], cleaned, cancellationToken).ConfigureAwait(false);
            var (pageItems, rawCount, total) = ItemsFromListing(body, cleaned);
            items.AddRange(pageItems);
            start += rawCount;
            if (rawCount < ListPageSize || start >= (total ?? 0))
            {
                break;
            }
        }

        return items;
    }

    /// <summary>The shared folders the account can see, as the top level.</summary>
    private async Task<IReadOnlyList<CloudFileItem>> ListSharesAsync(CancellationToken cancellationToken)
    {
        var body = await CallAsync("get_tree",
        [
            ("is_iso", "0"),
            ("node", "share_root"),
            ("hidden_file", "0"),
        ], null, cancellationToken).ConfigureAwait(false);
        return SharesFromTree(body);
    }

    /// <summary>
    /// One <c>get_list</c> page as panel rows. <c>RawCount</c> counts the rows QTS sent before
    /// system folders were dropped, which is what paging moves by.
    /// </summary>
    internal static (List<CloudFileItem> Items, int RawCount, int? Total) ItemsFromListing(string json, string parentPath)
    {
        using var document = Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw CloudProviderException.InvalidResponse();
        }

        var rows = Rows(document.RootElement);
        var items = new List<CloudFileItem>(rows.Count);
        foreach (var row in rows)
        {
            if (Text(row, "filename") is not { Length: > 0 } name || IsSystemEntry(name))
            {
                continue;
            }

            items.Add(Item(row, Join(parentPath, name), name));
        }

        return (items, rows.Count, Int(document.RootElement, "total"));
    }

    /// <summary><c>get_tree</c> with <c>node=share_root</c>: a bare array of shares.</summary>
    internal static List<CloudFileItem> SharesFromTree(string json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            if (root.ValueKind == JsonValueKind.Object && Int(root, "status") is { } status)
            {
                throw MapStatus(status);
            }

            throw CloudProviderException.InvalidResponse();
        }

        var shares = new List<CloudFileItem>();
        foreach (var node in root.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object || Text(node, "id") is not { Length: > 0 } id)
            {
                continue;
            }

            // Disc images, phones and optical drives show up as shares too; only real shared
            // folders hold files FileFluss can write to.
            if (Text(node, "iconCls") is { } kind && kind != "folder")
            {
                continue;
            }

            var name = Text(node, "text") ?? CloudPath.Name(id);
            shares.Add(new CloudFileItem(id, name, id, true, 0, CloudFileItem.UnknownDate) { IsHidden = CloudFileItem.IsDotHidden(name) });
        }

        return shares;
    }

    private static CloudFileItem Item(JsonElement row, string path, string name)
    {
        var isFolder = Int(row, "isfolder") == 1;
        return new CloudFileItem(
            path,
            name,
            path,
            isFolder,
            isFolder ? 0 : Long(row, "filesize") ?? 0,
            ModificationDate(row) ?? CloudFileItem.UnknownDate)
        {
            IsHidden = CloudFileItem.IsDotHidden(name),
        };
    }

    /// <summary><c>epochmt</c> is UTC seconds. <c>mt</c> is the NAS's local wall-clock time and only a fallback.</summary>
    private static DateTimeOffset? ModificationDate(JsonElement row)
    {
        if (Long(row, "epochmt") is { } epoch && epoch > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        if (row.TryGetProperty("mt", out var mt) && mt.ValueKind == JsonValueKind.Number && mt.TryGetInt64(out var seconds) && seconds > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        // The NAS's time zone isn't known here; read it as this PC's, as macOS does.
        return Text(row, "mt") is { Length: > 0 } raw
               && DateTime.TryParseExact(raw, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            ? new DateTimeOffset(local)
            : null;
    }

    /// <summary>QTS's own bookkeeping folders: the recycle bin, snapshots and the thumbnail caches File Station hides.</summary>
    internal static bool IsSystemEntry(string name)
        => name.StartsWith('@') || name.StartsWith(".@__", StringComparison.Ordinal) || name == ".streams";

    // MARK: - Transfers

    public async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var (parent, name) = Split(remotePath);
        var sid = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("/cgi-bin/filemanager/utilRequest.cgi",
        [
            ("func", "download"),
            ("sid", sid),
            ("isfolder", "0"),
            ("compress", "0"),
            ("source_path", parent),
            ("source_file", name),
            ("source_total", "1"),
        ]));
        using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureHttpSuccess(response);

        if (response.Content.Headers.ContentDisposition is not null)
        {
            await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A refused download is still HTTP 200, carrying a JSON status (or nothing) instead of
        // the file. Land it beside the target first, so an error body never replaces a file
        // already at the destination.
        var staging = localPath + ".fileflussqnap";
        try
        {
            await HttpTransfer.SaveToFileAsync(response, staging, onBytes, cancellationToken).ConfigureAwait(false);
            if (StatusInSmallJson(staging) is { } status)
            {
                if (status == 3)
                {
                    InvalidateSession(sid);
                }

                throw MapStatus(status, remotePath);
            }

            File.Move(staging, localPath, overwrite: true);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// A multipart POST streamed straight from disk (macOS spools the body to a temp file
    /// first; a streamed <see cref="MultipartContent"/> needs no copy). A lost session is
    /// retried once with a fresh body; progress never counts the re-sent prefix twice.
    /// </summary>
    public async Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var (parent, name) = Split(remotePath);
        var boundary = "----FileFlussQNAP" + Guid.NewGuid().ToString("N");
        var progress = new HighWaterProgress(onBytes);

        for (var attempt = 0; ; attempt++)
        {
            var sid = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("/cgi-bin/filemanager/utilRequest.cgi",
            [
                ("func", "upload"),
                ("type", "standard"),
                ("sid", sid),
                ("dest_path", parent),
                ("overwrite", "1"),
                // File Station's progress key: the destination path with every "/" turned into "-".
                ("progress", CleanPath(remotePath).Replace('/', '-')),
            ]))
            {
                Content = UploadBody(localPath, name, boundary, progress.NextAttempt()),
            };

            using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureHttpSuccess(response);
            var status = StatusIn(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (status is null or 1)
            {
                return;
            }

            if (status == 3 && attempt == 0)
            {
                InvalidateSession(sid);
                continue;
            }

            SupportLogger.Shared.Log($"[QNAP] upload → status {status}", Category, LogLevel.Error);
            throw MapStatus(status.Value, remotePath);
        }
    }

    internal static MultipartContent UploadBody(string localPath, string name, string boundary, ByteProgress? onBytes)
    {
        var multipart = new MultipartContent("form-data", boundary)
        {
            // Names go out as UTF-8, as File Station's own uploader sends them.
            HeaderEncodingSelector = (_, _) => Encoding.UTF8,
        };
        // .NET quotes the boundary parameter; File Station's CGI wants it bare, as macOS sends it.
        multipart.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + boundary);

        var fileName = new ByteArrayContent(Encoding.UTF8.GetBytes(name));
        fileName.Headers.TryAddWithoutValidation("Content-Disposition", "form-data; name=\"fileName\"");
        multipart.Add(fileName);

        var file = ProgressStreamContent.FromFile(localPath, onBytes);
        file.Headers.TryAddWithoutValidation("Content-Disposition", $"form-data; name=\"file\"; filename=\"{name.Replace("\"", "%22", StringComparison.Ordinal)}\"");
        multipart.Add(file);
        return multipart;
    }

    // MARK: - Changes

    public Task DeleteItemAsync(string path, CancellationToken cancellationToken)
    {
        var (parent, name) = Split(path);
        return CallAsync("delete", [("path", parent), ("file_total", "1"), ("file_name", name)], path, cancellationToken);
    }

    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        var (parent, name) = Split(path);
        try
        {
            await CallAsync("createdir", [("dest_folder", name), ("dest_path", parent)], path, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.CommandFailed && e.Message == AlreadyExistsMessage)
        {
            // Creating a folder that's already there is what a sync wants.
        }
    }

    public Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken)
    {
        var (parent, name) = Split(path);
        return CallAsync("rename", [("path", parent), ("source_name", name), ("dest_name", newName)], path, cancellationToken);
    }

    /// <summary>Server-side copy or move into <paramref name="destinationFolder"/>, keeping the source name. <c>mode=0</c> overwrites.</summary>
    public async Task CopyMoveAsync(string path, string destinationFolder, bool move, CancellationToken cancellationToken)
    {
        var (parent, name) = Split(path);
        var folder = CleanPath(destinationFolder);
        await CallAsync(move ? "move" : "copy",
        [
            ("source_file", name),
            ("source_total", "1"),
            ("source_path", parent),
            ("dest_path", folder),
            ("mode", "0"),
        ], path, cancellationToken).ConfigureAwait(false);

        // QTS may hand back a process id and finish in the background; the destination has to
        // exist before a follow-up rename can find it.
        var destination = Join(folder, name);
        for (var i = 0; i < 120; i++)
        {
            try
            {
                await GetFileInfoAsync(destination, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (CloudProviderException)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken)
    {
        var (parent, name) = Split(path);
        return CallAsync("stat",
        [
            ("settime", "1"),
            ("mtime", date.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ("path", parent),
            ("file_total", "1"),
            ("file_name", name),
        ], path, cancellationToken);
    }

    public async Task<CloudFileItem> GetFileInfoAsync(string path, CancellationToken cancellationToken)
    {
        var cleaned = CleanPath(path);
        if (cleaned == "/")
        {
            return new CloudFileItem("/", "/", "/", true, 0, CloudFileItem.UnknownDate);
        }

        var (parent, name) = Split(cleaned);
        var body = await CallAsync("stat", [("path", parent), ("file_total", "1"), ("file_name", name)], path, cancellationToken).ConfigureAwait(false);
        return ItemFromStat(body, cleaned) ?? throw CloudProviderException.NotFound(path);
    }

    /// <summary>
    /// A <c>stat</c> reply's first row, or null when it says the item isn't there. A share
    /// root has no parent folder to stat it in; QTS answers with an empty name, which still
    /// means "there" — the name comes from the path.
    /// </summary>
    internal static CloudFileItem? ItemFromStat(string json, string path)
    {
        using var document = Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var rows = Rows(document.RootElement);
        if (rows.Count == 0 || Int(rows[0], "exist") == 0)
        {
            return null;
        }

        return Item(rows[0], CleanPath(path), CloudPath.Name(CleanPath(path)));
    }

    public async Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken)
    {
        var (parent, name) = Split(path);
        try
        {
            var body = await CallAsync("get_file_size", [("path", parent), ("total", "1"), ("name", name)], path, cancellationToken).ConfigureAwait(false);
            using var document = Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && Long(document.RootElement, "size") is { } size)
            {
                return size;
            }
        }
        catch (CloudProviderException e) when (e.Kind is not (CloudErrorKind.Unauthorized or CloudErrorKind.NotAuthenticated))
        {
        }

        // Large folders are sized in the background and answered with a process id instead;
        // count the files ourselves then, capped so a giant share doesn't keep the NAS busy for hours.
        long total = 0;
        var queue = new Queue<string>([path]);
        var seen = 0;
        while (queue.TryDequeue(out var next))
        {
            foreach (var item in await ListFolderAsync(next, cancellationToken).ConfigureAwait(false))
            {
                if (++seen > 5000)
                {
                    return total;
                }

                if (item.IsDirectory)
                {
                    queue.Enqueue(item.Path);
                }
                else
                {
                    total += item.Size;
                }
            }
        }

        return total;
    }

    /// <summary>File Station's search across <paramref name="path"/> (or every shared folder), by name.</summary>
    public async Task<IReadOnlyList<CloudFileItem>> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken)
    {
        List<string> folders = path is not null && CleanPath(path) != "/"
            ? [CleanPath(path)]
            : (await ListSharesAsync(cancellationToken).ConfigureAwait(false)).Select(s => s.Path).ToList();
        if (folders.Count == 0)
        {
            return [];
        }

        var parameters = new List<(string, string)>
        {
            ("folderCount", folders.Count.ToString(CultureInfo.InvariantCulture)),
            ("keyword", query),
            ("searchType", "0"),
            ("fileSizeType", "0"),
            ("ownerType", "0"),
            ("dateType", "0"),
            ("dateField", "0"),
            ("hidden_file", "1"),
            ("v", "1"),
            ("start", "0"),
            ("limit", "200"),
            ("sort", "filename"),
            ("dir", "ASC"),
        };
        parameters.AddRange(folders.Select(f => ("folders", f)));
        var body = await CallAsync("search_ext", parameters, null, cancellationToken).ConfigureAwait(false);
        return SearchResults(body, folders.Count == 1 ? folders[0] : "/");
    }

    internal static List<CloudFileItem> SearchResults(string json, string basePath)
    {
        using var document = Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw CloudProviderException.InvalidResponse();
        }

        var results = new List<CloudFileItem>();
        foreach (var row in Rows(document.RootElement))
        {
            if (Text(row, "filename") is not { Length: > 0 } raw)
            {
                continue;
            }

            var fullPath = CleanPath(raw.StartsWith('/') ? raw : Join(basePath, raw));
            var components = CloudPath.Components(fullPath);
            if (components.Length == 0 || components.Any(IsSystemEntry))
            {
                continue;
            }

            results.Add(Item(row, fullPath, components[^1]));
        }

        return results;
    }

    // MARK: - Share links

    /// <summary>
    /// Creates a File Station share link for one item. QTS builds the URL from the host name
    /// it's given, so the address this account connects with is passed — otherwise it would
    /// use the NAS's LAN address.
    /// </summary>
    public async Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        var (parent, name) = Split(path);
        Uri.TryCreate(Credentials.ServerUrl, UriKind.Absolute, out var server);
        var query = new List<(string, string)>
        {
            ("c", "1"),
            ("path", parent),
            ("file_total", "1"),
            ("file_name", name),
            ("hostname", server?.Host ?? string.Empty),
            ("ssl", server?.Scheme == Uri.UriSchemeHttps ? "true" : "false"),
            ("expire_time", options.Expiry is { } expiry ? expiry.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : "0"),
        };
        var hasPassword = !string.IsNullOrEmpty(options.Password);
        if (hasPassword)
        {
            query.Add(("access_enabled", "true"));
            query.Add(("access_code", options.Password!));
        }

        var body = await CallAsync("get_share_link", query, path, cancellationToken).ConfigureAwait(false);
        var url = CreatedLinkUrl(body) ?? throw CloudProviderException.InvalidResponse();
        return new CloudShareLink(url)
        {
            ExpiresAt = options.Expiry,
            HasPassword = hasPassword,
            Note = LanAddressNote,
        };
    }

    internal static Uri? CreatedLinkUrl(string json)
    {
        using var document = Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
               && document.RootElement.TryGetProperty("links", out var links)
               && links.ValueKind == JsonValueKind.Array
               && links.GetArrayLength() > 0
               && Text(links[0], "link_url") is { } raw
               && Uri.TryCreate(raw, UriKind.Absolute, out var url)
            ? url
            : null;
    }

    public async Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        if (await FindShareLinkAsync(path, cancellationToken).ConfigureAwait(false) is not { } entry)
        {
            return null;
        }

        return ShareLinkFromListEntry(entry);
    }

    internal static CloudShareLink ShareLinkFromListEntry(ShareListEntry entry)
    {
        if (entry.DownloadLink is not { } raw || !Uri.TryCreate(raw, UriKind.Absolute, out var url))
        {
            throw CloudProviderException.InvalidResponse();
        }

        return new CloudShareLink(url)
        {
            ExpiresAt = entry.ExpireTime is { } seconds && seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null,
            // "----" is how File Station shows "no access code".
            HasPassword = entry.AccessCode is { Length: > 0 } code && code != "----",
            Note = LanAddressNote,
        };
    }

    public async Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        if (await FindShareLinkAsync(path, cancellationToken).ConfigureAwait(false) is not { Ssid: { Length: > 0 } ssid })
        {
            throw CloudProviderException.CommandFailed(Localization.L("This item doesn't have a public link."));
        }

        await CallAsync("delete_share", [("file_total", "1"), ("ssids", ssid)], path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One row of <c>get_share_list</c>.</summary>
    internal sealed record ShareListEntry(string? Filename, string? Ssid, string? DownloadLink, long? ExpireTime, string? AccessCode, bool Multiple);

    /// <summary>Pages through the account's links for the one on <paramref name="path"/>. QTS has no lookup by path.</summary>
    private async Task<ShareListEntry?> FindShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        var target = CleanPath(path);
        const int pageSize = 200;
        var start = 0;
        for (var page = 0; page < 50; page++)
        {
            var body = await CallAsync("get_share_list",
            [
                ("dir", "ASC"),
                ("sort", "filename"),
                ("start", start.ToString(CultureInfo.InvariantCulture)),
                ("limit", pageSize.ToString(CultureInfo.InvariantCulture)),
            ], null, cancellationToken).ConfigureAwait(false);
            var (rows, total) = ShareList(body);

            // Links to several items at once have no single path to match.
            if (rows.FirstOrDefault(r => r.Filename == target && !r.Multiple) is { } hit)
            {
                return hit;
            }

            start += rows.Count;
            if (rows.Count < pageSize || (total is { } count && start >= count))
            {
                return null;
            }
        }

        return null;
    }

    internal static (List<ShareListEntry> Rows, int? Total) ShareList(string json)
    {
        using var document = Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw CloudProviderException.InvalidResponse();
        }

        var rows = Rows(document.RootElement)
            .Select(r => new ShareListEntry(
                Text(r, "filename"),
                Text(r, "ssid"),
                Text(r, "download_link"),
                Long(r, "expire_time"),
                Text(r, "access_code"),
                Int(r, "multiple") == 1))
            .ToList();
        return (rows, Int(document.RootElement, "total"));
    }

    private static string LanAddressNote => Localization.L("The NAS built this link from the address you connect to it with. If that's a local address, recipients outside your network can't open it — set up myQNAPcloud or port forwarding for that.");

    // MARK: - Plumbing

    /// <summary>
    /// One <c>utilRequest.cgi</c> call. A <c>status</c> other than 1 is an error; listings
    /// carry no status at all when they succeed. A lost session (status 3) logs in again once.
    /// </summary>
    private async Task<string> CallAsync(string function, IReadOnlyList<(string Key, string Value)> parameters, string? path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var sid = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
            var query = new List<(string, string)>(parameters.Count + 2) { ("func", function), ("sid", sid) };
            query.AddRange(parameters);
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("/cgi-bin/filemanager/utilRequest.cgi", query));
            using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureHttpSuccess(response);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (StatusIn(body) is not { } status || status == 1)
            {
                return body;
            }

            if (status == 3 && attempt == 0)
            {
                InvalidateSession(sid);
                continue;
            }

            SupportLogger.Shared.Log($"[QNAP] {function} → status {status}", Category, LogLevel.Error);
            throw MapStatus(status, path);
        }
    }

    /// <summary>The <c>status</c> of a JSON object reply, as a number or a string.</summary>
    internal static int? StatusIn(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object ? Int(document.RootElement, "status") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a downloaded file as a File Station status reply when it's small enough to be one.</summary>
    private static int? StatusInSmallJson(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length >= 512)
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() <= 4 ? Int(root, "status") : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string AlreadyExistsMessage => Localization.L("An item with this name already exists on the NAS.");

    /// <summary>File Station's status codes, from QNAP's API reference.</summary>
    internal static CloudProviderException MapStatus(int status, string? path = null) => status switch
    {
        2 or 33 => CloudProviderException.CommandFailed(AlreadyExistsMessage),
        3 => CloudProviderException.NotAuthenticated(),
        4 or 10 or 11 => CloudProviderException.Unauthorized(),
        5 => CloudProviderException.NotFound(path ?? string.Empty),
        7 => CloudProviderException.CommandFailed(Localization.L("The NAS couldn't write the file.")),
        8 => CloudProviderException.CommandFailed(Localization.L("File Station is turned off on the NAS.")),
        9 => CloudProviderException.QuotaExceeded(),
        12 => CloudProviderException.CommandFailed(Localization.L("The NAS doesn't allow this name.")),
        19 or 21 => CloudProviderException.CommandFailed(Localization.L("The NAS is busy. Try again in a moment.")),
        _ => CloudProviderException.ServerError(status),
    };

    private Uri BuildUri(string path, IReadOnlyList<(string Key, string Value)> query)
    {
        if (!Uri.TryCreate(Credentials.ServerUrl, UriKind.Absolute, out var server))
        {
            throw CloudProviderException.InvalidResponse();
        }

        var text = server.GetLeftPart(UriPartial.Authority) + path + (query.Count > 0 ? "?" + FormEncode(query) : string.Empty);
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : throw CloudProviderException.InvalidResponse();
    }

    /// <summary>
    /// Strict RFC 3986 percent-encoding: "+" must be escaped, since QTS reads it as a space —
    /// "a+b.txt" would become "a b.txt" — and non-ASCII goes out as UTF-8 escapes.
    /// </summary>
    internal static string FormEncode(IEnumerable<(string Key, string Value)> fields)
        => string.Join('&', fields.Select(f => Uri.EscapeDataString(f.Key) + "=" + Uri.EscapeDataString(f.Value)));

    private void EnsureHttpSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        SupportLogger.Shared.Log($"[QNAP] HTTP {(int)response.StatusCode} from {HostFrom(Credentials.ServerUrl)}", Category, LogLevel.Error);
        throw CloudProviderException.ServerError((int)response.StatusCode);
    }

    internal static string CleanPath(string path)
    {
        var p = string.IsNullOrEmpty(path) ? "/" : path;
        if (!p.StartsWith('/'))
        {
            p = "/" + p;
        }

        while (p.Length > 1 && p.EndsWith('/'))
        {
            p = p[..^1];
        }

        return p;
    }

    /// <summary>Parent folder and name, the pair File Station's calls take.</summary>
    internal static (string Parent, string Name) Split(string path)
    {
        var cleaned = CleanPath(path);
        var slash = cleaned.LastIndexOf('/');
        var parent = slash <= 0 ? "/" : cleaned[..slash];
        return (parent, cleaned[(slash + 1)..]);
    }

    internal static string Join(string folder, string name) => folder.EndsWith('/') ? folder + name : folder + "/" + name;

    /// <summary>
    /// Adds <c>https://</c> when no scheme was typed, and QTS's HTTP port 8080 for a
    /// plain-HTTP address without one. HTTPS keeps the standard 443, QTS's default for secure access.
    /// </summary>
    internal static string NormalizeServerUrl(string raw)
    {
        var text = raw.Trim();
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text;
        }

        text = text.TrimEnd('/');
        return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? ServerAddress.WithDefaultPort(text, 8080) : text;
    }

    internal static string HostFrom(string serverUrl)
        => Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : "qnap";

    /// <summary>The text of each direct child of the root element (<c>&lt;QDocRoot&gt;</c>) — the shape of every <c>authLogin.cgi</c> reply.</summary>
    internal static Dictionary<string, string> TopLevelValues(string xml)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true };
        string? current = null;
        var text = new StringBuilder();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element when reader.Depth == 1:
                        current = reader.LocalName;
                        text.Clear();
                        if (reader.IsEmptyElement)
                        {
                            values[current] = string.Empty;
                            current = null;
                        }

                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace when current is not null && reader.Depth == 2:
                        text.Append(reader.Value);
                        break;
                    case XmlNodeType.EndElement when reader.Depth == 1 && current is not null:
                        values[current] = text.ToString().Trim();
                        current = null;
                        break;
                }
            }
        }
        catch (XmlException)
        {
            // Keep what parsed before the error.
        }

        return values;
    }

    // MARK: - JSON

    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    private static List<JsonElement> Rows(JsonElement root)
        => root.TryGetProperty("datas", out var datas) && datas.ValueKind == JsonValueKind.Array
            ? datas.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object).ToList()
            : [];

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>QTS sends numbers as numbers or strings, depending on the field and the firmware.</summary>
    private static int? Int(JsonElement element, string name)
        => Long(element, name) is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;

    private static long? Long(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.Number when value.TryGetDouble(out var real) => (long)real,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
