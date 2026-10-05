// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FileFluss.Core.Providers.Mega;

/// <summary>
/// MEGA's client-side crypto. MEGA works in 32-bit big-endian words ("a32"); every key here is
/// the byte form of those words, so <c>[0x93C467E3, …]</c> is <c>93 C4 67 E3 …</c>.
///
/// <list type="bullet">
/// <item>Account keys: AES-128-ECB (master key under the password key, node keys under the master key).</item>
/// <item>Node attributes: <c>"MEGA" + JSON</c>, zero-padded, AES-128-CBC with a zero IV.</item>
/// <item>File content: AES-128-CTR, counter <c>[nonce0, nonce1, 0, 0]</c>, plus a CBC-MAC per
/// chunk condensed into an 8-byte meta-MAC stored in the node key.</item>
/// <item>Session: RSA with MPI-encoded keys (csid), or a temporary session id (tsid).</item>
/// </list>
/// </summary>
internal static class MegaCrypto
{
    /// <summary>The fixed start value of MEGA's v1 password key (<c>prepare_key</c>).</summary>
    private static readonly byte[] PrepareKeySeed = Convert.FromHexString("93C467E37DB0C7A4D1BE3F810152CB56");

    private static readonly JsonSerializerOptions AttributeJson = new()
    {
        // Names stay as UTF-8 like the web client writes them; only what JSON requires is escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // MARK: - Base64 (MEGA uses URL-safe base64 without padding)

    public static byte[]? Base64UrlDecode(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var base64 = value.Trim().Replace('-', '+').Replace('_', '/').TrimEnd('=');
        switch (base64.Length % 4)
        {
            case 2:
                base64 += "==";
                break;
            case 3:
                base64 += "=";
                break;
            case 1:
                return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // MARK: - Words

    /// <summary>Big-endian words; a trailing partial word is dropped, as MEGA's <c>str_to_a32</c> callers do.</summary>
    public static uint[] ToWords(ReadOnlySpan<byte> bytes)
    {
        var words = new uint[bytes.Length / 4];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32BigEndian(bytes[(i * 4)..]);
        }

        return words;
    }

    public static byte[] ToBytes(ReadOnlySpan<uint> words)
    {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), words[i]);
        }

