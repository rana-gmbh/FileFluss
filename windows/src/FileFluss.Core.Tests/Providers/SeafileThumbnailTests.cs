// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Http.Headers;
using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Providers.Seafile;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// The request behind a Seafile gallery tile:
/// <c>/api2/repos/&lt;repo&gt;/thumbnail/?p=&lt;path in library&gt;&amp;size=&lt;n&gt;</c>
/// (the macOS <c>SeafileThumbnailTests</c>).
/// </summary>
public sealed class SeafileThumbnailTests
{
    private const string Server = "https://seafile.example.test";
    private const string Libraries = """
        [{"id":"aaaaaaaa-1111","name":"My Library","encrypted":false,"mtime":1700000000,"size":1234}]
        """;

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();

    private SeafileProvider Signed()
    {
        _store.Save($"seafile.{_accountId}", new SeafileCredentials(Server, "me@example.com", "tok-1"));
        return new SeafileProvider(_accountId, _store, _http);
    }

    private static CloudFileItem File(string path) => new(path, path[(path.LastIndexOf('/') + 1)..], path, false, 10, CloudFileItem.UnknownDate);

    /// <summary>The thumbnail route goes first: "/api2/repos/" would match it too.</summary>
    private void Serve(Func<HttpRequestMessage, HttpResponseMessage> thumbnail)
    {
        _http.On(HttpMethod.Get, "/thumbnail/", (request, _) => thumbnail(request));
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
    }

    private static HttpResponseMessage Image(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // MARK: - Helpers

    [Theory]
    [InlineData(48, 256)]
    [InlineData(190, 256)]
    [InlineData(256, 256)]
    [InlineData(290, 512)]
    [InlineData(512, 512)]
    [InlineData(800, 1024)]
    public void TileSizesSnapToTheSizesSeahubKeepsOnDisk(int maxPixelSize, int size)
        => Assert.Equal(size, SeafileProvider.ThumbnailSize(maxPixelSize));

    [Fact]
    public void TheQueryCarriesThePathInsideTheLibraryAndTheSize()
        => Assert.Equal("p=/Photos/IMG_0001.jpg&size=256", SeafileProvider.ThumbnailQuery("/Photos/IMG_0001.jpg", 256));

    [Fact]
    public void CharactersThatWouldSplitOrAlterTheQueryAreEscaped()
    {
        var query = SeafileProvider.ThumbnailQuery("/A&B/x+y=z #1.jpg", 512);
        Assert.Equal("p=/A%26B/x%2By%3Dz%20%231.jpg&size=512", query);
        var parsed = HttpUtility.ParseQueryString(query);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("/A&B/x+y=z #1.jpg", parsed["p"]);
    }

    // MARK: - Requests

    [Fact]
    public async Task AThumbnailIsFetchedFromTheFilesLibraryWithTheToken()
    {
        var png = new byte[] { 1, 2, 3 };
        Serve(_ => Image(png));
        using var provider = Signed();

        Assert.Equal(png, await provider.ThumbnailAsync(File("/My Library/Photos/a+b.jpg"), 300));

        var request = _http.Single(HttpMethod.Get, "/thumbnail/");
        Assert.Equal("/api2/repos/aaaaaaaa-1111/thumbnail/", request.Uri.AbsolutePath);
        Assert.Equal("?p=/Photos/a%2Bb.jpg&size=512", request.Uri.Query);
        Assert.Equal("Token tok-1", request.Headers["Authorization"]);
        // JSON at lower priority, so Seahub can send a refusal as its real 400 instead of a 406.
        Assert.Equal("image/*,application/json; q=0.5", request.Headers["Accept"]);
    }

    [Fact]
    public async Task TheLibraryListAndLibrariesThemselvesHaveNoThumbnail()
    {
        Serve(_ => throw new InvalidOperationException("no thumbnail request expected"));
        using var provider = Signed();

        Assert.Null(await provider.ThumbnailAsync(File("/My Library"), 256));
        Assert.Null(await provider.ThumbnailAsync(new CloudFileItem("lib:aaaaaaaa-1111", "My Library", "/My Library", true, 0, CloudFileItem.UnknownDate), 256));
        Assert.DoesNotContain(_http.Requests, r => r.Uri.AbsolutePath.Contains("/thumbnail/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotAcceptable)]
    public async Task NoThumbnailForThisFileIsNull(HttpStatusCode status)
    {
        Serve(_ => FakeHttpHandler.Json("""{"error_msg":"Invalid file type."}""", status));
        using var provider = Signed();
        Assert.Null(await provider.ThumbnailAsync(File("/My Library/clip.mov"), 256));
    }

    [Fact]
    public async Task ANonImageAnswerIsNull()
    {
        Serve(_ => FakeHttpHandler.Json("""{"detail":"not an image"}"""));
        using var provider = Signed();
        Assert.Null(await provider.ThumbnailAsync(File("/My Library/a.jpg"), 256));
    }
}
