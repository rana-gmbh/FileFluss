// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using FileFluss.Core.Models;
using FileFluss.Core.Providers.GoogleDrive;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// Server-side thumbnails of both Google providers: the pure helpers (file id, edge, link
/// rewrite, token host rule) and the lookup against scripted HTTP.
/// </summary>
public sealed class GoogleDriveThumbnailTests
{
    private const string Metadata = "googleapis.com/drive/v3/files/abc?";
    private const string Link = "https://lh3.googleusercontent.com/drive-storage/AJQWtBN=s220";

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private readonly MemoryCredentialStore _store = new();
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly FakeHttpHandler _http = new();

    private GoogleDriveProvider Provider()
    {
        _store.Save($"googledrive.{_accountId}", new GoogleDriveCredentials("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1), "me@example.com", "Me"));
        return new GoogleDriveProvider(_accountId, _store, _http);
    }

    private GoogleDrivePickerProvider PickerProvider()
    {
        _store.Save(
            $"googleDrivePicker.{_accountId}",
            new GoogleDrivePickerCredentials("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1), "me@example.com", "Me", [new GoogleDrivePickedRoot("root1", "Picked")]));
        return new GoogleDrivePickerProvider(_accountId, _store, _http);
    }

    private static CloudFileItem File(string id = "fabc", string path = "/photo.jpg") => new(id, "photo.jpg", path, false, 1000, DateTimeOffset.UnixEpoch);

    private static string Info(string link, bool hasThumbnail = true)
        => $$"""{"hasThumbnail":{{(hasThumbnail ? "true" : "false")}},"thumbnailLink":"{{link}}"}""";

    // MARK: - Helpers

    [Theory]
    [InlineData("fabc", "abc")]
    [InlineData("f1A-b_C", "1A-b_C")]
    [InlineData("dabc", null)]
    [InlineData("d:root1", null)]
    [InlineData("f", null)]
    [InlineData("", null)]
    public void DriveFileIdTakesOnlyAListedFilesId(string itemId, string? expected)
        => Assert.Equal(expected, GoogleDriveApi.DriveFileId(itemId));

    [Theory]
    [InlineData(1, 32)]
    [InlineData(32, 32)]
    [InlineData(256, 256)]
    [InlineData(320, 320)]
    [InlineData(1600, 1600)]
    [InlineData(5000, 1600)]
    public void EdgeIsTheTileSizeWithinBounds(int maxPixelSize, int expected)
        => Assert.Equal(expected, GoogleDriveApi.ThumbnailEdge(maxPixelSize));

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/drive-storage/AJQ=s220", "https://lh3.googleusercontent.com/drive-storage/AJQ=s256")]
    [InlineData("https://lh3.googleusercontent.com/a%2Fb=s220?x=1#top", "https://lh3.googleusercontent.com/a%2Fb=s256?x=1#top")]
    [InlineData("https://drive.google.com/thumbnail?id=abc&sz=s220", "https://drive.google.com/thumbnail?id=abc&sz=s256")]
    [InlineData("https://drive.google.com/thumbnail?sz=s220&id=abc", "https://drive.google.com/thumbnail?sz=s256&id=abc")]
    [InlineData("https://drive.google.com/thumbnail?sz=w220&id=abc", "https://drive.google.com/thumbnail?sz=w220&id=abc")]
    [InlineData("https://lh3.googleusercontent.com/drive-storage/AJQ=s220-c", "https://lh3.googleusercontent.com/drive-storage/AJQ=s220-c")]
    [InlineData("https://lh3.googleusercontent.com/drive-storage/AJQ", "https://lh3.googleusercontent.com/drive-storage/AJQ")]
    public void OnlyTheKnownSizeFormsAreRewritten(string link, string expected)
        => Assert.Equal(expected, GoogleDriveApi.ThumbnailLink(link, 256));

    [Fact]
    public void ASizeInTheQueryIsNotMistakenForOneInThePath()
        => Assert.Equal("https://x.google.com/p?q=s220", GoogleDriveApi.ThumbnailLink("https://x.google.com/p?q=s220", 256));

    [Fact]
    public void ARelativeLinkIsRejected()
        => Assert.Null(GoogleDriveApi.ThumbnailLink("/drive-storage/AJQ=s220", 256));

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/x", true)]
    [InlineData("https://googleusercontent.com/x", true)]
    [InlineData("https://drive.google.com/thumbnail", true)]
    [InlineData("https://www.googleapis.com/drive/v3", true)]
    [InlineData("https://LH3.GoogleUserContent.com/x", true)]
    [InlineData("http://lh3.googleusercontent.com/x", false)]
    [InlineData("https://evilgoogle.com/x", false)]
    [InlineData("https://googleusercontent.com.evil.example/x", false)]
    [InlineData("https://example.com/googleusercontent.com", false)]
    public void TheTokenGoesOnlyToGooglesOwnHttpsHosts(string url, bool expected)
        => Assert.Equal(expected, GoogleDriveApi.MayCarryGoogleToken(new Uri(url)));

    // MARK: - Lookup

    [Fact]
    public async Task FetchesTheResizedLinkWithTheToken()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, Metadata, Info(Link));
        _http.OnBytes(HttpMethod.Get, "lh3.googleusercontent.com/drive-storage/AJQWtBN=s320", Jpeg);

        Assert.Equal(Jpeg, await provider.ThumbnailAsync(File(), 320));

        var metadata = _http.Single(HttpMethod.Get, Metadata);
        Assert.Contains("fields=hasThumbnail,thumbnailLink", metadata.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("supportsAllDrives=true", metadata.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("Bearer access-1", metadata.Headers["Authorization"]);
        Assert.Equal("Bearer access-1", _http.Single(HttpMethod.Get, "googleusercontent.com").Headers["Authorization"]);
    }

    [Fact]
    public async Task ARefusedResizedLinkFallsBackToTheOriginalOnce()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, Metadata, Info(Link));
        _http.OnText(HttpMethod.Get, "=s256", "nope", HttpStatusCode.NotFound);
        _http.OnBytes(HttpMethod.Get, "=s220", Jpeg);

        Assert.Equal(Jpeg, await provider.ThumbnailAsync(File(), 256));
        Assert.Equal(3, _http.Requests.Count);
    }

    [Fact]
    public async Task AnAnswerThatIsNotAnImageIsNoPreview()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, Metadata, Info(Link));
        _http.OnText(HttpMethod.Get, "googleusercontent.com", "<html>sign in</html>", mediaType: "text/html");

        Assert.Null(await provider.ThumbnailAsync(File(), 256));
    }

