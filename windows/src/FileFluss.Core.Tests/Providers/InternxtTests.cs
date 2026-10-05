// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Internxt;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;
using static FileFluss.Core.Tests.Providers.EncryptedProviderTestKit;

namespace FileFluss.Core.Tests.Providers;

public class InternxtCryptoTests
{
    [Theory]
    [InlineData("", "9c1185a5c5e9fc54612808977ee8f548b2258d31")]
    [InlineData("a", "0bdc9d2d256b3ee9daae347be6f4dc835a467ffe")]
    [InlineData("abc", "8eb208f7e05d987a9b044a8e98c6b087f15a0bfc")]
    [InlineData("message digest", "5d0689ef49d2fae572b881b123a85ffa21595f36")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "f71c27109c692c1b56bbdceb5b9d2865b3708dbc")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "12a053384a9c0c88e405a06c27dcf49ada62eb2b")]
    public void Ripemd160MatchesTheReferenceVectors(string message, string expected)
        => Assert.Equal(expected, ToHex(InternxtCrypto.Ripemd160(Encoding.ASCII.GetBytes(message))));

    [Fact]
    public void Ripemd160OfAMillionAs()
        => Assert.Equal("52783243c1697bdbe16d37f97f68f08325dc1528", ToHex(InternxtCrypto.Ripemd160(Enumerable.Repeat((byte)'a', 1_000_000).ToArray())));

    [Fact]
    public void Bip39SeedMatchesTheStandardVector()
    {
        // BIP39 reference mnemonic with an empty passphrase (bip39.mnemonicToSeed without one).
        var seed = InternxtCrypto.MnemonicToSeed("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about");
        Assert.Equal(
            "5eb00bbddcf069084889a8ab9155568165f5c453ccb85e70811aaed6f6da5fc19a5ac40b389cd370d086206dec8aa6c43daea6690f20ad3d8d48b2d2ce9e38e4",
            ToHex(seed));
    }

    [Fact]
    public void CtrMatchesNistSp80038aAes256Vectors()
    {
        // NIST SP 800-38A F.5.5 (CTR-AES256.Encrypt).
        var key = Hex("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
        var data = Hex("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710");

        using var ctr = new InternxtAesCtr(key, Hex("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff"));
        ctr.Transform(data.AsSpan(0, 7));
        ctr.Transform(data.AsSpan(7));

        Assert.Equal("601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c52b0930daa23de94ce87017ba2d84988ddfc9c58db67aada613c2dd08457941a6", ToHex(data));
    }

    [Fact]
    public void CryptoJsEnvelopeIsSaltedEvpBytesToKeyCbc()
    {
        var salt = Hex("0102030405060708");
        var hex = InternxtCrypto.EncryptText("the quick brown fox", "secret", salt);

        Assert.StartsWith(ToHex("Salted__"u8.ToArray()) + "0102030405060708", hex);

        // Reference EVP_BytesToKey(MD5): D_i = MD5(D_{i-1} ‖ secret ‖ salt); key = D_0 ‖ D_1, IV = D_2.
        var material = "secret"u8.ToArray().Concat(salt).ToArray();
        var d0 = MD5.HashData(material);
        var d1 = MD5.HashData(d0.Concat(material).ToArray());
        var d2 = MD5.HashData(d1.Concat(material).ToArray());
        using var aes = Aes.Create();
        aes.Key = d0.Concat(d1).ToArray();
        var plain = aes.DecryptCbc(Hex(hex).AsSpan(16), d2, PaddingMode.PKCS7);
        Assert.Equal("the quick brown fox", Encoding.UTF8.GetString(plain));

        Assert.Equal("the quick brown fox", InternxtCrypto.DecryptText(hex, "secret"));
        Assert.Null(InternxtCrypto.DecryptText(hex, "wrong secret"));
        Assert.Null(InternxtCrypto.DecryptText("zz", "secret"));
    }

    [Fact]
    public void PasswordHashIsPbkdf2Sha1OverTheHexDecodedSalt()
    {
        // RFC 6070: PBKDF2-HMAC-SHA1("password", "salt", 1) = 0c60c80f…; the 32-byte output starts with it.
        var hash = InternxtCrypto.PassToHash("password", ToHex("salt"u8.ToArray()), iterations: 1)!;
        Assert.StartsWith("0c60c80f961f0e71f3a9b524af6012062fe037a6", hash);
        Assert.Equal(64, hash.Length);
        Assert.Null(InternxtCrypto.PassToHash("password", "not hex"));
    }

    [Fact]
    public void EncryptedPasswordHashUnwrapsTheSaltAndWrapsTheHash()
    {
        var saltHex = "00112233445566778899aabbccddeeff";
        var sKey = InternxtCrypto.EncryptText(saltHex, InternxtCrypto.AppCryptoSecret);

        var wrapped = InternxtCrypto.EncryptPasswordHash("pa55word", sKey)!;

        Assert.Equal(InternxtCrypto.PassToHash("pa55word", saltHex), InternxtCrypto.DecryptText(wrapped, InternxtCrypto.AppCryptoSecret));
        Assert.Null(InternxtCrypto.EncryptPasswordHash("pa55word", "deadbeef"));
    }

    [Fact]
    public void FileKeyIsTheSha512ChainOverSeedBucketAndIndex()
    {
        var mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        var bucket = "0123456789abcdef01234567";
        var index = Bytes(32, 1);

        var seed = InternxtCrypto.MnemonicToSeed(mnemonic);
        var bucketKey = SHA512.HashData(seed.Concat(Hex(bucket)).ToArray());
        var expected = SHA512.HashData(bucketKey.Take(32).Concat(index).ToArray()).Take(32).ToArray();

        Assert.Equal(expected, InternxtCrypto.GenerateFileKey(mnemonic, bucket, index));
        Assert.Null(InternxtCrypto.GenerateFileKey(mnemonic, "not-hex", index));
    }

    [Fact]
    public void ShardHashIsRipemd160OfSha256()
    {
        var ciphertext = Bytes(100, 2);
        var sha = SHA256.HashData(ciphertext);
        Assert.Equal(ToHex(InternxtCrypto.Ripemd160(sha)), InternxtCrypto.ShardHashHex(sha));
    }

    [Theory]
    [InlineData("report.pdf", "report", "pdf")]
    [InlineData("archive.tar.gz", "archive.tar", "gz")]
    [InlineData("README", "README", "")]
    [InlineData(".bashrc", ".bashrc", "")]
    [InlineData("trailing.", "trailing.", "")]
    public void ExtensionsSplitLikeNSString(string name, string baseName, string extension)
        => Assert.Equal((baseName, extension), InternxtApiClient.SplitExtension(name));
}

