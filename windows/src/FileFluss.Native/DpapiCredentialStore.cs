// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FileFluss.Core.Security;

namespace FileFluss.Native;

/// <summary>
/// The Windows counterpart of the macOS keychain: each secret is a file under
/// <c>%LOCALAPPDATA%\FileFluss\credentials</c>, encrypted with DPAPI for the current user —
/// readable only by this Windows account on this PC, never roamed.
///
/// <para>Credential Manager would be the obvious home, but it caps a secret at 2.5 KB, and
/// OAuth token sets with refresh tokens, Mega session keys and SFTP private keys all exceed
/// that. DPAPI has no limit and is what Credential Manager uses underneath anyway.</para>
/// </summary>
public sealed class DpapiCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FileFluss credential store v1");
    private readonly string _directory;
    private readonly Lock _gate = new();

    public DpapiCredentialStore(string directory)
    {
        _directory = directory;
    }

    public void Save(string key, byte[] data)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(key);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, Protect(data));
            File.Move(temporary, path, overwrite: true);
        }
    }

    public byte[]? Load(string key)
    {
        lock (_gate)
        {
            var path = PathFor(key);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return Unprotect(File.ReadAllBytes(path));
            }
            catch (Exception e) when (e is CryptographicException or IOException)
            {
                // Written by another Windows account or a reinstalled profile: unreadable, so
                // treat as absent; the account asks to sign in again.
                return null;
            }
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            try
            {
                File.Delete(PathFor(key));
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>File names are hashes so a key ("dropbox.&lt;uuid&gt;") never needs escaping.</summary>
    private string PathFor(string key)
        => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant() + ".bin");

    private static unsafe byte[] Protect(byte[] data)
    {
        fixed (byte* input = data)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new DataBlob { Size = data.Length, Data = (nint)input };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = (nint)entropy };
            if (!CryptProtectData(ref inBlob, "FileFluss", ref entropyBlob, 0, 0, CryptProtectUiForbidden, out var output))
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            return TakeBlob(output);
        }
    }

    private static unsafe byte[] Unprotect(byte[] data)
    {
        fixed (byte* input = data)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new DataBlob { Size = data.Length, Data = (nint)input };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = (nint)entropy };
            if (!CryptUnprotectData(ref inBlob, 0, ref entropyBlob, 0, 0, CryptProtectUiForbidden, out var output))
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            return TakeBlob(output);
        }
    }

    private static byte[] TakeBlob(DataBlob blob)
    {
        try
        {
            var result = new byte[blob.Size];
            Marshal.Copy(blob.Data, result, 0, blob.Size);
            return result;
        }
        finally
        {
            LocalFree(blob.Data);
        }
    }

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
