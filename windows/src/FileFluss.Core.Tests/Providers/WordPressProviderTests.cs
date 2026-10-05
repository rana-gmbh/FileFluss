// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.WordPress;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

public sealed class WordPressProviderTests : IDisposable
{
    private const string Api = "https://example.com/wp-json/wp/v2";

    private const string AprilMedia = """
        [
          { "id": 5, "date": "2026-04-08T12:00:00", "title": { "rendered": "photo" },
            "source_url": "https://example.com/wp-content/uploads/2026/04/photo.jpg",
            "media_details": { "filesize": 12345, "file": "2026/04/photo.jpg" } },
          { "id": 6, "date": "2026-04-09T08:30:00", "title": { "rendered": "My &amp; <b>Doc</b>" },
            "source_url": "https://example.com/wp-content/uploads/2026/04/doc.pdf",
            "media_details": { "filesize": "678" } },
          { "id": 7, "date": "2026-04-10T00:00:00", "title": { "rendered": "" },
            "source_url": null, "media_details": [] }
        ]
        """;

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ff-wp-" + Guid.NewGuid().ToString("N"));

    public WordPressProviderTests() => Directory.CreateDirectory(_temp);

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private string Key => $"wordpress.{_accountId}";

    private WordPressProvider SignedIn(string site = "https://example.com")
    {
        _store.Save(Key, new WordPressCredentials(site, "robert", "abcd efgh", "Robert"));
        return new WordPressProvider(_accountId, _store, _http);
    }