/// <summary>A scripted Internxt account with the Drive and Network endpoints the client uses.</summary>
internal sealed class InternxtTestServer
{
    public const string Email = "user@example.com";
    public const string ServerEmail = "User@Example.com";
    public const string Password = "pa55word";
    public const string SaltHex = "00112233445566778899aabbccddeeff";
    public const string Mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    public const string Bucket = "a1b2c3d4e5f60718293a4b5c";
    public const string Token = "JWT-TOKEN";
    public const string BridgeUser = "bridge@example.com";
    public const string UserId = "uid-123";
    public const string Root = "root-uuid";

    public InternxtTestServer()
    {
        ReportIndex = Bytes(32, 30);
        ReportContent = Bytes(70_000, 31);
        var key = InternxtCrypto.GenerateFileKey(Mnemonic, Bucket, ReportIndex)!;
        ReportCipher = MegaCryptoTests.NaiveCtr(key, ReportIndex[..16], ReportContent);
    }

    public bool TwoFactor { get; set; }

    public string? RequiredCode { get; set; }

    public bool ListDocs { get; set; } = true;

    public byte[] ReportIndex { get; }

    public byte[] ReportContent { get; }

    public byte[] ReportCipher { get; }

    public ConcurrentQueue<(HttpMethod Method, string Path, string? Body)> Calls { get; } = new();

    public InternxtCredentials Credentials() => new(Email, Token, Mnemonic, Bucket, Root, BridgeUser, UserId);

    public (HttpMethod Method, string Path, string? Body)[] CallsTo(HttpMethod method, string pathFragment)
        => Calls.Where(c => c.Method == method && c.Path.Contains(pathFragment, StringComparison.Ordinal)).ToArray();

