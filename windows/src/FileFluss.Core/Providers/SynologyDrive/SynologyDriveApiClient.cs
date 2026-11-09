// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.SynologyDrive;

/// <summary>
/// The Synology DSM web API (<c>SYNO.API.Auth</c> + <c>SYNO.FileStation.*</c>) on the user's
/// NAS — what rclone, Mountain Duck and Synology's own clients use (port of the macOS
/// <c>SynologyDriveAPIClient</c>).
///
/// <para><b>Sessions.</b> Login returns a session id (SID) that every call carries. DSM
/// expires it after inactivity and answers 119; the client then logs in again once.
/// Re-login is single-flight: concurrent callers that all saw the dead SID wait for one
/// login and share its SID. Never log request URLs — they carry the SID, and the login URL
/// the password.</para>
///
/// <para><b>Paths</b> are File Station's own: "/" lists the shared folders, "/home/Photos" is
/// inside the "home" share.</para>
/// </summary>
internal sealed class SynologyDriveApiClient
{
    private const string Category = "Synology";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private string? _sid;

    public SynologyDriveApiClient(HttpClient http, SynologyDriveCredentials credentials)
    {
        _http = http;
        Credentials = credentials;
    }

    public SynologyDriveCredentials Credentials { get; }

    /// <summary>Delay between task-status polls; tests shorten it.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    // MARK: - Authentication

    /// <summary>
    /// Logs in (with <paramref name="otp"/> when 2-factor is on) and keeps the SID. Returns
    /// null on success and DSM's auth error code otherwise, so sign-in can react to 403
    /// ("this account needs an OTP code") by asking for one.
    /// </summary>
    public async Task<int?> TryLoginAsync(string? otp, CancellationToken cancellationToken)
    {
        var query = new List<(string, string)>
        {
            ("api", "SYNO.API.Auth"),
            ("version", "6"),
            ("method", "login"),
            ("account", Credentials.Username),
            ("passwd", Credentials.Password),
            ("session", "FileStation"),
            ("format", "sid"),
        };
        if (!string.IsNullOrEmpty(otp))
        {
            query.Add(("otp_code", otp));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("/webapi/auth.cgi", query));
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureHttpSuccess(response);
        var envelope = await response.ReadJsonAsync<SynologyEnvelope<SynologyAuthData>>(cancellationToken).ConfigureAwait(false);
        if (!envelope.Success)
        {
            return envelope.Error?.Code ?? -1;
        }

        Volatile.Write(ref _sid, envelope.Data?.Sid ?? throw CloudProviderException.InvalidResponse());
        SupportLogger.Shared.Log($"[Synology] Logged in as {Credentials.Username} at {HostFrom(Credentials.ServerUrl)}", Category);
        return null;
    }

    /// <summary>The current SID, logging in first when there is none — one login for all concurrent callers.</summary>
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

            if (await TryLoginAsync(null, cancellationToken).ConfigureAwait(false) is { } code)
            {
                throw BackgroundLoginError(code);
            }

