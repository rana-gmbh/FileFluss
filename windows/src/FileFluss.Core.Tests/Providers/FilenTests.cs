// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Filen;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;
using static FileFluss.Core.Tests.Providers.EncryptedProviderTestKit;

namespace FileFluss.Core.Tests.Providers;

public class FilenCryptoTests
{
    [Fact]
    public void ChunkDecryptionMatchesGcmReferenceVectors()
    {
        // McGrew & Viega GCM test cases 13 and 14: AES-256, zero key, zero 96-bit IV.
        var key = new byte[32];
        var nonce = new byte[12];

        var empty = FilenCrypto.DecryptFileChunk(nonce.Concat(Hex("530f8afbc74536b9a963b4f1c4cb738b")).ToArray(), key);
        Assert.Empty(empty);

        var block = FilenCrypto.DecryptFileChunk(nonce.Concat(Hex("cea7403d4d606b6e074ec5d3baf39d18d0d1c8a799996bf0265b98b5d48ab919")).ToArray(), key);
        Assert.Equal(new byte[16], block);
    }

    [Fact]
    public void ChunksRoundTripAndRejectTampering()
    {
        var key = Bytes(32, 1);
        var plain = Bytes(1000, 2);
        var sealedChunk = FilenCrypto.EncryptFileChunk(plain, key);

        Assert.Equal(12 + 1000 + 16, sealedChunk.Length);
        Assert.Equal(plain, FilenCrypto.DecryptFileChunk(sealedChunk, key));

        sealedChunk[20] ^= 1;
        Assert.Equal(CloudErrorKind.InvalidResponse, Assert.Throws<CloudProviderException>(() => FilenCrypto.DecryptFileChunk(sealedChunk, key)).Kind);
    }

    [Fact]
    public void MetadataEnvelopeIs002NonceAndBase64()
    {
        var key = Bytes(32, 3);
        var envelope = FilenCrypto.EncryptMetadata("{\"name\":\"Fotos\"}", key, nonce: "abcdefABCDEF");

        Assert.StartsWith("002abcdefABCDEF", envelope);
        var body = Convert.FromBase64String(envelope[15..]);
        using var gcm = new AesGcm(key, 16);
        var plain = new byte[body.Length - 16];
        gcm.Decrypt("abcdefABCDEF"u8, body.AsSpan(0, plain.Length), body.AsSpan(plain.Length), plain);
        Assert.Equal("{\"name\":\"Fotos\"}", Encoding.UTF8.GetString(plain));

        Assert.Equal("{\"name\":\"Fotos\"}", FilenCrypto.DecryptMetadata(envelope, key));
        Assert.Equal(CloudErrorKind.InvalidResponse, Assert.Throws<CloudProviderException>(() => FilenCrypto.DecryptMetadata(envelope, Bytes(32, 4))).Kind);
        Assert.Equal(CloudErrorKind.InvalidResponse, Assert.Throws<CloudProviderException>(() => FilenCrypto.DecryptMetadata("001abc", key)).Kind);
    }

    [Fact]
    public void RandomNoncesUseTheSdkAlphabet()
    {
        var envelope = FilenCrypto.EncryptMetadata("x", Bytes(32, 5));
        Assert.All(envelope.Substring(3, 12), c => Assert.Contains(c, FilenCrypto.Alphabet));
        Assert.All(FilenCrypto.RandomString(32), c => Assert.Contains(c, FilenCrypto.Alphabet));
    }

    [Fact]
    public void V2LoginSplitsTheDerivedHex()
    {
        // PBKDF2-HMAC-SHA512("password", "salt", 1, 64): the well-known vector, split at hex char 64.
        var derivation = FilenCrypto.DeriveV2Login("password", "salt", iterations: 1);

        Assert.Equal("867f70cf1ade02cff3752599a3a53dc4af34c7a669815ae5d513554e1c8cf252", derivation.MasterKeyHex);
        var secondHalf = "c02d470a285a0501bad999bfe943c08f050235d7d68b1da55e63f73b60a57fce";
        Assert.Equal(ToHex(SHA512.HashData(Encoding.ASCII.GetBytes(secondHalf))), derivation.LoginPasswordHex);
        Assert.Equal(128, derivation.LoginPasswordHex.Length);
    }