        return bytes;
    }

    // MARK: - AES-128 building blocks

    public static Aes CreateAes(ReadOnlySpan<byte> key)
    {
        var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes;
    }

    /// <summary>AES-ECB without padding; <paramref name="data"/> is truncated to whole blocks.</summary>
    public static byte[] EncryptEcb(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        using var aes = CreateAes(key);
        return aes.EncryptEcb(data[..(data.Length / 16 * 16)], PaddingMode.None);
    }

    public static byte[] DecryptEcb(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        using var aes = CreateAes(key);
        return aes.DecryptEcb(data[..(data.Length / 16 * 16)], PaddingMode.None);
    }

    // MARK: - Password key, user handle

    /// <summary>
    /// MEGA's v1 <c>prepare_key</c>: the password (UTF-8, as big-endian words in 16-byte
    /// blocks, zero-filled) is used as a list of AES keys; the fixed seed is encrypted with each
    /// key in turn, and that whole pass repeats 65 536 times.
    /// <para>Deviation from macOS: the Swift port ran a single pass and could not sign in to
    /// v1 accounts; this follows the web client and megajs.</para>
    /// </summary>
    public static byte[] PrepareKeyV1(string password)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var blockCount = (passwordBytes.Length + 15) / 16;
        var encryptors = new ICryptoTransform[blockCount];
        var aesList = new Aes[blockCount];
        try
        {
            for (var b = 0; b < blockCount; b++)
            {
                var blockKey = new byte[16];
                var take = Math.Min(16, passwordBytes.Length - (b * 16));
                passwordBytes.AsSpan(b * 16, take).CopyTo(blockKey);
                aesList[b] = Aes.Create();
                aesList[b].Mode = CipherMode.ECB;
                aesList[b].Padding = PaddingMode.None;
                encryptors[b] = aesList[b].CreateEncryptor(blockKey, null);
            }

            var key = (byte[])PrepareKeySeed.Clone();
            var output = new byte[16];
            for (var round = 0; round < 0x10000; round++)
            {
                foreach (var encryptor in encryptors)
                {
                    encryptor.TransformBlock(key, 0, 16, output, 0);
                    (key, output) = (output, key);
                }
            }

            return key;
        }
        finally
        {
            foreach (var encryptor in encryptors)
            {
                encryptor?.Dispose();
            }

            foreach (var aes in aesList)
            {
                aes?.Dispose();
            }
        }
    }

    /// <summary>
    /// MEGA's v1 user handle (<c>stringhash</c>): the lower-cased email as big-endian words is
    /// XOR-folded into four words, encrypted 16 384 times with the password key, and words 0
    /// and 2 are sent base64url-encoded.
    /// <para>Deviation from macOS: the Swift port folded bytes, not words, into the hash.</para>
    /// </summary>
    public static string StringHash(string email, ReadOnlySpan<byte> passwordKey)
    {
        var bytes = Encoding.UTF8.GetBytes(email.ToLowerInvariant());
        var hash = new byte[16];
        for (var i = 0; i < bytes.Length; i++)
        {
            // Byte i belongs to word i/4 (big-endian), which folds onto hash word (i/4) & 3.
            hash[(((i / 4) & 3) * 4) + (i % 4)] ^= bytes[i];
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor(passwordKey.ToArray(), null);
        var output = new byte[16];
        for (var i = 0; i < 16384; i++)
        {
            encryptor.TransformBlock(hash, 0, 16, output, 0);
            (hash, output) = (output, hash);
        }

        var result = new byte[8];
        hash.AsSpan(0, 4).CopyTo(result);
        hash.AsSpan(8, 4).CopyTo(result.AsSpan(4));
        return Base64UrlEncode(result);
    }

    /// <summary>v2 accounts: PBKDF2-HMAC-SHA512(password, salt, 100 000, 32). Bytes 0–15 are the password key, 16–31 the user handle.</summary>
    public static byte[] DeriveV2(string password, byte[] salt, int iterations = 100_000)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA512, 32);

    // MARK: - Node keys and attributes

    /// <summary>The 128-bit key a node's attributes are encrypted with: folders use their key as is, files XOR the two halves.</summary>
    public static byte[] AttributeKey(ReadOnlySpan<uint> nodeKey)
    {
        if (nodeKey.Length >= 8)
        {
            return ToBytes([nodeKey[0] ^ nodeKey[4], nodeKey[1] ^ nodeKey[5], nodeKey[2] ^ nodeKey[6], nodeKey[3] ^ nodeKey[7]]);
        }

        return ToBytes(nodeKey[..Math.Min(4, nodeKey.Length)]);
    }

    /// <summary>
    /// The base64url key material of a node's <c>k</c> field (<c>"owner:key"</c> or several
    /// <c>"handle:key"</c> parts separated by '/'); the first part wins, as on macOS.
    /// </summary>
    public static byte[]? FirstKeyPart(string keyField)
    {
        foreach (var part in keyField.Split('/'))
        {
            var segments = part.Split(':');
            if (segments.Length >= 2)
            {
                return Base64UrlDecode(segments[^1]);
            }
        }

        return null;
    }

    /// <summary><c>"MEGA" + {"n":"name"}</c>, zero-padded to the block size, AES-CBC with a zero IV.</summary>
    public static string EncryptAttributes(string name, ReadOnlySpan<byte> key)
    {
        var json = "{\"n\":" + JsonSerializer.Serialize(name, AttributeJson) + "}";
        var plain = Encoding.UTF8.GetBytes("MEGA" + json);
        var padded = new byte[(plain.Length + 15) / 16 * 16];
        plain.CopyTo(padded, 0);
        using var aes = CreateAes(key);
        return Base64UrlEncode(aes.EncryptCbc(padded, new byte[16], PaddingMode.None));
    }

    /// <summary>The <c>n</c> attribute, or null when the attributes don't decrypt to MEGA's format (wrong key, foreign share).</summary>
    public static string? DecryptName(string attributes, ReadOnlySpan<byte> key)
    {
        var data = Base64UrlDecode(attributes);
        if (data is null || data.Length == 0 || data.Length % 16 != 0)
        {
            return null;
        }

        byte[] plain;
        try
        {
            using var aes = CreateAes(key);
            plain = aes.DecryptCbc(data, new byte[16], PaddingMode.None);
        }
        catch (CryptographicException)
        {
            return null;
        }

        if (plain.Length < 4 || plain[0] != 'M' || plain[1] != 'E' || plain[2] != 'G' || plain[3] != 'A')
        {
            return null;
        }

        var end = Array.IndexOf(plain, (byte)0, 4);
        var json = plain.AsSpan(4, (end < 0 ? plain.Length : end) - 4);
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("n", out var n) &&
                   n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A file node key as MEGA stores it: <c>[k0^n0, k1^n1, k2^m0, k3^m1, n0, n1, m0, m1]</c>
    /// where k is the content key, n the CTR nonce and m the meta-MAC.
    /// </summary>
    public static uint[] ComposeFileKey(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> metaMac)
    {
        var k = ToWords(contentKey);
        var n = ToWords(nonce);
        var m = ToWords(metaMac);
        return [k[0] ^ n[0], k[1] ^ n[1], k[2] ^ m[0], k[3] ^ m[1], n[0], n[1], m[0], m[1]];
    }

    // MARK: - RSA (session id)

    /// <summary>A multi-precision integer: 2-byte big-endian bit length, then the bytes. Null when truncated.</summary>
    public static byte[]? ReadMpi(ReadOnlySpan<byte> bytes, out int bytesRead)
    {
        bytesRead = 0;
        if (bytes.Length < 2)
        {
            return null;
        }

        var bitLength = (bytes[0] << 8) | bytes[1];
        var byteLength = (bitLength + 7) / 8;
        if (bytes.Length < 2 + byteLength)
        {
            return null;
        }

        bytesRead = 2 + byteLength;
        return bytes.Slice(2, byteLength).ToArray();
    }

    /// <summary>The first <paramref name="count"/> MPIs of a decrypted private key (p, q, d, u).</summary>
    public static byte[][] ReadMpiSequence(ReadOnlySpan<byte> bytes, int count)
    {
        var result = new byte[count][];
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            result[i] = ReadMpi(bytes[offset..], out var read) ?? throw CloudProviderException.InvalidResponse();
            offset += read;
        }

        return result;
    }

    /// <summary>Textbook RSA decryption with the private key's p, q and d (no padding — MEGA's csid has none).</summary>
    public static byte[] RsaDecrypt(byte[] ciphertext, byte[][] privateKey)
    {
        if (privateKey.Length < 4)
        {
            return ciphertext;
        }

        var p = new BigInteger(privateKey[0], isUnsigned: true, isBigEndian: true);
        var q = new BigInteger(privateKey[1], isUnsigned: true, isBigEndian: true);
        var d = new BigInteger(privateKey[2], isUnsigned: true, isBigEndian: true);
        var c = new BigInteger(ciphertext, isUnsigned: true, isBigEndian: true);
        var m = BigInteger.ModPow(c, d, p * q);
        return m.IsZero ? [0] : m.ToByteArray(isUnsigned: true, isBigEndian: true);
    }

    // MARK: - Hashcash proof-of-work

    /// <summary>The largest acceptable first digest word for an easiness byte.</summary>
    public static uint HashcashThreshold(byte easiness)
    {
        uint e = easiness;
        var mantissa = ((e & 63) << 1) + 1;
        var shift = ((e >> 6) * 7) + 3;
        return mantissa << (int)shift;
    }

    /// <summary>
    /// Solves an <c>X-Hashcash: 1:&lt;easiness&gt;:&lt;time&gt;:&lt;token&gt;</c> challenge: find a
    /// 4-byte nonce such that SHA-256(nonce ‖ token × 262 144) starts with a word at or below
    /// the threshold. Runs one worker per core, each with its own ~12 MB buffer.
    /// </summary>
    public static string SolveHashcash(string challenge, CancellationToken cancellationToken, int? workerCount = null)
    {
        var parts = challenge.Split(':');
        if (parts.Length != 4 || parts[0] != "1" || !byte.TryParse(parts[1], out var easiness))
        {
            throw CloudProviderException.InvalidResponse();
        }

        var token = parts[3];
        var tokenData = Base64UrlDecode(token);
        if (tokenData is not { Length: 48 })
        {
            throw CloudProviderException.InvalidResponse();
        }

        var threshold = HashcashThreshold(easiness);
        const int Repeat = 262_144;
        var template = new byte[4 + (Repeat * 48)];
        for (var i = 0; i < Repeat; i++)
        {
            tokenData.CopyTo(template, 4 + (i * 48));
        }

        var workers = Math.Max(1, workerCount ?? Environment.ProcessorCount);
        var stop = 0;
        string? solution = null;
        var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(() =>
        {
            var buffer = (byte[])template.Clone();
            Span<byte> digest = stackalloc byte[32];
            var n = (uint)w;
            var stride = (uint)workers;
            while (Volatile.Read(ref stop) == 0 && !cancellationToken.IsCancellationRequested)
            {
                BinaryPrimitives.WriteUInt32BigEndian(buffer, n);
                SHA256.HashData(buffer, digest);
                if (BinaryPrimitives.ReadUInt32BigEndian(digest) <= threshold)
                {
                    if (Interlocked.CompareExchange(ref stop, 1, 0) == 0)
                    {
                        Span<byte> nonce = stackalloc byte[4];
                        BinaryPrimitives.WriteUInt32BigEndian(nonce, n);
                        solution = $"1:{token}:{Base64UrlEncode(nonce)}";
                    }

                    return;
                }

                var next = n + stride;
                if (next < n)
                {
                    return; // wrapped around: this worker's share of the nonce space is exhausted
                }

                n = next;
            }
        })).ToArray();

        Task.WaitAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();
        return solution ?? throw CloudProviderException.ServerError(402);
    }
}

