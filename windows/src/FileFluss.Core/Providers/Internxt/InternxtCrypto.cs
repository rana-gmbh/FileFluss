// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileFluss.Core.Providers.Internxt;

/// <summary>
/// Internxt's client-side crypto, transliterated (like the macOS client) from the official
/// <c>@internxt/cli</c> (crypto.service.ts), <c>@internxt/lib</c> and <c>@internxt/inxt-js</c>.
/// The login password never leaves the device in clear, the account mnemonic is decrypted
/// locally, and per-file keys derived from it encrypt file content for the Network service.
///
/// <list type="bullet">
/// <item>Account-secret text (login salt, password hash, mnemonic): CryptoJS-compatible
/// AES-256-CBC in OpenSSL's <c>"Salted__"</c> envelope with an MD5 EVP_BytesToKey, hex-encoded.</item>
/// <item>File content: AES-256-CTR, key derived from the mnemonic's BIP39 seed, the bucket id
/// and a random per-file index; IV = the index's first 16 bytes.</item>
/// </list>
/// </summary>
internal static class InternxtCrypto
{
    /// <summary>
    /// <c>APP_CRYPTO_SECRET</c> from the Internxt CLI's committed <c>.env.template</c> — the
    /// production secret that wraps the login salt and the password hash, shared by all clients.
    /// </summary>
    public const string AppCryptoSecret = "6KYQBP847D4ATSFA";

    private static readonly byte[] SaltedPrefix = "Salted__"u8.ToArray();

    // MARK: - Hex

