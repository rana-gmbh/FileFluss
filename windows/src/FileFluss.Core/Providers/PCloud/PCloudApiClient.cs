// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.PCloud;

/// <summary>
/// pCloud's JSON API for one account — the macOS <c>PCloudAPIClient</c>. Every call is a
/// token-stamped GET (uploads a POST) against the account's regional host; failures come back
/// as HTTP 200 with a non-zero <c>result</c>. Items are addressed by path throughout.
///
/// <para>The token travels in the query string, as pCloud requires — request URLs are never
/// logged.</para>
/// </summary>
internal sealed class PCloudApiClient
{
    // The app FileFluss is registered as with pCloud (macOS PCloudAppCredentials). pCloud's
    // code flow needs the secret for the token exchange, so it ships in the binary, as it
    // must for a desktop app.
    internal const string ClientId = "vKBvKTqCvbF";
    internal const string ClientSecret = "YsF6bz4dFjHz4hdGxVkf7L4PAHEk";
    internal const string AuthorizeUrl = "https://my.pcloud.com/oauth2/authorize";

    /// <summary>
    /// Where an account's data lives decides which host serves it: api.pcloud.com in the US,
    /// eapi.pcloud.com in the EU. The browser sign-in names it, and only these two are accepted.
    /// </summary>
    internal static readonly string[] ApiHosts = ["api.pcloud.com", "eapi.pcloud.com"];

    /// <summary>
    /// pCloud matches the redirect exactly, port included, so each of these is registered in
    /// the app's settings at docs.pcloud.com: <c>http://localhost</c>,
    /// <c>http://localhost:53682</c>, <c>http://localhost:53683</c>. Port 80 comes first
    /// because its address needs no port; the others cover a PC where something else has it —
    /// on Windows often HTTP.sys, for IIS or another system service.
    /// </summary>
    internal static readonly int[] RedirectPorts = [80, 53682, 53683];

    private const string Category = "pCloud";

    private readonly HttpClient _http;

    public PCloudApiClient(HttpClient http, PCloudCredentials credentials)
    {
        _http = http;
        Credentials = credentials;
    }

    public PCloudCredentials Credentials { get; }

    // MARK: - Folders

    public async Task<IReadOnlyList<CloudFileItem>> ListFolderAsync(string path, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        var response = await RequestAsync<PCloudMetadataResponse>("listfolder", [("path", path), ("timeformat", "timestamp")], path, cancellationToken).ConfigureAwait(false);
        return (response.Metadata?.Contents ?? []).Select(item => item.ToCloudFileItem(path)).ToList();
    }

    /// <summary>One recursive listing, summed locally.</summary>
    public async Task<long> FolderSizeAsync(string path, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        var response = await RequestAsync<PCloudMetadataResponse>("listfolder", [("path", path), ("recursive", "1"), ("timeformat", "timestamp")], path, cancellationToken).ConfigureAwait(false);
        return response.Metadata?.TotalFileSize() ?? 0;
    }

    public Task CreateFolderAsync(string path, CancellationToken cancellationToken)
        => RequestAsync<JsonElement>("createfolderifnotexists", [("path", CloudPath.Normalize(path))], path, cancellationToken);

    public Task DeleteFolderAsync(string path, CancellationToken cancellationToken)
        => RequestAsync<JsonElement>("deletefolderrecursive", [("path", CloudPath.Normalize(path))], path, cancellationToken);

    /// <summary>
    /// <c>renamefile</c> and <c>renamefolder</c> accept any destination path, so they double
    /// as server-side moves across folders.
    /// </summary>
    public Task RenameFileAsync(string path, string toPath, CancellationToken cancellationToken)
        => RequestAsync<JsonElement>("renamefile", [("path", CloudPath.Normalize(path)), ("topath", CloudPath.Normalize(toPath))], path, cancellationToken);

    public Task RenameFolderAsync(string path, string toPath, CancellationToken cancellationToken)
        => RequestAsync<JsonElement>("renamefolder", [("path", CloudPath.Normalize(path)), ("topath", CloudPath.Normalize(toPath))], path, cancellationToken);

