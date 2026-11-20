// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Box;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

public sealed class BoxProviderTests : IDisposable
{
    private const string TokenUrl = "api.box.com/oauth2/token";
    private const string Api = "https://api.box.com/2.0";
    private const string Upload = "https://upload.box.com/api/2.0";

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ff-box-" + Guid.NewGuid().ToString("N"));

    /// <summary>Folder id → children, served by <see cref="ServeTree"/> as /folders/{id}/items.</summary>
    private readonly Dictionary<string, List<Node>> _tree = new() { ["0"] = [] };

    public BoxProviderTests()
    {
        Directory.CreateDirectory(_temp);
    }

    private string Key => $"box.{_accountId}";

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

    // MARK: - Lifecycle

    [Fact]
    public async Task WithoutStoredCredentialsTheProviderIsSignedOut()
    {
        using var provider = new BoxProvider(_accountId, _store, _http);

        Assert.False(provider.IsAuthenticated);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
        Assert.Null(await provider.StorageQuotaAsync());
    }

    [Fact]
    public void RegistryCreatesTheBoxProviderWithItsLimits()
    {
        using var provider = CloudProviderRegistry.Create(CloudProviderType.Box, _accountId, _store);

        Assert.IsType<BoxProvider>(provider);
        Assert.Equal(50L * 1024 * 1024 * 1024, provider.MaxUploadFileSize);
        Assert.True(provider.ConnectSpec.SupportsInlineReauthentication);
    }

    [Fact]
    public async Task SettingAModificationDateIsNotSupported()
    {
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.SetModificationDateAsync("/a.txt", DateTimeOffset.UtcNow));

