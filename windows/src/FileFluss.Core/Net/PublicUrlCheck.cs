// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text.RegularExpressions;

namespace FileFluss.Core.Net;

/// <summary>
/// Fetches a share link the way a stranger would — no cookies, no credentials — so the user
/// learns here, not from the recipient, when a link only works while signed in (macOS
/// <c>PublicURLCheck</c>). A ranged GET first, then a browser-like full GET, because some
/// providers refuse one or the other.
/// </summary>
public static partial class PublicUrlCheck
{
    public enum Outcome
    {
        Reachable,
        NotPublic,

        /// <summary>The check never reached the server — not evidence of anything.</summary>
        Unverified,
    }

    public sealed record Result(Outcome Outcome, string? Detail)
    {
        public bool Ok => Outcome == Outcome.Reachable;

        public bool CouldNotCheck => Outcome == Outcome.Unverified;
    }

    private static readonly string[] SignInHosts =
    [
        "login.live.com", "login.microsoftonline.com", "login.microsoft.com", "account.live.com",
        "accounts.google.com", "login.yahoo.com", "appleid.apple.com", "secure.login.gov",
    ];

    public static async Task<Result> IsReachableAsync(Uri url, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var first = await AttemptAsync(url, ranged: true, browserLike: false, timeout ?? TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (first.Result.Ok || first.IsSignInRedirect || first.Result.CouldNotCheck)
        {
            return first.Result;
        }

        var second = await AttemptAsync(url, ranged: false, browserLike: true, timeout ?? TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        return second.Result;
    }

    private sealed record Attempt(Result Result, bool IsSignInRedirect);

    private static async Task<Attempt> AttemptAsync(Uri url, bool ranged, bool browserLike, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler { UseCookies = false, Credentials = null, AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler) { Timeout = timeout };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (ranged)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 2047);
        }

        if (browserLike)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Edg/130.0");
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*");
        }

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var landed = response.RequestMessage?.RequestUri ?? url;
            var body = await ReadPrefixAsync(response, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new Attempt(new Result(Outcome.NotPublic, $"HTTP {(int)response.StatusCode} (final URL: {landed}) — {Summarize(body)}"), false);
            }

            if (body.Length == 0)
            {
                return new Attempt(new Result(Outcome.NotPublic, "empty response body"), false);
            }

            var host = landed.Host.ToLowerInvariant();
            if (SignInHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)))
            {
                return new Attempt(new Result(Outcome.NotPublic, $"the link redirects to a sign-in page at {host} — it is not public"), true);
            }

            return new Attempt(new Result(Outcome.Reachable, null), false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new Attempt(new Result(Outcome.Unverified, e.Message), false);
        }
    }

    private static async Task<byte[]> ReadPrefixAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[4096];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return buffer[..total];
    }

    /// <summary>Readable text from an error page, for the note shown to the user.</summary>
    public static string Summarize(byte[] data)
    {
        if (data.Length == 0)
        {
            return "empty body";
        }

        var raw = System.Text.Encoding.UTF8.GetString(data);
        var stripped = Whitespace().Replace(Tags().Replace(raw, " ").Replace("&amp;", "&"), " ").Trim();
        return stripped.Length == 0 ? "body had no readable text" : stripped[..Math.Min(300, stripped.Length)];
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
