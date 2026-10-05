// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.PCloud;

/// <summary>
/// pCloud's JSON API for one account — the macOS <c>PCloudAPIClient</c>. Every call is an
/// auth-stamped GET (uploads a POST) against the account's regional host; failures come back
/// as HTTP 200 with a non-zero <c>result</c>. Items are addressed by path throughout.
///
/// <para>The token travels in the query string, as pCloud requires — request URLs are never
/// logged.</para>
/// </summary>
internal sealed class PCloudApiClient
{
    /// <summary>EU first, then US — the order macOS tries them in.</summary>
    internal static readonly string[] Hostnames = ["eapi.pcloud.com", "api.pcloud.com"];

    /// <summary>pCloud's sign-in endpoints are sent a browser's user agent, as on macOS.</summary>
    internal const string BrowserUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15";

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

        var file = ProgressStreamContent.FromFile(localPath, onBytes);
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"file\"",
            // pCloud names the file from the query parameter; this is only the part's label,
            // and a quote would make the header invalid.
            FileName = $"\"{fileName.Replace('"', '_').Replace('\\', '_')}\"",
        };
        // .NET quotes the boundary parameter; macOS sends it bare, which is what pCloud has
        // been seen to accept. A GUID is a valid token either way.
        var boundary = Guid.NewGuid().ToString();
        using var content = new MultipartFormDataContent(boundary);
        content.Headers.ContentType!.Parameters.Single(p => p.Name == "boundary").Value = boundary;
        content.Add(file);
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

    // MARK: - Sign-in

    /// <summary>
    /// Digest login across both regions. pCloud's /userinfo still checks the password but
    /// refuses to issue an <c>auth</c> token unless the caller is an OAuth client — and pCloud
    /// has suspended new OAuth app registrations. When it says "yes, that's you" without a
    /// token, the user is told to paste their <c>pcauth</c> cookie instead.
    /// </summary>
    public static async Task<PCloudCredentials> LoginWithPasswordAsync(HttpClient http, string email, string password, CancellationToken cancellationToken)
    {
        // pCloud's 2000 "Log in failed" on both hosts leaves this standing: wrong email or password.
        var lastError = CloudProviderException.InvalidCredentials();
        var tokenWithheld = false;
        foreach (var hostname in Hostnames)
        {
            string digest;
            try
            {
                digest = await FetchDigestAsync(http, hostname, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException e)
            {
                lastError = e;
                continue;
            }

            var parameters = new (string, string)[]
            {
                ("getauth", "1"),
                ("authexpire", "31536000"),
                ("username", email),
                ("digest", digest),
                ("passworddigest", PasswordDigest(email, password, digest)),
            };

            try
            {
                if (await SubmitLoginAsync(http, hostname, parameters, cancellationToken).ConfigureAwait(false) is { } credentials)
                {
                    return credentials;
                }

                tokenWithheld = true;
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.Unauthorized)
            {
                // Wrong region (or wrong password) — try the other host.
            }
            catch (CloudProviderException e) when (e.Kind == CloudErrorKind.InvalidCredentials)
            {
                throw;
            }
            catch (CloudProviderException e)
            {
                lastError = e;
            }
        }

        if (tokenWithheld)
        {
            throw TokenWithheld();
        }

        throw lastError;
    }

    /// <summary>The password was right, but pCloud would not hand a token to a third-party app.</summary>
    internal static CloudProviderException TokenWithheld()
        => CloudProviderException.CommandFailed(Localization.L("pCloud now requires OAuth for third-party apps, and new app registrations are disabled. Sign in at my.pcloud.com, copy the 'pcauth' cookie value, and paste it into the Access Token field."));

    /// <summary>The account behind a pasted token, trying each region's host in turn.</summary>
    public static async Task<PCloudCredentials> ValidateTokenAsync(HttpClient http, string token, CancellationToken cancellationToken)
    {
        foreach (var hostname in Hostnames)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(hostname, "userinfo", [("auth", token)]));
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            HttpResponseMessage response;
            try
            {
                response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudProviderException)
            {
                continue;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                try
                {
                    var info = await response.ReadJsonAsync<PCloudUserInfoResponse>(cancellationToken).ConfigureAwait(false);
                    if (info.Result == 0 && info.UserId is { } userId)
                    {
                        return new PCloudCredentials(token, hostname, userId);
                    }
                }
                catch (CloudProviderException)
                {
                    // Undecodable — try the other host.
                }
            }
        }

        throw CloudProviderException.InvalidCredentials();
    }

    /// <returns>The credentials, or null when pCloud authenticated the user but withheld the token.</returns>
    private static async Task<PCloudCredentials?> SubmitLoginAsync(HttpClient http, string hostname, (string, string)[] parameters, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(hostname, "userinfo", parameters));
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CloudProviderException.ServerError(0);
        }

        var login = await response.ReadJsonAsync<PCloudLoginResponse>(cancellationToken).ConfigureAwait(false);
        return login.Result switch
        {
            0 when !string.IsNullOrEmpty(login.Auth) && login.UserId is { } userId => new PCloudCredentials(login.Auth, hostname, userId),
            // Authenticated, but the auth token was silently dropped — the OAuth-only rule.
            0 => null,
            2000 or 2012 => throw CloudProviderException.Unauthorized(),
            2205 or 2297 => throw CloudProviderException.InvalidCredentials(),
            _ => throw CloudProviderException.ServerError(login.Result),
        };
    }

    private static async Task<string> FetchDigestAsync(HttpClient http, string hostname, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{hostname}/getdigest");
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        using var response = await http.SendApiAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CloudProviderException.ServerError(0);
        }

        var digest = await response.ReadJsonAsync<PCloudDigestResponse>(cancellationToken).ConfigureAwait(false);
        return digest.Result == 0 && !string.IsNullOrEmpty(digest.Digest) ? digest.Digest : throw CloudProviderException.InvalidResponse();
    }

    /// <summary>sha1(password + sha1hex(lowercased email) + digest), hex — pCloud's documented digest login.</summary>
    internal static string PasswordDigest(string email, string password, string digest)
    {
        var emailHash = Sha1Hex(Encoding.UTF8.GetBytes(email.ToLowerInvariant()));
        return Sha1Hex(Encoding.UTF8.GetBytes(password + emailHash + digest));
    }

#pragma warning disable CA5350 // pCloud's login protocol is defined in terms of SHA-1.
    private static string Sha1Hex(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));
#pragma warning restore CA5350

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

    /// <summary>"https://{account host}/{method}?auth=…&amp;…".</summary>
    private Uri ApiUri(string method, IEnumerable<(string Key, string Value)> parameters)
        => BuildUri(Credentials.Hostname, method, new[] { ("auth", Credentials.AccessToken) }.Concat(parameters));

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
