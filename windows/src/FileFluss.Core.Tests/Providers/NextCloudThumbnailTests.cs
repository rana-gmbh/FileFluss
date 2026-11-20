// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.NextCloud;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// The request behind a Nextcloud gallery tile: <c>core/preview.png</c> with the file's path,
/// a fixed preview size and the aspect ratio kept (the macOS <c>NextcloudThumbnailTests</c>).
/// </summary>
public sealed class NextCloudThumbnailTests
{
    private const string Server = "https://cloud.example.com";

    private readonly FakeHttpHandler _http = new();
    private readonly MemoryCredentialStore _store = new();
    private readonly Guid _id = Guid.NewGuid();

    private NextCloudProvider Connected()
    {
        _store.Save($"nextcloud.{_id}", new NextCloudCredentials(Server, "jane@example.com", "app-pw", "Jane Doe", "jane.doe"));
        return new NextCloudProvider(_id, _store, _http);
    }

    private static CloudFileItem File(string path) => new(path, path[(path.LastIndexOf('/') + 1)..], path, false, 10, CloudFileItem.UnknownDate);

    private static HttpResponseMessage Image(byte[] bytes, string mediaType = "image/png")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // MARK: - Helpers

    [Theory]
    [InlineData(64, 256)]
    [InlineData(190, 256)]
    [InlineData(256, 256)]
    [InlineData(290, 512)]
    [InlineData(512, 512)]
    [InlineData(600, 1024)]
    public void TileSizesSnapToThePreviewSizesTheServerKeeps(int maxPixelSize, int edge)
        => Assert.Equal(edge, NextCloudApi.PreviewEdge(maxPixelSize));

    [Fact]
    public void ThePreviewUrlNamesTheFileByPathAndKeepsTheAspectRatio()
    {
        var url = new Uri(NextCloudApi.PreviewUrl("https://cloud.example.com/", "/Photos/Summer 2026/IMG_0001.jpg", 256));
        Assert.Equal("cloud.example.com", url.Host);
        Assert.Equal("/index.php/core/preview.png", url.AbsolutePath);
        var query = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("/Photos/Summer 2026/IMG_0001.jpg", query["file"]);
        Assert.Equal("256", query["x"]);
        Assert.Equal("256", query["y"]);
        Assert.Equal("1", query["a"]);
        Assert.Equal("0", query["forceIcon"]);
    }

    [Fact]
    public void AServerInstalledInASubfolderKeepsItsPrefix()
    {
        var url = new Uri(NextCloudApi.PreviewUrl("https://example.com/nextcloud", "a.png", 512));
        Assert.Equal("/nextcloud/index.php/core/preview.png", url.AbsolutePath);
        Assert.Equal("/a.png", HttpUtility.ParseQueryString(url.Query)["file"]);
    }

    [Fact]
    public void CharactersThatWouldSplitOrAlterTheQueryAreEscaped()
    {
        var url = new Uri(NextCloudApi.PreviewUrl(Server, "/A&B/x+y=z#1.jpg", 256));
        Assert.Contains("%2B", url.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("+", url.Query, StringComparison.Ordinal);
        Assert.Equal(string.Empty, url.Fragment);
        Assert.Equal("/A&B/x+y=z#1.jpg", HttpUtility.ParseQueryString(url.Query)["file"]);
    }

    // MARK: - Requests

    [Fact]
    public async Task APreviewComesBackWithBasicAuthAndNoCookies()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        _http.On(HttpMethod.Get, "/index.php/core/preview.png", (_, _) =>
        {
            var response = Image(png);
            response.Headers.TryAddWithoutValidation("Set-Cookie", "nc_session_id=abc; path=/");
            return response;
        });
        using var provider = Connected();

        Assert.Equal(png, await provider.ThumbnailAsync(File("/Photos/a+b.jpg"), 300));
        Assert.Equal(png, await provider.ThumbnailAsync(File("/Photos/a+b.jpg"), 300));

        var requests = _http.Requests.ToList();
        Assert.Equal(2, requests.Count);
        var query = HttpUtility.ParseQueryString(requests[0].Uri.Query);
        Assert.Equal("/Photos/a+b.jpg", query["file"]);
        Assert.Equal("512", query["x"]);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("jane@example.com:app-pw")), requests[0].Headers["Authorization"]);
        Assert.All(requests, r => Assert.False(r.Headers.ContainsKey("Cookie")));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task NoPreviewForThisFileIsNull(HttpStatusCode status)
    {
        _http.On(HttpMethod.Get, "/index.php/core/preview.png", (_, _) => new HttpResponseMessage(status));
        using var provider = Connected();
        Assert.Null(await provider.ThumbnailAsync(File("/clip.mov"), 256));
    }

    [Fact]
    public async Task ALoginPageInsteadOfAnImageIsNull()
    {
        _http.OnText(HttpMethod.Get, "/index.php/core/preview.png", "<html>login</html>", mediaType: "text/html");
        using var provider = Connected();
        Assert.Null(await provider.ThumbnailAsync(File("/a.jpg"), 256));
    }

    [Fact]
    public async Task AServerFailureSurfacesLikeOtherCalls()
    {
        _http.On(HttpMethod.Get, "/index.php/core/preview.png", (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var provider = Connected();
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ThumbnailAsync(File("/a.jpg"), 256));
        Assert.Equal(CloudErrorKind.ServerError, error.Kind);
    }

    [Fact]
    public async Task FoldersAreNeverAsked()
    {
        using var provider = Connected();
        var folder = new CloudFileItem("/Photos", "Photos", "/Photos", true, 0, CloudFileItem.UnknownDate);
        Assert.Null(await provider.ThumbnailAsync(folder, 256));
        Assert.Empty(_http.Requests);
    }
}
