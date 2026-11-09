// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json;
using System.Text.Json.Serialization;
using FileFluss.Core.Models;

namespace FileFluss.Core.Diagnostics;

/// <summary>
/// A saved set of test accounts with their credentials (macOS 1.6 <c>TestAccountSnapshot</c>),
/// so a debug build's accounts survive a reset and don't have to be added again before every
/// version test. Only debug builds read or write one.
///
/// <para>The credentials are the credential store's files as they are on disk — still
/// encrypted with DPAPI for this Windows user, so the snapshot is no easier to read than the
/// store itself.</para>
/// </summary>
public sealed record TestAccountSnapshot
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("savedAt")]
    public DateTimeOffset SavedAt { get; init; }

    [JsonPropertyName("accounts")]
    public List<CloudAccount> Accounts { get; init; } = [];

    /// <summary>Credential store file name → its (encrypted) bytes.</summary>
    [JsonPropertyName("credentials")]
    public Dictionary<string, byte[]> Credentials { get; init; } = [];

    /// <summary>
    /// The current accounts and credential files laid over an earlier snapshot: what's here
    /// wins, what's only in the snapshot stays — an account removed by accident doesn't vanish
    /// from the saved set as well. Without an earlier snapshot, just what's here.
    /// </summary>
    public static TestAccountSnapshot Merge(IReadOnlyList<CloudAccount> current, IReadOnlyDictionary<string, byte[]> credentials, TestAccountSnapshot? existing, DateTimeOffset at)
    {
        var currentIds = current.Select(a => a.Id).ToHashSet();
        var accounts = current.Select(a => a with { IsConnected = false }).ToList();
        var merged = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (existing is not null)
        {
            accounts.AddRange(existing.Accounts.Where(a => !currentIds.Contains(a.Id)));
            foreach (var (name, data) in existing.Credentials)
            {
                merged[name] = data;
            }
        }

        foreach (var (name, data) in credentials)
        {
            merged[name] = data;
        }

        return new TestAccountSnapshot { SavedAt = at, Accounts = accounts, Credentials = merged };
    }

    /// <summary>The saved accounts that aren't among <paramref name="present"/>.</summary>
    public IReadOnlyList<CloudAccount> Missing(IEnumerable<Guid> present)
    {
        var here = present.ToHashSet();
        return Accounts.Where(a => !here.Contains(a.Id)).ToList();
    }

    /// <summary>
    /// A debug build's first launch: starts its own data folder from the release app's — the
    /// settings, the account list and the credentials — so the accounts used for testing are
    /// there. The release folder is only read. False when the debug folder already existed or
    /// there was nothing to start from.
    /// </summary>
    public static bool SeedDebugFolder(string releaseFolder, string debugFolder)
    {
        if (Directory.Exists(debugFolder) || !Directory.Exists(releaseFolder) ||
            string.Equals(Path.GetFullPath(releaseFolder), Path.GetFullPath(debugFolder), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var staging = debugFolder + ".seeding";
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);
            foreach (var name in new[] { "settings.json", "accounts.json" })
            {
                var source = Path.Combine(releaseFolder, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(staging, name));
                }
            }

            var credentials = Path.Combine(releaseFolder, "credentials");
            if (Directory.Exists(credentials))
            {
                var target = Directory.CreateDirectory(Path.Combine(staging, "credentials")).FullName;
                foreach (var file in Directory.EnumerateFiles(credentials, "*.bin"))
                {
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
                }
            }

            // All or nothing: a half-copied folder would never be seeded again.
            Directory.Move(staging, debugFolder);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SupportLogger.Shared.Log($"Debug data folder not seeded: {e.Message}", "App", LogLevel.Notice);
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }
    }
}
