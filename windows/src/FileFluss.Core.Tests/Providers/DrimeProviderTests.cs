// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Drime;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Providers.WebDavFamily;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// Port of the macOS <c>DrimeProviderTests</c> — how a listing page becomes panel rows, how a
/// big upload is cut into parts, which server message reaches the user — plus the HTTP
/// behaviour of the Windows provider against a scripted server.
/// </summary>
// Asserts English UI text, so it shares the collection of the tests that switch the language.
[Collection("Localization")]
public sealed class DrimeProviderTests : IDisposable
{
    private const string Api = "https://app.drime.cloud/api/v1";

    private readonly DavTempFolder _temp = new();
    private readonly FakeHttpHandler _http = new();
    private readonly MemoryCredentialStore _store = new();
    private readonly Guid _id = Guid.NewGuid();

    public void Dispose() => _temp.Dispose();

    private DrimeProvider Connected(DrimeCredentials? credentials = null)
    {
        _store.Save($"drime.{_id}", credentials ?? DrimeCredentials.PersonalToken("drime-token", "ana@example.com", "Ana"));
        var provider = new DrimeProvider(_id, _store, _http);
        provider.Client!.Delay = (_, _) => Task.CompletedTask;
        return provider;
    }

    private static string Entry(long id, string name, string type = "text", long size = 12, string? updated = "2024-01-20T15:30:00.000000Z")
        => $$"""{"id":{{id}},"name":{{JsonSerializer.Serialize(name)}},"type":"{{type}}","file_size":{{size}},"updated_at":"{{updated}}"}""";

    private static string Listing(int current, int last, params string[] entries)
        => $$"""{"data":[{{string.Join(',', entries)}}],"current_page":{{current}},"last_page":{{last}},"per_page":1000}""";

    private static string FileEntry(long id, string name, long size = 12) => $$"""{"fileEntry":{{Entry(id, name, size: size)}}}""";