    [Fact]
    public void V2KeyIsOneRoundOfPbkdf2WithTheKeyAsSalt()
    {
        var key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var expected = Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(key), 1, HashAlgorithmName.SHA512, 32);
        Assert.Equal(expected, FilenCrypto.AesKeyFromV2MasterKey(key));
    }

    [Fact]
    public void NameHashIsSha1OfTheLowercaseSha512Hex()
    {
        var sha512Hex = ToHex(SHA512.HashData(Encoding.UTF8.GetBytes("report ä.pdf")));
        var expected = ToHex(SHA1.HashData(Encoding.ASCII.GetBytes(sha512Hex)));
        Assert.Equal(expected, FilenCrypto.V2HashName("Report Ä.PDF"));
        Assert.Equal(40, expected.Length);
    }

    [Theory]
    [InlineData(1700000000d, 1700000000L)]
    [InlineData(1700000000123d, 1700000000L)]
    public void TimestampsInSecondsOrMillisecondsAreAccepted(double raw, long expectedSeconds)
        => Assert.Equal(expectedSeconds, FilenApiClient.TimestampToDate(raw).ToUnixTimeSeconds());
}

/// <summary>A scripted Filen account: real v2 derivation, two master keys, encrypted metadata and chunks.</summary>
internal sealed class FilenTestServer
{
    public const string Email = "user@example.com";
    public const string Password = "hunter2-filen";
    public const string Salt = "a-salt-filen-sends-as-text";
    public const string ApiKey = "API-KEY-1";
    public const string Root = "root-uuid";
    public const string OlderMasterKey = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    // The 200 000-round derivation is computed once for the whole test run.
    private static readonly Lazy<FilenCrypto.V2LoginDerivation> SharedDerivation = new(() => FilenCrypto.DeriveV2Login(Password, Salt));

    public FilenTestServer()
    {
        ReportContent = Bytes((2 * 1024 * 1024) + 5000, 10);
    }

    public FilenCrypto.V2LoginDerivation Derivation => SharedDerivation.Value;

    public byte[] DerivedKey => FilenCrypto.AesKeyFromV2MasterKey(Derivation.MasterKeyHex);

    public byte[] OlderKey => FilenCrypto.AesKeyFromV2MasterKey(OlderMasterKey);

    public string ReportKey { get; } = "abcdefghijklmnopqrstuvwxyzABCDEF";

    public byte[] ReportContent { get; }

    public int AuthVersion { get; set; } = 2;

    public string? RequiredCode { get; set; }

    public ConcurrentQueue<(string Path, JsonObject Body)> Calls { get; } = new();

    public FilenCredentials Credentials() => new(Email, ApiKey, [Derivation.MasterKeyHex, OlderMasterKey], Root);

    public JsonObject[] CallsTo(string path) => Calls.Where(c => c.Path == path).Select(c => c.Body).ToArray();

    public FakeHttpHandler CreateHandler()
    {
        var fake = new FakeHttpHandler();
        fake.On(HttpMethod.Get, "gateway.filen.io/v3/user/baseFolder", (request, _) =>
            request.Headers.Authorization?.Parameter == ApiKey ? Envelope("{\"uuid\":\"" + Root + "\"}") : Status(HttpStatusCode.Unauthorized));
        fake.On(HttpMethod.Post, "gateway.filen.io/", (request, body) => Handle(request.RequestUri!.AbsolutePath, JsonNode.Parse(body!)!.AsObject()));
        fake.On(HttpMethod.Get, "egest.filen.io/", (request, _) => Chunk(request));
        fake.On(HttpMethod.Post, "ingest.filen.io/v3/upload", (_, _) => Envelope("{\"bucket\":\"b\",\"region\":\"r\"}"));
        return fake;
    }

    public static HttpResponseMessage Envelope(string data) => FakeHttpHandler.Json($"{{\"status\":true,\"message\":\"ok\",\"code\":\"ok\",\"data\":{data}}}");

