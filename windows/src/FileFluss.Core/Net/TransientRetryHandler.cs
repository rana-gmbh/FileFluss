// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;

namespace FileFluss.Core.Net;

/// <summary>
/// Retries the HTTP failures that are both passing and safe to repeat, for every provider
/// client <see cref="CloudHttp.Create"/> builds. Most provider ports have no retry of their
/// own, so one 429 during a sync of thousands of files failed that file outright.
///
/// <list type="bullet">
/// <item><b>429 and 503</b> mean the server did not process the request: any request whose
/// body can be sent again is retried, waiting for <c>Retry-After</c> when the server says
/// how long (a wait longer than <see cref="MaxRetryAfter"/> is handed back to the caller
/// rather than slept through).</item>
/// <item><b>502, 504 and a broken connection</b> may come after the server acted, so only
/// read-only methods (GET, HEAD, OPTIONS, PROPFIND) are retried.</item>
/// <item>A body streamed from a file (uploads, chunks) is never replayed — the provider's own
/// chunk logic owns those — and nothing retries more than <see cref="MaxAttempts"/> times.</item>
/// </list>
/// </summary>
public sealed class TransientRetryHandler : DelegatingHandler
{
    public const int MaxAttempts = 4;

    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly Func<int, TimeSpan> _backoff;

    public TransientRetryHandler(HttpMessageHandler inner, Func<int, TimeSpan>? backoff = null)
        : base(inner)
    {
        _backoff = backoff ?? DefaultBackoff;
    }

    /// <summary>1 s, 2 s, 4 s, with a little jitter so parallel transfers don't retry in lockstep.</summary>
    private static TimeSpan DefaultBackoff(int attempt)
        => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts && IsReadOnly(request) && CanReplay(request) && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (attempt >= MaxAttempts || !ShouldRetry(request, response.StatusCode))
            {
                return response;
            }

            var delay = RetryAfter(response) ?? _backoff(attempt);
            if (delay > MaxRetryAfter)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ShouldRetry(HttpRequestMessage request, HttpStatusCode status) => (int)status switch
    {
        429 or 503 => CanReplay(request),
        502 or 504 => CanReplay(request) && IsReadOnly(request),
        _ => false,
    };

    private static bool IsReadOnly(HttpRequestMessage request)
        => request.Method == HttpMethod.Get || request.Method == HttpMethod.Head || request.Method == HttpMethod.Options ||
           string.Equals(request.Method.Method, "PROPFIND", StringComparison.OrdinalIgnoreCase);

    /// <summary>No body, or one held in memory (JSON, form fields, XML) that serializes again identically.</summary>
    private static bool CanReplay(HttpRequestMessage request)
        => request.Content is null or ByteArrayContent or ReadOnlyMemoryContent;

    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }
}
