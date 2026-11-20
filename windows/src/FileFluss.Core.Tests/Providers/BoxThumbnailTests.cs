// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Box;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>Server-side thumbnails from <c>/files/{id}/thumbnail.jpg</c> — the macOS BoxThumbnailTests, plus the request on the wire.</summary>
public sealed class BoxThumbnailTests
{
    private const string Thumbnail = "api.box.com/2.0/files/123/thumbnail.jpg";

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();

    // MARK: - Helpers

    [Theory]
    [InlineData(20, 32)]
    [InlineData(32, 32)]
    [InlineData(100, 160)]
    [InlineData(190, 320)]
    [InlineData(290, 320)]
    [InlineData(1000, 320)]
    public void TheSmallestJpegSizeReachingTheTileIsChosenAtMost320(int maxPixelSize, int expected)
        => Assert.Equal(expected, BoxApiClient.ThumbnailSize(maxPixelSize));

    [Fact]
    public void OnlyLowerBoundsAreSent()
        => Assert.Equal(
            "https://api.box.com/2.0/files/123/thumbnail.jpg?min_width=320&min_height=320",
            BoxApiClient.ThumbnailUrl("123", 240));

    [Theory]
    [InlineData(404, "preview_cannot_be_generated", true)]
    [InlineData(400, "requested_preview_unavailable", true)]
    [InlineData(404, "not_found", false)]
    [InlineData(400, "bad_request", false)]
    [InlineData(500, "preview_cannot_be_generated", false)]
    public void BoxsNoPreviewCodesCountAsNoPreviewOtherErrorsDont(int status, string code, bool refusal)
        => Assert.Equal(refusal, BoxApiClient.IsThumbnailRefusal(status, $$"""{"type":"error","status":{{status}},"code":"{{code}}"}"""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>Not Found</html>")]
    public void AnUnreadableBodyIsNoRefusal(string? body)
        => Assert.False(BoxApiClient.IsThumbnailRefusal(404, body));

    // MARK: - On the wire

    [Fact]
    public async Task ThePictureIsFetchedByTheListedFilesId()
    {
        _http.OnBytes(HttpMethod.Get, Thumbnail, [0xFF, 0xD8, 0xFF]);
        using var provider = Provider();

        var bytes = await provider.ThumbnailAsync(File(), 256);

        Assert.Equal([0xFF, 0xD8, 0xFF], bytes);
        var request = _http.Single(HttpMethod.Get, Thumbnail);
        Assert.Equal("Bearer access-1", request.Headers["Authorization"]);
        Assert.Equal("?min_width=320&min_height=320", request.Uri.Query);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.Found)]
    public async Task APlaceholderIsNoPreview(HttpStatusCode status)
    {
        _http.On(HttpMethod.Get, Thumbnail, (_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Headers.Location = new Uri("https://cdn01.boxcdn.net/placeholder.png");
            return response;
        });
        using var provider = Provider();

        Assert.Null(await provider.ThumbnailAsync(File(), 256));
    }

    [Fact]
    public async Task ATypeBoxCantDrawIsNoPreview()
    {
        _http.OnJson(HttpMethod.Get, Thumbnail, """{"type":"error","status":404,"code":"preview_cannot_be_generated"}""", HttpStatusCode.NotFound);
        using var provider = Provider();

        Assert.Null(await provider.ThumbnailAsync(File(), 256));
    }

    [Fact]
    public async Task AMissingFileIsStillAnError()
    {
        _http.OnJson(HttpMethod.Get, Thumbnail, """{"type":"error","status":404,"code":"not_found"}""", HttpStatusCode.NotFound);
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ThumbnailAsync(File(), 256));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task FoldersAreNeverAsked()
    {
        using var provider = Provider();

        Assert.Null(await provider.ThumbnailAsync(new CloudFileItem("/Photos", "Photos", "/Photos", true, 0, CloudFileItem.UnknownDate), 256));
        Assert.Empty(_http.Requests);
    }

    /// <summary>A file whose id the client already knows, as it would after listing its folder.</summary>
    private static CloudFileItem File() => new("/a.jpg", "a.jpg", "/a.jpg", false, 10, CloudFileItem.UnknownDate);

    private BoxProvider Provider()
    {
        _store.Save($"box.{_accountId}", new BoxCredentials("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1), "someone@example.com", "Someone"));
        var provider = new BoxProvider(_accountId, _store, _http);
        provider.ApiClient!.PathIdCache["/a.jpg"] = "123";
        return provider;
    }
}
