// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Specialized;
using System.Net;
using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.GoogleDrive;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

public sealed class GoogleDriveProviderTests : IDisposable
{
    private const string Folder = "application/vnd.google-apps.folder";

    private const string RootListing = """
        {"files":[
          {"id":"fold1","name":"Docs","mimeType":"application/vnd.google-apps.folder","modifiedTime":"2024-03-01T10:00:00.000Z"},
          {"id":"file1","name":"a.txt","mimeType":"text/plain","size":"1234","modifiedTime":"2024-03-02T11:22:33.456Z","md5Checksum":"abc"},
          {"id":"doc1","name":"Report","mimeType":"application/vnd.google-apps.document","modifiedTime":"2024-03-03T00:00:00Z"},
          {"id":"hid1","name":".hidden","mimeType":"text/plain","size":"1"}
        ]}
        """;

    private readonly MemoryCredentialStore _store = new();
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly FakeHttpHandler _http = new();
    private readonly string _temp = Directory.CreateTempSubdirectory("ff-gdrive-").FullName;

    private string Key => $"googledrive.{_accountId}";

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private GoogleDriveProvider Provider(DateTimeOffset? expiresAt = null, bool stored = true)
    {
        if (stored)
        {
            _store.Save(Key, new GoogleDriveCredentials("access-1", "refresh-1", expiresAt ?? DateTimeOffset.UtcNow.AddHours(1), "me@example.com", "Me Myself"));
        }

        return new GoogleDriveProvider(_accountId, _store, _http) { RetryDelay = TimeSpan.Zero, FolderPollDelay = TimeSpan.Zero };
    }

    private static NameValueCollection Query(Uri uri) => HttpUtility.ParseQueryString(uri.Query);

    private static string? Q(HttpRequestMessage request) => Query(request.RequestUri!)["q"];

    private static HttpResponseMessage Status(HttpStatusCode status, string? body = null)
        => body is null ? new HttpResponseMessage(status) : FakeHttpHandler.Json(body, status);

    private static string Walk(string id, string name, string mimeType = "text/plain")
        => $$"""{"files":[{"id":"{{id}}","name":"{{name}}","mimeType":"{{mimeType}}"}]}""";