/// <summary>
/// AES-CTR with a 128-bit big-endian counter (CommonCrypto's <c>kCCModeOptionCTR_BE</c>),
/// streamable: successive calls continue the keystream. .NET has no CTR mode, so the
/// keystream is AES-ECB over the counter blocks.
/// </summary>
internal sealed class MegaAesCtr : IDisposable
{
    private const int BatchBlocks = 4096;
    private readonly Aes _aes;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _keystream = new byte[16];
    private readonly byte[] _counterBatch = new byte[BatchBlocks * 16];
    private readonly byte[] _keystreamBatch = new byte[BatchBlocks * 16];
    private int _keystreamUsed = 16;

    public MegaAesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> initialCounter)
    {
        _aes = MegaCrypto.CreateAes(key);
        initialCounter[..16].CopyTo(_counter);
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
            Xor(data.Slice(i, bytes), _keystreamBatch.AsSpan(0, bytes));
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

    private static void Xor(Span<byte> data, ReadOnlySpan<byte> keystream)
    {
        var i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(data[i..]) ^ BinaryPrimitives.ReadUInt64LittleEndian(keystream[i..]);
            BinaryPrimitives.WriteUInt64LittleEndian(data[i..], value);
        }

        for (; i < data.Length; i++)
        {
            data[i] ^= keystream[i];
        }
    }

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