    public FakeHttpHandler CreateHandler()
    {
        var fake = new FakeHttpHandler();
        fake.On(HttpMethod.Post, "/drive/auth/login/access", (_, body) => Record(HttpMethod.Post, "/auth/login/access", body, Access(JsonNode.Parse(body!)!.AsObject())));
        fake.On(HttpMethod.Post, "/drive/auth/login", (_, body) => Record(HttpMethod.Post, "/auth/login", body, FakeHttpHandler.Json(
            $"{{\"sKey\":\"{InternxtCrypto.EncryptText(SaltHex, InternxtCrypto.AppCryptoSecret)}\",\"tfa\":{(TwoFactor ? "true" : "false")},\"hasKeys\":true}}")));
        fake.On(HttpMethod.Get, "/drive/folders/content/", (request, _) => Record(HttpMethod.Get, request.RequestUri!.PathAndQuery, null, Content(request.RequestUri!)));
        fake.On(HttpMethod.Get, "/network/buckets/", (request, _) => Record(HttpMethod.Get, request.RequestUri!.AbsolutePath, null, FakeHttpHandler.Json(
            $"{{\"bucket\":\"{Bucket}\",\"index\":\"{ToHex(ReportIndex)}\",\"size\":{ReportContent.Length},\"shards\":[{{\"index\":1,\"url\":\"https://shards.example/s1\"}},{{\"index\":0,\"url\":\"https://shards.example/s0\"}}]}}")));
        fake.On(HttpMethod.Get, "shards.example/s0", (_, _) => Binary(ReportCipher[..40_000]));
        fake.On(HttpMethod.Get, "shards.example/s1", (_, _) => Binary(ReportCipher[40_000..]));
        fake.On(HttpMethod.Get, "/drive/users/usage", (_, _) => FakeHttpHandler.Json("{\"drive\":5000,\"backups\":0}"));
        fake.On(HttpMethod.Get, "/drive/users/limit", (_, _) => FakeHttpHandler.Json("{\"maxSpaceBytes\":\"10737418240\"}"));
        return fake;
    }

    /// <summary>Default answers for the mutating endpoints; tests add their own routes first when they need failures.</summary>
    public void AddMutationRoutes(FakeHttpHandler fake)
    {
        fake.On(HttpMethod.Post, $"/network/v2/buckets/{Bucket}/files/start", (_, body) => Record(HttpMethod.Post, "/files/start", body,
            FakeHttpHandler.Json("{\"uploads\":[{\"index\":0,\"uuid\":\"up-1\",\"url\":\"https://s3.example/put/1\",\"urls\":null}]}")));
        fake.On(HttpMethod.Put, "s3.example/put/1", (_, _) => Record(HttpMethod.Put, "/put/1", null, new HttpResponseMessage(HttpStatusCode.OK)));
        fake.On(HttpMethod.Post, $"/network/v2/buckets/{Bucket}/files/finish", (_, body) => Record(HttpMethod.Post, "/files/finish", body, FakeHttpHandler.Json("{\"id\":\"net-file-new\",\"bucket\":\"x\"}")));
        fake.On(HttpMethod.Post, "/drive/files", (_, body) => Record(HttpMethod.Post, "/files", body, FakeHttpHandler.Json("{\"uuid\":\"new-file-uuid\"}")));
        fake.On(HttpMethod.Post, "/drive/folders", (_, body) => Record(HttpMethod.Post, "/folders", body, FakeHttpHandler.Json("{\"uuid\":\"new-folder-uuid\"}")));
        fake.On(HttpMethod.Delete, "/drive/", (request, _) => Record(HttpMethod.Delete, request.RequestUri!.AbsolutePath, null, new HttpResponseMessage(HttpStatusCode.OK)));
        fake.On(HttpMethod.Put, "/drive/", (request, body) => Record(HttpMethod.Put, request.RequestUri!.AbsolutePath, body, FakeHttpHandler.Json("{}")));
        fake.On(HttpMethod.Patch, "/drive/", (request, body) => Record(HttpMethod.Patch, request.RequestUri!.AbsolutePath, body, FakeHttpHandler.Json("{}")));
    }

