// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Security;

namespace FileFluss.Core.Tests.Support;

/// <summary>
/// A cloud account held in memory, for engine tests. Paths are POSIX. Server-side move/copy
/// can be switched off to exercise the byte-copy fallback, and individual paths can be made
/// to fail.
/// </summary>
public sealed class MemoryCloudProvider : CloudProvider
{
    private readonly ConcurrentDictionary<string, Node> _nodes = new(StringComparer.Ordinal);

    public MemoryCloudProvider(CloudProviderType type = CloudProviderType.WebDav)
        : base(Guid.NewGuid(), new MemoryCredentialStore())
    {
        ProviderType = type;
        _nodes["/"] = new Node(true, [], DateTimeOffset.Now);
    }

    private sealed record Node(bool IsDirectory, byte[] Content, DateTimeOffset Modified);

    public override CloudProviderType ProviderType { get; }

    protected override string CredentialKeyPrefix => "memory";

    public override bool IsAuthenticated => true;

    public override ConnectSpec ConnectSpec => new();

    public bool SupportsServerSide { get; set; } = true;

    public int ServerSideCalls { get; private set; }

    public HashSet<string> FailingUploads { get; } = new(StringComparer.Ordinal);

    /// <summary>Set to give the account a sharing API; it builds the link for a path.</summary>
    public Func<string, CloudShareLink>? ShareLinkFactory { get; set; }

    public override ShareLinkCapabilities ShareLinkCapabilities
        => ShareLinkFactory is null ? ShareLinkCapabilities.Unsupported : new ShareLinkCapabilities { CanCreate = true };

    public override Task<CloudShareLink> CreateShareLinkAsync(string path, ShareLinkOptions options, CancellationToken cancellationToken = default)
        => ShareLinkFactory is { } factory && Exists(path)
            ? Task.FromResult(factory(CloudPath.Normalize(path)))
            : throw CloudProviderException.NotImplemented();

    public override Task<ConnectResult> ConnectAsync(ConnectValues values, IConnectPrompt prompt, CancellationToken cancellationToken = default)
        => Task.FromResult(new ConnectResult("memory"));

    // MARK: - Test helpers

    public void AddFile(string path, string content, DateTimeOffset? modified = null)
    {
        EnsureParents(path);
        _nodes[CloudPath.Normalize(path)] = new Node(false, System.Text.Encoding.UTF8.GetBytes(content), modified ?? DateTimeOffset.Now);
    }

    public void AddFolder(string path)
    {
        EnsureParents(path);
        _nodes[CloudPath.Normalize(path)] = new Node(true, [], DateTimeOffset.Now);
    }

    public bool Exists(string path) => _nodes.ContainsKey(CloudPath.Normalize(path));

    public string Read(string path) => System.Text.Encoding.UTF8.GetString(_nodes[CloudPath.Normalize(path)].Content);

    public DateTimeOffset ModifiedAt(string path) => _nodes[CloudPath.Normalize(path)].Modified;

    public IReadOnlyCollection<string> AllPaths => _nodes.Keys.Order().ToList();

    private void EnsureParents(string path)
    {
        var parent = CloudPath.Parent(path);
        while (!_nodes.ContainsKey(parent))
        {
            _nodes[parent] = new Node(true, [], DateTimeOffset.Now);
            parent = CloudPath.Parent(parent);
        }
    }

    private CloudFileItem ToItem(string path, Node node)
        => new(path, CloudPath.Name(path), path, node.IsDirectory, node.Content.Length, node.Modified);

    // MARK: - Contract

    public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = CloudPath.Normalize(path);
        if (!_nodes.TryGetValue(normalized, out var node) || !node.IsDirectory)
        {
            throw CloudProviderException.NotFound(path);
        }

        IReadOnlyList<CloudFileItem> children = _nodes
            .Where(kv => kv.Key != "/" && CloudPath.Parent(kv.Key) == normalized)
            .Select(kv => ToItem(kv.Key, kv.Value))
            .ToList();
        return Task.FromResult(children);
    }

    public override async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        if (!_nodes.TryGetValue(CloudPath.Normalize(remotePath), out var node) || node.IsDirectory)
        {
            throw CloudProviderException.NotFound(remotePath);
        }

        await File.WriteAllBytesAsync(localPath, node.Content, cancellationToken);
        onBytes?.Invoke(node.Content.Length);
    }

    public override async Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailingUploads.Contains(CloudPath.Name(remotePath)))
        {
            throw CloudProviderException.ServerError(500);
        }

        var bytes = await File.ReadAllBytesAsync(localPath, cancellationToken);
        EnsureParents(remotePath);
        _nodes[CloudPath.Normalize(remotePath)] = new Node(false, bytes, DateTimeOffset.Now);
        onBytes?.Invoke(bytes.Length);
    }

    public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = CloudPath.Normalize(path);
        if (!_nodes.ContainsKey(normalized))
        {
            throw CloudProviderException.NotFound(path);
        }

        foreach (var key in _nodes.Keys.Where(k => CloudPath.IsWithin(k, normalized)).ToList())
        {
            _nodes.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        AddFolder(path);
        return Task.CompletedTask;
    }

    public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
        => MoveTree(path, CloudPath.Combine(CloudPath.Parent(path), newName), copy: false);

    public override Task MoveItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        if (!SupportsServerSide)
        {
            throw CloudProviderException.NotImplemented();
        }

        ServerSideCalls++;
        return MoveTree(path, newPath, copy: false);
    }

    public override Task CopyItemAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        if (!SupportsServerSide)
        {
            throw CloudProviderException.NotImplemented();
        }

        ServerSideCalls++;
        return MoveTree(path, newPath, copy: true);
    }

    public override Task SetModificationDateAsync(string path, DateTimeOffset date, CancellationToken cancellationToken = default)
    {
        var normalized = CloudPath.Normalize(path);
        _nodes[normalized] = _nodes[normalized] with { Modified = date };
        return Task.CompletedTask;
    }

    public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = CloudPath.Normalize(path);
        return _nodes.TryGetValue(normalized, out var node)
            ? Task.FromResult(ToItem(normalized, node))
            : throw CloudProviderException.NotFound(path);
    }

    private Task MoveTree(string from, string to, bool copy)
    {
        var source = CloudPath.Normalize(from);
        var target = CloudPath.Normalize(to);
        if (!_nodes.ContainsKey(source))
        {
            throw CloudProviderException.NotFound(from);
        }

        EnsureParents(target);
        foreach (var key in _nodes.Keys.Where(k => CloudPath.IsWithin(k, source)).OrderBy(k => k.Length).ToList())
        {
            var node = _nodes[key];
            _nodes[target + key[source.Length..]] = node;
            if (!copy)
            {
                _nodes.TryRemove(key, out _);
            }
        }

        return Task.CompletedTask;
    }
}