/// <summary>
/// MEGA's file MAC, fed the plaintext in any slicing. The file is cut into chunks of 128 KiB,
/// 256 KiB, … 1 MiB (growing by 128 KiB), then 1 MiB each; every chunk gets a CBC-MAC with
/// IV <c>[n0, n1, n0, n1]</c> (last block zero-padded); the chunk MACs are folded by
/// <c>mac = AES(mac ⊕ chunkMac)</c>, and the 8-byte meta-MAC is
/// <c>[mac0 ⊕ mac1, mac2 ⊕ mac3]</c>.
/// </summary>
internal sealed class MegaMac : IDisposable
{
    private readonly Aes _aes;
    private readonly byte[] _chunkIv = new byte[16];
    private readonly byte[] _chunkMac = new byte[16];
    private readonly byte[] _fileMac = new byte[16];
    private readonly byte[] _pending = new byte[16];
    private byte[] _scratch = new byte[64 * 1024];
    private int _pendingCount;
    private long _position;
    private long _chunkEnd;
    private int _chunkIndex;
    private bool _inChunk;

    public MegaMac(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> nonce)
    {
        _aes = MegaCrypto.CreateAes(contentKey);
        nonce[..8].CopyTo(_chunkIv);
        nonce[..8].CopyTo(_chunkIv.AsSpan(8));
    }