    public HttpResponseMessage Record(HttpMethod method, string path, string? body, HttpResponseMessage response)
    {
        Calls.Enqueue((method, path, body));
        return response;
    }

    private HttpResponseMessage Access(JsonObject body)
    {
        var expectedHash = InternxtCrypto.PassToHash(Password, SaltHex);
        if (InternxtCrypto.DecryptText((string)body["password"]!, InternxtCrypto.AppCryptoSecret) != expectedHash)
        {
            return Status(HttpStatusCode.Unauthorized, "{\"error\":\"Wrong login credentials\"}");
        }

        if (RequiredCode is not null && (string?)body["tfa"] != RequiredCode)
        {
            return Status(HttpStatusCode.Unauthorized, "{\"error\":\"Wrong 2-factor auth code\"}");
        }

        var response = new JsonObject
        {
            ["token"] = "legacy",
            ["newToken"] = Token,
            ["user"] = new JsonObject
            {
                ["email"] = ServerEmail,
                ["mnemonic"] = InternxtCrypto.EncryptText(Mnemonic, Password),
                ["bucket"] = Bucket,
                ["bridgeUser"] = BridgeUser,
                ["userId"] = UserId,
                ["rootFolderId"] = Root,
                ["name"] = "U",
            },
        };
        return FakeHttpHandler.Json(response.ToJsonString());
    }

    private HttpResponseMessage Content(Uri uri)
    {
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries); // drive, folders, content, {uuid}, folders|files
        var uuid = parts[3];
        var kind = parts[4];
        var offset = int.Parse(System.Web.HttpUtility.ParseQueryString(uri.Query)["offset"]!, System.Globalization.CultureInfo.InvariantCulture);
        if (kind == "folders")
        {
            var folders = uuid switch
            {
                Root => (ListDocs ? new[] { Folder("docs-uuid", "Docs") } : []).Append(Folder("many-uuid", "Many")).ToList(),
                "many-uuid" => Enumerable.Range(0, 60).Select(i => Folder($"m{i}", $"Sub {i:00}")).ToList(),
                _ => [],
            };
            return FakeHttpHandler.Json(new JsonObject { ["folders"] = new JsonArray(folders.Skip(offset).Take(50).Select(f => (JsonNode)f).ToArray()) }.ToJsonString());
        }

        var files = uuid switch
        {
            Root => new List<JsonObject>
            {
                new()
                {
                    ["uuid"] = "report-file-uuid", ["fileId"] = "net-file-1", ["bucket"] = Bucket, ["plainName"] = "report", ["name"] = "encrypted",
                    ["type"] = "pdf", ["size"] = ReportContent.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), ["updatedAt"] = "2025-03-04T05:06:07.890Z",
                },
                new() { ["uuid"] = "readme-uuid", ["fileId"] = "net-file-2", ["bucket"] = Bucket, ["plainName"] = "README", ["type"] = null, ["size"] = 12, ["updatedAt"] = "2025-03-04T05:06:07Z" },
            },
            _ => [],
        };
        return FakeHttpHandler.Json(new JsonObject { ["files"] = new JsonArray(files.Skip(offset).Take(50).Select(f => (JsonNode)f).ToArray()) }.ToJsonString());
    }

    private static JsonObject Folder(string uuid, string name) => new() { ["uuid"] = uuid, ["plainName"] = name, ["name"] = "enc", ["updatedAt"] = "2024-01-02T03:04:05.000Z" };
}

internal sealed class InternxtHarness : IDisposable
{
    public InternxtHarness(bool mutations = true)
    {
        Server = new InternxtTestServer();
        Fake = Server.CreateHandler();
        if (mutations)
        {
            Server.AddMutationRoutes(Fake);
        }

        Capture = new RawCaptureHandler(Fake);
        Store = new MemoryCredentialStore();
        var id = Guid.NewGuid();
        Store.Save($"internxt.{id}", Server.Credentials());
        Provider = new InternxtProvider(id, Store, Capture);
        Provider.Client!.RetryDelayUnit = TimeSpan.Zero;
    }

    public InternxtTestServer Server { get; }

    public FakeHttpHandler Fake { get; }

    public RawCaptureHandler Capture { get; }

    public MemoryCredentialStore Store { get; }

    public InternxtProvider Provider { get; }

