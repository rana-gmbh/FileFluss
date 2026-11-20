// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.KDrive;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>kDrive's thumbnail endpoint: id parsing, the size box, and which refusals mean "no preview".</summary>
public sealed class KDriveThumbnailTests
{
    private const string Thumbnail = "https://api.infomaniak.com/2/drive/101/files/42/thumbnail";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 7, 8, 9];

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();

    private KDriveProvider Provider()
    {
        _store.Save($"kdrive.{_accountId}", new KDriveCredentials("kd-token", 101, "Me"));
        return new KDriveProvider(_accountId, _store, _http);
    }

    private static CloudFileItem File(string id = "f42") => new(id, "a.jpg", "/a.jpg", false, 1000, DateTimeOffset.UnixEpoch);

    // MARK: - Helpers

    [Theory]
    [InlineData("f42", 42L)]
    [InlineData("f9007199254740993", 9007199254740993L)]
    [InlineData("d42", null)]
    [InlineData("f", null)]
    [InlineData("f-1", null)]
    [InlineData("fabc", null)]
    [InlineData("/a.jpg", null)]
    public void FileIdTakesOnlyAListedFilesNumericId(string itemId, long? expected)
        => Assert.Equal(expected, KDriveApiClient.FileId(itemId));

    [Theory]
    [InlineData(1, "width=10&height=10")]
    [InlineData(256, "width=256&height=256")]
    [InlineData(320, "width=320&height=320")]
    [InlineData(400, "width=400&height=400")]
    [InlineData(1000, "width=400&height=400")]
    public void TheSizeIsASquareBoxWithinTheEndpointsRange(int maxPixelSize, string expected)
        => Assert.Equal(expected, KDriveApiClient.ThumbnailQuery(maxPixelSize));

    [Fact]
    public void ThePathNamesDriveAndFile()
        => Assert.Equal("/2/drive/101/files/42/thumbnail", KDriveApiClient.ThumbnailPath(101, 42));

    // MARK: - Requests

    [Fact]
    public async Task FetchesTheThumbnailWithTheToken()
    {
        var provider = Provider();
        _http.OnBytes(HttpMethod.Get, Thumbnail + "?width=256&height=256", Png);

        Assert.Equal(Png, await provider.ThumbnailAsync(File(), 256));
        Assert.Equal("Bearer kd-token", _http.Requests.Single().Headers["Authorization"]);
    }

    [Fact]
    public async Task NotFoundIsNoPreview()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, Thumbnail, """{"result":"error","error":{"code":"object_not_found"}}""", HttpStatusCode.NotFound);

        Assert.Null(await provider.ThumbnailAsync(File(), 256));
    }

    [Fact]
    public async Task AFileTheVirusScanRefusedIsNoPreview()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, Thumbnail, """{"result":"error","error":{"code":"file_scan_status_not_safe_error","description":"not safe"}}""", HttpStatusCode.Forbidden);

        Assert.Null(await provider.ThumbnailAsync(File(), 256));
    }

    [Fact]
    public async Task AnyOther403StaysAnError()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, Thumbnail, """{"result":"error","error":{"code":"forbidden"}}""", HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ThumbnailAsync(File(), 256));
        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
    }

    [Fact]
    public async Task AnAnswerThatIsNotAnImageIsNoPreview()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, Thumbnail, """{"result":"success","data":null}""");

        Assert.Null(await provider.ThumbnailAsync(File(), 256));
    }

    [Fact]
    public async Task FoldersAskNothing()
    {
        var provider = Provider();

        Assert.Null(await provider.ThumbnailAsync(new CloudFileItem("d42", "Photos", "/Photos", true, 0, DateTimeOffset.UnixEpoch), 256));
        Assert.Empty(_http.Requests);
    }
}
