// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.PCloud;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>Server-side thumbnails from <c>getthumb</c> — the macOS PCloudThumbnailTests, plus the request on the wire.</summary>
public sealed class PCloudThumbnailTests
{
    private const string Endpoint = "eapi.pcloud.com/getthumb";

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();

    // MARK: - Helpers

    [Theory]
    [InlineData(190, 190)]
    [InlineData(191, 192)]
    [InlineData(193, 195)]
    [InlineData(289, 290)]
    [InlineData(1, 16)]
    [InlineData(5000, 1024)]
    public void TheBoxEdgeIsTheFirstSizePCloudAcceptsFromTheTileUp(int maxPixelSize, int expected)
        => Assert.Equal(expected, PCloudApiClient.ThumbnailEdge(maxPixelSize));

    [Fact]
    public void EveryEdgeIsOnePCloudAccepts()
    {
        for (var size = 0; size <= 1100; size++)
        {
            var edge = PCloudApiClient.ThumbnailEdge(size);
            Assert.True(edge % 4 == 0 || edge % 5 == 0, $"{edge} for {size}");
            Assert.InRange(edge, 16, 1024);
            Assert.True(edge >= Math.Min(size, 1024), $"{edge} for {size}");
        }
    }

    [Theory]
    [InlineData("f123", 123UL)]
    [InlineData("f0", null)]
    [InlineData("d42", null)]
    [InlineData("/a/b.jpg", null)]
    [InlineData("f", null)]
    [InlineData("f-1", null)]
    [InlineData("f 1", null)]
    public void AListedFilesIdIsUsedAnythingElseFallsBackToThePath(string itemId, ulong? expected)
        => Assert.Equal(expected, PCloudApiClient.FileId(itemId));

    [Fact]
    public void TheParametersNameTheFileByIdWhenThereIsOne()
    {
        Assert.Equal([("fileid", "7"), ("size", "240x240")], PCloudApiClient.ThumbnailParameters(7, "/a.jpg", 240));
        Assert.Equal([("path", "/a.jpg"), ("size", "244x244")], PCloudApiClient.ThumbnailParameters(null, "/a.jpg", 241));
    }

    [Theory]
    [InlineData(1014, true)]
    [InlineData(3001, true)]
    [InlineData(2009, false)]
    [InlineData(1015, false)]
    [InlineData(1000, false)]
    public void UnsupportedTypesAndFailedDrawingsCountAsNoPreview(int code, bool refusal)
        => Assert.Equal(refusal, PCloudApiClient.IsThumbnailRefusal(code));

    // MARK: - On the wire

    [Fact]
    public async Task ThePictureIsFetchedOnTheAccountsHostWithTheTokenLikeEveryOtherCall()
    {
        _http.OnBytes(HttpMethod.Get, Endpoint, [0xFF, 0xD8, 0xFF]);
        using var provider = Provider();

        var bytes = await provider.ThumbnailAsync(File("f77", "/Photos/a b+c.jpg"), 256);

        Assert.Equal([0xFF, 0xD8, 0xFF], bytes);
        var query = HttpUtility.ParseQueryString(_http.Single(HttpMethod.Get, Endpoint).Uri.Query);
        Assert.Equal("TOKEN", query["auth"]);
        Assert.Equal("77", query["fileid"]);
        Assert.Null(query["path"]);
        Assert.Equal("256x256", query["size"]);
    }

    [Fact]
    public async Task AnItemWithoutAFileIdIsAskedForByPath()
    {
        _http.OnBytes(HttpMethod.Get, Endpoint, [1, 2, 3]);
        using var provider = Provider();

        await provider.ThumbnailAsync(File("/Photos/a b+c.jpg", "/Photos/a b+c.jpg"), 256);

        var query = HttpUtility.ParseQueryString(_http.Single(HttpMethod.Get, Endpoint).Uri.Query);
        Assert.Equal("/Photos/a b+c.jpg", query["path"]);
        Assert.Null(query["fileid"]);
    }

    [Theory]
    [InlineData(1014)]
    [InlineData(3001)]
    public async Task ARefusalIsNoPreviewRatherThanAnError(int code)
    {
        _http.OnJson(HttpMethod.Get, Endpoint, $$"""{"result":{{code}},"error":"Thumb can not be created from this file type."}""");
        using var provider = Provider();

        Assert.Null(await provider.ThumbnailAsync(File("f77", "/doc.xyz"), 256));
    }

    [Fact]
    public async Task AMissingFileIsStillAnError()
    {
        _http.OnJson(HttpMethod.Get, Endpoint, """{"result":2009,"error":"File not found."}""");
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ThumbnailAsync(File("f77", "/gone.jpg"), 256));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task FoldersAreNeverAsked()
    {
        using var provider = Provider();

        Assert.Null(await provider.ThumbnailAsync(new CloudFileItem("d5", "Photos", "/Photos", true, 0, CloudFileItem.UnknownDate), 256));
        Assert.Empty(_http.Requests);
    }

    private static CloudFileItem File(string id, string path)
        => new(id, path[(path.LastIndexOf('/') + 1)..], path, false, 10, CloudFileItem.UnknownDate);

    private PCloudProvider Provider()
    {
        _store.Save($"pcloud.{_accountId}", new PCloudCredentials("TOKEN", "eapi.pcloud.com", 42));
        return new PCloudProvider(_accountId, _store, _http);
    }
}