    /// <summary>Size of MAC chunk <paramref name="index"/> (0-based).</summary>
    public static int ChunkSize(int index) => index < 8 ? (index + 1) * 128 * 1024 : 1024 * 1024;

    /// <summary>The (offset, length) of every MAC chunk of a file of <paramref name="size"/> bytes.</summary>
    public static IEnumerable<(long Offset, int Length)> Chunks(long size)
    {
        long offset = 0;
        for (var index = 0; offset < size; index++)
        {
            var length = (int)Math.Min(ChunkSize(index), size - offset);
            yield return (offset, length);
            offset += length;
        }
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (!_inChunk)
            {
                _chunkIv.CopyTo(_chunkMac, 0);
                _chunkEnd = _position + ChunkSize(_chunkIndex);
                _inChunk = true;
            }

            var take = (int)Math.Min(data.Length, _chunkEnd - _position);
            Absorb(data[..take]);
            _position += take;
            data = data[take..];
            if (_position == _chunkEnd)
            {
                FinishChunk();
            }
        }
    }

    /// <summary>The 8-byte meta-MAC after the whole file went through <see cref="Update"/>.</summary>
    public byte[] FinishMetaMac()
    {
        if (_inChunk)
        {
            FinishChunk();
        }

        var meta = new byte[8];
        for (var i = 0; i < 4; i++)
        {
            meta[i] = (byte)(_fileMac[i] ^ _fileMac[i + 4]);
            meta[i + 4] = (byte)(_fileMac[i + 8] ^ _fileMac[i + 12]);
        }

        return meta;
    }

    public void Dispose() => _aes.Dispose();

    private void Absorb(ReadOnlySpan<byte> data)
    {
        if (_pendingCount > 0)
        {
            var fill = Math.Min(16 - _pendingCount, data.Length);
            data[..fill].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += fill;
            data = data[fill..];
            if (_pendingCount < 16)
            {
                return;
            }

            ProcessBlocks(_pending);
            _pendingCount = 0;
        }

        var whole = data.Length / 16 * 16;
        if (whole > 0)
        {
            ProcessBlocks(data[..whole]);
        }

        data[whole..].CopyTo(_pending);
        _pendingCount = data.Length - whole;
    }

    /// <summary>CBC-encrypts whole blocks with the running MAC as IV; the last ciphertext block is the new MAC.</summary>
    private void ProcessBlocks(ReadOnlySpan<byte> blocks)
    {
        if (_scratch.Length < blocks.Length)
        {
            _scratch = new byte[blocks.Length];
        }

        var output = _scratch.AsSpan(0, blocks.Length);
        _aes.EncryptCbc(blocks, _chunkMac, output, PaddingMode.None);
        output[^16..].CopyTo(_chunkMac);
    }

    private void FinishChunk()
    {
        if (_pendingCount > 0)
        {
            _pending.AsSpan(_pendingCount).Clear();
            ProcessBlocks(_pending);
            _pendingCount = 0;
        }

        Span<byte> folded = stackalloc byte[16];
        for (var i = 0; i < 16; i++)
        {
            folded[i] = (byte)(_fileMac[i] ^ _chunkMac[i]);
        }

        _aes.EncryptEcb(folded, _fileMac, PaddingMode.None);
        _chunkIndex++;
        _inChunk = false;
    }
}
