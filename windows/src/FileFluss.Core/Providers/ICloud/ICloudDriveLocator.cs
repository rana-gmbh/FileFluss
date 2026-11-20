// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Diagnostics;

namespace FileFluss.Core.Providers.ICloud;

/// <summary>
/// Read-only registry access for <see cref="ICloudDriveLocator"/>. Core is plain
/// <c>net10.0</c> and does not reference <c>Microsoft.Win32.Registry</c>, so the app installs
/// an implementation in <see cref="ICloudDriveLocator.Registry"/>. Implementations return
/// empty results for missing keys and may throw for anything else — the locator treats every
/// failure as "nothing recorded there".
/// </summary>
public interface IRegistryReader
{
    /// <summary>Names of the subkeys of <paramref name="keyPath"/>, a full path with its hive, e.g. <c>HKEY_CURRENT_USER\Software\Apple Inc.</c>.</summary>
    IReadOnlyList<string> GetSubKeyNames(string keyPath);

    /// <summary>The key's REG_SZ / REG_EXPAND_SZ values by name (unexpanded is fine; the default value under ""). Empty when the key is missing.</summary>
    IReadOnlyDictionary<string, string> GetStringValues(string keyPath);
}

/// <summary>
/// Finds the folder iCloud for Windows syncs iCloud Drive into. There is no iCloud API on
/// Windows: iCloud for Windows registers iCloud Drive as a Cloud Files sync root — usually
/// <c>%USERPROFILE%\iCloudDrive</c>, but the location can differ between versions and setups,
/// so every place Windows or iCloud records it is consulted before the default:
/// <list type="number">
/// <item>the Explorer navigation-pane entry named "iCloud…" (HKCU, so always this user's),
/// whose <c>TargetFolderPath</c> is the folder;</item>
/// <item>any path value under <c>HKCU\Software\Apple Inc.</c> that names iCloud Drive;</item>
/// <item>the Cloud Files sync-root registration (<c>SyncRootManager\iCloud…\UserSyncRoots</c>),
/// limited to this user's SID;</item>
/// <item><c>%USERPROFILE%\iCloudDrive</c>, then <c>%USERPROFILE%\iCloud Drive</c>.</item>
/// </list>
/// The first candidate that exists wins. Without a registry reader only the defaults are tried.
/// </summary>
public static class ICloudDriveLocator
{
    internal const string NavigationPaneKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace";
    internal const string ClassesClsidKey = @"HKEY_CURRENT_USER\Software\Classes\CLSID";
    internal const string AppleKey = @"HKEY_CURRENT_USER\Software\Apple Inc.";
    internal const string SyncRootManagerKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";
    internal const string ProfileListKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    private const int AppleSearchDepth = 4;
    private const int AppleSearchKeyLimit = 200;

    /// <summary>Registry access, installed by the app at startup. Null: the registry is not consulted.</summary>
    public static IRegistryReader? Registry { get; set; }

    /// <summary>The iCloud Drive folder, or null when iCloud for Windows isn't set up.</summary>
    public static string? FindRoot()
        => FindRoot(Registry, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Directory.Exists);

