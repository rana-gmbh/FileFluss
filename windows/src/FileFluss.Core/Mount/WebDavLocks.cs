// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Mount;

/// <summary>One granted lock.</summary>
internal sealed record DavLock(string Token, string ProviderPath, bool Exclusive, string Depth, string? OwnerXml, int TimeoutSeconds, DateTimeOffset Expires);

/// <summary>
/// Bookkeeping for LOCK/UNLOCK. The locks are advisory and never enforced: the only client
/// of a mount is the user's own WebDAV redirector, and it serialises writes itself.
///
/// <para>They still have to exist. Windows' redirector (with the default
/// <c>SupportLocking=1</c>) takes an exclusive write lock before writing a file and refreshes
/// it while the file stays open; Office takes one before opening a document for editing and
/// opens read-only when it can't get one. macOS answered LOCK with a fresh token and
/// forgot it; here the token is remembered, so a refresh (LOCK with an <c>If</c> header and
/// no body) returns the same token and <c>lockdiscovery</c> reports it.</para>
/// </summary>
internal sealed class WebDavLocks
{
    /// <summary>The longest lock handed out — macOS' stub always said one hour.</summary>
    public const int MaxTimeoutSeconds = 3600;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DavLock> _byToken = new(StringComparer.Ordinal);

    public DavLock Grant(string providerPath, bool exclusive, string depth, string? ownerXml, int timeoutSeconds)
    {
        var davLock = new DavLock(
            "urn:uuid:" + Guid.NewGuid().ToString("D"),
            providerPath,
            exclusive,
            depth,
            ownerXml,
            timeoutSeconds,
            DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds));
        lock (_gate)
        {
            Purge();
            _byToken[davLock.Token] = davLock;
        }

        return davLock;
    }

    /// <summary>Extends a lock named in an <c>If</c> header; null when the token is unknown or expired.</summary>
    public DavLock? Refresh(string token, int timeoutSeconds)
    {
        lock (_gate)
        {
            Purge();
            if (!_byToken.TryGetValue(token, out var existing))
            {
                return null;
            }

            var refreshed = existing with { TimeoutSeconds = timeoutSeconds, Expires = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds) };
            _byToken[token] = refreshed;
            return refreshed;
        }
    }

    public bool Release(string token)
    {
        lock (_gate)
        {
            return _byToken.Remove(token);
        }
    }

    public IReadOnlyList<DavLock> For(string providerPath)
    {
        lock (_gate)
        {
            Purge();
            return _byToken.Values.Where(l => l.ProviderPath == providerPath).ToList();
        }
    }

    /// <summary>A deleted or moved item takes its locks (and those beneath it) with it.</summary>
    public void ReleaseTree(string providerPath)
    {
        lock (_gate)
        {
            foreach (var token in _byToken.Values.Where(l => IO.CloudPath.IsWithin(l.ProviderPath, providerPath)).Select(l => l.Token).ToList())
            {
                _byToken.Remove(token);
            }
        }
    }

    /// <summary><c>Timeout: Second-600, Infinite</c> → the first usable value, capped at an hour.</summary>
    public static int ParseTimeout(string? header)
    {
        foreach (var part in (header ?? string.Empty).Split(','))
        {
            var value = part.Trim();
            if (value.StartsWith("Second-", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value["Second-".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                return Math.Min(seconds, MaxTimeoutSeconds);
            }

            if (value.Equals("Infinite", StringComparison.OrdinalIgnoreCase))
            {
                return MaxTimeoutSeconds;
            }
        }

        return MaxTimeoutSeconds;
    }

    /// <summary>The lock tokens in an <c>If</c> header: <c>(&lt;urn:uuid:…&gt;)</c>.</summary>
    public static IEnumerable<string> TokensInIfHeader(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            yield break;
        }

        var index = 0;
        while ((index = header.IndexOf('<', index)) >= 0)
        {
            var end = header.IndexOf('>', index);
            if (end < 0)
            {
                yield break;
            }

            var token = header[(index + 1)..end];
            if (token.StartsWith("opaquelocktoken:", StringComparison.Ordinal) || token.StartsWith("urn:uuid:", StringComparison.Ordinal))
            {
                yield return token;
            }

            index = end + 1;
        }
    }

    private void Purge()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var token in _byToken.Values.Where(l => l.Expires < now).Select(l => l.Token).ToList())
        {
            _byToken.Remove(token);
        }
    }
}