    public static byte[]? HexDecode(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string HexEncode(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(data);

    // MARK: - CryptoJS-compatible AES-256-CBC

    /// <summary>
    /// OpenSSL EVP_BytesToKey with MD5, as CryptoJS derives key and IV from a passphrase:
    /// three chained MD5s of (secret ‖ salt) — the first two are the 32-byte key, the third the IV.
    /// </summary>
    private static (byte[] Key, byte[] Iv) KeyAndIv(string secret, ReadOnlySpan<byte> salt)
    {
        var password = Encoding.UTF8.GetBytes(secret).Concat(salt.ToArray()).ToArray();
        var d0 = MD5.HashData(password);
        var d1 = MD5.HashData(d0.Concat(password).ToArray());
        var d2 = MD5.HashData(d1.Concat(password).ToArray());
        return (d0.Concat(d1).ToArray(), d2);
    }

    /// <summary>
    /// Decrypts the hex of CryptoJS <c>AES.encrypt(text, secret)</c>:
    /// <c>"Salted__"</c> (8) ‖ salt (8) ‖ AES-256-CBC ciphertext. Null for a wrong secret.
    /// </summary>
    public static string? DecryptText(string hex, string secret)
    {
        var raw = HexDecode(hex);
        if (raw is null || raw.Length <= 16)
        {
            return null;
        }

        var (key, iv) = KeyAndIv(secret, raw.AsSpan(8, 8));
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            var plain = aes.DecryptCbc(raw.AsSpan(16), iv, PaddingMode.PKCS7);
            return new UTF8Encoding(false, true).GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Encrypts into the same CryptoJS hex envelope with a random (or, in tests, a given) 8-byte salt.</summary>
    public static string EncryptText(string text, string secret, byte[]? salt = null)
    {
        salt ??= RandomNumberGenerator.GetBytes(8);
        var (key, iv) = KeyAndIv(secret, salt);
        using var aes = Aes.Create();
        aes.Key = key;
        var ciphertext = aes.EncryptCbc(Encoding.UTF8.GetBytes(text), iv, PaddingMode.PKCS7);
        return HexEncode(SaltedPrefix.Concat(salt).Concat(ciphertext).ToArray());
    }

    // MARK: - Login

    /// <summary>
    /// PBKDF2-HMAC-SHA1(password, hex-decoded salt, 10 000, 32) as hex — CryptoJS
    /// <c>PBKDF2(password, Hex.parse(salt), {keySize: 256/32, iterations: 10000})</c>.
    /// </summary>
    public static string? PassToHash(string password, string saltHex, int iterations = 10_000)
    {
        var salt = HexDecode(saltHex);
        return salt is null
            ? null
            : HexEncode(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA1, 32));
    }

    /// <summary>The CLI's <c>encryptPasswordHash</c>: unwrap the server's salt, hash the password with it, wrap the hash.</summary>
    public static string? EncryptPasswordHash(string password, string encryptedSalt)
    {
        var saltHex = DecryptText(encryptedSalt, AppCryptoSecret);
        if (saltHex is null || PassToHash(password, saltHex) is not { } hash)
        {
            return null;
        }

        return EncryptText(hash, AppCryptoSecret);
    }

    /// <summary>The mnemonic is CryptoJS-encrypted under the user's plain password (not the app secret).</summary>
    public static string? DecryptMnemonic(string encryptedMnemonic, string password) => DecryptText(encryptedMnemonic, password);

    // MARK: - File keys

    /// <summary>BIP39 seed: PBKDF2-HMAC-SHA512(NFKD(mnemonic), "mnemonic", 2048, 64) — no passphrase, like <c>bip39.mnemonicToSeed</c>.</summary>
    public static byte[] MnemonicToSeed(string mnemonic)
    {
        var normalized = mnemonic.Normalize(NormalizationForm.FormKD);
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(normalized), "mnemonic"u8.ToArray(), 2048, HashAlgorithmName.SHA512, 64);
    }

    /// <summary>
    /// inxt-js <c>GenerateFileKey(mnemonic, bucketId, index)</c>: bucketKey = SHA-512(seed ‖
    /// bucketId), fileKey = SHA-512(bucketKey[0..32] ‖ index)[0..32]. Null for a malformed bucket id.
    /// </summary>
    public static byte[]? GenerateFileKey(string mnemonic, string bucketIdHex, ReadOnlySpan<byte> index)
        => GenerateFileKey(MnemonicToSeed(mnemonic), bucketIdHex, index);

    public static byte[]? GenerateFileKey(byte[] seed, string bucketIdHex, ReadOnlySpan<byte> index)
    {
        if (HexDecode(bucketIdHex) is not { } bucketId)
        {
            return null;
        }

        var bucketKey = SHA512.HashData(seed.Concat(bucketId).ToArray());
        var fileKey = SHA512.HashData(bucketKey.AsSpan(0, 32).ToArray().Concat(index.ToArray()).ToArray());
        return fileKey[..32];
    }

    // MARK: - Shard hash

    /// <summary>The Network shard hash: RIPEMD-160 of the SHA-256 of the encrypted content ("hash160"), hex.</summary>
    public static string ShardHashHex(ReadOnlySpan<byte> sha256OfCiphertext) => HexEncode(Ripemd160(sha256OfCiphertext));

    /// <summary>
    /// RIPEMD-160 — not in .NET (or CommonCrypto), so implemented from the reference
    /// specification: two parallel 80-step lines over 512-bit blocks, little-endian words.
    /// </summary>
    public static byte[] Ripemd160(ReadOnlySpan<byte> message)
    {
        uint h0 = 0x67452301, h1 = 0xEFCDAB89, h2 = 0x98BADCFE, h3 = 0x10325476, h4 = 0xC3D2E1F0;

        var bitLength = (ulong)message.Length * 8;
        var paddedLength = ((message.Length + 8) / 64 + 1) * 64;
        var padded = new byte[paddedLength];
        message.CopyTo(padded);
        padded[message.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(paddedLength - 8), bitLength);

        Span<uint> x = stackalloc uint[16];
        for (var block = 0; block < paddedLength; block += 64)
        {
            for (var i = 0; i < 16; i++)
            {
                x[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(block + (i * 4)));
            }

            uint al = h0, bl = h1, cl = h2, dl = h3, el = h4;
            uint ar = h0, br = h1, cr = h2, dr = h3, er = h4;
            for (var j = 0; j < 80; j++)
            {
                var round = j / 16;
                var t = RotateLeft(al + F(round, bl, cl, dl) + x[RL[j]] + KL[round], SL[j]) + el;
                al = el;
                el = dl;
                dl = RotateLeft(cl, 10);
                cl = bl;
                bl = t;

                t = RotateLeft(ar + F(4 - round, br, cr, dr) + x[RR[j]] + KR[round], SR[j]) + er;
                ar = er;
                er = dr;
                dr = RotateLeft(cr, 10);
                cr = br;
                br = t;
            }

            var temp = h1 + cl + dr;
            h1 = h2 + dl + er;
            h2 = h3 + el + ar;
            h3 = h4 + al + br;
            h4 = h0 + bl + cr;
            h0 = temp;
        }

        var digest = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(0), h0);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4), h1);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8), h2);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12), h3);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(16), h4);
        return digest;
    }

    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));

    private static uint F(int round, uint x, uint y, uint z) => round switch
    {
        0 => x ^ y ^ z,
        1 => (x & y) | (~x & z),
        2 => (x | ~y) ^ z,
        3 => (x & z) | (y & ~z),
        _ => x ^ (y | ~z),
    };

    private static readonly int[] RL =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        7, 4, 13, 1, 10, 6, 15, 3, 12, 0, 9, 5, 2, 14, 11, 8,
        3, 10, 14, 4, 9, 15, 8, 1, 2, 7, 0, 6, 13, 11, 5, 12,
        1, 9, 11, 10, 0, 8, 12, 4, 13, 3, 7, 15, 14, 5, 6, 2,
        4, 0, 5, 9, 7, 12, 2, 10, 14, 1, 3, 8, 11, 6, 15, 13,
    ];

    private static readonly int[] RR =
    [
        5, 14, 7, 0, 9, 2, 11, 4, 13, 6, 15, 8, 1, 10, 3, 12,
        6, 11, 3, 7, 0, 13, 5, 10, 14, 15, 8, 12, 4, 9, 1, 2,
        15, 5, 1, 3, 7, 14, 6, 9, 11, 8, 12, 2, 10, 0, 4, 13,
        8, 6, 4, 1, 3, 11, 15, 0, 5, 12, 2, 13, 9, 7, 10, 14,
        12, 15, 10, 4, 1, 5, 8, 7, 6, 2, 13, 14, 0, 3, 9, 11,
    ];

    private static readonly int[] SL =
    [
        11, 14, 15, 12, 5, 8, 7, 9, 11, 13, 14, 15, 6, 7, 9, 8,
        7, 6, 8, 13, 11, 9, 7, 15, 7, 12, 15, 9, 11, 7, 13, 12,
        11, 13, 6, 7, 14, 9, 13, 15, 14, 8, 13, 6, 5, 12, 7, 5,
        11, 12, 14, 15, 14, 15, 9, 8, 9, 14, 5, 6, 8, 6, 5, 12,
        9, 15, 5, 11, 6, 8, 13, 12, 5, 12, 13, 14, 11, 8, 5, 6,
    ];

    private static readonly int[] SR =
    [
        8, 9, 9, 11, 13, 15, 15, 5, 7, 7, 8, 11, 14, 14, 12, 6,
        9, 13, 15, 7, 12, 8, 9, 11, 7, 7, 12, 7, 6, 15, 13, 11,
        9, 7, 15, 11, 8, 6, 6, 14, 12, 13, 5, 14, 13, 13, 7, 5,
        15, 5, 8, 11, 14, 14, 6, 14, 6, 9, 12, 9, 12, 5, 15, 8,
        8, 5, 12, 9, 12, 5, 14, 6, 8, 13, 6, 5, 15, 13, 11, 11,
    ];

    private static readonly uint[] KL = [0x00000000, 0x5A827999, 0x6ED9EBA1, 0x8F1BBCDC, 0xA953FD4E];

    private static readonly uint[] KR = [0x50A28BE6, 0x5C4DD124, 0x6D703EF3, 0x7A6D76E9, 0x00000000];
}

