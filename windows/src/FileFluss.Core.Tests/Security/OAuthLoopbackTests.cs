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
    private async Task<(Task<OAuthCallback> SignIn, string Redirect, CancellationTokenSource Cancel)> StartAsync(string? state = "expected-state", IReadOnlyList<int>? fixedPorts = null)
    {
        var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var signIn = OAuthLoopback.AuthenticateAsync(redirect => Authorize(redirect, state), cancel.Token, fixedPorts: fixedPorts);
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

    // MARK: - Fixed ports (pCloud)

    /// <summary>A listener another program might hold, on a port the system picks.</summary>
    private static TcpListener Occupy(IPAddress address)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        return listener;
    }

    private static int PortOf(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>A port nothing listens on right now, on 127.0.0.1 or ::1.</summary>
    private static int FreePort()
    {
        while (true)
        {
            var probe = Occupy(IPAddress.Loopback);
            var port = PortOf(probe);
            probe.Stop();
            if (!Socket.OSSupportsIPv6)
            {
                return port;
            }

            try
            {
                Occupy(IPAddress.IPv6Loopback, port).Stop();
                return port;
            }
            catch (SocketException)
            {
                // Taken on ::1 — pick another.
            }
        }
    }

    private static TcpListener Occupy(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        listener.Start();
        return listener;
    }

    /// <summary>pCloud matches the redirect exactly: port 80 is registered as plain http://localhost.</summary>
    [Theory]
    [InlineData(80, "http://localhost")]
    [InlineData(53682, "http://localhost:53682")]
    [InlineData(53683, "http://localhost:53683")]
    public void Port80IsWrittenWithoutAPort(int port, string origin)
        => Assert.Equal(origin, OAuthLoopback.RedirectOrigin(port));

    [Fact]
    public async Task TheFirstFreeFixedPortIsUsed()
    {
        var first = FreePort();
        var (signIn, redirect, cancel) = await StartAsync(fixedPorts: [first, FreePort()]);
        using (cancel)
        {
            Assert.Equal($"http://127.0.0.1:{first}", redirect);
            using var response = await _http.GetAsync($"{redirect}/?code=the-code&state=expected-state");

            var callback = await signIn.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal($"http://localhost:{first}", callback.RedirectUri);
            Assert.Equal("the-code", callback.RequireCode("expected-state"));
        }
    }

    /// <summary>On Windows, HTTP.sys often holds port 80: a port that can't be bound is passed over, not an error.</summary>
    [Fact]
    public async Task ABusyFixedPortIsSkipped()
    {
        var busy = Occupy(IPAddress.Loopback);
        try
        {
            var free = FreePort();
            var (signIn, redirect, cancel) = await StartAsync(fixedPorts: [PortOf(busy), free]);
            using (cancel)
            {
                Assert.Equal($"http://127.0.0.1:{free}", redirect);
                using var response = await _http.GetAsync($"{redirect}/?code=the-code&state=expected-state");

                var callback = await signIn.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal($"http://localhost:{free}", callback.RedirectUri);
                Assert.Equal("the-code", callback.RequireCode("expected-state"));
            }
        }
        finally
        {
            busy.Stop();
        }
    }

    /// <summary>A browser resolving "localhost" to ::1 would hand the code to whatever holds the port there.</summary>
    [Fact]
    public void APortTakenOnIpv6AloneIsSkippedToo()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        TcpListener busy;
        try
        {
            busy = Occupy(IPAddress.IPv6Loopback);
        }
        catch (SocketException)
        {
            // No IPv6 loopback on this machine.
            return;
        }

        try
        {
            var free = FreePort();
            var (v4, v6) = OAuthLoopback.ListenOnFirstFreePort([PortOf(busy), free])!.Value;
            try
            {
                Assert.Equal(free, PortOf(v4));
            }
            finally
            {
                v4.Stop();
                v6?.Stop();
            }
        }
        finally
        {
            busy.Stop();
        }
    }

    [Fact]
    public async Task WithEveryFixedPortBusyTheSignInFailsBeforeTheBrowserOpens()
    {
        var busy = new[] { Occupy(IPAddress.Loopback), Occupy(IPAddress.Loopback) };
        try
        {
            var error = await Assert.ThrowsAsync<CloudProviderException>(() =>
                OAuthLoopback.AuthenticateAsync(redirect => Authorize(redirect), CancellationToken.None, fixedPorts: busy.Select(PortOf).ToList()));

            Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
            Assert.Equal(Localization.L("The sign-in needs a free local port, and all of them are in use by other apps."), error.Message);
            Assert.False(_opened.Task.IsCompleted);
        }
        finally
        {
            foreach (var listener in busy)
            {
                listener.Stop();
            }
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