        Assert.Equal(CloudErrorKind.NotImplemented, error.Kind);
    }

    // MARK: - Credential persistence (issue #46, port of BoxCredentialPersistenceTests)

    [Fact]
    public async Task TheClientHandsOutRotatedCredentialsSoTheyCanBeStored()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, """{"access_token":"access-2","refresh_token":"refresh-2","expires_in":3600,"token_type":"bearer"}""");
        BoxCredentials? recorded = null;
        var client = new BoxApiClient(
            _http.CreateClient(),
            new BoxCredentials("access-1", "refresh-1", DateTimeOffset.UtcNow.AddHours(1), "someone@example.com", "Someone"),
            credentials => recorded = credentials);

        // The callback is what carries a rotated token to the store. If it ever stops being
        // wired, the bug returns silently — nothing fails until the next launch.
        Assert.Null(recorded);

        await client.ForceRefreshAsync(CancellationToken.None);

        Assert.Equal("refresh-2", recorded!.RefreshToken);
        Assert.Equal("access-2", recorded.AccessToken);
        Assert.Equal("someone@example.com", recorded.UserLogin);
    }

    [Fact]
    public void CredentialsSurviveARoundTripThroughStorage()
    {
        var credentials = new BoxCredentials("access-1", "refresh-2", DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), "someone@example.com", "Someone");

        _store.Save(Key, credentials);
        var decoded = _store.Load<BoxCredentials>(Key)!;

        Assert.Equal("refresh-2", decoded.RefreshToken);
        Assert.Equal("someone@example.com", decoded.UserLogin);
        Assert.Equal(credentials, decoded);
    }

    [Fact]
    public async Task ARotatedRefreshTokenIsPersistedImmediately()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, """{"access_token":"access-2","refresh_token":"refresh-2","expires_in":3600,"token_type":"bearer"}""");
        ServeTree();
        using var provider = Provider(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await provider.ListDirectoryAsync("/");

        var stored = _store.Load<BoxCredentials>(Key)!;
        Assert.Equal("refresh-2", stored.RefreshToken);
        Assert.Equal("access-2", stored.AccessToken);
        Assert.Equal("Someone", stored.DisplayName);
        var refresh = _http.Single(HttpMethod.Post, TokenUrl);
        var form = HttpUtility.ParseQueryString(refresh.Body!);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("refresh-1", form["refresh_token"]);
        Assert.Equal(BoxApiClient.ClientId, form["client_id"]);
        Assert.Equal(BoxApiClient.ClientSecret, form["client_secret"]);
        Assert.Equal("application/json", refresh.Headers["Accept"]);

        // A provider restored from the store on the next launch holds the live token.
        using var relaunched = new BoxProvider(_accountId, _store, _http);
        Assert.Equal("refresh-2", relaunched.ApiClient!.Credentials.RefreshToken);
    }

    [Fact]
    public async Task ARefreshLandingAfterDisconnectDoesNotResurrectTheCredentials()
    {
        using var gate = new ManualResetEventSlim();
        _http.On(HttpMethod.Post, TokenUrl, (_, _) =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return FakeHttpHandler.Json("""{"access_token":"access-2","refresh_token":"refresh-2","expires_in":3600}""");
        });
        ServeTree();
        using var provider = Provider(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var listing = Task.Run(() => provider.ListDirectoryAsync("/"));
        await Task.Delay(100);
        await provider.DisconnectAsync();
        gate.Set();
        await listing;

        Assert.Null(_store.Load(Key));
        Assert.False(provider.IsAuthenticated);
    }

    [Fact]
    public async Task A401ForcesOneRefreshAndRetries()
    {
        var calls = 0;
        _http.On(HttpMethod.Get, "/folders/0/items", (_, _) => Interlocked.Increment(ref calls) == 1
            ? FakeHttpHandler.Json("{}", HttpStatusCode.Unauthorized)
            : FakeHttpHandler.Json("""{"total_count":0,"entries":[]}"""));
        _http.OnJson(HttpMethod.Post, TokenUrl, """{"access_token":"access-2","refresh_token":"refresh-2","expires_in":3600}""");
        using var provider = Provider();

        await provider.ListDirectoryAsync("/");

        Assert.Equal(
            ["Bearer access-1", "Bearer access-2"],
            _http.Requests.Where(r => r.Uri.AbsolutePath == "/2.0/folders/0/items").Select(r => r.Headers["Authorization"]));
        Assert.Equal("refresh-2", _store.Load<BoxCredentials>(Key)!.RefreshToken);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneRefresh()
    {
        var refreshes = 0;
        using var gate = new ManualResetEventSlim();
        _http.On(HttpMethod.Post, TokenUrl, (_, _) =>
        {
            Interlocked.Increment(ref refreshes);
            gate.Wait(TimeSpan.FromSeconds(10));
            return FakeHttpHandler.Json("""{"access_token":"access-2","refresh_token":"refresh-2","expires_in":3600}""");
        });
        ServeTree();
        using var provider = Provider(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var calls = Enumerable.Range(0, 8).Select(_ => Task.Run(() => provider.ListDirectoryAsync("/"))).ToList();
        await Task.Delay(200);
        gate.Set();
        await Task.WhenAll(calls);

        // A second POST with the single-use refresh-1 would have come back invalid_grant.
        Assert.Equal(1, refreshes);
        Assert.All(
            _http.Requests.Where(r => r.Uri.AbsolutePath.StartsWith("/2.0/", StringComparison.Ordinal)),
            r => Assert.Equal("Bearer access-2", r.Headers["Authorization"]));
    }

    [Fact]
    public async Task ARejectedRefreshIsNotAuthenticated()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, """{"error":"invalid_grant","error_description":"Refresh token has expired"}""", HttpStatusCode.BadRequest);
        using var provider = Provider(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
        Assert.Equal("refresh-1", _store.Load<BoxCredentials>(Key)!.RefreshToken);
    }

    // MARK: - Listing and path resolution

    [Fact]
    public async Task ListingResolvesNestedPathsAndCachesIds()
    {
        _tree["0"] = [Folder("100", "A")];
        _tree["100"] = [Folder("200", "B")];
        _tree["200"] =
        [
            new Node("300", "file", "doc.txt", 42, Modified: "2025-06-01T00:00:00-07:00", ContentModified: "2024-01-02T03:04:05-08:00", Sha1: "s1"),
            Folder("400", "Sub"),
            new Node("500", "file", ".env", 1, Modified: "2025-06-01T00:00:00.123-07:00"),
        ];
        ServeTree();
        using var provider = Provider();

        var items = await provider.ListDirectoryAsync("/A/B");

        Assert.Equal(["doc.txt", "Sub", ".env"], items.Select(i => i.Name));
        var doc = items[0];
        Assert.Equal("/A/B/doc.txt", doc.Path);
        Assert.Equal("/A/B/doc.txt", doc.Id);
        Assert.Equal(42, doc.Size);
        Assert.Equal("s1", doc.Checksum);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 11, 4, 5, TimeSpan.Zero), doc.ModificationDate);
        Assert.True(items[1].IsDirectory);
        Assert.True(items[2].IsHidden);
        Assert.Equal(new DateTimeOffset(2025, 6, 1, 7, 0, 0, 123, TimeSpan.Zero), items[2].ModificationDate);

        var gets = _http.Requests.ToList();
        Assert.Equal(["/2.0/folders/0/items", "/2.0/folders/100/items", "/2.0/folders/200/items"], gets.Select(r => r.Uri.AbsolutePath));
        Assert.Equal("id,type,name", Query(gets[0])["fields"]);
        var list = Query(gets[2]);
        Assert.Equal(BoxApiClient.ItemFields, list["fields"]);
        Assert.Equal("1000", list["limit"]);
        Assert.Equal("0", list["offset"]);
        Assert.Equal("name", list["sort"]);
        Assert.Equal("ASC", list["direction"]);
        Assert.Equal("application/json", gets[2].Headers["Accept"]);
        Assert.StartsWith("FileFluss/", gets[2].Headers["User-Agent"], StringComparison.Ordinal);

        // Second time round the cached ids skip the walk.
        await provider.ListDirectoryAsync("/A/B");
        Assert.Equal(4, _http.Requests.Count);
        Assert.Equal("300", provider.ApiClient!.PathIdCache["/A/B/doc.txt"]);
    }

    [Fact]
    public async Task ListingPagesThroughLargeFolders()
    {
        _tree["0"] = Enumerable.Range(0, 1500).Select(i => new Node($"f{i}", "file", $"file{i:D4}.txt", i)).ToList();
        ServeTree();
        using var provider = Provider();

        var items = await provider.ListDirectoryAsync("/");

        Assert.Equal(1500, items.Count);
        Assert.Equal(["0", "1000"], _http.Requests.Select(r => Query(r)["offset"]));
    }

    [Fact]
    public async Task AListingEvictsChildrenThatVanished()
    {
        _tree["0"] = [Folder("100", "A"), Folder("101", "Keep")];
        _tree["100"] = [Folder("200", "B")];
        ServeTree();
        using var provider = Provider();
        await provider.ListDirectoryAsync("/");
        await provider.ListDirectoryAsync("/A");
        var cache = provider.ApiClient!.PathIdCache;
        Assert.Equal("200", cache["/A/B"]);

        _tree["0"] = [Folder("101", "Keep")];
        await provider.ListDirectoryAsync("/");

        Assert.False(cache.ContainsKey("/A"));
        Assert.False(cache.ContainsKey("/A/B"));
        Assert.Equal("101", cache["/Keep"]);
        Assert.Equal("0", cache["/"]);
    }

    [Fact]
    public async Task AMissingPathIsNotFound()
    {
        _tree["0"] = [Folder("100", "A")];
        ServeTree();
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.GetFileMetadataAsync("/A/missing.txt"));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal("/A/missing.txt", error.ItemPath);
    }

    [Fact]
    public async Task GetFileMetadataFetchesTheItemWithItsFields()
    {
        _tree["0"] = [new Node("11", "file", "a.txt", 5)];
        _http.OnJson(HttpMethod.Get, "/2.0/files/11", """{"type":"file","id":"11","name":"a.txt","size":5,"modified_at":"2024-05-05T05:05:05Z"}""");
        ServeTree();
        using var provider = Provider();

        var item = await provider.GetFileMetadataAsync("/a.txt");

        Assert.Equal("/a.txt", item.Path);
        Assert.Equal(new DateTimeOffset(2024, 5, 5, 5, 5, 5, TimeSpan.Zero), item.ModificationDate);
        Assert.Equal(BoxApiClient.ItemFields, Query(_http.Single(HttpMethod.Get, "/2.0/files/11"))["fields"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, CloudErrorKind.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, CloudErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, CloudErrorKind.RateLimited)]
    [InlineData((HttpStatusCode)507, CloudErrorKind.QuotaExceeded)]
    [InlineData(HttpStatusCode.Conflict, CloudErrorKind.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, CloudErrorKind.ServerError)]
    public async Task StatusCodesMapToErrorKinds(HttpStatusCode status, CloudErrorKind expected)
    {
        _http.OnJson(HttpMethod.Get, "/folders/0/items", """{"type":"error","status":0,"code":"x","message":"Nope"}""", status);
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(expected, error.Kind);
        if (expected == CloudErrorKind.NotFound)
        {
            Assert.Equal("/", error.ItemPath);
        }
    }

    // MARK: - Mutations

    [Fact]
    public async Task DeleteUsesTheRightCollectionAndEvictsTheSubtree()
    {
        _tree["0"] = [Folder("100", "A"), new Node("11", "file", "a.txt", 1)];
        _tree["100"] = [Folder("200", "B")];
        _http.On(HttpMethod.Delete, "/2.0/", (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        ServeTree();
        using var provider = Provider();
        await provider.ListDirectoryAsync("/A");

        await provider.DeleteItemAsync("/A");
        await provider.DeleteItemAsync("/a.txt");

        var deletes = _http.Requests.Where(r => r.Method == HttpMethod.Delete).ToList();
        Assert.Equal($"{Api}/folders/100?recursive=true", deletes[0].Uri.AbsoluteUri);
        Assert.Equal($"{Api}/files/11", deletes[1].Uri.AbsoluteUri);
        var cache = provider.ApiClient!.PathIdCache;
        Assert.False(cache.ContainsKey("/A"));
        Assert.False(cache.ContainsKey("/A/B"));
        Assert.False(cache.ContainsKey("/a.txt"));
    }

    [Fact]
    public async Task CreateFolderPostsUnderTheParentOrSkipsAnExistingOne()
    {
        _tree["0"] = [Folder("100", "Existing")];
        _http.OnJson(HttpMethod.Post, "/2.0/folders", """{"type":"folder","id":"900","name":"New"}""", HttpStatusCode.Created);
        ServeTree();
        using var provider = Provider();

        await provider.CreateDirectoryAsync("/Existing");
        await provider.CreateDirectoryAsync("/New");

        var post = _http.Single(HttpMethod.Post, "/2.0/folders");
        var body = Json(post.Body);
        Assert.Equal("New", body.GetProperty("name").GetString());
        Assert.Equal("0", body.GetProperty("parent").GetProperty("id").GetString());
        Assert.Equal("application/json", post.Headers["Content-Type"]);
        Assert.Equal("900", provider.ApiClient!.PathIdCache["/New"]);
    }

    [Fact]
    public async Task RenameMoveAndCopyAreServerSide()
    {
        _tree["0"] = [Folder("100", "A"), Folder("101", "B")];
        _tree["100"] = [new Node("300", "file", "doc.txt", 1), Folder("400", "Sub")];
        _tree["400"] = [new Node("401", "file", "deep.txt", 1)];
        _http.OnJson(HttpMethod.Put, "/2.0/files/300", """{"type":"file","id":"300","name":"x"}""");
        _http.OnJson(HttpMethod.Put, "/2.0/folders/400", """{"type":"folder","id":"400","name":"x"}""");
        _http.OnJson(HttpMethod.Post, "/2.0/folders/101/copy", """{"type":"folder","id":"777","name":"B"}""", HttpStatusCode.Created);
        ServeTree();
        using var provider = Provider();
        await provider.ListDirectoryAsync("/A/Sub");

        await provider.RenameItemAsync("/A/Sub", "Renamed");
        await provider.MoveItemAsync("/A/doc.txt", "/B/moved.txt");
        await provider.CopyItemAsync("/B", "/A/B copy");

        var rename = Json(_http.Single(HttpMethod.Put, "/2.0/folders/400").Body);
        Assert.Equal("Renamed", rename.GetProperty("name").GetString());
        var move = Json(_http.Single(HttpMethod.Put, "/2.0/files/300").Body);
        Assert.Equal("101", move.GetProperty("parent").GetProperty("id").GetString());
        Assert.Equal("moved.txt", move.GetProperty("name").GetString());
        var copy = Json(_http.Single(HttpMethod.Post, "/2.0/folders/101/copy").Body);
        Assert.Equal("100", copy.GetProperty("parent").GetProperty("id").GetString());
        Assert.Equal("B copy", copy.GetProperty("name").GetString());

        var cache = provider.ApiClient!.PathIdCache;
        Assert.Equal("400", cache["/A/Renamed"]);
        Assert.False(cache.ContainsKey("/A/Sub"));
        Assert.False(cache.ContainsKey("/A/Sub/deep.txt"));
        Assert.Equal("300", cache["/B/moved.txt"]);
        Assert.False(cache.ContainsKey("/A/doc.txt"));
        Assert.Equal("777", cache["/A/B copy"]);
    }

    [Fact]
    public async Task SearchScopesToTheFolderAndPlacesHitsByPathCollection()
    {
        _tree["0"] = [Folder("100", "Work")];
        _http.OnJson(HttpMethod.Get, "/2.0/search", """
            {"total_count":2,"entries":[
              {"type":"file","id":"5","name":"report.pdf","size":9,
               "path_collection":{"total_count":3,"entries":[{"type":"folder","id":"0","name":"All Files"},{"type":"folder","id":"100","name":"Work"},{"type":"folder","id":"101","name":"2026"}]}},
              {"type":"file","id":"6","name":"shared.pdf","size":1,
               "path_collection":{"total_count":1,"entries":[{"type":"folder","id":"999","name":"Someone else's"}]}}
            ]}
            """);
        ServeTree();
        using var provider = Provider();

        var hits = (await provider.SearchFilesAsync("report", "/Work"))!;

        Assert.Equal(["/Work/2026/report.pdf", "/shared.pdf"], hits.Select(h => h.Path));
        var query = Query(_http.Single(HttpMethod.Get, "/2.0/search"));
        Assert.Equal("report", query["query"]);
        Assert.Equal("200", query["limit"]);
        Assert.Equal("100", query["ancestor_folder_ids"]);
        Assert.Equal(BoxApiClient.ItemFields + ",path_collection", query["fields"]);
    }

    // MARK: - Transfers

    [Fact]
    public async Task DownloadFetchesTheContentById()
    {
        var payload = Encoding.UTF8.GetBytes("box bytes");
        _tree["0"] = [new Node("11", "file", "a.txt", payload.Length)];
        _http.OnBytes(HttpMethod.Get, "/2.0/files/11/content", payload);
        ServeTree();
        using var provider = Provider();
        var target = Path.Combine(_temp, "a.txt");
        long reported = 0;

        await provider.DownloadFileAsync("/a.txt", target, delta => Interlocked.Add(ref reported, delta));

        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        Assert.Equal(payload.Length, reported);
        Assert.Equal("Bearer access-1", _http.Single(HttpMethod.Get, "/files/11/content").Headers["Authorization"]);
    }

    [Fact]
    public async Task SmallUploadIsMultipartWithAttributesFirst()
    {
        _tree["0"] = [Folder("100", "Docs")];
        _http.OnJson(HttpMethod.Post, "upload.box.com/api/2.0/files/content", """{"total_count":1,"entries":[{"type":"file","id":"500","name":"hello.txt"}]}""", HttpStatusCode.Created);
        ServeTree();
        var local = WriteFile("hello.txt", "hello world");
        File.SetLastWriteTimeUtc(local, new DateTime(2024, 3, 1, 10, 20, 30, DateTimeKind.Utc));
        using var provider = Provider();
        long reported = 0;

        await provider.UploadFileAsync(local, "/Docs/hello.txt", delta => Interlocked.Add(ref reported, delta));

        var request = _http.Single(HttpMethod.Post, "upload.box.com/api/2.0/files/content");
        Assert.StartsWith("multipart/form-data; boundary=", request.Headers["Content-Type"], StringComparison.Ordinal);
        var (attributes, file) = SplitMultipart(request.Body!);
        Assert.Equal("hello.txt", attributes.GetProperty("name").GetString());
        Assert.Equal("100", attributes.GetProperty("parent").GetProperty("id").GetString());
        Assert.Equal("2024-03-01T10:20:30Z", attributes.GetProperty("content_modified_at").GetString());
        Assert.True(attributes.TryGetProperty("content_created_at", out _));
        Assert.Equal("hello world", file);
        Assert.Equal(11, reported);
        Assert.Equal("500", provider.ApiClient!.PathIdCache["/Docs/hello.txt"]);
    }

    [Fact]
    public async Task AnUploadThatConflictsBecomesANewVersion()
    {
        _tree["0"] = [new Node("77", "file", "hello.txt", 3)];
        _http.OnJson(HttpMethod.Post, "upload.box.com/api/2.0/files/content", """{"type":"error","status":409,"code":"item_name_in_use","message":"Item with the same name already exists"}""", HttpStatusCode.Conflict);
        _http.OnJson(HttpMethod.Post, "upload.box.com/api/2.0/files/77/content", """{"total_count":1,"entries":[{"type":"file","id":"77","name":"hello.txt"}]}""", HttpStatusCode.Created);
        ServeTree();
        var local = WriteFile("hello.txt", "hello world");
        using var provider = Provider();
        long reported = 0;

        await provider.UploadFileAsync(local, "/hello.txt", delta => Interlocked.Add(ref reported, delta));

        var version = _http.Single(HttpMethod.Post, "/files/77/content");
        var (attributes, file) = SplitMultipart(version.Body!);
        Assert.False(attributes.TryGetProperty("parent", out _));
        Assert.Equal("hello world", file);
        Assert.Equal(11, reported);
        Assert.Equal("77", provider.ApiClient!.PathIdCache["/hello.txt"]);
    }

    [Fact]
    public async Task LargeUploadStreamsPartsWithDigestsAndPollsTheCommit()
    {
        var local = WriteFile("big.bin", "0123456789");
        var bytes = Encoding.ASCII.GetBytes("0123456789");
        var commits = 0;
        ServeUploadParts();
        _http.On(HttpMethod.Post, "/upload_sessions/S1/commit", (_, _) =>
        {
            if (Interlocked.Increment(ref commits) == 1)
            {
                var pending = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(string.Empty) };
                pending.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
                return pending;
            }

            return FakeHttpHandler.Json("""{"total_count":1,"entries":[{"type":"file","id":"900","name":"big.bin"}]}""", HttpStatusCode.Created);
        });
        _http.OnJson(HttpMethod.Post, "upload.box.com/api/2.0/files/upload_sessions", SessionJson(4), HttpStatusCode.Created);
        using var provider = Provider();
        var client = provider.ApiClient!;
        client.SinglePutMaxBytes = 4;
        var delays = RecordDelays(client);
        long reported = 0;

        await provider.UploadFileAsync(local, "/big.bin", delta => Interlocked.Add(ref reported, delta));

        var open = Json(_http.Requests.Single(r => r.Uri.AbsolutePath == "/api/2.0/files/upload_sessions").Body);
        Assert.Equal(10, open.GetProperty("file_size").GetInt64());
        Assert.Equal("big.bin", open.GetProperty("file_name").GetString());
        Assert.Equal("0", open.GetProperty("folder_id").GetString());

        var parts = _http.Requests.Where(r => r.Method == HttpMethod.Put).ToList();
        Assert.Equal(["0123", "4567", "89"], parts.Select(p => p.Body));
        Assert.Equal(["bytes 0-3/10", "bytes 4-7/10", "bytes 8-9/10"], parts.Select(p => p.Headers["Content-Range"]));
        Assert.Equal("sha=" + Convert.ToBase64String(SHA1.HashData(bytes.AsSpan(0, 4))), parts[0].Headers["Digest"]);
        Assert.Equal("sha=" + Convert.ToBase64String(SHA1.HashData(bytes.AsSpan(8, 2))), parts[2].Headers["Digest"]);

        var commitRequests = _http.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/commit", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, commitRequests.Count);
        Assert.Equal("sha=" + Convert.ToBase64String(SHA1.HashData(bytes)), commitRequests[0].Headers["Digest"]);
        var commit = Json(commitRequests[0].Body);
        Assert.Equal(["P0", "P4", "P8"], commit.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("part_id").GetString()));
        Assert.Equal(8, commit.GetProperty("parts")[2].GetProperty("offset").GetInt64());
        Assert.Equal(2, commit.GetProperty("parts")[2].GetProperty("size").GetInt64());
        Assert.True(commit.GetProperty("attributes").TryGetProperty("content_modified_at", out _));

        Assert.Equal([TimeSpan.FromSeconds(3)], delays);
        Assert.Equal(10, reported);
        Assert.Equal("900", client.PathIdCache["/big.bin"]);
    }

    [Fact]
    public async Task ATransientPartFailureIsRetriedWithoutCountingBytesTwice()
    {
        var local = WriteFile("big.bin", "0123456789");
        var failures = 0;
        _http.On(HttpMethod.Put, "/upload_sessions/S1", (request, _) =>
        {
            var range = request.Content!.Headers.ContentRange!;
            if (range.From == 4 && Interlocked.Increment(ref failures) == 1)
            {
                return FakeHttpHandler.Json("""{"code":"internal_server_error"}""", HttpStatusCode.ServiceUnavailable);
            }

            return PartResponse(range);
        });
        _http.OnJson(HttpMethod.Post, "/upload_sessions/S1/commit", """{"total_count":1,"entries":[{"type":"file","id":"900","name":"big.bin"}]}""", HttpStatusCode.Created);
        _http.OnJson(HttpMethod.Post, "upload.box.com/api/2.0/files/upload_sessions", SessionJson(4), HttpStatusCode.Created);
        using var provider = Provider();
        provider.ApiClient!.SinglePutMaxBytes = 4;
        var delays = RecordDelays(provider.ApiClient);
        long reported = 0;

        await provider.UploadFileAsync(local, "/big.bin", delta => Interlocked.Add(ref reported, delta));

        Assert.Equal(4, _http.Requests.Count(r => r.Method == HttpMethod.Put));
        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
        Assert.Equal(10, reported);
    }

    [Fact]
    public async Task AFailedChunkedUploadAbortsTheSession()
    {
        var local = WriteFile("big.bin", "0123456789");
        _http.OnJson(HttpMethod.Put, "/upload_sessions/S1", """{"code":"bad_digest","message":"Digest mismatch"}""", HttpStatusCode.PreconditionFailed);
        _http.On(HttpMethod.Delete, "/upload_sessions/S1", (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        _http.OnJson(HttpMethod.Post, "upload.box.com/api/2.0/files/upload_sessions", SessionJson(4), HttpStatusCode.Created);
        using var provider = Provider();
        provider.ApiClient!.SinglePutMaxBytes = 4;

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(local, "/big.bin"));

        Assert.Equal(CloudErrorKind.ServerError, error.Kind);
        Assert.Equal(412, error.StatusCode);
        var abort = _http.Single(HttpMethod.Delete, "/upload_sessions/S1");
        Assert.Equal("Bearer access-1", abort.Headers["Authorization"]);
        Assert.DoesNotContain(_http.Requests, r => r.Uri.AbsolutePath.EndsWith("/commit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "{}")]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"file_size_limit_exceeded","message":"File size exceeds the folder owner's file size limit"}""")]
    public async Task APlanLimitOnTheSessionIsFileTooLarge(HttpStatusCode status, string body)
    {
        var local = WriteFile("big.bin", "0123456789");
        _http.OnJson(HttpMethod.Post, "upload.box.com/api/2.0/files/upload_sessions", body, status);
        using var provider = Provider();
        provider.ApiClient!.SinglePutMaxBytes = 4;

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(local, "/big.bin"));

        Assert.Equal(CloudErrorKind.FileTooLarge, error.Kind);
        Assert.Equal(10, error.FileBytes);
        Assert.Null(error.ProviderLimitBytes);
    }

    // MARK: - Quota

    [Theory]
    [InlineData("""{"space_amount":10737418240,"space_used":1024}""", 1024L, 10737418240L)]
    [InlineData("""{"space_amount":-1,"space_used":5}""", 5L, null)]
    [InlineData("""{"space_amount":1.0E+15,"space_used":7}""", 7L, 1_000_000_000_000_000L)]
    public async Task QuotaParsesSpaceAmountAndUsed(string json, long used, long? total)
    {
        _http.OnJson(HttpMethod.Get, "/2.0/users/me", json);
        using var provider = Provider();

        var quota = await provider.StorageQuotaAsync();

        Assert.Equal(used, quota!.UsedBytes);
        Assert.Equal(total, quota.TotalBytes);
        Assert.Equal("space_amount,space_used", Query(_http.Single(HttpMethod.Get, "/users/me"))["fields"]);
    }

    [Fact]
    public async Task QuotaFailureIsUnknown()
    {
        _http.OnJson(HttpMethod.Get, "/2.0/users/me", "{}", HttpStatusCode.Forbidden);
        using var provider = Provider();

        Assert.Null(await provider.StorageQuotaAsync());
    }

    // MARK: - Share links

    [Fact]
    public async Task CreateShareLinkPutsTheSettingsAndReportsAdminDowngrades()
    {
        _tree["0"] = [new Node("11", "file", "a.txt", 1)];
        _http.OnJson(HttpMethod.Put, "/2.0/files/11", """
            {"type":"file","id":"11","shared_link":{"url":"https://app.box.com/s/abc","download_url":"https://app.box.com/shared/static/abc.txt",
             "is_password_enabled":true,"unshared_at":"2026-12-31T23:00:00.000Z","effective_access":"company","effective_permission":"can_preview"}}
            """);
        ServeTree();
        using var provider = Provider();

        var link = await provider.CreateShareLinkAsync("/a.txt", new ShareLinkOptions
        {
            Password = "Secret123",
            Expiry = new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero),
        });

        var request = _http.Single(HttpMethod.Put, "/2.0/files/11");
        Assert.Equal("shared_link", Query(request)["fields"]);
        var shared = Json(request.Body).GetProperty("shared_link");
        Assert.Equal("open", shared.GetProperty("access").GetString());
        Assert.Equal("Secret123", shared.GetProperty("password").GetString());
        Assert.Equal("2026-12-31T23:00:00Z", shared.GetProperty("unshared_at").GetString());
        Assert.True(shared.GetProperty("permissions").GetProperty("can_download").GetBoolean());
        Assert.True(shared.GetProperty("permissions").GetProperty("can_preview").GetBoolean());

        Assert.Equal("https://app.box.com/s/abc", link.Url.AbsoluteUri);
        Assert.Equal("https://app.box.com/shared/static/abc.txt", link.DirectDownloadUrl!.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero), link.ExpiresAt);
        Assert.True(link.HasPassword);
        Assert.False(link.IsPublic);
        Assert.Equal(
            "Your Box administrator restricted this link to people in your company — it is not publicly accessible. " +
            "Your Box administrator disabled downloads for shared links — recipients can only preview the file.",
            link.Note);
    }

    [Fact]
    public async Task AFolderLinkWithoutOptionsSendsNoPasswordOrExpiry()
    {
        _tree["0"] = [Folder("100", "Shared")];
        _http.OnJson(HttpMethod.Put, "/2.0/folders/100", """{"type":"folder","id":"100","shared_link":{"url":"https://app.box.com/s/f","effective_access":"open","effective_permission":"can_download"}}""");
        ServeTree();
        using var provider = Provider();

        var link = await provider.UpdateShareLinkAsync("/Shared", new ShareLinkOptions { AllowDownload = false });

        var shared = Json(_http.Single(HttpMethod.Put, "/2.0/folders/100").Body).GetProperty("shared_link");
        Assert.False(shared.TryGetProperty("password", out _));
        Assert.False(shared.TryGetProperty("unshared_at", out _));
        Assert.False(shared.GetProperty("permissions").GetProperty("can_download").GetBoolean());
        Assert.True(link.IsPublic);
        Assert.Null(link.Note);
        Assert.Null(link.DirectDownloadUrl);
    }

    [Fact]
    public async Task ARejectedShareSettingSurfacesBoxsExplanation()
    {
        _tree["0"] = [new Node("11", "file", "a.txt", 1)];
        _http.OnJson(HttpMethod.Put, "/2.0/files/11", """
            {"type":"error","status":400,"code":"bad_request","message":"Bad Request",
             "context_info":{"errors":[{"reason":"invalid_parameter","name":"password","message":"Password must be at least 8 characters"}]}}
            """, HttpStatusCode.BadRequest);
        ServeTree();
        using var provider = Provider();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/a.txt", new ShareLinkOptions { Password = "short" }));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("Bad Request — password: Password must be at least 8 characters", error.Message);
    }

    [Fact]
    public async Task ExistingShareLinkReadsTheFieldBack()
    {
        _tree["0"] = [new Node("11", "file", "a.txt", 1), new Node("12", "file", "b.txt", 1)];
        _http.OnJson(HttpMethod.Get, "/2.0/files/11", """{"type":"file","id":"11","shared_link":null}""");
        _http.OnJson(HttpMethod.Get, "/2.0/files/12", """{"type":"file","id":"12","shared_link":{"url":"https://app.box.com/s/b","effective_access":"open"}}""");
        ServeTree();
        using var provider = Provider();

        Assert.Null(await provider.ExistingShareLinkAsync("/a.txt"));
        var link = await provider.ExistingShareLinkAsync("/b.txt");

        Assert.Equal("https://app.box.com/s/b", link!.Url.AbsoluteUri);
        Assert.Null(link.Note);
        Assert.Equal("shared_link", Query(_http.Single(HttpMethod.Get, "/2.0/files/12"))["fields"]);
    }

    [Fact]
    public async Task RemoveShareLinkSendsAnExplicitNullAndChecksTheResult()
    {
        _tree["0"] = [new Node("11", "file", "a.txt", 1), new Node("12", "file", "kept.txt", 1)];
        _http.OnJson(HttpMethod.Put, "/2.0/files/11", """{"type":"file","id":"11","shared_link":null}""");
        _http.OnJson(HttpMethod.Put, "/2.0/files/12", """{"type":"file","id":"12","shared_link":{"url":"https://app.box.com/s/k"}}""");
        ServeTree();
        using var provider = Provider();

        await provider.RemoveShareLinkAsync("/a.txt");
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.RemoveShareLinkAsync("/kept.txt"));

        Assert.Equal("""{"shared_link":null}""", _http.Single(HttpMethod.Put, "/2.0/files/11").Body);
        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
    }

    // MARK: - Connecting

    [Fact]
    public async Task ConnectRunsOAuthAndStoresTheCredentials()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"token_type":"bearer"}""");
        _http.OnJson(HttpMethod.Get, "/2.0/users/me", """{"type":"user","id":"42","name":"Someone","login":"someone@example.com"}""");
        using var provider = new BoxProvider(_accountId, _store, _http);
        Uri? authorizeUri = null;
        provider.Authenticator = (build, _) =>
        {
            authorizeUri = build("http://localhost:53682");
            var state = HttpUtility.ParseQueryString(authorizeUri.Query)["state"];
            return Task.FromResult(new OAuthCallback("http://localhost:53682", new Uri($"http://localhost:53682/?code=the-code&state={Uri.EscapeDataString(state!)}")));
        };
        var prompt = new ScriptedConnectPrompt();

        var result = await provider.ConnectAsync(ConnectValues.Empty("browser"), prompt);

        Assert.Equal("Someone", result.AccountLabel);
        Assert.True(provider.IsAuthenticated);
        Assert.Equal("https://account.box.com/api/oauth2/authorize", authorizeUri!.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(authorizeUri.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(BoxApiClient.ClientId, query["client_id"]);
        Assert.Equal("http://localhost:53682", query["redirect_uri"]);
        Assert.False(string.IsNullOrEmpty(query["state"]));

        var token = _http.Single(HttpMethod.Post, TokenUrl);
        var form = HttpUtility.ParseQueryString(token.Body!);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("the-code", form["code"]);
        Assert.Equal(BoxApiClient.ClientId, form["client_id"]);
        Assert.Equal(BoxApiClient.ClientSecret, form["client_secret"]);
        Assert.Equal("http://localhost:53682", form["redirect_uri"]);
        Assert.Equal("application/json", token.Headers["Accept"]);
        Assert.Equal("Bearer new-access", _http.Single(HttpMethod.Get, "/users/me").Headers["Authorization"]);

        var stored = _store.Load<BoxCredentials>(Key)!;
        Assert.Equal("new-refresh", stored.RefreshToken);
        Assert.Equal("someone@example.com", stored.UserLogin);
        Assert.Equal("Someone", stored.DisplayName);
        Assert.Null(prompt.StatusLines.Last());
    }

    [Fact]
    public async Task ConnectNamesTheAccountBoxUserWhenTheLookupFails()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}""");
        _http.OnJson(HttpMethod.Get, "/2.0/users/me", "{}", HttpStatusCode.InternalServerError);
        using var provider = new BoxProvider(_accountId, _store, _http) { Authenticator = Callback("the-code") };

        var result = await provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt());

        Assert.Equal("Box user", result.AccountLabel);
    }

    [Fact]
    public async Task AFailedConnectSurfacesBoxsReasonAndKeepsStoredCredentials()
    {
        _http.OnJson(HttpMethod.Post, TokenUrl, """{"error":"invalid_grant","error_description":"The redirect_uri does not match"}""", HttpStatusCode.BadRequest);
        using var provider = Provider();
        provider.Authenticator = Callback("bad-code");
        var before = _store.Load(Key);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Contains("The redirect_uri does not match", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, _store.Load(Key));
        Assert.Equal("access-1", provider.ApiClient!.Credentials.AccessToken);
    }

    [Fact]
    public async Task AnOAuthErrorCallbackIsUnauthorized()
    {
        using var provider = new BoxProvider(_accountId, _store, _http);
        provider.Authenticator = (build, _) =>
        {
            var state = HttpUtility.ParseQueryString(build("http://localhost:1").Query)["state"];
            return Task.FromResult(new OAuthCallback("http://localhost:1", new Uri($"http://localhost:1/?error=access_denied&state={Uri.EscapeDataString(state!)}")));
        };

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty("browser"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
        Assert.Empty(_http.Requests);
        Assert.Null(_store.Load(Key));
    }

    // MARK: - Helpers

    private sealed record Node(string Id, string Type, string Name, long Size = 0, string? Modified = null, string? ContentModified = null, string? Sha1 = null);

    private static Node Folder(string id, string name) => new(id, "folder", name);

    private BoxProvider Provider(DateTimeOffset? expiresAt = null)
    {
        _store.Save(Key, new BoxCredentials("access-1", "refresh-1", expiresAt ?? DateTimeOffset.UtcNow.AddHours(1), "someone@example.com", "Someone"));
        return new BoxProvider(_accountId, _store, _http);
    }

    /// <summary>Serves GET /folders/{id}/items from <see cref="_tree"/>, honouring offset and limit. Register specific routes first.</summary>
    private void ServeTree()
    {
        _http.On(HttpMethod.Get, "api.box.com/2.0/folders/", (request, _) =>
        {
            var segments = request.RequestUri!.AbsolutePath.Split('/');
            if (segments.Length != 5 || segments[4] != "items" || !_tree.TryGetValue(segments[3], out var children))
            {
                return FakeHttpHandler.Json("""{"type":"error","status":404,"code":"not_found","message":"Not Found"}""", HttpStatusCode.NotFound);
            }

            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            var offset = int.Parse(query["offset"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            var limit = int.Parse(query["limit"] ?? "100", System.Globalization.CultureInfo.InvariantCulture);
            var entries = new JsonArray(children.Skip(offset).Take(limit).Select(NodeJson).ToArray<JsonNode?>());
            return FakeHttpHandler.Json(new JsonObject
            {
                ["total_count"] = children.Count,
                ["entries"] = entries,
                ["offset"] = offset,
                ["limit"] = limit,
            }.ToJsonString());
        });
    }

    private static JsonObject NodeJson(Node node)
    {
        var json = new JsonObject { ["type"] = node.Type, ["id"] = node.Id, ["name"] = node.Name, ["size"] = node.Size };
        if (node.Modified is not null)
        {
            json["modified_at"] = node.Modified;
        }

        if (node.ContentModified is not null)
        {
            json["content_modified_at"] = node.ContentModified;
        }

        if (node.Sha1 is not null)
        {
            json["sha1"] = node.Sha1;
        }

        return json;
    }

    private static string SessionJson(int partSize) => $$"""
        {"id":"S1","part_size":{{partSize}},"total_parts":3,"session_endpoints":{
          "upload_part":"{{Upload}}/files/upload_sessions/S1",
          "commit":"{{Upload}}/files/upload_sessions/S1/commit",
          "abort":"{{Upload}}/files/upload_sessions/S1"}
        }
        """;

    /// <summary>Answers part PUTs from their Content-Range, as Box does.</summary>
    private void ServeUploadParts()
    {
        _http.On(HttpMethod.Put, "/upload_sessions/S1", (request, _) => PartResponse(request.Content!.Headers.ContentRange!));
    }

    private static HttpResponseMessage PartResponse(System.Net.Http.Headers.ContentRangeHeaderValue range)
        => FakeHttpHandler.Json($"{{\"part\":{{\"part_id\":\"P{range.From}\",\"offset\":{range.From},\"size\":{range.To - range.From + 1},\"sha1\":\"x\"}}}}");

    private static List<TimeSpan> RecordDelays(BoxApiClient client)
    {
        var delays = new List<TimeSpan>();
        client.Delay = (delay, _) =>
        {
            lock (delays)
            {
                delays.Add(delay);
            }

            return Task.CompletedTask;
        };
        return delays;
    }

    private static Func<Func<string, Uri>, CancellationToken, Task<OAuthCallback>> Callback(string code)
        => (build, _) =>
        {
            var uri = build("http://localhost:4000");
            var state = HttpUtility.ParseQueryString(uri.Query)["state"];
            return Task.FromResult(new OAuthCallback("http://localhost:4000", new Uri($"http://localhost:4000/?code={code}&state={Uri.EscapeDataString(state!)}")));
        };

    /// <summary>The attributes JSON and the file part of a Box multipart body; asserts attributes come first.</summary>
    private static (JsonElement Attributes, string File) SplitMultipart(string body)
    {
        var attributesAt = body.IndexOf("name=\"attributes\"", StringComparison.Ordinal);
        var fileAt = body.IndexOf("name=\"file\"", StringComparison.Ordinal);
        Assert.True(attributesAt >= 0 && fileAt > attributesAt, "attributes must precede the file part");

        var jsonStart = body.IndexOf("\r\n\r\n", attributesAt, StringComparison.Ordinal) + 4;
        var jsonEnd = body.IndexOf("\r\n--", jsonStart, StringComparison.Ordinal);
        var fileStart = body.IndexOf("\r\n\r\n", fileAt, StringComparison.Ordinal) + 4;
        var fileEnd = body.IndexOf("\r\n--", fileStart, StringComparison.Ordinal);
        return (Json(body[jsonStart..jsonEnd]), body[fileStart..fileEnd]);
    }

    private static System.Collections.Specialized.NameValueCollection Query(FakeHttpHandler.RecordedRequest request)
        => HttpUtility.ParseQueryString(request.Uri.Query);

    private string WriteFile(string name, string contents)
    {
        var path = Path.Combine(_temp, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static JsonElement Json(string? body)
    {
        using var document = JsonDocument.Parse(body ?? "null");
        return document.RootElement.Clone();
    }
}
