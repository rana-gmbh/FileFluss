// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.OneDrive;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>OneDrive's Graph thumbnails: size choice, item addressing and the one-step fallback, against scripted HTTP.</summary>
public sealed class OneDriveThumbnailTests
{
    private const string Graph = "https://graph.microsoft.com/v1.0";

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 4, 5, 6];

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();

    private OneDriveProvider Provider()
    {
        _store.Save($"onedrive.{_accountId}", new OneDriveCredentials("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1), "Me"));
        return new OneDriveProvider(_accountId, _store, _http);
    }

    private static CloudFileItem File(string id = "fABC!123", string path = "/Photos/a b.jpg") => new(id, "a b.jpg", path, false, 1000, DateTimeOffset.UnixEpoch);

    // MARK: - Helpers

    [Theory]
    [InlineData(1, "c96x96")]
    [InlineData(96, "c96x96")]
    [InlineData(256, "c256x256")]
    [InlineData(320, "c320x320")]
    [InlineData(800, "c800x800")]
    [InlineData(4000, "c800x800")]
    public void CustomSizeIsABoxOfTheTileWithinGraphsRange(int maxPixelSize, string expected)
        => Assert.Equal(expected, OneDriveApiClient.ThumbnailSize(maxPixelSize));

    [Theory]
    [InlineData(64, "small")]
    [InlineData(96, "small")]
    [InlineData(97, "medium")]
    [InlineData(176, "medium")]
    [InlineData(177, "large")]
    [InlineData(320, "large")]
    public void FallbackIsTheSmallestStandardSizeCoveringTheTile(int maxPixelSize, string expected)
        => Assert.Equal(expected, OneDriveApiClient.StandardThumbnailSize(maxPixelSize));

    [Theory]
    [InlineData("fABC!123", "/x.jpg", "/me/drive/items/ABC!123")]
    [InlineData("fa/b", "/x.jpg", "/me/drive/items/a%2Fb")]
    [InlineData("dABC", "/Photos/a b.jpg", "/me/drive/root:/Photos/a%20b.jpg:")]
    [InlineData("/Photos/a b.jpg", "/Photos/a b.jpg", "/me/drive/root:/Photos/a%20b.jpg:")]
    public void TheItemIsAddressedByIdWhenListedElseByPath(string itemId, string path, string expected)
        => Assert.Equal(expected, OneDriveApiClient.ThumbnailItemEndpoint(itemId, path));

    // MARK: - Requests

    [Fact]
    public async Task AsksForTheCustomSizeByItemId()
    {
        var provider = Provider();
        _http.OnBytes(HttpMethod.Get, $"{Graph}/me/drive/items/ABC!123/thumbnails/0/c256x256/content", Jpeg);

        Assert.Equal(Jpeg, await provider.ThumbnailAsync(File(), 256));
        Assert.Equal("Bearer access-1", _http.Requests.Single().Headers["Authorization"]);
    }

    [Fact]
    public async Task ARefusedCustomSizeFallsBackToAStandardOneOnce()
    {
        var provider = Provider();
        _http.OnText(HttpMethod.Get, "/c320x320/content", "{}", HttpStatusCode.BadRequest);
        _http.OnBytes(HttpMethod.Get, "/thumbnails/0/large/content", Jpeg);

        Assert.Equal(Jpeg, await provider.ThumbnailAsync(File(), 320));
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task BothSizesRefusedIsNoPreview()
    {
        var provider = Provider();
        _http.OnText(HttpMethod.Get, "/thumbnails/0/", "{}", HttpStatusCode.NotFound);

        Assert.Null(await provider.ThumbnailAsync(File(), 256));
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task AServerErrorSurfacesLikeOtherGraphCalls()
    {
        var provider = Provider();
        _http.OnText(HttpMethod.Get, "/thumbnails/0/", "{}", HttpStatusCode.ServiceUnavailable);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ThumbnailAsync(File(), 256));
        Assert.Equal(CloudErrorKind.ServerError, error.Kind);
    }

    [Fact]
    public async Task AnAnswerThatIsNotAnImageIsNoPreview()
    {
        var provider = Provider();
        _http.OnText(HttpMethod.Get, "/thumbnails/0/", "<html></html>", mediaType: "text/html");

        Assert.Null(await provider.ThumbnailAsync(File(), 256));
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task FoldersAskNothing()
    {
        var provider = Provider();

        Assert.Null(await provider.ThumbnailAsync(new CloudFileItem("dABC", "Photos", "/Photos", true, 0, DateTimeOffset.UnixEpoch), 256));
        Assert.Empty(_http.Requests);
    }
}
