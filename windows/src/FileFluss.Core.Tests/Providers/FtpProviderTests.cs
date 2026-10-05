// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net.Sockets;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Ftp;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;
using FluentFTP.Exceptions;

namespace FileFluss.Core.Tests.Providers;

/// <summary>A scripted FTP server: one tree, any number of control connections.</summary>
internal sealed class FakeFtpServer
{
    public PosixTestTree Tree { get; } = new();

    public string Home { get; set; } = "/home/u";

    public bool Utc { get; set; } = true;

    public bool CanSetModifiedTime { get; set; } = true;

    public int Connections;

    public List<FakeFtpConnection> Opened { get; } = [];

    public FtpCredentials? LastCredentials { get; private set; }

    /// <summary>Thrown by the next connect attempts (login refused, unreachable…).</summary>
    public Func<FtpCredentials, Exception?> OnConnect { get; set; } = _ => null;

    /// <summary>Thrown by the next listing; null afterwards.</summary>
    public Exception? FailNextList { get; set; }

    public Task<IFtpConnection> ConnectAsync(FtpCredentials credentials, CancellationToken cancellationToken)
    {
        LastCredentials = credentials;
        if (OnConnect(credentials) is { } failure)
        {
            throw failure;
        }

        Interlocked.Increment(ref Connections);
        var connection = new FakeFtpConnection(this);
        lock (Opened)
        {
            Opened.Add(connection);
        }

        return Task.FromResult<IFtpConnection>(connection);
    }
}

internal sealed class FakeFtpConnection(FakeFtpServer server) : IFtpConnection
{
    private PosixTestTree Tree => server.Tree;

    public bool IsConnected { get; set; } = true;

    public bool Disposed { get; private set; }

    public bool ListsInUtc => server.Utc;

    public bool CanSetModifiedTime => server.CanSetModifiedTime;

    public Task<string> GetWorkingDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult(server.Home);

    public Task<bool> IsAliveAsync(CancellationToken cancellationToken) => Task.FromResult(IsConnected);

    public Task<IReadOnlyList<FtpEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue("LIST " + path);
        if (server.FailNextList is { } failure)
        {
            server.FailNextList = null;
            throw failure;
        }

        if (Tree.Get(path) is not { Kind: PosixTestTree.NodeKind.Directory })
        {
            throw new FtpCommandException("550", "No such file or directory");
        }

        var entries = new List<FtpEntry>
        {
            new(".", FtpEntryKind.Directory, 0, DateTime.MinValue),
            new("..", FtpEntryKind.Directory, 0, DateTime.MinValue),
        };
        foreach (var (name, node) in Tree.Children(path))
        {
            var kind = node.Kind switch
            {
                PosixTestTree.NodeKind.Directory => FtpEntryKind.Directory,
                PosixTestTree.NodeKind.Link => FtpEntryKind.Link,
                _ => FtpEntryKind.File,
            };
            entries.Add(new FtpEntry(name, kind, node.Content.Length, server.Utc ? node.Modified : node.Modified.ToLocalTime()));
        }

        return Task.FromResult<IReadOnlyList<FtpEntry>>(entries);
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken)
        => Task.FromResult(Tree.Get(path) is { Kind: PosixTestTree.NodeKind.Directory });

    public async Task DownloadAsync(string path, Stream target, Action<long> onTotal, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue("RETR " + path);
        var node = Tree.Get(path) ?? throw new FtpCommandException("550", "No such file or directory");
        var half = node.Content.Length / 2;
        await target.WriteAsync(node.Content.AsMemory(0, half), cancellationToken);
        onTotal(half);
        await target.WriteAsync(node.Content.AsMemory(half), cancellationToken);
        onTotal(node.Content.Length);
        onTotal(node.Content.Length);
    }

    public async Task UploadAsync(Stream source, string path, Action<long> onTotal, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue("STOR " + path);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        onTotal(3);
        onTotal(buffer.Length);
        Tree.Put(path, new PosixTestTree.Node(PosixTestTree.NodeKind.File, buffer.ToArray(), DateTime.UtcNow));
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue("MKD " + path);
        if (Tree.Get(path) is null)
        {
            Tree.Dir(path);
        }

        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue("DELE " + path);
        return Tree.Remove(path) ? Task.CompletedTask : throw new FtpCommandException("550", "No such file or directory");
    }

    public Task RemoveDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue("RMD " + path);
        if (Tree.Children(path).Count > 0)
        {
            throw new FtpCommandException("550", "Directory not empty");
        }

        Tree.Remove(path);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue($"RNFR {path} RNTO {newPath}");
        if (Tree.Get(newPath) is not null)
        {
            throw new FtpCommandException("550", "File exists");
        }

        Tree.Move(path, newPath);
        return Task.CompletedTask;
    }

    public Task SetModifiedTimeAsync(string path, DateTime utc, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue($"MFMT {utc:yyyyMMddHHmmss} {path}");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Disposed = true;
        IsConnected = false;
    }
}

