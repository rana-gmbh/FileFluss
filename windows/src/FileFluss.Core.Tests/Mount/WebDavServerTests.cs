// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using FileFluss.Core.Mount;

namespace FileFluss.Core.Tests.Mount;

/// <summary>
/// Port of the macOS <c>WebDAVServerTests</c>: the server over an in-memory provider, driven
/// over loopback the way the Windows redirector drives it — plus the Windows-specific parts
/// (secret path, Win32 properties, ranges, keep-alive, lock refresh, PROPPATCH times).
/// </summary>
public sealed class WebDavServerTests : IAsyncLifetime
{
    private static readonly XNamespace D = "DAV:";
    private static readonly XNamespace Z = "urn:schemas-microsoft-com:";

    private MountTestServer _t = null!;

    public async Task InitializeAsync() => _t = await MountTestServer.StartAsync();

    public async Task DisposeAsync() => await _t.DisposeAsync();

    private static Dictionary<string, string> H(params (string Name, string Value)[] headers)
        => headers.ToDictionary(h => h.Name, h => h.Value);

    // MARK: - OPTIONS

    [Fact]
    public async Task OptionsAdvertisesDavClassesAndAuthoring()
    {
        var (response, _) = await _t.SendAsync("OPTIONS", _t.In("/"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1, 2", string.Join(", ", response.Headers.GetValues("DAV")));
        Assert.Equal("DAV", response.Headers.GetValues("MS-Author-Via").Single());
        Assert.Contains("PROPFIND", response.Content.Headers.Allow);
        Assert.Contains("LOCK", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task OptionsOnServerRootAnswersForTheRedirectorsProbe()
    {
        var (response, _) = await _t.SendAsync("OPTIONS", "/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("DAV", response.Headers.Select(h => h.Key));
    }

    // MARK: - PROPFIND

    [Fact]
    public async Task PropfindRootListsChildren()
    {
        var (response, body) = await _t.SendAsync("PROPFIND", _t.In("/"), H(("Depth", "1")));
        Assert.Equal(207, (int)response.StatusCode);
        var xml = XDocument.Parse(body);
        var hrefs = xml.Descendants(D + "href").Select(h => h.Value).ToList();
        Assert.Equal(_t.Root + "/", hrefs[0]);
        Assert.Contains(_t.Root + "/hello.txt", hrefs);
        Assert.Contains(_t.Root + "/folder/", hrefs);
        Assert.Equal("Test Cloud", xml.Descendants(D + "displayname").First().Value);

        var folder = Response(xml, _t.Root + "/folder/");
        Assert.NotNull(folder.Descendants(D + "collection").SingleOrDefault());
    }

    [Fact]
    public async Task PropfindOnFileReturnsOneEntry()
    {
        var (response, body) = await _t.SendAsync("PROPFIND", _t.In("/hello.txt"), H(("Depth", "0")));
        Assert.Equal(207, (int)response.StatusCode);
        var xml = XDocument.Parse(body);
        Assert.Single(xml.Descendants(D + "response"));
        Assert.Equal("2", xml.Descendants(D + "getcontentlength").Single().Value);
        Assert.Equal("hello.txt", xml.Descendants(D + "displayname").Single().Value);
    }

    [Fact]
    public async Task PropfindMissingPathIs404()
    {
        var (response, _) = await _t.SendAsync("PROPFIND", _t.In("/missing.txt"), H(("Depth", "0")));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PropfindAnswersWindowsPropertiesAndRejectsUnknownOnesGracefully()
    {
        // Close to what the Mini-Redirector sends when it lists a folder.
        const string body = """
            <?xml version="1.0" encoding="utf-8" ?>
            <D:propfind xmlns:D="DAV:" xmlns:Z="urn:schemas-microsoft-com:"><D:prop>
            <D:getcontentlength/><D:getlastmodified/><D:creationdate/><D:resourcetype/><D:displayname/>
            <Z:Win32FileAttributes/><Z:Win32CreationTime/><Z:Win32LastAccessTime/><Z:Win32LastModifiedTime/>
            <D:quota-available-bytes/><D:quota-used-bytes/><X:custom xmlns:X="urn:example"/>
            </D:prop></D:propfind>
            """;
        _t.Provider.AddFile("/.hidden", "x");
        var modified = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        _t.Provider.AddFile("/dated.txt", "abc", modified);

        var (response, text) = await _t.SendAsync("PROPFIND", _t.In("/"), H(("Depth", "1")), body);
        Assert.Equal(207, (int)response.StatusCode);
        var xml = XDocument.Parse(text);

        var dated = Response(xml, _t.Root + "/dated.txt");
        var ok = Propstat(dated, "200");
        Assert.Equal("00000020", ok.Descendants(Z + "Win32FileAttributes").Single().Value);
        Assert.Equal("Wed, 04 Mar 2026 05:06:07 GMT", ok.Descendants(Z + "Win32LastModifiedTime").Single().Value);
        Assert.Equal("Wed, 04 Mar 2026 05:06:07 GMT", ok.Descendants(D + "getlastmodified").Single().Value);
        Assert.Equal("2026-03-04T05:06:07Z", ok.Descendants(D + "creationdate").Single().Value);
        Assert.Equal("3", ok.Descendants(D + "getcontentlength").Single().Value);

        // No quota from this provider, and a property nobody knows: 404 propstat, not an error.
        var missing = Propstat(dated, "404");
        Assert.NotNull(missing.Descendants(D + "quota-available-bytes").SingleOrDefault());
        Assert.NotNull(missing.Descendants((XNamespace)"urn:example" + "custom").SingleOrDefault());

        Assert.Equal("00000010", Propstat(Response(xml, _t.Root + "/folder/"), "200").Descendants(Z + "Win32FileAttributes").Single().Value);
        Assert.Equal("00000022", Propstat(Response(xml, _t.Root + "/.hidden"), "200").Descendants(Z + "Win32FileAttributes").Single().Value);
    }

    [Fact]
    public async Task PropfindPercentEncodesNamesInHrefs()
    {
        _t.Provider.AddFile("/Ünïcode & spaces #1.txt", "x");
        var (_, text) = await _t.SendAsync("PROPFIND", _t.In("/"), H(("Depth", "1")));
        var hrefs = XDocument.Parse(text).Descendants(D + "href").Select(h => h.Value).ToList();
        Assert.Contains(_t.Root + "/%C3%9Cn%C3%AFcode%20&%20spaces%20%231.txt", hrefs);

        var (response, body) = await _t.SendAsync("GET", _t.In("/%C3%9Cn%C3%AFcode%20%26%20spaces%20%231.txt"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("x", body);
    }

    [Fact]
    public async Task PropfindDepthZeroOnFolderOmitsChildren()
    {
        var (_, text) = await _t.SendAsync("PROPFIND", _t.In("/folder"), H(("Depth", "0")));
        var xml = XDocument.Parse(text);
        Assert.Single(xml.Descendants(D + "response"));
        Assert.Equal(_t.Root + "/folder/", xml.Descendants(D + "href").Single().Value);
    }

    [Fact]
    public async Task MountRootMapsToTheAccountsRootPath()
    {
        await using var scoped = await MountTestServer.StartAsync(mountRoot: "/folder");
        var (_, text) = await scoped.SendAsync("PROPFIND", scoped.In("/"), H(("Depth", "1")));
        var hrefs = XDocument.Parse(text).Descendants(D + "href").Select(h => h.Value).ToList();
        Assert.Equal([scoped.Root + "/", scoped.Root + "/inside.txt"], hrefs);

        var (_, body) = await scoped.SendAsync("GET", scoped.In("/inside.txt"));
        Assert.Equal("nested", body);
    }

    // MARK: - Access control

    [Fact]
    public async Task EverythingOutsideTheSecretIs404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _t.SendAsync("GET", "/hello.txt")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _t.SendAsync("PROPFIND", "/FileFluss-00000000000000000000000000000000/", H(("Depth", "1")))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _t.SendAsync("DELETE", "/hello.txt")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _t.SendAsync("PUT", "/x.txt", body: "data", contentType: "text/plain")).Response.StatusCode);
        Assert.True(_t.Provider.Exists("/hello.txt"));
        Assert.False(_t.Provider.Exists("/x.txt"));
    }

    [Fact]
    public async Task ServerRootListsNothing()
    {
        var (response, text) = await _t.SendAsync("PROPFIND", "/", H(("Depth", "1")));
        Assert.Equal(207, (int)response.StatusCode);
        Assert.Single(XDocument.Parse(text).Descendants(D + "response"));
        Assert.DoesNotContain(_t.Server.Secret, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForeignHostHeaderIsRefused()
    {
        // DNS rebinding: a web page whose host resolves to 127.0.0.1 still sends its own Host.
        var (response, _) = await _t.SendAsync("GET", _t.In("/hello.txt"), H(("Host", "evil.example")));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SecretMatchesIgnoringCase()
    {
        var (response, body) = await _t.SendAsync("GET", "/" + _t.Server.Secret.ToUpperInvariant() + "/hello.txt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hi", body);
    }

    [Fact]
    public async Task DotSegmentsAreRejected()
    {
        // HttpClient normalises "..", so speak raw HTTP.
        var answer = await RawAsync($"GET {_t.Root}/folder/../hello.txt HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400", answer);
    }

    [Theory]
    [InlineData("/folder/%2e%2e/hello.txt")]
    [InlineData("/folder/%2E%2E/%2E%2E/hello.txt")]
    [InlineData("/folder%2F..%2F..%2Fhello.txt")]
    [InlineData("/folder%5C..%5C..%5Chello.txt")]
    [InlineData("/folder\\..\\..\\hello.txt")]
    [InlineData("/a%00b")]
    public async Task EncodedTraversalIsRejected(string suffix)
    {
        var answer = await RawAsync($"GET {_t.Root}{suffix} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400", answer);
    }

    [Theory]
    [InlineData("/a/%2e%2e/b", null)]
    [InlineData("/a%5Cb", null)]
    [InlineData("/a/./b", null)]
    [InlineData("http://127.0.0.1:1/x/..%2Fy", null)]
    [InlineData("/a%20b/c", "/a b/c")]
    [InlineData("/a/b/", "/a/b/")]
    [InlineData("http://127.0.0.1:1/x/y?q=1#f", "/x/y")]
    public void DecodeTargetRefusesTraversal(string raw, string? expected)
        => Assert.Equal(expected, WebDavServer.DecodeTarget(raw));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.1:8080", true)]
    [InlineData("localhost:8080", true)]
    [InlineData("[::1]:8080", true)]
    [InlineData("127.0.0.1.evil.example", false)]
    [InlineData("evil.example", false)]
    [InlineData("localhost.evil.example:80", false)]
    public void OnlyLoopbackHostHeadersPass(string host, bool expected)
        => Assert.Equal(expected, WebDavServer.IsLoopbackHost(host));

    [Fact]
    public async Task WithoutTheSecretNothingIsServed()
    {
        var (response, _) = await _t.SendAsync("GET", "/FileFluss-00000000000000000000000000000000/hello.txt");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // The redirector's probe of the server root gets an empty collection that names nothing.
        var (_, listing) = await _t.SendAsync("PROPFIND", "/", H(("Depth", "1")));
        Assert.DoesNotContain(_t.Server.Secret, listing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hello.txt", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretsAreRandomAndRecognisable()
    {
        var a = WebDavServer.NewSecret();
        var b = WebDavServer.NewSecret();
        Assert.NotEqual(a, b);
        Assert.Matches("^FileFluss-[0-9a-f]{32}$", a);
        Assert.True(WebDavServer.IsMountSecret(a));
        Assert.True(WebDavServer.IsMountSecret(a.ToUpperInvariant()));
        Assert.False(WebDavServer.IsMountSecret("DavWWWRoot"));
    }

    // MARK: - GET / HEAD

    [Fact]
    public async Task GetStreamsBytes()
    {
        var (response, body) = await _t.SendAsync("GET", _t.In("/hello.txt"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hi", body);
        Assert.Equal(2, response.Content.Headers.ContentLength);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
    }

    [Fact]
    public async Task HeadReturnsMetadataOnly()
    {
        var (response, body) = await _t.SendAsync("HEAD", _t.In("/hello.txt"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(body);
        Assert.Equal(2, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task GetHonoursByteRanges()
    {
        _t.Provider.AddFile("/digits.txt", "0123456789");
        var (partial, body) = await _t.SendAsync("GET", _t.In("/digits.txt"), H(("Range", "bytes=2-4")));
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("234", body);
        Assert.Equal("bytes 2-4/10", partial.Content.Headers.ContentRange!.ToString());

        Assert.Equal("789", (await _t.SendAsync("GET", _t.In("/digits.txt"), H(("Range", "bytes=-3")))).Body);
        Assert.Equal("89", (await _t.SendAsync("GET", _t.In("/digits.txt"), H(("Range", "bytes=8-")))).Body);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, (await _t.SendAsync("GET", _t.In("/digits.txt"), H(("Range", "bytes=20-")))).Response.StatusCode);
    }

    [Fact]
    public async Task SecondGetComesFromTheReadCache()
    {
        await _t.SendAsync("GET", _t.In("/hello.txt"));
        Assert.Equal(2, _t.Cache.TotalBytes);

        // The cache answers while the provider's copy is unchanged …
        var version = $"{_t.Provider.ModifiedAt("/hello.txt").ToUnixTimeSeconds()}-2";
        Assert.NotNull(_t.Cache.Lookup("/hello.txt", version));

        // … and a write through the mount drops it, so the next GET sees the new bytes.
        await _t.SendAsync("PUT", _t.In("/hello.txt"), body: "fresh", contentType: "text/plain");
        Assert.Null(_t.Cache.Lookup("/hello.txt", version));
        Assert.Equal("fresh", (await _t.SendAsync("GET", _t.In("/hello.txt"))).Body);
    }

    [Fact]
    public async Task RepeatedGetsDownloadOnce()
    {
        await _t.SendAsync("GET", _t.In("/hello.txt"));
        await _t.SendAsync("GET", _t.In("/hello.txt"));
        Assert.Equal(1, _t.Wrapper.Downloads);
    }

    [Fact]
    public async Task ConcurrentGetsShareOneDownload()
    {
        _t.Provider.AddFile("/big.bin", new string('x', 200_000));
        _t.Wrapper.DownloadDelay = TimeSpan.FromMilliseconds(300);
        var bodies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _t.Http.GetStringAsync(_t.Url(_t.In("/big.bin")))));
        Assert.All(bodies, b => Assert.Equal(200_000, b.Length));
        Assert.Equal(1, _t.Wrapper.Downloads);
    }

    [Fact]
    public async Task FilesLargerThanTheCacheStillStream()
    {
        await using var small = await MountTestServer.StartAsync(p => p.AddFile("/large.bin", new string('y', 5000)), cacheBytes: 1000);
        Assert.Equal(5000, (await small.SendAsync("GET", small.In("/large.bin"))).Body.Length);
        Assert.Equal(5000, (await small.SendAsync("GET", small.In("/large.bin"))).Body.Length);
        Assert.Equal(0, small.Cache.TotalBytes);
        Assert.Equal(2, small.Wrapper.Downloads);
        Assert.Empty(Directory.GetFiles(small.Server.TempDirectory));
    }

    [Fact]
    public async Task ProbesAfterAListingDontReachTheProvider()
    {
        // Explorer: list the folder, then ask about each file and about desktop.ini /
        // autorun.inf-style names that usually don't exist.
        await _t.SendAsync("PROPFIND", _t.In("/"), H(("Depth", "1")));
        var before = _t.Wrapper.MetadataCalls;
        Assert.Equal(207, (int)(await _t.SendAsync("PROPFIND", _t.In("/hello.txt"), H(("Depth", "0")))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _t.SendAsync("PROPFIND", _t.In("/autorun.inf"), H(("Depth", "0")))).Response.StatusCode);
        Assert.Equal(before, _t.Wrapper.MetadataCalls);

        await _t.SendAsync("PROPFIND", _t.In("/"), H(("Depth", "1")));
        Assert.Equal(1, _t.Wrapper.Listings);
    }

    [Fact]
    public async Task QuotaComesFromTheProvider()
    {
        _t.Wrapper.AvailableSpace = 123_456_789;
        const string body = "<D:propfind xmlns:D=\"DAV:\"><D:prop><D:quota-available-bytes/><D:quota-used-bytes/></D:prop></D:propfind>";
        var (_, text) = await _t.SendAsync("PROPFIND", _t.In("/"), H(("Depth", "0")), body);
        var xml = XDocument.Parse(text);
        Assert.Equal("123456789", Propstat(xml.Descendants(D + "response").Single(), "200").Descendants(D + "quota-available-bytes").Single().Value);
    }

    // MARK: - PUT

    [Fact]
    public async Task PutCreatesFile()
    {
        var (response, _) = await _t.SendAsync("PUT", _t.In("/created.txt"), body: "written via PUT", contentType: "text/plain");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("written via PUT", _t.Provider.Read("/created.txt"));
    }

    [Fact]
    public async Task ZeroLengthPutCreatesAnEmptyFile()
    {
        // The redirector creates every new file with an empty PUT before writing it.
        var (response, _) = await _t.SendAsync("PUT", _t.In("/New Text Document.txt"), body: string.Empty, contentType: "text/plain");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(string.Empty, _t.Provider.Read("/New Text Document.txt"));
    }

    [Fact]
    public async Task PutOverExistingFileReplacesItWithoutLeftovers()
    {
        var (response, _) = await _t.SendAsync("PUT", _t.In("/hello.txt"), body: "replaced", contentType: "text/plain");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("replaced", _t.Provider.Read("/hello.txt"));
        Assert.DoesNotContain(_t.Provider.AllPaths, p => p.Contains(".~filefluss-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedOverwriteKeepsTheOriginal()
    {
        // Every upload fails — the sibling's included. The original must survive, and the
        // provider error maps to 502.
        _t.Wrapper.FailUpload = _ => true;
        var (response, _) = await _t.SendAsync("PUT", _t.In("/hello.txt"), body: "new", contentType: "text/plain");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("hi", _t.Provider.Read("/hello.txt"));
        Assert.DoesNotContain(_t.Provider.AllPaths, p => p.Contains(".~filefluss-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PutInPlaceWhenTheProviderReplacesNatively()
    {
        await using var inPlace = await MountTestServer.StartAsync(replacesInPlace: true);
        var (response, _) = await inPlace.SendAsync("PUT", inPlace.In("/hello.txt"), body: "direct", contentType: "text/plain");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("direct", inPlace.Provider.Read("/hello.txt"));
    }

    [Fact]
    public async Task PutIntoMissingFolderIsConflict()
    {
        var (response, _) = await _t.SendAsync("PUT", _t.In("/nowhere/file.txt"), body: "x", contentType: "text/plain");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ChunkedAndExpectContinuePutsArrive()
    {
        var payload = Encoding.UTF8.GetBytes(new string('z', 300_000));
        using var request = new HttpRequestMessage(HttpMethod.Put, _t.Url(_t.In("/chunked.bin")))
        {
            // Unknown length → Transfer-Encoding: chunked.
            Content = new StreamContent(new NonSeekableStream(payload)),
        };
        request.Headers.ExpectContinue = true;
        using var response = await _t.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(300_000, _t.Provider.Read("/chunked.bin").Length);
    }

    [Fact]
    public async Task PutDropsFileManagerSidecarsButServesThemBack()
    {
        foreach (var name in new[] { ".DS_Store", "._ghost", "desktop.ini", "Thumbs.db" })
        {
            var (response, _) = await _t.SendAsync("PUT", _t.In("/" + name), body: "meta:" + name, contentType: "application/octet-stream");
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.False(_t.Provider.Exists("/" + name), $"{name} must not reach the provider");
        }

        Assert.Equal("meta:desktop.ini", (await _t.SendAsync("GET", _t.In("/desktop.ini"))).Body);
        Assert.Equal(207, (int)(await _t.SendAsync("PROPFIND", _t.In("/desktop.ini"), H(("Depth", "0")))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _t.SendAsync("PROPFIND", _t.In("/folder/desktop.ini"), H(("Depth", "0")))).Response.StatusCode);

        // Never listed, never deleted from the cloud.
        var (_, listing) = await _t.SendAsync("PROPFIND", _t.In("/"), H(("Depth", "1")));
        Assert.DoesNotContain("desktop.ini", listing, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NoContent, (await _t.SendAsync("DELETE", _t.In("/desktop.ini"))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _t.SendAsync("GET", _t.In("/desktop.ini"))).Response.StatusCode);
    }

    // MARK: - MKCOL / DELETE

    [Fact]
    public async Task MkcolCreatesFolder()
    {
        var (response, _) = await _t.SendAsync("MKCOL", _t.In("/newdir/"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(_t.Provider.Exists("/newdir"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _t.SendAsync("MKCOL", _t.In("/newdir/"))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _t.SendAsync("MKCOL", _t.In("/a/b/"))).Response.StatusCode);
    }

    [Fact]
    public async Task DeleteRemovesFileAndFolder()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await _t.SendAsync("DELETE", _t.In("/hello.txt"))).Response.StatusCode);
        Assert.False(_t.Provider.Exists("/hello.txt"));

        Assert.Equal(HttpStatusCode.NoContent, (await _t.SendAsync("DELETE", _t.In("/folder/"))).Response.StatusCode);
        Assert.False(_t.Provider.Exists("/folder/inside.txt"));

        Assert.Equal(HttpStatusCode.NotFound, (await _t.SendAsync("DELETE", _t.In("/hello.txt"))).Response.StatusCode);
    }

    [Fact]
    public async Task ListingReflectsWritesImmediately()
    {
        await _t.SendAsync("PROPFIND", _t.In("/"), H(("Depth", "1")));
        await _t.SendAsync("PUT", _t.In("/late.txt"), body: "x", contentType: "text/plain");
        var (_, listing) = await _t.SendAsync("PROPFIND", _t.In("/"), H(("Depth", "1")));
        Assert.Contains("late.txt", listing);

        await _t.SendAsync("DELETE", _t.In("/late.txt"));
        Assert.Equal(HttpStatusCode.NotFound, (await _t.SendAsync("PROPFIND", _t.In("/late.txt"), H(("Depth", "0")))).Response.StatusCode);
    }

    // MARK: - MOVE / COPY

    [Fact]
    public async Task MoveRenamesWithinTheSameFolder()
    {
        var (response, _) = await _t.SendAsync("MOVE", _t.In("/hello.txt"), H(("Destination", _t.Url(_t.In("/hello_renamed.txt")).AbsoluteUri)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(_t.Provider.Exists("/hello.txt"));
        Assert.Equal("hi", _t.Provider.Read("/hello_renamed.txt"));
    }

    [Fact]
    public async Task MoveAcrossFolders()
    {
        var (response, _) = await _t.SendAsync("MOVE", _t.In("/hello.txt"), H(("Destination", _t.Url(_t.In("/folder/hello.txt")).AbsoluteUri)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(_t.Provider.Exists("/hello.txt"));
        Assert.Equal("hi", _t.Provider.Read("/folder/hello.txt"));
    }

    [Fact]
    public async Task MoveRespectsOverwrite()
    {
        _t.Provider.AddFile("/target.txt", "old");
        var refused = await _t.SendAsync("MOVE", _t.In("/hello.txt"), H(("Destination", _t.Url(_t.In("/target.txt")).AbsoluteUri), ("Overwrite", "F")));
        Assert.Equal(HttpStatusCode.PreconditionFailed, refused.Response.StatusCode);
        Assert.Equal("old", _t.Provider.Read("/target.txt"));

        var replaced = await _t.SendAsync("MOVE", _t.In("/hello.txt"), H(("Destination", _t.Url(_t.In("/target.txt")).AbsoluteUri), ("Overwrite", "T")));
        Assert.Equal(HttpStatusCode.NoContent, replaced.Response.StatusCode);
        Assert.Equal("hi", _t.Provider.Read("/target.txt"));
    }

    [Fact]
    public async Task MoveFallsBackToCopyAndDeleteForFolders()
    {
        _t.Provider.SupportsServerSide = false;
        _t.Provider.AddFolder("/dest");
        var (response, _) = await _t.SendAsync("MOVE", _t.In("/folder"), H(("Destination", _t.Url(_t.In("/dest/folder")).AbsoluteUri)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(_t.Provider.Exists("/folder"));
        Assert.Equal("nested", _t.Provider.Read("/dest/folder/inside.txt"));
    }

    [Fact]
    public async Task MoveOutOfTheMountIsRefused()
    {
        var outside = await _t.SendAsync("MOVE", _t.In("/hello.txt"), H(("Destination", _t.Url("/elsewhere.txt").AbsoluteUri)));
        Assert.Equal(HttpStatusCode.Forbidden, outside.Response.StatusCode);
        var otherServer = await _t.SendAsync("MOVE", _t.In("/hello.txt"), H(("Destination", "http://example.com" + _t.In("/x.txt"))));
        Assert.Equal(HttpStatusCode.BadGateway, otherServer.Response.StatusCode);
        Assert.True(_t.Provider.Exists("/hello.txt"));
    }

    [Fact]
    public async Task CopyUsesServerSideCopyOrFallsBack()
    {
        var (response, _) = await _t.SendAsync("COPY", _t.In("/hello.txt"), H(("Destination", _t.Url(_t.In("/copy.txt")).AbsoluteUri)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, _t.Provider.ServerSideCalls);

        _t.Provider.SupportsServerSide = false;
        var (fallback, _) = await _t.SendAsync("COPY", _t.In("/folder"), H(("Destination", _t.Url(_t.In("/folder2")).AbsoluteUri)));
        Assert.Equal(HttpStatusCode.Created, fallback.StatusCode);
        Assert.Equal("nested", _t.Provider.Read("/folder2/inside.txt"));
        Assert.Equal("nested", _t.Provider.Read("/folder/inside.txt"));
    }

    // MARK: - LOCK / UNLOCK

    private const string LockBody = """
        <?xml version="1.0" encoding="utf-8" ?>
        <D:lockinfo xmlns:D="DAV:"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype><D:owner><D:href>robert</D:href></D:owner></D:lockinfo>
        """;

    [Fact]
    public async Task LockReturnsTokenAndRefreshKeepsIt()
    {
        var (response, body) = await _t.SendAsync("LOCK", _t.In("/hello.txt"), H(("Timeout", "Second-600")), LockBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = response.Headers.GetValues("Lock-Token").Single();
        Assert.StartsWith("<urn:uuid:", token);
        var xml = XDocument.Parse(body);
        Assert.NotNull(xml.Descendants(D + "exclusive").SingleOrDefault());
        Assert.Equal("Second-600", xml.Descendants(D + "timeout").Single().Value);
        Assert.Equal("robert", xml.Descendants(D + "owner").Single().Value);

        var (refresh, refreshBody) = await _t.SendAsync("LOCK", _t.In("/hello.txt"), H(("If", "(" + token + ")"), ("Timeout", "Second-3600")));
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Equal(token, refresh.Headers.GetValues("Lock-Token").Single());
        Assert.Contains(token.Trim('<', '>'), refreshBody);

        var (_, discovery) = await _t.SendAsync("PROPFIND", _t.In("/hello.txt"), H(("Depth", "0")), "<D:propfind xmlns:D=\"DAV:\"><D:prop><D:lockdiscovery/></D:prop></D:propfind>");
        Assert.Contains(token.Trim('<', '>'), discovery);

        Assert.Equal(HttpStatusCode.NoContent, (await _t.SendAsync("UNLOCK", _t.In("/hello.txt"), H(("Lock-Token", token)))).Response.StatusCode);
        var (stale, _) = await _t.SendAsync("LOCK", _t.In("/hello.txt"), H(("If", "(" + token + ")")));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task UnlockOfUnknownTokenStillSucceeds()
    {
        var (response, _) = await _t.SendAsync("UNLOCK", _t.In("/hello.txt"), H(("Lock-Token", "<urn:uuid:test>")));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task LockOnUnmappedUrlCreatesAnEmptyFile()
    {
        var (response, _) = await _t.SendAsync("LOCK", _t.In("/new.docx"), body: LockBody);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(string.Empty, _t.Provider.Read("/new.docx"));
    }

    // MARK: - PROPPATCH

    [Fact]
    public async Task ProppatchStampsTheModificationTimeAndEchoesEveryProperty()
    {
        const string body = """
            <?xml version="1.0" encoding="utf-8" ?>
            <D:propertyupdate xmlns:D="DAV:" xmlns:Z="urn:schemas-microsoft-com:"><D:set><D:prop>
            <Z:Win32CreationTime>Mon, 02 Feb 2026 10:00:00 GMT</Z:Win32CreationTime>
            <Z:Win32LastAccessTime>Tue, 03 Feb 2026 10:00:00 GMT</Z:Win32LastAccessTime>
            <Z:Win32LastModifiedTime>Wed, 04 Feb 2026 10:00:00 GMT</Z:Win32LastModifiedTime>
            <Z:Win32FileAttributes>00000020</Z:Win32FileAttributes>
            </D:prop></D:set></D:propertyupdate>
            """;
        var (response, text) = await _t.SendAsync("PROPPATCH", _t.In("/hello.txt"), body: body);
        Assert.Equal(207, (int)response.StatusCode);
        var ok = Propstat(XDocument.Parse(text).Descendants(D + "response").Single(), "200");
        Assert.Equal(
            ["Win32CreationTime", "Win32LastAccessTime", "Win32LastModifiedTime", "Win32FileAttributes"],
            ok.Element(D + "prop")!.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(new DateTimeOffset(2026, 2, 4, 10, 0, 0, TimeSpan.Zero), _t.Provider.ModifiedAt("/hello.txt"));

        var (_, listing) = await _t.SendAsync("PROPFIND", _t.In("/hello.txt"), H(("Depth", "0")));
        Assert.Contains("Wed, 04 Feb 2026 10:00:00 GMT", listing);
    }

    // MARK: - Connection handling

    [Fact]
    public async Task KeepAliveServesSeveralRequestsOnOneConnection()
    {
        var request = $"GET {_t.Root}/hello.txt HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
        var answer = await RawAsync(request + request + $"GET {_t.Root}/hello.txt HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        Assert.Equal(3, answer.Split("HTTP/1.1 200 OK").Length - 1);
        Assert.Contains("Connection: close", answer);
    }

    [Fact]
    public async Task StopReleasesThePort()
    {
        var port = _t.Server.Port;
        await _t.Server.StopAsync();
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
    }

    // MARK: - Helpers

    private async Task<string> RawAsync(string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _t.Server.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await reader.ReadToEndAsync(timeout.Token);
    }

    private static XElement Response(XDocument xml, string href)
        => xml.Descendants(D + "response").Single(r => r.Element(D + "href")!.Value == href);

    private static XElement Propstat(XElement response, string status)
        => response.Elements(D + "propstat").Single(p => p.Element(D + "status")!.Value.Contains(" " + status + " ", StringComparison.Ordinal));

    /// <summary>A stream without a length, so HttpClient has to send it chunked.</summary>
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();
    }
}
