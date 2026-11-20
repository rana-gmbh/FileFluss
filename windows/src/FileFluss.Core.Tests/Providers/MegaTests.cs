// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Mega;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;
using static FileFluss.Core.Tests.Providers.EncryptedProviderTestKit;

namespace FileFluss.Core.Tests.Providers;

public class MegaCryptoTests
{
    [Fact]
    public void CtrMatchesNistSp80038aAes128Vectors()
    {
        // NIST SP 800-38A F.5.1 (CTR-AES128.Encrypt); the counter's low byte rolls over after block 1.
        var key = Hex("2b7e151628aed2a6abf7158809cf4f3c");
        var counter = Hex("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var data = Hex("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710");
        var expected = "874d6191b620e3261bef6864990db6ce9806f66b7970fdff8617187bb9fffdff5ae4df3edbd5d35e5b4f09020db03eab1e031dda2fbe03d1792170a0f3009cee";

        using var ctr = new MegaAesCtr(key, counter);
        ctr.Transform(data);

        Assert.Equal(expected, ToHex(data));
    }

    [Fact]
    public void CtrIsStreamableInAnySlicing()
    {
        var key = Bytes(16, 10);
        var counter = new byte[16];
        Bytes(8, 11).CopyTo(counter, 0);
        var plain = Bytes(300_001, 12);

        var oneShot = (byte[])plain.Clone();
        using (var ctr = new MegaAesCtr(key, counter))
        {
            ctr.Transform(oneShot);
        }

        var sliced = (byte[])plain.Clone();
        using (var ctr = new MegaAesCtr(key, counter))
        {
            var random = new Random(13);
            for (var offset = 0; offset < sliced.Length;)
            {
                var length = Math.Min(random.Next(1, 70_000), sliced.Length - offset);
                ctr.Transform(sliced.AsSpan(offset, length));
                offset += length;
            }
        }

        Assert.Equal(oneShot, sliced);
        Assert.Equal(NaiveCtr(key, counter, plain), oneShot);
    }

    [Fact]
    public void MacChunksGrowBy128KiBUpTo1MiB()
    {
        var chunks = MegaMac.Chunks(10L * 1024 * 1024).ToList();
        var expectedStarts = new long[] { 0, 128, 384, 768, 1280, 1920, 2688, 3584, 4608, 5632 }.Select(k => k * 1024);
        Assert.Equal(expectedStarts, chunks.Take(10).Select(c => c.Offset));
        Assert.Equal(1024 * 1024, chunks[8].Length);
        Assert.Equal(10L * 1024 * 1024, chunks.Sum(c => (long)c.Length));
        Assert.Empty(MegaMac.Chunks(0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(131_072)]
    [InlineData(131_073)]
    [InlineData(2_500_017)]
    [InlineData(5_242_881)]
    public void MacMatchesAPerBlockReferenceInAnySlicing(int size)
    {
        var key = Bytes(16, 20);
        var nonce = Bytes(8, 21);
        var data = Bytes(size, 22);

        using var mac = new MegaMac(key, nonce);
        var random = new Random(23);
        for (var offset = 0; offset < data.Length;)
        {
            var length = Math.Min(random.Next(1, 200_000), data.Length - offset);
            mac.Update(data.AsSpan(offset, length));
            offset += length;
        }

        Assert.Equal(ToHex(NaiveMetaMac(key, nonce, data)), ToHex(mac.FinishMetaMac()));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("a password longer than sixteen bytes")]
    [InlineData("pässwörd")]
    public void PrepareKeyRunsAllRoundsOverEveryPasswordBlock(string password)
    {
        // Reference, straight from the web client's prepare_key: 65 536 passes, each encrypting
        // the seed with every 16-byte (zero-filled) password block in turn.
        var bytes = Encoding.UTF8.GetBytes(password);
        var keys = Enumerable.Range(0, (bytes.Length + 15) / 16).Select(b =>
        {
            var k = new byte[16];
            bytes.AsSpan(b * 16, Math.Min(16, bytes.Length - (b * 16))).CopyTo(k);
            return k;
        }).ToList();
        var aes = keys.Select(k =>
        {
            var a = Aes.Create();
            a.Key = k;
            return a;
        }).ToList();
        var expected = Hex("93C467E37DB0C7A4D1BE3F810152CB56");
        for (var round = 0; round < 65536; round++)
        {
            foreach (var a in aes)
            {
                expected = a.EncryptEcb(expected, PaddingMode.None);
            }
        }

        aes.ForEach(a => a.Dispose());
        Assert.Equal(ToHex(expected), ToHex(MegaCrypto.PrepareKeyV1(password)));
    }

    [Fact]
    public void StringHashFoldsBigEndianWordsAndEncrypts16384Times()
    {
        var key = Bytes(16, 30);
        var email = "Some.User@Example.COM";

        // Reference: str_to_a32(lowercased email), h32[i & 3] ^= word[i], 16 384 AES rounds, words 0 and 2.
        var bytes = Encoding.UTF8.GetBytes(email.ToLowerInvariant());
        var padded = new byte[(bytes.Length + 3) / 4 * 4];
        bytes.CopyTo(padded, 0);
        var h = new uint[4];
        for (var i = 0; i < padded.Length / 4; i++)
        {
            h[i & 3] ^= BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(i * 4));
        }

        var block = MegaCrypto.ToBytes(h);
        using var aes = Aes.Create();
        aes.Key = key;
        for (var i = 0; i < 16384; i++)
        {
            block = aes.EncryptEcb(block, PaddingMode.None);
        }

        var expected = MegaCrypto.Base64UrlEncode(block.AsSpan(0, 4).ToArray().Concat(block.AsSpan(8, 4).ToArray()).ToArray());
        Assert.Equal(expected, MegaCrypto.StringHash(email, key));
    }

    [Fact]
    public void V2DerivationIsPbkdf2Sha512()
    {
        // PBKDF2-HMAC-SHA512("password", "salt", 1): the first 32 bytes of the well-known 64-byte vector.
        var derived = MegaCrypto.DeriveV2("password", "salt"u8.ToArray(), iterations: 1);
        Assert.Equal("867f70cf1ade02cff3752599a3a53dc4af34c7a669815ae5d513554e1c8cf252", ToHex(derived));
    }

    [Fact]
    public void AttributesRoundTripAndKeepUnicodeAndQuotes()
    {
        var key = Bytes(16, 40);
        var name = "Bericht \"final\" — Größe\\2026.pdf";
        var encrypted = MegaCrypto.EncryptAttributes(name, key);

        Assert.DoesNotContain('=', encrypted);
        Assert.Equal(name, MegaCrypto.DecryptName(encrypted, key));
        Assert.Null(MegaCrypto.DecryptName(encrypted, Bytes(16, 41)));

        // The plaintext layout is "MEGA" + JSON, zero-padded to the block size, CBC with a zero IV.
        using var aes = Aes.Create();
        aes.Key = key;
        var plain = aes.DecryptCbc(MegaCrypto.Base64UrlDecode(encrypted)!, new byte[16], PaddingMode.None);
        Assert.StartsWith("MEGA{\"n\":", Encoding.UTF8.GetString(plain));
        Assert.Equal(0, plain.Length % 16);
    }

    [Fact]
    public void FileKeyFoldsNonceAndMetaMacIntoTheContentKey()
    {
        var contentKey = Bytes(16, 50);
        var nonce = Bytes(8, 51);
        var metaMac = Bytes(8, 52);
        var composed = MegaCrypto.ComposeFileKey(contentKey, nonce, metaMac);

        Assert.Equal(contentKey, MegaCrypto.AttributeKey(composed));
        Assert.Equal(nonce, MegaCrypto.ToBytes(composed.AsSpan(4, 2)));
        Assert.Equal(metaMac, MegaCrypto.ToBytes(composed.AsSpan(6, 2)));
    }

    [Fact]
    public void RsaDecryptsAnMpiEncodedSessionId()
    {
        // Textbook key p=61, q=53 (n=3233), d=2753: 2790^d mod n = 65.
        var privateKey = MegaCrypto.ReadMpiSequence(Mpi([61]).Concat(Mpi([53])).Concat(Mpi([0x0A, 0xC1])).Concat(Mpi([38])).ToArray(), 4);
        var cipher = MegaCrypto.ReadMpi(Mpi([0x0A, 0xE6]), out var read);

        Assert.Equal(4, read);
        Assert.Equal(new byte[] { 65 }, MegaCrypto.RsaDecrypt(cipher!, privateKey));
    }

    [Fact]
    public void TruncatedMpiSequenceIsAnInvalidResponse()
    {
        var error = Assert.Throws<CloudProviderException>(() => MegaCrypto.ReadMpiSequence([0x00, 0x10, 0x01], 1));
        Assert.Equal(CloudErrorKind.InvalidResponse, error.Kind);
    }

    [Theory]
    [InlineData(0, 8u)]
    [InlineData(63, 127u << 3)]
    [InlineData(192, 1u << 24)]
    [InlineData(255, 127u << 24)]
    public void HashcashThresholdFollowsTheEasinessByte(int easiness, uint expected)
        => Assert.Equal(expected, MegaCrypto.HashcashThreshold((byte)easiness));

    [Fact]
    public void HashcashSolutionSatisfiesTheChallenge()
    {
        var token = MegaCrypto.Base64UrlEncode(Bytes(48, 60));
        var solution = MegaCrypto.SolveHashcash($"1:255:1700000000:{token}", CancellationToken.None, workerCount: 2);

        var parts = solution.Split(':');
        Assert.Equal("1", parts[0]);
        Assert.Equal(token, parts[1]);
        Assert.True(HashcashHolds(token, parts[2], 255));
    }

    [Fact]
    public void Base64UrlRoundTripsWithoutPadding()
    {
        var data = Bytes(31, 70);
        var encoded = MegaCrypto.Base64UrlEncode(data);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
        Assert.Equal(data, MegaCrypto.Base64UrlDecode(encoded));
        Assert.Null(MegaCrypto.Base64UrlDecode("a"));
    }

    internal static bool HashcashHolds(string token, string nonce, byte easiness)
    {
        var tokenData = MegaCrypto.Base64UrlDecode(token)!;
        var buffer = new byte[4 + (262_144 * 48)];
        MegaCrypto.Base64UrlDecode(nonce)!.CopyTo(buffer, 0);
        for (var i = 0; i < 262_144; i++)
        {
            tokenData.CopyTo(buffer, 4 + (i * 48));
        }

        return BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(buffer)) <= MegaCrypto.HashcashThreshold(easiness);
    }

    internal static byte[] Mpi(byte[] value)
    {
        var trimmed = value.SkipWhile(b => b == 0).ToArray();
        var bits = trimmed.Length == 0 ? 0 : ((trimmed.Length - 1) * 8) + (8 - BitOperations.LeadingZeroCount((uint)trimmed[0]) + 24);
        return new[] { (byte)(bits >> 8), (byte)bits }.Concat(trimmed).ToArray();
    }

    /// <summary>Reference CTR: keystream block i = AES(key, counter + i), one block at a time.</summary>
    internal static byte[] NaiveCtr(byte[] key, byte[] counter, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var output = new byte[data.Length];
        var block = (byte[])counter.Clone();
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var keystream = aes.EncryptEcb(block, PaddingMode.None);
            for (var i = 0; i < 16 && offset + i < data.Length; i++)
            {
                output[offset + i] = (byte)(data[offset + i] ^ keystream[i]);
            }

            var value = new BigInteger(block, isUnsigned: true, isBigEndian: true) + 1;
            var next = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            block = new byte[16];
            next.AsSpan(Math.Max(0, next.Length - 16)).CopyTo(block.AsSpan(Math.Max(0, 16 - next.Length)));
        }

        return output;
    }

