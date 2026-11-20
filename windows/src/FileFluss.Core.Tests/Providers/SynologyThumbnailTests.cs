// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.SynologyDrive;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// The request behind a Synology gallery tile: <c>SYNO.FileStation.Thumb</c> version 2, method
/// <c>get</c>, with the path JSON-quoted as in the API guide (the macOS
/// <c>SynologyThumbnailTests</c>).
/// </summary>
public sealed class SynologyThumbnailTests
{
    private static readonly Guid AccountId = Guid.Parse("8d0b6c52-3c1f-4f55-a0f4-2f1c5d3f7e10");

    private readonly FakeHttpHandler _http = new();
    private readonly MemoryCredentialStore _store = new();
    private int _logins;

    public SynologyThumbnailTests()
        => _http.On(HttpMethod.Get, "/webapi/auth.cgi", (_, _) => FakeHttpHandler.Json($"{{\"success\":true,\"data\":{{\"sid\":\"sid-{Interlocked.Increment(ref _logins)}\"}}}}"));

    private SynologyDriveProvider Connected()
    {
        _store.Save($"synologyDrive.{AccountId}", new SynologyDriveCredentials("https://nas.local:5001", "robert", "pw", true, "robert@nas.local"));
        return new SynologyDriveProvider(AccountId, _store, _http);
    }

    private static CloudFileItem File(string path) => new(path, path[(path.LastIndexOf('/') + 1)..], path, false, 10, CloudFileItem.UnknownDate);

    private static HttpResponseMessage Image(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Fail(int code) => FakeHttpHandler.Json($"{{\"success\":false,\"error\":{{\"code\":{code}}}}}");

    private List<FakeHttpHandler.RecordedRequest> ThumbCalls()
        => _http.Requests.Where(r => HttpUtility.ParseQueryString(r.Uri.Query)["api"] == "SYNO.FileStation.Thumb").ToList();

    // MARK: - Helpers

    [Theory]
    [InlineData(64, "small")]
    [InlineData(120, "small")]
    [InlineData(190, "medium")]
    [InlineData(290, "medium")]
    [InlineData(320, "medium")]
    [InlineData(600, "large")]
    public void TileSizesMapOntoDsmsNamedThumbnailSizes(int maxPixelSize, string size)
        => Assert.Equal(size, SynologyDriveApiClient.ThumbnailSize(maxPixelSize));

    [Fact]
    public void TheQueryNamesTheThumbApiAndQuotesThePath()
    {
        var query = SynologyDriveApiClient.ThumbnailQuery("/photo/test.jpg", "medium").ToDictionary(q => q.Name, q => q.Value);
        Assert.Equal("SYNO.FileStation.Thumb", query["api"]);
        Assert.Equal("2", query["version"]);
        Assert.Equal("get", query["method"]);
        Assert.Equal("\"/photo/test.jpg\"", query["path"]);
        Assert.Equal("medium", query["size"]);

        // The session id is added by the client, never part of the shared part.
        Assert.False(query.ContainsKey("_sid"));
    }

    [Fact]
    public void QuotesAndBackslashesInAPathStayInsideTheJsonString()
    {
        var query = SynologyDriveApiClient.ThumbnailQuery("/photo/say \"hi\"\\.jpg", "small");
        Assert.Equal("\"/photo/say \\\"hi\\\"\\\\.jpg\"", query.Single(q => q.Name == "path").Value);
    }

    [Fact]
    public void APlusSignInTheQueryIsEscapedSoDsmDoesntReadASpace()
    {
        var raw = SynologyDriveApiClient.EncodeQuery(SynologyDriveApiClient.ThumbnailQuery("/photo/a+b.jpg", "medium"));
        Assert.DoesNotContain("+", raw, StringComparison.Ordinal);
        Assert.Contains("a%2Bb.jpg", raw, StringComparison.Ordinal);
        Assert.Equal("\"/photo/a+b.jpg\"", HttpUtility.ParseQueryString(raw)["path"]);
    }

    [Theory]
    [InlineData("""{"success":false,"error":{"code":119}}""", 119)]
    [InlineData("""{"success":false,"error":{"code":408}}""", 408)]
    [InlineData("""{"success":false}""", -1)]
    [InlineData("""{"success":true,"data":{}}""", -1)]
    [InlineData("""{"other":1}""", null)]
    [InlineData("<html></html>", null)]
    public void TheEnvelopeErrorCodeIsReadOnlyFromAnEnvelope(string body, int? code)
        => Assert.Equal(code, SynologyDriveApiClient.EnvelopeErrorCode(Encoding.UTF8.GetBytes(body)));

    // MARK: - Requests

    [Fact]
    public async Task AThumbnailComesBackWithTheSessionIdInTheQuery()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF };
        _http.On(HttpMethod.Get, "/webapi/entry.cgi", (_, _) => Image(jpeg));
        using var provider = Connected();

        Assert.Equal(jpeg, await provider.ThumbnailAsync(File("/photo/a+b.jpg"), 300));

        var query = HttpUtility.ParseQueryString(Assert.Single(ThumbCalls()).Uri.Query);
        Assert.Equal("sid-1", query["_sid"]);
        Assert.Equal("\"/photo/a+b.jpg\"", query["path"]);
        Assert.Equal("medium", query["size"]);
    }

    [Fact]
    public async Task NotFoundMeansNoThumbnail()
    {
        _http.On(HttpMethod.Get, "/webapi/entry.cgi", (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var provider = Connected();
        Assert.Null(await provider.ThumbnailAsync(File("/home/clip.mov"), 256));
    }

    [Fact]
    public async Task AnExpiredSessionLogsInAgainOnce()
    {
        var jpeg = new byte[] { 0xFF, 0xD8 };
        _http.On(HttpMethod.Get, "/webapi/entry.cgi", (request, _) =>
            HttpUtility.ParseQueryString(request.RequestUri!.Query)["_sid"] == "sid-1" ? Fail(119) : Image(jpeg));
        using var provider = Connected();

        Assert.Equal(jpeg, await provider.ThumbnailAsync(File("/photo/a.jpg"), 256));
        Assert.Equal(2, _logins);
        Assert.Equal(2, ThumbCalls().Count);
    }

    [Fact]
    public async Task ASessionThatStaysExpiredIsNotAuthenticated()
    {
        _http.On(HttpMethod.Get, "/webapi/entry.cgi", (_, _) => Fail(119));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ThumbnailAsync(File("/photo/a.jpg"), 256));
        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
        Assert.Equal(2, ThumbCalls().Count);
    }

    [Fact]
    public async Task OtherRefusalsAreAboutTheFile()
    {
        _http.On(HttpMethod.Get, "/webapi/entry.cgi", (_, _) => Fail(408));
        using var provider = Connected();
        Assert.Null(await provider.ThumbnailAsync(File("/photo/gone.jpg"), 256));
    }

    [Fact]
    public async Task FoldersAreNeverAsked()
    {
        using var provider = Connected();
        Assert.Null(await provider.ThumbnailAsync(new CloudFileItem("/photo", "photo", "/photo", true, 0, CloudFileItem.UnknownDate), 256));
        Assert.Empty(_http.Requests);
    }
}