    /// <summary>Serves listings: the root, and each folder id → its entries (one page).</summary>
    private void ServeFolders(Dictionary<long, string[]> folders)
        => _http.On(HttpMethod.Get, Api + "/drive/file-entries", (request, _) =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var id = query["folderId"] is { } folder ? long.Parse(folder, System.Globalization.CultureInfo.InvariantCulture) : 0;
            return FakeHttpHandler.Json(Listing(1, 1, folders.TryGetValue(id, out var entries) ? entries : []));
        });

    private static JsonElement Body(FakeHttpHandler.RecordedRequest request) => JsonDocument.Parse(request.Body!).RootElement;

    // MARK: - Listing page

    /// <summary>The documented root listing with the variations real responses carry: ids and sizes as strings, a null size on a folder, a millisecond client mtime.</summary>
    private const string ListingPage = """
        {
          "data": [
            {"id": 485529677, "name": "My Documents", "type": "folder", "hash": "NDg1NTI5Njc3fA",
             "file_size": null, "parent_id": null,
             "created_at": "2024-01-15T10:30:00.000000Z", "updated_at": "2024-01-15T10:30:00.000000Z"},
            {"id": "485529678", "name": "photo.jpg", "type": "image", "hash": "NDg1NTI5Njc4fA",
             "file_size": "2048576", "parent_id": null, "mime": "image/jpeg",
             "file_hash": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
             "created_at": "2024-01-14T08:20:00.000000Z", "updated_at": "2024-01-20T15:30:00.000000Z",
             "client_last_modified": 1712345678000},
            {"id": 485529679, "name": "notes.txt", "type": "text", "file_size": 12,
             "updated_at": "2024-01-20T15:30:00Z", "client_last_modified": null, "file_hash": ""}
          ],
          "folder": null, "current_page": 1, "last_page": 1, "per_page": 1000, "total": 3
        }
        """;

    [Fact]
    public void AListingPageBecomesRowsUnderTheFolderThatWasListed()
    {
        var items = DrimeApiClient.ItemsFromListingPage(ListingPage, "/Work/");
        Assert.Equal(["/Work/My Documents", "/Work/photo.jpg", "/Work/notes.txt"], items.Select(i => i.Path));

        // Only "folder" is a folder: Drime types files by content ("image", "text").
        Assert.Equal([true, false, false], items.Select(i => i.IsDirectory));
        Assert.Equal(2_048_576, items[1].Size);
        Assert.Equal(0, items[0].Size);
    }

    /// <summary>The original mtime is what a sync compares, so it wins over updated_at — and an empty one must not read as 1970.</summary>
    [Fact]
    public void TheClientsModificationTimeWinsOverTheServers()
    {
        var items = DrimeApiClient.ItemsFromListingPage(ListingPage, "/");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_712_345_678), items[1].ModificationDate);
        Assert.Equal(new DateTimeOffset(2024, 1, 20, 15, 30, 0, TimeSpan.Zero), items[2].ModificationDate);
    }

    [Fact]
    public void TheContentHashComesThroughAnEmptyOneDoesNot()
    {
        var items = DrimeApiClient.ItemsFromListingPage(ListingPage, "/");
        Assert.Equal("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", items[1].Checksum);
        Assert.Null(items[2].Checksum);
    }

    // MARK: - Pure rules

    [Fact]
    public void PartsAre16MiBUntilThe10000PartLimitForcesThemLarger()
    {
        const long mib = 1024 * 1024;
        Assert.Equal(16 * mib, DrimeApiClient.PartSize(6 * mib));
        Assert.Equal(16 * mib, DrimeApiClient.PartSize(100 * 1024 * mib));

        // 500 GiB / 10,000 parts needs more than 16 MiB per part.
        const long huge = 500 * 1024 * mib;
        var part = DrimeApiClient.PartSize(huge);
        Assert.Equal(0, part % mib);
        Assert.True((huge + part - 1) / part <= 10_000);
        Assert.True(part - mib < (huge + 9_999) / 10_000);
    }

    /// <summary>A token without the right permission is the likeliest failure with personal tokens, and Drime names the missing scope.</summary>
    [Fact]
    public void AMissingScopeIsReportedInDrimesWords()
    {
        var error = DrimeJson.MapHttpError(403, """{"message": "This API key is missing the required scope: files.write"}""");
        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Contains("files.write", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AValidationErrorShowsTheFieldMessageNotLaravelsSummary()
        => Assert.Equal(
            "The name has already been taken.",
            DrimeJson.ServerMessage("""{"status": "error", "message": "The given data was invalid.", "errors": {"name": ["The name has already been taken."]}}"""));

    [Fact]
    public void AnUnexplained403AndA401KeepTheirUsualMeaning()
    {
        Assert.Equal(CloudErrorKind.Unauthorized, DrimeJson.MapHttpError(403, """{"message": "This action is unauthorized."}""").Kind);
        Assert.Equal(CloudErrorKind.NotAuthenticated, DrimeJson.MapHttpError(401, null).Kind);
        Assert.Equal(CloudErrorKind.QuotaExceeded, DrimeJson.MapHttpError(507, null).Kind);
        Assert.Equal(CloudErrorKind.FileTooLarge, DrimeJson.MapHttpError(413, null).Kind);
        Assert.Equal((CloudErrorKind.ServerError, 422), (DrimeJson.MapHttpError(422, "oops").Kind, DrimeJson.MapHttpError(422, "oops").StatusCode));
    }

    [Fact]
    public void ExpiryDatesAreWrittenTheWayDrimeWritesThem()
    {
        Assert.Equal("2021-03-06T17:34:00.000000Z", DrimeJson.FormatTimestamp(DateTimeOffset.FromUnixTimeSeconds(1_615_052_040)));
        Assert.Equal("2021-03-06T17:34:00.250000Z", DrimeJson.FormatTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(1_615_052_040_250).ToOffset(TimeSpan.FromHours(2))));
    }

    [Fact]
    public void DrimeIsACloudStorageProviderWithTheMacRawValue()
    {
        Assert.Equal(ProviderCategory.CloudStorage, CloudProviderType.Drime.Category());
        Assert.Equal("Drime", CloudProviderType.Drime.DisplayName());
        Assert.Equal("Drime", CloudProviderType.Drime.LogoAssetName());
        Assert.False(CloudProviderType.Drime.IsReadOnly());
        Assert.Equal("\"drime\"", JsonSerializer.Serialize(CloudProviderType.Drime));
    }

    [Fact]
    public void RegistryCreatesTheProvider()
    {
        Assert.True(CloudProviderRegistry.IsAvailable(CloudProviderType.Drime));
        using var provider = CloudProviderRegistry.Create(CloudProviderType.Drime, _id, _store);
        Assert.IsType<DrimeProvider>(provider);
    }

    // MARK: - Connect

    [Fact]
    public async Task APersonalTokenIsCheckedAgainstUsersMeAndNamesTheAccount()
    {
        _http.OnJson(HttpMethod.Get, Api + "/users/me", """{"user":{"id":7,"email":"ana@example.com","display_name":"Ana Novak"}}""");
        using var provider = new DrimeProvider(_id, _store, _http);

        var result = await provider.ConnectAsync(new ConnectValues("default", new Dictionary<string, string> { ["token"] = " 12|abc \n" }), new ScriptedConnectPrompt());

        Assert.Equal("ana@example.com", result.AccountLabel);
        var stored = _store.Load<DrimeCredentials>($"drime.{_id}")!;
        Assert.Equal((DrimeCredentialKind.PersonalToken, "12|abc", DateTimeOffset.MaxValue), (stored.Kind, stored.AccessToken, stored.ExpiresAt));
        Assert.Equal("Bearer 12|abc", Assert.Single(_http.Requests).Header("Authorization"));
    }

    [Fact]
    public async Task ARejectedTokenSaysSoAndLeavesStoredCredentialsUntouched()
    {
        using var provider = Connected();
        _http.On(HttpMethod.Get, Api + "/users/me", (_, _) => FakeHttpHandler.Json("""{"message":"Unauthenticated."}""", HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<CloudProviderException>(() =>
            provider.ConnectAsync(new ConnectValues("default", new Dictionary<string, string> { ["token"] = "wrong" }), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.StartsWith("Drime didn't accept this token.", error.Message, StringComparison.Ordinal);
        Assert.Equal("drime-token", _store.Load<DrimeCredentials>($"drime.{_id}")!.AccessToken);
    }

    [Fact]
    public async Task TheConnectFormMatchesTheMacSheetAndEditingKeepsTheToken()
    {
        using (var fresh = new DrimeProvider(_id, _store, _http))
        {
            var mode = Assert.Single(fresh.ConnectSpec.Modes);
            Assert.False(mode.OpensBrowser);
            Assert.StartsWith("Create a personal access token in your Drime account", mode.Fields[0].Label);
            Assert.Equal("https://app.drime.cloud/account-settings#developers", mode.Fields.Single(f => f.Kind == ConnectFieldKind.Link).Url);
            var token = Assert.Single(mode.Fields, f => f.HasValue);
            Assert.Equal(("token", ConnectFieldKind.Password, "Personal Access Token"), (token.Key, token.Kind, token.Label));
        }

        using var provider = Connected();
        Assert.Equal(
            "To connect Drime with a different token or account, remove this account and add it again.",
            Assert.Single(provider.ConnectSpec.Modes.Single().Fields).Label);
        Assert.Equal("ana@example.com", (await provider.ConnectAsync(ConnectValues.Empty(), new ScriptedConnectPrompt())).AccountLabel);
        Assert.Empty(_http.Requests);
    }

    // MARK: - Listing

    [Fact]
    public async Task ListingsFollowEveryPageAndResolveSubfoldersById()
    {
        _http.On(HttpMethod.Get, Api + "/drive/file-entries", (request, _) =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            return FakeHttpHandler.Json((query["folderId"], query["page"]) switch
            {
                (null, _) => Listing(1, 1, Entry(10, "Work", "folder", 0)),
                ("10", "1") => Listing(1, 2, Entry(11, "a.txt")),
                ("10", "2") => Listing(2, 2, Entry(12, "b.txt")),
                _ => "{}",
            });
        });
        using var provider = Connected();

        var items = await provider.ListDirectoryAsync("/Work/");

        Assert.Equal(["/Work/a.txt", "/Work/b.txt"], items.Select(i => i.Path));
        var uris = _http.Requests.Select(r => r.Uri.Query).ToList();
        Assert.Equal("?workspaceId=0&perPage=1000&page=1&orderBy=name&orderDir=asc", uris[0]);
        Assert.Equal("?workspaceId=0&perPage=1000&page=1&orderBy=name&orderDir=asc&folderId=10", uris[1]);
        Assert.EndsWith("page=2&orderBy=name&orderDir=asc&folderId=10", uris[2], StringComparison.Ordinal);
        Assert.All(_http.Requests, r => Assert.Equal("Bearer drime-token", r.Header("Authorization")));

        // The folder id is cached: listing it again doesn't walk from the root.
        await provider.ListDirectoryAsync("/Work");
        Assert.Equal(5, _http.Requests.Count);
    }

    /// <summary>Past its last page Drime serves that page again; returning what we have would make the rest of the folder look deleted.</summary>
    [Fact]
    public async Task AFolderLargerThanDrimeWillListIsRefusedNotTruncated()
    {
        _http.On(HttpMethod.Get, Api + "/drive/file-entries", (request, _) =>
            FakeHttpHandler.Json(Listing(1, 3, Entry(1, "a"))));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.StartsWith("This Drime folder holds more items", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARateLimitedListingIsRetried()
    {
        var calls = 0;
        _http.On(HttpMethod.Get, Api + "/drive/file-entries", (_, _) => Interlocked.Increment(ref calls) < 3
            ? FakeHttpHandler.Json("{}", HttpStatusCode.TooManyRequests)
            : FakeHttpHandler.Json(Listing(1, 1, Entry(1, "a.txt"))));
        using var provider = Connected();

        Assert.Single(await provider.ListDirectoryAsync("/"));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ADeletedPersonalTokenAsksForANewSignIn()
    {
        _http.On(HttpMethod.Get, Api + "/drive/file-entries", (_, _) => FakeHttpHandler.Json("{}", HttpStatusCode.Unauthorized));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.True(error.NeedsReauthentication);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task AMissingItemIsNotFound()
    {
        ServeFolders(new() { [0] = [Entry(1, "a.txt")] });
        using var provider = Connected();

        Assert.Equal(CloudErrorKind.NotFound, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.GetFileMetadataAsync("/b.txt"))).Kind);
        Assert.Equal(12, (await provider.GetFileMetadataAsync("/a.txt")).Size);
    }

    // MARK: - Transfers

    [Fact]
    public async Task ADownloadStreamsTheEntrysContent()
    {
        ServeFolders(new() { [0] = [Entry(1, "Docs", "folder", 0)], [1] = [Entry(42, "report.pdf")] });
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7 report");
        _http.OnBytes(HttpMethod.Get, Api + "/file-entries/42", bytes);
        using var provider = Connected();
        var target = Path.Combine(_temp.Path, "report.pdf");
        long reported = 0;

        await provider.DownloadFileAsync("/Docs/report.pdf", target, delta => reported += delta);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        Assert.Equal(bytes.Length, reported);
        Assert.Equal("Bearer drime-token", _http.Single(HttpMethod.Get, "/file-entries/42").Header("Authorization"));
    }

    [Fact]
    public async Task ASmallFileGoesUpInOneMultipartPost()
    {
        ServeFolders(new() { [0] = [Entry(5, "Docs", "folder", 0)], [5] = [] });
        _http.On(HttpMethod.Post, Api + "/uploads", (_, _) => FakeHttpHandler.Json(FileEntry(77, "Grüße \"final\".txt", 20)));
        using var provider = Connected();
        var file = _temp.File("local.txt", 20);
        long reported = 0;

        await provider.UploadFileAsync(file, "/Docs/Grüße \"final\".txt", delta => reported += delta);

        Assert.Equal(20, reported);
        var upload = _http.Single(HttpMethod.Post, "/uploads");
        Assert.StartsWith("multipart/form-data; boundary=", upload.Header("Content-Type"), StringComparison.Ordinal);
        Assert.Contains("name=\"parentId\"\r\n\r\n5\r\n", upload.Body, StringComparison.Ordinal);
        Assert.Contains("name=\"workspaceId\"\r\n\r\n0\r\n", upload.Body, StringComparison.Ordinal);
        Assert.Contains("name=\"relativePath\"\r\n\r\nGrüße \"final\".txt\r\n", upload.Body, StringComparison.Ordinal);

        // The file name goes out as raw UTF-8 with quotes escaped, the only form PHP reads.
        Assert.Contains("name=\"file\"; filename=\"Grüße %22final%22.txt\"", upload.Body, StringComparison.Ordinal);
        Assert.Contains("abcdefghijklmnopqrst", upload.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(_http.Requests, r => r.Uri.AbsolutePath.EndsWith("/file-entries/delete", StringComparison.Ordinal));
    }

    /// <summary>Drime never overwrites: the new file is stored as "name (1)", then the old one goes to the trash and the new one takes its name.</summary>
    [Fact]
    public async Task ReplacingAFileUploadsFirstThenTrashesTheOldOneAndRenames()
    {
        ServeFolders(new() { [0] = [Entry(3, "a.txt")] });
        _http.On(HttpMethod.Post, Api + "/uploads", (_, _) => FakeHttpHandler.Json(FileEntry(4, "a (1).txt")));
        _http.OnJson(HttpMethod.Post, Api + "/file-entries/delete", """{"status":"success"}""");
        _http.On(HttpMethod.Post, Api + "/file-entries/4", (_, _) => FakeHttpHandler.Json(FileEntry(4, "a.txt")));
        using var provider = Connected();

        await provider.UploadFileAsync(_temp.File("a.txt", 3), "/a.txt");

        var order = _http.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Uri.AbsolutePath).ToList();
        Assert.Equal(["/api/v1/uploads", "/api/v1/file-entries/delete", "/api/v1/file-entries/4"], order);
        var trash = Body(_http.Single(HttpMethod.Post, "/file-entries/delete"));
        Assert.Equal(3, trash.GetProperty("entryIds")[0].GetInt64());
        Assert.False(trash.GetProperty("deleteForever").GetBoolean());
        var rename = _http.Single(HttpMethod.Post, "/file-entries/4");
        Assert.Equal("PUT", rename.Header("X-HTTP-Method-Override"));
        Assert.Equal("?workspaceId=0", rename.Uri.Query);
        Assert.Equal("a.txt", Body(rename).GetProperty("name").GetString());
    }

    [Fact]
    public async Task AFolderInTheWayIsNotReplacedByAFile()
    {
        ServeFolders(new() { [0] = [Entry(3, "a.txt", "folder", 0)] });
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(_temp.File("a.txt", 3), "/a.txt"));

        Assert.Equal("A folder named “a.txt” is already there.", error.Message);
        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Post);
    }

    /// <summary>
    /// The multipart path, scaled down: 10 bytes in 4-byte parts. Each part is signed and PUT
    /// to its presigned URL without the API token; a part that fails with a 500 is resent
    /// without its bytes counting twice; the ETags go back in part order, quotes and all.
    /// </summary>
    [Fact]
    public async Task ALargeFileGoesUpInSignedPartsThenIsRegistered()
    {
        ServeFolders(new() { [0] = [] });
        _http.OnJson(HttpMethod.Post, Api + "/s3/multipart/create", """{"key":"uploads/ab/cd/9f2e.bin","uploadId":"up-1"}""");
        _http.On(HttpMethod.Post, Api + "/s3/multipart/batch-sign-part-urls", (_, body) =>
        {
            var number = JsonDocument.Parse(body!).RootElement.GetProperty("partNumbers")[0].GetInt32();
            return FakeHttpHandler.Json($$"""{"urls":[{"partNumber":{{number}},"url":"https://storage.example.com/bucket/key?partNumber={{number}}&X-Amz-Signature=sig"}]}""");
        });
        var failures = new ConcurrentDictionary<string, int>();
        _http.On(HttpMethod.Put, "https://storage.example.com/bucket/key", (request, _) =>
        {
            var part = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["partNumber"]!;
            if (part == "2" && failures.AddOrUpdate(part, 1, (_, n) => n + 1) == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("slow down") };
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) };
            response.Headers.TryAddWithoutValidation("ETag", $"\"etag-{part}\"");
            return response;
        });
        _http.OnJson(HttpMethod.Post, Api + "/s3/multipart/complete", """{"location":"https://storage.example.com/bucket/key"}""");
        _http.On(HttpMethod.Post, Api + "/s3/entries", (_, _) => FakeHttpHandler.Json(FileEntry(90, "video.mov", 10)));
        using var provider = Connected();
        provider.Client!.MultipartThresholdBytes = 5;
        provider.Client.PartSizeOverride = 4;
        var file = _temp.File("video.mov", 10);
        long reported = 0;

        await provider.UploadFileAsync(file, "/video.mov", delta => Interlocked.Add(ref reported, delta));

        Assert.Equal(10, reported);
        var create = Body(_http.Single(HttpMethod.Post, "/s3/multipart/create"));
        Assert.Equal(("video.mov", "mov", 10L, "video.mov", 0), (create.GetProperty("filename").GetString(), create.GetProperty("extension").GetString(),
            create.GetProperty("size").GetInt64(), create.GetProperty("relativePath").GetString(), create.GetProperty("workspaceId").GetInt32()));
        Assert.False(create.TryGetProperty("parentId", out _));

        var puts = _http.All(HttpMethod.Put);
        Assert.Equal(4, puts.Length);
        Assert.All(puts, put => Assert.Null(put.Header("Authorization")));
        Assert.Equal(["abcd", "efgh", "ij"], puts.Select(p => p.Body!).Distinct().Order(StringComparer.Ordinal));

        var complete = Body(_http.Single(HttpMethod.Post, "/s3/multipart/complete"));
        Assert.Equal("up-1", complete.GetProperty("uploadId").GetString());
        Assert.Equal(
            ["1:\"etag-1\"", "2:\"etag-2\"", "3:\"etag-3\""],
            complete.GetProperty("parts").EnumerateArray().Select(p => $"{p.GetProperty("PartNumber").GetInt32()}:{p.GetProperty("ETag").GetString()}"));

        var entry = Body(_http.Single(HttpMethod.Post, "/s3/entries"));
        Assert.Equal(("9f2e.bin", "video.mov", "mov"), (entry.GetProperty("filename").GetString(), entry.GetProperty("clientName").GetString(), entry.GetProperty("clientExtension").GetString()));
        Assert.DoesNotContain(_http.Requests, r => r.Uri.AbsolutePath.EndsWith("/abort", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedMultipartUploadIsAborted()
    {
        ServeFolders(new() { [0] = [] });
        _http.OnJson(HttpMethod.Post, Api + "/s3/multipart/create", """{"key":"k","uploadId":"up-2"}""");
        _http.On(HttpMethod.Post, Api + "/s3/multipart/batch-sign-part-urls", (_, _) => FakeHttpHandler.Json("""{"urls":[{"partNumber":1,"url":"https://storage.example.com/p"}]}"""));
        _http.On(HttpMethod.Put, "https://storage.example.com/p", (_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<Error>SignatureDoesNotMatch</Error>") });
        _http.OnJson(HttpMethod.Post, Api + "/s3/multipart/abort", "{}");
        using var provider = Connected();
        provider.Client!.MultipartThresholdBytes = 5;
        provider.Client.PartSizeOverride = 4;

        await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(_temp.File("x.bin", 10), "/x.bin"));

        var abort = Body(_http.Single(HttpMethod.Post, "/s3/multipart/abort"));
        Assert.Equal(("k", "up-2"), (abort.GetProperty("key").GetString(), abort.GetProperty("uploadId").GetString()));
        Assert.DoesNotContain(_http.Requests, r => r.Uri.AbsolutePath.EndsWith("/s3/entries", StringComparison.Ordinal));
    }

    // MARK: - Changes

    [Fact]
    public async Task MovingToAnotherFolderMovesThenRenamesWhenTheNameChanges()
    {
        ServeFolders(new() { [0] = [Entry(1, "A", "folder", 0), Entry(2, "B", "folder", 0)], [1] = [Entry(7, "x.txt")] });
        _http.OnJson(HttpMethod.Post, Api + "/file-entries/move", """{"status":"success"}""");
        _http.On(HttpMethod.Post, Api + "/file-entries/7", (_, _) => FakeHttpHandler.Json(FileEntry(7, "y.txt")));
        using var provider = Connected();

        await provider.MoveItemAsync("/A/x.txt", "/B/y.txt");

        var move = _http.Single(HttpMethod.Post, "/file-entries/move");
        Assert.Equal("?workspaceId=0", move.Uri.Query);
        var body = Body(move);
        Assert.Equal((7L, 2L, 0), (body.GetProperty("entryIds")[0].GetInt64(), body.GetProperty("destinationId").GetInt64(), body.GetProperty("workspaceId").GetInt32()));
        Assert.Equal("y.txt", Body(_http.Single(HttpMethod.Post, "/file-entries/7")).GetProperty("name").GetString());
    }

    [Fact]
    public async Task MovingToTheRootLeavesTheDestinationOut()
    {
        ServeFolders(new() { [0] = [Entry(1, "A", "folder", 0)], [1] = [Entry(7, "x.txt")] });
        _http.OnJson(HttpMethod.Post, Api + "/file-entries/move", """{"status":"success"}""");
        using var provider = Connected();

        await provider.MoveItemAsync("/A/x.txt", "/x.txt");

        Assert.False(Body(_http.Single(HttpMethod.Post, "/file-entries/move")).TryGetProperty("destinationId", out _));
        Assert.DoesNotContain(_http.Requests, r => r.Uri.AbsolutePath.EndsWith("/file-entries/7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACopyIsDuplicatedServerSideAndRenamedWhenDrimeAddsASuffix()
    {
        ServeFolders(new() { [0] = [Entry(1, "a.txt")] });
        _http.OnJson(HttpMethod.Post, Api + "/file-entries/duplicate", $$"""{"entries":[{{Entry(8, "a (1).txt")}}]}""");
        _http.On(HttpMethod.Post, Api + "/file-entries/8", (_, _) => FakeHttpHandler.Json(FileEntry(8, "copy.txt")));
        using var provider = Connected();

        await provider.CopyItemAsync("/a.txt", "/copy.txt");

        Assert.Equal(1, Body(_http.Single(HttpMethod.Post, "/file-entries/duplicate")).GetProperty("entryIds")[0].GetInt64());
        Assert.Equal("copy.txt", Body(_http.Single(HttpMethod.Post, "/file-entries/8")).GetProperty("name").GetString());
    }

    [Fact]
    public async Task NewFoldersDeletesAndModificationDates()
    {
        ServeFolders(new() { [0] = [Entry(1, "Docs", "folder", 0), Entry(2, "a.txt")], [1] = [] });
        _http.OnJson(HttpMethod.Post, Api + "/folders", """{"folder":{"id":30,"name":"New","type":"folder"}}""");
        _http.OnJson(HttpMethod.Post, Api + "/file-entries/delete", """{"status":"success"}""");
        _http.OnJson(HttpMethod.Post, Api + "/file-entries/2/metadata", "");
        using var provider = Connected();

        await provider.CreateDirectoryAsync("/Docs/New");
        await provider.CreateDirectoryAsync("/Docs");
        await provider.DeleteItemAsync("/a.txt");
        await provider.SetModificationDateAsync("/a.txt", DateTimeOffset.FromUnixTimeMilliseconds(1_712_345_678_123));

        var folder = _http.Single(HttpMethod.Post, "/folders");
        Assert.Equal("?workspaceId=0", folder.Uri.Query);
        Assert.Equal(("New", 1L), (Body(folder).GetProperty("name").GetString(), Body(folder).GetProperty("parentId").GetInt64()));
        Assert.Equal(2, Body(_http.Single(HttpMethod.Post, "/file-entries/delete")).GetProperty("entryIds")[0].GetInt64());
        Assert.Equal(1_712_345_678_123, Body(_http.Single(HttpMethod.Post, "/metadata")).GetProperty("lastModified").GetInt64());
    }

    // MARK: - Share links

    [Fact]
    public async Task AShareLinkCarriesPasswordExpiryAndTheDownloadSwitch()
    {
        ServeFolders(new() { [0] = [Entry(5, "a.pdf")] });
        _http.OnJson(HttpMethod.Post, Api + "/file-entries/5/shareable-link", """
            {"link":{"id":1,"hash":"aBcD123","password":"$2y$10$hashed","expires_at":"2026-12-01T00:00:00.000000Z","allow_download":false}}
            """);
        using var provider = Connected();
        Assert.True(provider.ShareLinkCapabilities is { CanCreate: true, CanQueryExisting: true, CanUpdate: true, CanRemove: true, SupportsPassword: true, SupportsExpiry: true, SupportsDownloadToggle: true });

        var link = await provider.CreateShareLinkAsync("/a.pdf", new ShareLinkOptions
        {
            Password = "s3cret",
            Expiry = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
            AllowDownload = false,
        });

        Assert.Equal("https://app.drime.cloud/drive/shares/aBcD123", link.Url.AbsoluteUri);
        Assert.True(link.HasPassword);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), link.ExpiresAt);
        var body = Body(_http.Single(HttpMethod.Post, "/shareable-link"));
        Assert.Equal("s3cret", body.GetProperty("password").GetString());
        Assert.Equal("2026-12-01T00:00:00.000000Z", body.GetProperty("expires_at").GetString());
        Assert.False(body.GetProperty("allow_edit").GetBoolean());
        Assert.False(body.GetProperty("allow_download").GetBoolean());
    }

    [Fact]
    public async Task ALinkWithoutOptionsSendsNoPasswordOrExpiry()
    {
        ServeFolders(new() { [0] = [Entry(5, "a.pdf")] });
        _http.OnJson(HttpMethod.Post, Api + "/file-entries/5/shareable-link", """{"link":{"hash":"h1","password":null,"expires_at":null}}""");
        using var provider = Connected();

        var link = await provider.CreateShareLinkAsync("/a.pdf", ShareLinkOptions.Default);

        Assert.False(link.HasPassword);
        Assert.Null(link.ExpiresAt);
        var body = Body(_http.Single(HttpMethod.Post, "/shareable-link"));
        Assert.False(body.TryGetProperty("password", out _));
        Assert.False(body.TryGetProperty("expires_at", out _));
        Assert.True(body.GetProperty("allow_download").GetBoolean());
    }

    [Fact]
    public async Task UpdatingALinkUsesTheCamelCaseRouteAndRemovingDeletesIt()
    {
        ServeFolders(new() { [0] = [Entry(5, "a.pdf")] });
        _http.OnJson(HttpMethod.Put, Api + "/file-entries/5/shareable-link", """{"link":{"hash":"h1","password":null,"expires_at":"2027-01-01T00:00:00Z"}}""");
        _http.OnJson(HttpMethod.Delete, Api + "/file-entries/5/shareable-link", "");
        using var provider = Connected();

        var link = await provider.UpdateShareLinkAsync("/a.pdf", new ShareLinkOptions { Expiry = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) });
        await provider.RemoveShareLinkAsync("/a.pdf");

        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), link.ExpiresAt);
        var body = Body(_http.Single(HttpMethod.Put, "/shareable-link"));
        Assert.Equal("2027-01-01T00:00:00.000000Z", body.GetProperty("expiresAt").GetString());
        Assert.True(body.GetProperty("allowDownload").GetBoolean());
        Assert.False(body.GetProperty("allowEdit").GetBoolean());
        Assert.Single(_http.All(HttpMethod.Delete));
    }

    [Fact]
    public async Task AnExistingLinkIsReadBackAndAMissingOneIsNull()
    {
        ServeFolders(new() { [0] = [Entry(5, "shared.pdf"), Entry(6, "private.pdf")] });
        _http.OnJson(HttpMethod.Get, Api + "/file-entries/5/shareable-link", """{"link":{"hash":"h5","password":"x"}}""");
        _http.On(HttpMethod.Get, Api + "/file-entries/6/shareable-link", (_, _) => FakeHttpHandler.Json("""{"message":"Not found"}""", HttpStatusCode.NotFound));
        using var provider = Connected();

        Assert.Equal("https://app.drime.cloud/drive/shares/h5", (await provider.ExistingShareLinkAsync("/shared.pdf"))!.Url.AbsoluteUri);
        Assert.Null(await provider.ExistingShareLinkAsync("/private.pdf"));
    }

    [Fact]
    public async Task ARefusedLinkShowsDrimesOwnMessage()
    {
        ServeFolders(new() { [0] = [Entry(5, "a.pdf")] });
        _http.On(HttpMethod.Post, Api + "/file-entries/5/shareable-link", (_, _) =>
            FakeHttpHandler.Json("""{"message":"The given data was invalid.","errors":{"password":["Password protection requires a paid plan."]}}""", HttpStatusCode.UnprocessableEntity));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/a.pdf", new ShareLinkOptions { Password = "x" }));

        Assert.Equal((CloudErrorKind.CommandFailed, "Password protection requires a paid plan."), (error.Kind, error.Message));
    }

    // MARK: - Quota and tokens

    [Fact]
    public async Task TheQuotaIsUsedOutOfThePlansTotal()
    {
        _http.OnJson(HttpMethod.Get, Api + "/user/space-usage", """{"used":1048576,"available":21474836480,"status":"success"}""");
        using var provider = Connected();

        var quota = await provider.StorageQuotaAsync();

        Assert.Equal((1_048_576L, (long?)21_474_836_480), (quota!.UsedBytes, quota.TotalBytes));
        Assert.Equal("?workspaceId=0", Assert.Single(_http.Requests).Uri.Query);
    }

    /// <summary>
    /// Drime rotates refresh tokens and revokes the grant when one is reused, so concurrent
    /// callers share one refresh and the new token is stored before it is used.
    /// </summary>
    [Fact]
    public async Task AnExpiredOAuthTokenIsRefreshedOnceForConcurrentCallers()
    {
        var refreshes = 0;
        _http.On(HttpMethod.Post, "https://app.drime.cloud/oauth/token", (_, body) =>
        {
            Interlocked.Increment(ref refreshes);
            Assert.Contains("grant_type=refresh_token", body, StringComparison.Ordinal);
            Assert.Contains("refresh_token=old-refresh", body, StringComparison.Ordinal);
            Thread.Sleep(50);
            return FakeHttpHandler.Json("""{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}""");
        });
        _http.OnJson(HttpMethod.Get, Api + "/drive/file-entries", Listing(1, 1, Entry(1, "a.txt")));
        using var provider = Connected(new DrimeCredentials(DrimeCredentialKind.OAuth, "old-access", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-5), "ana@example.com", "Ana"));

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.ListDirectoryAsync("/")));

        Assert.Equal(1, refreshes);
        Assert.All(_http.All(HttpMethod.Get), r => Assert.Equal("Bearer new-access", r.Header("Authorization")));
        var stored = _store.Load<DrimeCredentials>($"drime.{_id}")!;
        Assert.Equal(("new-access", "new-refresh"), (stored.AccessToken, stored.RefreshToken));
    }

    [Fact]
    public async Task ARevokedGrantAsksForANewSignIn()
    {
        _http.On(HttpMethod.Post, "https://app.drime.cloud/oauth/token", (_, _) => FakeHttpHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));
        using var provider = Connected(new DrimeCredentials(DrimeCredentialKind.OAuth, "old-access", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-5), "", "Ana"));

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
    }
}