    /// <summary>Reference MEGA MAC, written straight from the web client's description.</summary>
    internal static byte[] NaiveMetaMac(byte[] key, byte[] nonce, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var fileMac = new byte[16];
        long position = 0;
        for (var index = 0; position < data.Length; index++)
        {
            var size = index < 8 ? (index + 1) * 131_072 : 1_048_576;
            var length = (int)Math.Min(size, data.Length - position);
            var mac = nonce.Concat(nonce).ToArray();
            for (var offset = 0; offset < length; offset += 16)
            {
                var block = new byte[16];
                System.Array.Copy(data, position + offset, block, 0, Math.Min(16, length - offset));
                for (var i = 0; i < 16; i++)
                {
                    block[i] ^= mac[i];
                }

                mac = aes.EncryptEcb(block, PaddingMode.None);
            }

            for (var i = 0; i < 16; i++)
            {
                fileMac[i] ^= mac[i];
            }

            fileMac = aes.EncryptEcb(fileMac, PaddingMode.None);
            position += length;
        }

        var meta = new byte[8];
        for (var i = 0; i < 4; i++)
        {
            meta[i] = (byte)(fileMac[i] ^ fileMac[i + 4]);
            meta[i + 4] = (byte)(fileMac[i + 8] ^ fileMac[i + 12]);
        }

        return meta;
    }
}

