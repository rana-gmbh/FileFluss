// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FileFluss.Core.Diagnostics;

namespace FileFluss.Core.Net;

/// <summary>
/// "Allow self-signed certificate", as trust on first use. A certificate that validates
/// normally is always accepted. One that doesn't is accepted only from the host the user
/// typed, and only while it is the certificate seen the first time: its public key is pinned
/// then (SHA-256 of the SubjectPublicKeyInfo, so a NAS renewing its certificate with the same
/// key stays trusted) and a different one is refused with <see cref="CertificateChangedException"/>.
///
/// <para>Accepting any certificate for that host, as before, let anyone on the network path to a
/// NAS reached by its public name present their own and read the password. SFTP host keys are
/// handled the same way (<c>SftpHostKeyCheck</c>).</para>
///
/// <para>Thread-safe: every connection of an HttpClient (or every FTP data channel) asks.</para>
/// </summary>
public sealed class SelfSignedTrust
{
    private readonly Lock _gate = new();
    private readonly Action<string>? _pinned;
    private string? _pin;
    private int _refusals;

    /// <param name="host">The host the exemption applies to; any other host must validate normally.</param>
    /// <param name="pin">The fingerprint trusted so far ("SHA256:…"); null trusts the first certificate seen.</param>
    /// <param name="pinned">Called once with the fingerprint when the first certificate is pinned, to store it.</param>
    public SelfSignedTrust(string host, string? pin, Action<string>? pinned = null)
    {
        Host = host;
        _pin = string.IsNullOrEmpty(pin) ? null : pin;
        _pinned = pinned;
    }

    public string Host { get; }

    /// <summary>The fingerprint trusted now: the stored one, or the first one seen.</summary>
    public string? Pin
    {
        get
        {
            lock (_gate)
            {
                return _pin;
            }
        }
    }

    /// <summary>The fingerprint of the last certificate refused as changed; null when none was.</summary>
    public string? Refused { get; private set; }

    /// <summary>How many times a changed certificate was refused, so a caller can tell whether its own connection was.</summary>
    public int Refusals => Volatile.Read(ref _refusals);

    /// <summary>A certificate's public-key fingerprint, "SHA256:" + Base64 (no padding), as SSH shows host keys.</summary>
    public static string Fingerprint(X509Certificate certificate)
    {
        using var certificate2 = new X509Certificate2(certificate);
        var hash = SHA256.HashData(certificate2.PublicKey.ExportSubjectPublicKeyInfo());
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    /// <summary>What to do with a certificate (pure: the decision only).</summary>
    public static TrustDecision Decide(SslPolicyErrors errors, bool isTrustedHost, string? pin, string? presented)
    {
        if (errors == SslPolicyErrors.None)
        {
            return TrustDecision.Accept;
        }

        if (!isTrustedHost || presented is null)
        {
            return TrustDecision.Refuse;
        }

        if (pin is null)
        {
            return TrustDecision.AcceptAndPin;
        }

        return string.Equals(pin, presented, StringComparison.Ordinal) ? TrustDecision.Accept : TrustDecision.Changed;
    }

    /// <summary>
    /// The TLS validation callback's answer for a connection to <paramref name="targetHost"/>.
    /// A changed certificate throws <see cref="CertificateChangedException"/>, which comes back
    /// inside the request's exception and becomes a specific error in <c>CloudProviderException.Network</c>.
    /// </summary>
    public bool Validate(string? targetHost, X509Certificate? certificate, SslPolicyErrors errors)
        => Check(targetHost, certificate, errors, throwOnChange: true);

    /// <summary>Like <see cref="Validate"/>, but a changed certificate only answers false (and sets <see cref="Refused"/>).</summary>
    public bool Accepts(string? targetHost, X509Certificate? certificate, SslPolicyErrors errors)
        => Check(targetHost, certificate, errors, throwOnChange: false);

    private bool Check(string? targetHost, X509Certificate? certificate, SslPolicyErrors errors, bool throwOnChange)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        var isTrustedHost = targetHost is not null && string.Equals(targetHost, Host, StringComparison.OrdinalIgnoreCase);
        var presented = certificate is null ? null : Fingerprint(certificate);
        string? pinnedNow = null;
        string? previous;
        TrustDecision decision;
        lock (_gate)
        {
            previous = _pin;
            decision = Decide(errors, isTrustedHost, _pin, presented);
            if (decision == TrustDecision.AcceptAndPin)
            {
                _pin = presented;
                pinnedNow = presented;
            }
            else if (decision == TrustDecision.Changed)
            {
                Refused = presented;
                _refusals++;
            }
        }

        if (pinnedNow is not null)
        {
            SupportLogger.Shared.Log($"Trusting the self-signed certificate of {Host} from now on: {pinnedNow}", "TLS");
            try
            {
                _pinned?.Invoke(pinnedNow);
            }
            catch (Exception e)
            {
                // The connection may go on; the next launch pins again.
                SupportLogger.Shared.Log($"The certificate of {Host} could not be remembered: {e.Message}", "TLS", LogLevel.Error);
            }
        }

        if (decision == TrustDecision.Changed)
        {
            SupportLogger.Shared.Log($"Refused the certificate of {Host}: it changed from {previous} to {presented}", "TLS", LogLevel.Error);
            if (throwOnChange)
            {
                throw new CertificateChangedException(Host, previous!, presented!);
            }
        }

        return decision is TrustDecision.Accept or TrustDecision.AcceptAndPin;
    }
}

public enum TrustDecision
{
    Accept,

    /// <summary>Accept, and remember this certificate as the one to trust.</summary>
    AcceptAndPin,

    Refuse,

    /// <summary>The host's certificate isn't the one trusted before.</summary>
    Changed,
}

/// <summary>A server presented a different untrusted certificate than the one pinned for it.</summary>
public sealed class CertificateChangedException(string host, string previous, string presented)
    : System.Security.Authentication.AuthenticationException($"The certificate of {host} changed from {previous} to {presented}.")
{
    public string Host { get; } = host;

    public string Previous { get; } = previous;

    public string Presented { get; } = presented;
}