            return Volatile.Read(ref _sid) ?? throw CloudProviderException.NotAuthenticated();
        }
        finally
        {
            _loginGate.Release();
        }
    }

    /// <summary>Forgets <paramref name="deadSid"/> unless another caller already replaced it.</summary>
    private void InvalidateSession(string deadSid) => Interlocked.CompareExchange(ref _sid, null, deadSid);

    // MARK: - Listing

    public Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        var cleaned = path.Length == 0 ? "/" : path;
        return cleaned == "/" ? ListSharesAsync(cancellationToken) : ListInsideAsync(cleaned, cancellationToken);
    }

    /// <summary>The top level: the NAS's shared folders.</summary>
    private async Task<IReadOnlyList<CloudFileItem>> ListSharesAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync<SynologyListSharesData>(
            "SYNO.FileStation.List", "2", "list_share",
            [("additional", "[\"time\",\"real_path\"]")],
            cancellationToken).ConfigureAwait(false);
        return (result.Shares ?? [])
            .Select(share => new CloudFileItem(share.Path, share.Name, share.Path, true, 0, share.Additional?.Time?.ModifiedDate ?? CloudFileItem.UnknownDate)
            {
                IsHidden = CloudFileItem.IsDotHidden(share.Name),
            })
            .ToList();
    }

    private async Task<IReadOnlyList<CloudFileItem>> ListInsideAsync(string folderPath, CancellationToken cancellationToken)
    {
        var result = await CallAsync<SynologyListData>(
            "SYNO.FileStation.List", "2", "list",
            [("folder_path", folderPath), ("additional", "[\"size\",\"time\",\"type\"]")],
            cancellationToken).ConfigureAwait(false);
        return (result.Files ?? []).Select(ToItem).ToList();
    }

    // MARK: - Transfers

    public async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var sid = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("/webapi/entry.cgi",
            [
                ("api", "SYNO.FileStation.Download"),
                ("version", "2"),
                ("method", "download"),
                ("path", remotePath),
                ("mode", "download"),
                ("_sid", sid),
            ]));
            using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureHttpSuccess(response);

            // DSM reports a failed download (missing file, expired session) as a small JSON
            // envelope with HTTP 200. macOS saved that as the file's contents; here it becomes
            // the error it is.
            if (await ReadDownloadErrorAsync(response, cancellationToken).ConfigureAwait(false) is { } failure)
            {
                if (failure.Code == 119 && attempt == 0)
                {
                    InvalidateSession(sid);
                    continue;
                }

                throw failure.Code == 408 ? CloudProviderException.NotFound(remotePath) : MapApiError(failure.Code);
            }

            await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// Streams the file as multipart/form-data straight from disk — no temporary body file
    /// as on macOS, and nothing held in memory. Fields: the parent folder, create_parents and
    /// overwrite, then the file part last.
    /// </summary>
    public async Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var parentPath = ParentPath(remotePath);
        var fileName = NameOf(remotePath);
        for (var attempt = 0; ; attempt++)
        {
            var sid = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("/webapi/entry.cgi",
            [
                ("api", "SYNO.FileStation.Upload"),
                ("version", "2"),
                ("method", "upload"),
                ("_sid", sid),
            ]))
            {
                Content = new UploadContent(
                    [("path", parentPath.Length == 0 ? "/" : parentPath), ("create_parents", "true"), ("overwrite", "true")],
                    fileName,
                    localPath,
                    onBytes),
            };
            using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && CloudProviderException.SizeLimitError((int)response.StatusCode, localPath) is { } tooLarge)
            {
                throw tooLarge;
            }

            EnsureHttpSuccess(response);
            var envelope = await response.ReadJsonAsync<SynologyEnvelope<SynologyEmpty>>(cancellationToken).ConfigureAwait(false);
            if (envelope.Success)
            {
                return;
            }

            var code = envelope.Error?.Code ?? -1;
            if (code == 119 && attempt == 0)
            {
                // A stale session after idling; macOS failed the upload here.
                InvalidateSession(sid);
                continue;
            }

            throw MapApiError(code);
        }
    }

    // MARK: - Mutations

    /// <summary>Synchronous (blocking) delete with <c>recursive=true</c>: a folder and its contents in one call, no task to poll.</summary>
    public Task DeleteItemAsync(string path, CancellationToken cancellationToken)
        => CallAsync<SynologyEmpty>("SYNO.FileStation.Delete", "2", "delete", [("path", path), ("recursive", "true")], cancellationToken, path);

    public Task CreateFolderAsync(string path, CancellationToken cancellationToken)
        => CallAsync<JsonElement>(
            "SYNO.FileStation.CreateFolder", "2", "create",
            [("folder_path", ParentPath(path) is { Length: > 0 } parent ? parent : "/"), ("name", NameOf(path)), ("force_parent", "true")],
            cancellationToken);

    public Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken)
        => CallAsync<JsonElement>("SYNO.FileStation.Rename", "2", "rename", [("path", path), ("name", newName)], cancellationToken, path);

    /// <summary>
    /// Server-side move (<paramref name="removeSource"/>) or copy into <paramref name="destinationFolder"/>,
    /// keeping the source name. An asynchronous task: start, then poll the status every half
    /// second for up to a minute — most finish on the first poll.
    /// </summary>
    public async Task CopyMoveAsync(string path, string destinationFolder, bool removeSource, CancellationToken cancellationToken)
    {
        var start = await CallAsync<SynologyTaskStartData>(
            "SYNO.FileStation.CopyMove", "3", "start",
            [("path", path), ("dest_folder_path", destinationFolder), ("overwrite", "true"), ("remove_src", removeSource ? "true" : "false")],
            cancellationToken, path).ConfigureAwait(false);
        if (start.TaskId is not { } taskId)
        {
            return;
        }

        // A big folder takes the NAS minutes. Returning while the task still runs would report a
        // move as done (and the caller would go on to rename or delete) before it happened, so
        // keep asking — slower after the first minute — and fail outright when it never ends.
        var deadline = DateTimeOffset.UtcNow + CopyMoveTimeout;
        for (var poll = 0; ; poll++)
        {
            var status = await CallAsync<SynologyTaskStatusData>(
                "SYNO.FileStation.CopyMove", "3", "status", [("taskid", taskId)], cancellationToken).ConfigureAwait(false);
            if (status.ErrorCode is { } code)
            {
                throw MapApiError(code);
            }

            if (status.Finished == true)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw CloudProviderException.Network(new TimeoutException(Localization.L("The request timed out.")));
            }

            var interval = poll < 120 ? PollInterval : TimeSpan.FromTicks(Math.Max(PollInterval.Ticks, TimeSpan.FromSeconds(5).Ticks));
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>How long a server-side copy or move may run before it is given up on.</summary>
    internal TimeSpan CopyMoveTimeout { get; set; } = TimeSpan.FromMinutes(30);

    // MARK: - Metadata

    public async Task<CloudFileItem> GetFileInfoAsync(string path, CancellationToken cancellationToken)
    {
        var result = await CallAsync<SynologyListData>(
            "SYNO.FileStation.List", "2", "getinfo",
            [("path", "[" + JsonQuoted(path) + "]"), ("additional", "[\"size\",\"time\",\"type\"]")],
            cancellationToken, path).ConfigureAwait(false);
        var first = result.Files?.FirstOrDefault();
        if (first is null || first.Code is > 0 || first.Path is null)
        {
            throw CloudProviderException.NotFound(path);
        }

        return ToItem(first);
    }

    /// <summary>
    /// Recursive enumeration, capped at the first 5000 items so a giant share doesn't keep the
    /// NAS busy for hours — DSM itself can give an exact size.
    /// </summary>
    public async Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken)
    {
        long total = 0;
        var seen = 0;
        var queue = new Queue<string>();
        queue.Enqueue(path);
        while (queue.TryDequeue(out var next))
        {
            foreach (var item in await ListInsideAsync(next, cancellationToken).ConfigureAwait(false))
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

    /// <summary>
    /// A File Station search task: start it on the folder (or the root), poll briefly, fetch
    /// up to 200 results — partial results are fine — and stop the task.
    /// </summary>
    public async Task<IReadOnlyList<CloudFileItem>> SearchFilesAsync(string query, string? path, CancellationToken cancellationToken)
    {
        var start = await CallAsync<SynologyTaskStartData>(
            "SYNO.FileStation.Search", "2", "start",
            [("folder_path", path ?? "/"), ("pattern", query), ("recursive", "true")],
            cancellationToken).ConfigureAwait(false);
        if (start.TaskId is not { } taskId)
        {
            return [];
        }

        for (var poll = 0; poll < 10; poll++)
        {
            var status = await CallAsync<SynologyTaskStatusData>("SYNO.FileStation.Search", "2", "status", [("taskid", taskId)], cancellationToken).ConfigureAwait(false);
            if (status.Finished == true)
            {
                break;
            }

            await Task.Delay(PollInterval * 0.6, cancellationToken).ConfigureAwait(false);
        }

        var results = await CallAsync<SynologyListData>(
            "SYNO.FileStation.Search", "2", "list",
            [("taskid", taskId), ("limit", "200"), ("additional", "[\"size\",\"time\",\"type\"]")],
            cancellationToken).ConfigureAwait(false);

        try
        {
            await CallAsync<SynologyEmpty>("SYNO.FileStation.Search", "2", "stop", [("taskid", taskId)], cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException)
        {
            // Best-effort cleanup of the server-side task.
        }

        return (results.Files ?? []).Where(f => f.Path is not null).Select(ToItem).ToList();
    }

    // MARK: - Share links

    /// <summary>
    /// Creates a File Station sharing link. DSM builds the URL from whatever address it believes
    /// it has — often a LAN address or an unreachable DDNS name — hence the note.
    /// </summary>
    public async Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        // Sharing's `path` is a JSON-encoded parameter, like List.getinfo's.
        var extra = new List<(string, string)> { ("path", JsonQuoted(path)) };
        if (!string.IsNullOrEmpty(options.Password))
        {
            extra.Add(("password", options.Password));
        }

        if (options.Expiry is { } expiry)
        {
            extra.Add(("date_expired", JsonQuoted(ExpiryDateString(expiry))));
        }

        var result = await SharingCallAsync<SynologySharingLinksData>("create", extra, cancellationToken).ConfigureAwait(false);
        var link = result.Links?.FirstOrDefault() ?? throw CloudProviderException.InvalidResponse();
        if (link.Error is { } code && code != 0)
        {
            throw MapSharingError(code);
        }

        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var url))
        {
            throw CloudProviderException.InvalidResponse();
        }

        return new CloudShareLink(url)
        {
            // File Station has no documented suffix that streams the file itself.
            DirectDownloadUrl = null,

            // DSM doesn't echo the effective expiry back on create.
            ExpiresAt = options.Expiry,
            HasPassword = !string.IsNullOrEmpty(options.Password),
            Note = LanAddressNote,
        };
    }

    /// <summary>
    /// The link this path already has, or null. DSM has no per-path lookup, so this pages
    /// through the links the account owns. Not finding the path means "not shared"; a DSM
    /// error code is a real error. Links other DSM users made for the file aren't visible.
    /// </summary>
    public async Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        if (await FindSharingLinkAsync(path, cancellationToken).ConfigureAwait(false) is not { } entry)
        {
            return null;
        }

        return ShareLink(entry, null, null) ?? throw CloudProviderException.InvalidResponse();
    }

    /// <summary>
    /// Rewrites the existing link's password and expiry with <c>edit</c>, keeping its id and
    /// therefore the URL recipients already have.
    /// </summary>
    public async Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        if (await FindSharingLinkAsync(path, cancellationToken).ConfigureAwait(false) is not { Id: { } id } existing)
        {
            throw CloudProviderException.CommandFailed(Localization.L("This item doesn't have a public link."));
        }

        // File Station clears a password with "" and lifts an expiry with "0"; leaving either
        // out keeps what's there, so "none" has to be said explicitly. expire_times=0 (unlimited)
        // every time, so an edit doesn't leave a counted-down link in place.
        await SharingCallAsync<SynologyEmpty>(
            "edit",
            [
                ("id", JsonQuoted(id)),
                ("password", string.IsNullOrEmpty(options.Password) ? string.Empty : options.Password),
                ("date_expired", JsonQuoted(options.Expiry is { } expiry ? ExpiryDateString(expiry) : "0")),
                ("expire_times", "0"),
            ],
            cancellationToken).ConfigureAwait(false);

        // `edit` answers with an empty payload: read the link back rather than assume DSM
        // applied what was asked.
        SynologySharingEntry? effective = null;
        try
        {
            effective = (await SharingCallAsync<SynologySharingListData>("getinfo", [("id", JsonQuoted(id))], cancellationToken).ConfigureAwait(false)).Links?.FirstOrDefault();
        }
        catch (CloudProviderException)
        {
        }

        return ShareLink(effective ?? existing, options.Password, options.Expiry) ?? throw CloudProviderException.InvalidResponse();
    }

    /// <summary>Withdraws the link. DSM answers success for an id it no longer knows, so this only fails when the path has no link.</summary>
    public async Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        if (await FindSharingLinkAsync(path, cancellationToken).ConfigureAwait(false) is not { Id: { } id })
        {
            throw CloudProviderException.CommandFailed(Localization.L("This item doesn't have a public link."));
        }

        await SharingCallAsync<SynologyEmpty>("delete", [("id", JsonQuoted(id))], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Pages through the account's links; the page cap guards against a DSM build that ignores <c>offset</c>.</summary>
    private async Task<SynologySharingEntry?> FindSharingLinkAsync(string path, CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var offset = 0;
        for (var page = 0; page < 50; page++)
        {
            var result = await SharingCallAsync<SynologySharingListData>(
                "list",
                [("offset", offset.ToString(CultureInfo.InvariantCulture)), ("limit", pageSize.ToString(CultureInfo.InvariantCulture))],
                cancellationToken).ConfigureAwait(false);
            var links = result.Links ?? [];
            if (links.FirstOrDefault(l => l.Path == path) is { } hit)
            {
                return hit;
            }

            if (links.Count < pageSize)
            {
                return null;
            }

            offset += links.Count;
            if (result.Total is { } total && offset >= total)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Every Sharing call, with the 2000-range sharing codes mapped before the generic ones.</summary>
    private Task<T> SharingCallAsync<T>(string method, IReadOnlyList<(string, string)> extra, CancellationToken cancellationToken)
        => CallAsync<T>("SYNO.FileStation.Sharing", "3", method, extra, cancellationToken, mapError: MapSharingError);

    private static CloudShareLink? ShareLink(SynologySharingEntry entry, string? requestedPassword, DateTimeOffset? requestedExpiry)
    {
        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var url))
        {
            return null;
        }

        return new CloudShareLink(url)
        {
            DirectDownloadUrl = null,
            ExpiresAt = ParseExpiryDate(entry.DateExpired) ?? requestedExpiry,
            HasPassword = entry.HasPassword ?? !string.IsNullOrEmpty(requestedPassword),
            Note = LanAddressNote,
        };
    }

    private static string LanAddressNote => Localization.L("The NAS built this link from the address DSM thinks it has, so it may only work on your local network. Check DSM's external access settings if recipients can't open it.");

    /// <summary><c>date_expired</c> as File Station wants it: a UTC calendar day.</summary>
    internal static string ExpiryDateString(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Reads <c>date_expired</c> back; "0" and "" both mean "never expires".</summary>
    internal static DateTimeOffset? ParseExpiryDate(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "0")
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(raw, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;
    }

    // MARK: - Plumbing

    /// <summary>
    /// One <c>webapi/entry.cgi</c> call, decoded from the JSON envelope. Logs in again once
    /// when DSM reports the SID expired (119).
    /// </summary>
    private async Task<T> CallAsync<T>(
        string api,
        string version,
        string method,
        IReadOnlyList<(string, string)> extra,
        CancellationToken cancellationToken,
        string? path = null,
        Func<int, CloudProviderException>? mapError = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var sid = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
            var query = new List<(string, string)> { ("api", api), ("version", version), ("method", method), ("_sid", sid) };
            query.AddRange(extra);
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri("/webapi/entry.cgi", query));
            using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureHttpSuccess(response);

            var envelope = await response.ReadJsonAsync<SynologyEnvelope<T>>(cancellationToken).ConfigureAwait(false);
            if (envelope.Success && envelope.Data is not null)
            {
                return envelope.Data;
            }

            // Some endpoints succeed without a payload (delete, edit).
            if (envelope.Success && typeof(T) == typeof(SynologyEmpty))
            {
                return (T)(object)new SynologyEmpty();
            }

            var code = envelope.Error?.Code ?? -1;
            if (code == 119 && attempt == 0)
            {
                InvalidateSession(sid);
                continue;
            }

            SupportLogger.Shared.Log($"[Synology] {api}.{method} failed with code {code}", Category, LogLevel.Error);
            if (code == 408 && path is not null)
            {
                throw CloudProviderException.NotFound(path);
            }

            throw (mapError ?? MapApiError)(code);
        }
    }

    /// <summary>Query values are percent-encoded one by one; the server URL's own path is replaced.</summary>
    private Uri BuildUri(string path, IEnumerable<(string Name, string Value)> query)
    {
        if (!Uri.TryCreate(Credentials.ServerUrl, UriKind.Absolute, out var server))
        {
            throw CloudProviderException.InvalidResponse();
        }

        var encoded = string.Join('&', query.Select(q => Uri.EscapeDataString(q.Name) + "=" + Uri.EscapeDataString(q.Value)));
        return new Uri(server.GetLeftPart(UriPartial.Authority) + path + "?" + encoded);
    }

    private void EnsureHttpSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        SupportLogger.Shared.Log($"[Synology] HTTP {(int)response.StatusCode} from {HostFrom(Credentials.ServerUrl)}", Category, LogLevel.Error);
        throw CloudProviderException.ServerError((int)response.StatusCode);
    }

    /// <summary>
    /// When a download answered with a small JSON body, buffers it: a DSM error envelope comes
    /// back as its code; anything else was the file itself and is left in place (as a
    /// <see cref="ByteArrayContent"/>) for the caller to save.
    /// </summary>
    private static async Task<SynologyErrorInfo?> ReadDownloadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var headers = response.Content.Headers;
        if (headers.ContentType?.MediaType is not ("application/json" or "text/json" or "text/plain") || headers.ContentLength is not (>= 0 and <= 4096))
        {
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Only an explicit {"success":false,…} is DSM talking; any other JSON is file content.
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.False)
            {
                var code = root.TryGetProperty("error", out var error)
                           && error.ValueKind == JsonValueKind.Object
                           && error.TryGetProperty("code", out var value)
                           && value.TryGetInt32(out var number)
                    ? number
                    : -1;
                return new SynologyErrorInfo(code);
            }
        }
        catch (JsonException)
        {
        }

        var replacement = new ByteArrayContent(bytes);
        foreach (var header in headers)
        {
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var original = response.Content;
        response.Content = replacement;
        original.Dispose();
        return null;
    }

    private static CloudFileItem ToItem(SynologyFileEntry file)
    {
        var path = file.Path ?? string.Empty;
        var name = file.Name ?? NameOf(path);
        return new CloudFileItem(path, name, path, file.IsDir, file.Additional?.Size ?? 0, file.Additional?.Time?.ModifiedDate ?? CloudFileItem.UnknownDate)
        {
            IsHidden = CloudFileItem.IsDotHidden(name),
        };
    }

    /// <summary>Encodes a value as a JSON string literal, for the File Station parameters that expect JSON.</summary>
    internal static string JsonQuoted(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string ParentPath(string path)
    {
        var slash = path.TrimEnd('/').LastIndexOf('/');
        return slash <= 0 ? string.Empty : path[..slash];
    }

    private static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    // MARK: - Server URL

    /// <summary>
    /// "nas.local" → "https://nas.local:5001": HTTPS by default, and DSM's port (5001 for
    /// HTTPS, 5000 for HTTP) unless the URL names one. No trailing slash.
    /// </summary>
    public static string NormalizeServerUrl(string raw)
    {
        var text = raw.Trim();
        var https = !text.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        if (https && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text;
        }

        if (!HasExplicitPort(text))
        {
            // Insert the port after the host, keeping anything that follows it.
            var authorityStart = text.IndexOf("://", StringComparison.Ordinal) + 3;
            var authorityEnd = text.IndexOfAny(['/', '?', '#'], authorityStart);
            authorityEnd = authorityEnd < 0 ? text.Length : authorityEnd;
            text = text[..authorityEnd] + (https ? ":5001" : ":5000") + text[authorityEnd..];
        }

        return text.EndsWith('/') ? text[..^1] : text;
    }

    /// <summary>The host of the server URL, for the account label and the self-signed exemption.</summary>
    public static string HostFrom(string serverUrl)
        => Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : "synology";

    private static bool HasExplicitPort(string url)
    {
        var start = url.IndexOf("://", StringComparison.Ordinal);
        var authority = start < 0 ? url : url[(start + 3)..];
        var end = authority.IndexOfAny(['/', '?', '#']);
        authority = end < 0 ? authority : authority[..end];
        var at = authority.LastIndexOf('@');
        authority = at < 0 ? authority : authority[(at + 1)..];
        var bracket = authority.LastIndexOf(']');
        return authority.IndexOf(':', bracket + 1) >= 0;
    }

    // MARK: - Errors

    /// <summary>SYNO.API.Auth codes.</summary>
    public static CloudProviderException MapAuthError(int code) => CloudProviderException.CommandFailed(code switch
    {
        400 => Localization.L("No such account or incorrect password."),
        401 => Localization.L("Account is disabled."),
        402 => Localization.L("Permission denied."),
        403 => Localization.L("This account requires a 2-factor authentication (OTP) code."),
        404 => Localization.L("OTP code is incorrect."),
        405 => Localization.L("OTP code authentication has failed too many times."),
        406 => Localization.L("Synology administrator must enforce 2-factor authentication."),
        407 => Localization.L("Maximum number of OTP retries reached. Try again later."),
        408 => Localization.L("Password expired — please reset it from DSM."),
        409 => Localization.L("Password must be changed at first login."),
        _ => Localization.L("Synology login failed (code {0}).", code),
    });

    /// <summary>
    /// A re-login nobody is there to answer (session expired mid-browse). A wrong or expired
    /// password, or an account that now needs an OTP code, is a "sign in again" — the panel
    /// turns Unauthorized into its re-auth banner. The rest keep DSM's explanation.
    /// </summary>
    private static CloudProviderException BackgroundLoginError(int code)
    {
        SupportLogger.Shared.Log($"[Synology] Re-login failed with code {code}", Category, LogLevel.Error);
        return code is 400 or 403 or 404 or 408 or 409 ? CloudProviderException.Unauthorized() : MapAuthError(code);
    }

    /// <summary>The DSM codes macOS maps; 408 ("no such file or directory") becomes NotFound where the call names a path.</summary>
    private static CloudProviderException MapApiError(int code) => code switch
    {
        400 => CloudProviderException.CommandFailed(Localization.L("Invalid parameter for the request.")),
        401 => CloudProviderException.CommandFailed(Localization.L("Unknown API method.")),
        402 => CloudProviderException.CommandFailed(Localization.L("Invalid API method version.")),
        403 => CloudProviderException.CommandFailed(Localization.L("Invalid API method.")),
        405 => CloudProviderException.CommandFailed(Localization.L("Insufficient user privilege.")),
        406 => CloudProviderException.CommandFailed(Localization.L("Connection timed out.")),
        407 => CloudProviderException.CommandFailed(Localization.L("Multiple login detected.")),
        408 => CloudProviderException.CommandFailed(Localization.L("Request denied — too many connections.")),
        119 => CloudProviderException.NotAuthenticated(),
        401_400 or 1_400 or 1_401 or 1_402 => CloudProviderException.CommandFailed(Localization.L("Synology rejected the file path.")),
        _ => CloudProviderException.ServerError(code),
    };

    private static CloudProviderException MapSharingError(int code) => code switch
    {
        2000 => CloudProviderException.CommandFailed(Localization.L("The sharing link does not exist.")),
        2001 => CloudProviderException.CommandFailed(Localization.L("The NAS has reached its maximum number of sharing links — delete some in File Station and try again.")),
        2002 => CloudProviderException.CommandFailed(Localization.L("Failed to access the NAS's sharing links.")),
        _ => MapApiError(code),
    };

    /// <summary>
    /// multipart/form-data with the text fields first and the file streamed last, the same
    /// bytes macOS wrote to its temporary body file. Progress counts the file's bytes only.
    /// </summary>
    private sealed class UploadContent : HttpContent
    {
        private readonly byte[] _head;
        private readonly byte[] _tail;
        private readonly string _localPath;
        private readonly long _fileLength;
        private readonly ByteProgress? _onBytes;

        public UploadContent(IReadOnlyList<(string Name, string Value)> fields, string fileName, string localPath, ByteProgress? onBytes)
        {
            var boundary = "----FileFlussSynology" + Guid.NewGuid().ToString("D").ToUpperInvariant();
            var head = new StringBuilder();
            foreach (var (name, value) in fields)
            {
                head.Append("--").Append(boundary).Append("\r\n")
                    .Append("Content-Disposition: form-data; name=\"").Append(name).Append("\"\r\n\r\n")
                    .Append(value).Append("\r\n");
            }

            head.Append("--").Append(boundary).Append("\r\n")
                .Append("Content-Disposition: form-data; name=\"file\"; filename=\"").Append(fileName).Append("\"\r\n")
                .Append("Content-Type: application/octet-stream\r\n\r\n");

            _head = Encoding.UTF8.GetBytes(head.ToString());
            _tail = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");
            _localPath = localPath;
            _fileLength = new FileInfo(localPath).Length;
            _onBytes = onBytes;
            Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(_head, cancellationToken).ConfigureAwait(false);
            await using (var file = HttpTransfer.OpenForUpload(_localPath))
            {
                await HttpTransfer.CopyAsync(file, stream, _onBytes, cancellationToken).ConfigureAwait(false);
            }

            await stream.WriteAsync(_tail, cancellationToken).ConfigureAwait(false);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = _head.Length + _fileLength + _tail.Length;
            return true;
        }
    }
}
