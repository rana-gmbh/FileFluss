// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace FileFluss.Core.Updates;

/// <summary>
/// Checks a downloaded installer's code signature through <paramref name="file"/>, a handle
/// that keeps it from being changed meanwhile. Returns null when it is validly signed by
/// FileFluss's publisher, otherwise why not. (The Windows app checks with WinVerifyTrust; Core
/// stays platform-neutral.)
/// </summary>
public delegate string? InstallerSignatureCheck(string path, SafeFileHandle file);

/// <summary>
/// Who FileFluss releases are signed by. The SHA256SUMS.txt match alone only proves the
/// installer is the one the release lists, and both come from the same GitHub release: anyone
/// able to change a release could replace both. Sparkle refuses a Mac update without the EdDSA
/// signature only Rana GmbH can make; here the installer must also carry a valid Authenticode
/// signature from Rana GmbH's Azure Artifact Signing identity.
///
/// <para>Artifact Signing certificates last days, not years, and are renewed constantly, so the
/// pin is on who they are issued to (and by), never on a thumbprint:
/// "CN=Rana GmbH, O=Rana GmbH, L=04275 Leipzig, S=Saxony, C=DE", issued by one of Microsoft's
/// "Microsoft ID Verified CS …" CAs. The town is part of it because the company name alone is
/// not unique. If the company's registered details change, the signing identity changes with
/// them: update this pin in a release signed with the old identity first.</para>
/// </summary>
public static class UpdateSigner
{
    public const string PublisherName = "Rana GmbH";

    private const string CommonNameOid = "2.5.4.3";
    private const string OrganizationOid = "2.5.4.10";
    private const string LocalityOid = "2.5.4.7";
    private const string CountryOid = "2.5.4.6";

    private const string ExpectedLocality = "Leipzig";
    private const string ExpectedCountry = "DE";
    private const string ExpectedIssuerPrefix = "Microsoft ID Verified CS ";

    /// <summary>Whether the (already validly verified) signing certificate is FileFluss's publisher's.</summary>
    public static bool IsExpectedPublisher(X509Certificate2 signer)
        => IsExpectedSubject(signer.SubjectName) && IsExpectedIssuer(signer.IssuerName);

    /// <summary>The subject part of the pin: CN and O exactly "Rana GmbH", in Leipzig, Germany.</summary>
    public static bool IsExpectedSubject(string subject) => TryParse(subject) is { } name && IsExpectedSubject(name);

    /// <summary>The issuer part of the pin: one of Microsoft's identity-verified code signing CAs (Artifact Signing).</summary>
    public static bool IsExpectedIssuer(string issuer) => TryParse(issuer) is { } name && IsExpectedIssuer(name);

    public static bool IsExpectedSubject(X500DistinguishedName subject)
    {
        if (Attributes(subject) is not { } attributes)
        {
            return false;
        }

        return Single(attributes, CommonNameOid) == PublisherName &&
               Single(attributes, OrganizationOid) == PublisherName &&
               Single(attributes, CountryOid) == ExpectedCountry &&
               Single(attributes, LocalityOid) is { } locality &&
               (locality == ExpectedLocality || locality.EndsWith(" " + ExpectedLocality, StringComparison.Ordinal));
    }

    public static bool IsExpectedIssuer(X500DistinguishedName issuer)
        => Attributes(issuer) is { } attributes &&
           Single(attributes, CommonNameOid) is { } name &&
           name.StartsWith(ExpectedIssuerPrefix, StringComparison.Ordinal);

    private static X500DistinguishedName? TryParse(string name)
    {
        try
        {
            return new X500DistinguishedName(name);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Every attribute as (OID, value); null for a name with multi-valued parts, which a genuine signer never has.</summary>
    private static List<(string Oid, string? Value)>? Attributes(X500DistinguishedName name)
    {
        var list = new List<(string, string?)>();
        foreach (var part in name.EnumerateRelativeDistinguishedNames())
        {
            if (part.HasMultipleElements)
            {
                return null;
            }

            list.Add((part.GetSingleElementType().Value ?? string.Empty, part.GetSingleElementValue()));
        }

        return list;
    }

    /// <summary>The attribute's value when it appears exactly once; null when it is missing or repeated.</summary>
    private static string? Single(List<(string Oid, string? Value)> attributes, string oid)
    {
        string? found = null;
        var count = 0;
        foreach (var (key, value) in attributes)
        {
            if (key == oid)
            {
                found = value;
                count++;
            }
        }

        return count == 1 ? found : null;
    }
}
