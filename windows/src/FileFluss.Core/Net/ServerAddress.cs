// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;

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
}
