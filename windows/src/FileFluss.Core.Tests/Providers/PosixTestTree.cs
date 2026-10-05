// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// An in-memory POSIX tree (folders, files, symlinks) for the SFTP and FTP fakes, with a log
/// of the mutating commands they receive.
/// </summary>
internal sealed class PosixTestTree
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal)
    {
        ["/"] = new Node(NodeKind.Directory, [], new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
    };

    public enum NodeKind
    {
        File,
        Directory,
        Link,
    }

    public sealed record Node(NodeKind Kind, byte[] Content, DateTime Modified, string? Target = null);

    public ConcurrentQueue<string> Log { get; } = new();

    public static readonly DateTime DefaultTime = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    public PosixTestTree Dir(string path, DateTime? modified = null)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _nodes[Clean(path)] = new Node(NodeKind.Directory, [], modified ?? DefaultTime);
        }

        return this;
    }

    public PosixTestTree File(string path, string content, DateTime? modified = null)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _nodes[Clean(path)] = new Node(NodeKind.File, System.Text.Encoding.UTF8.GetBytes(content), modified ?? DefaultTime);
        }

        return this;
    }

    public PosixTestTree Link(string path, string target)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _nodes[Clean(path)] = new Node(NodeKind.Link, [], DefaultTime, target);
        }

        return this;
    }

    public Node? Get(string path)
    {
        lock (_gate)
        {
            return _nodes.GetValueOrDefault(Clean(path));
        }
    }

    /// <summary>Follows links (up to a few hops); null when dangling or missing.</summary>
    public (string Path, Node Node)? Resolve(string path)
    {
        lock (_gate)
        {
            var current = Clean(path);
            for (var hop = 0; hop < 8; hop++)
            {
                if (!_nodes.TryGetValue(current, out var node))
                {
                    return null;
                }

                if (node.Kind != NodeKind.Link)
                {
                    return (current, node);
                }

                current = Clean(node.Target!.StartsWith('/') ? node.Target : Parent(current) + "/" + node.Target);
            }

            return null;
        }
    }

    public IReadOnlyList<(string Name, Node Node)> Children(string path)
    {
        lock (_gate)
        {
            var prefix = Clean(path) == "/" ? "/" : Clean(path) + "/";
            return _nodes
                .Where(kv => kv.Key != "/" && kv.Key.StartsWith(prefix, StringComparison.Ordinal) && !kv.Key[prefix.Length..].Contains('/'))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (kv.Key[prefix.Length..], kv.Value))
                .ToList();
        }
    }

    public void Put(string path, Node node)
    {
        lock (_gate)
        {
            _nodes[Clean(path)] = node;
        }
    }

    public bool Remove(string path)
    {
        lock (_gate)
        {
            return _nodes.Remove(Clean(path));
        }
    }

    /// <summary>Moves a node and everything beneath it.</summary>
    public void Move(string from, string to)
    {
        lock (_gate)
        {
            var source = Clean(from);
            var target = Clean(to);
            foreach (var key in _nodes.Keys.Where(k => k == source || k.StartsWith(source + "/", StringComparison.Ordinal)).ToList())
            {
                var node = _nodes[key];
                _nodes.Remove(key);
                _nodes[target + key[source.Length..]] = node;
            }
        }
    }

    /// <summary>Copies a node and everything beneath it (links copied as links).</summary>
    public void Copy(string from, string to)
    {
        lock (_gate)
        {
            var source = Clean(from);
            var target = Clean(to);
            foreach (var key in _nodes.Keys.Where(k => k == source || k.StartsWith(source + "/", StringComparison.Ordinal)).ToList())
            {
                _nodes[target + key[source.Length..]] = _nodes[key];
            }
        }
    }

    public string Text(string path) => System.Text.Encoding.UTF8.GetString(Get(path)!.Content);

    public static string Clean(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', parts);
    }

    public static string Parent(string path)
    {
        var clean = Clean(path);
        var slash = clean.LastIndexOf('/');
        return slash <= 0 ? "/" : clean[..slash];
    }

    private void EnsureParents(string path)
    {
        var parent = Parent(path);
        while (parent != "/" && !_nodes.ContainsKey(parent))
        {
            _nodes[parent] = new Node(NodeKind.Directory, [], DefaultTime);
            parent = Parent(parent);
        }
    }
}