public class FtpProviderTests
{
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeFtpServer _server = new();

    private string Key => $"ftp.{_accountId}";

    private FtpProvider Signed(string remotePath = "/")
    {
        _store.Save(Key, new FtpCredentials { Host = "ftp.example.test", Username = "u", Password = "pw", RemotePath = remotePath });
        return new FtpProvider(_accountId, _store, _server.ConnectAsync);
    }

    private static ConnectValues Values(string host = "ftp.example.test", string remotePath = "pub", bool tls = false, bool allowInvalid = true) => new("default", new Dictionary<string, string>
    {
        ["host"] = host,
        ["port"] = "2121",
        ["username"] = " u ",
        ["password"] = " pw ",
        ["remotePath"] = remotePath,
        ["useTLS"] = tls ? "true" : "false",
        ["allowInvalidCertificate"] = allowInvalid ? "true" : "false",
    });

    // MARK: - Connect

    [Fact]
    public async Task ConnectVerifiesTheLandingFolderAndStoresTheAccount()
    {
        _server.Tree.Dir("/home/u/pub");
        using var provider = new FtpProvider(_accountId, _store, _server.ConnectAsync);

        var result = await provider.ConnectAsync(Values(host: "ftp://ftp.example.test:21/"), new ScriptedConnectPrompt());

        Assert.Equal("u@ftp.example.test", result.AccountLabel);
        Assert.Equal("/pub", result.RootPath);
        var stored = _store.Load<FtpCredentials>(Key)!;
        Assert.Equal("ftp.example.test", stored.Host);
        Assert.Equal(2121, stored.Port);
        Assert.Equal("u", stored.Username);
        Assert.Equal(" pw ", stored.Password);
        Assert.False(stored.UseTls);
        Assert.False(stored.AllowInvalidCertificate);
        Assert.Contains("LIST /home/u/pub", _server.Tree.Log);

        // The probe connection is reused for the first operation.
        await provider.ListDirectoryAsync("/pub");
        Assert.Equal(1, _server.Connections);
        Assert.Equal("/pub", provider.GetEditableConnectValues()["remotePath"]);
    }

    [Fact]
    public async Task FtpsKeepsTheSelfSignedChoice()
    {
        _server.Tree.Dir("/home/u/pub");
        using var provider = new FtpProvider(_accountId, _store, _server.ConnectAsync);

        await provider.ConnectAsync(Values(tls: true), new ScriptedConnectPrompt());

        Assert.True(_server.LastCredentials!.UseTls);
        Assert.True(_server.LastCredentials.AllowInvalidCertificate);
        Assert.Equal("true", provider.GetEditableConnectValues()["useTLS"]);
    }

