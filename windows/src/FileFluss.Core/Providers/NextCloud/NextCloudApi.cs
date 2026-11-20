// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers.WebDav;

namespace FileFluss.Core.Providers.NextCloud;

/// <summary>The macOS <c>NextCloudCredentials</c> (same JSON keys) plus the server's user id.</summary>
/// <param name="UserId">
/// The account's user id as the OCS user endpoint reports it. The DAV files root is
/// <c>/remote.php/dav/files/&lt;user id&gt;</c>, and the login name can differ from it (LDAP,
/// sign-in by email address). Null for credentials that predate it: the login name is used,
/// as on macOS.
/// </param>
internal sealed record NextCloudCredentials(
    [property: JsonPropertyName("serverURL")] string ServerUrl,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("appPassword")] string AppPassword,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("userId")] string? UserId = null)
{
    public string DavBaseUrl => $"{ServerUrl}/remote.php/dav/files/{CloudHttp.Escape(string.IsNullOrEmpty(UserId) ? Username : UserId)}";
}

/// <summary>
/// The Nextcloud-only half of the macOS <c>NextCloudAPIClient</c>: sign-in (app password and
/// Login Flow v2), the OCS user endpoint (display name, quota) and OCS link shares. Files go
/// through <see cref="WebDavClient"/>.
/// </summary>
internal sealed class NextCloudApi
{
    internal const string LogCategory = "NextCloud";

