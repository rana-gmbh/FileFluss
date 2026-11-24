// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text.Json;
using System.Text.Json.Serialization;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Services;

/// <summary>
/// Whose tags: this PC's files, or one cloud account's items. Decides which file the tags are
/// kept in and how paths are matched.
/// </summary>
public sealed record TagScope(string Key, bool LastingItemIds, bool CaseSensitivePaths)
{
    /// <summary>
    /// The local file system. Windows has nothing like Finder's tags for arbitrary files and
    /// folders — the "Tags" property only exists for a few file types, and alternate data
    /// streams are lost off NTFS — so local tags are kept by FileFluss too, by path.
    /// </summary>
    public static readonly TagScope Local = new("local", LastingItemIds: false, CaseSensitivePaths: false);

    /// <summary>A cloud account. The file is named like the Mac app's: the account id in upper case.</summary>
    public static TagScope Account(Guid accountId, bool lastingItemIds, bool caseSensitivePaths = true)
        => new(accountId.ToString("D").ToUpperInvariant(), lastingItemIds, caseSensitivePaths);

    /// <summary>
    /// The scope a provider's items belong to, or null where tags aren't kept: inside an
    /// archive, on an unplugged drive browsed from its index.
    /// </summary>
    public static TagScope? For(FileProvider? provider) => provider switch
    {
        LocalFileProvider => Local,
        CloudProvider cloud => Account(cloud.AccountId, cloud.ProviderType.HasLastingItemIds(), cloud.IsCaseSensitive),
        _ => null,
    };
}

public static class TagProviderTypes
{
    /// <summary>
    /// Whether the identifier this provider puts in <see cref="CloudFileItem.Id"/> is the
    /// service's own and survives a rename or move. For the others it is the path, and says
    /// nothing a path doesn't.
    /// </summary>
    public static bool HasLastingItemIds(this CloudProviderType type) => type is
        CloudProviderType.PCloud or CloudProviderType.KDrive or CloudProviderType.OneDrive
        or CloudProviderType.GoogleDrive or CloudProviderType.GoogleDrivePicker or CloudProviderType.Dropbox
        or CloudProviderType.Mega or CloudProviderType.Filen or CloudProviderType.Internxt or CloudProviderType.TeraBox;
}

