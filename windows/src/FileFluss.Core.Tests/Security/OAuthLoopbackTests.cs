// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;
using FileFluss.Core.Net;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Tests.Security;

/// <summary>Swaps the process-wide <see cref="BrowserLauncher.Handler"/>; its tests must not overlap.</summary>
[CollectionDefinition("BrowserLauncher", DisableParallelization = true)]
public sealed class BrowserLauncherCollection;

[Collection("BrowserLauncher")]
public sealed class OAuthLoopbackTests : IDisposable
{
    private readonly Action<Uri> _savedHandler = BrowserLauncher.Handler;
    private readonly TaskCompletionSource<Uri> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };

    public OAuthLoopbackTests()
    {
        BrowserLauncher.Handler = uri => _opened.TrySetResult(uri);
    }

    public void Dispose()
    {
        BrowserLauncher.Handler = _savedHandler;
        _http.Dispose();
    }

    private static Uri Authorize(string redirect, string? state = "expected-state")
        => new($"https://auth.example/authorize?client_id=x&redirect_uri={Uri.EscapeDataString(redirect)}" + (state is null ? string.Empty : "&state=" + state));

    /// <summary>Starts a sign-in and returns it with the redirect URI the "browser" was sent to.</summary>
    private async Task<(Task<OAuthCallback> SignIn, string Redirect, CancellationTokenSource Cancel)> StartAsync(string? state = "expected-state")
    {
        var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var signIn = OAuthLoopback.AuthenticateAsync(redirect => Authorize(redirect, state), cancel.Token);
        var opened = await _opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var redirect = HttpUtility.ParseQueryString(opened.Query)["redirect_uri"]!;
        return (signIn, redirect.Replace("localhost", "127.0.0.1", StringComparison.Ordinal), cancel);
    }

    [Fact]
    public async Task ACallbackWithTheRightStateDeliversTheCode()
    {
        var (signIn, redirect, cancel) = await StartAsync();
        using (cancel)
        {
            using var response = await _http.GetAsync($"{redirect}/?code=the-code&state=expected-state");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var callback = await signIn.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("the-code", callback.RequireCode("expected-state"));
        }
    }

    [Fact]
    public async Task ForgedOrStrayCallbacksDoNotEndTheSignIn()
    {
        var (signIn, redirect, cancel) = await StartAsync();
        using (cancel)
        {
            // A local process or a web page guessing the port: no state, wrong state, an error
            // without the state, another path, another method.
            using (var wrong = await _http.GetAsync($"{redirect}/?code=attacker&state=guess"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            }

            using (var missing = await _http.GetAsync($"{redirect}/?code=attacker"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            }

            using (var error = await _http.GetAsync($"{redirect}/?error=access_denied"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
            }

            using (var path = await _http.GetAsync($"{redirect}/other?code=x&state=expected-state"))
            {
                Assert.Equal(HttpStatusCode.NotFound, path.StatusCode);
            }

            using (var post = await _http.PostAsync($"{redirect}/?code=x&state=expected-state", new StringContent(string.Empty)))
            {
                Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
            }

            Assert.False(signIn.IsCompleted);

            using var real = await _http.GetAsync($"{redirect}/?code=real&state=expected-state");
            var callback = await signIn.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("real", callback.RequireCode("expected-state"));
        }
    }

    [Fact]
    public async Task AnIdleConnectionDoesNotBlockTheCallback()
    {
        var (signIn, redirect, cancel) = await StartAsync();
        using (cancel)
        {
            // A browser's speculative connection that never sends a request.
            var port = new Uri(redirect).Port;
            using var idle = new TcpClient();
            await idle.ConnectAsync(IPAddress.Loopback, port);
            using var idle2 = new TcpClient();
            await idle2.ConnectAsync(IPAddress.Loopback, port);
            await idle2.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /?code=half"));

            using var response = await _http.GetAsync($"{redirect}/?code=the-code&state=expected-state");
            var callback = await signIn.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("the-code", callback.RequireCode("expected-state"));
        }
    }

    [Fact]
    public async Task AnErrorWithTheRightStateEndsTheSignIn()
    {
        var (signIn, redirect, cancel) = await StartAsync();
        using (cancel)
        {
            using var response = await _http.GetAsync($"{redirect}/?error=%3Cscript%3Ealert(1)%3C%2Fscript%3E&state=expected-state");
            var page = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("<script>", page, StringComparison.Ordinal);
            Assert.Contains("&lt;script&gt;", page, StringComparison.Ordinal);

            var error = await Assert.ThrowsAsync<CloudProviderException>(() => signIn.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
        }
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("\\\\server\\share\\evil.exe")]
    public void BrowserLauncherOpensOnlyWebUrls(string url)
    {
        BrowserLauncher.Open(url);
        Assert.False(_opened.Task.IsCompleted);
    }

    [Fact]
    public void BrowserLauncherOpensHttps()
    {
        BrowserLauncher.Open("https://github.com/rana-gmbh/FileFluss");
        Assert.True(_opened.Task.IsCompleted);
    }
}
