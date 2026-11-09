// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Sync;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Transfers;
using FileFluss.Core.VirtualFiles;

namespace FileFluss.Core.Tests.Security;

/// <summary>
/// A server chooses the names it lists. Every way a remote name becomes a local (or another
/// account's) path must keep it inside the folder the user picked: "..\..\Startup\evil.exe",
/// "C:\x", "a:b" (an alternate data stream, or a drive-relative path), device names.
/// </summary>
public sealed class HostileNameTests : IDisposable
{
    /// <summary>Names a hostile server might list, none of which may escape.</summary>
    public static readonly TheoryData<string> Hostile = new(HostileNames);

    private static string[] HostileNames =>
    [
        "..",
        ".",
        "..\\..\\evil.txt",
        "../../evil.txt",
        "a\\..\\..\\evil.txt",
        "C:\\evil.txt",
        "C:evil.txt",
        "\\\\server\\share\\evil.txt",
        "a:b.txt",
        "evil.txt:Zone.Identifier",
        "CON",
        "nul.txt",
        "...",
        "evil.txt.  ",
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-hostile-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// <see cref="HostileNames"/> for tests that write files: the rooted, drive-relative and UNC
    /// names are replaced by an absolute path inside the test folder, so a regression lands
    /// there (and fails the test) instead of on C:\ or the network.
    /// </summary>
    private string[] WritableHostileNames =>
    [
        .. HostileNames.Where(n => !Path.IsPathRooted(n) && !n.StartsWith("C:", StringComparison.Ordinal)),
        Path.Combine(_root, "escaped"),
    ];

    public HostileNameTests()
    {
        Directory.CreateDirectory(_root);
        LocalFileProvider.RecycleHandler = null;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Destination()
    {
        var path = Path.Combine(_root, "inner", "destination");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Every file and folder anywhere under the test root, outside <paramref name="destination"/>.</summary>
    private IReadOnlyList<string> Outside(string destination)
        => Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(destination, StringComparison.OrdinalIgnoreCase) && !destination.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            .ToList();

    // MARK: - Helpers

    [Theory]
    [MemberData(nameof(Hostile))]
    public void SafeFileNameIsOneHarmlessComponent(string name)
    {
        var safe = SafeLocalPath.FileName(name);
        Assert.False(Path.IsPathRooted(safe));
        Assert.DoesNotContain(':', safe);
        Assert.DoesNotContain('\\', safe);
        Assert.DoesNotContain('/', safe);
        Assert.False(CloudPath.IsTraversalName(safe));
        Assert.NotEqual("CON", safe, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("..", true)]
    [InlineData(".", true)]
    [InlineData("a/../b", true)]
    [InlineData("..\\x", true)]
    [InlineData("...", false)]
    [InlineData("..a", false)]
    [InlineData("a..b", false)]
    [InlineData("report.txt", false)]
    public void TraversalNamesAreRecognised(string name, bool expected)
        => Assert.Equal(expected, CloudPath.IsTraversalName(name));

    [Theory]
    [InlineData("report.txt", "report.txt")]
    [InlineData(" leading space", " leading space")]
    [InlineData("C:\\x", "C_-x")]
    [InlineData("a:b", "a_b")]
    [InlineData("CON", "_CON")]
    [InlineData("...", "unnamed")]
    public void ComponentKeepsPlainNamesAndDefusesTheRest(string name, string expected)
        => Assert.Equal(expected, SafeLocalPath.Component(name));

    [Theory]
    [InlineData("..")]
    [InlineData("a\\..\\..\\x")]
    public void ComponentRefusesTraversal(string name)
        => Assert.Throws<CloudProviderException>(() => SafeLocalPath.Component(name));

    // MARK: - Sync and compare paths

    [Theory]
    [MemberData(nameof(Hostile))]
    public void LocalEndpointResolveNeverLeavesItsFolder(string name)
    {
        var destination = Destination();
        var endpoint = new SyncEndpoint(LocalFileProvider.Shared, destination);
        foreach (var relative in new[] { name, "sub/" + name, name + "/child.txt" })
        {
            string resolved;
            try
            {
                resolved = endpoint.Resolve(relative);
            }
            catch (CloudProviderException)
            {
                continue;
            }

            var full = Path.GetFullPath(resolved);
            Assert.StartsWith(destination + Path.DirectorySeparatorChar, full, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(':', full[2..]);
        }
    }

    [Fact]
    public void LocalEndpointResolveKeepsOrdinaryNames()
    {
        var destination = Destination();
        var endpoint = new SyncEndpoint(LocalFileProvider.Shared, destination);
        Assert.Equal(Path.Combine(destination, "a b", "c.d.txt"), endpoint.Resolve("a b/c.d.txt"));
    }

    [Fact]
    public void CloudEndpointResolveRefusesTraversal()
    {
        var endpoint = new SyncEndpoint(new MemoryCloudProvider(), "/Base");
        Assert.Throws<CloudProviderException>(() => endpoint.Resolve("../Other/x.txt"));
        Assert.Throws<CloudProviderException>(() => endpoint.Resolve("a\\..\\..\\x.txt"));
        Assert.Equal("/Base/a/b.txt", endpoint.Resolve("a/b.txt"));
    }

    [Fact]
    public async Task SyncFromAHostileServerStaysInsideTheDestination()
    {
        var destination = Destination();
        var outside = Path.Combine(_root, "escaped"); // in WritableHostileNames: Path.Combine would return it unchanged
        var server = new HostileProvider();

        // A name that climbs ("..", "a\..\..\x") stops the scan instead (SyncListingWithTraversalStopsThePlan).
        foreach (var name in WritableHostileNames.Where(n => !CloudPath.IsTraversalName(n)))
        {
            var folder = server.AddFolder("/", name);
            server.AddFile(folder, "inside.txt");
            server.AddFile("/", name);
        }

        var source = new SyncEndpoint(server, "/");
        var target = new SyncEndpoint(LocalFileProvider.Shared, destination);
        var plan = SyncPlanner.Plan(
            await SyncPlanner.EnumerateAsync(source),
            await SyncPlanner.EnumerateAsync(target),
            SyncMode.Mirror,
            PlanDirection.LeftToRight,
            sourceIsCloud: true,
            destinationIsCloud: false,
            ignoreCase: true);
        var progress = SyncExecutor.CreateProgress(plan, source, target, PanelSide.Right);
        await SyncExecutor.ExecuteAsync(plan, source, target, progress);

        Assert.False(Directory.Exists(outside));
        Assert.Empty(Outside(destination));
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../../evil.txt")]
    [InlineData("a\\..\\..\\evil.txt")]
    public async Task SyncListingWithTraversalStopsThePlan(string name)
    {
        // macOS 1.6: skipping the entry would let Mirror delete it on the other side instead,
        // so the whole scan stops with the server's path in the message.
        var server = new HostileProvider();
        server.AddFile("/", "fine.txt");
        server.AddFile(server.AddFolder("/", "sub"), name);

        await Assert.ThrowsAsync<CloudProviderException>(() => SyncPlanner.EnumerateAsync(new SyncEndpoint(server, "/")));
    }

    // MARK: - Transfers

    [Fact]
    public async Task DownloadFromAHostileServerStaysInsideTheDestination()
    {
        var destination = Destination();
        var server = new HostileProvider();
        var top = server.AddFolder("/", "Folder");
        foreach (var name in WritableHostileNames)
        {
            server.AddFile(top, name);
            server.AddFile(server.AddFolder(top, name), "inside.txt");
        }

        var items = await server.ListDirectoryAsync("/");
        var request = new TransferRequest(server, items, LocalFileProvider.Shared, destination, IsMove: false);
        await TransferEngine.RunAsync(request, TransferEngine.CreateProgress(request, PanelSide.Right));

        Assert.Empty(Outside(destination));
        Assert.True(Path.Exists(Path.Combine(destination, "Folder", "_CON")));
    }

    [Fact]
    public async Task CopyFromAHostileServerToAnotherAccountRefusesTraversal()
    {
        var server = new HostileProvider();
        var top = server.AddFolder("/", "Folder");
        server.AddFile(top, "..");
        server.AddFile(server.AddFolder(top, ".."), "escaped.txt");
        server.AddFile(top, "ok.txt");
        server.AddFile("/", "..");

        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/Target/Inbox");
        var items = await server.ListDirectoryAsync("/");
        var request = new TransferRequest(server, items, cloud, "/Target/Inbox", IsMove: false);
        var progress = TransferEngine.CreateProgress(request, PanelSide.Right);
        await TransferEngine.RunAsync(request, progress);

        Assert.True(cloud.Exists("/Target/Inbox/Folder/ok.txt"));
        Assert.All(cloud.AllPaths, p => Assert.True(p is "/" or "/Target" || p.StartsWith("/Target/Inbox", StringComparison.Ordinal), p));
        Assert.False(cloud.Exists("/Target/Inbox/escaped.txt"));
        Assert.False(cloud.Exists("/Target/escaped.txt"));
    }

    // MARK: - Virtual files (drag to Explorer)

    [Fact]
    public async Task VirtualFileNamesAreRelativeAndSafe()
    {
        var server = new HostileProvider();
        var top = server.AddFolder("/", "Folder");
        foreach (var name in HostileNames)
        {
            server.AddFile(top, name);
            server.AddFile(server.AddFolder(top, name), "inside.txt");
        }

        var tree = await VirtualFileTree.BuildAsync(server, await server.ListDirectoryAsync("/"));

        Assert.NotEmpty(tree.Entries);
        foreach (var entry in tree.Entries)
        {
            Assert.False(Path.IsPathRooted(entry.RelativePath), entry.RelativePath);
            Assert.DoesNotContain(':', entry.RelativePath);
            Assert.DoesNotContain('/', entry.RelativePath);
            foreach (var component in entry.RelativePath.Split('\\'))
            {
                Assert.Equal(SafeLocalPath.FileName(component), component);
                Assert.False(CloudPath.IsTraversalName(component), entry.RelativePath);
            }

            var full = Path.GetFullPath(Path.Combine(_root, entry.RelativePath));
            Assert.StartsWith(_root + Path.DirectorySeparatorChar, full, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A server that lists whatever names it likes. Items are addressed by opaque paths
    /// ("/n7"), as an id-based API or a WebDAV href that differs from its display name would.
    /// Every download writes a few bytes, so a name that escaped would leave a file behind.
    /// </summary>
    private sealed class HostileProvider : FileProvider
    {
        private readonly Dictionary<string, List<CloudFileItem>> _children = new(StringComparer.Ordinal) { ["/"] = [] };
        private int _next;

        public string AddFolder(string parent, string name)
        {
            var path = "/n" + ++_next;
            _children[parent].Add(new CloudFileItem(path, name, path, isDirectory: true, 0, DateTimeOffset.UnixEpoch));
            _children[path] = [];
            return path;
        }

        public void AddFile(string parent, string name)
        {
            var path = "/n" + ++_next;
            _children[parent].Add(new CloudFileItem(path, name, path, isDirectory: false, 5, DateTimeOffset.UnixEpoch));
        }

        public override Task<IReadOnlyList<CloudFileItem>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
            => _children.TryGetValue(path, out var children)
                ? Task.FromResult<IReadOnlyList<CloudFileItem>>(children.ToList())
                : throw CloudProviderException.NotFound(path);

        public override async Task DownloadFileAsync(string remotePath, string localPath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
        {
            await File.WriteAllTextAsync(localPath, "pwned", cancellationToken);
            onBytes?.Invoke(5);
        }

        public override Task UploadFileAsync(string localPath, string remotePath, ByteProgress? onBytes = null, CancellationToken cancellationToken = default)
            => throw CloudProviderException.NotImplemented();

        public override Task DeleteItemAsync(string path, CancellationToken cancellationToken = default)
            => throw CloudProviderException.NotImplemented();

        public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
            => throw CloudProviderException.NotImplemented();

        public override Task RenameItemAsync(string path, string newName, CancellationToken cancellationToken = default)
            => throw CloudProviderException.NotImplemented();

        public override Task<CloudFileItem> GetFileMetadataAsync(string path, CancellationToken cancellationToken = default)
            => throw CloudProviderException.NotFound(path);
    }
}
