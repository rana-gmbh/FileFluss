// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using FileFluss.Core.Net;

namespace FileFluss.Core.Providers.WebDav;

/// <summary>
/// HTTP plumbing the WebDAV family shares (and Koofr/kDrive reuse for error mapping and
/// progress): status mapping, redirects that keep their credentials, and progress that
/// survives a re-sent body.
/// </summary>
internal static class WebDavHttp
{
    public static readonly HttpMethod Propfind = new("PROPFIND");
    public static readonly HttpMethod Proppatch = new("PROPPATCH");
    public static readonly HttpMethod Mkcol = new("MKCOL");
    public static readonly HttpMethod Move = new("MOVE");
    public static readonly HttpMethod Copy = new("COPY");
    public static readonly HttpMethod Search = new("SEARCH");

    /// <summary>
    /// Bound for listings, searches and server-side MOVE/COPY/DELETE of whole trees — the
    /// macOS clients' 300 s resource timeout. A dead host still fails after the 30 s
    /// connect timeout.
    /// </summary>
    public static readonly TimeSpan LongOperationTimeout = TimeSpan.FromMinutes(5);

    private const int MaxRedirects = 5;

    /// <summary>
    /// The macOS <c>mapHTTPError</c> of the WebDAV, NextCloud, Koofr and kDrive clients.
    /// 401 is <c>invalidCredentials</c>/<c>notAuthenticated</c> there, which the macOS panel
    /// turns into "sign in again" just like <c>unauthorized</c>; on Windows that meaning is
    /// carried by <see cref="CloudProviderException.Unauthorized"/>. 403 stays
    /// <c>unauthorized</c>, as on macOS.
    /// </summary>
    public static CloudProviderException MapStatus(int status, string? path = null) => status switch
    {
        401 or 403 => CloudProviderException.Unauthorized(),
        404 => CloudProviderException.NotFound(path ?? string.Empty),
        429 => CloudProviderException.RateLimited(),
        507 => CloudProviderException.QuotaExceeded(),
        _ => CloudProviderException.ServerError(status),
    };

    public static bool IsSuccess(HttpResponseMessage response) => (int)response.StatusCode is >= 200 and <= 299;

    /// <summary>
    /// Sends <paramref name="build"/>(uri), following redirects by hand. WebDAV servers
    /// redirect a lot (<c>/path</c> → <c>/path/</c>, http → https) and .NET, like URLSession,
    /// drops the Authorization header on every redirect — the request then fails with 401.
    /// As on macOS the header is carried over when the redirect stays on the host the
    /// request was made to (and never from https down to http). The method and body are
    /// kept; <paramref name="build"/> recreates the body for each hop.
    /// </summary>
    /// <param name="transfer">Stream the response (downloads, uploads) without an overall timeout.</param>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        Uri uri,
        Func<Uri, HttpRequestMessage> build,
        CancellationToken cancellationToken,
        bool transfer = false,
        TimeSpan? timeout = null,
        bool followRedirects = true)
    {
        var current = uri;
        for (var hop = 0; ; hop++)
        {
            var request = build(current);
            if (hop > 0 && !KeepsCredentials(uri, current))
            {
                request.Headers.Authorization = null;
            }

            HttpResponseMessage response;
            try
            {
                response = transfer
                    ? await http.SendTransferAsync(request, cancellationToken).ConfigureAwait(false)
                    : await http.SendApiAsync(request, cancellationToken, timeout).ConfigureAwait(false);
            }
            finally
            {
                // HttpClient no longer disposes request content; an upload body is a file
                // stream that would otherwise stay open (and the file locked) until GC.
                request.Dispose();
            }

            if (!followRedirects || hop >= MaxRedirects || !IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                return response;
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (!IsFollowable(current, next))
            {
                // Like HttpClient's own redirect handling: never from https down to plain
                // http (the body — an upload — would cross the network in the clear), and
                // never to anything but http(s). The redirect answer is the result.
                return response;
            }

            response.Dispose();
            current = next;
        }
    }

    /// <summary>An http(s) target, and no step down from https to http.</summary>
    internal static bool IsFollowable(Uri from, Uri to)
        => (to.Scheme == Uri.UriSchemeHttps || to.Scheme == Uri.UriSchemeHttp)
           && !(from.Scheme == Uri.UriSchemeHttps && to.Scheme == Uri.UriSchemeHttp);

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool KeepsCredentials(Uri original, Uri target)
        => string.Equals(original.Host, target.Host, StringComparison.OrdinalIgnoreCase)
           && !(original.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp);

    /// <summary>The file's mtime as Unix seconds, or null when it can't be read (the header is then left out).</summary>
    public static long? UnixMtime(string localPath) => LocalModificationTime(localPath)?.ToUnixTimeSeconds();

    /// <summary>The local file's modification time (UTC), or null when it can't be read.</summary>
    public static DateTimeOffset? LocalModificationTime(string localPath) => FileTime(localPath, File.GetLastWriteTimeUtc);

    /// <summary>The local file's creation time (UTC), or null when it can't be read.</summary>
    public static DateTimeOffset? LocalCreationTime(string localPath) => FileTime(localPath, File.GetCreationTimeUtc);

    private static DateTimeOffset? FileTime(string localPath, Func<string, DateTime> read)
    {
        try
        {
            return File.Exists(localPath) ? new DateTimeOffset(read(localPath), TimeSpan.Zero) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Progress for a body that may be sent more than once (a redirect hop, a retried upload):
/// only bytes beyond the furthest point any attempt reached are reported, so the transfer
/// total never counts a re-sent prefix twice and never runs backwards.
/// </summary>
internal sealed class HighWaterProgress(ByteProgress? target)
{
    private long _furthest;
    private long _attempt;

    /// <summary>The callback for the next attempt (null when nobody listens).</summary>
    public ByteProgress? NextAttempt()
    {
        if (target is null)
        {
            return null;
        }

        Interlocked.Exchange(ref _attempt, 0);
        return delta =>
        {
            var reached = Interlocked.Add(ref _attempt, delta);
            var furthest = Interlocked.Read(ref _furthest);
            if (reached > furthest && Interlocked.CompareExchange(ref _furthest, reached, furthest) == furthest)
            {
                target(reached - furthest);
            }
        };
    }
}