    public Task CopyFileAsync(string path, string toPath, CancellationToken cancellationToken)
        => RequestAsync<JsonElement>("copyfile", [("path", CloudPath.Normalize(path)), ("topath", CloudPath.Normalize(toPath))], path, cancellationToken);

    public Task CopyFolderAsync(string path, string toPath, CancellationToken cancellationToken)
        => RequestAsync<JsonElement>("copyfolder", [("path", CloudPath.Normalize(path)), ("topath", CloudPath.Normalize(toPath))], path, cancellationToken);

    // MARK: - Files

    public async Task<CloudFileItem> StatAsync(string path, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        var response = await RequestAsync<PCloudMetadataResponse>("stat", [("path", path), ("timeformat", "timestamp")], path, cancellationToken).ConfigureAwait(false);
        var metadata = response.Metadata ?? throw CloudProviderException.InvalidResponse();
        return metadata.ToCloudFileItem(CloudPath.Parent(path));
    }

    /// <summary>
    /// pCloud quirks this handles:
    /// <list type="bullet">
    /// <item>Error 2055 on the first deletes after a bulk upload — the file's metadata is
    /// briefly locked while pCloud processes the upload. Transient; retried with backoff.</item>
    /// <item>NotFound on the first attempt when the path is really a folder — propagated, so
    /// the provider falls through to deleting a folder.</item>
    /// <item>Shadow duplicates occasionally left by rapid upload+replace cycles — the
    /// path-based delete repeats until pCloud says NotFound.</item>
    /// </list>
    /// </summary>
    public async Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        var deletedOnce = false;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                await RequestAsync<JsonElement>("deletefile", [("path", path)], path, cancellationToken).ConfigureAwait(false);
                deletedOnce = true;
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
            {
                if (!deletedOnce && attempt == 0)
                {
                    throw;
                }

                return;
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.ServerError && e.StatusCode == 2055)
            {
                // Metadata locked — back off and retry (250 ms, 500 ms, … 1.5 s).
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException) when (deletedOnce)
            {
                break;
            }
        }

        try
        {
            // timeformat matters here too: a decode failure would masquerade as "the file is
            // still there".
            await RequestAsync<JsonElement>("stat", [("path", path), ("timeformat", "timestamp")], path, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NotFound)
        {
            return;
        }

        throw CloudProviderException.ServerError(0);
    }

    public async Task<Uri> FileLinkAsync(string path, CancellationToken cancellationToken)
    {
        var response = await RequestAsync<PCloudFileLinkResponse>("getfilelink", [("path", CloudPath.Normalize(path))], path, cancellationToken).ConfigureAwait(false);
        return response.DownloadUrl() ?? throw CloudProviderException.InvalidResponse();
    }

    /// <summary>Resolves a CDN link (no token needed there) and streams it to disk.</summary>
    public async Task DownloadAsync(string remotePath, string localPath, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var url = await FileLinkAsync(remotePath, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"[pCloud] Download of {remotePath} → HTTP {(int)response.StatusCode}", Category, LogLevel.Error);
            throw CloudProviderException.InvalidResponse();
        }

        await HttpTransfer.SaveToFileAsync(response, localPath, onBytes, cancellationToken).ConfigureAwait(false);
    }

    // MARK: - Thumbnails

    /// <summary>
    /// The edge of the square box asked of <c>getthumb</c>: the first value from
    /// <paramref name="maxPixelSize"/> up that pCloud accepts — divisible by 4 or 5, 16 to 1024
    /// (the height's limit, the lower of the two). Without <c>crop</c> the picture keeps its
    /// proportions inside the box, so its longer edge is this one.
    /// </summary>
    internal static int ThumbnailEdge(int maxPixelSize)
    {
        var edge = Math.Clamp(maxPixelSize, 16, 1024);
        while (edge % 4 != 0 && edge % 5 != 0)
        {
            edge++;
        }

        return edge;
    }

    /// <summary>The file id inside a listed item's id ("f123"), or null for anything else, which is then looked up by path instead.</summary>
    internal static ulong? FileId(string itemId)
        => itemId.StartsWith('f') &&
           ulong.TryParse(itemId.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
           value != 0
            ? value
            : null;

    /// <summary>The <c>getthumb</c> parameters, without the token.</summary>
    internal static List<(string Key, string Value)> ThumbnailParameters(ulong? fileId, string path, int maxPixelSize)
    {
        var edge = ThumbnailEdge(maxPixelSize).ToString(CultureInfo.InvariantCulture);
        return
        [
            fileId is { } id ? ("fileid", id.ToString(CultureInfo.InvariantCulture)) : ("path", CloudPath.Normalize(path)),
            ("size", $"{edge}x{edge}"),
        ];
    }

    /// <summary>pCloud's answers that mean "no preview for this file": 1014, a type it can't draw, and 3001, a file it failed to draw.</summary>
    internal static bool IsThumbnailRefusal(int code) => code is 1014 or 3001;

    /// <summary>
    /// A preview made by pCloud, or null where pCloud has none for the file. A token-stamped
    /// GET on the account's own host like every other call here. The answer is the picture
    /// itself; a refusal comes back as the usual JSON envelope with a non-zero <c>result</c>.
    /// </summary>
    public async Task<byte[]?> ThumbnailAsync(ulong? fileId, string path, int maxPixelSize, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUri("getthumb", ThumbnailParameters(fileId, path, maxPixelSize)));
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"[pCloud] getthumb → HTTP {(int)response.StatusCode}", Category, LogLevel.Error);
            throw response.StatusCode == HttpStatusCode.TooManyRequests
                ? CloudProviderException.RateLimited()
                : CloudProviderException.ServerError((int)response.StatusCode);
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (response.Headers.Contains("X-Error") || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var (code, _) = ResultCode(body);
            if (code == 0)
            {
                // JSON where a picture was due, yet no error: nothing to show.
                throw CloudProviderException.InvalidResponse();
            }

            if (IsThumbnailRefusal(code))
            {
                return null;
            }

            throw MapError(code, path);
        }

        return body.Length == 0 ? null : body;
    }

    /// <summary>
    /// <c>uploadfile</c> with the file as one multipart part, streamed from disk. The local
    /// modification and creation times go along as <c>mtime</c>/<c>ctime</c>, and
    /// <c>nopartial=1</c> stops pCloud keeping a truncated file when the upload breaks off.
    /// </summary>
    public async Task UploadAsync(string localPath, string folderPath, string fileName, ByteProgress? onBytes, CancellationToken cancellationToken)
    {
        var info = new FileInfo(localPath);
        var parameters = new List<(string, string)>
        {
            ("path", CloudPath.Normalize(folderPath)),
            ("filename", fileName),
            ("nopartial", "1"),
        };
        if (KnownLocalDate(info.LastWriteTimeUtc) is { } modified)
        {
            parameters.Add(("mtime", modified.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        }

        if (KnownLocalDate(info.CreationTimeUtc) is { } created)
        {
            parameters.Add(("ctime", created.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        }

        // pCloud names the file after the part's filename, not the query parameter, and takes
        // the RFC 2047 word .NET's MultipartFormDataContent writes for a non-ASCII name
        // literally: "Brückenstr.docx" landed as "=?utf-8?B?…?=". Raw UTF-8 in the quotes, the
        // same body as macOS.
        using var content = new MultipartFileContent(localPath, "file", fileName, "application/octet-stream", onBytes);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUri("uploadfile", parameters)) { Content = content };
        using var response = await _http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"[pCloud] Upload to {folderPath} → HTTP {(int)response.StatusCode}", Category, LogLevel.Error);
            throw CloudProviderException.InvalidResponse();
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var (code, _) = ResultCode(body);
        if (code != 0)
        {
            throw MapError(code, CloudPath.Combine(folderPath, fileName));
        }
    }

    /// <summary>Stamps a file's mtime via <c>setfilemtime</c>, so copies keep the source's "Date modified".</summary>
    public Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken)
        => RequestAsync<JsonElement>(
            "setfilemtime",
            [("path", CloudPath.Normalize(path)), ("newtime", date.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))],
            path,
            cancellationToken);

    // MARK: - Account

    public async Task<PCloudUserInfo> UserInfoAsync(CancellationToken cancellationToken)
    {
        var response = await RequestAsync<PCloudUserInfoResponse>("userinfo", [], null, cancellationToken).ConfigureAwait(false);
        return new PCloudUserInfo(response.Email ?? string.Empty, response.UserId ?? 0, response.Quota ?? 0, response.UsedQuota ?? 0);
    }

    // MARK: - Share links

    /// <summary>
    /// A public download link. Password (<c>linkpassword</c>) and expiry (<c>expire</c>) are
    /// Premium-only: a free account gets a non-zero result with pCloud's own wording, passed
    /// through verbatim rather than flattened to a code the user can't act on.
    /// </summary>
    public async Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        var parameters = new List<(string, string)> { ("path", CloudPath.Normalize(path)) };
        if (!string.IsNullOrEmpty(options.Password))
        {
            parameters.Add(("linkpassword", options.Password));
        }

        if (options.Expiry is { } expiry)
        {
            parameters.Add(("expire", PCloudDates.ExpiryParameter(expiry)));
        }

        var response = await RequestSurfacingServerMessageAsync<PCloudPubLinkResponse>("getfilepublink", parameters, path, cancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(response.Link, UriKind.Absolute, out var url))
        {
            throw CloudProviderException.InvalidResponse();
        }

        return new CloudShareLink(url)
        {
            DirectDownloadUrl = response.Code is { } code ? await PublicLinkDownloadUrlOrNullAsync(code, cancellationToken).ConfigureAwait(false) : null,
            // getfilepublink doesn't echo the effective expiry, so report what was asked for.
            ExpiresAt = options.Expiry,
            HasPassword = !string.IsNullOrEmpty(options.Password),
        };
    }

    /// <summary>
    /// The link the file already has, or null. pCloud has no "is this shared?" call, so the
    /// account's links are listed and searched; a non-zero result (expired token, rate limit)
    /// is a real error and propagates with pCloud's wording.
    /// </summary>
    public async Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        var publink = await FindPublinkAsync(path, cancellationToken).ConfigureAwait(false);
        return publink is null ? null : await ShareLinkFromAsync(publink, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rewrites password and expiry with <c>changepublink</c>; the link's code, and so its
    /// URL, survives. Omitting <c>linkpassword</c> would leave the old password in place, so
    /// clearing a setting has to say so (<c>deletepassword</c> / <c>deleteexpire</c>).
    /// </summary>
    public async Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        var existing = await FindPublinkAsync(path, cancellationToken).ConfigureAwait(false);
        if (existing?.LinkId is not { } linkId)
        {
            throw CloudProviderException.CommandFailed(Localization.L("This file doesn't have a public link."));
        }

        var parameters = new List<(string, string)> { ("linkid", linkId.ToString(CultureInfo.InvariantCulture)) };
        parameters.Add(string.IsNullOrEmpty(options.Password) ? ("deletepassword", "1") : ("linkpassword", options.Password));
        parameters.Add(options.Expiry is { } expiry ? ("expire", PCloudDates.ExpiryParameter(expiry)) : ("deleteexpire", "1"));

        // Premium-only settings fail here just as on create, in pCloud's own words.
        await RequestSurfacingServerMessageAsync<JsonElement>("changepublink", parameters, path, cancellationToken).ConfigureAwait(false);

        // changepublink answers with the bare envelope: read the link back rather than
        // assuming pCloud applied what was asked for.
        var updated = await FindPublinkAsync(path, cancellationToken).ConfigureAwait(false);
        return await ShareLinkFromAsync(updated ?? existing, options.Password, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        if ((await FindPublinkAsync(path, cancellationToken).ConfigureAwait(false))?.LinkId is not { } linkId)
        {
            throw CloudProviderException.CommandFailed(Localization.L("This file doesn't have a public link."));
        }

        await RequestSurfacingServerMessageAsync<JsonElement>("deletepublink", [("linkid", linkId.ToString(CultureInfo.InvariantCulture))], path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds <paramref name="path"/> among the account's public links. The file id match is
    /// exact, so it comes first: pCloud echoes stored paths inconsistently (with or without
    /// the leading slash, sometimes only the name) and a path match would confuse two files
    /// whose links predate a rename. Path, then name, are fallbacks for links without an id.
    /// </summary>
    private async Task<PCloudPublink?> FindPublinkAsync(string path, CancellationToken cancellationToken)
    {
        path = CloudPath.Normalize(path);
        ulong? fileId = null;
        try
        {
            fileId = await FileIdAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudProviderException e)
        {
            SupportLogger.Shared.Log($"pCloud: couldn't resolve a file id for {path} — {e.Message}", "sharing", LogLevel.Error);
        }

        var response = await RequestSurfacingServerMessageAsync<PCloudPublinksResponse>("listpublinks", [("timeformat", "timestamp")], path, cancellationToken).ConfigureAwait(false);
        var links = response.Publinks ?? [];

        if (fileId is { } id && links.FirstOrDefault(l => l.ResolvedFileId == id) is { } byId)
        {
            return byId;
        }

        if (links.FirstOrDefault(l => l.ResolvedPath == path) is { } byPath)
        {
            return byPath;
        }

        var fileName = CloudPath.Name(path);
        if (fileName.Length > 0 &&
            links.FirstOrDefault(l => l.ResolvedPath is { } candidate && LastComponent(candidate) == fileName) is { } byName)
        {
            return byName;
        }

        // "No public link" is indistinguishable from "the matching failed" without the payload.
        var described = string.Join(' ', links.Select(l =>
            $"[id={l.LinkId?.ToString(CultureInfo.InvariantCulture) ?? "?"} fileid={l.ResolvedFileId?.ToString(CultureInfo.InvariantCulture) ?? "?"} path={l.ResolvedPath ?? "?"}]"));
        SupportLogger.Shared.Log(
            $"pCloud listpublinks: no match for {path} (fileid {fileId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}) among {links.Count} link(s): {described}",
            "sharing",
            LogLevel.Error);
        return null;
    }

    private async Task<ulong?> FileIdAsync(string path, CancellationToken cancellationToken)
    {
        // timeformat=timestamp is required, not decoration: without it the dates arrive as
        // strings, the decode fails and the file id silently goes missing — which is exactly
        // how "stop sharing" once claimed a shared file had no link.
        var response = await RequestAsync<PCloudMetadataResponse>("stat", [("path", path), ("timeformat", "timestamp")], path, cancellationToken).ConfigureAwait(false);
        return response.Metadata?.FileId;
    }

    private async Task<CloudShareLink> ShareLinkFromAsync(PCloudPublink publink, string? requestedPassword, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(publink.Link, UriKind.Absolute, out var landing))
        {
            // listpublinks doesn't always echo the full link; the landing page has a documented form.
            landing = !string.IsNullOrEmpty(publink.Code)
                ? new Uri($"https://my.pcloud.com/publink/show?code={CloudHttp.Escape(publink.Code)}")
                : throw CloudProviderException.InvalidResponse();
        }

        return new CloudShareLink(landing)
        {
            DirectDownloadUrl = publink.Code is { } code ? await PublicLinkDownloadUrlOrNullAsync(code, cancellationToken).ConfigureAwait(false) : null,
            ExpiresAt = publink.ExpiryDate,
            HasPassword = publink.HasPassword ?? !string.IsNullOrEmpty(requestedPassword),
        };
    }

    /// <summary>
    /// Resolves a public-link code to the CDN URL serving the bytes (<c>getpublinkdownload</c>).
    /// Best effort: a landing-page-only link is still useful, so failure must not sink the operation.
    /// </summary>
    private async Task<Uri?> PublicLinkDownloadUrlOrNullAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            var response = await RequestAsync<PCloudFileLinkResponse>("getpublinkdownload", [("code", code)], null, cancellationToken).ConfigureAwait(false);
            return response.DownloadUrl();
        }
        catch (CloudProviderException)
        {
            return null;
        }
    }

    // MARK: - Browser sign-in

    /// <summary>
    /// The authorize page of pCloud's OAuth 2.0 code flow (macOS <c>PCloudAPIClient.startOAuthFlow</c>).
    /// The user signs in and approves FileFluss there; pCloud then redirects back with a code
    /// and the API host of the account's region. Its tokens don't expire, so there is no
    /// refresh step, and pCloud takes no PKCE.
    /// </summary>
    public static Uri BuildAuthorizeUri(string redirectUri, string state)
    {
        var query = new (string Key, string Value)[]
        {
            ("client_id", ClientId),
            ("response_type", "code"),
            ("redirect_uri", redirectUri),
            ("state", state),
        };
        return new Uri(AuthorizeUrl + "?" + string.Join('&', query.Select(q => $"{q.Key}={CloudHttp.Escape(q.Value)}")));
    }

    /// <summary>
    /// Exchanges the redirect's code for a token on the API host the redirect named — only if
    /// that is one of pCloud's own (<see cref="OAuthHost"/>), because the app secret goes
    /// there. When the answer doesn't name the user, userinfo does.
    /// </summary>
    public static async Task<PCloudCredentials> ExchangeCodeAsync(HttpClient http, string code, string? hostname, CancellationToken cancellationToken)
    {
        var host = OAuthHost(hostname);
        byte[] body;
        using (var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(host, "oauth2_token", [("client_id", ClientId), ("client_secret", ClientSecret), ("code", code)])))
        using (var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                SupportLogger.Shared.Log($"[pCloud] oauth2_token → HTTP {(int)response.StatusCode}", Category, LogLevel.Error);
                throw CloudProviderException.ServerError((int)response.StatusCode);
            }

            body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        var (accessToken, userId) = ParseTokenResponse(body);
        var credentials = new PCloudCredentials(accessToken, host, userId ?? 0, IsOAuth: true);
        if (userId is null)
        {
            // Not every answer names the user; userinfo always does.
            var info = await new PCloudApiClient(http, credentials).UserInfoAsync(cancellationToken).ConfigureAwait(false);
            credentials = credentials with { UserId = info.UserId };
        }

        return credentials;
    }

    /// <summary>
    /// Reads oauth2_token's answer. The documentation promises <c>access_token</c> and
    /// <c>uid</c>, but pCloud actually sends <c>userid</c> (seen 2026-11: access_token,
    /// locationid, result, token_type, userid), so both spellings are read, as a number or as
    /// text, and <c>auth</c> for the token too. A refusal carries pCloud's own words. The
    /// support log records which fields came back, never their values.
    /// </summary>
    internal static (string AccessToken, ulong? UserId) ParseTokenResponse(byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw CloudProviderException.InvalidResponse();
            }

            var result = root.TryGetProperty("result", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var code) ? code : -1;
            var fields = string.Join(", ", root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            SupportLogger.Shared.Log($"pCloud oauth2_token: result {result}, fields {fields}", Category);

            if (result != 0)
            {
                throw CloudProviderException.CommandFailed(Text(root, "error") ?? Localization.L("pCloud didn't issue a token."));
            }

            var token = Text(root, "access_token") ?? Text(root, "auth")
                ?? throw CloudProviderException.CommandFailed(Localization.L("pCloud didn't issue a token."));
            return (token, Number(root, "uid") ?? Number(root, "userid"));
        }

        static string? Text(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

        static ulong? Number(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetUInt64(out var number) => number,
                JsonValueKind.String when ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) => number,
                _ => null,
            };
        }
    }

    /// <summary>
    /// The API host named in the redirect, if it is one of pCloud's own. Anything else is
    /// refused: the code and the app secret go there next.
    /// </summary>
    internal static string OAuthHost(string? hostname)
    {
        if (hostname?.ToLowerInvariant() is { } host && ApiHosts.Contains(host, StringComparer.Ordinal))
        {
            return host;
        }

        SupportLogger.Shared.Log($"pCloud: the sign-in named an API host that isn't pCloud's ({hostname ?? "none"})", Category, LogLevel.Error);
        throw CloudProviderException.InvalidResponse();
    }

    // MARK: - HTTP

    /// <summary>Runs <paramref name="method"/>, mapping a non-zero result to the matching error.</summary>
    private async Task<T> RequestAsync<T>(string method, IEnumerable<(string Key, string Value)> parameters, string? itemPath, CancellationToken cancellationToken)
    {
        var body = await FetchAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        var (code, _) = ResultCode(body);
        if (code != 0)
        {
            throw MapError(code, itemPath);
        }

        return Deserialize<T>(body);
    }

    /// <summary>
    /// Like <see cref="RequestAsync{T}"/>, but a failure carries pCloud's own <c>error</c>
    /// text. The plain helper collapses a result to a mapped kind so callers such as
    /// <see cref="DeleteFileAsync"/> can react to NotFound or 2055; the sharing calls want
    /// the opposite — their interesting failures (Premium-only options) are only explicable
    /// in pCloud's words.
    /// </summary>
    private async Task<T> RequestSurfacingServerMessageAsync<T>(string method, IEnumerable<(string Key, string Value)> parameters, string? itemPath, CancellationToken cancellationToken)
    {
        var body = await FetchAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        var (code, error) = ResultCode(body);
        if (code != 0)
        {
            throw string.IsNullOrEmpty(error) ? MapError(code, itemPath) : CloudProviderException.CommandFailed(error);
        }

        return Deserialize<T>(body);
    }

    private async Task<byte[]> FetchAsync(string method, IEnumerable<(string Key, string Value)> parameters, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUri(method, parameters));
        using var response = await _http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            SupportLogger.Shared.Log($"[pCloud] {method} → HTTP {(int)response.StatusCode}", Category, LogLevel.Error);
            throw response.StatusCode == HttpStatusCode.TooManyRequests
                ? CloudProviderException.RateLimited()
                : CloudProviderException.ServerError((int)response.StatusCode);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"https://{account host}/{method}?access_token=…&amp;…" — or <c>auth=</c> for a token from before the browser sign-in.</summary>
    private Uri ApiUri(string method, IEnumerable<(string Key, string Value)> parameters)
        => BuildUri(Credentials.Hostname, method, new[] { Credentials.AuthParameter }.Concat(parameters));

    private static Uri BuildUri(string hostname, string method, IEnumerable<(string Key, string Value)> parameters)
    {
        var query = string.Join('&', parameters.Select(p => $"{p.Key}={CloudHttp.Escape(p.Value)}"));
        return new Uri(query.Length == 0 ? $"https://{hostname}/{method}" : $"https://{hostname}/{method}?{query}");
    }

    /// <summary>The envelope's <c>result</c> and <c>error</c>; InvalidResponse when the body isn't one.</summary>
    private static (int Code, string? Error) ResultCode(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var result) || !result.TryGetInt32(out var code))
            {
                throw CloudProviderException.InvalidResponse();
            }

            var error = root.TryGetProperty("error", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            return (code, error);
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    private static T Deserialize<T>(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, CloudHttp.Json) ?? throw CloudProviderException.InvalidResponse();
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    /// <summary>pCloud's result codes (https://docs.pcloud.com/errors/).</summary>
    internal static CloudProviderException MapError(int code, string? itemPath) => code switch
    {
        1000 => CloudProviderException.NotAuthenticated(),   // Log in required.
        2000 => CloudProviderException.Unauthorized(),       // Log in failed (token expired or revoked).
        2003 => CloudProviderException.Unauthorized(),       // Access denied.
        2005 => CloudProviderException.NotFound(itemPath ?? string.Empty), // Directory does not exist.
        2009 => CloudProviderException.NotFound(itemPath ?? string.Empty), // File not found.
        2008 => CloudProviderException.QuotaExceeded(),
        4000 => CloudProviderException.RateLimited(),        // Too many login tries from this IP.
        _ => CloudProviderException.ServerError(code),
    };

    private static string LastComponent(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    private static DateTimeOffset? KnownLocalDate(DateTime utc)
        => utc.Year > 1601 ? new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)) : null;
}
