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

    /// <summary>
    /// All waits of one request together stay below the API timeout
    /// (<see cref="CloudHttp.DefaultApiTimeout"/>, 60 s): two 30-second Retry-After waits used
    /// to end as "the request timed out" instead of the server's rate limit.
    /// </summary>
    public static readonly TimeSpan MaxTotalWait = TimeSpan.FromSeconds(40);

    /// <summary>Set on a request this handler retried as far as it would and then handed back.</summary>
    private static readonly HttpRequestOptionsKey<bool> GaveUpKey = new("FileFluss.TransientRetry.GaveUp");

    /// <summary>Set while this handler waits before retrying a request.</summary>
    private static readonly HttpRequestOptionsKey<bool> WaitingKey = new("FileFluss.TransientRetry.Waiting");

    private const string AlreadyRetriedDataKey = "FileFluss.TransientRetry.AlreadyRetried";

    private readonly Func<int, TimeSpan> _backoff;
    private readonly TimeSpan _maxTotalWait;

    public TransientRetryHandler(HttpMessageHandler inner, Func<int, TimeSpan>? backoff = null, TimeSpan? maxTotalWait = null)
        : base(inner)
    {
        _backoff = backoff ?? DefaultBackoff;
        _maxTotalWait = maxTotalWait ?? MaxTotalWait;
    }

    /// <summary>
    /// Whether this handler already retried the request behind <paramref name="response"/> as
    /// far as it would (a 429 or 503 with a replayable body, a 502/504 on a read). A provider's
    /// own retry loop then mustn't start another round: four attempts here times four there
    /// made one call into sixteen during rate limiting, prolonging it.
    /// </summary>
    public static bool AlreadyRetried(HttpResponseMessage response)
        => response.RequestMessage is { } request && request.Options.TryGetValue(GaveUpKey, out var gaveUp) && gaveUp;

    /// <summary>Whether the request is waiting to be retried right now (a timeout then means the server kept refusing).</summary>
    public static bool IsWaitingToRetry(HttpRequestMessage request)
        => request.Options.TryGetValue(WaitingKey, out var waiting) && waiting;

    /// <summary>Marks an error made from a response <see cref="AlreadyRetried"/>, for loops that only see the error.</summary>
    public static TException MarkAlreadyRetried<TException>(TException error, HttpResponseMessage response)
        where TException : Exception
    {
        if (AlreadyRetried(response))
        {
            error.Data[AlreadyRetriedDataKey] = true;
        }

        return error;
    }

    /// <summary>Whether <see cref="MarkAlreadyRetried"/> marked the error.</summary>
    public static bool WasAlreadyRetried(Exception error) => error.Data[AlreadyRetriedDataKey] is true;

    /// <summary>1 s, 2 s, 4 s, with a little jitter so parallel transfers don't retry in lockstep.</summary>
    private static TimeSpan DefaultBackoff(int attempt)
        => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts && IsReadOnly(request) && CanReplay(request) && !cancellationToken.IsCancellationRequested)
            {
                var backoff = _backoff(attempt);
                waited += backoff;
                await WaitAsync(request, backoff, cancellationToken).ConfigureAwait(false);
                continue;
            }

            response.RequestMessage ??= request;
            if (!ShouldRetry(request, response.StatusCode))
            {
                return response;
            }

            var delay = RetryAfter(response) ?? _backoff(attempt);
            if (attempt >= MaxAttempts || delay > MaxRetryAfter || waited + delay > _maxTotalWait)
            {
                // As far as this handler goes; the caller's own loop shouldn't go round again.
                request.Options.Set(GaveUpKey, true);
                return response;
            }

            response.Dispose();
            waited += delay;
            await WaitAsync(request, delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WaitAsync(HttpRequestMessage request, TimeSpan delay, CancellationToken cancellationToken)
    {
        // Left set when the wait is cancelled, so the caller can tell a timeout that struck while
        // the server was refusing (rate limited) from one where nothing answered.
        request.Options.Set(WaitingKey, true);
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        request.Options.Set(WaitingKey, false);
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