    public void Dispose()
    {
        Provider.Dispose();
        Capture.Dispose();
    }
}

public class InternxtProviderTests
{
    private static ConnectValues SignIn(string email, string password)
        => new("default", new Dictionary<string, string> { ["email"] = email, ["password"] = password });

    /// <summary>A harness whose routes for <paramref name="path"/> answer from <paramref name="respond"/> before the defaults.</summary>
    private static InternxtHarness WithRoute(HttpMethod method, string path, Func<int, HttpResponseMessage?> respond)
    {
        var h = new InternxtHarness(mutations: false);
        var calls = 0;
        h.Fake.On(method, path, (request, body) =>
        {
            var response = respond(Interlocked.Increment(ref calls));
            return response is null
                ? h.Server.Record(method, request.RequestUri!.AbsolutePath, body, new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(method == HttpMethod.Post ? "{\"uuid\":\"new-file-uuid\"}" : "{}") })
                : h.Server.Record(method, request.RequestUri!.AbsolutePath, body, response);
        });
        h.Server.AddMutationRoutes(h.Fake);
        return h;
    }

    [Fact]
    public async Task ConnectHashesThePasswordAndDecryptsTheMnemonic()
    {
        var server = new InternxtTestServer();
        var store = new MemoryCredentialStore();
        var id = Guid.NewGuid();
        var capture = new RawCaptureHandler(server.CreateHandler());
        using var provider = new InternxtProvider(id, store, capture);

        var result = await provider.ConnectAsync(SignIn(InternxtTestServer.Email, InternxtTestServer.Password), new ScriptedConnectPrompt());

        Assert.Equal(InternxtTestServer.ServerEmail, result.AccountLabel);
        var stored = store.Load<InternxtCredentials>($"internxt.{id}")!;
        Assert.Equal(InternxtTestServer.Mnemonic, stored.Mnemonic);
        Assert.Equal(InternxtTestServer.Token, stored.Token);
        Assert.Equal(InternxtTestServer.Bucket, stored.Bucket);
        Assert.Equal(InternxtTestServer.Root, stored.RootFolderUuid);
        Assert.Equal(InternxtTestServer.BridgeUser, stored.BridgeUser);
        Assert.Equal(InternxtTestServer.UserId, stored.UserId);

        var access = JsonNode.Parse(server.CallsTo(HttpMethod.Post, "/auth/login/access").Single().Body!)!.AsObject();
        Assert.Equal(InternxtTestServer.Email, (string?)access["email"]);
        Assert.False(access.ContainsKey("tfa"));
        var request = capture.Matching(HttpMethod.Post, "/auth/login/access").Single();
        Assert.Equal("drive-web", request.Headers["internxt-client"]);
        Assert.Equal("1.0.0", request.Headers["internxt-version"]);
        Assert.Equal("application/json", request.Headers["Content-Type"]);
    }

    [Fact]
    public async Task TwoFactorAccountsAreAskedForTheCode()
    {
        var server = new InternxtTestServer { TwoFactor = true, RequiredCode = "445566" };
        using var provider = new InternxtProvider(Guid.NewGuid(), new MemoryCredentialStore(), server.CreateHandler());
        var prompt = new ScriptedConnectPrompt();
        prompt.Codes.Enqueue("445566");

        await provider.ConnectAsync(SignIn(InternxtTestServer.Email, InternxtTestServer.Password), prompt);

        var access = JsonNode.Parse(server.CallsTo(HttpMethod.Post, "/auth/login/access").Single().Body!)!.AsObject();
        Assert.Equal("445566", (string?)access["tfa"]);
        Assert.Equal(2, server.CallsTo(HttpMethod.Post, "/auth/login").Count(c => c.Path == "/auth/login"));
        Assert.True(provider.IsAuthenticated);
    }

    [Fact]
    public async Task WrongPasswordIsInvalidCredentialsAndKeepsStoredOnes()
    {
        using var h = new InternxtHarness();
        var key = h.Store.Keys.Single();
        var before = h.Store.Load(key);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.ConnectAsync(SignIn(InternxtTestServer.Email, "wrong"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal(before, h.Store.Load(key));
    }

    [Fact]
    public async Task ListingCombinesFoldersAndFilesWithExtensions()
    {
        using var h = new InternxtHarness();

        var items = await h.Provider.ListDirectoryAsync("/");

        Assert.Equal(new[] { "Docs", "Many", "report.pdf", "README" }, items.Select(i => i.Name));
        Assert.Equal("ddocs-uuid", items[0].Id);
        Assert.True(items[0].IsDirectory);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), items[0].ModificationDate);
        var report = items[2];
        Assert.Equal("freport-file-uuid", report.Id);
        Assert.Equal("/report.pdf", report.Path);
        Assert.Equal(h.Server.ReportContent.Length, report.Size);
        Assert.Equal(new DateTimeOffset(2025, 3, 4, 5, 6, 7, 890, TimeSpan.Zero), report.ModificationDate);
        Assert.Equal(12, items[3].Size);

        var request = h.Capture.Matching(HttpMethod.Get, "/folders/content/root-uuid/folders/").Single();
        Assert.Equal("Bearer " + InternxtTestServer.Token, request.Headers["Authorization"]);
        Assert.Contains("offset=0&limit=50", request.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListingFollowsPages()
    {
        using var h = new InternxtHarness();

        var items = await h.Provider.ListDirectoryAsync("/Many");

        Assert.Equal(60, items.Count);
        Assert.Equal("/Many/Sub 59", items[^1].Path);
        Assert.Equal(2, h.Capture.Matching(HttpMethod.Get, "/folders/content/many-uuid/folders/").Count());
    }

    [Fact]
    public async Task ACompletedListingRetiresVanishedEntries()
    {
        using var h = new InternxtHarness();
        await h.Provider.ListDirectoryAsync("/Docs");

        h.Server.ListDocs = false;
        await h.Provider.ListDirectoryAsync("/");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.ListDirectoryAsync("/Docs"));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task DownloadDecryptsTheShardsInOrder()
    {
        using var h = new InternxtHarness();
        using var temp = new TempDirectory();
        var target = temp.Combine("report.pdf");
        long reported = 0;

        await h.Provider.DownloadFileAsync("/report.pdf", target, delta => reported += delta);

        Assert.Equal(h.Server.ReportContent, File.ReadAllBytes(target));
        Assert.Equal(h.Server.ReportContent.Length, reported);

        var info = h.Capture.Matching(HttpMethod.Get, $"/network/buckets/{InternxtTestServer.Bucket}/files/net-file-1/info").Single();
        var password = ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(InternxtTestServer.UserId)));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{InternxtTestServer.BridgeUser}:{password}")), info.Headers["Authorization"]);
        Assert.Equal("2", info.Headers["x-api-version"]);
        Assert.Equal(new[] { "https://shards.example/s0", "https://shards.example/s1" }, h.Capture.Matching(HttpMethod.Get, "shards.example").Select(r => r.Uri.ToString()));
    }

    [Fact]
    public async Task UploadEncryptsHashesFinishesAndReplacesTheOldEntry()
    {
        using var h = new InternxtHarness();
        using var temp = new TempDirectory();
        var content = Bytes(200_000, 40);
        var local = temp.File("report.pdf", content);
        File.SetLastWriteTimeUtc(local, new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        long reported = 0;

        await h.Provider.UploadFileAsync(local, "/report.pdf", delta => reported += delta);

        Assert.Equal(content.Length, reported);
        var start = JsonNode.Parse(h.Server.CallsTo(HttpMethod.Post, "/files/start").Single().Body!)!;
        Assert.Equal(content.Length, (long)start["uploads"]![0]!["size"]!);
        Assert.Equal(0, (int)start["uploads"]![0]!["index"]!);

        // The PUT body is the AES-256-CTR ciphertext under the key derived from the finish index.
        var put = h.Capture.Matching(HttpMethod.Put, "s3.example/put/1").Single();
        var finish = JsonNode.Parse(h.Server.CallsTo(HttpMethod.Post, "/files/finish").Single().Body!)!;
        var index = Hex((string)finish["index"]!);
        Assert.Equal(32, index.Length);
        var key = InternxtCrypto.GenerateFileKey(InternxtTestServer.Mnemonic, InternxtTestServer.Bucket, index)!;
        Assert.Equal(content, MegaCryptoTests.NaiveCtr(key, index[..16], put.Body));
        var shard = finish["shards"]![0]!;
        Assert.Equal("up-1", (string?)shard["uuid"]);
        Assert.Equal(ToHex(InternxtCrypto.Ripemd160(SHA256.HashData(put.Body))), (string?)shard["hash"]);

        // The old Drive entry goes before the new one is registered (Drive answers 409 otherwise).
        var calls = h.Server.Calls.ToList();
        var deleteAt = calls.FindIndex(c => c.Method == HttpMethod.Delete && c.Path == "/drive/files/report-file-uuid");
        var registerAt = calls.FindIndex(c => c.Method == HttpMethod.Post && c.Path == "/files");
        Assert.True(deleteAt >= 0 && deleteAt < registerAt);

        var entry = JsonNode.Parse(calls[registerAt].Body!)!.AsObject();
        Assert.Equal("report", (string?)entry["plainName"]);
        Assert.Equal("pdf", (string?)entry["type"]);
        Assert.Equal(content.Length, (long)entry["size"]!);
        Assert.Equal(InternxtTestServer.Root, (string?)entry["folderUuid"]);
        Assert.Equal("net-file-new", (string?)entry["fileId"]);
        Assert.Equal(InternxtTestServer.Bucket, (string?)entry["bucket"]);
        Assert.Equal("03-aes", (string?)entry["encryptVersion"]);
        Assert.Equal("2025-01-02T03:04:05Z", (string?)entry["modificationTime"]);
    }

    [Fact]
    public async Task TransientPutFailuresAreRetriedWithoutCountingBytesTwice()
    {
        using var h = WithRoute(HttpMethod.Put, "s3.example/put/1", call => call == 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null);
        using var temp = new TempDirectory();
        long reported = 0;

        await h.Provider.UploadFileAsync(temp.File("new.bin", Bytes(5000, 41)), "/new.bin", delta => reported += delta);

        Assert.Equal(5000, reported);
        Assert.Equal(2, h.Capture.Matching(HttpMethod.Put, "s3.example/put/1").Count());
    }

    [Fact]
    public async Task RegisteringTheUploadIsRetriedThroughAGatewayHiccup()
    {
        using var h = WithRoute(HttpMethod.Post, "/drive/files", call => call == 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null);
        using var temp = new TempDirectory();

        await h.Provider.UploadFileAsync(temp.File("new.bin", Bytes(10, 42)), "/new.bin");

        Assert.Equal(2, h.Capture.Matching(HttpMethod.Post, "/drive/files").Count());
        Assert.Single(h.Capture.Matching(HttpMethod.Put, "s3.example/put/1"));
    }

    [Fact]
    public async Task ANameConflictIsResolvedAgainstTheLiveListing()
    {
        using var h = WithRoute(HttpMethod.Post, "/drive/files", call => call == 1 ? new HttpResponseMessage(HttpStatusCode.Conflict) : null);
        using var temp = new TempDirectory();

        // Uploading to a path the cache doesn't know: the 409 re-lists the folder, finds the
        // live entry and deletes it before creating again.
        await h.Provider.UploadFileAsync(temp.File("README", Bytes(10, 43)), "/README");

        var calls = h.Server.Calls.ToList();
        Assert.Contains(calls, c => c.Method == HttpMethod.Delete && c.Path == "/drive/files/readme-uuid");
        Assert.Equal(2, h.Capture.Matching(HttpMethod.Post, "/drive/files").Count());
    }

    [Fact]
    public async Task DeleteRetriesAndTreatsAVanishedItemAsDoneOnlyAfterAHiccup()
    {
        using (var h = WithRoute(HttpMethod.Delete, "/drive/files/", call => call == 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : new HttpResponseMessage(HttpStatusCode.NotFound)))
        {
            await h.Provider.DeleteItemAsync("/report.pdf");
            Assert.Equal(2, h.Capture.Matching(HttpMethod.Delete, "/drive/files/report-file-uuid").Count());
        }

        using (var h = WithRoute(HttpMethod.Delete, "/drive/files/", _ => new HttpResponseMessage(HttpStatusCode.NotFound)))
        {
            var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.DeleteItemAsync("/report.pdf"));
            Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        }
    }

    [Fact]
    public async Task DeletingAFolderForgetsItsSubtree()
    {
        using var h = new InternxtHarness();
        await h.Provider.ListDirectoryAsync("/Many");
        int RootListings() => h.Capture.Matching(HttpMethod.Get, "/folders/content/root-uuid/folders/").Count();
        var before = RootListings();

        await h.Provider.DeleteItemAsync("/Many");

        Assert.Single(h.Server.CallsTo(HttpMethod.Delete, "/drive/folders/many-uuid"));
        Assert.Equal(before, RootListings()); // resolved from the cache

        // Afterwards "/Many/Sub 01" must be looked up on the server again, not answered from
        // a cache entry for a folder that no longer exists.
        await h.Provider.CreateDirectoryAsync("/Many/Sub 01");
        Assert.True(RootListings() > before);
    }

    [Fact]
    public async Task RenameSendsTheBareNameForFilesAndTheFullNameForFolders()
    {
        using var h = new InternxtHarness();

        await h.Provider.RenameItemAsync("/report.pdf", "summary.pdf");
        await h.Provider.RenameItemAsync("/Docs", "Papers.2025");

        var file = h.Server.CallsTo(HttpMethod.Put, "/drive/files/report-file-uuid/meta").Single();
        Assert.Equal("{\"plainName\":\"summary\"}", file.Body);
        var folder = h.Server.CallsTo(HttpMethod.Put, "/drive/folders/docs-uuid/meta").Single();
        Assert.Equal("{\"plainName\":\"Papers.2025\"}", folder.Body);
    }

    [Fact]
    public async Task MoveIsAPatchToTheDestinationFolder()
    {
        using var h = new InternxtHarness();

        await h.Provider.MoveItemAsync("/report.pdf", "/Docs/report.pdf");

        var patch = h.Server.CallsTo(HttpMethod.Patch, "/drive/files/report-file-uuid").Single();
        Assert.Equal("{\"destinationFolder\":\"docs-uuid\"}", patch.Body);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.MoveItemAsync("/README", "/Docs/README.md"));
        Assert.Equal(CloudErrorKind.NotImplemented, error.Kind);
    }

    [Fact]
    public async Task CreateFolderIsIdempotent()
    {
        using var h = new InternxtHarness();

        await h.Provider.CreateDirectoryAsync("/Docs");
        Assert.Empty(h.Server.CallsTo(HttpMethod.Post, "/folders"));

        await h.Provider.CreateDirectoryAsync("/Docs/Sub");
        var create = h.Server.CallsTo(HttpMethod.Post, "/folders").Single();
        Assert.Equal("{\"plainName\":\"Sub\",\"parentFolderUuid\":\"docs-uuid\"}", create.Body);

        // Now cached: a second create is answered locally.
        await h.Provider.CreateDirectoryAsync("/Docs/Sub");
        Assert.Single(h.Server.CallsTo(HttpMethod.Post, "/folders"));
    }

    [Fact]
    public async Task QuotaCombinesUsageAndLimit()
    {
        using var h = new InternxtHarness();

        var quota = await h.Provider.StorageQuotaAsync();

        Assert.Equal(5000, quota!.UsedBytes);
        Assert.Equal(10737418240, quota.TotalBytes);
    }

    [Fact]
    public async Task UnauthorizedListingAsksForReauthentication()
    {
        var store = new MemoryCredentialStore();
        var id = Guid.NewGuid();
        store.Save($"internxt.{id}", new InternxtTestServer().Credentials());
        var fake = new FakeHttpHandler().OnJson(HttpMethod.Get, "/drive/folders/content/", "{\"message\":\"Unauthorized\"}", HttpStatusCode.Unauthorized);
        using var provider = new InternxtProvider(id, store, fake);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));

        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
        Assert.Null(await provider.StorageQuotaAsync());
    }

    [Fact]
    public void RegistryCreatesTheProvider()
    {
        using var provider = CloudProviderRegistry.Create(CloudProviderType.Internxt, Guid.NewGuid(), new MemoryCredentialStore());
        Assert.IsType<InternxtProvider>(provider);
        Assert.Equal(4, provider.ConnectSpec.Modes[0].Fields.Count);
    }
}