/// <summary>
/// A scripted MEGA account: real v2 key derivation, an RSA-encrypted session id, and a node
/// tree whose keys and names are encrypted exactly as MEGA stores them.
/// </summary>
internal sealed class MegaTestServer
{
    public const string Root = "ROOTHNDL";
    public const string DocsHandle = "DOCSHNDL";
    public const string FileAHandle = "FILEAHND";
    public const string LegacyHandle = "LEGACYHN";
    public const string Owner = "USERHNDL";

    public MegaTestServer()
    {
        Sid[0] = 0x42; // keep the RSA plaintext's leading byte non-zero
        FileAContent = Bytes(400_000, 80);
        FileANonce = Bytes(8, 81);
        FileAKey = Bytes(16, 82);
        FileAMac = MegaCryptoTests.NaiveMetaMac(FileAKey, FileANonce, FileAContent);
        LegacyContent = Bytes(1000, 83);
        LegacyKey = Bytes(16, 84);
        LegacyNonce = Bytes(8, 85);
    }

    public string Email { get; } = "user@example.com";

    public string Password { get; } = "correct horse battery staple";

    public byte[] MasterKey { get; } = Bytes(16, 1);

    public byte[] Salt { get; } = Bytes(16, 2);

    public byte[] Sid { get; } = Bytes(43, 3);

    public byte[] DocsKey { get; } = Bytes(16, 4);

    public byte[] FileAContent { get; }

    public byte[] FileANonce { get; }

    public byte[] FileAKey { get; }

    public byte[] FileAMac { get; set; }

    public byte[] LegacyContent { get; }

    public byte[] LegacyKey { get; }

    public byte[] LegacyNonce { get; }

    public ConcurrentQueue<JsonObject> Commands { get; } = new();

    /// <summary>Per-command overrides: return a response body, or null to fall through to the default.</summary>
    public ConcurrentDictionary<string, Func<JsonObject, HttpResponseMessage?>> Overrides { get; } = new();

    public string? RequiredMfa { get; set; }

    /// <summary>1 for a legacy account (prepare_key + stringhash, answered with a temporary session id), 2 for PBKDF2.</summary>
    public int Version { get; set; } = 2;

    /// <summary>The 32-byte temporary session id of a v1 login: t ‖ AES(master, t).</summary>
    public string Tsid
    {
        get
        {
            var t = Bytes(16, 6);
            return MegaCrypto.Base64UrlEncode(t.Concat(MegaCrypto.EncryptEcb(MasterKey, t)).ToArray());
        }
    }

    public string ExpectedSessionId => MegaCrypto.Base64UrlEncode(Sid);

    /// <summary>When set, the first API request is answered with a 402 hashcash challenge for this token.</summary>
    public string? HashcashToken { get; set; }

    public int HashcashSolutionsAccepted { get; private set; }

    public FakeHttpHandler CreateHandler()
    {
        var fake = new FakeHttpHandler();
        var challenged = false;
        fake.On(HttpMethod.Post, "g.api.mega.co.nz/cs", (request, body) =>
        {
            if (HashcashToken is { } token)
            {
                if (!challenged)
                {
                    challenged = true;
                    var challenge = new HttpResponseMessage((HttpStatusCode)402);
                    challenge.Headers.TryAddWithoutValidation("X-Hashcash", $"1:255:1700000000:{token}");
                    return challenge;
                }

                if (request.Headers.TryGetValues("X-Hashcash", out var values))
                {
                    var parts = values.Single().Split(':');
                    if (parts.Length != 3 || parts[1] != token || !MegaCryptoTests.HashcashHolds(token, parts[2], 255))
                    {
                        return new HttpResponseMessage(HttpStatusCode.BadRequest);
                    }

                    HashcashSolutionsAccepted++;
                }
            }

            return Handle(body!);
        });
        fake.On(HttpMethod.Get, "dl.example/file-a", (_, _) => Binary(Encrypt(FileAContent, FileAKey, FileANonce)));
        fake.On(HttpMethod.Get, "dl.example/legacy", (_, _) => Binary(Encrypt(LegacyContent, LegacyKey, LegacyNonce)));
        return fake;
    }

    public JsonObject[] CommandsNamed(string name) => Commands.Where(c => (string?)c["a"] == name).ToArray();

    public static byte[] Encrypt(byte[] plain, byte[] key, byte[] nonce)
    {
        var counter = new byte[16];
        nonce.CopyTo(counter, 0);
        return MegaCryptoTests.NaiveCtr(key, counter, plain);
    }

    public MegaCredentials Credentials() => new(ExpectedSessionId, MegaCrypto.ToWords(MasterKey), Email);

    public string NodeKeyField(uint[] nodeKey) => $"{Owner}:{MegaCrypto.Base64UrlEncode(MegaCrypto.EncryptEcb(MasterKey, MegaCrypto.ToBytes(nodeKey)))}";

