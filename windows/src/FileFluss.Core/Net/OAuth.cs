// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using FileFluss.Core.Diagnostics;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Net;

/// <summary>Opens URLs in the user's browser. The app may replace <see cref="Handler"/>; tests do.</summary>
public static class BrowserLauncher
{
    public static Action<Uri> Handler { get; set; } = uri =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

    public static void Open(Uri uri) => Handler(uri);

    public static void Open(string url) => Handler(new Uri(url));
}

/// <summary>What the browser was redirected back with, and the exact redirect URI used.</summary>
/// <param name="RedirectUri">Must be passed unchanged to the token exchange — servers cross-check it.</param>
/// <param name="CallbackUri">The full callback URL, including <c>?code=…&amp;state=…</c>.</param>
public sealed record OAuthCallback(string RedirectUri, Uri CallbackUri)
{
    public string? Get(string name) => HttpUtility.ParseQueryString(CallbackUri.Query)[name];

    /// <summary>
    /// The authorization code, after checking <paramref name="expectedState"/> and the error
    /// parameter. A state mismatch means the callback was not for this sign-in.
    /// </summary>
    public string RequireCode(string? expectedState)
    {
        if (expectedState is not null && Get("state") != expectedState)
        {
            SupportLogger.Shared.Log("OAuth state mismatch — rejecting callback", "OAuth", LogLevel.Error);
            throw CloudProviderException.Unauthorized();
        }

        if (Get("error") is { } error)
        {
            SupportLogger.Shared.Log($"OAuth error: {error}", "OAuth", LogLevel.Error);
            throw CloudProviderException.Unauthorized();
        }

        return Get("code") ?? throw CloudProviderException.InvalidResponse();
    }
}

/// <summary>PKCE (RFC 7636) and state values.</summary>
public static class Pkce
{
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    public static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The Windows <c>LoopbackOAuthAuthenticator</c>: listens on a random loopback port, opens the
/// browser at the provider's authorize URL with <c>http://localhost:&lt;port&gt;</c> as the
/// redirect, and waits (up to five minutes) for the browser to come back with a code.
///
/// <para>Listens on both 127.0.0.1 and ::1 with the same port, because browsers resolve
/// "localhost" to either. Requests without <c>code</c> or <c>error</c> (favicon, probes) get a
/// 404 and the wait continues.</para>
/// </summary>
public static class OAuthLoopback
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private const string SuccessPage = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>FileFluss</title></head><body style=\"font-family:Segoe UI,sans-serif;text-align:center;margin-top:15%\"><h2>Signed in to FileFluss</h2><p>You can close this window and return to FileFluss.</p></body></html>";

    private const string FailurePage = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>FileFluss</title></head><body style=\"font-family:Segoe UI,sans-serif;text-align:center;margin-top:15%\"><h2>Authentication failed</h2><p>{0}</p><p>You can close this window.</p></body></html>";

    /// <param name="authorizeUrl">Builds the authorize URL from the redirect URI.</param>
    /// <param name="redirectPath">Path appended to the loopback origin; macOS uses none.</param>
    public static async Task<OAuthCallback> AuthenticateAsync(
        Func<string, Uri> authorizeUrl,
        CancellationToken cancellationToken,
        string redirectPath = "")
    {
        var v4 = new TcpListener(IPAddress.Loopback, 0);
        v4.Start();
        var port = ((IPEndPoint)v4.LocalEndpoint).Port;
        TcpListener? v6 = null;
        try
        {
            v6 = new TcpListener(IPAddress.IPv6Loopback, port);
            v6.Start();
        }
        catch (SocketException)
        {
            v6 = null;
        }

        var redirectUri = $"http://localhost:{port}{redirectPath}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            BrowserLauncher.Open(authorizeUrl(redirectUri));
            var listeners = v6 is null ? new[] { v4 } : new[] { v4, v6 };
            var waits = listeners.Select(l => AcceptCallbackAsync(l, timeout.Token)).ToList();
            while (waits.Count > 0)
            {
                var finished = await Task.WhenAny(waits).ConfigureAwait(false);
                waits.Remove(finished);
                if (finished.IsCompletedSuccessfully)
                {
                    return new OAuthCallback(redirectUri, new Uri($"http://localhost:{port}{finished.Result}"));
                }

                if (finished.Exception?.InnerException is CloudProviderException providerError)
                {
                    throw providerError;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw CloudProviderException.NotAuthenticated();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Five minutes passed without the browser coming back.
            throw CloudProviderException.NotAuthenticated();
        }
        finally
        {
            timeout.Cancel();
            v4.Stop();
            v6?.Stop();
        }
    }

    /// <summary>Accepts connections until one carries a code or an error; returns its path and query.</summary>
    private static async Task<string> AcceptCallbackAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            var target = await ReadRequestTargetAsync(stream, cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                continue;
            }

            var query = HttpUtility.ParseQueryString(new Uri("http://localhost" + target).Query);
            if (query["error"] is { } error)
            {
                await RespondAsync(stream, 200, string.Format(FailurePage, WebUtility.HtmlEncode(error)), cancellationToken).ConfigureAwait(false);
                SupportLogger.Shared.Log($"OAuth error: {error}", "OAuth", LogLevel.Error);
                throw CloudProviderException.Unauthorized();
            }

            if (query["code"] is null)
            {
                // Favicon, preflight or a DNS probe — keep listening.
                await RespondAsync(stream, 404, string.Empty, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await RespondAsync(stream, 200, SuccessPage, cancellationToken).ConfigureAwait(false);
            return target;
        }
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        var requestLine = Encoding.ASCII.GetString(buffer, 0, total).Split("\r\n")[0];
        var parts = requestLine.Split(' ');
        return parts.Length >= 2 && parts[1].StartsWith('/') ? parts[1] : null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string html, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status == 200 ? "OK" : "Not Found";
        var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The browser hung up first; the callback itself already arrived.
        }
    }
}