    private static HttpResponseMessage Failure(string code, string message)
        => FakeHttpHandler.Json($"{{\"status\":false,\"message\":\"{message}\",\"code\":\"{code}\",\"data\":null}}");

    private HttpResponseMessage Handle(string path, JsonObject body)
    {
        Calls.Enqueue((path, body));
        switch (path)
        {
            case "/v3/auth/info":
                return Envelope($"{{\"email\":\"{Email}\",\"authVersion\":{AuthVersion},\"salt\":\"{Salt}\",\"id\":42}}");
            case "/v3/login":
                if ((string?)body["password"] != Derivation.LoginPasswordHex)
                {
                    return Failure("email_or_password_wrong", "Email address or password wrong.");
                }

                if (RequiredCode is not null && (string?)body["twoFactorCode"] != RequiredCode)
                {
                    return Failure("enter_2fa", "Please enter your Two Factor Authentication code.");
                }

                var blob = FilenCrypto.EncryptMetadata($"{Derivation.MasterKeyHex}|{OlderMasterKey}", DerivedKey);
                return Envelope($"{{\"apiKey\":\"{ApiKey}\",\"masterKeys\":\"{blob}\",\"publicKey\":\"pk\",\"privateKey\":\"sk\"}}");
            case "/v3/dir/content":
                return (string?)body["uuid"] switch
                {
                    Root => Envelope(RootContent()),
                    "photos-uuid" => Envelope("{\"uploads\":[],\"folders\":[]}"),
                    _ => Status(HttpStatusCode.NotFound),
                };
            case "/v3/user/info":
                return Envelope("{\"storage\":123456,\"maxStorage\":10737418240,\"email\":\"" + Email + "\"}");
            default:
                return Envelope("{}");
        }
    }

    private string RootContent()
    {
        var photos = FilenCrypto.EncryptMetadata("{\"name\":\"Photos\",\"creation\":1700000000000}", DerivedKey);
        var old = FilenCrypto.EncryptMetadata("{\"name\":\"Old\"}", OlderKey);
        var report = FilenCrypto.EncryptMetadata(
            $"{{\"name\":\"report.pdf\",\"size\":{ReportContent.Length},\"mime\":\"application/pdf\",\"key\":\"{ReportKey}\",\"lastModified\":1700000500}}",
            DerivedKey);
        var empty = FilenCrypto.EncryptMetadata("{\"name\":\"empty.txt\",\"size\":0,\"mime\":\"text/plain\",\"key\":\"ZYXWVUTSRQPONMLKJIHGFEDCBAzyxwvu\",\"lastModified\":1700000600000}", DerivedKey);
        return $$"""
            {
              "folders": [
                {"uuid":"photos-uuid","name":"{{photos}}","parent":"{{Root}}","color":null,"timestamp":1600000000,"favorited":0},
                {"uuid":"old-uuid","name":"{{old}}","parent":"{{Root}}","timestamp":1600000001},
                {"uuid":"broken-uuid","name":"002notreallyencrypted","parent":"{{Root}}","timestamp":1600000002}
              ],
              "uploads": [
                {"uuid":"report-uuid","metadata":"{{report}}","rm":"x","timestamp":1600000003,"chunks":3,"size":0,"bucket":"filen-1","region":"de-1","parent":"{{Root}}","version":2},
                {"uuid":"empty-uuid","metadata":"{{empty}}","chunks":0,"bucket":"filen-1","region":"de-1","parent":"{{Root}}","version":2}
              ]
            }
            """;
    }

    private HttpResponseMessage Chunk(HttpRequestMessage request)
    {
        if (request.Headers.Authorization?.Parameter != ApiKey)
        {
            return Status(HttpStatusCode.Unauthorized);
        }

        var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
        if (segments is not ["de-1", "filen-1", "report-uuid", var index])
        {
            return Status(HttpStatusCode.NotFound);
        }

        var i = int.Parse(index, System.Globalization.CultureInfo.InvariantCulture);
        var start = i * FilenApiClient.PlaintextChunkSize;
        var length = Math.Min(FilenApiClient.PlaintextChunkSize, ReportContent.Length - start);
        return Binary(FilenCrypto.EncryptFileChunk(ReportContent.AsSpan(start, length), Encoding.ASCII.GetBytes(ReportKey)));
    }
}