    private HttpResponseMessage Handle(string body)
    {
        var command = JsonNode.Parse(body)!.AsArray()[0]!.AsObject();
        Commands.Enqueue(command);
        var name = (string)command["a"]!;
        if (Overrides.TryGetValue(name, out var handler) && handler(command) is { } overridden)
        {
            return overridden;
        }

        return name switch
        {
            "us0" => FakeHttpHandler.Json(Version == 1 ? "[{\"v\":1}]" : $"[{{\"v\":2,\"s\":\"{MegaCrypto.Base64UrlEncode(Salt)}\"}}]"),
            "us" => Login(command),
            "f" => FakeHttpHandler.Json(Tree()),
            "g" => FakeHttpHandler.Json((string)command["n"]! == FileAHandle
                ? $"[{{\"s\":{FileAContent.Length},\"g\":\"http://dl.example/file-a\"}}]"
                : $"[{{\"s\":{LegacyContent.Length},\"g\":\"https://dl.example/legacy\"}}]"),
            "uq" => FakeHttpHandler.Json("[{\"cstrg\":1234567,\"mstrg\":21474836480}]"),
            _ => FakeHttpHandler.Json("[0]"),
        };
    }

    private HttpResponseMessage Login(JsonObject command)
    {
        if (Version == 1)
        {
            var legacyKey = MegaCrypto.PrepareKeyV1(Password);
            if ((string?)command["uh"] != MegaCrypto.StringHash(Email, legacyKey))
            {
                return FakeHttpHandler.Json("-9");
            }

            var encryptedMaster = MegaCrypto.Base64UrlEncode(MegaCrypto.EncryptEcb(legacyKey, MasterKey));
            return FakeHttpHandler.Json($"[{{\"k\":\"{encryptedMaster}\",\"tsid\":\"{Tsid}\"}}]");
        }

        var derived = MegaCrypto.DeriveV2(Password, Salt);
        if ((string?)command["uh"] != MegaCrypto.Base64UrlEncode(derived.AsSpan(16, 16)))
        {
            return FakeHttpHandler.Json("-9");
        }

        if (RequiredMfa is not null && (string?)command["mfa"] != RequiredMfa)
        {
            return FakeHttpHandler.Json("-26");
        }

        using var rsa = RSA.Create(1024);
        var key = rsa.ExportParameters(true);
        var privatePlain = MegaCryptoTests.Mpi(key.P!).Concat(MegaCryptoTests.Mpi(key.Q!)).Concat(MegaCryptoTests.Mpi(key.D!)).Concat(MegaCryptoTests.Mpi(key.InverseQ!)).ToArray();
        System.Array.Resize(ref privatePlain, (privatePlain.Length + 15) / 16 * 16);
        var privk = MegaCrypto.Base64UrlEncode(MegaCrypto.EncryptEcb(MasterKey, privatePlain));

        var plainSid = new BigInteger(Sid.Concat(Bytes(20, 5)).ToArray(), isUnsigned: true, isBigEndian: true);
        var modulus = new BigInteger(key.Modulus, isUnsigned: true, isBigEndian: true);
        var exponent = new BigInteger(key.Exponent, isUnsigned: true, isBigEndian: true);
        var csid = MegaCrypto.Base64UrlEncode(MegaCryptoTests.Mpi(BigInteger.ModPow(plainSid, exponent, modulus).ToByteArray(isUnsigned: true, isBigEndian: true)));

        var passwordKey = derived[..16];
        var k = MegaCrypto.Base64UrlEncode(MegaCrypto.EncryptEcb(passwordKey, MasterKey));
        return FakeHttpHandler.Json($"[{{\"k\":\"{k}\",\"csid\":\"{csid}\",\"privk\":\"{privk}\",\"u\":\"{Owner}\"}}]");
    }

    private string Tree()
    {
        var fileAKey = MegaCrypto.ComposeFileKey(FileAKey, FileANonce, FileAMac);
        var legacyKey = MegaCrypto.ComposeFileKey(LegacyKey, LegacyNonce, new byte[8]); // macOS uploads: MAC left zero
        var trashedKey = MegaCrypto.ComposeFileKey(Bytes(16, 86), Bytes(8, 87), new byte[8]);
        var nodes = new JsonArray(
            new JsonObject { ["h"] = Root, ["p"] = string.Empty, ["t"] = 2, ["ts"] = 1600000000 },
            new JsonObject { ["h"] = "INBOXHND", ["p"] = string.Empty, ["t"] = 3, ["ts"] = 1600000000 },
            new JsonObject { ["h"] = "TRASHHND", ["p"] = string.Empty, ["t"] = 4, ["ts"] = 1600000000 },
            new JsonObject
            {
                ["h"] = DocsHandle, ["p"] = Root, ["t"] = 1, ["ts"] = 1700000000,
                ["a"] = MegaCrypto.EncryptAttributes("Docs", DocsKey), ["k"] = NodeKeyField(MegaCrypto.ToWords(DocsKey)),
            },
            new JsonObject
            {
                ["h"] = FileAHandle, ["p"] = Root, ["t"] = 0, ["s"] = FileAContent.Length, ["ts"] = 1700000100,
                ["a"] = MegaCrypto.EncryptAttributes("a.txt", FileAKey), ["k"] = NodeKeyField(fileAKey),
            },
            new JsonObject
            {
                ["h"] = LegacyHandle, ["p"] = DocsHandle, ["t"] = 0, ["s"] = LegacyContent.Length, ["ts"] = 1700000200,
                ["a"] = MegaCrypto.EncryptAttributes("legacy report.bin", LegacyKey), ["k"] = NodeKeyField(legacyKey),
            },
            new JsonObject
            {
                ["h"] = "TRASHEDF", ["p"] = "TRASHHND", ["t"] = 0, ["s"] = 5, ["ts"] = 1700000300,
                ["a"] = MegaCrypto.EncryptAttributes("a.txt.old", Bytes(16, 86)), ["k"] = NodeKeyField(trashedKey),
            },
            new JsonObject
            {
                ["h"] = "FOREIGN1", ["p"] = Root, ["t"] = 0, ["s"] = 5, ["ts"] = 1700000400,
                ["a"] = MegaCrypto.EncryptAttributes("not for us", Bytes(16, 88)), ["k"] = "OTHERUSR:" + MegaCrypto.Base64UrlEncode(Bytes(32, 89)),
            });
        return new JsonArray(new JsonObject { ["f"] = nodes }).ToJsonString();
    }
}

