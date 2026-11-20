// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Models;

namespace FileFluss.Core.Archives;

/// <summary>
/// One entry inside an archive (macOS <c>ArchiveEntry</c>). <see cref="Path"/> is always
/// archive-relative, slash separated, with no leading "./" and no trailing slash — so a
/// directory and a file of the same name are spelled identically and looked up the same way.
/// </summary>
public sealed record ArchiveEntry(string Path, bool IsDirectory, bool IsSymlink, long Size, DateTimeOffset ModificationDate, bool IsEncrypted)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>The directory holding this entry; "" for a top-level entry.</summary>
    public string ParentPath => ParentOf(Path);

    public static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>
    /// Normalises a path as an archive declares it, or returns null when the entry must not be
    /// trusted — the one place that stands between a hostile archive and the file system.
    ///
    /// <para>An entry named <c>../../AppData/Roaming/…/Startup/evil.lnk</c> extracts outside
    /// the destination on any tool that joins paths naively (the "zip slip" family). Any ".."
    /// component is refused outright rather than resolved: an entry that needs to climb out of
    /// the archive is never something the user asked for. A leading "/" is stripped instead,
    /// as on macOS — archives made with <c>tar -P</c> carry them, and once the root is gone the
    /// path is ordinary.</para>
    ///
    /// <para>Windows adds its own escapes, all refused: a backslash is a separator here (so
    /// <c>..\..\x</c> climbs too), a colon names a drive (<c>C:\x</c>) or an alternate data
    /// stream (<c>file.txt:payload</c>), device names (CON, NUL, COM1 … with any extension)
    /// open the device instead of a file, and trailing dots or spaces vanish, so "a." would
    /// land on "a". Characters NTFS can't hold (<c>&lt;&gt;"|?*</c>, controls) are refused for
    /// the same reason: the entry can't be written as itself.</para>
    /// </summary>
    public static string? Normalise(string raw)
    {
        var components = new List<string>();
        foreach (var component in raw.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
            {
                continue;
            }

            if (component == ".." || !IsSafeComponent(component))
            {
                return null;
            }

            components.Add(component);
        }

        return components.Count == 0 ? null : string.Join('/', components);
    }

    private static bool IsSafeComponent(string component)
    {
        if (component.EndsWith('.') || component.EndsWith(' ') || component.All(c => c == '.'))
        {
            return false;
        }

        // Component() keeps a name only when Windows takes it as one ordinary file name:
        // no separators, no ':' (drives, streams), no <>"|?* or controls, no device name.
        return SafeLocalPath.Component(component) == component;
    }

    /// <summary>AppleDouble metadata a Mac-made zip carries beside each file: not files the user put there.</summary>
    public static bool IsAppleDouble(string path)
        => path == "__MACOSX" || path.StartsWith("__MACOSX/", StringComparison.Ordinal) || path[(path.LastIndexOf('/') + 1)..].StartsWith("._", StringComparison.Ordinal);

    /// <summary>A link that points out of the archive: absolute, a drive, or climbing with "..".</summary>
    public static bool LinkEscapes(string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return true;
        }

        var normalized = target.Replace('\\', '/');
        return normalized.StartsWith('/') || normalized.Contains(':') || normalized.Split('/').Contains("..");
    }
}

/// <summary>
/// An archive's entry list arranged for browsing: children by directory and recursive sizes,
/// both resolved once when the archive is listed (macOS <c>ArchiveTree</c>). Built eagerly on
/// purpose — walking the entry list per navigation is fine for a hundred entries and visibly
/// not for a hundred thousand.
/// </summary>
public sealed class ArchiveTree
{
    private readonly Dictionary<string, ArchiveEntry> _byPath;
    private readonly Dictionary<string, List<ArchiveEntry>> _children;
    private readonly Dictionary<string, long> _recursiveSizes;

