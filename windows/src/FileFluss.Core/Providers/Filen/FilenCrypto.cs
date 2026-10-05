// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Security.Cryptography;
using System.Text;

namespace FileFluss.Core.Providers.Filen;

/// <summary>
/// All Filen-specific crypto in one place; every routine corresponds to a function in
/// filen-sdk-rs (<c>src/crypto/v2.rs</c>). Bugs here present as "wrong password" or garbled
/// names, which is why each method is named for the Filen concept it implements.
///
/// <list type="bullet">
/// <item>Login (auth version 2): PBKDF2-HMAC-SHA512(password, salt as UTF-8 text, 200 000, 64
/// bytes) → lower-case hex (128 chars); the first 64 chars are the master-key string, the
/// last 64 are SHA-512'd (as text) to the hex password sent to <c>/v3/login</c>.</item>
/// <item>Master-key string → AES-256 key: PBKDF2-HMAC-SHA512(key, key, 1, 32) ("V2Key").</item>
/// <item>File-key string (32 ASCII chars) → AES-256 key: its bytes, no derivation.</item>
/// <item>Metadata ("002" envelope): <c>"002" ‖ nonce (12 chars [A-Za-z0-9]) ‖ base64(ciphertext ‖ tag)</c>, AES-256-GCM, no AAD.</item>
/// <item>File chunks: <c>nonce (12 bytes) ‖ ciphertext ‖ tag (16)</c>, AES-256-GCM, 1 MiB plaintext per chunk.</item>
/// </list>
/// </summary>
internal static class FilenCrypto
{
    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private const int NonceSize = 12;
    private const int TagSize = 16;

    public sealed record V2LoginDerivation(string MasterKeyHex, string LoginPasswordHex);

    /// <summary>
    /// The v2 login derivation. Filen feeds the salt string's bytes to PBKDF2 — not a base64 or
    /// hex decode (filen-types even calls that "probably bad practice").
    /// </summary>
    public static V2LoginDerivation DeriveV2Login(string password, string salt, int iterations = 200_000)
    {
        var derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt), iterations, HashAlgorithmName.SHA512, 64);
        var hex = Convert.ToHexStringLower(derived);
        var secondHalf = hex[64..];
        var loginPassword = Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(secondHalf)));
        return new V2LoginDerivation(hex[..64], loginPassword);
    }

    /// <summary>
    /// Filen's "V2Key": PBKDF2(key, key, 1 iteration, 32 bytes). Using the same value as
    /// password and salt with one round is essentially a hash, but it's what the SDK does.
    /// Also used on a file-key string for the per-file metadata key (<c>to_meta_key()</c>).
    /// </summary>
    public static byte[] AesKeyFromV2MasterKey(string keyAscii)
    {
        var bytes = Encoding.UTF8.GetBytes(keyAscii);
        return Rfc2898DeriveBytes.Pbkdf2(bytes, bytes, 1, HashAlgorithmName.SHA512, 32);
    }

    /// <summary>One chunk for upload: random 12-byte nonce ‖ ciphertext ‖ 16-byte tag.</summary>
    public static byte[] EncryptFileChunk(ReadOnlySpan<byte> plain, byte[] key)
    {
        var output = new byte[NonceSize + plain.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plain, output.AsSpan(NonceSize, plain.Length), output.AsSpan(NonceSize + plain.Length, TagSize));
        return output;
    }

    /// <summary>Decrypts <c>nonce ‖ ciphertext ‖ tag</c>; a wrong key or damaged chunk fails the tag check.</summary>
    public static byte[] DecryptFileChunk(ReadOnlySpan<byte> data, byte[] key)
    {
        if (data.Length < NonceSize + TagSize)
        {
            throw CloudProviderException.InvalidResponse();
        }

        var cipherLength = data.Length - NonceSize - TagSize;
        var plain = new byte[cipherLength];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(data[..NonceSize], data.Slice(NonceSize, cipherLength), data[^TagSize..], plain);
        }
        catch (CryptographicException)
        {
            throw CloudProviderException.InvalidResponse();
        }

        return plain;
    }

    /// <summary>
    /// The "002" metadata envelope. The nonce is 12 printable characters (the SDK's alphabet)
    /// so the server can keep the envelope as one UTF-8 string; tests pass a fixed one.
    /// </summary>
    public static string EncryptMetadata(string plaintext, byte[] key, string? nonce = null)
    {
        nonce ??= RandomString(NonceSize);
        var nonceBytes = Encoding.UTF8.GetBytes(nonce);
        if (nonceBytes.Length != NonceSize)
        {
            throw new ArgumentException("The nonce must be 12 ASCII characters.", nameof(nonce));
        }

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var body = new byte[plain.Length + TagSize];
        using (var gcm = new AesGcm(key, TagSize))
        {
            gcm.Encrypt(nonceBytes, plain, body.AsSpan(0, plain.Length), body.AsSpan(plain.Length));
        }

        return "002" + nonce + Convert.ToBase64String(body);
    }

    /// <summary>Opens an "002" envelope. Throws <see cref="CloudErrorKind.InvalidResponse"/> for anything malformed or a wrong key.</summary>
    public static string DecryptMetadata(string encrypted, byte[] key)
    {
        if (encrypted.Length < 15 || !encrypted.StartsWith("002", StringComparison.Ordinal))
        {
            throw CloudProviderException.InvalidResponse();
        }

        var nonce = Encoding.UTF8.GetBytes(encrypted.Substring(3, NonceSize));
        if (nonce.Length != NonceSize)
        {
            throw CloudProviderException.InvalidResponse();
        }

        byte[] body;
        try
        {
            body = Convert.FromBase64String(encrypted[15..]);
        }
        catch (FormatException)
        {
            throw CloudProviderException.InvalidResponse();
        }

        if (body.Length < TagSize)
        {
            throw CloudProviderException.InvalidResponse();
        }

        var plain = new byte[body.Length - TagSize];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, body.AsSpan(0, plain.Length), body.AsSpan(plain.Length), plain);
            return new UTF8Encoding(false, true).GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    /// <summary>
    /// v2 name hash: SHA-1 of the lower-case hex SHA-512 of the lower-cased name. The server
    /// indexes names by it without ever seeing them.
    /// </summary>
    public static string V2HashName(string name)
    {
        var sha512Hex = Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant())));
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(sha512Hex)));
    }

    /// <summary>Random string from <c>[A-Za-z0-9]</c>: file keys, upload keys, the <c>rm</c> value, metadata nonces.</summary>
    public static string RandomString(int length) => new(RandomNumberGenerator.GetItems<char>(Alphabet, length));
}
