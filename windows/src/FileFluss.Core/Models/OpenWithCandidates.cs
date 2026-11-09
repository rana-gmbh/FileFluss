// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Core.Models;

/// <summary>One entry of the Open With submenu.</summary>
/// <typeparam name="T">Whatever the platform describes an app with.</typeparam>
public sealed record OpenWithCandidate<T>(T App, string Id, string Name, bool IsDefault);

/// <summary>
/// Which apps right-click → Open With offers, and in what order — macOS
/// <c>OpenWithCandidates.rank</c>: only apps that open <em>every</em> item of the selection,
/// the default first (when every item agrees it can open them), the rest by name, and a
/// second copy of the same app (an old version beside the new one) listed once. Split out
/// from the shell lookups so the rules can be tested on their own.
/// </summary>
public static class OpenWithCandidates
{
    /// <param name="perItem">The apps for each item (or each distinct type) of the selection.</param>
    /// <param name="defaultId">The first item's default app, or null.</param>
    /// <param name="id">An app's identity — the same app answers with the same id for every type. Compared ignoring case (Windows paths).</param>
    /// <param name="name">An app's display name.</param>
    public static IReadOnlyList<OpenWithCandidate<T>> Rank<T>(
        IReadOnlyList<IReadOnlyList<T>> perItem, string? defaultId, Func<T, string> id, Func<T, string> name)
    {
        if (perItem.Count == 0)
        {
            return [];
        }

        // Keyed by identity; the first item's description of each app is the one shown.
        var common = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in perItem[0])
        {
            common.TryAdd(id(app), app);
        }

        foreach (var apps in perItem.Skip(1))
        {
            var ids = new HashSet<string>(apps.Select(id), StringComparer.OrdinalIgnoreCase);
            foreach (var key in common.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                common.Remove(key);
            }
        }

        // The default only leads when every item agrees it can open them.
        T? leader = default;
        var hasLeader = defaultId is not null && common.TryGetValue(defaultId, out leader);

        var seenNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var result = new List<OpenWithCandidate<T>>();
        if (hasLeader && leader is not null)
        {
            seenNames.Add(name(leader));
            result.Add(new OpenWithCandidate<T>(leader, id(leader), name(leader), IsDefault: true));
        }

        foreach (var (key, app) in common.OrderBy(pair => name(pair.Value), StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (hasLeader && string.Equals(key, defaultId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var appName = name(app);
            if (!seenNames.Add(appName))
            {
                continue;
            }

            result.Add(new OpenWithCandidate<T>(app, key, appName, IsDefault: false));
        }

        return result;
    }

    /// <summary>
    /// The distinct types of a selection, as the shell asks about them: ".txt" for
    /// "notes.TXT" (lower-cased, so "a.TXT" and "b.txt" are one lookup), and an empty string
    /// for a name without an extension — which no app claims.
    /// </summary>
    public static IReadOnlyList<string> Extensions(IEnumerable<string> names)
        => names.Select(n => Path.GetExtension(n).ToLowerInvariant())
            .Select(e => e == "." ? string.Empty : e)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