    /// <param name="rawEntries">Entries as the archive declared them, unsafe paths and AppleDouble sidecars already dropped.</param>
    /// <param name="firstUnsafeEntry">The first entry refused as unsafe, for the message saying so.</param>
    public ArchiveTree(IEnumerable<ArchiveEntry> rawEntries, string? firstUnsafeEntry = null, int unsafeEntryCount = 0)
    {
        FirstUnsafeEntry = firstUnsafeEntry;
        UnsafeEntryCount = unsafeEntryCount;
        var raw = rawEntries as IReadOnlyCollection<ArchiveEntry> ?? rawEntries.ToList();

        // A zip may carry the same name twice; the later entry is the one that would win on
        // extraction, so it wins here too.
        _byPath = new Dictionary<string, ArchiveEntry>(raw.Count * 2, StringComparer.Ordinal);
        foreach (var entry in raw)
        {
            _byPath[entry.Path] = entry;
            ContainsEncryptedEntries |= entry.IsEncrypted;
        }

        // Many archives (most zips) declare files without the directories above them. Those
        // still have to be navigable, so stand them up from the paths that mention them.
        foreach (var entry in raw)
        {
            for (var parent = entry.ParentPath; parent.Length > 0 && !_byPath.ContainsKey(parent); parent = ArchiveEntry.ParentOf(parent))
            {
                _byPath[parent] = new ArchiveEntry(parent, true, false, 0, entry.ModificationDate, false);
            }
        }

        _children = new Dictionary<string, List<ArchiveEntry>>(StringComparer.Ordinal);
        _recursiveSizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in _byPath.Values)
        {
            if (!_children.TryGetValue(entry.ParentPath, out var siblings))
            {
                _children[entry.ParentPath] = siblings = [];
            }

            siblings.Add(entry);
            if (entry.IsDirectory)
            {
                continue;
            }

            // Sizes roll up to every ancestor, so a folder's row shows what extracting it
            // would cost without walking the list again.
            for (var parent = entry.ParentPath; parent.Length > 0; parent = ArchiveEntry.ParentOf(parent))
            {
                _recursiveSizes[parent] = _recursiveSizes.GetValueOrDefault(parent) + entry.Size;
            }
        }

        Entries = _byPath.Values.ToList();
    }

    /// <summary>Every entry, including the directories synthesised for paths that mention them.</summary>
    public IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>At least one entry's data is encrypted: extraction will need a password FileFluss can't ask for yet.</summary>
    public bool ContainsEncryptedEntries { get; }

    /// <summary>The first entry refused as unsafe (its path or a link out of the archive). Refused entries aren't in <see cref="Entries"/>.</summary>
    public string? FirstUnsafeEntry { get; }

    public int UnsafeEntryCount { get; }

    /// <summary>The archive's own entry list was readable but empty.</summary>
    public bool IsEmpty => Entries.Count == 0;

    public IReadOnlyList<ArchiveEntry> TopLevel => Children(string.Empty);

    /// <summary>Direct children of <paramref name="directory"/> ("" for the archive root).</summary>
    public IReadOnlyList<ArchiveEntry> Children(string directory) => _children.TryGetValue(directory, out var list) ? list : [];

    public ArchiveEntry? Entry(string path) => _byPath.GetValueOrDefault(path);

    /// <summary>Total uncompressed bytes of the files anywhere beneath <paramref name="directory"/>.</summary>
    public long RecursiveSize(string directory) => _recursiveSizes.GetValueOrDefault(directory);

    /// <summary>Every entry at or beneath <paramref name="path"/> — what extracting a selected folder pulls out.</summary>
    public IReadOnlyList<ArchiveEntry> Subtree(string path)
    {
        if (Entry(path) is not { } root)
        {
            return [];
        }

        if (!root.IsDirectory)
        {
            return [root];
        }

        var prefix = path + "/";
        var result = new List<ArchiveEntry> { root };
        result.AddRange(Entries.Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal)));
        return result;
    }

    public long TotalSize => Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);

    public int FileCount => Entries.Count(e => !e.IsDirectory);

    /// <summary>A row for the panel: POSIX path under the archive root, a folder's size what unpacking it costs.</summary>
    public CloudFileItem ItemFor(ArchiveEntry entry)
    {
        var path = "/" + entry.Path;
        return new CloudFileItem(path, entry.Name, path, entry.IsDirectory, entry.IsDirectory ? RecursiveSize(entry.Path) : entry.Size, entry.ModificationDate)
        {
            IsHidden = CloudFileItem.IsDotHidden(entry.Name),
            IsSymlink = entry.IsSymlink,
        };
    }
}