/// <summary>A provider signed in to a <see cref="MegaTestServer"/>, with every request body captured.</summary>
internal sealed class MegaHarness : IDisposable
{
    public MegaHarness(MemoryCredentialStore? store = null)
    {
        Server = new MegaTestServer();
        Fake = Server.CreateHandler();
        Capture = new RawCaptureHandler(Fake);
        Store = store ?? new MemoryCredentialStore();
        var id = Guid.NewGuid();
        Store.Save($"mega.{id}", Server.Credentials());
        Provider = new MegaProvider(id, Store, Capture);
        Provider.Client!.RetryDelayUnit = TimeSpan.Zero;
    }

    public MegaTestServer Server { get; }

    public FakeHttpHandler Fake { get; }

    public RawCaptureHandler Capture { get; }

    public MemoryCredentialStore Store { get; }

    public MegaProvider Provider { get; }

    public void Dispose()
    {
        Provider.Dispose();
        Capture.Dispose();
    }
}

public class MegaProviderTests
{
    private static ConnectValues SignIn(string email, string password)
        => new("default", new Dictionary<string, string> { ["email"] = email, ["password"] = password });

    [Fact]
    public async Task ConnectDerivesV2KeysAndDecryptsTheRsaSessionId()
    {
        var server = new MegaTestServer();
        var store = new MemoryCredentialStore();
        var id = Guid.NewGuid();
        using var provider = new MegaProvider(id, store, server.CreateHandler());
        Assert.False(provider.IsAuthenticated);

        var result = await provider.ConnectAsync(SignIn($" {server.Email} ", server.Password), new ScriptedConnectPrompt());

        Assert.Equal(server.Email, result.AccountLabel);
        Assert.True(provider.IsAuthenticated);
        var stored = store.Load<MegaCredentials>($"mega.{id}")!;
        Assert.Equal(server.ExpectedSessionId, stored.SessionId);
        Assert.Equal(MegaCrypto.ToWords(server.MasterKey), stored.MasterKey);
        Assert.Equal(server.Email, provider.GetEditableConnectValues()["email"]);

        var login = server.CommandsNamed("us").Single();
        Assert.Equal(server.Email, (string?)login["user"]);
        Assert.True(login.ContainsKey("sek"));
        Assert.Null(login["sek"]);
        Assert.False(login.ContainsKey("mfa"));
    }

    [Fact]
    public async Task LegacyAccountsSignInWithPrepareKeyAndATemporarySession()
    {
        var server = new MegaTestServer { Version = 1 };
        var store = new MemoryCredentialStore();
        var id = Guid.NewGuid();
        using var provider = new MegaProvider(id, store, server.CreateHandler());

        await provider.ConnectAsync(SignIn(server.Email, server.Password), new ScriptedConnectPrompt());

        var login = server.CommandsNamed("us").Single();
        Assert.False(login.ContainsKey("sek"));
        var stored = store.Load<MegaCredentials>($"mega.{id}")!;
        Assert.Equal(server.Tsid, stored.SessionId);
        Assert.Equal(MegaCrypto.ToWords(server.MasterKey), stored.MasterKey);
    }