internal sealed class FilenHarness : IDisposable
{
    public FilenHarness()
    {
        Server = new FilenTestServer();
        Fake = Server.CreateHandler();
        Capture = new RawCaptureHandler(Fake);
        Store = new MemoryCredentialStore();
        var id = Guid.NewGuid();
        Store.Save($"filen.{id}", Server.Credentials());
        Provider = new FilenProvider(id, Store, Capture);
    }

    public FilenTestServer Server { get; }

    public FakeHttpHandler Fake { get; }

    public RawCaptureHandler Capture { get; }

    public MemoryCredentialStore Store { get; }

    public FilenProvider Provider { get; }

    public void Dispose()
    {
        Provider.Dispose();
        Capture.Dispose();
    }
}

public class FilenProviderTests
{
    private static ConnectValues SignIn(string email, string password, string? code = null)
    {
        var values = new Dictionary<string, string> { ["email"] = email, ["password"] = password };
        if (code is not null)
        {
            values["code"] = code;
        }

        return new ConnectValues("default", values);
    }

    [Fact]
    public async Task ConnectDerivesTheLoginPasswordAndKeepsEveryMasterKey()
    {
        var server = new FilenTestServer();
        var store = new MemoryCredentialStore();
        var id = Guid.NewGuid();
        using var provider = new FilenProvider(id, store, server.CreateHandler());

        var result = await provider.ConnectAsync(SignIn(FilenTestServer.Email, FilenTestServer.Password), new ScriptedConnectPrompt());

        Assert.Equal(FilenTestServer.Email, result.AccountLabel);
        Assert.True(provider.IsAuthenticated);
        var stored = store.Load<FilenCredentials>($"filen.{id}")!;
        Assert.Equal(FilenTestServer.ApiKey, stored.ApiKey);
        Assert.Equal(FilenTestServer.Root, stored.RootFolderUuid);
        Assert.Equal(new[] { server.Derivation.MasterKeyHex, FilenTestServer.OlderMasterKey }, stored.MasterKeys);

        var login = server.CallsTo("/v3/login").Single();
        Assert.Equal(server.Derivation.LoginPasswordHex, (string?)login["password"]);
        Assert.Equal("XXXXXX", (string?)login["twoFactorCode"]);
        Assert.Equal(2, (int)login["authVersion"]!);
        Assert.Equal(FilenTestServer.Email, provider.GetEditableConnectValues()["email"]);
    }

    [Fact]
    public async Task TwoFactorIsAskedForWhenTheServerWantsIt()
    {
        var server = new FilenTestServer { RequiredCode = "112233" };
        using var provider = new FilenProvider(Guid.NewGuid(), new MemoryCredentialStore(), server.CreateHandler());
        var prompt = new ScriptedConnectPrompt();
        prompt.Codes.Enqueue("112233");

        await provider.ConnectAsync(SignIn(FilenTestServer.Email, FilenTestServer.Password), prompt);

        var logins = server.CallsTo("/v3/login");
        Assert.Equal(new[] { "XXXXXX", "112233" }, logins.Select(l => (string?)l["twoFactorCode"]));
        Assert.True(provider.IsAuthenticated);
    }

    [Fact]
    public async Task UnsupportedAuthVersionFailsAndKeepsStoredCredentials()
    {
        using var h = new FilenHarness();
        h.Server.AuthVersion = 3;
        var key = h.Store.Keys.Single();
        var before = h.Store.Load(key);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.ConnectAsync(SignIn(FilenTestServer.Email, FilenTestServer.Password), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Contains("3", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, h.Store.Load(key));
        Assert.Empty(h.Server.CallsTo("/v3/login"));
    }

    [Fact]
    public async Task WrongPasswordSurfacesTheServerMessage()
    {
        using var h = new FilenHarness();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.ConnectAsync(SignIn(FilenTestServer.Email, "nope"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("Email address or password wrong.", error.Message);
    }

    [Fact]
    public async Task ListingDecryptsWithEveryMasterKeyAndSkipsUnreadableItems()
    {
        using var h = new FilenHarness();

        var items = await h.Provider.ListDirectoryAsync("/");

        Assert.Equal(new[] { "Photos", "Old", "report.pdf", "empty.txt" }, items.Select(i => i.Name));
        var photos = items[0];
        Assert.Equal("d:photos-uuid", photos.Id);
        Assert.Equal("/Photos", photos.Path);
        Assert.True(photos.IsDirectory);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), photos.ModificationDate);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1600000001), items[1].ModificationDate);

