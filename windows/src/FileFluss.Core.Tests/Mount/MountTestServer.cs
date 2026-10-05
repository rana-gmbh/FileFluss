// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net.Http.Headers;
using System.Text;
using FileFluss.Core.Mount;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Mount;

/// <summary>
/// A <see cref="WebDavServer"/> over a <see cref="MemoryCloudProvider"/> on a free loopback
/// port, with its read cache, sidecar store and temp files in a private temp folder.
/// </summary>
internal sealed class MountTestServer : IAsyncDisposable
{
    private MountTestServer(MemoryCloudProvider provider, InstrumentedProvider wrapper, WebDavServer server, string folder, WebDavReadCache cache, WebDavSidecarStore sidecar)
    {
        Provider = provider;
        Wrapper = wrapper;
        Server = server;
        Folder = folder;
        Cache = cache;
        Sidecar = sidecar;
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public MemoryCloudProvider Provider { get; }

    /// <summary>What the server actually talks to: counts calls, injects failures.</summary>
    public InstrumentedProvider Wrapper { get; }

    public WebDavServer Server { get; }

    public string Folder { get; }

    public WebDavReadCache Cache { get; }

    public WebDavSidecarStore Sidecar { get; }

    public HttpClient Http { get; }

    /// <summary>"/FileFluss-…" — prefix of every request path inside the mount.</summary>
    public string Root => "/" + Server.Secret;

    public static async Task<MountTestServer> StartAsync(
        Action<MemoryCloudProvider>? seed = null,
        string mountRoot = "/",
        bool replacesInPlace = false,
        long cacheBytes = 10 * 1024 * 1024)
    {
        var provider = new MemoryCloudProvider();
        provider.AddFile("/hello.txt", "hi");
        provider.AddFolder("/folder");
        provider.AddFile("/folder/inside.txt", "nested");
        seed?.Invoke(provider);

        var folder = Path.Combine(Path.GetTempPath(), "filefluss-mount-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var cache = new WebDavReadCache(Path.Combine(folder, "cache"), cacheBytes);
        var sidecar = new WebDavSidecarStore(Path.Combine(folder, "sidecar"));
        var wrapper = new InstrumentedProvider(provider);
        var server = new WebDavServer(wrapper, new WebDavServerOptions
        {
            MountRoot = mountRoot,
            DisplayName = "Test Cloud",
            ReadCache = cache,
            Sidecar = sidecar,
            TempDirectory = Path.Combine(folder, "temp"),
            UploadReplacesInPlace = replacesInPlace,
        });
        await server.StartAsync();
        return new MountTestServer(provider, wrapper, server, folder, cache, sidecar);
    }

    public Uri Url(string path) => new($"http://127.0.0.1:{Server.Port}{path}");

    /// <summary>A path inside the mount: "/hello.txt" → "/FileFluss-…/hello.txt".</summary>
    public string In(string path) => Root + path;

    public async Task<(HttpResponseMessage Response, string Body)> SendAsync(
        string method,
        string path,
        IReadOnlyDictionary<string, string>? headers = null,
        string? body = null,
        string? contentType = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), Url(path));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/xml");
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response, text);
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Server.DisposeAsync();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