    /// <summary>The PROPFIND body with Nextcloud's folder size and checksums.</summary>
    internal static readonly WebDavDialect Dialect = new(
        LogCategory,
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <d:propfind xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns" xmlns:nc="http://nextcloud.org/ns">
            <d:prop>
                <d:getlastmodified/>
                <d:getcontentlength/>
                <d:getcontenttype/>
                <d:resourcetype/>
                <d:displayname/>
                <oc:checksums/>
                <oc:size/>
            </d:prop>
        </d:propfind>
        """,
        MkcolTriesBothForms: false);

    /// <summary>NC 26+ share attribute that hides the download button.</summary>
    private const string DownloadOffAttribute = """[{"scope":"permissions","key":"download","value":false}]""";
    private const string DownloadOnAttribute = """[{"scope":"permissions","key":"download","value":true}]""";

    private readonly HttpClient _http;
    private readonly NextCloudCredentials _credentials;
    private readonly AuthenticationHeaderValue _auth;

    public NextCloudApi(HttpClient http, NextCloudCredentials credentials)
    {
        _http = http;
        _credentials = credentials;
        _auth = CloudHttp.Basic(credentials.Username, credentials.AppPassword);
        Dav = new WebDavClient(http, credentials.DavBaseUrl, credentials.Username, credentials.AppPassword, Dialect);
    }

    public NextCloudCredentials Credentials => _credentials;

    public WebDavClient Dav { get; }

    // MARK: - Sign-in

    /// <summary>Trims, drops a trailing slash, assumes https:// when no scheme was typed.</summary>
    public static string NormalizeServerUrl(string raw) => WebDavClient.NormalizeServerUrl(raw);

    /// <summary>App-password sign-in: checks the credentials against the OCS user endpoint.</summary>
    public static async Task<NextCloudCredentials> AuthenticateAsync(
        HttpClient http, string serverUrl, string username, string appPassword, CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        using var response = await SendUserRequestAsync(http, baseUrl, username, appPassword, "application/json", cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status == 401)
        {
            throw CloudProviderException.InvalidCredentials();
        }

        if (!WebDavHttp.IsSuccess(response))
        {
            SupportLogger.Shared.Log($"User info failed: HTTP {status}", LogCategory, LogLevel.Error);
            throw CloudProviderException.ServerError(status);
        }

        var user = ParseUser(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var displayName = user.DisplayName ?? username;
        SupportLogger.Shared.Log($"Authenticated as {displayName}", LogCategory);
        return new NextCloudCredentials(baseUrl, username, appPassword, displayName, user.Id);
    }

    /// <summary>
    /// Nextcloud's Login Flow v2: the server mints an app password while the user signs in
    /// in the browser with their normal password (2FA handled there). POST
    /// <c>/index.php/login/v2</c>, open the returned login URL, then poll the endpoint every
    /// <paramref name="pollInterval"/> (macOS: 3 s — snappier than the 5 s the docs suggest
    /// without hammering) until the user finishes or five minutes pass. We never see the
    /// user's real password.
    /// </summary>
    public static async Task<NextCloudCredentials> LoginFlowV2Async(
        HttpClient http,
        string serverUrl,
        Action<Uri> openBrowser,
        IConnectPrompt prompt,
        TimeSpan pollInterval,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeServerUrl(serverUrl);
        if (!Uri.TryCreate($"{normalized}/index.php/login/v2", UriKind.Absolute, out var initUrl))
        {
            throw CloudProviderException.InvalidResponse();
        }

        LoginFlowInit flow;
        using (var response = await WebDavHttp.SendAsync(http, initUrl, u => LoginFlowRequest(u, null), cancellationToken).ConfigureAwait(false))
        {
            if (!WebDavHttp.IsSuccess(response))
            {
                var status = (int)response.StatusCode;
                SupportLogger.Shared.Log($"Login flow init failed: HTTP {status}", LogCategory, LogLevel.Error);
                if (status == 404)
                {
                    throw CloudProviderException.CommandFailed(
                        Localization.L("This Nextcloud server doesn't expose Login Flow v2. Use the App Password sign-in option instead."));
                }

                throw WebDavHttp.MapStatus(status);
            }

            flow = await response.ReadJsonAsync<LoginFlowInit>(cancellationToken).ConfigureAwait(false);
        }

        if (flow.Poll is null
            || !Uri.TryCreate(flow.Login, UriKind.Absolute, out var loginUrl)
            || !Uri.TryCreate(flow.Poll.Endpoint, UriKind.Absolute, out var pollUrl))
        {
            throw CloudProviderException.InvalidResponse();
        }

        openBrowser(loginUrl);
        prompt.ShowStatus(Localization.L("Waiting for sign-in in browser…"));
        try
        {
            var giveUpAt = DateTimeOffset.UtcNow + deadline;
            while (DateTimeOffset.UtcNow < giveUpAt)
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);

                HttpResponseMessage pollResponse;
                try
                {
                    var token = flow.Poll.Token ?? string.Empty;
                    pollResponse = await WebDavHttp.SendAsync(
                        http,
                        pollUrl,
                        u => LoginFlowRequest(u, CloudHttp.FormContent(("token", token))),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (CloudProviderException e) when (e.Kind == CloudErrorKind.NetworkError)
                {
                    // A blip while the user is still typing in the browser shouldn't throw
                    // away the whole sign-in; the next poll tries again.
                    SupportLogger.Shared.Log($"Login flow poll: {e.Message}", LogCategory);
                    continue;
                }

                using (pollResponse)
                {
                    var status = (int)pollResponse.StatusCode;
                    if (status == 404)
                    {
                        // Still pending: the user hasn't finished in the browser.
                        continue;
                    }

                    if (status != 200)
                    {
                        SupportLogger.Shared.Log($"Login Flow v2 poll failed: HTTP {status}", LogCategory, LogLevel.Error);
                        throw WebDavHttp.MapStatus(status);
                    }

                    var result = await pollResponse.ReadJsonAsync<LoginFlowResult>(cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(result.Server) || string.IsNullOrEmpty(result.LoginName) || string.IsNullOrEmpty(result.AppPassword))
                    {
                        throw CloudProviderException.InvalidResponse();
                    }

                    var server = NormalizeServerUrl(result.Server);
                    var user = await TryFetchUserAsync(http, server, result.LoginName, result.AppPassword, cancellationToken).ConfigureAwait(false);
                    var displayName = user?.DisplayName ?? result.LoginName;
                    SupportLogger.Shared.Log($"Login Flow v2 succeeded for {displayName}", LogCategory);
                    return new NextCloudCredentials(server, result.LoginName, result.AppPassword, displayName, user?.Id);
                }
            }

            throw CloudProviderException.CommandFailed(Localization.L("Timed out waiting for browser sign-in."));
        }
        finally
        {
            prompt.ShowStatus(null);
        }
    }

    private static HttpRequestMessage LoginFlowRequest(Uri url, HttpContent? content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

        // The User-Agent is what Nextcloud names the app password after in the user's
        // "Devices & sessions" list.
        request.Headers.TryAddWithoutValidation("User-Agent", "FileFluss");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<OcsUser?> TryFetchUserAsync(HttpClient http, string baseUrl, string username, string appPassword, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendUserRequestAsync(http, baseUrl, username, appPassword, "application/json", cancellationToken).ConfigureAwait(false);
            return WebDavHttp.IsSuccess(response)
                ? ParseUser(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
                : null;
        }
        catch (CloudProviderException)
        {
            return null;
        }
    }

    private static async Task<HttpResponseMessage> SendUserRequestAsync(
        HttpClient http, string baseUrl, string username, string appPassword, string accept, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate($"{baseUrl}/ocs/v1.php/cloud/user", UriKind.Absolute, out var url))
        {
            throw CloudProviderException.InvalidResponse();
        }

        var auth = CloudHttp.Basic(username, appPassword);
        return await WebDavHttp.SendAsync(
            http,
            url,
            u =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, u) { Headers = { Authorization = auth } };
                request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
                return request;
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal sealed record OcsUser(string? Id, string? DisplayName);

    /// <summary>
    /// Id and display name from <c>/ocs/v1.php/cloud/user</c>, as JSON or XML (servers differ
    /// in honouring Accept). The endpoint calls the name <c>display-name</c>; older ones
    /// <c>displayname</c>. macOS only looked for the latter in XML and so mostly fell back to
    /// the login name.
    /// </summary>
    internal static OcsUser ParseUser(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.TryGetProperty("ocs", out var ocs) && ocs.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    return new OcsUser(
                        NonEmpty(StringOf(data, "id")),
                        NonEmpty(StringOf(data, "display-name")) ?? NonEmpty(StringOf(data, "displayname")));
                }
            }
            catch (JsonException)
            {
            }

            return new OcsUser(null, null);
        }

        string? id = null;
        string? displayName = null;
        ReadXml(body, element =>
        {
            switch (element)
            {
                case { Name: "id", Parent: "data" }:
                    id ??= NonEmpty(element.Value);
                    break;
                case { Name: "display-name" or "displayname" }:
                    displayName ??= NonEmpty(element.Value);
                    break;
            }
        });
        return new OcsUser(id, displayName);
    }

    // MARK: - Quota

    /// <summary>
    /// The OCS user endpoint carries a <c>&lt;quota&gt;</c> block with <c>used</c> and
    /// <c>total</c> (the configured cap: -3 "default", &gt; 0 bytes). When total ≤ 0 (admin
    /// unlimited) only the used figure is reported. Null when the request is refused.
    /// </summary>
    public async Task<CloudStorageQuota?> StorageQuotaAsync(CancellationToken cancellationToken)
    {
        using var response = await SendUserRequestAsync(_http, _credentials.ServerUrl, _credentials.Username, _credentials.AppPassword, "application/xml", cancellationToken).ConfigureAwait(false);
        return WebDavHttp.IsSuccess(response)
            ? ParseQuota(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
            : null;
    }

    /// <summary>
    /// <c>used</c>/<c>total</c> from the outer <c>&lt;quota&gt;</c> element only, so a stray
    /// <c>total</c> elsewhere in the document can't confuse it. Accepts the JSON shape too.
    /// </summary>
    internal static CloudStorageQuota? ParseQuota(string body)
    {
        long? used = null;
        long? total = null;
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.TryGetProperty("ocs", out var ocs)
                    && ocs.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("quota", out var quota)
                    && quota.ValueKind == JsonValueKind.Object)
                {
                    used = LongOf(quota, "used");
                    total = LongOf(quota, "total");
                }
            }
            catch (JsonException)
            {
            }
        }
        else
        {
            ReadXml(body, element =>
            {
                switch (element)
                {
                    case { Name: "used", Parent: "quota" }:
                        used ??= ParseLong(element.Value);
                        break;
                    case { Name: "total", Parent: "quota" }:
                        total ??= ParseLong(element.Value);
                        break;
                }
            });
        }

        return used is { } usedBytes
            ? new CloudStorageQuota(usedBytes, total is > 0 ? total : null)
            : null;
    }

    // MARK: - Share links

    /// <summary>
    /// Creates a public link share (<c>shareType=3</c>). Admins can disable link shares or
    /// enforce a password/expiry, in which case the server refuses a request without them;
    /// the OCS message is surfaced verbatim so the user sees the server's own reason.
    /// </summary>
    public async Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        var form = new List<(string, string)> { ("path", path), ("shareType", "3") };
        if (!string.IsNullOrEmpty(options.Password))
        {
            form.Add(("password", options.Password));
        }

        if (options.Expiry is { } expiry)
        {
            form.Add(("expireDate", OcsExpireDate(expiry)));
        }

        if (!options.AllowDownload)
        {
            // Share attribute added in NC 26: hides the download button.
            form.Add(("attributes", DownloadOffAttribute));
        }

        using var response = await SendOcsAsync(HttpMethod.Post, SharesUrl("?format=json"), form, cancellationToken).ConfigureAwait(false);
        var envelope = await ReadShareEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);

        // OCS reports failures inside the body with HTTP 200 as often as not, so the OCS
        // status code decides, not the HTTP one.
        if (envelope is not { IsSuccess: true })
        {
            throw OcsFailure(envelope, response, "share create");
        }

        var link = envelope.Shares.FirstOrDefault()?.Url;
        if (link is null || !Uri.TryCreate(link, UriKind.Absolute, out var shareUrl))
        {
            throw CloudProviderException.InvalidResponse();
        }

        return new CloudShareLink(shareUrl)
        {
            // `/s/<token>/download` streams the file itself.
            DirectDownloadUrl = options.AllowDownload && Uri.TryCreate(link + "/download", UriKind.Absolute, out var direct) ? direct : null,
            ExpiresAt = ParseShareExpiry(envelope.Shares.FirstOrDefault()?.Expiration),
            HasPassword = !string.IsNullOrEmpty(options.Password),
        };
    }

    /// <summary>The public link this path already has, or null.</summary>
    public async Task<CloudShareLink?> ExistingShareLinkAsync(string path, CancellationToken cancellationToken)
        => await PublicLinkShareAsync(path, cancellationToken).ConfigureAwait(false) is { } share ? ShareLinkFrom(share) : null;

    /// <summary>
    /// Changes password, expiry and download permission in place; Nextcloud keeps the token,
    /// so the URL stays.
    /// </summary>
    public async Task<CloudShareLink> UpdateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken)
    {
        var share = await PublicLinkShareAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw CloudProviderException.CommandFailed(Localization.L("This file has no public link to update. Create one first."));

        // An *empty* value is how OCS clears a field, so both are always sent: leaving
        // `password` out would keep the existing password — the opposite of "remove the
        // password". Same for expiry.
        var form = new List<(string, string)>
        {
            ("password", options.Password ?? string.Empty),
            ("expireDate", options.Expiry is { } expiry ? OcsExpireDate(expiry) : string.Empty),

            // The share's own bitmask, not a constant: updating a folder link's password must
            // not strip its upload permission.
            ("permissions", (share.Permissions ?? 1).ToString(CultureInfo.InvariantCulture)),
        };
        if (!options.AllowDownload)
        {
            form.Add(("attributes", DownloadOffAttribute));
        }
        else if (DownloadDisabled(share.Attributes))
        {
            // Re-enabling only needs saying when the link has the attribute — and its
            // presence proves the server understands it (NC 26+), so nothing older gets an
            // unknown field.
            form.Add(("attributes", DownloadOnAttribute));
        }

        using var response = await SendOcsAsync(HttpMethod.Put, SharesUrl($"/{CloudHttp.Escape(share.Id)}?format=json"), form, cancellationToken).ConfigureAwait(false);
        var envelope = await ReadShareEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
        if (envelope is not { IsSuccess: true })
        {
            throw OcsFailure(envelope, response, "share update");
        }

        if (envelope.Shares.FirstOrDefault() is { Url: not null } updated)
        {
            return ShareLinkFrom(updated);
        }

        // A few releases answer PUT with an empty `data`. Re-read the share rather than
        // guessing what the server applied.
        var fresh = await PublicLinkShareAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw CloudProviderException.InvalidResponse();
        return ShareLinkFrom(fresh);
    }

    /// <summary>Withdraws the public link. Not an error when there is none: "not shared" already holds.</summary>
    public async Task RemoveShareLinkAsync(string path, CancellationToken cancellationToken)
    {
        if (await PublicLinkShareAsync(path, cancellationToken).ConfigureAwait(false) is not { } share)
        {
            SupportLogger.Shared.Log("removeShareLink: no public link on that path, nothing to withdraw", LogCategory);
            return;
        }

        using var response = await SendOcsAsync(HttpMethod.Delete, SharesUrl($"/{CloudHttp.Escape(share.Id)}?format=json"), null, cancellationToken).ConfigureAwait(false);
        var envelope = await ReadShareEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
        if (envelope is not { IsSuccess: true })
        {
            throw OcsFailure(envelope, response, "share delete");
        }
    }

    /// <summary>
    /// The <c>share_type == 3</c> (public link) share for the path, or null. A path can carry
    /// user, group and link shares; only the link one is ours. An unshared file is a success
    /// with an empty list; a 404 means the file itself is unknown, which stays an error.
    /// </summary>
    private async Task<OcsShare?> PublicLinkShareAsync(string path, CancellationToken cancellationToken)
    {
        var query = $"?path={CloudHttp.Escape(path)}&reshares=false&format=json";
        using var response = await SendOcsAsync(HttpMethod.Get, SharesUrl(query), null, cancellationToken).ConfigureAwait(false);
        var envelope = await ReadShareEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
        if (envelope is not { IsSuccess: true })
        {
            throw OcsFailure(envelope, response, "share lookup", path);
        }

        return envelope.Shares.FirstOrDefault(s => s.ShareType == 3);
    }

    private static CloudShareLink ShareLinkFrom(OcsShare share)
    {
        if (share.Url is not { } link || !Uri.TryCreate(link, UriKind.Absolute, out var url))
        {
            throw CloudProviderException.InvalidResponse();
        }

        // `/s/<token>/download` is pointless on a link whose download button is switched off.
        var allowsDownload = !DownloadDisabled(share.Attributes);
        return new CloudShareLink(url)
        {
            DirectDownloadUrl = allowsDownload && Uri.TryCreate(link + "/download", UriKind.Absolute, out var direct) ? direct : null,
            ExpiresAt = ParseShareExpiry(share.Expiration),
            HasPassword = !string.IsNullOrEmpty(share.Password),
        };
    }

    /// <summary>
    /// True when the share carries the NC 26+ attribute that hides the download button. The
    /// attribute list is a JSON string inside the JSON body, so it's matched textually.
    /// </summary>
    internal static bool DownloadDisabled(string? attributes)
    {
        if (string.IsNullOrEmpty(attributes))
        {
            return false;
        }

        var compact = attributes.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.Contains("\"key\":\"download\"", StringComparison.Ordinal) && compact.Contains("\"value\":false", StringComparison.Ordinal);
    }

    /// <summary>Nextcloud takes a plain <c>yyyy-MM-dd</c> for <c>expireDate</c>, in UTC.</summary>
    private static string OcsExpireDate(DateTimeOffset date)
        => date.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"" or null for no expiry, "yyyy-MM-dd HH:mm:ss" (server-local, read as local as on macOS) otherwise.</summary>
    internal static DateTimeOffset? ParseShareExpiry(string? raw)
        => !string.IsNullOrEmpty(raw)
           && DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? new DateTimeOffset(parsed)
            : null;

    // MARK: - OCS plumbing

    private string SharesUrl(string suffix) => $"{_credentials.ServerUrl}/ocs/v2.php/apps/files_sharing/api/v1/shares{suffix}";

    /// <summary>RFC 3986 form encoding (spaces as %20), as the macOS client builds it.</summary>
    private static ByteArrayContent FormBody(IEnumerable<(string Key, string Value)> pairs)
    {
        var body = string.Join('&', pairs.Select(p => $"{CloudHttp.Escape(p.Key)}={CloudHttp.Escape(p.Value)}"));
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        return content;
    }

    private Task<HttpResponseMessage> SendOcsAsync(HttpMethod method, string url, IReadOnlyList<(string Key, string Value)>? form, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw CloudProviderException.InvalidResponse();
        }

        return WebDavHttp.SendAsync(
            _http,
            uri,
            u =>
            {
                var request = new HttpRequestMessage(method, u) { Headers = { Authorization = _auth } };

                // Mandatory for every OCS call — without it the server answers 401.
                request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (form is not null)
                {
                    request.Content = FormBody(form);
                }

                return request;
            },
            cancellationToken);
    }

    private static CloudProviderException OcsFailure(OcsEnvelope? envelope, HttpResponseMessage response, string operation, string? path = null)
    {
        SupportLogger.Shared.Log($"{operation} failed: HTTP {(int)response.StatusCode}, OCS {envelope?.StatusCode ?? -1}", LogCategory, LogLevel.Error);
        return !string.IsNullOrEmpty(envelope?.Message)
            ? CloudProviderException.CommandFailed(envelope.Message)
            : WebDavHttp.MapStatus((int)response.StatusCode, path);
    }

    internal sealed record OcsShare(string Id, int ShareType, string? Url, string? Expiration, string? Password, int? Permissions, string? Attributes);

    internal sealed record OcsEnvelope(int StatusCode, string? Message, IReadOnlyList<OcsShare> Shares)
    {
        public bool IsSuccess => StatusCode is 100 or 200;
    }

    private static async Task<OcsEnvelope?> ReadShareEnvelopeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => ParseShareEnvelope(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// <c>{"ocs":{"meta":…,"data":…}}</c>. <c>data</c> is an array for the share list and a
    /// bare object for one share (create/PUT), so both are flattened into
    /// <see cref="OcsEnvelope.Shares"/>. <c>id</c> arrives as a string on most releases and a
    /// number on some, <c>share_type</c> the other way round — both are read leniently, since
    /// a type mismatch would otherwise sink the whole lookup.
    /// </summary>
    internal static OcsEnvelope? ParseShareEnvelope(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("ocs", out var ocs) || !ocs.TryGetProperty("meta", out var meta))
            {
                return null;
            }

            var statusCode = (int?)LongOf(meta, "statuscode") ?? -1;
            var message = StringOf(meta, "message");
            var shares = new List<OcsShare>();
            if (ocs.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.Array)
                {
                    shares.AddRange(data.EnumerateArray().Select(ParseShare).OfType<OcsShare>());
                }
                else if (data.ValueKind == JsonValueKind.Object && ParseShare(data) is { } single)
                {
                    shares.Add(single);
                }
            }

            return new OcsEnvelope(statusCode, message, shares);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static OcsShare? ParseShare(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = StringOf(element, "id");
        return new OcsShare(
            id ?? string.Empty,
            (int?)LongOf(element, "share_type") ?? -1,
            StringOf(element, "url"),
            StringOf(element, "expiration"),
            StringOf(element, "password"),
            (int?)LongOf(element, "permissions"),
            StringOf(element, "attributes"));
    }

    /// <summary>A string, or a number rendered as one; null for null/absent/other kinds.</summary>
    private static string? StringOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    /// <summary>A number, or a numeric string; null otherwise.</summary>
    private static long? LongOf(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.Number when value.TryGetDouble(out var real) => (long)real,
            JsonValueKind.String => ParseLong(value.GetString()),
            _ => null,
        };
    }

    private static long? ParseLong(string? value)
        => long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string? NonEmpty(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private readonly record struct XmlElementText(string Name, string? Parent, string Value);

    /// <summary>
    /// Visits every element (by local name, with its parent's local name and its own text)
    /// in document order of their end tags; malformed XML ends the walk quietly.
    /// </summary>
    private static void ReadXml(string xml, Action<XmlElementText> visit)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        var stack = new Stack<(string Name, StringBuilder Text)>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element when reader.IsEmptyElement:
                        visit(new XmlElementText(reader.LocalName, stack.Count > 0 ? stack.Peek().Name : null, string.Empty));
                        break;
                    case XmlNodeType.Element:
                        stack.Push((reader.LocalName, new StringBuilder()));
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA when stack.Count > 0:
                        stack.Peek().Text.Append(reader.Value);
                        break;
                    case XmlNodeType.EndElement when stack.Count > 0:
                        var done = stack.Pop();
                        visit(new XmlElementText(done.Name, stack.Count > 0 ? stack.Peek().Name : null, done.Text.ToString()));
                        break;
                }
            }
        }
        catch (XmlException)
        {
        }
    }

    private sealed record LoginFlowInit(
        [property: JsonPropertyName("poll")] LoginFlowPoll? Poll,
        [property: JsonPropertyName("login")] string? Login);

    private sealed record LoginFlowPoll(
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("endpoint")] string? Endpoint);

    private sealed record LoginFlowResult(
        [property: JsonPropertyName("server")] string? Server,
        [property: JsonPropertyName("loginName")] string? LoginName,
        [property: JsonPropertyName("appPassword")] string? AppPassword);
}
