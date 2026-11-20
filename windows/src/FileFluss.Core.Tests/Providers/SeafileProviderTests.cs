// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Text.Json;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Seafile;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

public class SeafileProviderTests
{
    private const string Server = "https://seafile.example.test";
    private const string Libraries = """
        [
          {"id":"aaaaaaaa-1111","name":"My Library","owner":"me@example.com","encrypted":false,"mtime":1700000000,"size":1234},
          {"id":"bbbbbbbb-2222","name":"Shared","encrypted":false,"mtime":1700000100,"size":10},
          {"id":"cccccccc-3333","name":"Shared","encrypted":true,"mtime":1700000200,"size":20}
        ]
        """;

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeHttpHandler _http = new();

    private SeafileProvider Signed()
    {
        _store.Save($"seafile.{_accountId}", new SeafileCredentials(Server, "me@example.com", "tok-1"));
        return new SeafileProvider(_accountId, _store, _http);
    }

    private SeafileCredentials? Stored() => _store.Load<SeafileCredentials>($"seafile.{_accountId}");

    private static ConnectValues Values(string? otp = null, bool selfSigned = false) => new("default", new Dictionary<string, string>
    {
        ["serverUrl"] = "seafile.example.test/",
        ["email"] = "me@example.com",
        ["password"] = "p&ss=word+",
        ["otp"] = otp ?? string.Empty,
        ["allowSelfSigned"] = selfSigned ? "true" : "false",
    });

    // MARK: - Connect

    [Fact]
    public async Task ConnectExchangesThePasswordForAToken()
    {
        _http.OnJson(HttpMethod.Post, "/api2/auth-token/", """{"token":"tok-new"}""");
        using var provider = new SeafileProvider(_accountId, _store, _http);

        var result = await provider.ConnectAsync(Values(selfSigned: true), new ScriptedConnectPrompt());

        Assert.Equal("me@example.com", result.AccountLabel);
        var stored = Stored()!;
        Assert.Equal(Server, stored.ServerUrl);
        Assert.Equal("tok-new", stored.Token);
        Assert.True(stored.AllowSelfSignedCertificate);
        var request = _http.Single(HttpMethod.Post, "/api2/auth-token/");
        Assert.Equal(Server + "/api2/auth-token/", request.Uri.AbsoluteUri);

        // Form-encoded properly: "&", "=" and "+" in a password must not split the body.
        Assert.Equal("username=me%40example.com&password=p%26ss%3Dword%2B", request.Body);
        Assert.False(request.Headers.ContainsKey("X-SEAFILE-OTP"));
        Assert.Equal("true", provider.GetEditableConnectValues()["allowSelfSigned"]);
    }

    [Fact]
    public async Task TwoFactorRequiredPromptsForTheCodeAndRetries()
    {
        var calls = 0;
        _http.On(HttpMethod.Post, "/api2/auth-token/", (request, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                var refused = FakeHttpHandler.Json("""{"non_field_errors":["Two factor auth token is missing."]}""", HttpStatusCode.Forbidden);
                refused.Headers.Add("X-SEAFILE-OTP", "required");
                return refused;
            }

            return FakeHttpHandler.Json("""{"token":"tok-2fa"}""");
        });
        using var provider = new SeafileProvider(_accountId, _store, _http);
        var prompt = new ScriptedConnectPrompt();
        prompt.Codes.Enqueue(" 123456 ");

        await provider.ConnectAsync(Values(), prompt);

