// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Tests.Support;

/// <summary>
/// Scripted HTTP for provider tests: register responders by predicate, inspect what was
/// sent. Unmatched requests fail the test loudly with a 599 so a missing stub is obvious.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, string?, HttpResponseMessage> Respond)> _routes = [];

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);

    /// <summary>Responds to requests whose method matches and whose URL contains <paramref name="urlFragment"/>.</summary>
    public FakeHttpHandler On(HttpMethod method, string urlFragment, Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        _routes.Add((r => r.Method == method && r.RequestUri!.ToString().Contains(urlFragment, StringComparison.Ordinal), respond));
        return this;
    }

    public FakeHttpHandler OnJson(HttpMethod method, string urlFragment, string json, HttpStatusCode status = HttpStatusCode.OK)
        => On(method, urlFragment, (_, _) => Json(json, status));

    public FakeHttpHandler OnText(HttpMethod method, string urlFragment, string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "text/plain")
        => On(method, urlFragment, (_, _) => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

    public FakeHttpHandler OnBytes(HttpMethod method, string urlFragment, byte[] body, HttpStatusCode status = HttpStatusCode.OK)
        => On(method, urlFragment, (_, _) => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public HttpClient CreateClient() => new(this) { Timeout = Timeout.InfiniteTimeSpan };

    public RecordedRequest Single(HttpMethod method, string urlFragment)
        => Requests.Single(r => r.Method == method && r.Uri.ToString().Contains(urlFragment, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
        {
            // Read through a copy so ProgressStreamContent still reports bytes as it would on the wire.
            using var buffer = new MemoryStream();
            await request.Content.CopyToAsync(buffer, cancellationToken);
            body = Encoding.UTF8.GetString(buffer.ToArray());
        }

        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, headers, body));

        foreach (var (match, respond) in _routes)
        {
            if (match(request))
            {
                var response = respond(request, body);
                response.RequestMessage = request;
                return response;
            }
        }

        return new HttpResponseMessage((HttpStatusCode)599)
        {
            Content = new StringContent($"FakeHttpHandler: no route for {request.Method} {request.RequestUri}"),
        };
    }
}

/// <summary>Answers every prompt from a script; records status lines.</summary>
public sealed class ScriptedConnectPrompt : IConnectPrompt
{
    public Queue<string?> Codes { get; } = new();

    public Queue<int?> Choices { get; } = new();

    public List<string?> StatusLines { get; } = [];

    public bool ConfirmAnswer { get; set; } = true;

    public void ShowStatus(string? message) => StatusLines.Add(message);

    public Task<string?> RequestCodeAsync(string title, string message, CancellationToken cancellationToken)
        => Task.FromResult(Codes.Count > 0 ? Codes.Dequeue() : null);

    public Task<int?> ChooseAsync(string title, string message, IReadOnlyList<string> options, CancellationToken cancellationToken)
        => Task.FromResult(Choices.Count > 0 ? Choices.Dequeue() : 0);

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) => Task.FromResult(ConfirmAnswer);

    public Task ShowImageAsync(byte[] png, string caption, Task until, CancellationToken cancellationToken) => until;
}
