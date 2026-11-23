// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Native.Tests;

/// <summary>The updater's Authenticode check, against real files.</summary>
public sealed class AuthenticodeVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-authenticode-" + Guid.NewGuid().ToString("N"));

    public AuthenticodeVerifierTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AnUnsignedFileIsRefused()
    {
        var path = Path.Combine(_root, "FileFluss-Setup-9.9.9-x64.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 1, 2, 3, 4]);
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var refusal = AuthenticodeVerifier.CheckUpdateInstaller(path, hold.SafeFileHandle);

        Assert.NotNull(refusal);
    }

    [Fact]
    public void AValidSignatureFromAnotherPublisherIsRefused()
    {
        // The runtime's own CoreLib carries a valid Microsoft signature: WinVerifyTrust accepts
        // it, the Rana GmbH pin must not.
        var path = typeof(object).Assembly.Location;
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Null(AuthenticodeVerifier.Verify(path, hold.SafeFileHandle, out var signer));
        Assert.NotNull(signer);
        Assert.Contains("Microsoft", signer!.Subject, StringComparison.Ordinal);

        var refusal = AuthenticodeVerifier.CheckUpdateInstaller(path, hold.SafeFileHandle);
        Assert.NotNull(refusal);
        Assert.Contains("Microsoft", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ATamperedSignedFileIsRefused()
    {
        var path = Path.Combine(_root, "tampered.dll");
        var bytes = File.ReadAllBytes(typeof(object).Assembly.Location);
        bytes[bytes.Length / 3] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.NotNull(AuthenticodeVerifier.Verify(path, hold.SafeFileHandle, out _));
    }

    [Fact]
    public void ARanaGmbHSignedFileIsAccepted()
    {
        // Only where a signed FileFluss is installed (as on the release-testing PC); elsewhere
        // there is no Rana GmbH-signed file to try.
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FileFluss", "FileFluss.exe");
        if (!File.Exists(installed))
        {
            return;
        }

        using var hold = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Null(AuthenticodeVerifier.CheckUpdateInstaller(installed, hold.SafeFileHandle));
    }
}