        Assert.Equal("tok-2fa", Stored()!.Token);
        Assert.Equal("123456", _http.Requests.Last().Headers["X-SEAFILE-OTP"]);
    }

    [Fact]
    public async Task CancelledTwoFactorPromptLeavesStoredCredentialsUntouched()
    {
        _http.On(HttpMethod.Post, "/api2/auth-token/", (_, _) =>
        {
            var refused = FakeHttpHandler.Json("{}", HttpStatusCode.Forbidden);
            refused.Headers.Add("X-SEAFILE-OTP", "required");
            return refused;
        });
        using var provider = Signed();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(Values(), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.TwoFactorRequired, error.Kind);
        Assert.Equal("tok-1", Stored()!.Token);
    }

    [Fact]
    public async Task WrongPasswordIsInvalidCredentials()
    {
        _http.OnJson(HttpMethod.Post, "/api2/auth-token/", """{"non_field_errors":["Unable to login with provided credentials."]}""", HttpStatusCode.BadRequest);
        using var provider = Signed();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(Values("000000"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal("000000", _http.Single(HttpMethod.Post, "/api2/auth-token/").Headers["X-SEAFILE-OTP"]);
        Assert.Equal("tok-1", Stored()!.Token);
    }

    [Theory]
    [InlineData("seafile.example.test", "https://seafile.example.test")]
    [InlineData(" http://nas.local:8000/ ", "http://nas.local:8000")]
    [InlineData("https://example.com/seafile//", "https://example.com/seafile")]
    public void ServerUrlIsNormalised(string raw, string expected)
        => Assert.Equal(expected, SeafileProvider.NormalizeServerUrl(raw));

    // MARK: - Listing

    [Fact]
    public async Task RootListsLibrariesAndDisambiguatesDuplicateNames()
    {
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        using var provider = Signed();

        var items = await provider.ListDirectoryAsync("/");

        Assert.Equal(["My Library", "Shared (bbbbbb)", "Shared (cccccc)"], items.Select(i => i.Name).ToArray());
        Assert.All(items, i => Assert.True(i.IsDirectory));
        var library = items[0];
        Assert.Equal("/My Library", library.Path);
        Assert.Equal("lib:aaaaaaaa-1111", library.Id);
        Assert.Equal(1234, library.Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), library.ModificationDate);
        Assert.Equal("Token tok-1", _http.Requests.Single().Headers["Authorization"]);
    }

    [Fact]
    public async Task LibraryPathListsEntriesWithSizesDatesAndChecksums()
    {
        _http.OnJson(HttpMethod.Get, "/api2/repos/aaaaaaaa-1111/dir/", """
            [
              {"type":"dir","id":"0000","name":"Photos","mtime":1700000300,"permission":"rw"},
              {"type":"file","id":"sha1abc","name":"a & b.txt","size":42,"mtime":1700000400,"modifier_email":"x"}
            ]
            """);
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        using var provider = Signed();

        var items = await provider.ListDirectoryAsync("/My Library/Docs & More");

        var folder = Assert.Single(items, i => i.IsDirectory);
        Assert.Equal("/My Library/Docs & More/Photos", folder.Path);
        Assert.Null(folder.Checksum);
        var file = Assert.Single(items, i => !i.IsDirectory);
        Assert.Equal(42, file.Size);
        Assert.Equal("sha1abc", file.Checksum);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000400), file.ModificationDate);
        Assert.Equal("f:aaaaaaaa-1111:/My Library/Docs & More/a & b.txt", file.Id);

        // Strict query encoding keeps "&" from splitting the query.
        Assert.Contains(_http.Requests, r => r.Uri.AbsoluteUri == Server + "/api2/repos/aaaaaaaa-1111/dir/?p=/Docs%20%26%20More");
    }

    [Fact]
    public async Task UnknownLibraryIsNotFoundAfterOneRefresh()
    {
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        using var provider = Signed();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/Nope/x"));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Single(_http.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{\"detail\":\"Invalid token\"}", CloudErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, "{\"error_msg\":\"Folder not found.\"}", CloudErrorKind.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, "{\"error_msg\":\"Permission denied.\"}", CloudErrorKind.CommandFailed)]
    [InlineData(HttpStatusCode.Forbidden, "", CloudErrorKind.Unauthorized)]
    [InlineData((HttpStatusCode)440, "{\"error_msg\":\"Library is encrypted.\"}", CloudErrorKind.CommandFailed)]
    [InlineData((HttpStatusCode)443, "", CloudErrorKind.QuotaExceeded)]
    [InlineData(HttpStatusCode.TooManyRequests, "", CloudErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, "", CloudErrorKind.ServerError)]
    public async Task ErrorsMapToKinds(HttpStatusCode status, string body, CloudErrorKind expected)
    {
        _http.OnText(HttpMethod.Get, "/api2/repos/aaaaaaaa-1111/dir/", body, status, "application/json");
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        using var provider = Signed();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/My Library/x"));

        Assert.Equal(expected, error.Kind);
    }

    [Fact]
    public async Task MetadataOfALibraryAndOfAFile()
    {
        _http.OnJson(HttpMethod.Get, "/file/detail/", """{"id":"sha1","mtime":1700000500,"type":"file","name":"a.txt","size":7}""");
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        using var provider = Signed();

        var library = await provider.GetFileMetadataAsync("/My Library");
        var file = await provider.GetFileMetadataAsync("/My Library/a.txt");

        Assert.True(library.IsDirectory);
        Assert.Equal("lib:aaaaaaaa-1111", library.Id);
        Assert.False(file.IsDirectory);
        Assert.Equal(7, file.Size);
        Assert.Equal("sha1", file.Checksum);
    }

    // MARK: - Transfers

    [Fact]
    public void LinksAreRewrittenToTheTypedServer()
    {
        Assert.Equal(
            "https://seafile.example.test/seafhttp/files/abc/a%20b.txt",
            SeafileProvider.RewriteToServer("\"http://internal:8082/seafhttp/files/abc/a%20b.txt\"", Server)!.AbsoluteUri);
        Assert.Equal(
            "http://nas.local:8000/seafhttp/upload-api/xyz?ret-json=1",
            SeafileProvider.RewriteToServer("https://cloud.example.com/seafhttp/upload-api/xyz?ret-json=1", "http://nas.local:8000")!.AbsoluteUri);
        Assert.Null(SeafileProvider.RewriteToServer("\"not a url\"", Server));
    }

    [Fact]
    public async Task DownloadFetchesTheSignedLinkThenTheBytes()
    {
        var local = Path.Combine(Path.GetTempPath(), $"ff-seafile-{Guid.NewGuid():N}.bin");
        try
        {
            _http.OnBytes(HttpMethod.Get, "/seafhttp/files/", [9, 8, 7]);
            _http.OnJson(HttpMethod.Get, "/api2/repos/aaaaaaaa-1111/file/", "\"https://internal.example:8443/seafhttp/files/tok/a.bin\"");
            _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
            using var provider = Signed();
            long reported = 0;

            await provider.DownloadFileAsync("/My Library/a.bin", local, d => reported += d);

            Assert.Equal([9, 8, 7], await File.ReadAllBytesAsync(local));
            Assert.Equal(3, reported);
            var fetch = _http.Single(HttpMethod.Get, "/seafhttp/files/");
            Assert.Equal(Server + "/seafhttp/files/tok/a.bin", fetch.Uri.AbsoluteUri);
            Assert.False(fetch.Headers.ContainsKey("Authorization"));
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public async Task UploadPostsMultipartToTheUploadLink()
    {
        var local = Path.Combine(Path.GetTempPath(), $"ff-seafile-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(local, "seafile bytes");
        try
        {
            _http.OnText(HttpMethod.Post, "/seafhttp/upload-api/", "sha1");
            _http.OnJson(HttpMethod.Get, "/upload-link/", "\"https://other.host/seafhttp/upload-api/u1\"");
            _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
            using var provider = Signed();
            long reported = 0;

            await provider.UploadFileAsync(local, "/My Library/Docs/Übersicht.txt", d => reported += d);

            Assert.Equal(13, reported);
            Assert.Contains(_http.Requests, r => r.Uri.AbsoluteUri == Server + "/api2/repos/aaaaaaaa-1111/upload-link/?p=/Docs");
            var upload = _http.Single(HttpMethod.Post, "/seafhttp/upload-api/");
            Assert.Equal(Server + "/seafhttp/upload-api/u1", upload.Uri.AbsoluteUri);
            Assert.StartsWith("multipart/form-data; boundary=", upload.Headers["Content-Type"]);
            Assert.Contains("name=\"parent_dir\"\r\n\r\n/Docs\r\n", upload.Body);
            Assert.Contains("name=\"replace\"\r\n\r\n1\r\n", upload.Body);
            Assert.Contains("name=\"file\"; filename=\"Übersicht.txt\"", upload.Body);
            Assert.Contains("\r\n\r\nseafile bytes\r\n--", upload.Body);
            Assert.Equal("Token tok-1", upload.Headers["Authorization"]);
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public async Task UploadToTheLibraryListIsRefused()
    {
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        using var provider = Signed();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync("x", "/My Library"));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
    }

    // MARK: - File operations

    [Fact]
    public async Task CreatingAnExistingFolderIsANoOp()
    {
        _http.OnJson(HttpMethod.Get, "/api2/repos/aaaaaaaa-1111/dir/", """[{"type":"dir","id":"1","name":"Photos","mtime":1}]""");
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        _http.OnJson(HttpMethod.Post, "/dir/", "\"success\"");
        using var provider = Signed();

        await provider.CreateDirectoryAsync("/My Library/Photos");
        await provider.CreateDirectoryAsync("/My Library/New");

        var post = Assert.Single(_http.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal(Server + "/api2/repos/aaaaaaaa-1111/dir/?p=/New", post.Uri.AbsoluteUri);
        Assert.Equal("operation=mkdir", post.Body);
    }

    [Fact]
    public async Task LibrariesCannotBeCreatedDeletedOrRenamedHere()
    {
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        using var provider = Signed();

        await provider.CreateDirectoryAsync("/My Library");
        Assert.Equal(CloudErrorKind.CommandFailed, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateDirectoryAsync("/Brand New"))).Kind);
        Assert.Equal(CloudErrorKind.CommandFailed, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.DeleteItemAsync("/My Library"))).Kind);
        Assert.Equal(CloudErrorKind.CommandFailed, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.RenameItemAsync("/My Library", "x"))).Kind);
        Assert.DoesNotContain(_http.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task DeleteAndRenamePickTheFileOrDirEndpoint()
    {
        _http.OnJson(HttpMethod.Get, "/api2/repos/aaaaaaaa-1111/dir/", """[{"type":"dir","id":"1","name":"Photos"},{"type":"file","id":"2","name":"a.txt"}]""");
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        _http.OnJson(HttpMethod.Delete, "/api2/repos/", "\"success\"");
        _http.OnJson(HttpMethod.Post, "/api2/repos/", "\"success\"");
        using var provider = Signed();

        await provider.DeleteItemAsync("/My Library/Photos");
        await provider.DeleteItemAsync("/My Library/a.txt");
        await provider.RenameItemAsync("/My Library/a.txt", "b & c.txt");

        var changes = _http.Requests.Where(r => r.Method != HttpMethod.Get).Select(r => $"{r.Method} {r.Uri.PathAndQuery} {r.Body}").ToList();
        Assert.Equal(
            [
                "DELETE /api2/repos/aaaaaaaa-1111/dir/?p=/Photos ",
                "DELETE /api2/repos/aaaaaaaa-1111/file/?p=/a.txt ",
                "POST /api2/repos/aaaaaaaa-1111/file/?p=/a.txt operation=rename&newname=b+%26+c.txt",
            ],
            changes);
    }

    [Fact]
    public async Task MoveAcrossLibrariesUsesFileopsThenRenames()
    {
        _http.OnJson(HttpMethod.Get, "/api2/repos/bbbbbbbb-2222/dir/", "[]");
        _http.OnJson(HttpMethod.Get, "/api2/repos/aaaaaaaa-1111/dir/", """[{"type":"file","id":"2","name":"a.txt"}]""");
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        _http.OnJson(HttpMethod.Post, "/api2/repos/", "true");
        using var provider = Signed();

        await provider.MoveItemAsync("/My Library/a.txt", "/Shared (bbbbbb)/Inbox/renamed.txt");

        var posts = _http.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => $"{r.Uri.PathAndQuery} {r.Body}").ToList();
        Assert.Equal(
            [
                "/api2/repos/aaaaaaaa-1111/fileops/move/?p=/ file_names=a.txt&dst_repo=bbbbbbbb-2222&dst_dir=%2FInbox",
                "/api2/repos/bbbbbbbb-2222/file/?p=/Inbox/a.txt operation=rename&newname=renamed.txt",
            ],
            posts);
    }

    [Fact]
    public async Task CopyOntoAnExistingNameFailsAndAmbiguousRenamesFallBack()
    {
        _http.OnJson(HttpMethod.Get, "/api2/repos/bbbbbbbb-2222/dir/", """[{"type":"file","id":"9","name":"taken.txt"},{"type":"file","id":"8","name":"a.txt"}]""");
        _http.OnJson(HttpMethod.Get, "/api2/repos/aaaaaaaa-1111/dir/", """[{"type":"file","id":"2","name":"a.txt"}]""");
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        using var provider = Signed();

        var clash = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/My Library/a.txt", "/Shared (bbbbbb)/taken.txt"));
        var ambiguous = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/My Library/a.txt", "/Shared (bbbbbb)/other.txt"));
        var sameFolder = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/My Library/a.txt", "/My Library/copy.txt"));

        Assert.Equal(CloudErrorKind.CommandFailed, clash.Kind);
        Assert.Equal(CloudErrorKind.NotImplemented, ambiguous.Kind);
        Assert.Equal(CloudErrorKind.NotImplemented, sameFolder.Kind);
        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Post);
    }

    // MARK: - Quota and share links

    [Theory]
    [InlineData("""{"usage":100,"total":1000}""", 100L, 1000L)]
    [InlineData("""{"usage":100,"total":-2}""", 100L, null)]
    [InlineData("""{"usage":5,"total":0}""", 5L, null)]
    public async Task QuotaReportsUsedAndTotal(string json, long used, long? total)
    {
        _http.OnJson(HttpMethod.Get, "/api2/account/info/", json);
        using var provider = Signed();

        var quota = await provider.StorageQuotaAsync();

        Assert.NotNull(quota);
        Assert.Equal(used, quota.UsedBytes);
        Assert.Equal(total, quota.TotalBytes);
    }

    [Fact]
    public async Task ShareLinkCreationSendsOnlyTheNeededFields()
    {
        _http.OnJson(HttpMethod.Post, "/api/v2.1/share-links/", """{"link":"https://seafile.example.test/f/abc/","token":"abc","expire_date":"2030-01-02T03:04:05+00:00","permissions":{"can_edit":false,"can_download":true}}""");
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        using var provider = Signed();

        var link = await provider.CreateShareLinkAsync("/My Library/a.txt", ShareLinkOptions.Default);

        Assert.Equal("https://seafile.example.test/f/abc/", link.Url.AbsoluteUri);
        Assert.Equal("https://seafile.example.test/f/abc/?dl=1", link.DirectDownloadUrl!.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero), link.ExpiresAt);
        Assert.False(link.HasPassword);
        using var body = JsonDocument.Parse(_http.Single(HttpMethod.Post, "/api/v2.1/share-links/").Body!);
        Assert.Equal("aaaaaaaa-1111", body.RootElement.GetProperty("repo_id").GetString());
        Assert.Equal("/a.txt", body.RootElement.GetProperty("path").GetString());
        Assert.False(body.RootElement.TryGetProperty("expire_days", out _));
        Assert.False(body.RootElement.TryGetProperty("password", out _));
        Assert.False(body.RootElement.TryGetProperty("permissions", out _));
    }

    [Fact]
    public async Task ShareLinkWithPasswordExpiryAndNoDownload()
    {
        _http.OnJson(HttpMethod.Post, "/api/v2.1/share-links/", """{"link":"https://seafile.example.test/f/abc/","token":"abc","permissions":{"can_download":false}}""");
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        using var provider = Signed();

        var link = await provider.CreateShareLinkAsync("/My Library/a.txt", new ShareLinkOptions
        {
            Password = "secret",
            Expiry = DateTimeOffset.Now.AddHours(30),
            AllowDownload = false,
        });

        Assert.Null(link.DirectDownloadUrl);
        Assert.True(link.HasPassword);
        using var body = JsonDocument.Parse(_http.Single(HttpMethod.Post, "/api/v2.1/share-links/").Body!);
        Assert.Equal(2, body.RootElement.GetProperty("expire_days").GetInt32());
        Assert.Equal("secret", body.RootElement.GetProperty("password").GetString());
        Assert.False(body.RootElement.GetProperty("permissions").GetProperty("can_download").GetBoolean());
    }

    [Fact]
    public async Task AlreadySharedReturnsTheExistingLinkWithANote()
    {
        _http.OnJson(HttpMethod.Post, "/api/v2.1/share-links/", """{"error_msg":"Share link a.txt already exists."}""", HttpStatusCode.BadRequest);
        _http.OnJson(HttpMethod.Get, "/api/v2.1/share-links/", """[{"link":"https://seafile.example.test/f/old/","token":"old","is_password_set":true}]""");
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        using var provider = Signed();

        var link = await provider.CreateShareLinkAsync("/My Library/a.txt", ShareLinkOptions.Default);

        Assert.Equal("https://seafile.example.test/f/old/", link.Url.AbsoluteUri);
        Assert.True(link.HasPassword);
        Assert.NotNull(link.Note);
        Assert.Contains(_http.Requests, r => r.Uri.AbsoluteUri == Server + "/api/v2.1/share-links/?repo_id=aaaaaaaa-1111&path=/a.txt");
    }

    [Fact]
    public async Task ServerRefusalIsSurfacedVerbatim()
    {
        _http.OnJson(HttpMethod.Post, "/api/v2.1/share-links/", """{"error_msg":"Password is too short."}""", HttpStatusCode.BadRequest);
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        using var provider = Signed();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateShareLinkAsync("/My Library/a.txt", new ShareLinkOptions { Password = "x" }));

        Assert.Equal("Password is too short.", error.Message);
    }

    [Fact]
    public async Task UpdateReplacesTheLinkAndSaysSo()
    {
        _http.OnJson(HttpMethod.Delete, "/api/v2.1/share-links/old/", "{\"success\":true}");
        _http.OnJson(HttpMethod.Post, "/api/v2.1/share-links/", """{"link":"https://seafile.example.test/f/new/","token":"new"}""");
        _http.OnJson(HttpMethod.Get, "/api/v2.1/share-links/", """[{"link":"https://seafile.example.test/f/old/","token":"old"}]""");
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        using var provider = Signed();

        var link = await provider.UpdateShareLinkAsync("/My Library/a.txt", new ShareLinkOptions { Password = "longer-secret" });

        Assert.Equal("https://seafile.example.test/f/new/", link.Url.AbsoluteUri);
        Assert.False(string.IsNullOrEmpty(link.Note));
        Assert.Single(_http.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task UnsharedPathHasNoLinkAndRemoveIsQuiet()
    {
        _http.OnJson(HttpMethod.Get, "/api/v2.1/share-links/", "[]");
        _http.OnJson(HttpMethod.Get, "/api2/repos/", Libraries);
        using var provider = Signed();

        Assert.Null(await provider.ExistingShareLinkAsync("/My Library/a.txt"));
        Assert.Null(await provider.ExistingShareLinkAsync("/"));
        await provider.RemoveShareLinkAsync("/My Library/a.txt");
        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public void ShareExpiryParsing()
    {
        Assert.Null(SeafileProvider.ParseShareExpiry(""));
        Assert.Null(SeafileProvider.ParseShareExpiry(null));
        Assert.Equal(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.FromHours(8)), SeafileProvider.ParseShareExpiry("2030-01-02T03:04:05+08:00"));
        Assert.NotNull(SeafileProvider.ParseShareExpiry("2030-01-02T03:04:05"));
    }

    [Fact]
    public void RegistryCreatesTheProviderWithoutCredentials()
    {
        using var provider = CloudProviderRegistry.Create(CloudProviderType.Seafile, _accountId, _store);
        Assert.IsType<SeafileProvider>(provider);
        Assert.False(provider.IsAuthenticated);
    }
}
