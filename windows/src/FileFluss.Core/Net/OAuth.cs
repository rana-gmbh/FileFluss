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

/// <summary>
/// Opens URLs in the user's browser. The app may replace <see cref="Handler"/>; tests do.
/// Only http and https are ever handed to the shell: a <c>file:</c>, <c>ms-…:</c> or other
/// protocol URL — from a server's share-link or error response, say — would otherwise run
/// whatever the shell associates with it.
/// </summary>
public static class BrowserLauncher
{
    public static Action<Uri> Handler { get; set; } = uri =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

    public static bool IsWebUrl(Uri uri)
        => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public static void Open(Uri uri)
    {
        if (!IsWebUrl(uri))
        {
            SupportLogger.Shared.Log($"Refused to open a non-web URL ({(uri.IsAbsoluteUri ? uri.Scheme : "relative")})", "General", LogLevel.Error);
            return;
        }

        Handler(uri);
    }

    public static void Open(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Open(uri);
        }
    }
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
/// "localhost" to either. Connections are served concurrently, each with a short read
/// timeout: a browser's speculative connection that never carries a request must not hold
/// up the real callback.</para>
///
/// <para>Any local process — or a web page firing requests at localhost ports — can reach
/// the listener. So when the authorize URL carries a <c>state</c>, only a callback with that
/// exact state ends the wait (with its code or its error); anything else gets a 400 and the
/// wait goes on. Requests on other paths, without <c>code</c> or <c>error</c> (favicon,
/// probes), or other than GET get a 404 likewise.</para>
/// </summary>
public static class OAuthLoopback
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>How long one connection may take to send its request line.</summary>
    internal static TimeSpan RequestReadTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private const int MaxRequestHeadBytes = 16 * 1024;

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
        var callbackPath = redirectPath.Length == 0 ? "/" : redirectPath;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listeners = v6 is null ? new[] { v4 } : new[] { v4, v6 };
        var loops = new List<Task>();
        try
        {
            var authorize = authorizeUrl(redirectUri);
            var expectedState = HttpUtility.ParseQueryString(authorize.Query)["state"];
            loops.AddRange(listeners.Select(l => AcceptLoopAsync(l, callbackPath, expectedState, result, timeout.Token)));
            BrowserLauncher.Open(authorize);

            var target = await result.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            return new OAuthCallback(redirectUri, new Uri($"http://localhost:{port}{target}"));
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
            try
            {
                await Task.WhenAll(loops).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    private static async Task AcceptLoopAsync(
        TcpListener listener, string callbackPath, string? expectedState, TaskCompletionSource<string> result, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !result.Task.IsCompleted)
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            _ = Task.Run(() => ServeAsync(client, callbackPath, expectedState, result, cancellationToken), CancellationToken.None);
        }
    }

    /// <summary>One connection: a callback for this sign-in completes <paramref name="result"/>; anything else is answered and ignored.</summary>
    private static async Task ServeAsync(
        TcpClient client, string callbackPath, string? expectedState, TaskCompletionSource<string> result, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address))
                {
                    return;
                }

                await using var stream = client.GetStream();
                string? method;
                string? target;
                using (var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    read.CancelAfter(RequestReadTimeout);
                    (method, target) = await ReadRequestLineAsync(stream, read.Token).ConfigureAwait(false);
                }

                if (method != "GET" || target is null || result.Task.IsCompleted)
                {
                    await RespondAsync(stream, 404, string.Empty, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var uri = new Uri("http://localhost" + target);
                var query = HttpUtility.ParseQueryString(uri.Query);
                if (uri.AbsolutePath != callbackPath || (query["code"] is null && query["error"] is null))
                {
                    // Favicon, preflight or a DNS probe — keep listening.
                    await RespondAsync(stream, 404, string.Empty, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (expectedState is not null && query["state"] != expectedState)
                {
                    // Not this sign-in's callback: a stray (or forged) request can't end it.
                    SupportLogger.Shared.Log("OAuth callback with a foreign state ignored", "OAuth", LogLevel.Notice);
                    await RespondAsync(stream, 400, string.Empty, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (query["error"] is { } error)
                {
                    await RespondAsync(stream, 200, string.Format(FailurePage, WebUtility.HtmlEncode(error)), cancellationToken).ConfigureAwait(false);
                    SupportLogger.Shared.Log($"OAuth error: {error}", "OAuth", LogLevel.Error);
                    result.TrySetException(CloudProviderException.Unauthorized());
                    return;
                }

                await RespondAsync(stream, 200, SuccessPage, cancellationToken).ConfigureAwait(false);
                result.TrySetResult(target);
            }
            catch (Exception)
            {
                // A browser that hung up or stalled, a malformed request, or the listener
                // stopping. One connection must never end the sign-in or go unobserved.
            }
        }
    }

    /// <summary>The method and request target (path and query); nulls for anything that isn't an origin-form request line.</summary>
    private static async Task<(string? Method, string? Target)> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestHeadBytes];
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
        return parts.Length >= 2 && parts[1].StartsWith('/') && !parts[1].StartsWith("//", StringComparison.Ordinal)
            ? (parts[0], parts[1])
            : (null, null);
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string html, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
        var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
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
