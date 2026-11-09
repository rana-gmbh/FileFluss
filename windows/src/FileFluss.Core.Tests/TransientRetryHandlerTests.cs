// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Http.Headers;
using FileFluss.Core.Net;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests;

public sealed class TransientRetryHandlerTests
{
    private static HttpClient Client(FakeHttpHandler fake)
        => new(new TransientRetryHandler(fake, _ => TimeSpan.FromMilliseconds(1))) { Timeout = Timeout.InfiniteTimeSpan };

    private static Func<HttpRequestMessage, string?, HttpResponseMessage> Sequence(params HttpStatusCode[] statuses)
    {
        var queue = new Queue<HttpStatusCode>(statuses);
        return (_, _) => new HttpResponseMessage(queue.Count > 1 ? queue.Dequeue() : queue.Peek()) { Content = new StringContent("{}") };
    }

    [Fact]
    public async Task RateLimitedGetIsRetriedUntilItSucceeds()
    {
        var fake = new FakeHttpHandler().On(HttpMethod.Get, "/list", Sequence(HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK));

        using var response = await Client(fake).GetAsync("https://api.example/list");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, fake.Requests.Count);
    }

    [Fact]
    public async Task RetriesAreBounded()
    {
        var fake = new FakeHttpHandler().On(HttpMethod.Get, "/list", Sequence(HttpStatusCode.TooManyRequests));

        using var response = await Client(fake).GetAsync("https://api.example/list");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TransientRetryHandler.MaxAttempts, fake.Requests.Count);
    }

    [Fact]
    public async Task RateLimitedJsonPostIsReplayedWithItsBody()
    {
        var fake = new FakeHttpHandler().On(HttpMethod.Post, "/rpc", Sequence(HttpStatusCode.TooManyRequests, HttpStatusCode.OK));

        using var response = await Client(fake).PostAsync("https://api.example/rpc", new StringContent("{\"path\":\"/a\"}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(fake.Requests, r => Assert.Equal("{\"path\":\"/a\"}", r.Body));
    }

    [Fact]
    public async Task StreamedUploadsAreNeverReplayed()
    {
        var fake = new FakeHttpHandler().On(HttpMethod.Put, "/upload", Sequence(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK));

        using var response = await Client(fake).PutAsync("https://api.example/upload", new StreamContent(new MemoryStream([1, 2, 3])));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task BadGatewayIsRetriedOnlyForReadOnlyMethods()
    {
        var fake = new FakeHttpHandler()
            .On(HttpMethod.Get, "/item", Sequence(HttpStatusCode.BadGateway, HttpStatusCode.OK))
            .On(HttpMethod.Post, "/create", Sequence(HttpStatusCode.BadGateway, HttpStatusCode.OK));
        var client = Client(fake);

        using var get = await client.GetAsync("https://api.example/item");
        using var post = await client.PostAsync("https://api.example/create", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, post.StatusCode);
        Assert.Equal(3, fake.Requests.Count);
    }

    [Fact]
    public async Task ALongRetryAfterIsHandedBackInsteadOfSleptThrough()
    {
        var fake = new FakeHttpHandler().On(HttpMethod.Get, "/list", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return response;
        });

        using var response = await Client(fake).GetAsync("https://api.example/list");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public void RetryAfterReadsSecondsAndDates()
    {
        using var seconds = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        seconds.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(3), TransientRetryHandler.RetryAfter(seconds));

        using var past = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        past.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(TimeSpan.Zero, TransientRetryHandler.RetryAfter(past));
    }

    [Fact]
    public async Task ConnectionFailuresAreRetriedForGetsButNotPosts()
    {
        var failing = new FailingHandler(failures: 2);
        var client = new HttpClient(new TransientRetryHandler(failing, _ => TimeSpan.FromMilliseconds(1)));

        using var response = await client.GetAsync("https://api.example/x");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, failing.Calls);

        var failingPost = new FailingHandler(failures: 1);
        var postClient = new HttpClient(new TransientRetryHandler(failingPost, _ => TimeSpan.FromMilliseconds(1)));
        await Assert.ThrowsAsync<HttpRequestException>(() => postClient.PostAsync("https://api.example/x", new StringContent("{}")));
        Assert.Equal(1, failingPost.Calls);
    }

    private sealed class FailingHandler(int failures) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Calls <= failures
                ? throw new HttpRequestException("connection reset")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
