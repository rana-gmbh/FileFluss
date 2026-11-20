// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text.Json;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Dropbox;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>Server-side thumbnails from <c>/files/get_thumbnail_v2</c> — the macOS DropboxThumbnailTests, plus the request on the wire.</summary>
public sealed class DropboxThumbnailTests
{
    private const string Endpoint = "content.dropboxapi.com/2/files/get_thumbnail_v2";

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();

    // MARK: - Helpers

    [Theory]
    [InlineData(1, "w32h32")]
    [InlineData(128, "w128h128")]
    [InlineData(190, "w256h256")]
    [InlineData(256, "w256h256")]
    [InlineData(257, "w480h320")]
    [InlineData(290, "w480h320")]
    [InlineData(321, "w640h480")]
    [InlineData(5000, "w2048h1536")]
    public void TheSmallestSizeWhoseShorterSideReachesTheTileIsChosen(int maxPixelSize, string expected)
        => Assert.Equal(expected, DropboxApiClient.ThumbnailSizeName(maxPixelSize));

    [Theory]
    [InlineData("Screen.PNG", "png")]
    [InlineData("anim.gif", "png")]
    [InlineData("photo.jpg", "jpeg")]
    [InlineData("clip.mov", "jpeg")]
    [InlineData("noext", "jpeg")]
    [InlineData(".png", "jpeg")]
    public void PngAndGifFilesAskForPngEverythingElseForJpeg(string fileName, string expected)
        => Assert.Equal(expected, DropboxApiClient.ThumbnailFormat(fileName));

    [Fact]
    public void TheArgumentNamesTheFileByPathInBestfitMode()
    {
        var arg = DropboxApiClient.ThumbnailArgument("/Photos/a.jpg", "a.jpg", 240);

        using var document = JsonDocument.Parse(arg);
        var root = document.RootElement;
        Assert.Equal("path", root.GetProperty("resource").GetProperty(".tag").GetString());
        Assert.Equal("/Photos/a.jpg", root.GetProperty("resource").GetProperty("path").GetString());
        Assert.Equal("jpeg", root.GetProperty("format").GetString());
        Assert.Equal("w256h256", root.GetProperty("size").GetString());
        Assert.Equal("bestfit", root.GetProperty("mode").GetString());
    }

    [Fact]
    public void NonAsciiCharactersInThePathAreEscapedForTheHeader()
    {
        var arg = DropboxApiClient.ThumbnailArgument("/Größe.png", "Größe.png", 256);

        Assert.All(arg, c => Assert.True(c <= '~', $"non-ASCII {(int)c} in the header"));
        Assert.Contains("Gr\\u00f6\\u00dfe.png", arg, StringComparison.Ordinal);
        Assert.Contains("\"format\":\"png\"", arg, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unsupported_extension", true)]
    [InlineData("unsupported_image", true)]
    [InlineData("conversion_error", true)]
    [InlineData("path", false)]
    [InlineData("access_denied", false)]
    public void UnsupportedTypesAndUnreadablePicturesCountAsNoPreview(string tag, bool refusal)
        => Assert.Equal(refusal, DropboxApiClient.IsThumbnailRefusal(Refusal(tag)));

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""{"error_summary":"x"}""")]
    [InlineData("""{"error":"unsupported_extension"}""")]
    public void AnythingButATaggedErrorIsNoRefusal(string body)
        => Assert.False(DropboxApiClient.IsThumbnailRefusal(body));

    // MARK: - On the wire

    [Fact]
    public async Task ThePictureIsFetchedByPostWithTheArgumentInTheHeader()
    {
        _http.OnBytes(HttpMethod.Post, Endpoint, [0xFF, 0xD8, 0xFF]);
        using var provider = Provider();

        var bytes = await provider.ThumbnailAsync(File("/Photos/a.jpg"), 256);

        Assert.Equal([0xFF, 0xD8, 0xFF], bytes);
        var request = _http.Single(HttpMethod.Post, Endpoint);
        Assert.Equal("Bearer access-1", request.Headers["Authorization"]);
        Assert.Equal(DropboxApiClient.ThumbnailArgument("/Photos/a.jpg", "a.jpg", 256), request.Headers["Dropbox-API-Arg"]);
    }

    [Fact]
    public async Task ARefusalIsNoPreviewRatherThanAnError()
    {
        _http.OnJson(HttpMethod.Post, Endpoint, Refusal("unsupported_extension"), HttpStatusCode.Conflict);
        using var provider = Provider();

        Assert.Null(await provider.ThumbnailAsync(File("/doc.xyz"), 256));
    }

    [Fact]
    public async Task AMissingFileIsStillAnError()
    {
        _http.OnJson(HttpMethod.Post, Endpoint, """{"error_summary":"path/not_found/","error":{".tag":"path","path":{".tag":"not_found"}}}""", HttpStatusCode.Conflict);
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ThumbnailAsync(File("/gone.jpg"), 256));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task AnExpiredTokenIsRefreshedAndTheRequestSentAgain()
    {
        var calls = 0;
        _http.OnJson(HttpMethod.Post, "api.dropboxapi.com/oauth2/token", """{"access_token":"access-2","expires_in":14400}""");
        _http.On(HttpMethod.Post, Endpoint, (_, _) => ++calls == 1
            ? FakeHttpHandler.Json("""{"error":{".tag":"expired_access_token"}}""", HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        using var provider = Provider();

        Assert.Equal([1, 2, 3], await provider.ThumbnailAsync(File("/a.jpg"), 256));
        Assert.Equal("Bearer access-2", _http.Requests.Last(r => r.Uri.ToString().Contains(Endpoint, StringComparison.Ordinal)).Headers["Authorization"]);
    }

    [Fact]
    public async Task FoldersAreNeverAsked()
    {
        using var provider = Provider();

        Assert.Null(await provider.ThumbnailAsync(new CloudFileItem("id:1", "Photos", "/Photos", true, 0, CloudFileItem.UnknownDate), 256));
        Assert.Empty(_http.Requests);
    }

    private static string Refusal(string tag) => $$$"""{"error_summary":"{{{tag}}}/","error":{".tag":"{{{tag}}}"}}""";

    private static CloudFileItem File(string path)
        => new("id:abc", path[(path.LastIndexOf('/') + 1)..], path, false, 10, CloudFileItem.UnknownDate);

    private DropboxProvider Provider()
    {
        _store.Save($"dropbox.{_accountId}", new DropboxCredentials("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(4), "dbid:1", "Someone"));
        return new DropboxProvider(_accountId, _store, _http);
    }
}