    [Fact]
    public async Task MissingLandingFolderFailsAndStoresNothing()
    {
        using var provider = new FtpProvider(_accountId, _store, _server.ConnectAsync);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(Values(remotePath: "/nope"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Null(_store.Load<FtpCredentials>(Key));
        Assert.False(provider.IsAuthenticated);
        Assert.All(_server.Opened, c => Assert.True(c.Disposed));
    }

    [Theory]
    [InlineData("530", "Login incorrect.", CloudErrorKind.InvalidCredentials)]
    [InlineData("530", "Non-anonymous sessions must use encryption.", CloudErrorKind.CommandFailed)]
    public async Task RefusedLoginLeavesStoredCredentialsUntouched(string code, string message, CloudErrorKind expected)
    {
        using var provider = Signed();
        _server.OnConnect = _ => new FtpAuthenticationException(code, message);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(Values(), new ScriptedConnectPrompt()));

        Assert.Equal(expected, error.Kind);
        Assert.Equal("pw", _store.Load<FtpCredentials>(Key)!.Password);
    }

    [Fact]
    public async Task UnreachableHostSaysSo()
    {
        using var provider = new FtpProvider(_accountId, _store, _server.ConnectAsync);
        _server.OnConnect = _ => new SocketException((int)SocketError.HostNotFound);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(Values(), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
    }

    [Theory]
    [InlineData("ftp.example.com", "ftp.example.com")]
    [InlineData(" ftps://ftp.example.com:990/pub ", "ftp.example.com")]
    [InlineData("ftp.example.com:2121", "ftp.example.com")]
    [InlineData("::1", "::1")]
    public void HostIsCleaned(string raw, string expected) => Assert.Equal(expected, FtpProvider.CleanHost(raw));

    // MARK: - Paths and listing

    [Theory]
    [InlineData("/home/u", "/", "/home/u")]
    [InlineData("/home/u/", "/pub/a b", "/home/u/pub/a b")]
    [InlineData("/", "/pub", "/pub")]
    [InlineData("/", "/", "/")]
    public void AppPathsMapUnderTheLoginDirectory(string home, string path, string expected)
        => Assert.Equal(expected, FtpProvider.ServerPath(home, path));

    [Fact]
    public async Task ListingSkipsDotEntriesAndKeepsLinksAsFiles()
    {
        var modified = new DateTime(2023, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        _server.Tree.Dir("/home/u/pub/docs", modified).File("/home/u/pub/a.txt", "hello", modified).Link("/home/u/pub/link", "/elsewhere").File("/home/u/pub/.hidden", "x");
        using var provider = Signed("/pub");

        var items = await provider.ListDirectoryAsync("/pub/");

        Assert.Equal([".hidden", "a.txt", "docs", "link"], items.Select(i => i.Name).ToArray());
        var file = Assert.Single(items, i => i.Name == "a.txt");
        Assert.Equal("/pub/a.txt", file.Path);
        Assert.Equal(5, file.Size);
        Assert.Equal(new DateTimeOffset(modified), file.ModificationDate);
        var folder = Assert.Single(items, i => i.Name == "docs");
        Assert.True(folder.IsDirectory);
        Assert.Equal(0, folder.Size);
        var link = Assert.Single(items, i => i.Name == "link");
        Assert.False(link.IsDirectory);
        Assert.True(link.IsSymlink);
        Assert.True(Assert.Single(items, i => i.Name == ".hidden").IsHidden);
        Assert.Contains("LIST /home/u/pub", _server.Tree.Log);
    }

    [Fact]
    public void ListTimesAreUtcForMlsdAndLocalForList()
    {
        var wallClock = new DateTime(2023, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);
        var entries = new[] { new FtpEntry("a", FtpEntryKind.File, 1, wallClock), new FtpEntry("/x/b", FtpEntryKind.File, 1, DateTime.MinValue) };

        var utc = FtpProvider.ToItems(entries, "/", utc: true);
        var local = FtpProvider.ToItems(entries, "/", utc: false);

        Assert.Equal(new DateTimeOffset(wallClock, TimeSpan.Zero), utc[0].ModificationDate);
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(wallClock, DateTimeKind.Local)), local[0].ModificationDate);
        Assert.Equal("b", utc[1].Name);
        Assert.False(utc[1].HasKnownModificationDate);
    }

    [Fact]
    public async Task MissingFolderIsNotFoundAndPermissionErrorsCarryTheServerText()
    {
        using var provider = Signed();

        var missing = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/nope"));
        Assert.Equal(CloudErrorKind.NotFound, missing.Kind);

        _server.Tree.Dir("/home/u/secret");
        _server.FailNextList = new FtpCommandException("550", "Permission denied");
        var denied = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/secret"));
        Assert.Equal(CloudErrorKind.CommandFailed, denied.Kind);
        Assert.Equal("Permission denied", denied.Message);

        // Server replies leave the connection usable.
        Assert.Equal(1, _server.Connections);
    }

    [Fact]
    public async Task TransportFailureRetiresTheConnection()
    {
        _server.Tree.Dir("/home/u/a");
        using var provider = Signed();
        _server.FailNextList = new IOException("connection reset");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/a"));
        Assert.Equal(CloudErrorKind.NetworkError, error.Kind);
        Assert.True(_server.Opened[0].Disposed);

        await provider.ListDirectoryAsync("/a");
        Assert.Equal(2, _server.Connections);
    }

    [Fact]
    public async Task MetadataComesFromTheParentListing()
    {
        _server.Tree.File("/home/u/a.txt", "abc");
        using var provider = Signed();

        var item = await provider.GetFileMetadataAsync("/a.txt");
        Assert.Equal(3, item.Size);
        Assert.True((await provider.GetFileMetadataAsync("/")).IsDirectory);
        Assert.Equal(CloudErrorKind.NotFound, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.GetFileMetadataAsync("/b.txt"))).Kind);
    }

    [Fact]
    public async Task SearchWalksTheTree()
    {
        _server.Tree.File("/home/u/Report.pdf", "x").File("/home/u/deep/er/old-report.txt", "y").File("/home/u/other.txt", "z");
        using var provider = Signed();

        var results = await provider.SearchFilesAsync("report", null);

        Assert.Equal(["/Report.pdf", "/deep/er/old-report.txt"], results!.Select(r => r.Path).Order(StringComparer.Ordinal).ToArray());
    }

    // MARK: - Transfers

    [Fact]
    public async Task DownloadStagesAndReportsDeltas()
    {
        _server.Tree.File("/home/u/f.bin", "0123456789");
        using var provider = Signed();
        var local = Path.Combine(Path.GetTempPath(), $"ff-ftp-{Guid.NewGuid():N}", "f.bin");
        try
        {
            var deltas = new List<long>();
            await provider.DownloadFileAsync("/f.bin", local, d => deltas.Add(d));

            Assert.Equal("0123456789", await File.ReadAllTextAsync(local));
            Assert.Equal([5L, 5L], deltas);
            Assert.False(File.Exists(local + ".fileflusspart"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(local)!, recursive: true);
        }
    }

    [Fact]
    public async Task FailedDownloadLeavesNoFile()
    {
        using var provider = Signed();
        var local = Path.Combine(Path.GetTempPath(), $"ff-ftp-{Guid.NewGuid():N}.bin");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DownloadFileAsync("/missing.bin", local));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.False(File.Exists(local));
        Assert.False(File.Exists(local + ".fileflusspart"));
    }

    [Fact]
    public async Task UploadStoresUnderTheLoginDirectory()
    {
        var local = Path.Combine(Path.GetTempPath(), $"ff-ftp-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(local, "uploaded!");
        try
        {
            _server.Tree.Dir("/home/u/in");
            using var provider = Signed();
            var deltas = new List<long>();

            await provider.UploadFileAsync(local, "/in/x.txt", d => deltas.Add(d));

            Assert.Equal("uploaded!", _server.Tree.Text("/home/u/in/x.txt"));
            Assert.Equal([3L, 6L], deltas);
        }
        finally
        {
            File.Delete(local);
        }
    }

    // MARK: - File operations

    [Fact]
    public async Task DeleteEmptiesFoldersDepthFirst()
    {
        _server.Tree.File("/home/u/d/a.txt", "a").File("/home/u/d/sub/b.txt", "b").File("/home/u/keep.txt", "k");
        using var provider = Signed();

        await provider.DeleteItemAsync("/d");

        Assert.Null(_server.Tree.Get("/home/u/d"));
        Assert.NotNull(_server.Tree.Get("/home/u/keep.txt"));
        var log = _server.Tree.Log.ToList();
        Assert.True(log.IndexOf("RMD /home/u/d/sub") < log.IndexOf("RMD /home/u/d"));
        Assert.Contains("DELE /home/u/d/sub/b.txt", log);
    }

    [Fact]
    public async Task DeleteOfAMissingItemIsNotFound()
    {
        using var provider = Signed();
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DeleteItemAsync("/ghost"));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task RenameAndMoveUseRnfrRnto()
    {
        _server.Tree.File("/home/u/a.txt", "a").Dir("/home/u/dest");
        using var provider = Signed();

        await provider.RenameItemAsync("/a.txt", "b.txt");
        await provider.MoveItemAsync("/b.txt", "/dest/b.txt");

        Assert.Contains("RNFR /home/u/a.txt RNTO /home/u/b.txt", _server.Tree.Log);
        Assert.Contains("RNFR /home/u/b.txt RNTO /home/u/dest/b.txt", _server.Tree.Log);
        Assert.Equal("a", _server.Tree.Text("/home/u/dest/b.txt"));
    }

    [Fact]
    public async Task MkdirIsIdempotent()
    {
        _server.Tree.Dir("/home/u/x");
        using var provider = Signed();

        await provider.CreateDirectoryAsync("/x");
        await provider.CreateDirectoryAsync("/x/y");

        Assert.NotNull(_server.Tree.Get("/home/u/x/y"));
    }

    [Fact]
    public async Task ModificationDateUsesMfmtWhereSupported()
    {
        _server.Tree.File("/home/u/a.txt", "a");
        using var provider = Signed();

        await provider.SetModificationDateAsync("/a.txt", new DateTimeOffset(2020, 2, 3, 4, 5, 6, TimeSpan.FromHours(2)));
        Assert.Contains("MFMT 20200203020506 /home/u/a.txt", _server.Tree.Log);

        _server.CanSetModifiedTime = false;
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.SetModificationDateAsync("/a.txt", DateTimeOffset.Now));
        Assert.Equal(CloudErrorKind.NotImplemented, error.Kind);
    }

    [Fact]
    public async Task CopyIsNotServerSide()
    {
        using var provider = Signed();
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/a", "/b"));
        Assert.Equal(CloudErrorKind.NotImplemented, error.Kind);
    }

    [Fact]
    public async Task NotSignedInWithoutCredentials()
    {
        using var provider = new FtpProvider(_accountId, _store, _server.ConnectAsync);
        Assert.False(provider.IsAuthenticated);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
    }

    [Theory]
    [InlineData("550", "Can't find file", CloudErrorKind.NotFound)]
    [InlineData("550", "Access is denied.", CloudErrorKind.CommandFailed)]
    [InlineData("552", "Exceeded storage allocation", CloudErrorKind.QuotaExceeded)]
    [InlineData("530", "Not logged in.", CloudErrorKind.InvalidCredentials)]
    [InlineData("451", "Local error", CloudErrorKind.CommandFailed)]
    public void RepliesMapToKinds(string code, string message, CloudErrorKind expected)
        => Assert.Equal(expected, FtpErrors.MapReply(code, message, "/x").Kind);

    // MARK: - Pool

    [Fact]
    public async Task PoolOpensOneConnectionPerConcurrentOperationUpToTheLimit()
    {
        using var pool = new FtpConnectionPool(ct => _server.ConnectAsync(new FtpCredentials { Host = "h", Username = "u" }, ct), limit: 2);

        var first = await pool.LeaseAsync(CancellationToken.None);
        var second = await pool.LeaseAsync(CancellationToken.None);
        var third = pool.LeaseAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(third.IsCompleted);

        pool.Return(first, reusable: true);
        Assert.Same(first, await third);
        Assert.Equal(2, _server.Connections);

        pool.Return(second, reusable: false);
        Assert.True(((FakeFtpConnection)second).Disposed);
        Assert.Equal(1, pool.LiveCount);
    }

    [Fact]
    public async Task PoolAdaptsWhenTheServerRefusesMoreConnections()
    {
        var attempts = 0;
        using var pool = new FtpConnectionPool(ct =>
            Interlocked.Increment(ref attempts) == 2
                ? throw new FtpCommandException("421", "Too many connections (1) from this IP")
                : _server.ConnectAsync(new FtpCredentials { Host = "h", Username = "u" }, ct), limit: 3);

        var first = await pool.LeaseAsync(CancellationToken.None);
        var waiting = pool.LeaseAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(1, pool.Limit);

        pool.Return(first, reusable: true);
        Assert.Same(first, await waiting);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotSwallowAConnection()
    {
        using var pool = new FtpConnectionPool(ct => _server.ConnectAsync(new FtpCredentials { Host = "h", Username = "u" }, ct), limit: 1);
        var first = await pool.LeaseAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var waiting = pool.LeaseAsync(cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        pool.Return(first, reusable: true);
        Assert.Same(first, await pool.LeaseAsync(CancellationToken.None));
    }

    [Fact]
    public void RegistryCreatesTheProvider()
    {
        using var provider = CloudProviderRegistry.Create(CloudProviderType.Ftp, _accountId, _store);
        Assert.IsType<FtpProvider>(provider);
    }
}