    private void NoSharedDrives() => _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/drives", """{"drives":[]}""");

    // MARK: - Export names

    [Fact]
    public async Task ExportExtensionComesFromTheListedMimeTypeWithoutAnotherRequest()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", RootListing);
        NoSharedDrives();
        var items = await provider.ListDirectoryAsync("/");
        var before = _http.Requests.Count;

        Assert.Equal("docx", await provider.ExportExtensionAsync(items.Single(i => i.Name == "Report")));
        Assert.Null(await provider.ExportExtensionAsync(items.Single(i => i.Name == "a.txt")));
        Assert.Null(await provider.ExportExtensionAsync(items.Single(i => i.Name == "Docs")));
        Assert.Equal(before, _http.Requests.Count);
    }

    // MARK: - Listing

    [Fact]
    public async Task ListingParsesFilesAndAddsTheSharedDrivesContainer()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", RootListing);
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/drives", """{"drives":[{"id":"sd1","name":"Team"}]}""");

        var items = await provider.ListDirectoryAsync("/");

        Assert.Equal(5, items.Count);
        var docs = items[0];
        Assert.True(docs.IsDirectory);
        Assert.Equal("dfold1", docs.Id);
        Assert.Equal("/Docs", docs.Path);

        var file = items[1];
        Assert.False(file.IsDirectory);
        Assert.Equal("ffile1", file.Id);
        Assert.Equal("/a.txt", file.Path);
        Assert.Equal(1234, file.Size);
        Assert.Equal(new DateTimeOffset(2024, 3, 2, 11, 22, 33, 456, TimeSpan.Zero), file.ModificationDate);
        Assert.Equal("abc", file.Checksum);

        Assert.Equal(0, items[2].Size);
        Assert.Equal(new DateTimeOffset(2024, 3, 3, 0, 0, 0, TimeSpan.Zero), items[2].ModificationDate);
        Assert.True(items[3].IsHidden);
        Assert.False(file.IsHidden);

        var container = items[4];
        Assert.Equal("/Shared drives", container.Path);
        Assert.True(container.IsDirectory);
        Assert.Equal(CloudItemRole.VirtualContainer, container.Role);
        Assert.Equal("drive", container.IconOverride);

        var request = _http.Single(HttpMethod.Get, "googleapis.com/drive/v3/files?");
        var query = Query(request.Uri);
        Assert.Equal("'root' in parents and trashed = false", query["q"]);
        Assert.Equal("nextPageToken,files(id,name,mimeType,size,modifiedTime,md5Checksum)", query["fields"]);
        Assert.Equal("1000", query["pageSize"]);
        Assert.Equal("folder,name", query["orderBy"]);
        Assert.Equal("true", query["supportsAllDrives"]);
        Assert.Null(query["corpora"]);
        Assert.Null(query["driveId"]);
        Assert.Equal("Bearer access-1", request.Headers["Authorization"]);
    }

    [Fact]
    public async Task RootWithoutSharedDrivesHasNoContainer()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", RootListing);
        NoSharedDrives();

        var items = await provider.ListDirectoryAsync("/");

        Assert.DoesNotContain(items, i => i.Role != CloudItemRole.Normal);
    }

    [Fact]
    public async Task ListingFollowsPageTokens()
    {
        var provider = Provider();
        NoSharedDrives();
        _http.On(HttpMethod.Get, "googleapis.com/drive/v3/files?", (request, _) => Query(request.RequestUri!)["pageToken"] == "p2"
            ? FakeHttpHandler.Json("""{"files":[{"id":"b","name":"b.txt","mimeType":"text/plain","size":"2"}]}""")
            : FakeHttpHandler.Json("""{"nextPageToken":"p2","files":[{"id":"a","name":"a.txt","mimeType":"text/plain","size":"1"}]}"""));

        var items = await provider.ListDirectoryAsync("/");

        Assert.Equal(new[] { "a.txt", "b.txt" }, items.Select(i => i.Name));
    }

    [Fact]
    public async Task NestedPathsAreWalkedOnceThenCached()
    {
        var provider = Provider();
        _http.On(HttpMethod.Get, "googleapis.com/drive/v3/files?", (request, _) => Q(request) switch
        {
            "'root' in parents and name = 'Docs' and trashed = false" => FakeHttpHandler.Json(Walk("fold1", "Docs", Folder)),
            "'fold1' in parents and name = 'Sub' and trashed = false" => FakeHttpHandler.Json(Walk("sub1", "Sub", Folder)),
            "'sub1' in parents and trashed = false" => FakeHttpHandler.Json("""{"files":[{"id":"x","name":"x.txt","mimeType":"text/plain"}]}"""),
            _ => Status(HttpStatusCode.BadRequest),
        });

        var first = await provider.ListDirectoryAsync("/Docs/Sub/");
        var second = await provider.ListDirectoryAsync("/Docs/Sub");

        Assert.Equal("/Docs/Sub/x.txt", Assert.Single(first).Path);
        Assert.Single(second);
        var queries = _http.Requests.Select(r => Query(r.Uri)["q"]).ToList();
        Assert.Single(queries, q => q!.Contains("name = 'Docs'", StringComparison.Ordinal));
        Assert.Single(queries, q => q!.Contains("name = 'Sub'", StringComparison.Ordinal));
        Assert.Equal(2, queries.Count(q => q == "'sub1' in parents and trashed = false"));

        var walk = Query(_http.Requests.First().Uri);
        Assert.Equal("files(id,name,mimeType)", walk["fields"]);
        Assert.Equal("1", walk["pageSize"]);
    }

    [Fact]
    public async Task NamesInQueriesEscapeQuotesAndBackslashes()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", """{"files":[]}""");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/Bob's \\ files"));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal("/Bob's \\ files", error.ItemPath);
        Assert.Equal("'root' in parents and name = 'Bob\\'s \\\\ files' and trashed = false", Query(_http.Requests.Single().Uri)["q"]);
    }

    [Fact]
    public async Task SharedDrivesListAsDriveRootsAndScopeTheirQueries()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/drives", """{"drives":[{"id":"sd1","name":"Team"}]}""");
        _http.On(HttpMethod.Get, "googleapis.com/drive/v3/files?", (request, _) => Q(request) switch
        {
            "'sd1' in parents and trashed = false" => FakeHttpHandler.Json("""{"files":[{"id":"p1","name":"Plans","mimeType":"application/vnd.google-apps.folder"}]}"""),
            "'p1' in parents and trashed = false" => FakeHttpHandler.Json("""{"files":[]}"""),
            _ => Status(HttpStatusCode.BadRequest),
        });

        var drives = await provider.ListDirectoryAsync("/Shared drives");
        var inside = await provider.ListDirectoryAsync("/Shared drives/Team");
        await provider.ListDirectoryAsync("/Shared drives/Team/Plans");

        var drive = Assert.Single(drives);
        Assert.Equal("/Shared drives/Team", drive.Path);
        Assert.Equal("dsd1", drive.Id);
        Assert.Equal(CloudItemRole.DriveRoot, drive.Role);
        Assert.Equal("drive", drive.IconOverride);
        Assert.Equal("/Shared drives/Team/Plans", Assert.Single(inside).Path);

        Assert.Equal("100", Query(_http.Single(HttpMethod.Get, "/drives").Uri)["pageSize"]);
        foreach (var request in _http.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal)))
        {
            var query = Query(request.Uri);
            Assert.Equal("drive", query["corpora"]);
            Assert.Equal("sd1", query["driveId"]);
            Assert.Equal("true", query["includeItemsFromAllDrives"]);
            Assert.Equal("true", query["supportsAllDrives"]);
        }
    }

    [Fact]
    public async Task SharedDrivePathsResolveWithoutListingFirst()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/drives", """{"drives":[{"id":"sd1","name":"Team"}]}""");
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", """{"files":[]}""");

        await provider.ListDirectoryAsync("/Shared drives/Team");
        var missing = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/Shared drives/Nope"));

        Assert.Equal("'sd1' in parents and trashed = false", Query(_http.Single(HttpMethod.Get, "/files?").Uri)["q"]);
        Assert.Equal(CloudErrorKind.NotFound, missing.Kind);
        Assert.Equal("/Shared drives/Nope", missing.ItemPath);
    }

    [Fact]
    public async Task VirtualSharedDriveNodesRefuseChangesWithoutCallingTheApi()
    {
        var provider = Provider();
        var local = Path.Combine(_temp, "x.txt");
        await File.WriteAllTextAsync(local, "x");

        await AssertNotImplemented(() => provider.DeleteItemAsync("/Shared drives/Team"));
        await AssertNotImplemented(() => provider.DeleteItemAsync("/Shared drives"));
        await AssertNotImplemented(() => provider.CreateDirectoryAsync("/Shared drives/New"));
        await AssertNotImplemented(() => provider.RenameItemAsync("/Shared drives/Team", "Other"));
        await AssertNotImplemented(() => provider.UploadFileAsync(local, "/Shared drives/x.txt"));
        await AssertNotImplemented(() => provider.CreateShareLinkAsync("/Shared drives/Team", ShareLinkOptions.Default));

        Assert.Empty(_http.Requests);
        Assert.Equal(CloudItemRole.VirtualContainer, (await provider.GetFileMetadataAsync("/Shared drives")).Role);
        Assert.Empty(await provider.SearchFilesAsync("x", "/Shared drives") ?? throw new InvalidOperationException());
        Assert.Empty(_http.Requests);

        static async Task AssertNotImplemented(Func<Task> action)
            => Assert.Equal(CloudErrorKind.NotImplemented, (await Assert.ThrowsAsync<CloudProviderException>(action)).Kind);
    }

    // MARK: - Download

    [Fact]
    public async Task WorkspaceDocumentsAreExportedAsDocxWithTheExtensionAppended()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", Walk("doc1", "Report", "application/vnd.google-apps.document"));
        _http.OnBytes(HttpMethod.Get, "/files/doc1/export", "DOCX-BYTES"u8.ToArray());
        var local = Path.Combine(_temp, "Report");
        long bytes = 0;

        await provider.DownloadFileAsync("/Report", local, delta => Interlocked.Add(ref bytes, delta));

        Assert.False(File.Exists(local));
        Assert.Equal("DOCX-BYTES", await File.ReadAllTextAsync(local + ".docx"));
        Assert.Equal(10, bytes);
        var query = Query(_http.Single(HttpMethod.Get, "/export").Uri);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", query["mimeType"]);
        Assert.Equal("true", query["supportsAllDrives"]);
    }

    [Theory]
    [InlineData("application/vnd.google-apps.spreadsheet", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx")]
    [InlineData("application/vnd.google-apps.presentation", "application/vnd.openxmlformats-officedocument.presentationml.presentation", "pptx")]
    [InlineData("application/vnd.google-apps.drawing", "application/pdf", "pdf")]
    public async Task OtherWorkspaceTypesExportToTheirOfficeFormat(string googleType, string exportType, string extension)
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", Walk("g1", "Thing", googleType));
        _http.OnBytes(HttpMethod.Get, "/files/g1/export", [1, 2, 3]);
        var local = Path.Combine(_temp, "Thing");

        await provider.DownloadFileAsync("/Thing", local);

        Assert.True(File.Exists(local + "." + extension));
        Assert.Equal(exportType, Query(_http.Single(HttpMethod.Get, "/export").Uri)["mimeType"]);
    }

    [Fact]
    public async Task RegularFilesDownloadWithAltMedia()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", Walk("file1", "a.txt"));
        _http.OnBytes(HttpMethod.Get, "/files/file1?alt=media", "hello"u8.ToArray());
        var local = Path.Combine(_temp, "a.txt");
        long bytes = 0;

        await provider.DownloadFileAsync("/a.txt", local, delta => bytes += delta);

        Assert.Equal("hello", await File.ReadAllTextAsync(local));
        Assert.Equal(5, bytes);
        Assert.Equal("true", Query(_http.Single(HttpMethod.Get, "alt=media").Uri)["supportsAllDrives"]);
    }

    [Fact]
    public async Task FailedDownloadLeavesNoFileAndMapsTheStatus()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", Walk("file1", "a.txt"));
        _http.On(HttpMethod.Get, "alt=media", (_, _) => Status(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"File not found: file1."}}"""));
        var local = Path.Combine(_temp, "a.txt");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DownloadFileAsync("/a.txt", local));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal("/a.txt", error.ItemPath);
        Assert.False(File.Exists(local));
    }

    // MARK: - Upload

    [Fact]
    public async Task SmallUploadCreatesWithMultipartThenReplacesTheCachedId()
    {
        var provider = Provider();
        var local = Path.Combine(_temp, "up.txt");
        await File.WriteAllTextAsync(local, "hello world");
        File.SetLastWriteTimeUtc(local, new DateTime(2023, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", """{"files":[]}""");
        _http.OnJson(HttpMethod.Post, "googleapis.com/upload/drive/v3/files?", """{"id":"new1","name":"up.txt","mimeType":"text/plain"}""");
        _http.OnJson(HttpMethod.Patch, "googleapis.com/upload/drive/v3/files/new1?", """{"id":"new1","name":"up.txt","mimeType":"text/plain"}""");
        _http.OnJson(HttpMethod.Patch, "googleapis.com/drive/v3/files/new1?", "{}");
        long bytes = 0;

        await provider.UploadFileAsync(local, "/up.txt", delta => bytes += delta);

        Assert.Equal(11, bytes);
        var find = Query(_http.Single(HttpMethod.Get, "/files?").Uri);
        Assert.Equal("'root' in parents and name = 'up.txt' and trashed = false", find["q"]);
        Assert.Equal("files(id)", find["fields"]);

        var create = _http.Single(HttpMethod.Post, "upload/drive/v3/files?");
        var query = Query(create.Uri);
        Assert.Equal("multipart", query["uploadType"]);
        Assert.Equal("id,name,mimeType", query["fields"]);
        Assert.Equal("true", query["supportsAllDrives"]);
        Assert.Matches("^multipart/related; boundary=[0-9A-F-]+$", create.Headers["Content-Type"]);
        Assert.Contains("\"name\":\"up.txt\"", create.Body);
        Assert.Contains("\"parents\":[\"root\"]", create.Body);
        Assert.Contains("\"modifiedTime\":\"2023-05-06T07:08:09.000Z\"", create.Body);
        Assert.Contains("\"createdTime\":", create.Body);
        Assert.Contains("Content-Type: application/octet-stream", create.Body);
        Assert.Contains("\r\n\r\nhello world\r\n--", create.Body);
        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Patch);

        // The id is cached: replacing needs no lookup, PATCHes the content with empty
        // metadata, then stamps the dates in a separate metadata PATCH.
        await provider.UploadFileAsync(local, "/up.txt");

        Assert.Single(_http.Requests, r => r.Method == HttpMethod.Get);
        var replace = _http.Single(HttpMethod.Patch, "upload/drive/v3/files/new1?");
        Assert.Equal("multipart", Query(replace.Uri)["uploadType"]);
        Assert.Contains("\r\n\r\n{}\r\n--", replace.Body);
        var stamp = _http.Single(HttpMethod.Patch, "googleapis.com/drive/v3/files/new1?");
        Assert.Equal("true", Query(stamp.Uri)["supportsAllDrives"]);
        Assert.Contains("\"modifiedTime\":\"2023-05-06T07:08:09.000Z\"", stamp.Body);
        Assert.Contains("\"createdTime\":", stamp.Body);
    }

    [Fact]
    public async Task SmallUploadRetriesNotFoundWithoutCountingBytesTwice()
    {
        var provider = Provider();
        var local = Path.Combine(_temp, "up.txt");
        await File.WriteAllTextAsync(local, "0123456789");
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", """{"files":[]}""");
        var attempts = 0;
        _http.On(HttpMethod.Post, "googleapis.com/upload/drive/v3/files?", (_, _) => Interlocked.Increment(ref attempts) < 3
            ? Status(HttpStatusCode.NotFound, """{"error":{"message":"File not found: root."}}""")
            : FakeHttpHandler.Json("""{"id":"new1","name":"up.txt","mimeType":"text/plain"}"""));
        long bytes = 0;

        await provider.UploadFileAsync(local, "/up.txt", delta => bytes += delta);

        Assert.Equal(3, attempts);
        Assert.Equal(10, bytes);
    }

    [Fact]
    public async Task LargeUploadUsesAResumableSessionInTenMegabyteChunks()
    {
        var provider = Provider();
        const long size = 12 * 1024 * 1024;
        var local = Path.Combine(_temp, "big.bin");
        await using (var file = File.Create(local))
        {
            file.SetLength(size);
        }

        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", """{"files":[]}""");
        _http.On(HttpMethod.Post, "uploadType=resumable", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Location = new Uri("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=XYZ");
            return response;
        });
        _http.On(HttpMethod.Put, "upload_id=XYZ", (request, _) => request.Content!.Headers.ContentRange!.From == 0
            ? new HttpResponseMessage((HttpStatusCode)308)
            : FakeHttpHandler.Json("""{"id":"big1","name":"big.bin","mimeType":"application/octet-stream"}"""));
        long bytes = 0;

        await provider.UploadFileAsync(local, "/big.bin", delta => bytes += delta);

        Assert.Equal(size, bytes);
        var init = _http.Single(HttpMethod.Post, "uploadType=resumable");
        Assert.Equal("12582912", init.Headers["X-Upload-Content-Length"]);
        Assert.Equal("application/octet-stream", init.Headers["X-Upload-Content-Type"]);
        Assert.StartsWith("application/json", init.Headers["Content-Type"]);
        Assert.Contains("\"name\":\"big.bin\"", init.Body);
        Assert.Contains("\"parents\":[\"root\"]", init.Body);

        var chunks = _http.Requests.Where(r => r.Method == HttpMethod.Put).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal("bytes 0-10485759/12582912", chunks[0].Headers["Content-Range"]);
        Assert.Equal(10485760, chunks[0].Body!.Length);
        Assert.Equal("bytes 10485760-12582911/12582912", chunks[1].Headers["Content-Range"]);
        Assert.Equal(2097152, chunks[1].Body!.Length);

        // The id was cached from the final chunk's response: no timestamp PATCH for a new file.
        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task UploadsIntoASharedDriveStayInItsCorpus()
    {
        var provider = Provider();
        var local = Path.Combine(_temp, "x.txt");
        await File.WriteAllTextAsync(local, "x");
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/drives", """{"drives":[{"id":"sd1","name":"Team"}]}""");
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", """{"files":[]}""");
        _http.OnJson(HttpMethod.Post, "googleapis.com/upload/drive/v3/files?", """{"id":"n1","name":"x.txt","mimeType":"text/plain"}""");

        await provider.UploadFileAsync(local, "/Shared drives/Team/x.txt");

        var find = Query(_http.Single(HttpMethod.Get, "/files?").Uri);
        Assert.Equal("sd1", find["driveId"]);
        Assert.Contains("\"parents\":[\"sd1\"]", _http.Single(HttpMethod.Post, "upload/drive").Body);
    }

    // MARK: - Mutations

    [Fact]
    public async Task DeletingAFolderEvictsItsCachedSubtree()
    {
        var provider = Provider();
        var deleted = false;
        NoSharedDrives();
        _http.On(HttpMethod.Get, "googleapis.com/drive/v3/files?", (request, _) => Q(request) switch
        {
            "'root' in parents and trashed = false" => FakeHttpHandler.Json(RootListing),
            "'fold1' in parents and trashed = false" => FakeHttpHandler.Json("""{"files":[{"id":"x1","name":"x.txt","mimeType":"text/plain"}]}"""),
            "'root' in parents and name = 'Docs' and trashed = false" => FakeHttpHandler.Json(deleted ? """{"files":[]}""" : Walk("fold1", "Docs", Folder)),
            _ => Status(HttpStatusCode.BadRequest),
        });
        _http.On(HttpMethod.Delete, "googleapis.com/drive/v3/files/fold1", (_, _) =>
        {
            deleted = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await provider.ListDirectoryAsync("/");
        await provider.ListDirectoryAsync("/Docs");
        await provider.DeleteItemAsync("/Docs");

        Assert.Equal("true", Query(_http.Single(HttpMethod.Delete, "/files/fold1").Uri)["supportsAllDrives"]);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.GetFileMetadataAsync("/Docs/x.txt"));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal("/Docs", error.ItemPath);
    }

    [Fact]
    public async Task CreateDirectoryIsIdempotentAndWaitsForTheNewFolder()
    {
        var provider = Provider();
        var polls = 0;
        _http.On(HttpMethod.Get, "googleapis.com/drive/v3/files?", (request, _) => Q(request) switch
        {
            "'root' in parents and name = 'Docs' and trashed = false" => FakeHttpHandler.Json(Walk("fold1", "Docs", Folder)),
            "'root' in parents and name = 'New' and trashed = false" => FakeHttpHandler.Json("""{"files":[]}"""),
            "'new9' in parents and trashed = false" => FakeHttpHandler.Json("""{"files":[]}"""),
            _ => Status(HttpStatusCode.BadRequest),
        });
        _http.OnJson(HttpMethod.Post, "googleapis.com/drive/v3/files?", """{"id":"new9","name":"New","mimeType":"application/vnd.google-apps.folder"}""");
        _http.On(HttpMethod.Get, "googleapis.com/drive/v3/files/new9?", (_, _) => Interlocked.Increment(ref polls) == 1
            ? Status(HttpStatusCode.NotFound)
            : FakeHttpHandler.Json("""{"id":"new9","name":"New","mimeType":"application/vnd.google-apps.folder"}"""));

        await provider.CreateDirectoryAsync("/Docs");
        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Post);

        await provider.CreateDirectoryAsync("/New");
        var create = _http.Single(HttpMethod.Post, "/files?");
        Assert.Equal("""{"name":"New","mimeType":"application/vnd.google-apps.folder","parents":["root"]}""", create.Body);
        Assert.Equal("true", Query(create.Uri)["supportsAllDrives"]);
        Assert.Equal(2, polls);
        Assert.Equal("id,name,mimeType", Query(_http.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/new9", StringComparison.Ordinal)).Uri)["fields"]);

        // The new folder's id is cached: listing it needs no walk.
        await provider.ListDirectoryAsync("/New");
        Assert.Single(_http.Requests, r => Query(r.Uri)["q"]?.Contains("name = 'New'", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task RenameSendsTheNewNameAndRekeysTheCache()
    {
        var provider = Provider();
        _http.On(HttpMethod.Get, "googleapis.com/drive/v3/files?", (request, _) => Q(request) == "'root' in parents and name = 'a.txt' and trashed = false"
            ? FakeHttpHandler.Json(Walk("file1", "a.txt"))
            : Status(HttpStatusCode.BadRequest));
        _http.OnJson(HttpMethod.Patch, "googleapis.com/drive/v3/files/file1?", """{"id":"file1","name":"b.txt","mimeType":"text/plain"}""");
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files/file1?", """{"id":"file1","name":"b.txt","mimeType":"text/plain","size":"3","modifiedTime":"2024-01-01T00:00:00Z"}""");

        await provider.RenameItemAsync("/a.txt", "b.txt");
        var metadata = await provider.GetFileMetadataAsync("/b.txt");

        Assert.Equal("""{"name":"b.txt"}""", _http.Single(HttpMethod.Patch, "/files/file1").Body);
        Assert.Equal("/b.txt", metadata.Path);
        Assert.Equal(3, metadata.Size);
        Assert.Equal("id,name,mimeType,size,modifiedTime,md5Checksum", Query(_http.Single(HttpMethod.Get, "/files/file1?").Uri)["fields"]);
        Assert.Single(_http.Requests, r => r.Uri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetModificationDatePatchesModifiedTime()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", Walk("file1", "a.txt"));
        _http.OnJson(HttpMethod.Patch, "googleapis.com/drive/v3/files/file1?", "{}");

        await provider.SetModificationDateAsync("/a.txt", new DateTimeOffset(2022, 1, 2, 4, 4, 5, TimeSpan.FromHours(1)));

        Assert.Equal("""{"modifiedTime":"2022-01-02T03:04:05.000Z"}""", _http.Single(HttpMethod.Patch, "/files/file1").Body);
    }

    [Fact]
    public async Task ServerSideMoveAndCopyAreLeftToTheCallerAsOnMacOS()
    {
        var provider = Provider();

        Assert.Equal(CloudErrorKind.NotImplemented, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.MoveItemAsync("/a", "/b"))).Kind);
        Assert.Equal(CloudErrorKind.NotImplemented, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/a", "/b"))).Kind);
    }

    // MARK: - Search

    [Fact]
    public async Task SearchEscapesTheQueryAndScopesToAFolder()
    {
        var provider = Provider();
        NoSharedDrives();
        _http.On(HttpMethod.Get, "googleapis.com/drive/v3/files?", (request, _) => Q(request) switch
        {
            "'root' in parents and trashed = false" => FakeHttpHandler.Json(RootListing),
            "name contains 'O\\'Brien' and trashed = false" => FakeHttpHandler.Json("""{"files":[{"id":"s1","name":"O'Brien.txt","mimeType":"text/plain","parents":["fold1"]},{"id":"s2","name":"O'Brien 2.txt","mimeType":"text/plain","parents":["elsewhere"]}]}"""),
            "'fold1' in parents and name contains 'plan' and trashed = false" => FakeHttpHandler.Json("""{"files":[{"id":"s3","name":"plan.txt","mimeType":"text/plain"}]}"""),
            _ => Status(HttpStatusCode.BadRequest),
        });

        await provider.ListDirectoryAsync("/");
        var global = await provider.SearchFilesAsync("O'Brien", null);
        var scoped = await provider.SearchFilesAsync("plan", "/Docs");

        Assert.NotNull(global);
        Assert.Equal(new[] { "/Docs/O'Brien.txt", "/O'Brien 2.txt" }, global.Select(i => i.Path));
        Assert.Equal("/Docs/plan.txt", Assert.Single(scoped!).Path);
        var search = Query(_http.Requests.First(r => Query(r.Uri)["q"]?.StartsWith("name contains", StringComparison.Ordinal) == true).Uri);
        Assert.Equal("nextPageToken,files(id,name,mimeType,size,modifiedTime,md5Checksum,parents)", search["fields"]);
        Assert.Equal("100", search["pageSize"]);
    }

    // MARK: - Errors and tokens

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":403,"message":"Drive API has not been used"}}""", CloudErrorKind.ServerError)]
    [InlineData(HttpStatusCode.Forbidden, null, CloudErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, null, CloudErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InsufficientStorage, null, CloudErrorKind.QuotaExceeded)]
    [InlineData(HttpStatusCode.InternalServerError, null, CloudErrorKind.ServerError)]
    public async Task ErrorStatusesMapAsOnMacOS(HttpStatusCode status, string? body, CloudErrorKind expected)
    {
        var provider = Provider();
        _http.On(HttpMethod.Get, "googleapis.com/drive/v3/files?", (_, _) => Status(status, body));

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(expected, error.Kind);
    }

    [Fact]
    public async Task UnauthorizedForcesOneRefreshAndRetries()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Post, "oauth2.googleapis.com/token", """{"access_token":"access-2","expires_in":3600,"token_type":"Bearer"}""");
        _http.On(HttpMethod.Get, "googleapis.com/drive/v3/files?", (request, _) => request.Headers.Authorization!.Parameter == "access-2"
            ? FakeHttpHandler.Json(RootListing)
            : Status(HttpStatusCode.Unauthorized));
        NoSharedDrives();

        var items = await provider.ListDirectoryAsync("/");

        Assert.Equal(4, items.Count);
        Assert.Single(_http.Requests, r => r.Uri.Host == "oauth2.googleapis.com");
        Assert.Equal("access-2", _store.Load<GoogleDriveCredentials>(Key)!.AccessToken);
    }

    [Fact]
    public async Task PersistentUnauthorizedAsksForSignInAgain()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Post, "oauth2.googleapis.com/token", """{"access_token":"access-2","expires_in":3600,"token_type":"Bearer"}""");
        _http.On(HttpMethod.Get, "googleapis.com/drive/v3/files?", (_, _) => Status(HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
        Assert.True(error.NeedsReauthentication);
        Assert.Equal(2, _http.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ExpiredTokenRefreshesOnceForConcurrentCallers()
    {
        var provider = Provider(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var refreshes = 0;
        _http.On(HttpMethod.Post, "oauth2.googleapis.com/token", (_, _) =>
        {
            Interlocked.Increment(ref refreshes);
            Thread.Sleep(150);
            return FakeHttpHandler.Json("""{"access_token":"access-2","expires_in":3600,"token_type":"Bearer"}""");
        });
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/about", """{"storageQuota":{"limit":"100","usage":"40"}}""");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => provider.StorageQuotaAsync())));

        Assert.Equal(1, refreshes);
        var form = HttpUtility.ParseQueryString(_http.Single(HttpMethod.Post, "oauth2.googleapis.com/token").Body!);
        Assert.Equal("682536313816-j07jrk2kbff3sljb16vfal5soqs8vte9.apps.googleusercontent.com", form["client_id"]);
        Assert.Equal("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", form["client_secret"]);
        Assert.Equal("refresh-1", form["refresh_token"]);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.All(_http.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal)), r => Assert.Equal("Bearer access-2", r.Headers["Authorization"]));

        var stored = _store.Load<GoogleDriveCredentials>(Key)!;
        Assert.Equal("access-2", stored.AccessToken);
        Assert.Equal("refresh-1", stored.RefreshToken);
        Assert.True(stored.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(30));
    }

    [Fact]
    public async Task FailedRefreshMeansNotAuthenticated()
    {
        var provider = Provider(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        _http.OnJson(HttpMethod.Post, "oauth2.googleapis.com/token", """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
        Assert.Equal("access-1", _store.Load<GoogleDriveCredentials>(Key)!.AccessToken);
    }

    [Fact]
    public async Task WithoutCredentialsOperationsNeedSignIn()
    {
        var provider = Provider(stored: false);

        Assert.False(provider.IsAuthenticated);
        Assert.Equal(CloudErrorKind.NotAuthenticated, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"))).Kind);
        Assert.Null(await provider.StorageQuotaAsync());
        Assert.Empty(_http.Requests);
    }

    // MARK: - Quota and share links

    [Fact]
    public async Task QuotaParsesStringNumbersAndToleratesNoLimit()
    {
        var provider = Provider();
        var unlimited = false;
        _http.On(HttpMethod.Get, "googleapis.com/drive/v3/about", (_, _) => FakeHttpHandler.Json(unlimited
            ? """{"storageQuota":{"usage":"9007199254740993"}}"""
            : """{"storageQuota":{"limit":"16106127360","usage":"5368709120"}}"""));

        var quota = await provider.StorageQuotaAsync();
        unlimited = true;
        var noLimit = await provider.StorageQuotaAsync();

        Assert.Equal(5368709120, quota!.UsedBytes);
        Assert.Equal(16106127360, quota.TotalBytes);
        Assert.Equal(9007199254740993, noLimit!.UsedBytes);
        Assert.Null(noLimit.TotalBytes);
        Assert.Equal("storageQuota", Query(_http.Requests.First().Uri)["fields"]);
    }

    [Fact]
    public async Task ShareLinkGrantsAnyoneReaderAndReadsBothLinks()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", Walk("file1", "a.txt"));
        _http.OnJson(HttpMethod.Post, "/files/file1/permissions", """{"id":"anyoneWithLink"}""");
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files/file1?", """{"webViewLink":"https://drive.google.com/file/d/file1/view?usp=drivesdk","webContentLink":"https://drive.google.com/uc?id=file1&export=download"}""");

        var link = await provider.CreateShareLinkAsync("/a.txt", ShareLinkOptions.Default);

        Assert.Equal("https://drive.google.com/file/d/file1/view?usp=drivesdk", link.Url.AbsoluteUri);
        Assert.Equal("https://drive.google.com/uc?id=file1&export=download", link.DirectDownloadUrl!.AbsoluteUri);
        var grant = _http.Single(HttpMethod.Post, "/permissions");
        Assert.Equal("""{"role":"reader","type":"anyone"}""", grant.Body);
        Assert.Equal("true", Query(grant.Uri)["supportsAllDrives"]);
        Assert.Equal("webViewLink,webContentLink", Query(_http.Single(HttpMethod.Get, "/files/file1?").Uri)["fields"]);
        Assert.True(provider.ShareLinkCapabilities.CanCreate);
        Assert.True(provider.ShareLinkCapabilities.CanQueryExisting);
        Assert.True(provider.ShareLinkCapabilities.CanRemove);
        Assert.False(provider.ShareLinkCapabilities.CanUpdate);
        Assert.False(provider.ShareLinkCapabilities.HasOptions);
    }

    [Fact]
    public async Task ShareLinkSurfacesTheAdminsMessage()
    {
        var provider = Provider();
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", Walk("file1", "a.txt"));
        _http.OnJson(HttpMethod.Post, "/files/file1/permissions", """{"error":{"code":403,"message":"Sharing outside the domain is disabled by your administrator."}}""", HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/a.txt", ShareLinkOptions.Default));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("Sharing outside the domain is disabled by your administrator.", error.Message);
    }

    [Fact]
    public async Task ExistingShareLinkAndRemovalLookForTheAnyonePermission()
    {
        var provider = Provider();
        var shared = false;
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files?", Walk("file1", "a.txt"));
        _http.On(HttpMethod.Get, "/files/file1/permissions", (_, _) => FakeHttpHandler.Json(shared
            ? """{"permissions":[{"id":"u1","type":"user","role":"owner"},{"id":"anyoneWithLink","type":"anyone","role":"reader"}]}"""
            : """{"permissions":[{"id":"u1","type":"user","role":"owner"}]}"""));
        _http.OnJson(HttpMethod.Get, "googleapis.com/drive/v3/files/file1?", """{"webViewLink":"https://docs.google.com/document/d/file1/edit"}""");
        _http.On(HttpMethod.Delete, "/permissions/anyoneWithLink", (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));

        Assert.Null(await provider.ExistingShareLinkAsync("/a.txt"));
        var notShared = await Assert.ThrowsAsync<CloudProviderException>(() => provider.RemoveShareLinkAsync("/a.txt"));
        Assert.Equal(CloudErrorKind.CommandFailed, notShared.Kind);

        shared = true;
        var existing = await provider.ExistingShareLinkAsync("/a.txt");
        await provider.RemoveShareLinkAsync("/a.txt");

        Assert.Equal("https://docs.google.com/document/d/file1/edit", existing!.Url.AbsoluteUri);
        Assert.Null(existing.DirectDownloadUrl);
        Assert.Equal("permissions(id,type,role)", Query(_http.Requests.First(r => r.Uri.AbsolutePath.EndsWith("/permissions", StringComparison.Ordinal)).Uri)["fields"]);
        Assert.Equal("true", Query(_http.Single(HttpMethod.Delete, "/permissions/anyoneWithLink").Uri)["supportsAllDrives"]);
    }

    // MARK: - Connecting

    [Fact]
    public void ConnectFormWarnsAboutTheUserLimitAndEditOffersReauthorization()
    {
        var add = Provider(stored: false).ConnectSpec;
        var edit = Provider().ConnectSpec;

        Assert.True(add.SupportsInlineReauthentication);
        Assert.True(add.Modes.Single().OpensBrowser);
        Assert.Contains(add.Modes.Single().Fields, f => f.Kind == ConnectFieldKind.Warning && f.Label.Contains("reached its user limit", StringComparison.Ordinal));
        Assert.Contains(add.Modes.Single().Fields, f => f.Label == "Click Connect to sign in with your Google account. Your browser will open for authentication.");
        Assert.True(edit.SupportsInlineReauthentication);
        Assert.StartsWith("Click Re-authorize", Assert.Single(edit.Modes.Single().Fields).Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectRunsTheDesktopClientFlowAndStoresCredentials()
    {
        var provider = Provider(stored: false);
        Uri? authorize = null;
        provider.Authenticator = (build, _) =>
        {
            authorize = build("http://localhost:5123");
            var state = Query(authorize)["state"];
            return Task.FromResult(new OAuthCallback("http://localhost:5123", new Uri($"http://localhost:5123/?state={state}&code=4%2F0Acode&scope=x")));
        };
        _http.OnJson(HttpMethod.Post, "oauth2.googleapis.com/token", """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3599,"token_type":"Bearer","scope":"drive"}""");
        _http.OnJson(HttpMethod.Get, "googleapis.com/oauth2/v2/userinfo", """{"email":"jane@example.com","name":"Jane Doe"}""");
        var prompt = new ScriptedConnectPrompt();

        var result = await provider.ConnectAsync(ConnectValues.Empty("browser"), prompt);

        Assert.Equal("Jane Doe", result.AccountLabel);
        Assert.Null(result.DisplayName);
        Assert.True(provider.IsAuthenticated);
        Assert.NotNull(prompt.StatusLines[0]);
        Assert.Null(prompt.StatusLines[^1]);

        Assert.Equal("accounts.google.com", authorize!.Host);
        Assert.Equal("/o/oauth2/v2/auth", authorize.AbsolutePath);
        var query = Query(authorize);
        Assert.Equal("682536313816-j07jrk2kbff3sljb16vfal5soqs8vte9.apps.googleusercontent.com", query["client_id"]);
        Assert.Equal("http://localhost:5123", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("https://www.googleapis.com/auth/drive https://www.googleapis.com/auth/userinfo.profile https://www.googleapis.com/auth/userinfo.email", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);

        var form = HttpUtility.ParseQueryString(_http.Single(HttpMethod.Post, "oauth2.googleapis.com/token").Body!);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("4/0Acode", form["code"]);
        Assert.Equal("http://localhost:5123", form["redirect_uri"]);
        Assert.Equal("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", form["client_secret"]);
        Assert.Equal(query["code_challenge"], Pkce.Challenge(form["code_verifier"]!));
        Assert.Equal("Bearer new-access", _http.Single(HttpMethod.Get, "userinfo").Headers["Authorization"]);

        var stored = _store.Load<GoogleDriveCredentials>(Key)!;
        Assert.Equal("new-access", stored.AccessToken);
        Assert.Equal("new-refresh", stored.RefreshToken);
        Assert.Equal("jane@example.com", stored.UserEmail);
        Assert.Equal("Jane Doe", stored.DisplayName);
    }

    [Fact]
    public async Task FailedConnectLeavesStoredCredentialsUntouched()
    {
        var provider = Provider();
        var before = _store.Load(Key);
        provider.Authenticator = (build, _) =>
        {
            var state = Query(build("http://localhost:5123"))["state"];
            return Task.FromResult(new OAuthCallback("http://localhost:5123", new Uri($"http://localhost:5123/?state={state}&code=bad")));
        };
        _http.OnJson(HttpMethod.Post, "oauth2.googleapis.com/token", """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal(before, _store.Load(Key));
        Assert.True(provider.IsAuthenticated);
    }

    [Fact]
    public async Task ConnectRejectsACallbackForSomeoneElsesState()
    {
        var provider = Provider(stored: false);
        provider.Authenticator = (build, _) =>
        {
            build("http://localhost:5123");
            return Task.FromResult(new OAuthCallback("http://localhost:5123", new Uri("http://localhost:5123/?state=forged&code=x")));
        };

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
        Assert.Empty(_http.Requests);
        Assert.Null(_store.Load(Key));
    }

    [Fact]
    public async Task UnknownUserInfoStillSignsIn()
    {
        var provider = Provider(stored: false);
        provider.Authenticator = (build, _) =>
        {
            var state = Query(build("http://localhost:5123"))["state"];
            return Task.FromResult(new OAuthCallback("http://localhost:5123", new Uri($"http://localhost:5123/?state={state}&code=c")));
        };
        _http.OnJson(HttpMethod.Post, "oauth2.googleapis.com/token", """{"access_token":"a","expires_in":3600,"token_type":"Bearer"}""");
        _http.OnJson(HttpMethod.Get, "googleapis.com/oauth2/v2/userinfo", "{}", HttpStatusCode.Forbidden);

        var result = await provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt());

        Assert.Equal("Unknown", result.AccountLabel);
        Assert.Equal("Google Drive", CloudAccount.ComposeDisplayName(CloudProviderType.GoogleDrive, result.AccountLabel));
        Assert.Equal(string.Empty, _store.Load<GoogleDriveCredentials>(Key)!.RefreshToken);
    }

    [Fact]
    public async Task DisconnectForgetsTheCredentials()
    {
        var provider = Provider();

        await provider.DisconnectAsync();

        Assert.False(provider.IsAuthenticated);
        Assert.Null(_store.Load(Key));
    }

    [Fact]
    public void RegistryCreatesTheProvider()
    {
        using var provider = CloudProviderRegistry.Create(CloudProviderType.GoogleDrive, Guid.NewGuid(), new MemoryCredentialStore());

        Assert.IsType<GoogleDriveProvider>(provider);
        Assert.False(provider.IsAuthenticated);
    }
}