        var report = items[2];
        Assert.Equal("f:report-uuid", report.Id);
        Assert.Equal(h.Server.ReportContent.Length, report.Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000500), report.ModificationDate);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000600), items[3].ModificationDate);

        var request = h.Capture.Matching(HttpMethod.Post, "/v3/dir/content").First();
        Assert.Equal("Bearer " + FilenTestServer.ApiKey, request.Headers["Authorization"]);
        Assert.Equal("{\"uuid\":\"root-uuid\"}", Encoding.UTF8.GetString(request.Body));
    }

    [Fact]
    public async Task NestedPathsResolveSegmentBySegment()
    {
        using var h = new FilenHarness();

        Assert.Empty(await h.Provider.ListDirectoryAsync("/Photos"));
        Assert.Equal(new[] { "root-uuid", "photos-uuid" }, h.Server.CallsTo("/v3/dir/content").Select(b => (string?)b["uuid"]));

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.ListDirectoryAsync("/Missing/Deeper"));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal("/Missing", error.ItemPath);
    }

    [Fact]
    public async Task DownloadFetchesAndDecryptsEveryChunk()
    {
        using var h = new FilenHarness();
        using var temp = new TempDirectory();
        var target = temp.Combine("report.pdf");
        long reported = 0;

        await h.Provider.DownloadFileAsync("/report.pdf", target, delta => reported += delta);

        Assert.Equal(h.Server.ReportContent, File.ReadAllBytes(target));
        Assert.Equal(h.Server.ReportContent.Length, reported);
        Assert.Equal(
            new[] { "https://egest.filen.io/de-1/filen-1/report-uuid/0", "https://egest.filen.io/de-1/filen-1/report-uuid/1", "https://egest.filen.io/de-1/filen-1/report-uuid/2" },
            h.Capture.Matching(HttpMethod.Get, "egest.filen.io").Select(r => r.Uri.ToString()));
    }

    [Fact]
    public async Task EmptyFilesDownloadWithoutChunks()
    {
        using var h = new FilenHarness();
        using var temp = new TempDirectory();
        var target = temp.Combine("empty.txt");

        await h.Provider.DownloadFileAsync("/empty.txt", target);

        Assert.Empty(File.ReadAllBytes(target));
        Assert.Empty(h.Capture.Matching(HttpMethod.Get, "egest.filen.io"));
    }

    [Fact]
    public async Task DownloadOfAMissingFileIsNotFound()
    {
        using var h = new FilenHarness();
        using var temp = new TempDirectory();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.DownloadFileAsync("/Photos", temp.Combine("x")));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task UploadEncryptsOneMebibyteChunksAndPublishesTheMetadata()
    {
        using var h = new FilenHarness();
        using var temp = new TempDirectory();
        var content = Bytes((2 * 1024 * 1024) + 512 * 1024, 20);
        var local = temp.File("notes.txt", content);
        var modified = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(local, modified);
        long reported = 0;

        await h.Provider.UploadFileAsync(local, "/Photos/notes.txt", delta => reported += delta);

        Assert.Equal(content.Length, reported);
        var done = h.Server.CallsTo("/v3/upload/done").Single();
        Assert.Equal(3, (int)done["chunks"]!);
        Assert.Equal("photos-uuid", (string?)done["parent"]);
        Assert.Equal(2, (int)done["version"]!);
        Assert.Equal(FilenCrypto.V2HashName("notes.txt"), (string?)done["nameHashed"]);

        // The listing metadata is under the newest master key and carries the file key.
        using var metadata = JsonDocument.Parse(FilenCrypto.DecryptMetadata((string)done["metadata"]!, h.Server.DerivedKey));
        var meta = metadata.RootElement;
        Assert.Equal("notes.txt", meta.GetProperty("name").GetString());
        Assert.Equal(content.Length, meta.GetProperty("size").GetInt64());
        Assert.Equal("text/plain", meta.GetProperty("mime").GetString());
        Assert.Equal(new DateTimeOffset(modified).ToUnixTimeMilliseconds(), meta.GetProperty("lastModified").GetInt64());
        var fileKey = meta.GetProperty("key").GetString()!;
        Assert.Equal(32, fileKey.Length);

        // The standalone fields are under the file's own meta key.
        var metaKey = FilenCrypto.AesKeyFromV2MasterKey(fileKey);
        Assert.Equal("notes.txt", FilenCrypto.DecryptMetadata((string)done["name"]!, metaKey));
        Assert.Equal(content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), FilenCrypto.DecryptMetadata((string)done["size"]!, metaKey));
        Assert.Equal("text/plain", FilenCrypto.DecryptMetadata((string)done["mime"]!, metaKey));

        // Chunks: raw ciphertext bodies, index in order, SHA-512 of the body in the URL.
        var chunks = h.Capture.Matching(HttpMethod.Post, "ingest.filen.io/v3/upload").ToList();
        Assert.Equal(3, chunks.Count);
        var plain = new List<byte>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var query = HttpUtility.ParseQueryString(chunks[i].Uri.Query);
            Assert.Equal(i.ToString(System.Globalization.CultureInfo.InvariantCulture), query["index"]);
            Assert.Equal((string?)done["uuid"], query["uuid"]);
            Assert.Equal("photos-uuid", query["parent"]);
            Assert.Equal((string?)done["uploadKey"], query["uploadKey"]);
            Assert.Equal(ToHex(SHA512.HashData(chunks[i].Body)), query["hash"]);
            Assert.Equal("Bearer " + FilenTestServer.ApiKey, chunks[i].Headers["Authorization"]);
            plain.AddRange(FilenCrypto.DecryptFileChunk(chunks[i].Body, Encoding.ASCII.GetBytes(fileKey)));
        }

        Assert.Equal(content, plain.ToArray());
        Assert.Equal(1024 * 1024 + 28, chunks[0].Body.Length);
    }

    [Fact]
    public async Task EmptyUploadsUseTheEmptyEndpoint()
    {
        using var h = new FilenHarness();
        using var temp = new TempDirectory();

        await h.Provider.UploadFileAsync(temp.File("e.txt", []), "/e.txt");

        var empty = h.Server.CallsTo("/v3/upload/empty").Single();
        Assert.Equal(FilenTestServer.Root, (string?)empty["parent"]);
        Assert.Empty(h.Server.CallsTo("/v3/upload/done"));
        Assert.Empty(h.Capture.Matching(HttpMethod.Post, "ingest.filen.io"));
    }

    [Fact]
    public async Task CreateFolderEncryptsTheNameAndSkipsExistingFolders()
    {
        using var h = new FilenHarness();

        await h.Provider.CreateDirectoryAsync("/Photos");
        Assert.Empty(h.Server.CallsTo("/v3/dir/create"));

        await h.Provider.CreateDirectoryAsync("/New");
        var create = h.Server.CallsTo("/v3/dir/create").Single();
        Assert.Equal(FilenTestServer.Root, (string?)create["parent"]);
        Assert.Equal(FilenCrypto.V2HashName("New"), (string?)create["nameHashed"]);
        Assert.True(Guid.TryParse((string?)create["uuid"], out _));
        using var meta = JsonDocument.Parse(FilenCrypto.DecryptMetadata((string)create["name"]!, h.Server.DerivedKey));
        Assert.Equal("New", meta.RootElement.GetProperty("name").GetString());
        Assert.True(meta.RootElement.GetProperty("creation").GetInt64() > 1_000_000_000_000);
    }

    [Fact]
    public async Task DeleteMovesFilesAndFoldersToTheTrash()
    {
        using var h = new FilenHarness();

        await h.Provider.DeleteItemAsync("/report.pdf");
        await h.Provider.DeleteItemAsync("/Photos");

        Assert.Equal("report-uuid", (string?)h.Server.CallsTo("/v3/file/trash").Single()["uuid"]);
        Assert.Equal("photos-uuid", (string?)h.Server.CallsTo("/v3/dir/trash").Single()["uuid"]);
    }

    [Fact]
    public async Task RenameRewritesFileMetadataAndKeepsTheMimeType()
    {
        using var h = new FilenHarness();

        await h.Provider.RenameItemAsync("/report.pdf", "final.pdf");

        var call = h.Server.CallsTo("/v3/file/metadata").Single();
        Assert.Equal("report-uuid", (string?)call["uuid"]);
        Assert.Equal(FilenCrypto.V2HashName("final.pdf"), (string?)call["nameHashed"]);
        Assert.Equal("final.pdf", FilenCrypto.DecryptMetadata((string)call["name"]!, FilenCrypto.AesKeyFromV2MasterKey(h.Server.ReportKey)));
        using var meta = JsonDocument.Parse(FilenCrypto.DecryptMetadata((string)call["metadata"]!, h.Server.DerivedKey));
        Assert.Equal("final.pdf", meta.RootElement.GetProperty("name").GetString());
        Assert.Equal("application/pdf", meta.RootElement.GetProperty("mime").GetString());
        Assert.Equal(h.Server.ReportKey, meta.RootElement.GetProperty("key").GetString());
        Assert.Equal(h.Server.ReportContent.Length, meta.RootElement.GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task RenameRewritesFolderMetadata()
    {
        using var h = new FilenHarness();

        await h.Provider.RenameItemAsync("/Photos", "Pictures");

        var call = h.Server.CallsTo("/v3/dir/metadata").Single();
        Assert.Equal("photos-uuid", (string?)call["uuid"]);
        using var meta = JsonDocument.Parse(FilenCrypto.DecryptMetadata((string)call["name"]!, h.Server.DerivedKey));
        Assert.Equal("Pictures", meta.RootElement.GetProperty("name").GetString());
        Assert.Equal(1700000000000, meta.RootElement.GetProperty("creation").GetInt64());
    }

    [Fact]
    public async Task MetadataOfFilesAndTheRoot()
    {
        using var h = new FilenHarness();

        var report = await h.Provider.GetFileMetadataAsync("/report.pdf");
        Assert.Equal(h.Server.ReportContent.Length, report.Size);
        Assert.True((await h.Provider.GetFileMetadataAsync("/")).IsDirectory);
        Assert.Equal(CloudErrorKind.NotFound, (await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.GetFileMetadataAsync("/nope"))).Kind);
    }

    [Fact]
    public async Task QuotaReadsStorageAndMaximum()
    {
        using var h = new FilenHarness();

        var quota = await h.Provider.StorageQuotaAsync();

        Assert.Equal(123456, quota!.UsedBytes);
        Assert.Equal(10737418240, quota.TotalBytes);
    }

    [Fact]
    public async Task HttpErrorsAreMapped()
    {
        using var h = new FilenHarness();
        var fake = new FakeHttpHandler().OnJson(HttpMethod.Post, "/v3/dir/content", "{}", HttpStatusCode.Unauthorized);
        var id = Guid.NewGuid();
        var store = new MemoryCredentialStore();
        store.Save($"filen.{id}", h.Server.Credentials());
        using var provider = new FilenProvider(id, store, fake);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);

        var failing = new FakeHttpHandler().OnJson(HttpMethod.Post, "/v3/dir/content", "{\"status\":false,\"message\":\"Folder not found.\",\"code\":\"folder_not_found\"}");
        using var other = new FilenProvider(id, store, failing);
        var message = await Assert.ThrowsAsync<CloudProviderException>(() => other.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.CommandFailed, message.Kind);
        Assert.Equal("Folder not found.", message.Message);
    }

    [Fact]
    public void RegistryCreatesTheProvider()
    {
        using var provider = CloudProviderRegistry.Create(CloudProviderType.Filen, Guid.NewGuid(), new MemoryCredentialStore());
        Assert.IsType<FilenProvider>(provider);
        Assert.False(provider.IsAuthenticated);
    }
}