/// <summary>
/// AES-CTR with a 128-bit big-endian counter (CommonCrypto's <c>kCCModeOptionCTR_BE</c>, Node's
/// <c>aes-256-ctr</c>), streamable: successive calls continue the keystream.
/// </summary>
internal sealed class InternxtAesCtr : IDisposable
{
    private const int BatchBlocks = 4096;
    private readonly Aes _aes;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _keystream = new byte[16];
    private readonly byte[] _counterBatch = new byte[BatchBlocks * 16];
    private readonly byte[] _keystreamBatch = new byte[BatchBlocks * 16];
    private int _keystreamUsed = 16;

    public InternxtAesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        _aes = Aes.Create();
        _aes.Key = key.ToArray();
        iv[..16].CopyTo(_counter);
    }

    /// <summary>XORs <paramref name="data"/> with the keystream in place.</summary>
    public void Transform(Span<byte> data)
    {
        var i = 0;
        while (i < data.Length && _keystreamUsed < 16)
        {
            data[i++] ^= _keystream[_keystreamUsed++];
        }

        while (data.Length - i >= 16)
        {
            var blocks = Math.Min(BatchBlocks, (data.Length - i) / 16);
            for (var b = 0; b < blocks; b++)
            {
                _counter.CopyTo(_counterBatch, b * 16);
                Increment();
            }

            var bytes = blocks * 16;
            _aes.EncryptEcb(_counterBatch.AsSpan(0, bytes), _keystreamBatch.AsSpan(0, bytes), PaddingMode.None);
            var target = data.Slice(i, bytes);
            for (var k = 0; k < bytes; k += 8)
            {
                var value = BinaryPrimitives.ReadUInt64LittleEndian(target[k..]) ^ BinaryPrimitives.ReadUInt64LittleEndian(_keystreamBatch.AsSpan(k));
                BinaryPrimitives.WriteUInt64LittleEndian(target[k..], value);
            }

            i += bytes;
        }

        if (i < data.Length)
        {
            _aes.EncryptEcb(_counter, _keystream, PaddingMode.None);
            Increment();
            _keystreamUsed = 0;
            while (i < data.Length)
            {
                data[i++] ^= _keystream[_keystreamUsed++];
            }
        }
    }

    public void Dispose() => _aes.Dispose();

    private void Increment()
    {
        for (var j = 15; j >= 0; j--)
        {
            if (++_counter[j] != 0)
            {
                break;
            }
        }
    }
}