    [Fact]
    public async Task ALinkOffGooglesHostsIsNeverFetched()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, Metadata, Info("https://attacker.example/steal=s220"));

        Assert.Null(await provider.ThumbnailAsync(File(), 256));
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task NoThumbnailOrAGoneFileIsNull()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, Metadata, Info(Link, hasThumbnail: false));
        _http.OnText(HttpMethod.Get, "googleapis.com/drive/v3/files/gone?", "{}", HttpStatusCode.NotFound);

        Assert.Null(await provider.ThumbnailAsync(File(), 256));
        Assert.Null(await provider.ThumbnailAsync(File("fgone"), 256));
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task A401RefreshesTheTokenAndRetriesOnce()
    {
        var provider = Provider();
        _http.On(HttpMethod.Get, Metadata, (request, _) => request.Headers.Authorization?.Parameter == "access-2"
            ? FakeHttpHandler.Json(Info(Link))
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        _http.OnJson(HttpMethod.Post, "oauth2.googleapis.com/token", """{"access_token":"access-2","expires_in":3600}""");
        _http.OnBytes(HttpMethod.Get, "googleusercontent.com", Jpeg);

        Assert.Equal(Jpeg, await provider.ThumbnailAsync(File(), 256));
        Assert.Equal("Bearer access-2", _http.Single(HttpMethod.Get, "googleusercontent.com").Headers["Authorization"]);
    }

    [Fact]
    public async Task FoldersAndVirtualNodesAskNothing()
    {
        var provider = Provider();
        var folder = new CloudFileItem("dabc", "Docs", "/Docs", true, 0, DateTimeOffset.UnixEpoch);
        var driveRoot = File("dshared", "/Shared drives/Team") with { Role = CloudItemRole.DriveRoot };

        Assert.Null(await provider.ThumbnailAsync(folder, 256));
        Assert.Null(await provider.ThumbnailAsync(driveRoot, 256));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task ThePickerAccountUsesTheSameLookup()
    {
        var provider = PickerProvider();
        _http.OnJson(HttpMethod.Get, Metadata, Info(Link));
        _http.OnBytes(HttpMethod.Get, "googleusercontent.com", Jpeg);

        Assert.Equal(Jpeg, await provider.ThumbnailAsync(File(path: "/Picked/photo.jpg"), 256));
    }

    [Fact]
    public async Task ThePickerAccountNeverWalksAPath()
    {
        var provider = PickerProvider();

        Assert.Null(await provider.ThumbnailAsync(File("/Picked/photo.jpg", "/Picked/photo.jpg"), 256));
        Assert.Empty(_http.Requests);
    }
}
