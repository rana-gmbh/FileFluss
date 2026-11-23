// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using FileFluss.Core.Updates;
using Microsoft.Win32.SafeHandles;

namespace FileFluss.Native;

/// <summary>
/// Authenticode checks for downloaded updates: WinVerifyTrust decides whether the embedded
/// signature is valid and chains to a trusted root (a timestamped signature stays valid after
/// the short-lived Artifact Signing certificate expires), and <see cref="UpdateSigner"/>
/// decides whether the certificate WinVerifyTrust verified is Rana GmbH's.
/// </summary>
public static class AuthenticodeVerifier
{
    /// <summary>The updater's check (an <see cref="InstallerSignatureCheck"/>): null when signed by Rana GmbH, else the reason.</summary>
    public static string? CheckUpdateInstaller(string path, SafeFileHandle file)
        => Verify(path, file, out var signer) is { } failure
            ? failure
            : signer is not null && UpdateSigner.IsExpectedPublisher(signer)
                ? null
                : $"it is signed by \"{signer?.Subject}\" (issued by \"{signer?.Issuer}\")";

    /// <summary>
    /// Verifies <paramref name="path"/>'s embedded Authenticode signature, reading through
    /// <paramref name="file"/>. Null when valid, with the signing certificate in
    /// <paramref name="signer"/>; otherwise why not.
    ///
    /// <para>Revocation isn't checked: a CRL or OCSP lookup can hang for a long time behind
    /// a captive portal or a strict proxy, and the certificates are only valid for days anyway.</para>
    /// </summary>
    public static string? Verify(string path, SafeFileHandle? file, out X509Certificate2? signer)
    {
        signer = null;
        var fileInfo = new WinTrustFileInfo
        {
            Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path,
            File = file?.DangerousGetHandle() ?? 0,
        };

        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var added = false;
        try
        {
            file?.DangerousAddRef(ref added);
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = WtdStateActionVerify,
                ProvFlags = WtdRevocationCheckNone,
            };

            var action = GenericVerifyV2;
            var result = WinVerifyTrust(InvalidWindow, ref action, ref data);
            try
            {
                if (result != 0)
                {
                    return Describe(result);
                }

                signer = SignerOf(data.StateData);
                return signer is null ? "its signing certificate could not be read" : null;
            }
            finally
            {
                data.StateAction = WtdStateActionClose;
                WinVerifyTrust(InvalidWindow, ref action, ref data);
            }
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
            if (added)
            {
                file!.DangerousRelease();
            }
        }
    }

    /// <summary>The leaf certificate of the first signer WinVerifyTrust verified.</summary>
    private static X509Certificate2? SignerOf(nint stateData)
    {
        var provider = WTHelperProvDataFromStateData(stateData);
        if (provider == 0)
        {
            return null;
        }

        var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == 0)
        {
            return null;
        }

        var chain = Marshal.PtrToStructure<CryptProviderSigner>(signer);
        if (chain.CertificateCount == 0 || chain.Certificates == 0)
        {
            return null;
        }

        var leaf = Marshal.PtrToStructure<CryptProviderCertificate>(chain.Certificates);
        return leaf.Certificate == 0 ? null : new X509Certificate2(leaf.Certificate);
    }

    private static string Describe(int result) => (uint)result switch
    {
        0x800B0100 => "it has no signature",                              // TRUST_E_NOSIGNATURE
        0x80096010 => "its signature doesn't match its contents",         // TRUST_E_BAD_DIGEST
        0x800B0109 => "its certificate doesn't chain to a trusted root",  // CERT_E_UNTRUSTEDROOT
        0x800B0101 => "its certificate has expired",                      // CERT_E_EXPIRED
        0x800B0111 => "its publisher is explicitly distrusted",           // TRUST_E_EXPLICIT_DISTRUST
        _ => $"WinVerifyTrust failed with 0x{result:X8} ({new Win32Exception(result).Message})",
    };

    // MARK: - Interop

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x10;
    private static readonly nint InvalidWindow = -1;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public nint File;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    /// <summary>The start of CRYPT_PROVIDER_SGNR: cbStruct, sftVerifyAsOf (FILETIME), csCertChain, pasCertChain.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        public uint Size;
        public uint VerifyAsOfLow;
        public uint VerifyAsOfHigh;
        public uint CertificateCount;
        public nint Certificates;
    }

    /// <summary>The start of CRYPT_PROVIDER_CERT: cbStruct, pCert.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificate
    {
        public uint Size;
        public nint Certificate;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref WinTrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(nint providerData, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);
}