    [Fact]
    public async Task ATemporarySessionThatDoesNotVerifyIsRejected()
    {
        var server = new MegaTestServer { Version = 1 };
        server.Overrides["us"] = _ =>
        {
            var key = MegaCrypto.Base64UrlEncode(MegaCrypto.EncryptEcb(MegaCrypto.PrepareKeyV1(server.Password), server.MasterKey));
            return FakeHttpHandler.Json($"[{{\"k\":\"{key}\",\"tsid\":\"{MegaCrypto.Base64UrlEncode(Bytes(32, 7))}\"}}]");
        };
        using var provider = new MegaProvider(Guid.NewGuid(), new MemoryCredentialStore(), server.CreateHandler());

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(SignIn(server.Email, server.Password), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.False(provider.IsAuthenticated);
    }

    [Fact]
    public async Task TwoFactorIsAskedForWithoutRetypingThePassword()
    {
        var server = new MegaTestServer { RequiredMfa = "123456" };
        using var provider = new MegaProvider(Guid.NewGuid(), new MemoryCredentialStore(), server.CreateHandler());
        var prompt = new ScriptedConnectPrompt();
        prompt.Codes.Enqueue(" 123456 ");

        await provider.ConnectAsync(SignIn(server.Email, server.Password), prompt);

        var logins = server.CommandsNamed("us");
        Assert.Equal(2, logins.Length);
        Assert.False(logins[0].ContainsKey("mfa"));
        Assert.Equal("123456", (string?)logins[1]["mfa"]);
        Assert.True(provider.IsAuthenticated);
    }

    [Fact]
    public async Task CodeFromTheFormIsSentWithTheFirstLogin()
    {
        var server = new MegaTestServer { RequiredMfa = "654321" };
        using var provider = new MegaProvider(Guid.NewGuid(), new MemoryCredentialStore(), server.CreateHandler());
        var values = new ConnectValues("default", new Dictionary<string, string>
        {
            ["email"] = server.Email,
            ["password"] = server.Password,
            ["code"] = "654321",
        });

        await provider.ConnectAsync(values, new ScriptedConnectPrompt());

        Assert.Equal("654321", (string?)server.CommandsNamed("us").Single()["mfa"]);
    }

    [Fact]
    public async Task CancelledTwoFactorPromptFailsWithTwoFactorRequired()
    {
        var server = new MegaTestServer { RequiredMfa = "123456" };
        using var provider = new MegaProvider(Guid.NewGuid(), new MemoryCredentialStore(), server.CreateHandler());

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(SignIn(server.Email, server.Password), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.TwoFactorRequired, error.Kind);
        Assert.False(provider.IsAuthenticated);
    }

    [Fact]
    public async Task WrongPasswordLeavesStoredCredentialsUntouched()
    {
        using var h = new MegaHarness();
        var key = h.Store.Keys.Single();
        var before = h.Store.Load(key);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.ConnectAsync(SignIn(h.Server.Email, "wrong"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.InvalidCredentials, error.Kind);
        Assert.Equal(before, h.Store.Load(key));
        Assert.True(h.Provider.IsAuthenticated);
    }

    [Fact]
    public async Task HashcashChallengeIsSolvedAndTheRequestRepeated()
    {
        var server = new MegaTestServer { HashcashToken = MegaCrypto.Base64UrlEncode(Bytes(48, 90)) };
        using var provider = new MegaProvider(Guid.NewGuid(), new MemoryCredentialStore(), server.CreateHandler());
        var prompt = new ScriptedConnectPrompt();

        await provider.ConnectAsync(SignIn(server.Email, server.Password), prompt);

        Assert.True(provider.IsAuthenticated);
        Assert.Equal(1, server.HashcashSolutionsAccepted);
        Assert.Single(server.CommandsNamed("us0"));
        Assert.Contains(prompt.StatusLines, s => s is not null);
        Assert.Null(prompt.StatusLines[^1]);
    }

    [Fact]
    public async Task ListingDecryptsNamesSortsFoldersFirstAndHidesSpecialNodes()
    {
        using var h = new MegaHarness();

        var root = await h.Provider.ListDirectoryAsync("/");

        Assert.Equal(new[] { "Docs", "a.txt", "Unknown" }, root.Select(i => i.Name));
        var docs = root[0];
        Assert.True(docs.IsDirectory);
        Assert.Equal("/Docs", docs.Path);
        Assert.Equal(MegaTestServer.DocsHandle, docs.Id);
        var file = root[1];
        Assert.False(file.IsDirectory);
        Assert.Equal(400_000, file.Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000100), file.ModificationDate);

        var inDocs = Assert.Single(await h.Provider.ListDirectoryAsync("/Docs/"));
        Assert.Equal("/Docs/legacy report.bin", inDocs.Path);
    }

    [Fact]
    public async Task MissingPathsAreNotFound()
    {
        using var h = new MegaHarness();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.ListDirectoryAsync("/Nope"));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Equal("/Nope", error.ItemPath);
    }

