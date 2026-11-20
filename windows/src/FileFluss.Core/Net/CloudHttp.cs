// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Net;

/// <summary>
/// HttpClient construction and response handling shared by every HTTP provider.
///
/// <para><b>Timeouts.</b> <see cref="HttpClient.Timeout"/> is infinite: it bounds the whole
/// request, and a 7 GB upload legitimately takes hours. API calls bound themselves with
/// <see cref="SendApiAsync"/> (60 s by default); transfers are bounded by the connect timeout
/// and the user's Cancel.</para>
/// </summary>
public static class CloudHttp
{
    public static readonly TimeSpan DefaultApiTimeout = TimeSpan.FromSeconds(60);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string UserAgent { get; } =
        $"FileFluss/{typeof(CloudHttp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0"} (Windows)";

    /// <summary>
    /// A client for one provider instance. <paramref name="trustedSelfSignedHost"/> accepts an
    /// invalid certificate for exactly that host and no other — the macOS "Allow self-signed
    /// certificate" option, whose trust is scoped to the host the user typed.
    /// </summary>
    public static HttpClient Create(string? trustedSelfSignedHost = null, bool allowAutoRedirect = true, CookieContainer? cookies = null)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = allowAutoRedirect,
            UseCookies = cookies is not null,
            CookieContainer = cookies ?? new CookieContainer(),

            // TCP keep-alive, so a connection that died silently (Wi-Fi switch, NAT timeout)
            // is noticed within about two minutes even while nothing is being read — waiting
            // for the server to finish a large upload, say — instead of never.
            ConnectCallback = ConnectWithKeepAliveAsync,
        };

        if (!string.IsNullOrEmpty(trustedSelfSignedHost))
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                // The sender is the SslStream for the connection being validated, so the
                // exemption applies to that one host name and never to a redirect elsewhere.
                RemoteCertificateValidationCallback = (sender, _, _, errors) =>
                    errors == SslPolicyErrors.None ||
                    (sender is SslStream stream &&
                     string.Equals(stream.TargetHostName, trustedSelfSignedHost, StringComparison.OrdinalIgnoreCase)),
            };
        }

        var client = new HttpClient(new TransientRetryHandler(handler), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private static async ValueTask<Stream> ConnectWithKeepAliveAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 60);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 6);
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Sends an API call bounded by <paramref name="timeout"/> (default 60 s), translating transport failures.</summary>
    public static async Task<HttpResponseMessage> SendApiAsync(
        this HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? DefaultApiTimeout);
        try
        {
            return await client.SendAsync(request, completion, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw CloudProviderException.Network(new TimeoutException(Localization.L("The request timed out.")));
        }
        catch (HttpRequestException e)
        {
            throw CloudProviderException.Network(e);
        }
        catch (ObjectDisposedException e)
        {
            // The provider was disposed under a running call (its account was re-authenticated
            // or edited); report it like any lost connection rather than as a crash-looking error.
            throw CloudProviderException.Network(e);
        }
    }

    /// <summary>Sends a transfer request with no overall timeout, streaming the response.</summary>
    public static async Task<HttpResponseMessage> SendTransferAsync(this HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw CloudProviderException.Network(e);
        }
        catch (ObjectDisposedException e)
        {
            throw CloudProviderException.Network(e);
        }
    }

    /// <summary>
    /// Throws the conventional <see cref="CloudProviderException"/> for an unsuccessful
    /// status: 401 → Unauthorized, 404 → NotFound, 429 → RateLimited, 507 → QuotaExceeded,
    /// otherwise ServerError. Providers with richer error bodies map those first.
    /// </summary>
    public static void ThrowForStatus(HttpResponseMessage response, string? path = null)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw StatusError(response.StatusCode, path);
    }

    public static CloudProviderException StatusError(HttpStatusCode status, string? path = null) => (int)status switch
    {
        401 => CloudProviderException.Unauthorized(),
        404 => CloudProviderException.NotFound(path ?? string.Empty),
        429 => CloudProviderException.RateLimited(),
        507 => CloudProviderException.QuotaExceeded(),
        _ => CloudProviderException.ServerError((int)status),
    };

    /// <summary>Reads and deserializes a JSON body, mapping malformed bodies to InvalidResponse.</summary>
    public static async Task<T> ReadJsonAsync<T>(this HttpResponseMessage response, CancellationToken cancellationToken, JsonSerializerOptions? options = null)
    {
        try
        {
            var value = await response.Content.ReadFromJsonAsyncSafe<T>(options ?? Json, cancellationToken).ConfigureAwait(false);
            return value ?? throw CloudProviderException.InvalidResponse();
        }
        catch (JsonException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    private static async Task<T?> ReadFromJsonAsyncSafe<T>(this HttpContent content, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A JSON request body.</summary>
    public static StringContent JsonContent(object? value, JsonSerializerOptions? options = null)
        => new(JsonSerializer.Serialize(value, options ?? Json), Encoding.UTF8, "application/json");

    /// <summary>An application/x-www-form-urlencoded body.</summary>
    public static FormUrlEncodedContent FormContent(params (string Key, string Value)[] fields)
        => new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    public static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    public static AuthenticationHeaderValue Basic(string user, string password)
        => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    /// <summary>Body text for logging and error messages, capped so a huge HTML error page stays readable.</summary>
    public static async Task<string> ReadBodySnippetAsync(this HttpResponseMessage response, CancellationToken cancellationToken, int maxLength = 500)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return text.Length <= maxLength ? text : text[..maxLength];
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>RFC 3986 percent-encoding of a single value (spaces as %20, not +).</summary>
    public static string Escape(string value) => Uri.EscapeDataString(value);

    /// <summary>Percent-encodes each segment of a POSIX path, keeping the slashes.</summary>
    public static string EscapePath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
