// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Net;

namespace FileFluss.Core.Net;

/// <summary>Server addresses as users type them into Add Account.</summary>
public static class ServerAddress
{
    /// <summary>
    /// Whether the URL names a port (<c>host:8443</c>). Read from the text, not from
    /// <see cref="Uri.Port"/>: an explicit ":443" must count as typed, which <see cref="Uri"/>
    /// can't tell apart from no port at all.
    /// </summary>
    public static bool HasExplicitPort(string url)
    {
        var start = url.IndexOf("://", StringComparison.Ordinal);
        var authority = start < 0 ? url : url[(start + 3)..];
        var end = authority.IndexOfAny(['/', '?', '#']);
        authority = end < 0 ? authority : authority[..end];
        var at = authority.LastIndexOf('@');
        authority = at < 0 ? authority : authority[(at + 1)..];
        var bracket = authority.LastIndexOf(']');
        return authority.IndexOf(':', bracket + 1) >= 0;
    }

    /// <summary>
    /// <paramref name="url"/> (which has a scheme) with <paramref name="port"/> inserted after
    /// the host when it names none; anything after the host is kept.
    /// </summary>
    public static string WithDefaultPort(string url, int port)
    {
        if (HasExplicitPort(url))
        {
            return url;
        }

        var authorityStart = url.IndexOf("://", StringComparison.Ordinal);
        authorityStart = authorityStart < 0 ? 0 : authorityStart + 3;
        var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
        authorityEnd = authorityEnd < 0 ? url.Length : authorityEnd;
        return url[..authorityEnd] + ":" + port.ToString(CultureInfo.InvariantCulture) + url[authorityEnd..];
    }

    /// <summary>
    /// Whether a password sent to <paramref name="address"/> would cross a network in the
    /// clear (the macOS <c>CredentialScope.exposesPassword</c>). Addresses on this PC or the
    /// local network don't count: home NAS units are often reached over plain HTTP, and
    /// that's a fair choice.
    /// </summary>
    public static bool ExposesPassword(string? address)
    {
        var trimmed = (address ?? string.Empty).Trim().ToLowerInvariant();
        if (!trimmed.StartsWith("http://", StringComparison.Ordinal)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Host.Length == 0)
        {
            return false;
        }

        var host = uri.Host;
        if (host == "localhost" || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".localhost", StringComparison.Ordinal))
        {
            return false;
        }

        var octets = host.Split('.');
        if (octets.Length != 4 || !octets.All(o => int.TryParse(o, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            return true;
        }

        var bytes = ip.GetAddressBytes();
        return (bytes[0], bytes[1]) switch
        {
            (10, _) or (127, _) or (192, 168) or (169, 254) => false,
            (172, >= 16 and <= 31) => false,
            _ => true,
        };
    }
}
