// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.S3;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

public sealed class S3ProviderTests : IDisposable
{
    private static readonly Guid AccountId = Guid.Parse("6f9b0c51-6a2f-4a8e-9a43-0d6c1e2f3a4b");
    private static readonly DateTimeOffset Now = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);
    private const string Bucket = "photos.s3.eu-central-1.amazonaws.com";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ff-s3-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttpHandler _http = new();
    private readonly MemoryCredentialStore _store = new();

    public S3ProviderTests() => Directory.CreateDirectory(_temp);

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static HttpResponseMessage Xml(string xml, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    private static HttpResponseMessage Empty(HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new ByteArrayContent([]) };

    private static string ListResult(string inner) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><Name>photos</Name>{inner}</ListBucketResult>
        """;

    private static string ErrorBody(string code, string message) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{message}</Message><RequestId>1</RequestId></Error>";

    private S3Provider Connected(string region = "eu-central-1", string? rootPath = null)
    {
        _store.Save($"s3.{AccountId}", new S3Credentials("AKIAEXAMPLE6UDK", "secret", region, "AWS S3 (***6UDK)", RootPath: rootPath));
        return new S3Provider(AccountId, _store, _http) { Clock = () => Now };
    }

    private string TempFile(string name, byte[] content)
    {
        var path = Path.Combine(_temp, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    // MARK: - Listing

    [Fact]
    public async Task RootListsBucketsAsFolders()
    {
        _http.On(HttpMethod.Get, "https://s3.eu-central-1.amazonaws.com/", (_, _) => Xml("""
            <?xml version="1.0" encoding="UTF-8"?>
            <ListAllMyBucketsResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <Owner><ID>abc</ID><DisplayName>me</DisplayName></Owner>
              <Buckets>
                <Bucket><Name>photos</Name><CreationDate>2024-01-02T03:04:05.000Z</CreationDate></Bucket>
                <Bucket><Name>backups</Name><CreationDate>2023-06-07T08:09:10Z</CreationDate></Bucket>
              </Buckets>
            </ListAllMyBucketsResult>
            """));
        using var provider = Connected();

        var items = await provider.ListDirectoryAsync("/");

        Assert.Equal(["photos", "backups"], items.Select(i => i.Name));
        Assert.All(items, i => Assert.True(i.IsDirectory));
        Assert.Equal("/photos", items[0].Path);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), items[0].ModificationDate);
        Assert.Equal(new DateTimeOffset(2023, 6, 7, 8, 9, 10, TimeSpan.Zero), items[1].ModificationDate);
    }

    [Fact]
    public async Task FolderListingPagesThroughPrefixesAndObjects()
    {
        _http.On(HttpMethod.Get, Bucket, (request, _) => request.RequestUri!.Query.Contains("continuation-token=token-2", StringComparison.Ordinal)
            ? Xml(ListResult("""
                <IsTruncated>false</IsTruncated>
                <Contents><Key>2024/.hidden</Key><LastModified>2024-07-02T00:00:00.000Z</LastModified><ETag>"e2"</ETag><Size>5</Size></Contents>
                <CommonPrefixes><Prefix>2024/winter/</Prefix></CommonPrefixes>
                """))
            : Xml(ListResult("""
                <IsTruncated>true</IsTruncated>
                <Contents><Key>2024/</Key><LastModified>2024-01-01T00:00:00.000Z</LastModified><ETag>"d41d8cd98f00b204e9800998ecf8427e"</ETag><Size>0</Size></Contents>
                <Contents><Key>2024/beach photo.jpg</Key><LastModified>2024-07-01T10:20:30.000Z</LastModified><ETag>"abc"</ETag><Size>1234567</Size><StorageClass>STANDARD</StorageClass></Contents>
                <CommonPrefixes><Prefix>2024/summer/</Prefix></CommonPrefixes>
                <NextContinuationToken>token-2</NextContinuationToken>
                """)));
        using var provider = Connected();

        var items = await provider.ListDirectoryAsync("/photos/2024");

        // Folders of every page first, then files; the folder's own marker object is skipped.
        Assert.Equal(["summer", "winter", "beach photo.jpg", ".hidden"], items.Select(i => i.Name));
        var summer = items[0];
        Assert.True(summer.IsDirectory);
        Assert.Equal("/photos/2024/summer", summer.Path);
        var photo = items[2];
        Assert.False(photo.IsDirectory);
        Assert.Equal("/photos/2024/beach photo.jpg", photo.Path);
        Assert.Equal(1234567, photo.Size);
        Assert.Equal(new DateTimeOffset(2024, 7, 1, 10, 20, 30, TimeSpan.Zero), photo.ModificationDate);
        Assert.Equal("\"abc\"", photo.Checksum);
        Assert.True(items[3].IsHidden);

        var first = _http.Requests.First();
        Assert.Equal("?delimiter=%2F&list-type=2&max-keys=1000&prefix=2024%2F", first.Uri.Query);
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task RequestsAreSignedOverWhatIsSent()
    {
        _http.On(HttpMethod.Get, Bucket, (_, _) => Xml(ListResult("<IsTruncated>false</IsTruncated>")));
        using var provider = Connected();

        await provider.ListDirectoryAsync("/photos/a b+c");

        var sent = Assert.Single(_http.Requests);
        Assert.Equal("photos.s3.eu-central-1.amazonaws.com", sent.Headers["Host"]);
        Assert.Equal("20260105T100000Z", sent.Headers["x-amz-date"]);
        Assert.Equal(SigV4.EmptyPayloadHash, sent.Headers["x-amz-content-sha256"]);
        Assert.Equal("?delimiter=%2F&list-type=2&max-keys=1000&prefix=a%20b%2Bc%2F", sent.Uri.Query);

        // Recompute the signature from the request as it went out.
        var expected = SigV4.Authorization(
            "GET",
            sent.Uri.AbsolutePath,
            sent.Uri.Query[1..],
            [new("host", sent.Headers["Host"]), new("x-amz-content-sha256", SigV4.EmptyPayloadHash), new("x-amz-date", "20260105T100000Z")],
            SigV4.EmptyPayloadHash,
            "AKIAEXAMPLE6UDK",
            "secret",
            "eu-central-1",
            "s3",
            Now);
        Assert.Equal(expected, sent.Headers["Authorization"]);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIAEXAMPLE6UDK/20260105/eu-central-1/s3/aws4_request, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature=", expected);
    }

    [Fact]
    public async Task WrongRegionRedirectIsFollowedOnceAndRemembered()
    {
        _http.On(HttpMethod.Get, "photos.s3.us-east-1.amazonaws.com", (_, _) =>
        {
            var redirect = Xml(ErrorBody("PermanentRedirect", "The bucket you are attempting to access must be addressed using the specified endpoint."), HttpStatusCode.MovedPermanently);
            redirect.Headers.TryAddWithoutValidation("x-amz-bucket-region", "eu-central-1");
            return redirect;
        });
        _http.On(HttpMethod.Get, Bucket, (_, _) => Xml(ListResult("<IsTruncated>false</IsTruncated><Contents><Key>a.txt</Key><Size>1</Size></Contents>")));
        using var provider = Connected(region: "us-east-1");

        var items = await provider.ListDirectoryAsync("/photos");
        await provider.ListDirectoryAsync("/photos");

        Assert.Equal("a.txt", Assert.Single(items).Name);
        Assert.Single(_http.Requests, r => r.Uri.Host == "photos.s3.us-east-1.amazonaws.com");
        var retried = _http.Requests.Where(r => r.Uri.Host == Bucket).ToList();
        Assert.Equal(2, retried.Count);
        Assert.All(retried, r => Assert.Contains("/eu-central-1/s3/aws4_request", r.Headers["Authorization"]));
    }

    [Fact]
    public async Task LegacyRedirectBodyCarriesTheRegion()
    {
        _http.On(HttpMethod.Get, "photos.s3.us-east-1.amazonaws.com", (_, _) => Xml(
            "<Error><Code>PermanentRedirect</Code><Message>Wrong endpoint</Message><Region>eu-central-1</Region></Error>",
            HttpStatusCode.TemporaryRedirect));
        _http.On(HttpMethod.Get, Bucket, (_, _) => Xml(ListResult("<IsTruncated>false</IsTruncated>")));
        using var provider = Connected(region: "us-east-1");

        Assert.Empty(await provider.ListDirectoryAsync("/photos"));
        Assert.Contains(_http.Requests, r => r.Uri.Host == Bucket);
    }

    // MARK: - Bucket scope (issue #53)

    [Fact]
    public async Task ScopedAccountShowsOnlyItsBucketWithoutAskingForTheList()
    {
        using var provider = Connected(rootPath: "/photos/team");

        var root = await provider.ListDirectoryAsync("/");

        var bucket = Assert.Single(root);
        Assert.Equal("photos", bucket.Name);
        Assert.Equal("/photos", bucket.Path);
        Assert.True(bucket.IsDirectory);
        Assert.Empty(_http.Requests);
        Assert.Equal("/photos/team", provider.RootPath);
    }

    [Fact]
    public async Task ConnectWithAPathProbesThatBucketNotTheAccount()
    {
        _http.On(HttpMethod.Get, Bucket, (_, _) => Xml(ListResult("<IsTruncated>false</IsTruncated>")));
        using var provider = new S3Provider(AccountId, _store, _http) { Clock = () => Now };
        Assert.False(provider.IsAuthenticated);

        var result = await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string>
            {
                ["accessKeyId"] = " AKIAEXAMPLE6UDK ",
                ["secretAccessKey"] = "secret",
                ["region"] = "eu-central-1",
                ["path"] = " photos/team/ ",
            }),
            new ScriptedConnectPrompt());

        Assert.Equal("***6UDK", result.AccountLabel);
        Assert.Equal("AWS S3 (***6UDK) — photos/team", result.DisplayName);
        Assert.Equal("/photos/team", result.RootPath);
        Assert.True(provider.IsAuthenticated);

        // The probe lists one key under the prefix; nothing goes to the account-wide host.
        Assert.DoesNotContain(_http.Requests, r => r.Uri.Host == "s3.eu-central-1.amazonaws.com");
        Assert.Contains(_http.Requests, r => r.Uri.Query == "?delimiter=%2F&list-type=2&max-keys=1&prefix=team%2F");

        var stored = _store.Load<S3Credentials>($"s3.{AccountId}");
        Assert.Equal("/photos/team", stored?.RootPath);
        Assert.Equal("AKIAEXAMPLE6UDK", stored?.AccessKeyId);
        Assert.Null(stored?.EndpointHost);
    }

    [Fact]
    public async Task DeniedBucketListPointsAtThePathFieldAndKeepsStoredCredentials()
    {
        _http.On(HttpMethod.Get, "https://s3.eu-central-1.amazonaws.com/", (_, _) => Xml(ErrorBody("AccessDenied", "Access Denied"), HttpStatusCode.Forbidden));
        using var provider = Connected();
        var before = _store.Load($"s3.{AccountId}");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string>
            {
                ["accessKeyId"] = "AKIANEWKEY0000",
                ["secretAccessKey"] = "new-secret",
                ["region"] = "eu-central-1",
            }),
            new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Contains("s3:ListAllMyBuckets", error.Message);
        Assert.Equal(before, _store.Load($"s3.{AccountId}"));
        Assert.True(provider.IsAuthenticated);
        Assert.Equal("AKIAEXAMPLE6UDK", _store.Load<S3Credentials>($"s3.{AccountId}")?.AccessKeyId);
    }

    [Fact]
    public async Task EditingMayLeaveTheSecretEmptyToKeepTheStoredOne()
    {
        _http.On(HttpMethod.Get, "https://s3.eu-west-1.amazonaws.com/", (_, _) => Xml("<ListAllMyBucketsResult><Buckets/></ListAllMyBucketsResult>"));
        using var provider = Connected();

        var secretField = provider.ConnectSpec.Modes[0].Fields.Single(f => f.Key == "secretAccessKey");
        Assert.False(secretField.Required);
        Assert.Equal(ConnectFieldKind.Password, secretField.Kind);
        Assert.DoesNotContain(provider.GetEditableConnectValues(), v => v.Value.Contains("secret", StringComparison.Ordinal));
        Assert.Equal("eu-central-1", provider.GetEditableConnectValues()["region"]);

        await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string> { ["accessKeyId"] = "AKIAEXAMPLE6UDK", ["region"] = "eu-west-1" }),
            new ScriptedConnectPrompt());

        var stored = _store.Load<S3Credentials>($"s3.{AccountId}");
        Assert.Equal("secret", stored?.SecretAccessKey);
        Assert.Equal("eu-west-1", stored?.Region);
        Assert.Equal("/", provider.RootPath);

        using var fresh = new S3Provider(Guid.NewGuid(), new MemoryCredentialStore(), _http);
        Assert.True(fresh.ConnectSpec.Modes[0].Fields.Single(f => f.Key == "secretAccessKey").Required);
    }

    // MARK: - Transfers

    [Fact]
    public async Task UploadIsOneSignedPutWithTheFilesHash()
    {
        var content = RandomNumberGenerator.GetBytes(300 * 1024);
        var local = TempFile("notes.txt", content);
        long? sentLength = null;
        _http.On(HttpMethod.Put, Bucket, (request, _) =>
        {
            sentLength = request.Content?.Headers.ContentLength;
            return Empty();
        });
        using var provider = Connected();

        long reported = 0;
        await provider.UploadFileAsync(local, "/photos/2024/new file.txt", d => Interlocked.Add(ref reported, d));

        var put = _http.Single(HttpMethod.Put, Bucket);
        Assert.Equal("/2024/new%20file.txt", put.Uri.AbsolutePath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), put.Headers["x-amz-content-sha256"]);
        Assert.Equal(content.Length, sentLength);
        Assert.Equal("text/plain", put.Headers["Content-Type"]);
        Assert.Equal("100-continue", put.Headers["Expect"]);
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date,", put.Headers["Authorization"]);
        Assert.Equal(content.Length, reported);
        Assert.Equal(S3FamilyProvider.SinglePutLimit, provider.MaxUploadFileSize);
    }

    [Fact]
    public async Task DownloadStreamsTheObjectToDisk()
    {
        var content = RandomNumberGenerator.GetBytes(200 * 1024);
        _http.OnBytes(HttpMethod.Get, Bucket + "/2024/beach", content);
        using var provider = Connected();
        var target = Path.Combine(_temp, "beach.jpg");

        long reported = 0;
        await provider.DownloadFileAsync("/photos/2024/beach photo.jpg", target, d => reported += d);

        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        Assert.Equal(content.Length, reported);
    }

    [Fact]
    public async Task MissingObjectIsNotFoundAndLeavesNoFile()
    {
        _http.On(HttpMethod.Get, Bucket, (_, _) => Xml(ErrorBody("NoSuchKey", "The specified key does not exist."), HttpStatusCode.NotFound));
        using var provider = Connected();
        var target = Path.Combine(_temp, "gone.jpg");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DownloadFileAsync("/photos/gone.jpg", target));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal("/photos/gone.jpg", error.ItemPath);
        Assert.False(File.Exists(target));
    }

    // MARK: - Errors

    [Theory]
    [InlineData(403, "AccessDenied", CloudErrorKind.CommandFailed, "AccessDenied: Access Denied")]
    [InlineData(403, null, CloudErrorKind.Unauthorized, null)]
    [InlineData(401, null, CloudErrorKind.Unauthorized, null)]
    [InlineData(503, "SlowDown", CloudErrorKind.RateLimited, null)]
    [InlineData(429, null, CloudErrorKind.RateLimited, null)]
    [InlineData(500, "InternalError", CloudErrorKind.CommandFailed, "S3 error (HTTP 500): InternalError: Access Denied")]
    [InlineData(500, null, CloudErrorKind.ServerError, null)]
    public async Task ErrorsCarryTheServersExplanation(int status, string? code, CloudErrorKind kind, string? message)
    {
        _http.On(HttpMethod.Get, Bucket, (_, _) => code is null ? Empty((HttpStatusCode)status) : Xml(ErrorBody(code, "Access Denied"), (HttpStatusCode)status));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/photos"));

        Assert.Equal(kind, error.Kind);
        if (message is not null)
        {
            Assert.Equal(message, error.Message);
        }
    }

    [Fact]
    public async Task NotSignedInIsNotAuthenticated()
    {
        using var provider = new S3Provider(Guid.NewGuid(), new MemoryCredentialStore(), _http);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
    }

    // MARK: - Mutations

    [Fact]
    public async Task DeletingAFolderDeletesEveryObjectUnderIt()
    {
        _http.On(HttpMethod.Get, Bucket, (request, _) => request.RequestUri!.Query.Contains("max-keys=1&", StringComparison.Ordinal)
            ? Xml(ListResult("<Contents><Key>2024/a.txt</Key><Size>1</Size></Contents>"))
            : Xml(ListResult("""
                <IsTruncated>false</IsTruncated>
                <Contents><Key>2024/</Key><Size>0</Size></Contents>
                <Contents><Key>2024/a.txt</Key><Size>1</Size></Contents>
                <Contents><Key>2024/deep/b.txt</Key><Size>2</Size></Contents>
                """)));
        _http.On(HttpMethod.Delete, Bucket, (_, _) => Empty(HttpStatusCode.NoContent));
        using var provider = Connected();

        await provider.DeleteItemAsync("/photos/2024");

        var deleted = _http.Requests.Where(r => r.Method == HttpMethod.Delete).Select(r => r.Uri.AbsolutePath).ToList();
        Assert.Equal(["/2024/", "/2024/a.txt", "/2024/deep/b.txt"], deleted);
        Assert.Contains(_http.Requests, r => r.Uri.Query == "?list-type=2&max-keys=1000&prefix=2024%2F");
    }

    [Fact]
    public async Task DeletingAFileDeletesOneObject()
    {
        _http.On(HttpMethod.Get, Bucket, (_, _) => Xml(ListResult("<KeyCount>0</KeyCount>")));
        _http.On(HttpMethod.Delete, Bucket, (_, _) => Empty(HttpStatusCode.NoContent));
        using var provider = Connected();

        await provider.DeleteItemAsync("/photos/2024/a.txt");

        Assert.Equal("/2024/a.txt", _http.Single(HttpMethod.Delete, Bucket).Uri.AbsolutePath);
        await Assert.ThrowsAsync<CloudProviderException>(() => provider.DeleteItemAsync("/photos"));
    }

    [Fact]
    public async Task CreatingAFolderPutsAnEmptyMarker()
    {
        long? sentLength = null;
        _http.On(HttpMethod.Put, Bucket, (request, _) =>
        {
            sentLength = request.Content?.Headers.ContentLength;
            return Empty();
        });
        using var provider = Connected();

        await provider.CreateDirectoryAsync("/photos/2024/new folder");

        var put = _http.Single(HttpMethod.Put, Bucket);
        Assert.Equal("/2024/new%20folder/", put.Uri.AbsolutePath);
        Assert.Equal(0, sentLength);
        Assert.Equal(SigV4.EmptyPayloadHash, put.Headers["x-amz-content-sha256"]);
    }

    [Fact]
    public async Task RenameCopiesServerSideThenDeletes()
    {
        _http.On(HttpMethod.Put, Bucket, (_, _) => Xml("<CopyObjectResult><ETag>\"x\"</ETag></CopyObjectResult>"));
        _http.On(HttpMethod.Delete, Bucket, (_, _) => Empty(HttpStatusCode.NoContent));
        using var provider = Connected();

        await provider.RenameItemAsync("/photos/2024/a b.txt", "c d.txt");

        var copy = _http.Single(HttpMethod.Put, Bucket);
        Assert.Equal("/2024/c%20d.txt", copy.Uri.AbsolutePath);
        Assert.Equal("/photos/2024/a%20b.txt", copy.Headers["x-amz-copy-source"]);
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-copy-source;x-amz-date,", copy.Headers["Authorization"]);
        Assert.Equal("/2024/a%20b.txt", _http.Single(HttpMethod.Delete, Bucket).Uri.AbsolutePath);
    }

    [Fact]
    public async Task CopyThatFailsMidwayKeepsTheSourceOfAMove()
    {
        // S3 reports an error that happens during the copy as 200 with an <Error> body.
        _http.On(HttpMethod.Put, "backups.s3.eu-central-1.amazonaws.com", (_, _) => Xml(ErrorBody("InternalError", "We encountered an internal error. Please try again.")));
        _http.On(HttpMethod.Delete, Bucket, (_, _) => Empty(HttpStatusCode.NoContent));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.MoveItemAsync("/photos/a.txt", "/backups/old/a.txt"));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Contains("InternalError", error.Message);
        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Equal("/photos/a.txt", _http.Single(HttpMethod.Put, "backups.s3").Headers["x-amz-copy-source"]);
    }

    [Fact]
    public async Task RenamingAFolderAsksCallersToFallBack()
    {
        _http.On(HttpMethod.Put, Bucket, (_, _) => Xml(ErrorBody("NoSuchKey", "The specified key does not exist."), HttpStatusCode.NotFound));
        _http.On(HttpMethod.Head, Bucket, (_, _) => Empty(HttpStatusCode.NotFound));
        _http.On(HttpMethod.Get, Bucket, (_, _) => Xml(ListResult("<CommonPrefixes><Prefix>2024/x/</Prefix></CommonPrefixes>")));
        using var provider = Connected();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.RenameItemAsync("/photos/2024", "2025"));

        Assert.Equal(CloudErrorKind.NotImplemented, error.Kind);
        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Delete);
    }

    // MARK: - Metadata

    [Fact]
    public async Task MetadataComesFromHead()
    {
        _http.On(HttpMethod.Head, Bucket, (_, _) =>
        {
            var response = Empty();
            response.Content.Headers.ContentLength = 4242;
            response.Content.Headers.LastModified = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
            response.Headers.ETag = new EntityTagHeaderValue("\"etag-1\"");
            return response;
        });
        using var provider = Connected();

        var item = await provider.GetFileMetadataAsync("/photos/2024/a.txt");

        Assert.Equal("a.txt", item.Name);
        Assert.False(item.IsDirectory);
        Assert.Equal(4242, item.Size);
        Assert.Equal(new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero), item.ModificationDate);
        Assert.Equal("\"etag-1\"", item.Checksum);
    }

    [Fact]
    public async Task MetadataOfAPrefixIsAFolder()
    {
        _http.On(HttpMethod.Head, Bucket, (_, _) => Empty(HttpStatusCode.NotFound));
        _http.On(HttpMethod.Get, Bucket, (request, _) => request.RequestUri!.Query.Contains("prefix=2024%2F", StringComparison.Ordinal)
            ? Xml(ListResult("<Contents><Key>2024/a.txt</Key><Size>1</Size></Contents>"))
            : Xml(ListResult(string.Empty)));
        using var provider = Connected();

        var folder = await provider.GetFileMetadataAsync("/photos/2024");
        Assert.True(folder.IsDirectory);
        Assert.Equal("2024", folder.Name);

        var bucket = await provider.GetFileMetadataAsync("/photos");
        Assert.True(bucket.IsDirectory);

        var missing = await Assert.ThrowsAsync<CloudProviderException>(() => provider.GetFileMetadataAsync("/photos/nothing"));
        Assert.Equal(CloudErrorKind.NotFound, missing.Kind);
    }

    [Fact]
    public async Task FolderSizeSumsEveryObjectUnderThePrefix()
    {
        _http.On(HttpMethod.Get, Bucket, (_, _) => Xml(ListResult("""
            <Contents><Key>2024/</Key><Size>0</Size></Contents>
            <Contents><Key>2024/a.txt</Key><Size>100</Size></Contents>
            <Contents><Key>2024/deep/b.txt</Key><Size>23</Size></Contents>
            """)));
        using var provider = Connected();

        Assert.Equal(123, await provider.FolderSizeAsync("/photos/2024"));
        Assert.Equal("?list-type=2&max-keys=1000&prefix=2024%2F", Assert.Single(_http.Requests).Uri.Query);
        Assert.Equal(0, await provider.FolderSizeAsync("/"));
    }

    // MARK: - Share links

    [Fact]
    public async Task ShareLinkIsAPresignedDownloadCappedAtSevenDays()
    {
        _http.On(HttpMethod.Head, Bucket, (_, _) => Empty());
        using var provider = Connected();

        var capabilities = provider.ShareLinkCapabilities;
        Assert.True(capabilities.CanCreate);
        Assert.True(capabilities.RequiresExpiry);
        Assert.False(capabilities.SupportsPassword);
        Assert.Equal(TimeSpan.FromDays(7), capabilities.MaximumExpiry);

        var link = await provider.CreateShareLinkAsync("/photos/2024/beach photo.jpg", new ShareLinkOptions { Expiry = Now.AddDays(30) });

        var query = System.Web.HttpUtility.ParseQueryString(link.Url.Query);
        Assert.Equal("https://photos.s3.eu-central-1.amazonaws.com/2024/beach%20photo.jpg", link.Url.GetLeftPart(UriPartial.Path));
        Assert.Equal("604800", query["X-Amz-Expires"]);
        Assert.Equal("AKIAEXAMPLE6UDK/20260105/eu-central-1/s3/aws4_request", query["X-Amz-Credential"]);
        Assert.Equal("attachment; filename=\"beach photo.jpg\"", query["response-content-disposition"]);
        Assert.Equal(link.Url, link.DirectDownloadUrl);
        Assert.Equal(Now.AddDays(7), link.ExpiresAt);
        Assert.False(link.HasPassword);
        Assert.NotNull(link.Note);

        var hour = await provider.CreateShareLinkAsync("/photos/a.txt", new ShareLinkOptions { Expiry = Now.AddHours(1) });
        Assert.Equal("3600", System.Web.HttpUtility.ParseQueryString(hour.Url.Query)["X-Amz-Expires"]);
    }

    [Fact]
    public async Task ShareLinkIsSignedForTheBucketsRealRegion()
    {
        _http.On(HttpMethod.Head, "photos.s3.us-east-1.amazonaws.com", (_, _) =>
        {
            var redirect = Empty(HttpStatusCode.MovedPermanently);
            redirect.Headers.TryAddWithoutValidation("x-amz-bucket-region", "eu-central-1");
            return redirect;
        });
        _http.On(HttpMethod.Head, Bucket, (_, _) => Empty());
        using var provider = Connected(region: "us-east-1");

        var link = await provider.CreateShareLinkAsync("/photos/a.txt", new ShareLinkOptions { Expiry = Now.AddDays(1) });

        Assert.Equal(Bucket, link.Url.Host);
        Assert.Contains("%2Feu-central-1%2Fs3%2F", link.Url.AbsoluteUri);
    }

    // MARK: - Registry

    [Theory]
    [InlineData(CloudProviderType.S3, typeof(S3Provider))]
    [InlineData(CloudProviderType.S3Compatible, typeof(S3CompatibleProvider))]
    [InlineData(CloudProviderType.SynologyC2, typeof(SynologyC2Provider))]
    [InlineData(CloudProviderType.SynologyDrive, typeof(FileFluss.Core.Providers.SynologyDrive.SynologyDriveProvider))]
    public void RegistryCreatesTheProvider(CloudProviderType type, Type expected)
    {
        using var provider = CloudProviderRegistry.Create(type, Guid.NewGuid(), new MemoryCredentialStore());
        Assert.IsType(expected, provider);
        Assert.Equal(type, provider.ProviderType);
        Assert.False(provider.IsAuthenticated);
    }
}

public sealed class S3CompatibleAndC2Tests
{
    private static readonly Guid AccountId = Guid.Parse("0d6c1e2f-3a4b-4a8e-9a43-6f9b0c516a2f");
    private readonly FakeHttpHandler _http = new();
    private readonly MemoryCredentialStore _store = new();

    private static HttpResponseMessage Xml(string xml)
        => new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    [Theory]
    [InlineData("https://fsn1.your-objectstorage.com", "fsn1.your-objectstorage.com")]
    [InlineData("fsn1.your-objectstorage.com", "fsn1.your-objectstorage.com")]
    [InlineData(" https://fsn1.your-objectstorage.com/ ", "fsn1.your-objectstorage.com")]
    [InlineData("https://minio.example.com:9000", "minio.example.com:9000")]
    [InlineData("minio.example.com:9000", "minio.example.com:9000")]
    [InlineData("https://s3.example.com:443/path", "s3.example.com")]
    [InlineData("http://nas.local:9000/", "nas.local:9000")]
    public void EndpointBecomesAHost(string typed, string expected)
        => Assert.Equal(expected, S3CompatibleProvider.HostFromEndpoint(typed));

    [Theory]
    [InlineData("fsn1.your-objectstorage.com", "fsn1")]
    [InlineData("s3.eu-central-003.backblazeb2.com", "eu-central-003")]
    [InlineData("eu-005.s3.synologyc2.net", "eu-005")]
    [InlineData("0123456789abcdef.r2.cloudflarestorage.com", "auto")]
    [InlineData("minio.local:9000", "minio")]
    [InlineData("", "us-east-1")]
    public void RegionIsGuessedFromTheHost(string host, string expected)
        => Assert.Equal(expected, S3CompatibleProvider.RegionGuess(host));

    [Fact]
    public void EndpointPresetsFillTheEndpointField()
    {
        using var provider = new S3CompatibleProvider(AccountId, _store, _http);
        var endpoint = provider.ConnectSpec.Modes[0].Fields.Single(f => f.Key == "endpoint");
        Assert.Equal(ConnectFieldKind.Choice, endpoint.Kind);
        Assert.True(endpoint.AllowsCustomValue);
        Assert.Contains(endpoint.Choices, c => c.Value == "https://fsn1.your-objectstorage.com");
        Assert.All(endpoint.Choices, c => Assert.StartsWith("https://", c.Value));
        Assert.All(endpoint.Choices, c => Assert.NotEqual(string.Empty, S3CompatibleProvider.HostFromEndpoint(c.Value)));
    }

    [Fact]
    public async Task CompatibleConnectUsesTheEndpointAndGuessedRegion()
    {
        _http.On(HttpMethod.Get, "https://fsn1.your-objectstorage.com/", (_, _) => Xml("<ListAllMyBucketsResult><Buckets><Bucket><Name>data</Name></Bucket></Buckets></ListAllMyBucketsResult>"));
        _http.On(HttpMethod.Get, "https://data.fsn1.your-objectstorage.com/", (_, _) => Xml("<ListBucketResult><Contents><Key>x.bin</Key><Size>3</Size></Contents></ListBucketResult>"));
        using var provider = new S3CompatibleProvider(AccountId, _store, _http);

        var result = await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string>
            {
                ["accessKeyId"] = "HETZNERKEY",
                ["secretAccessKey"] = "s",
                ["endpoint"] = "https://fsn1.your-objectstorage.com",
            }),
            new ScriptedConnectPrompt());

        Assert.Equal("S3 (fsn1.your-objectstorage.com)", result.DisplayName);
        Assert.Contains("/fsn1/s3/aws4_request", _http.Requests.Single().Headers["Authorization"]);

        Assert.Equal("data", Assert.Single(await provider.ListDirectoryAsync("/")).Name);
        Assert.Equal("x.bin", Assert.Single(await provider.ListDirectoryAsync("/data")).Name);

        var stored = _store.Load<S3Credentials>($"s3Compatible.{AccountId}");
        Assert.Equal("fsn1.your-objectstorage.com", stored?.EndpointHost);
        Assert.Equal("fsn1", stored?.Region);
        Assert.Equal("fsn1.your-objectstorage.com", provider.GetEditableConnectValues()["endpoint"]);
    }

    [Fact]
    public async Task CompatibleKeepsANamedScopeAndAPort()
    {
        _http.On(HttpMethod.Get, "https://media.minio.example.com:9000/", (_, _) => Xml("<ListBucketResult/>"));
        using var provider = new S3CompatibleProvider(AccountId, _store, _http);

        var result = await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string>
            {
                ["accessKeyId"] = "minioadmin",
                ["secretAccessKey"] = "s",
                ["endpoint"] = "https://minio.example.com:9000",
                ["region"] = "us-east-1",
                ["displayName"] = "Home MinIO",
                ["path"] = "media",
            }),
            new ScriptedConnectPrompt());

        Assert.Equal("Home MinIO — media", result.DisplayName);
        Assert.Equal("/media", result.RootPath);
        Assert.All(_http.Requests, r => Assert.Equal("media.minio.example.com:9000", r.Headers["Host"]));
        Assert.DoesNotContain(_http.Requests, r => r.Uri.Host == "minio.example.com");
    }

    [Theory]
    [InlineData("eu-005", "eu-005.s3.synologyc2.net")]
    [InlineData("https://eu-005.s3.synologyc2.net/", "eu-005.s3.synologyc2.net")]
    [InlineData(" eu-001.s3.synologyc2.net ", "eu-001.s3.synologyc2.net")]
    public void C2EndpointFromWhateverWasPasted(string typed, string expected)
    {
        var host = SynologyC2Provider.EndpointForRegion(typed);
        Assert.Equal(expected, host);
        Assert.Equal(expected.Split('.')[0], SynologyC2Provider.RegionFromEndpoint(host));
    }

    [Fact]
    public async Task C2ConnectNamesTheRegion()
    {
        _http.On(HttpMethod.Get, "https://eu-005.s3.synologyc2.net/", (_, _) => Xml("<ListAllMyBucketsResult><Buckets/></ListAllMyBucketsResult>"));
        using var provider = new SynologyC2Provider(AccountId, _store, _http);

        var result = await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string>
            {
                ["accessKeyId"] = "C2KEY",
                ["secretAccessKey"] = "s",
                ["endpoint"] = "https://eu-005.s3.synologyc2.net",
            }),
            new ScriptedConnectPrompt());

        Assert.Equal("Synology C2 (eu-005)", result.DisplayName);
        Assert.Equal("eu-005", result.AccountLabel);
        Assert.Contains("/eu-005/s3/aws4_request", _http.Requests.Single().Headers["Authorization"]);
        Assert.Equal("eu-005.s3.synologyc2.net", provider.GetEditableConnectValues()["endpoint"]);
        Assert.Equal(S3FamilyProvider.SinglePutLimit, provider.MaxUploadFileSize);
    }
}