    internal static string? FindRoot(IRegistryReader? registry, string userProfile, Func<string, bool> directoryExists)
    {
        foreach (var candidate in Candidates(registry, userProfile))
        {
            try
            {
                if (Path.IsPathFullyQualified(candidate) && directoryExists(candidate))
                {
                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
                }
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }

        return null;
    }

    internal static IEnumerable<string> Candidates(IRegistryReader? registry, string userProfile)
    {
        var candidates = new List<string>();
        if (registry is not null)
        {
            candidates.AddRange(Safely(() => NavigationPaneCandidates(registry)));
            candidates.AddRange(Safely(() => AppleCandidates(registry)));
            candidates.AddRange(Safely(() => SyncRootCandidates(registry, userProfile)));
        }

        if (userProfile.Length > 0)
        {
            candidates.Add(Path.Combine(userProfile, "iCloudDrive"));
            candidates.Add(Path.Combine(userProfile, "iCloud Drive"));
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> NavigationPaneCandidates(IRegistryReader registry)
    {
        var results = new List<string>();
        foreach (var clsid in registry.GetSubKeyNames(NavigationPaneKey))
        {
            var values = registry.GetStringValues($@"{NavigationPaneKey}\{clsid}");
            if (!values.TryGetValue(string.Empty, out var name) || !name.Contains("iCloud", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bag = registry.GetStringValues($@"{ClassesClsidKey}\{clsid}\Instance\InitPropertyBag");
            if (bag.TryGetValue("TargetFolderPath", out var target) && target.Length > 0)
            {
                results.Add(Environment.ExpandEnvironmentVariables(target));
            }
        }

        return results;
    }

    /// <summary>Any path-looking value under Apple's key that mentions iCloud Drive, breadth-first and bounded.</summary>
    private static List<string> AppleCandidates(IRegistryReader registry)
    {
        var results = new List<string>();
        var pending = new Queue<(string Key, int Depth)>();
        pending.Enqueue((AppleKey, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < AppleSearchKeyLimit)
        {
            var (key, depth) = pending.Dequeue();
            foreach (var (name, raw) in Safely(() => registry.GetStringValues(key).ToList()))
            {
                var value = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                var mentionsDrive = MentionsICloudDrive(name) || MentionsICloudDrive(value);
                if (mentionsDrive && value.Length > 3 && Path.IsPathFullyQualified(value))
                {
                    results.Add(value);
                }
            }

            if (depth < AppleSearchDepth)
            {
                foreach (var child in Safely(() => registry.GetSubKeyNames(key).ToList()))
                {
                    pending.Enqueue(($@"{key}\{child}", depth + 1));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// The Cloud Files registration: <c>SyncRootManager\iCloudDrive!&lt;SID&gt;!…\UserSyncRoots</c>
    /// maps a user SID to the folder. The SID of this user is found through ProfileList (the
    /// entry whose ProfileImagePath is this profile), so another user's iCloud Drive on a shared
    /// PC is never picked.
    /// </summary>
    private static List<string> SyncRootCandidates(IRegistryReader registry, string userProfile)
    {
        var results = new List<string>();
        var sid = CurrentUserSid(registry, userProfile);
        if (sid is null)
        {
            return results;
        }

        foreach (var root in registry.GetSubKeyNames(SyncRootManagerKey))
        {
            if (!root.StartsWith("iCloud", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var (valueName, path) in registry.GetStringValues($@"{SyncRootManagerKey}\{root}\UserSyncRoots"))
            {
                if (string.Equals(valueName, sid, StringComparison.OrdinalIgnoreCase) && path.Length > 0)
                {
                    results.Add(Environment.ExpandEnvironmentVariables(path));
                }
            }
        }

        return results;
    }

    private static string? CurrentUserSid(IRegistryReader registry, string userProfile)
    {
        if (userProfile.Length == 0)
        {
            return null;
        }

        var profile = Path.TrimEndingDirectorySeparator(userProfile);
        foreach (var sid in registry.GetSubKeyNames(ProfileListKey))
        {
            var values = registry.GetStringValues($@"{ProfileListKey}\{sid}");
            if (values.TryGetValue("ProfileImagePath", out var image) &&
                string.Equals(Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(image)), profile, StringComparison.OrdinalIgnoreCase))
            {
                return sid;
            }
        }

        return null;
    }

    private static bool MentionsICloudDrive(string text)
        => text.Contains("iCloudDrive", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("iCloud Drive", StringComparison.OrdinalIgnoreCase);

    /// <summary>The reader is the app's code talking to the registry; any failure means "nothing there".</summary>
    private static IEnumerable<T> Safely<T>(Func<IEnumerable<T>> read)
    {
        try
        {
            return read();
        }
#pragma warning disable CA1031 // Defensive by design: a broken or denied registry key must never stop iCloud detection.
        catch (Exception e)
#pragma warning restore CA1031
        {
            SupportLogger.Shared.Log($"[iCloud] Registry lookup failed: {e.GetType().Name}", "iCloud");
            return [];
        }
    }
}
