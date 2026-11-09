// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Net.Sockets;

namespace FileFluss.Core.Net;

/// <summary>Decides where login details may go (macOS <c>CredentialScope</c>).</summary>
public static class CredentialScope
{
    /// <summary>
    /// Whether a request to <paramref name="target"/> may carry credentials meant for
    /// <paramref name="origin"/>: the same host and port, over a connection no weaker than the
    /// original. Moving up from http to https is fine (on the standard ports); the other way
    /// round would send the password in the clear.
    /// </summary>
    public static bool Allows(Uri? target, Uri? origin)
    {
        if (target is not { IsAbsoluteUri: true } || origin is not { IsAbsoluteUri: true } ||
            !string.Equals(target.Host, origin.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var targetScheme = target.Scheme.ToLowerInvariant();
        var originScheme = origin.Scheme.ToLowerInvariant();
        return (originScheme, targetScheme) switch
        {
            ("https", "https") or ("http", "http") => target.Port == origin.Port,
            ("http", "https") => origin.Port == 80 && target.Port == 443,
            _ => false,
        };
    }

    /// <summary>
    /// Whether a password sent to <paramref name="address"/> would cross a network in the
    /// clear. Addresses on this PC or the local network don't count: home NAS units are often
    /// reached over plain HTTP, and that's a fair choice.
    /// </summary>
    public static bool ExposesPassword(string address)
    {
        var trimmed = address.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host == "localhost" || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".localhost", StringComparison.Ordinal))
        {
            return false;
        }

        if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            // A name (or an IPv6 literal, which the Mac rule doesn't recognise either).
            return true;
        }

        var octets = ip.GetAddressBytes();
        return (octets[0], octets[1]) switch
        {
            (10, _) or (127, _) or (192, 168) or (169, 254) => false,
            (172, >= 16 and <= 31) => false,
            _ => true,
        };
    }
}
