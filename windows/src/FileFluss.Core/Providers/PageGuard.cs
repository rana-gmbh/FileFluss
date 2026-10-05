// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;

namespace FileFluss.Core.Providers;

/// <summary>
/// Keeps a paginated listing finite. A server that answers "there is more" with a cursor it
/// already gave (or none at all), or that ignores the page number, would otherwise keep a
/// listing — and the panel, sync or indexer waiting on it — going for ever. Each cursor may
/// be followed once, and no listing runs past <see cref="MaxPages"/> pages.
/// </summary>
internal sealed class PageGuard(string listing)
{
    /// <summary>Far beyond any real folder (10 million entries at 100 per page).</summary>
    public const int MaxPages = 100_000;

    private readonly HashSet<string> _cursors = new(StringComparer.Ordinal);
    private int _pages;

    /// <summary>Call before following <paramref name="cursor"/>; throws when the listing has stopped making progress.</summary>
    public void Follow(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor) || !_cursors.Add(cursor))
        {
            SupportLogger.Shared.Log($"{listing}: the server repeated a page cursor; stopping the listing", "Providers", LogLevel.Error);
            throw CloudProviderException.InvalidResponse();
        }

        Count();
    }

    /// <summary>Call for each page of a numbered listing.</summary>
    public void Count()
    {
        if (++_pages > MaxPages)
        {
            SupportLogger.Shared.Log($"{listing}: more than {MaxPages} pages; stopping the listing", "Providers", LogLevel.Error);
            throw CloudProviderException.InvalidResponse();
        }
    }
}
