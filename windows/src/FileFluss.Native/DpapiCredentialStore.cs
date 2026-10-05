// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
    private bool _secured;

    public DpapiCredentialStore(string directory)
    {
        _directory = directory;
    }

    public void Save(string key, byte[] data)
    {
        lock (_gate)
        {
            EnsureDirectory();
            var path = PathFor(key);
            var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                // Flushed to disk before the rename: after a power cut the old secret or the
                // new one is there, never an empty file that reads as "signed out".
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Protect(data));
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, path, overwrite: true);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
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
            catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
            {
                // Written by another Windows account or a reinstalled profile, truncated, or
                // locked away: unreadable, so treat as absent; the account asks to sign in again.
                return null;
            }
        }
    }

    /// <summary>
    /// Creates the folder readable by this user (and SYSTEM) only, with inheritance from
    /// %LOCALAPPDATA% cut — defence in depth on top of DPAPI, for profiles whose AppData was
    /// redirected or loosened. Best effort: a folder whose permissions can't be changed still
    /// holds DPAPI-encrypted files only.
    /// </summary>
    private void EnsureDirectory()
    {
        Directory.CreateDirectory(_directory);
        if (_secured)
        {
            return;
        }

        _secured = true;
        try
        {
            new DirectoryInfo(_directory).SetAccessControl(PrivateSecurity());
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException or IdentityNotMappedException)
        {
        }
    }

    /// <summary>Full control for the current user and SYSTEM, inherited by everything inside; nothing else, nothing inherited from above.</summary>
    internal static DirectorySecurity PrivateSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var identity in new IdentityReference[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
        {
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            TryDelete(PathFor(key));
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