/// <summary>
/// The tags FileFluss keeps for files and folders (macOS <c>TagStore</c>, there for cloud
/// accounts only; here for local files as well, see <see cref="TagScope.Local"/>).
///
/// <para>One small JSON file per scope, read once and held in memory, in the Mac app's
/// format: <c>{"version":1,"entries":{"/path":{"tags":[{"name":"Red","color":6}],"itemID":"…"}}}</c>.</para>
///
/// <para>An entry belongs to a path. Where the service gives items an identifier that outlives
/// a rename, the entry remembers that too, and <see cref="Resolve"/> uses it to follow an item
/// that was renamed or moved somewhere other than in FileFluss. Without one the path is all
/// there is, and a rename made elsewhere leaves the tag behind.</para>
///
/// <para>FileFluss's own renames, moves, copies and deletes are passed on by the callers
/// through <see cref="MoveSubtree"/>, <see cref="CopySubtree"/> and <see cref="RemoveSubtree"/>.
/// Thread-safe: panels, transfers and sync all call one instance.</para>
/// </summary>
public sealed class TagStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private static TagStore? _shared;

    private readonly string _directory;
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly Dictionary<string, Dictionary<string, Entry>> _scopes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);

    /// <summary>
    /// Scopes whose file exists but couldn't be read (nor its backup): never written or deleted
    /// this session, so a file a scanner held at the wrong moment, or one that went bad, isn't
    /// replaced by the empty set it loaded as.
    /// </summary>
    private readonly HashSet<string> _unreadable = new(StringComparer.Ordinal);
    private Timer? _saveTimer;

    public TagStore(string directory)
    {
        _directory = directory;
    }

    /// <summary>
    /// The app's store, in its data folder. The transfer engine and sync reach it here, as
    /// they reach the support log; tests give a request its own store instead.
    /// </summary>
    public static TagStore Shared
    {
        get => _shared ??= new TagStore(Path.Combine(Settings.SettingsStore.DataDirectory, "Tags"));
        set => _shared = value;
    }

    // MARK: - Reading

    public IReadOnlyList<FileTag> Tags(TagScope scope, string path)
    {
        lock (_gate)
        {
            return Entries(scope).TryGetValue(Normalized(path), out var entry) ? entry.Tags : FileTags.None;
        }
    }

    /// <summary>
    /// The tags of the items in one folder listing, keyed by path. Items without tags are left out.
    ///
    /// <para>With lasting identifiers, an entry is matched by the item's identifier first, and
    /// follows the item to its new path if it was renamed elsewhere. An item that has no entry
    /// of its own then takes the one at its path — that is a file replaced in place, which
    /// gets a new identifier but is to the user the same file.</para>
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<FileTag>> Resolve(TagScope scope, IReadOnlyList<CloudFileItem> items)
        => ResolveDetailed(scope, items).Tags;

    /// <summary>
    /// As <see cref="Resolve"/>, and also which of the items had a record without an
    /// identifier until now: those were written by a transfer, and this listing is the first
    /// time FileFluss has seen them on the service. A service with labels of its own hasn't
    /// been told their tags yet (see <see cref="NativeTagSync"/>).
    /// </summary>
    public (IReadOnlyDictionary<string, IReadOnlyList<FileTag>> Tags, IReadOnlySet<string> NewlySeen) ResolveDetailed(TagScope scope, IReadOnlyList<CloudFileItem> items)
    {
        var newlySeen = new HashSet<string>(PathComparer(scope));
        return (ResolveCore(scope, items, newlySeen), newlySeen);
    }

    private Dictionary<string, IReadOnlyList<FileTag>> ResolveCore(TagScope scope, IReadOnlyList<CloudFileItem> items, HashSet<string> newlySeen)
    {
        var result = new Dictionary<string, IReadOnlyList<FileTag>>(PathComparer(scope));
        lock (_gate)
        {
            var entries = Entries(scope);
            if (entries.Count == 0)
            {
                return result;
            }

            if (!scope.LastingItemIds)
            {
                foreach (var item in items)
                {
                    if (entries.TryGetValue(Normalized(item.Path), out var entry))
                    {
                        result[item.Path] = entry.Tags;
                    }
                }

                return result;
            }

            var pathById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (path, entry) in entries)
            {
                if (entry.ItemId is { } id)
                {
                    pathById[id] = path;
                }
            }

            var changed = false;

            // Matched entries are taken out first and put back at the end, so two items that
            // swapped names don't overwrite one another.
            var matched = new List<(string Path, Entry Entry)>();
            var unmatched = new List<CloudFileItem>();
            foreach (var item in items)
            {
                var path = Normalized(item.Path);
                if (pathById.TryGetValue(item.Id, out var oldPath) && entries.Remove(oldPath, out var entry))
                {
                    changed |= !PathComparer(scope).Equals(oldPath, path);
                    matched.Add((path, entry));
                    result[item.Path] = entry.Tags;
                }
                else
                {
                    unmatched.Add(item);
                }
            }

            foreach (var item in unmatched)
            {
                var path = Normalized(item.Path);
                if (!entries.Remove(path, out var entry))
                {
                    continue;
                }

                changed = true;
                if (entry.ItemId is null)
                {
                    newlySeen.Add(item.Path);
                }

                matched.Add((path, entry with { ItemId = item.Id }));
                result[item.Path] = entry.Tags;
            }

            foreach (var (path, entry) in matched)
            {
                entries[path] = entry;
            }

            if (changed)
            {
                MarkDirty(scope);
            }
        }

        return result;
    }

    // MARK: - Writing

    /// <summary>
    /// Sets an item's tags; an empty list forgets the item. <paramref name="itemId"/> is the
    /// service's identifier where it has a lasting one, and null for an item that has only
    /// just been written, whose identifier isn't known yet.
    /// </summary>
    public void SetTags(TagScope scope, string path, IReadOnlyList<FileTag> tags, string? itemId = null)
    {
        lock (_gate)
        {
            var entries = Entries(scope);
            var key = Normalized(path);
            entries.TryGetValue(key, out var current);
            if (tags.Count == 0)
            {
                if (current is null)
                {
                    return;
                }

                entries.Remove(key);
            }
            else
            {
                var id = scope.LastingItemIds ? itemId : null;
                if (current is not null && current.Tags.SameAs(tags) && current.ItemId == id)
                {
                    return;
                }

                entries[key] = new Entry(tags.ToList(), id);
            }

            MarkDirty(scope);
        }
    }

    /// <summary>
    /// A rename or move within one scope: the item's tags, and those of everything inside it,
    /// go with it. Whatever was at the destination is replaced.
    /// </summary>
    public void MoveSubtree(TagScope scope, string from, string to)
        => Transfer(scope, from, scope, to, keepSource: false, replaceDestination: true);

    /// <summary>
    /// A copy, within a scope or from one to another. The copies are new items, so they start
    /// without an identifier. With <paramref name="replaceDestination"/> false the copy only
    /// adds: tags already at the destination stay where the source brings none — a sync that
    /// refreshes a file's contents must not take off the tag someone gave it on the other side.
    /// </summary>
    public void CopySubtree(TagScope from, string path, TagScope to, string toPath, bool replaceDestination = false)
        => Transfer(from, path, to, toPath, keepSource: true, replaceDestination);

    public void RemoveSubtree(TagScope scope, string path)
    {
        lock (_gate)
        {
            var entries = Entries(scope);
            var doomed = SubtreeKeys(scope, entries, Normalized(path));
            if (doomed.Count == 0)
            {
                return;
            }

            foreach (var key in doomed)
            {
                entries.Remove(key);
            }

            MarkDirty(scope);
        }
    }

    /// <summary>Forgets an account that has been removed from FileFluss.</summary>
    public void RemoveAccount(Guid accountId)
    {
        var scope = TagScope.Account(accountId, lastingItemIds: false);
        lock (_gate)
        {
            // Removing the account removes its tags file too, readable or not.
            _unreadable.Remove(scope.Key);
            _scopes[scope.Key] = NewEntries(scope);
            MarkDirty(scope);
        }
    }

    private void Transfer(TagScope fromScope, string path, TagScope toScope, string toPath, bool keepSource, bool replaceDestination)
    {
        var from = Normalized(path);
        var to = Normalized(toPath);
        var sameScope = string.Equals(fromScope.Key, toScope.Key, StringComparison.Ordinal);
        if (sameScope && PathComparer(fromScope).Equals(from, to))
        {
            return;
        }

        lock (_gate)
        {
            var source = Entries(fromScope);
            var keys = SubtreeKeys(fromScope, source, from);
            var moving = new List<(string Path, Entry Entry)>(keys.Count);
            foreach (var key in keys)
            {
                var entry = source[key];
                if (keepSource)
                {
                    entry = entry with { ItemId = null };
                }
                else
                {
                    source.Remove(key);
                }

                // An identifier means nothing on another account.
                if (!sameScope || !toScope.LastingItemIds)
                {
                    entry = entry with { ItemId = null };
                }

                moving.Add((to + Rebased(key[from.Length..], fromScope, toScope), entry));
            }

            var destination = Entries(toScope);
            var replaced = replaceDestination ? SubtreeKeys(toScope, destination, to) : [];
            if (moving.Count == 0 && replaced.Count == 0)
            {
                return;
            }

            foreach (var key in replaced)
            {
                destination.Remove(key);
            }

            foreach (var (key, entry) in moving)
            {
                destination[key] = entry;
            }

            if (!sameScope && !keepSource && keys.Count > 0)
            {
                MarkDirty(fromScope);
            }

            MarkDirty(toScope);
        }
    }

    // MARK: - Paths

    private static StringComparer PathComparer(TagScope scope)
        => scope.CaseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static bool IsSeparator(char c) => c is '/' or '\\';

    /// <summary>Without a trailing separator, except where that is all there is ("/", "C:\").</summary>
    private static string Normalized(string path)
    {
        if (path.Length <= 1 || !IsSeparator(path[^1]))
        {
            return path;
        }

        return path.Length == 3 && path[1] == ':' ? path : path[..^1];
    }

    /// <summary>The path itself and everything below it.</summary>
    private static List<string> SubtreeKeys(TagScope scope, Dictionary<string, Entry> entries, string path)
    {
        var comparison = scope.CaseSensitivePaths ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var endsInSeparator = path.Length > 0 && IsSeparator(path[^1]);
        var keys = new List<string>();
        foreach (var key in entries.Keys)
        {
            if (!key.StartsWith(path, comparison))
            {
                continue;
            }

            if (key.Length == path.Length || endsInSeparator || IsSeparator(key[path.Length]))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>
    /// The part of a path below the copied item, with the separators of where it is going: a
    /// folder downloaded from an account to this PC turns "/a/b" into "\a\b".
    /// </summary>
    private static string Rebased(string remainder, TagScope from, TagScope to)
    {
        var fromLocal = ReferenceEquals(from, TagScope.Local) || from.Key == TagScope.Local.Key;
        var toLocal = ReferenceEquals(to, TagScope.Local) || to.Key == TagScope.Local.Key;
        if (fromLocal == toLocal || remainder.Length == 0)
        {
            return remainder;
        }

        return toLocal ? remainder.Replace('/', Path.DirectorySeparatorChar) : remainder.Replace('\\', '/');
    }

    // MARK: - Storage

    private sealed record Entry(
        [property: JsonPropertyName("tags")] List<FileTag> Tags,
        [property: JsonPropertyName("itemID")] string? ItemId);

    private sealed record ScopeFile(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("entries")] Dictionary<string, Entry> Entries);

    private string FilePath(string scopeKey) => Path.Combine(_directory, scopeKey + ".json");

    private static Dictionary<string, Entry> NewEntries(TagScope scope) => new(PathComparer(scope));

    private Dictionary<string, Entry> Entries(TagScope scope)
    {
        if (_scopes.TryGetValue(scope.Key, out var loaded))
        {
            return loaded;
        }

        var entries = NewEntries(scope);
        var path = FilePath(scope.Key);
        var backup = AtomicFile.BackupPath(path);

        // The file, or else the previous good one: a power cut's half-written file or one a
        // scanner holds at launch mustn't read as "no tags" — the next save would then write
        // that empty set over the real one (and its backup on the save after).
        if (File.Exists(path) || File.Exists(backup))
        {
            if (!TryRead(path, entries, scope.Key) && !TryRead(backup, entries, scope.Key))
            {
                // A damaged file is no tags, not a failure to open a folder — but it stays as
                // it is: this scope is neither written nor deleted until the next launch.
                entries.Clear();
                _unreadable.Add(scope.Key);
                Diagnostics.SupportLogger.Shared.Log($"Tags for {scope.Key} are left untouched this session: neither the file nor its backup could be read", "Tags", Diagnostics.LogLevel.Error);
            }
        }

        _scopes[scope.Key] = entries;
        return entries;
    }

    /// <summary>
    /// Reads one tags file into <paramref name="entries"/>. A file held by another process is
    /// tried again briefly (a scanner opening it as FileFluss starts); false when it can't be
    /// read or parsed, or isn't there.
    /// </summary>
    private static bool TryRead(string path, Dictionary<string, Entry> entries, string scopeKey)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (JsonSerializer.Deserialize<ScopeFile>(File.ReadAllText(path), Json) is not { Entries: { } stored })
                {
                    return false;
                }

                entries.Clear();
                foreach (var (key, entry) in stored)
                {
                    if (entry?.Tags is not { Count: > 0 } tags)
                    {
                        continue;
                    }

                    // A colour index a newer version wrote is shown as no colour, not refused.
                    entries[key] = entry with { Tags = tags.Where(t => !string.IsNullOrEmpty(t.Name)).Select(t => t with { Color = FileTags.Known(t.Color) }).ToList() };
                }

                return true;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(50 * attempt);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                Diagnostics.SupportLogger.Shared.Log($"Tags for {scopeKey} could not be read from {Path.GetFileName(path)}: {e.Message}", "Tags", Diagnostics.LogLevel.Error);
                return false;
            }
        }
    }

    /// <summary>
    /// Writes are gathered for a moment: a transfer of a few thousand tagged files would
    /// otherwise rewrite the file once per file. Called with the gate held.
    /// </summary>
    private void MarkDirty(TagScope scope)
    {
        // An unreadable scope's file is kept as it is; changes to it live for this session only.
        if (_unreadable.Contains(scope.Key))
        {
            return;
        }

        _dirty.Add(scope.Key);
        _saveTimer ??= new Timer(_ => Flush(), null, SaveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Writes pending changes now.</summary>
    public void Flush()
    {
        // One writer at a time, so an older snapshot can't land on top of a newer one.
        lock (_writeGate)
        {
            FlushCore();
        }
    }

    private void FlushCore()
    {
        List<(string Key, string? Contents)> pending;
        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            if (_dirty.Count == 0)
            {
                return;
            }

            pending = [];
            foreach (var key in _dirty)
            {
                var entries = _scopes.TryGetValue(key, out var loaded) ? loaded : null;
                pending.Add((key, entries is { Count: > 0 }
                    ? JsonSerializer.Serialize(new ScopeFile(1, new Dictionary<string, Entry>(entries, StringComparer.Ordinal)), Json)
                    : null));
            }

            _dirty.Clear();
        }

        foreach (var (key, contents) in pending)
        {
            try
            {
                var path = FilePath(key);
                if (contents is null)
                {
                    File.Delete(path);
                    File.Delete(AtomicFile.BackupPath(path));
                }
                else
                {
                    AtomicFile.WriteAllText(path, contents);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Kept pending, so the next save (the one on quitting at the latest) tries again
                // rather than the change being lost.
                lock (_gate)
                {
                    _dirty.Add(key);
                }

                Diagnostics.SupportLogger.Shared.Log($"Tags for {key} could not be saved: {e.Message}", "Tags", Diagnostics.LogLevel.Error);
            }
        }
    }

    public void Dispose() => Flush();
}
