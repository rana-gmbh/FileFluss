// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Globalization;
using System.Text.Json;

namespace FileFluss.Core.Providers.GitHub;

/// <summary>
/// Everything needed to talk to GitHub on the user's behalf: a personal access token —
/// classic (needs the <c>repo</c> scope to see private repositories) or fine-grained (needs
/// "Contents: Read-only") — and the login it belongs to, resolved at connect time so the
/// sidebar can label the account without a round trip. <see cref="ApiBaseUrl"/> is stored
/// although it is always github.com today: GitHub Enterprise Server differs only in that URL,
/// so an Enterprise account later needs no credential migration.
/// </summary>
internal sealed record GitHubCredentials(string Token, string Login, string? ApiBaseUrl = null, string? WebBaseUrl = null)
{
    public const string PublicApiBaseUrl = "https://api.github.com";
    public const string PublicWebBaseUrl = "https://github.com";

    public string EffectiveApiBaseUrl => string.IsNullOrEmpty(ApiBaseUrl) ? PublicApiBaseUrl : ApiBaseUrl.TrimEnd('/');
}

/// <summary>
/// One repository as the account overview needs it. <see cref="SizeKb"/> is GitHub's own
/// figure for the packed git repository — an order of magnitude, not a byte count.
/// </summary>
internal sealed record GitHubRepository(
    string Owner,
    string Name,
    bool IsPrivate,
    bool IsArchived,
    string DefaultBranch,
    DateTimeOffset PushedAt,
    long SizeKb)
{
    public string FullName => $"{Owner}/{Name}";

    /// <summary>
    /// GitHub reports kilobytes of packed git objects. A zipball of one branch is a different
    /// thing — usually smaller (no history), occasionally larger (no delta compression). Only
    /// ever an estimate: it feeds the size column and the free-space pre-flight (which then
    /// over-reserves, the safe direction), never a correctness decision.
    /// </summary>
    public long EstimatedBytes => SizeKb * 1024;
}

/// <summary>One entry of a repository's tree. <see cref="Size"/> is meaningful for blobs only.</summary>
internal sealed record GitHubTreeEntry(string Path, bool IsDirectory, long Size, string Sha)
{
    public string Name
    {
        get
        {
            var slash = Path.LastIndexOf('/');
            return slash < 0 ? Path : Path[(slash + 1)..];
        }
    }
}

/// <summary>
/// A repository's whole tree in one object. <see cref="IsTruncated"/> mirrors GitHub's own
/// flag: above roughly 100k entries the recursive listing is cut short, and a backup tool
/// quietly showing a partial repository would be worse than useless — so callers fall back
/// to per-directory listings.
/// </summary>
internal sealed record GitHubTree(IReadOnlyList<GitHubTreeEntry> Entries, bool IsTruncated)
{
    public static readonly GitHubTree Empty = new([], false);

    /// <summary>Entries directly inside <paramref name="directory"/> ("" for the repository root).</summary>
    public IReadOnlyList<GitHubTreeEntry> Children(string directory)
    {
        var prefix = directory.Length == 0 ? string.Empty : directory + "/";
        return Entries.Where(entry =>
        {
            if (!entry.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            var remainder = entry.Path.AsSpan(prefix.Length);
            return remainder.Length > 0 && !remainder.Contains('/');
        }).ToList();
    }

    /// <summary>Total bytes of every blob at or below <paramref name="directory"/> — exact, the tree carries every blob's size.</summary>
    public long RecursiveSize(string directory)
    {
        var prefix = directory.Length == 0 ? string.Empty : directory + "/";
        return Entries.Where(e => !e.IsDirectory && e.Path.StartsWith(prefix, StringComparison.Ordinal)).Sum(e => e.Size);
    }
}

/// <summary>
/// Turns GitHub's JSON into the shapes the provider uses. Separate from the client so the
/// formats can be pinned by tests: the fields that matter here (private, default branch, push
/// date, entry type) are easy to regress on silently.
/// </summary>
internal static class GitHubResponseParser
{
    public static IReadOnlyList<GitHubRepository> Repositories(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw CloudProviderException.InvalidResponse();
            }

            return document.RootElement.EnumerateArray().Select(Repository).ToList();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    public static GitHubRepository Repository(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Repository(document.RootElement);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    private static GitHubRepository Repository(JsonElement payload)
    {
        // name, owner.login and private are required, as in the macOS payload type; a
        // missing one throws and the whole response counts as invalid.
        var name = payload.GetProperty("name").GetString() ?? throw CloudProviderException.InvalidResponse();
        var owner = payload.GetProperty("owner").GetProperty("login").GetString() ?? throw CloudProviderException.InvalidResponse();
        var isPrivate = payload.GetProperty("private").GetBoolean();

        // A repository with no commits has no push date; falling back keeps date-based sync
        // comparisons from seeing a 1970 timestamp and deciding everything is ancient.
        var timestamp = new[] { "pushed_at", "updated_at", "created_at" }
            .Select(key => ParseDate(String(payload, key)))
            .FirstOrDefault(date => date is not null);

        return new GitHubRepository(
            owner,
            name,
            isPrivate,
            payload.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True,
            String(payload, "default_branch") ?? "main",
            timestamp ?? Models.CloudFileItem.UnknownDate,
            Int(payload, "size") ?? 0);
    }

    public static GitHubTree Tree(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw CloudProviderException.InvalidResponse();
            }

            var entries = new List<GitHubTreeEntry>();
            if (root.TryGetProperty("tree", out var tree) && tree.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in tree.EnumerateArray())
                {
                    var path = entry.GetProperty("path").GetString() ?? throw CloudProviderException.InvalidResponse();
                    var type = entry.GetProperty("type").GetString();

                    // Submodules ("commit") and symlinks have no content the Contents API will
                    // serve; listing them would offer a download that could only fail.
                    if (type is not ("blob" or "tree"))
                    {
                        continue;
                    }

                    entries.Add(new GitHubTreeEntry(path, type == "tree", Int(entry, "size") ?? 0, String(entry, "sha") ?? path));
                }
            }

            return new GitHubTree(entries, root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw CloudProviderException.InvalidResponse();
        }
    }

    /// <summary>
    /// One directory from the Contents API. A single file answers with an object rather than
    /// an array — the caller asked for a blob as if it were a folder — so that returns null.
    /// </summary>
    public static IReadOnlyList<GitHubTreeEntry>? ContentsListing(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return document.RootElement.EnumerateArray().Select(entry =>
            {
                var path = entry.GetProperty("path").GetString() ?? throw CloudProviderException.InvalidResponse();
                return new GitHubTreeEntry(path, String(entry, "type") == "dir", Int(entry, "size") ?? 0, String(entry, "sha") ?? path);
            }).ToList();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The <c>login</c> of a <c>/user</c> response, or null.</summary>
    public static string? Login(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? String(document.RootElement, "login") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static DateTimeOffset? ParseDate(string? raw)
        => !string.IsNullOrEmpty(raw) && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;
}