    private static HttpResponseMessage Paged(string json, int totalPages)
    {
        var response = FakeHttpHandler.Json(json);
        response.Headers.Add("X-WP-TotalPages", totalPages.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    private static string ExpectedBasic(string user, string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    [Fact]
    public async Task AMonthListingThatFailsPartWayThrowsInsteadOfReturningHalf()
    {
        var pages = 0;
        _http.On(HttpMethod.Get, "/wp/v2/media?after=2026-04", (_, _) => ++pages == 1
            ? Paged("""[{ "id": 5, "date": "2026-04-08T10:00:00", "title": { "rendered": "a" }, "source_url": "https://example.com/a.jpg", "media_details": { "filesize": 1 } }]""", 3)
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var provider = SignedIn();

        await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/2026/04"));
    }

    [Fact]
    public async Task APagePastTheEndEndsTheListing()
    {
        var pages = 0;
        _http.On(HttpMethod.Get, "/wp/v2/media?after=2026-04", (_, _) => ++pages == 1
            ? Paged("""[{ "id": 5, "date": "2026-04-08T10:00:00", "title": { "rendered": "a" }, "source_url": "https://example.com/a.jpg", "media_details": { "filesize": 1 } }]""", 5)
            : FakeHttpHandler.Json("""{"code":"rest_post_invalid_page_number"}""", HttpStatusCode.BadRequest));
        var provider = SignedIn();

        Assert.Single(await provider.ListDirectoryAsync("/2026/04"));
    }

    // MARK: - Connect

    [Fact]
    public async Task ConnectVerifiesAndStoresCredentials()
    {
        _http.OnJson(HttpMethod.Get, "/wp/v2/media?per_page=1", "[]")
             .OnJson(HttpMethod.Get, "/wp/v2/users/me", """{ "id": 1, "name": "Robert Rudolph" }""");
        var provider = new WordPressProvider(_accountId, _store, _http);
        Assert.False(provider.IsAuthenticated);

        var result = await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["siteUrl"] = "example.com/", ["username"] = "robert", ["password"] = "abcd efgh" }),
            new ScriptedConnectPrompt());

        Assert.Equal("Robert Rudolph", result.AccountLabel);
        Assert.True(provider.IsAuthenticated);
        var verify = _http.Single(HttpMethod.Get, "/media?per_page=1");
        Assert.Equal("https://example.com/wp-json/wp/v2/media?per_page=1", verify.Uri.AbsoluteUri);
        Assert.Equal(ExpectedBasic("robert", "abcd efgh"), verify.Headers["Authorization"]);

        var stored = _store.Load<WordPressCredentials>(Key)!;
        Assert.Equal("example.com/", stored.SiteUrl);
        Assert.Equal("Robert Rudolph", stored.DisplayName);
        Assert.Equal("example.com/", provider.GetEditableConnectValues()["siteUrl"]);
        Assert.False(provider.GetEditableConnectValues().ContainsKey("password"));
    }

    [Fact]
    public async Task FailedConnectLeavesStoredCredentialsUntouched()
    {
        var provider = SignedIn();
        var before = _store.Load(Key);
        _http.OnText(HttpMethod.Get, "/wp/v2/media?per_page=1", """{"code":"incorrect_password"}""", HttpStatusCode.Unauthorized);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["siteUrl"] = "https://example.com", ["username"] = "robert", ["password"] = "wrong" }),
            new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal(before, _store.Load(Key));
        Assert.True(provider.IsAuthenticated);
    }

    [Fact]
    public async Task ConnectRejectsAnHtmlFrontPage()
    {
        _http.OnText(HttpMethod.Get, "/wp/v2/media", "<html>Welcome</html>", mediaType: "text/html");
        var provider = new WordPressProvider(_accountId, _store, _http);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["siteUrl"] = "https://example.com", ["username"] = "u", ["password"] = "p" }),
            new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidResponse, error.Kind);
        Assert.Null(_store.Load(Key));
    }

    [Fact]
    public async Task ConnectReportsAMissingRestApi()
    {
        _http.OnText(HttpMethod.Get, "/wp/v2/media", "nope", HttpStatusCode.NotFound);
        var provider = new WordPressProvider(_accountId, _store, _http);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["siteUrl"] = "https://example.com", ["username"] = "u", ["password"] = "p" }),
            new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task OperationsWithoutCredentialsAskForSignIn()
    {
        var provider = new WordPressProvider(_accountId, _store, _http);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
    }

    // MARK: - Virtual folders

    [Fact]
    public async Task RootListsYearsFromFirstAndLastPage()
    {
        _http.On(HttpMethod.Get, "&page=1&orderby=date", (_, _) => Paged("""[ { "id": 1, "date": "2019-03-01T10:00:00" }, { "id": 2, "date": "2021-07-04T10:00:00" } ]""", 2))
             .On(HttpMethod.Get, "&page=2&orderby=date", (_, _) => Paged("""[ { "id": 3, "date": "2026-01-02T10:00:00" } ]""", 2));
        var provider = SignedIn();

        var years = await provider.ListDirectoryAsync("/");

        Assert.Equal(["2026", "2021", "2019"], years.Select(y => y.Name));
        Assert.All(years, y => Assert.True(y.IsDirectory));
        Assert.Equal("/2026", years[0].Path);
        Assert.Equal(ExpectedBasic("robert", "abcd efgh"), _http.Requests.First().Headers["Authorization"]);
    }

    [Fact]
    public async Task YearListsMonthsWithEnglishNames()
    {
        _http.On(HttpMethod.Get, "after=2026-01-01T00", (_, _) => Paged("""
            [ { "id": 1, "date": "2026-04-08T12:00:00" }, { "id": 2, "date": "2026-11-02T12:00:00" }, { "id": 3, "date": "2026-04-30T12:00:00" } ]
            """, 1));
        var provider = SignedIn();

        var months = await provider.ListDirectoryAsync("/2026");

        Assert.Equal(["11 - November", "04 - April"], months.Select(m => m.Name));
        Assert.Equal(["/2026/11", "/2026/04"], months.Select(m => m.Path));
    }

    [Fact]
    public async Task MonthListsMediaWithLenientFields()
    {
        _http.On(HttpMethod.Get, "after=2026-04-01T00", (_, _) => Paged(AprilMedia, 1));
        var provider = SignedIn();

        var files = await provider.ListDirectoryAsync("/2026/04");

        Assert.Equal(["photo.jpg", "My & Doc.pdf", "media-7"], files.Select(f => f.Name));
        Assert.Equal(["/2026/04/photo.jpg", "/2026/04/My & Doc.pdf", "/2026/04/media-7"], files.Select(f => f.Path));
        Assert.Equal([12345L, 678L, 0L], files.Select(f => f.Size));
        Assert.All(files, f => Assert.False(f.IsDirectory));
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 4, 8, 12, 0, 0, DateTimeKind.Local)), files[0].ModificationDate);
        Assert.Equal("wp-5", files[0].Id);
        Assert.Equal(
            "/wp-json/wp/v2/media?after=2026-04-01T00%3A00%3A00&before=2026-05-01T00%3A00%3A00&per_page=100&page=1",
            _http.Requests.Single().Uri.PathAndQuery);
    }

    [Fact]
    public async Task DecemberRunsIntoJanuary()
    {
        _http.On(HttpMethod.Get, "before=2026-01-01T00", (_, _) => Paged("[]", 1));
        var provider = SignedIn();

        Assert.Empty(await provider.ListDirectoryAsync("/2025/12"));
    }

    [Fact]
    public async Task UnauthorizedListingAsksForSignIn()
    {
        _http.OnText(HttpMethod.Get, "/wp/v2/media", """{"code":"rest_forbidden"}""", HttpStatusCode.Unauthorized);
        var provider = SignedIn();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/2026/04"));
        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
    }

    [Fact]
    public async Task VirtualFoldersHaveMetadataWithoutRequests()
    {
        var provider = SignedIn();
        var month = await provider.GetFileMetadataAsync("/2026/04");
        Assert.True(month.IsDirectory);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task FoldersCannotBeCreated()
    {
        var provider = SignedIn();
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateDirectoryAsync("/2026/05"));
        Assert.Equal(405, error.StatusCode);
        Assert.False(CloudProviderType.WordPress.CanCreateFolders());
    }

    // MARK: - Mutations

    [Fact]
    public async Task DeleteResolvesTheMediaIdAndForces()
    {
        _http.On(HttpMethod.Get, "after=2026-04-01", (_, _) => Paged(AprilMedia, 1))
             .OnJson(HttpMethod.Delete, "/wp/v2/media/6", """{ "deleted": true }""");
        var provider = SignedIn();

        await provider.DeleteItemAsync("/2026/04/My & Doc.pdf");

        var delete = _http.Single(HttpMethod.Delete, "/media/6");
        Assert.Equal("/wp-json/wp/v2/media/6?force=true", delete.Uri.PathAndQuery);
    }

    [Fact]
    public async Task DeleteOfAnUnknownFileIsNotFound()
    {
        _http.On(HttpMethod.Get, "after=2026-04-01", (_, _) => Paged(AprilMedia, 1))
             .OnJson(HttpMethod.Get, "/wp/v2/media?search=ghost", "[]");
        var provider = SignedIn();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DeleteItemAsync("/2026/04/ghost.png"));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task RenameSetsTheTitleWithoutExtension()
    {
        _http.On(HttpMethod.Get, "after=2026-04-01", (_, _) => Paged(AprilMedia, 1))
             .OnJson(HttpMethod.Post, "/wp/v2/media/5", """{ "id": 5 }""");
        var provider = SignedIn();

        await provider.RenameItemAsync("/2026/04/photo.jpg", "Holiday \"best\".jpg");

        var post = _http.Single(HttpMethod.Post, "/media/5");
        Assert.Equal("{\"title\":\"Holiday \\u0022best\\u0022\"}", post.Body);
        Assert.StartsWith("application/json", post.Headers["Content-Type"], StringComparison.Ordinal);
    }

    // MARK: - Transfers

    [Fact]
    public async Task UploadStreamsMultipartWithRawUtf8NameAndProgress()
    {
        var local = Path.Combine(_temp, "staging.tmp");
        await File.WriteAllBytesAsync(local, Enumerable.Range(0, 300_000).Select(i => (byte)'a').ToArray());
        _http.OnJson(HttpMethod.Post, "/wp/v2/media", """{ "id": 99 }""", HttpStatusCode.Created);
        var provider = SignedIn();

        long reported = 0;
        await provider.UploadFileAsync(local, "/2026/04/Grüße.jpg", d => Interlocked.Add(ref reported, d));

        Assert.Equal(300_000, reported);
        var post = _http.Single(HttpMethod.Post, "/wp/v2/media");
        Assert.Contains("Content-Disposition: form-data; name=\"file\"; filename=\"Grüße.jpg\"\r\n", post.Body, StringComparison.Ordinal);
        Assert.Contains("Content-Type: image/jpeg\r\n\r\n", post.Body, StringComparison.Ordinal);
        Assert.StartsWith("multipart/form-data; boundary=", post.Headers["Content-Type"], StringComparison.Ordinal);
        Assert.Equal(ExpectedBasic("robert", "abcd efgh"), post.Headers["Authorization"]);
        Assert.EndsWith("--\r\n", post.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadMapsTooLarge()
    {
        var local = Path.Combine(_temp, "big.mov");
        await File.WriteAllTextAsync(local, "movie");
        _http.OnText(HttpMethod.Post, "/wp/v2/media", "<html>413</html>", HttpStatusCode.RequestEntityTooLarge, "text/html");
        var provider = SignedIn();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(local, "/big.mov"));
        Assert.Equal(CloudErrorKind.FileTooLarge, error.Kind);
    }

    [Fact]
    public async Task UploadSurfacesWordPressMessages()
    {
        var local = Path.Combine(_temp, "a.exe");
        await File.WriteAllTextAsync(local, "MZ");
        _http.OnJson(HttpMethod.Post, "/wp/v2/media", """{ "code": "rest_upload_sideload_error", "message": "Sorry, you are not allowed to upload this file type." }""", HttpStatusCode.InternalServerError);
        var provider = SignedIn();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(local, "/a.exe"));
        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("Sorry, you are not allowed to upload this file type.", error.Message);
    }

    [Fact]
    public async Task SameHostRedirectKeepsMethodBodyAndCredentials()
    {
        var local = Path.Combine(_temp, "pic.png");
        await File.WriteAllTextAsync(local, "png-bytes");
        _http.OnJson(HttpMethod.Post, "https://example.com/wp-json/wp/v2/media", """{ "id": 1 }""", HttpStatusCode.Created)
             .On(HttpMethod.Post, "http://example.com/wp-json/wp/v2/media", (_, _) =>
             {
                 var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                 redirect.Headers.Location = new Uri("https://example.com/wp-json/wp/v2/media");
                 return redirect;
             });
        var provider = SignedIn("http://example.com");

        await provider.UploadFileAsync(local, "/pic.png");

        var requests = _http.Requests.ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal(HttpMethod.Post, requests[1].Method);
        Assert.Equal("https", requests[1].Uri.Scheme);
        Assert.Contains("png-bytes", requests[1].Body, StringComparison.Ordinal);
        Assert.Equal(ExpectedBasic("robert", "abcd efgh"), requests[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task CrossHostRedirectDropsCredentials()
    {
        _http.OnJson(HttpMethod.Get, "https://www.example.org/wp-json/wp/v2/media", "[]")
             .On(HttpMethod.Get, "https://example.com/wp-json/wp/v2/media", (_, _) =>
             {
                 var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                 redirect.Headers.Location = new Uri("https://www.example.org/wp-json/wp/v2/media?per_page=100&page=1&orderby=date&order=asc");
                 return redirect;
             });
        var provider = SignedIn();

        await provider.ListDirectoryAsync("/");

        var second = _http.Requests.Last();
        Assert.Equal("www.example.org", second.Uri.Host);
        Assert.False(second.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task DownloadFetchesTheSourceUrlWithProgress()
    {
        var payload = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        _http.On(HttpMethod.Get, "after=2026-04-01", (_, _) => Paged(AprilMedia, 1))
             .OnJson(HttpMethod.Get, "/wp/v2/media/5", """{ "id": 5, "date": "2026-04-08T12:00:00", "source_url": "https://example.com/wp-content/uploads/2026/04/photo.jpg" }""")
             .OnBytes(HttpMethod.Get, "/wp-content/uploads/2026/04/photo.jpg", payload);
        var provider = SignedIn();
        var target = Path.Combine(_temp, "photo.jpg");

        long reported = 0;
        await provider.DownloadFileAsync("/2026/04/photo.jpg", target, d => reported += d);

        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        Assert.Equal(payload.Length, reported);
        Assert.True(_http.Single(HttpMethod.Get, "/wp-content/uploads/").Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task DownloadFromACdnSendsNoCredentials()
    {
        _http.On(HttpMethod.Get, "after=2026-04-01", (_, _) => Paged(AprilMedia, 1))
             .OnJson(HttpMethod.Get, "/wp/v2/media/5", """{ "id": 5, "date": "2026-04-08T12:00:00", "source_url": "https://i0.wp.com/example.com/photo.jpg" }""")
             .OnBytes(HttpMethod.Get, "https://i0.wp.com/", [1, 2, 3]);
        var provider = SignedIn();

        await provider.DownloadFileAsync("/2026/04/photo.jpg", Path.Combine(_temp, "x.jpg"));

        Assert.False(_http.Single(HttpMethod.Get, "https://i0.wp.com/").Headers.ContainsKey("Authorization"));
    }

    // MARK: - Search and sharing

    [Fact]
    public async Task SearchReturnsVirtualPaths()
    {
        _http.On(HttpMethod.Get, "/wp/v2/media?search=photo", (_, _) => Paged(AprilMedia, 1));
        var provider = SignedIn();

        var results = await provider.SearchFilesAsync("photo", null);

        Assert.NotNull(results);
        Assert.Equal("/2026/04/photo.jpg", results![0].Path);
    }

    [Fact]
    public async Task ShareLinkIsThePublicMediaUrl()
    {
        _http.On(HttpMethod.Get, "after=2026-04-01", (_, _) => Paged(AprilMedia, 1))
             .OnJson(HttpMethod.Get, "/wp/v2/media/5", """{ "id": 5, "date": "2026-04-08T12:00:00", "source_url": "https://example.com/wp-content/uploads/2026/04/photo.jpg" }""");
        var provider = SignedIn();

        Assert.True(provider.ShareLinkCapabilities.CanCreate);
        Assert.False(provider.ShareLinkCapabilities.HasOptions);
        var link = await provider.CreateShareLinkAsync("/2026/04/photo.jpg", ShareLinkOptions.Default);

        Assert.Equal("https://example.com/wp-content/uploads/2026/04/photo.jpg", link.Url.AbsoluteUri);
        Assert.Equal(link.Url, link.DirectDownloadUrl);
        Assert.NotNull(link.Note);
    }

    // MARK: - Parsing helpers

    [Theory]
    [InlineData("2026-04-08T12:00:00Z", 2026, 4, 8, 12)]
    [InlineData("2026-04-08T12:00:00+02:00", 2026, 4, 8, 10)]
    public void DatesWithAZoneAreHonoured(string value, int year, int month, int day, int utcHour)
        => Assert.Equal(new DateTimeOffset(year, month, day, utcHour, 0, 0, TimeSpan.Zero), WordPressApiClient.ParseDate(value));

    [Fact]
    public void UnparseableDatesAreUnknown()
        => Assert.Equal(CloudFileItem.UnknownDate, WordPressApiClient.ParseDate("yesterday"));
}