    [Fact]
    public async Task ExpiredSessionMapsToUnauthorized()
    {
        using var h = new MegaHarness();
        h.Server.Overrides["f"] = _ => FakeHttpHandler.Json("-15");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.Unauthorized, error.Kind);
        Assert.True(error.NeedsReauthentication);
    }

    [Fact]
    public async Task EagainIsRetried()
    {
        using var h = new MegaHarness();
        var calls = 0;
        h.Server.Overrides["f"] = _ => Interlocked.Increment(ref calls) < 3 ? FakeHttpHandler.Json("-3") : null;

        var items = await h.Provider.ListDirectoryAsync("/");

        Assert.Equal(3, calls);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task ConcurrentFirstCallsFetchTheTreeOnce()
    {
        using var h = new MegaHarness();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.Provider.ListDirectoryAsync("/")));

        Assert.Single(h.Server.CommandsNamed("f"));
    }

    [Fact]
    public async Task DownloadDecryptsAndVerifiesTheMac()
    {
        using var h = new MegaHarness();
        using var temp = new TempDirectory();
        var target = temp.Combine("a.txt");
        long reported = 0;

        await h.Provider.DownloadFileAsync("/a.txt", target, delta => Interlocked.Add(ref reported, delta));

        Assert.Equal(h.Server.FileAContent, File.ReadAllBytes(target));
        Assert.Equal(h.Server.FileAContent.Length, reported);
        Assert.Equal(MegaTestServer.FileAHandle, (string?)h.Server.CommandsNamed("g").Single()["n"]);

        // http:// transfer URLs are upgraded, as on macOS.
        Assert.Contains(h.Capture.Requests, r => r.Uri.ToString() == "https://dl.example/file-a");
    }

    [Fact]
    public async Task DownloadWithAWrongMacLeavesNoFileBehind()
    {
        using var h = new MegaHarness();
        h.Server.FileAMac = Bytes(8, 99);
        using var temp = new TempDirectory();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.DownloadFileAsync("/a.txt", temp.Combine("a.txt")));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task FilesWithAZeroMacStillDownload()
    {
        // FileFluss for macOS never computed a MAC; its uploads must stay readable.
        using var h = new MegaHarness();
        using var temp = new TempDirectory();
        var target = temp.Combine("legacy.bin");

        await h.Provider.DownloadFileAsync("/Docs/legacy report.bin", target);

        Assert.Equal(h.Server.LegacyContent, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task DownloadOfAFolderIsNotFound()
    {
        using var h = new MegaHarness();
        using var temp = new TempDirectory();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.DownloadFileAsync("/Docs", temp.Combine("x")));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task UploadPostsMacChunksAndCreatesTheNodeWithMacInItsKey()
    {
        using var h = new MegaHarness();
        h.Server.Overrides["u"] = _ => FakeHttpHandler.Json("[{\"p\":\"http://ul.example/up1\"}]");
        h.Server.Overrides["p"] = command => FakeHttpHandler.Json(
            $"[{{\"f\":[{{\"h\":\"NEWFILEH\",\"p\":\"{MegaTestServer.Root}\",\"t\":0,\"a\":\"{(string)command["n"]![0]!["a"]!}\",\"k\":\"x\"}}]}}]");
        h.Fake.On(HttpMethod.Post, "ul.example/up1/", (request, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/131072", StringComparison.Ordinal) ? "COMPLETEHANDLE" : string.Empty),
        });

        var content = Bytes(300_000, 100);
        using var temp = new TempDirectory();
        var local = temp.File("a.txt", content);
        long reported = 0;

        await h.Provider.UploadFileAsync(local, "/a.txt", delta => Interlocked.Add(ref reported, delta));

        Assert.Equal(content.Length, reported);
        Assert.Equal(content.Length, (long)h.Server.CommandsNamed("u").Single()["s"]!);

        // Two MAC chunks (128 KiB, then the rest), posted to {url}/{offset} over https.
        var chunks = h.Capture.Matching(HttpMethod.Post, "ul.example/up1/").ToList();
        Assert.Equal(new[] { "https://ul.example/up1/0", "https://ul.example/up1/131072" }, chunks.Select(c => c.Uri.ToString()));
        Assert.Equal(new[] { 131_072, 168_928 }, chunks.Select(c => c.Body.Length));

        // The node key decrypts to [k^n, n, mac]: the content key and nonce decrypt the chunks,
        // and the stored meta-MAC is the real one.
        var put = h.Server.CommandsNamed("p").Single();
        Assert.Equal(MegaTestServer.Root, (string?)put["t"]);
        var node = put["n"]![0]!;
        Assert.Equal("COMPLETEHANDLE", (string?)node["h"]);
        Assert.Equal(0, (int)node["t"]!);
        var nodeKey = MegaCrypto.ToWords(MegaCrypto.DecryptEcb(h.Server.MasterKey, MegaCrypto.Base64UrlDecode((string)node["k"]!)!));
        var contentKey = MegaCrypto.AttributeKey(nodeKey);
        var nonce = MegaCrypto.ToBytes(nodeKey.AsSpan(4, 2));
        var ciphertext = chunks.SelectMany(c => c.Body).ToArray();
        Assert.Equal(content, MegaTestServer.Encrypt(ciphertext, contentKey, nonce));
        Assert.Equal(ToHex(MegaCryptoTests.NaiveMetaMac(contentKey, nonce, content)), ToHex(MegaCrypto.ToBytes(nodeKey.AsSpan(6, 2))));
        Assert.Equal("a.txt", MegaCrypto.DecryptName((string)node["a"]!, contentKey));

        // MEGA keeps both nodes with the same name, so the replaced file is deleted afterwards.
        Assert.Equal(MegaTestServer.FileAHandle, (string?)h.Server.CommandsNamed("d").Single()["n"]);
        var listed = await h.Provider.ListDirectoryAsync("/");
        Assert.Equal("NEWFILEH", listed.Single(i => i.Name == "a.txt").Id);
    }

    [Fact]
    public async Task EmptyFileUploadPostsOnceAtOffsetZero()
    {
        using var h = new MegaHarness();
        h.Server.Overrides["u"] = _ => FakeHttpHandler.Json("[{\"p\":\"https://ul.example/empty\"}]");
        h.Server.Overrides["p"] = _ => FakeHttpHandler.Json("[{\"f\":[{\"h\":\"EMPTYHND\",\"p\":\"DOCSHNDL\",\"t\":0}]}]");
        h.Fake.OnText(HttpMethod.Post, "ul.example/empty/0", "EMPTYTOKEN");
        using var temp = new TempDirectory();

        await h.Provider.UploadFileAsync(temp.File("empty.txt", []), "/Docs/empty.txt");

        var post = Assert.Single(h.Capture.Matching(HttpMethod.Post, "ul.example/empty"));
        Assert.Empty(post.Body);
        var put = h.Server.CommandsNamed("p").Single();
        Assert.Equal("EMPTYTOKEN", (string?)put["n"]![0]!["h"]);
        Assert.Equal(MegaTestServer.DocsHandle, (string?)put["t"]);
    }

    [Fact]
    public async Task UploadChunkErrorCodesAreMapped()
    {
        using var h = new MegaHarness();
        h.Server.Overrides["u"] = _ => FakeHttpHandler.Json("[{\"p\":\"https://ul.example/full\"}]");
        h.Fake.OnText(HttpMethod.Post, "ul.example/full/", "-17");
        using var temp = new TempDirectory();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.UploadFileAsync(temp.File("x.bin", Bytes(10, 1)), "/x.bin"));

        Assert.Equal(CloudErrorKind.QuotaExceeded, error.Kind);
        Assert.Empty(h.Server.CommandsNamed("p"));
    }

    [Fact]
    public async Task TransientChunkFailuresAreRetried()
    {
        using var h = new MegaHarness();
        h.Server.Overrides["u"] = _ => FakeHttpHandler.Json("[{\"p\":\"https://ul.example/flaky\"}]");
        h.Server.Overrides["p"] = _ => FakeHttpHandler.Json("[{\"f\":[{\"h\":\"FLAKYHND\",\"p\":\"ROOTHNDL\",\"t\":0}]}]");
        var attempts = 0;
        h.Fake.On(HttpMethod.Post, "ul.example/flaky/0", (_, _) => Interlocked.Increment(ref attempts) == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("DONE") });
        using var temp = new TempDirectory();
        long reported = 0;

        await h.Provider.UploadFileAsync(temp.File("f.bin", Bytes(1000, 2)), "/f.bin", delta => reported += delta);

        Assert.Equal(2, attempts);
        Assert.Equal(1000, reported);
    }

    [Fact]
    public async Task RenameSendsNewAttributesUnderTheNodeKey()
    {
        using var h = new MegaHarness();

        await h.Provider.RenameItemAsync("/a.txt", "b.txt");

        var command = h.Server.CommandsNamed("a").Single();
        Assert.Equal(MegaTestServer.FileAHandle, (string?)command["n"]);
        Assert.Equal("b.txt", MegaCrypto.DecryptName((string)command["at"]!, h.Server.FileAKey));
        Assert.Contains(await h.Provider.ListDirectoryAsync("/"), i => i.Name == "b.txt");
    }

    [Fact]
    public async Task CreateFolderIsIdempotentAndCachesTheNewKey()
    {
        using var h = new MegaHarness();
        h.Server.Overrides["p"] = command => FakeHttpHandler.Json($"[{{\"f\":[{{\"h\":\"NEWDIRHN\",\"p\":\"{(string)command["t"]!}\",\"t\":1}}]}}]");

        await h.Provider.CreateDirectoryAsync("/Docs");
        Assert.Empty(h.Server.CommandsNamed("p"));

        await h.Provider.CreateDirectoryAsync("/Docs/New");
        var put = h.Server.CommandsNamed("p").Single();
        Assert.Equal(MegaTestServer.DocsHandle, (string?)put["t"]);
        var node = put["n"]![0]!;
        Assert.Equal("xxxxxxxx", (string?)node["h"]);
        Assert.Equal(1, (int)node["t"]!);
        var folderKey = MegaCrypto.DecryptEcb(h.Server.MasterKey, MegaCrypto.Base64UrlDecode((string)node["k"]!)!);
        Assert.Equal("New", MegaCrypto.DecryptName((string)node["a"]!, folderKey));

        // Renaming the folder just made uses its own key, not the master key.
        await h.Provider.RenameItemAsync("/Docs/New", "Newer");
        Assert.Equal("Newer", MegaCrypto.DecryptName((string)h.Server.CommandsNamed("a").Single()["at"]!, folderKey));
    }

    [Fact]
    public async Task DeleteSendsTheHandleAndForgetsTheNode()
    {
        using var h = new MegaHarness();

        await h.Provider.DeleteItemAsync("/Docs");

        Assert.Equal(MegaTestServer.DocsHandle, (string?)h.Server.CommandsNamed("d").Single()["n"]);
        Assert.DoesNotContain(await h.Provider.ListDirectoryAsync("/"), i => i.Name == "Docs");
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => h.Provider.GetFileMetadataAsync("/Docs/legacy report.bin"));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task SearchFolderSizeAndMetadataUseTheCachedTree()
    {
        using var h = new MegaHarness();

        var hits = await h.Provider.SearchFilesAsync("A.TXT", null);
        Assert.Equal(new[] { "/a.txt" }, hits!.Select(i => i.Path)); // the trashed "a.txt.old" is not offered

        var inDocs = await h.Provider.SearchFilesAsync("report", "/Docs");
        Assert.Equal(new[] { "/Docs/legacy report.bin" }, inDocs!.Select(i => i.Path));

        Assert.Equal(1000, await h.Provider.FolderSizeAsync("/Docs"));
        Assert.Equal(401_005, await h.Provider.FolderSizeAsync("/"));

        var meta = await h.Provider.GetFileMetadataAsync("/Docs/legacy report.bin");
        Assert.Equal(1000, meta.Size);
        Assert.True((await h.Provider.GetFileMetadataAsync("/")).IsDirectory);
        Assert.Single(h.Server.CommandsNamed("f"));
    }

    [Fact]
    public async Task QuotaReadsUsedAndMaximum()
    {
        using var h = new MegaHarness();

        var quota = await h.Provider.StorageQuotaAsync();
        Assert.Equal(1234567, quota!.UsedBytes);
        Assert.Equal(21474836480, quota.TotalBytes);
        Assert.Equal(1, (int)h.Server.CommandsNamed("uq").Single()["strg"]!);

        h.Server.Overrides["uq"] = _ => FakeHttpHandler.Json("[{\"cstrg\":\"42\",\"mstrg\":0}]");
        quota = await h.Provider.StorageQuotaAsync();
        Assert.Equal(42, quota!.UsedBytes);
        Assert.Null(quota.TotalBytes);
    }

    [Fact]
    public async Task RequestsCarryTheSessionId()
    {
        using var h = new MegaHarness();

        await h.Provider.ListDirectoryAsync("/");

        var request = h.Capture.Matching(HttpMethod.Post, "g.api.mega.co.nz/cs").First();
        Assert.Contains("&sid=" + h.Server.ExpectedSessionId, request.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("[{\"a\":\"f\",\"c\":1}]", Encoding.UTF8.GetString(request.Body));
    }

    [Fact]
    public async Task OperationsWithoutCredentialsAreNotAuthenticated()
    {
        using var provider = new MegaProvider(Guid.NewGuid(), new MemoryCredentialStore(), new FakeHttpHandler());

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
        Assert.Null(await provider.StorageQuotaAsync());
    }

    [Fact]
    public async Task DisconnectForgetsTheCredentials()
    {
        using var h = new MegaHarness();

        await h.Provider.DisconnectAsync();

        Assert.False(h.Provider.IsAuthenticated);
        Assert.Empty(h.Store.Keys);
    }

    [Fact]
    public void RegistryCreatesTheProvider()
    {
        using var provider = CloudProviderRegistry.Create(CloudProviderType.Mega, Guid.NewGuid(), new MemoryCredentialStore());
        Assert.IsType<MegaProvider>(provider);
        Assert.Contains(provider.ConnectSpec.Modes[0].Fields, f => f.Key == "code" && f.Kind == ConnectFieldKind.OneTimeCode && !f.Required);
    }

    [Fact]
    public void ParseMapsMegaErrorCodes()
    {
        Assert.Equal(CloudErrorKind.NotFound, Assert.Throws<CloudProviderException>(() => MegaApiClient.Parse("[-9]"u8.ToArray())).Kind);
        Assert.Equal(CloudErrorKind.TwoFactorRequired, Assert.Throws<CloudProviderException>(() => MegaApiClient.Parse("-26"u8.ToArray())).Kind);
        Assert.Equal(CloudErrorKind.RateLimited, Assert.Throws<CloudProviderException>(() => MegaApiClient.Parse("-6"u8.ToArray())).Kind);
        Assert.Equal(CloudErrorKind.InvalidResponse, Assert.Throws<CloudProviderException>(() => MegaApiClient.Parse("{}"u8.ToArray())).Kind);
        Assert.Equal(JsonValueKind.Array, MegaApiClient.Parse("[0]"u8.ToArray()).ValueKind);
    }
}

