// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net.Sockets;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Sftp;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;
using Renci.SshNet.Common;

namespace FileFluss.Core.Tests.Providers;

/// <summary>A scripted SSH server: one tree, a host key, and an exec handler.</summary>
internal sealed class FakeSshServer
{
    public PosixTestTree Tree { get; } = new();

    public string Home { get; set; } = "/home/u";

    public string HostKey { get; set; } = "SHA256:first-key";

    public List<FakeSftpTransport> Opened { get; } = [];

    public List<SftpHostKeyCheck> Checks { get; } = [];

    public List<SftpCredentials> Logins { get; } = [];

    public Func<SftpCredentials, Exception?> OnConnect { get; set; } = _ => null;

    /// <summary>Exec channel; default is a POSIX shell with cp.</summary>
    public Func<string, SftpExecResult> Exec { get; set; }

    public List<string> Commands { get; } = [];

    /// <summary>Thrown once by the next ListAsync.</summary>
    public Exception? FailNextList { get; set; }

    public FakeSshServer()
    {
        Exec = DefaultShell;
    }

    public Task<ISftpTransport> ConnectAsync(SftpCredentials credentials, SftpHostKeyCheck hostKey, CancellationToken cancellationToken)
    {
        lock (Opened)
        {
            Checks.Add(hostKey);
            Logins.Add(credentials);
        }

        if (!hostKey.Verify(HostKey))
        {
            throw new SshConnectionException("Key exchange negotiation failed.");
        }

        if (OnConnect(credentials) is { } failure)
        {
            throw failure;
        }

        var transport = new FakeSftpTransport(this);
        lock (Opened)
        {
            Opened.Add(transport);
        }

        return Task.FromResult<ISftpTransport>(transport);
    }

    private SftpExecResult DefaultShell(string command)
    {
        if (command == "command -v cp")
        {
            return new SftpExecResult(0, "/bin/cp\n", string.Empty);
        }

        var parts = command.Split(" -- ");
        var paths = ParseQuoted(parts[1]);
        Tree.Copy(paths[0], paths[1]);
        return new SftpExecResult(0, string.Empty, string.Empty);
    }

    /// <summary>Reads POSIX single-quoted words.</summary>
    public static List<string> ParseQuoted(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                inQuote = !inQuote;
            }
            else if (c == '\\' && !inQuote && i + 1 < text.Length)
            {
                current.Append(text[++i]);
            }
            else if (c == ' ' && !inQuote)
            {
                words.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        words.Add(current.ToString());
        return words;
    }
}

internal sealed class FakeSftpTransport(FakeSshServer server) : ISftpTransport
{
    private PosixTestTree Tree => server.Tree;

    public bool IsConnected { get; set; } = true;

    public bool Disposed { get; private set; }

    public string WorkingDirectory => server.Home;

    public Task<IReadOnlyList<SftpEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue("LIST " + path);
        if (server.FailNextList is { } failure)
        {
            server.FailNextList = null;
            throw failure;
        }

        // Like SSH.NET, the directory itself is resolved through links.
        if (Tree.Resolve(path) is not { Node.Kind: PosixTestTree.NodeKind.Directory } directory)
        {
            throw new SftpPathNotFoundException("No such file");
        }

        var entries = new List<SftpEntry>
        {
            new(".", true, false, 4096, PosixTestTree.DefaultTime),
            new("..", true, false, 4096, PosixTestTree.DefaultTime),
        };
        entries.AddRange(Tree.Children(directory.Path).Select(c => ToEntry(c.Name, c.Node)));
        return Task.FromResult<IReadOnlyList<SftpEntry>>(entries);
    }

    public Task<SftpEntry> LStatAsync(string path, CancellationToken cancellationToken)
        => Tree.Get(path) is { } node
            ? Task.FromResult(ToEntry(PosixTestTree.Clean(path).Split('/')[^1], node))
            : throw new SftpPathNotFoundException("No such file");

    public Task<SftpEntry> StatAsync(string path, CancellationToken cancellationToken)
    {
        var name = PosixTestTree.Clean(path).Split('/')[^1];
        if (Tree.Resolve(path) is { } resolved)
        {
            return Task.FromResult(ToEntry(name, resolved.Node));
        }

        // A dangling link reports the link itself, as SSH.NET's realpath fallback does.
        return Tree.Get(path) is { } link
            ? Task.FromResult(ToEntry(name, link))
            : throw new SftpPathNotFoundException("No such file");
    }

    public async Task DownloadAsync(string path, Stream target, Action<long> onTotal, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue("GET " + path);
        var node = Tree.Resolve(path)?.Node ?? throw new SftpPathNotFoundException("No such file");
        var half = node.Content.Length / 2;
        await target.WriteAsync(node.Content.AsMemory(0, half), cancellationToken);
        onTotal(half);
        await target.WriteAsync(node.Content.AsMemory(half), cancellationToken);
        onTotal(node.Content.Length);
    }

    public async Task UploadAsync(Stream source, string path, Action<long> onTotal, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue("PUT " + path);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        onTotal(buffer.Length);
        Tree.Put(path, new PosixTestTree.Node(PosixTestTree.NodeKind.File, buffer.ToArray(), DateTime.UtcNow));
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue("MKDIR " + path);
        if (Tree.Get(path) is not null)
        {
            throw new SshException("Failure");
        }

        Tree.Dir(path);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string path, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue("REMOVE " + path);
        if (Tree.Get(path) is not { } node)
        {
            throw new SftpPathNotFoundException("No such file");
        }

        if (node.Kind == PosixTestTree.NodeKind.Directory)
        {
            throw new SshException("Failure");
        }

        Tree.Remove(path);
        return Task.CompletedTask;
    }

    public Task RemoveDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue("RMDIR " + path);
        if (Tree.Children(path).Count > 0)
        {
            throw new SshException("Failure");
        }

        Tree.Remove(path);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue($"RENAME {path} {newPath}");
        if (Tree.Get(newPath) is not null)
        {
            throw new SshException("Failure");
        }

        Tree.Move(path, newPath);
        return Task.CompletedTask;
    }

    public Task SetLastWriteTimeUtcAsync(string path, DateTime utc, CancellationToken cancellationToken)
    {
        Tree.Log.Enqueue($"SETSTAT {path} {utc:O}");
        var resolved = Tree.Resolve(path) ?? throw new SftpPathNotFoundException("No such file");
        Tree.Put(resolved.Path, resolved.Node with { Modified = utc });
        return Task.CompletedTask;
    }

    public Task<SftpExecResult> ExecuteAsync(string command, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        lock (server.Commands)
        {
            server.Commands.Add(command);
        }

        return Task.FromResult(server.Exec(command));
    }

    public void Dispose()
    {
        Disposed = true;
        IsConnected = false;
    }

    private static SftpEntry ToEntry(string name, PosixTestTree.Node node) => new(
        name,
        node.Kind == PosixTestTree.NodeKind.Directory,
        node.Kind == PosixTestTree.NodeKind.Link,
        node.Kind == PosixTestTree.NodeKind.Directory ? 4096 : node.Content.Length,
        node.Modified);
}

public class SftpProviderTests
{
    private const string PemKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----";

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly FakeSshServer _server = new();

    private string Key => $"sftp.{_accountId}";

    private SftpCredentials? Stored() => _store.Load<SftpCredentials>(Key);

    private SftpProvider Signed(string? fingerprint = "SHA256:first-key", string remotePath = "/home/u")
    {
        _store.Save(Key, new SftpCredentials { Host = "sftp.example.test", Username = "u", Password = "pw", RemotePath = remotePath, HostKeyFingerprint = fingerprint });
        return new SftpProvider(_accountId, _store, _server.ConnectAsync);
    }

    private static ConnectValues PasswordValues(string host = "sftp.example.test", string remotePath = "/home/u", string port = "22") => new("password", new Dictionary<string, string>
    {
        ["host"] = host,
        ["port"] = port,
        ["username"] = "u",
        ["remotePath"] = remotePath,
        ["password"] = " secret ",
    });

    private static ConnectValues KeyValues(string key = PemKey, string passphrase = "") => new("privateKey", new Dictionary<string, string>
    {
        ["host"] = "sftp.example.test",
        ["port"] = "22",
        ["username"] = "u",
        ["remotePath"] = "/",
        ["privateKey"] = key,
        ["passphrase"] = passphrase,
    });

    // MARK: - Connect and host keys

    [Fact]
    public async Task ConnectPinsTheHostKeyOnFirstUse()
    {
        _server.Tree.Dir("/home/u");
        using var provider = new SftpProvider(_accountId, _store, _server.ConnectAsync);

        var result = await provider.ConnectAsync(PasswordValues(port: "2222"), new ScriptedConnectPrompt());

        Assert.Equal("u@sftp.example.test", result.AccountLabel);
        Assert.Equal("/home/u", result.RootPath);
        var stored = Stored()!;
        Assert.Equal("SHA256:first-key", stored.HostKeyFingerprint);
        Assert.Equal(2222, stored.Port);
        Assert.Equal(SftpAuthMethod.Password, stored.AuthMethod);
        Assert.Equal(" secret ", stored.Password);
        Assert.Null(stored.PrivateKey);
        Assert.Null(Assert.Single(_server.Checks).Expected);
    }

    [Fact]
    public async Task RelativeRemotePathsResolveAgainstTheLoginDirectory()
    {
        _server.Tree.Dir("/home/u/docs");
        using var provider = new SftpProvider(_accountId, _store, _server.ConnectAsync);

        var result = await provider.ConnectAsync(PasswordValues(remotePath: "~/docs"), new ScriptedConnectPrompt());

        Assert.Equal("/home/u/docs", result.RootPath);
        Assert.Equal("/home/u/docs", Stored()!.RemotePath);
    }

    [Theory]
    [InlineData("", "/home/u", "/")]
    [InlineData("/", "/home/u", "/")]
    [InlineData("~", "/home/u", "/home/u")]
    [InlineData("~/a/", "/home/u", "/home/u/a")]
    [InlineData("data", "/home/u", "/home/u/data")]
    [InlineData("/srv//www/", "/home/u", "/srv/www")]
    public void RemotePathResolution(string remote, string home, string expected)
        => Assert.Equal(expected, SftpProvider.ResolveRemotePath(remote, home));

    [Theory]
    [InlineData("server.example.com", "server.example.com")]
    [InlineData("sftp://me@server.example.com:2222/home", "server.example.com")]
    [InlineData("me@server.example.com", "server.example.com")]
    public void HostIsCleaned(string raw, string expected) => Assert.Equal(expected, SftpProvider.CleanHost(raw));

    [Fact]
    public async Task MissingRemotePathFailsAndStoresNothing()
    {
        using var provider = new SftpProvider(_accountId, _store, _server.ConnectAsync);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(PasswordValues(remotePath: "/nope"), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.Null(Stored());
        Assert.True(Assert.Single(_server.Opened).Disposed);
    }

    [Fact]
    public async Task ChangedHostKeyIsAHardFailureForStoredAccounts()
    {
        _server.Tree.Dir("/home/u");
        _server.HostKey = "SHA256:other-key";
        using var provider = Signed();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/home/u"));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal(SftpErrors.HostKeyChanged().Message, error.Message);
        Assert.Equal("SHA256:first-key", Stored()!.HostKeyFingerprint);
    }

    [Fact]
    public async Task EditingAskesBeforeTrustingAChangedKey()
    {
        _server.Tree.Dir("/home/u");
        _server.HostKey = "SHA256:other-key";
        using var provider = Signed();

        var declined = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(PasswordValues(), new ScriptedConnectPrompt { ConfirmAnswer = false }));
        Assert.Equal(SftpErrors.HostKeyChanged().Message, declined.Message);
        Assert.Equal("SHA256:first-key", Stored()!.HostKeyFingerprint);
        Assert.Equal("pw", Stored()!.Password);

        await provider.ConnectAsync(PasswordValues(), new ScriptedConnectPrompt { ConfirmAnswer = true });
        Assert.Equal("SHA256:other-key", Stored()!.HostKeyFingerprint);
        Assert.Equal("SHA256:other-key", _server.Checks[^1].Expected);
    }

    [Fact]
    public async Task ANewServerStartsWithoutAPinnedKey()
    {
        _server.Tree.Dir("/home/u");
        _server.HostKey = "SHA256:new-server";
        using var provider = Signed();

        await provider.ConnectAsync(PasswordValues(host: "other.example.test"), new ScriptedConnectPrompt { ConfirmAnswer = false });

        Assert.Equal("SHA256:new-server", Stored()!.HostKeyFingerprint);
    }

    [Fact]
    public async Task LegacyAccountsPinTheKeyOnFirstConnect()
    {
        _server.Tree.Dir("/home/u");
        using var provider = Signed(fingerprint: null);

        await provider.ListDirectoryAsync("/home/u");

        Assert.Equal("SHA256:first-key", Stored()!.HostKeyFingerprint);
    }

    [Fact]
    public async Task KeyModeValidatesAndStoresTheKey()
    {
        _server.Tree.Dir("/home/u");
        using var provider = new SftpProvider(_accountId, _store, _server.ConnectAsync);

        var bad = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(KeyValues("ssh-rsa AAAA public key"), new ScriptedConnectPrompt()));
        Assert.Equal(CloudErrorKind.CommandFailed, bad.Kind);
        Assert.Empty(_server.Logins);

        await provider.ConnectAsync(KeyValues(passphrase: " pass "), new ScriptedConnectPrompt());
        var stored = Stored()!;
        Assert.Equal(SftpAuthMethod.PrivateKey, stored.AuthMethod);
        Assert.Equal(PemKey, stored.PrivateKey);
        Assert.Equal(" pass ", stored.Passphrase);
        Assert.Null(stored.Password);
        Assert.Equal("/", stored.RemotePath);
    }

    [Fact]
    public async Task EmptyPassphraseIsStoredAsNone()
    {
        _server.Tree.Dir("/home/u");
        using var provider = new SftpProvider(_accountId, _store, _server.ConnectAsync);

        await provider.ConnectAsync(KeyValues(key: "PuTTY-User-Key-File-3: ssh-ed25519\n..."), new ScriptedConnectPrompt());

        Assert.Null(Stored()!.Passphrase);
    }

    [Fact]
    public async Task FailedLoginLeavesStoredCredentialsUntouched()
    {
        using var provider = Signed();
        _server.OnConnect = _ => new SshAuthenticationException("Permission denied (password).");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(PasswordValues(), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("pw", Stored()!.Password);
    }

    [Fact]
    public void FailuresMapLikeTheMacOsStderrPatterns()
    {
        var keyOnly = SftpErrors.Map(new SshAuthenticationException("Permission denied (publickey)."), null, SftpAuthMethod.Password);
        var password = SftpErrors.Map(new SshAuthenticationException("Permission denied (password,keyboard-interactive)."), null, SftpAuthMethod.Password);
        var key = SftpErrors.Map(new SshAuthenticationException("Permission denied (password)."), null, SftpAuthMethod.PrivateKey);
        Assert.Contains("authorized_keys", keyOnly.Message);
        Assert.NotEqual(password.Message, key.Message);

        Assert.NotEqual(
            SftpErrors.Map(new SftpKeyLoadException(true, new SshException("x")), null, SftpAuthMethod.PrivateKey).Message,
            SftpErrors.Map(new SftpKeyLoadException(false, new SshException("x")), null, SftpAuthMethod.PrivateKey).Message);
        Assert.Equal(CloudErrorKind.NotFound, SftpErrors.Map(new SftpPathNotFoundException("No such file"), "/x", SftpAuthMethod.Password).Kind);
        Assert.Equal(CloudErrorKind.CommandFailed, SftpErrors.Map(new SocketException((int)SocketError.ConnectionRefused), null, SftpAuthMethod.Password).Kind);
        Assert.Equal(CloudErrorKind.CommandFailed, SftpErrors.Map(new SocketException((int)SocketError.HostNotFound), null, SftpAuthMethod.Password).Kind);
        Assert.Equal(CloudErrorKind.NetworkError, SftpErrors.Map(new SshConnectionException("Connection reset"), null, SftpAuthMethod.Password).Kind);
        Assert.Equal(CloudErrorKind.CommandFailed, SftpErrors.Map(new SftpPermissionDeniedException("Permission denied"), "/root", SftpAuthMethod.Password).Kind);
    }

    // MARK: - Listing (issues #31/#42)

    [Fact]
    public async Task ListingFollowsSymlinksForDisplay()
    {
        _server.Tree
            .File("/data/real/inside.txt", "inside")
            .File("/data/big.bin", "0123456789")
            .Link("/home/u/to-dir", "/data/real")
            .Link("/home/u/to-file", "/data/big.bin")
            .Link("/home/u/dangling", "/data/nothing")
            .File("/home/u/.profile", "x")
            .Dir("/home/u/sub");
        using var provider = Signed();

        var items = await provider.ListDirectoryAsync("/home/u/");

        Assert.Equal([".profile", "dangling", "sub", "to-dir", "to-file"], items.Select(i => i.Name).ToArray());
        var toDir = Assert.Single(items, i => i.Name == "to-dir");
        Assert.True(toDir.IsDirectory);
        Assert.True(toDir.IsSymlink);
        Assert.Equal("/home/u/to-dir", toDir.Path);
        var toFile = Assert.Single(items, i => i.Name == "to-file");
        Assert.False(toFile.IsDirectory);
        Assert.Equal(10, toFile.Size);
        var dangling = Assert.Single(items, i => i.Name == "dangling");
        Assert.False(dangling.IsDirectory);
        Assert.True(dangling.IsSymlink);
        Assert.Equal(0, Assert.Single(items, i => i.Name == "sub").Size);
        Assert.True(Assert.Single(items, i => i.Name == ".profile").IsHidden);

        var inside = await provider.ListDirectoryAsync("/home/u/to-dir");
        Assert.Equal("/home/u/to-dir/inside.txt", Assert.Single(inside).Path);
    }

    [Fact]
    public void NamesPrintedWithAPathKeepTheirLastComponent()
    {
        var cleaned = SftpProvider.Clean(
        [
            new SftpEntry("/VERSION", false, false, 51, PosixTestTree.DefaultTime),
            new SftpEntry("/html/.htaccess", false, false, 1, PosixTestTree.DefaultTime),
            new SftpEntry("/.", true, false, 0, PosixTestTree.DefaultTime),
            new SftpEntry("/html/..", true, false, 0, PosixTestTree.DefaultTime),
            new SftpEntry("über.txt", false, false, 1, PosixTestTree.DefaultTime),
        ]);

        Assert.Equal(["VERSION", ".htaccess", "über.txt"], cleaned.Select(e => e.Name).ToArray());
    }

    [Fact]
    public async Task UnreadableFolderIsAnErrorNotAnEmptyFolder()
    {
        _server.Tree.Dir("/root");
        using var provider = Signed();
        _server.FailNextList = new SftpPermissionDeniedException("Permission denied");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/root"));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Contains("/root", error.Message);
    }

    [Fact]
    public async Task DroppedSessionReconnectsForListings()
    {
        _server.Tree.Dir("/home/u");
        using var provider = Signed();
        await provider.ListDirectoryAsync("/home/u");

        _server.FailNextList = new SshConnectionException("An established connection was aborted by the server.");
        var items = await provider.ListDirectoryAsync("/home/u");

        Assert.Empty(items);
        Assert.Equal(2, _server.Opened.Count);
        Assert.True(_server.Opened[0].Disposed);
    }

    [Fact]
    public async Task SessionIsReusedWhileConnected()
    {
        _server.Tree.Dir("/home/u");
        using var provider = Signed();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => provider.ListDirectoryAsync("/home/u")));
        _server.Opened[0].IsConnected = false;
        await provider.ListDirectoryAsync("/home/u");

        Assert.Equal(2, _server.Opened.Count);
    }

    [Fact]
    public async Task MetadataReportsLinksResolved()
    {
        _server.Tree.Dir("/data/real").Link("/home/u/to-dir", "/data/real").File("/home/u/a.txt", "abc");
        using var provider = Signed();

        var link = await provider.GetFileMetadataAsync("/home/u/to-dir");
        var file = await provider.GetFileMetadataAsync("/home/u/a.txt");

        Assert.True(link.IsDirectory);
        Assert.True(link.IsSymlink);
        Assert.Equal(3, file.Size);
        Assert.Equal(new DateTimeOffset(PosixTestTree.DefaultTime), file.ModificationDate);
        Assert.Equal(CloudErrorKind.NotFound, (await Assert.ThrowsAsync<CloudProviderException>(() => provider.GetFileMetadataAsync("/home/u/none"))).Kind);
    }

    [Fact]
    public async Task FolderSizeAndSearchDoNotFollowLinkLoops()
    {
        _server.Tree.File("/home/u/a.txt", "12345").File("/home/u/sub/b.txt", "123").Link("/home/u/sub/loop", "/home/u");
        using var provider = Signed();

        Assert.Equal(8, await provider.FolderSizeAsync("/home/u"));
        var found = await provider.SearchFilesAsync("B.TXT", "/home/u");
        Assert.Equal("/home/u/sub/b.txt", Assert.Single(found!).Path);
    }

    // MARK: - Transfers

    [Fact]
    public async Task DownloadReportsDeltasAndStagesTheFile()
    {
        _server.Tree.File("/home/u/f.bin", "0123456789");
        using var provider = Signed();
        var local = Path.Combine(Path.GetTempPath(), $"ff-sftp-{Guid.NewGuid():N}.bin");
        try
        {
            var deltas = new List<long>();
            await provider.DownloadFileAsync("/home/u/f.bin", local, d => deltas.Add(d));

            Assert.Equal("0123456789", await File.ReadAllTextAsync(local));
            Assert.Equal([5L, 5L], deltas);
        }
        finally
        {
            File.Delete(local);
        }

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.DownloadFileAsync("/home/u/missing", local));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
        Assert.False(File.Exists(local + ".fileflusspart"));
    }

    [Fact]
    public async Task UploadKeepsTheLocalModificationTime()
    {
        var local = Path.Combine(Path.GetTempPath(), $"ff-sftp-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(local, "payload");
        var stamp = new DateTime(2021, 7, 8, 9, 10, 11, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(local, stamp);
        try
        {
            _server.Tree.Dir("/home/u");
            using var provider = Signed();
            long reported = 0;

            await provider.UploadFileAsync(local, "/home/u/p.txt", d => reported += d);

            Assert.Equal(7, reported);
            Assert.Equal("payload", _server.Tree.Text("/home/u/p.txt"));
            Assert.Equal(stamp, _server.Tree.Get("/home/u/p.txt")!.Modified);
        }
        finally
        {
            File.Delete(local);
        }
    }

    // MARK: - File operations

    [Fact]
    public async Task DeletingALinkNeverTouchesItsTarget()
    {
        _server.Tree.File("/data/real/precious.txt", "keep").Link("/home/u/link", "/data/real");
        _server.Tree.File("/home/u/dir/a.txt", "a").Link("/home/u/dir/inner", "/data/real").File("/home/u/dir/sub/b.txt", "b");
        using var provider = Signed();

        await provider.DeleteItemAsync("/home/u/link");
        await provider.DeleteItemAsync("/home/u/dir");

        Assert.Null(_server.Tree.Get("/home/u/link"));
        Assert.Null(_server.Tree.Get("/home/u/dir"));
        Assert.Equal("keep", _server.Tree.Text("/data/real/precious.txt"));
        Assert.DoesNotContain(_server.Tree.Log, l => l.Contains("/data/real", StringComparison.Ordinal));
        var log = _server.Tree.Log.ToList();
        Assert.True(log.IndexOf("RMDIR /home/u/dir/sub") < log.IndexOf("RMDIR /home/u/dir"));
    }

    [Fact]
    public async Task RenameAndMoveAreServerSide()
    {
        _server.Tree.File("/home/u/a.txt", "a").Dir("/home/u/dest").File("/home/u/dest/exists.txt", "e");
        using var provider = Signed();

        await provider.RenameItemAsync("/home/u/a.txt", "b.txt");
        await provider.MoveItemAsync("/home/u/b.txt", "/home/u/dest/b.txt");
        var clash = await Assert.ThrowsAsync<CloudProviderException>(() => provider.MoveItemAsync("/home/u/dest/b.txt", "/home/u/dest/exists.txt"));

        Assert.Equal("a", _server.Tree.Text("/home/u/dest/b.txt"));
        Assert.Contains("RENAME /home/u/a.txt /home/u/b.txt", _server.Tree.Log);
        Assert.Equal(CloudErrorKind.CommandFailed, clash.Kind);
        Assert.Equal("e", _server.Tree.Text("/home/u/dest/exists.txt"));
    }

    [Fact]
    public async Task MkdirIsIdempotentButNotOverAFile()
    {
        _server.Tree.Dir("/home/u/x").File("/home/u/file", "f");
        using var provider = Signed();

        await provider.CreateDirectoryAsync("/home/u/x");
        await provider.CreateDirectoryAsync("/home/u/y");
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateDirectoryAsync("/home/u/file"));

        Assert.Equal(PosixTestTree.NodeKind.Directory, _server.Tree.Get("/home/u/y")!.Kind);
        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
    }

    [Fact]
    public async Task SetModificationDateStampsTheFile()
    {
        _server.Tree.File("/home/u/a.txt", "a");
        using var provider = Signed();

        await provider.SetModificationDateAsync("/home/u/a.txt", new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(1)));

        Assert.Equal(new DateTime(2020, 1, 2, 2, 4, 5, DateTimeKind.Utc), _server.Tree.Get("/home/u/a.txt")!.Modified);
    }

    // MARK: - Server-side copy (issue #62)

    [Fact]
    public async Task CopyRunsCpOnTheShellWithQuotedPaths()
    {
        _server.Tree.File("/home/u/it's a dir/f.txt", "f");
        using var provider = Signed();

        await provider.CopyItemAsync("/home/u/it's a dir", "/home/u/copy -n");

        Assert.Equal(["command -v cp", "cp -Rp -- '/home/u/it'\\''s a dir' '/home/u/copy -n'"], _server.Commands);
        Assert.Equal("f", _server.Tree.Text("/home/u/copy -n/f.txt"));

        await provider.CopyItemAsync("/home/u/it's a dir/f.txt", "/home/u/g.txt");
        Assert.Equal(3, _server.Commands.Count);
    }

    [Fact]
    public async Task CopyOntoAnExistingTargetIsRefused()
    {
        _server.Tree.Dir("/home/u/a").Dir("/home/u/b");
        using var provider = Signed();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/home/u/a", "/home/u/b"));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Empty(_server.Commands);
    }

    [Theory]
    [InlineData(127)]
    [InlineData(1)]
    public async Task NoShellMeansNotImplementedAndIsRemembered(int probeExit)
    {
        _server.Tree.File("/home/u/a.txt", "a");
        _server.Exec = _ => new SftpExecResult(probeExit, string.Empty, "This service allows sftp connections only.");
        using var provider = Signed();

        var first = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/home/u/a.txt", "/home/u/b.txt"));
        var second = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/home/u/a.txt", "/home/u/c.txt"));

        Assert.Equal(CloudErrorKind.NotImplemented, first.Kind);
        Assert.Equal(CloudErrorKind.NotImplemented, second.Kind);
        Assert.Single(_server.Commands);
    }

    [Fact]
    public async Task RefusedExecChannelMeansNotImplemented()
    {
        _server.Tree.File("/home/u/a.txt", "a");
        _server.Exec = _ => throw new SshException("Failed to open a channel.");
        using var provider = Signed();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/home/u/a.txt", "/home/u/b.txt"));

        Assert.Equal(CloudErrorKind.NotImplemented, error.Kind);
    }

    [Fact]
    public async Task FailingCpSurfacesItsMessage()
    {
        _server.Tree.File("/home/u/a.txt", "a");
        _server.Exec = command => command.StartsWith("command", StringComparison.Ordinal)
            ? new SftpExecResult(0, "/usr/bin/cp", string.Empty)
            : new SftpExecResult(1, string.Empty, "cp: cannot create regular file '/home/u/b.txt': Disk quota exceeded\n");
        using var provider = Signed();

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/home/u/a.txt", "/home/u/b.txt"));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Contains("Disk quota exceeded", error.Message);
    }

    [Fact]
    public void ShellQuotingSurvivesQuotesAndDashes()
    {
        Assert.Equal("'plain'", SftpProvider.ShellQuote("plain"));
        Assert.Equal("'it'\\''s'", SftpProvider.ShellQuote("it's"));
        Assert.Equal(["it's", "-rf $HOME"], FakeSshServer.ParseQuoted(SftpProvider.ShellQuote("it's") + " " + SftpProvider.ShellQuote("-rf $HOME")));
    }

    // MARK: - Wiring

    [Fact]
    public void SshNetInternalsForSymlinkSafeOperationsAreReachable()
    {
        // Deleting or renaming a symlink must not go through SSH.NET's realpath canonicalisation.
        // If this fails after an SSH.NET upgrade, re-bind SshNetSftpTransport to the new internals.
        Assert.True(SshNetSftpTransport.RawSessionAvailable);
    }

    [Fact]
    public void ConnectSpecOffersPasswordAndKeyModes()
    {
        using var provider = new SftpProvider(_accountId, _store, _server.ConnectAsync);
        var spec = provider.ConnectSpec;

        Assert.Equal(["password", "privateKey"], spec.Modes.Select(m => m.Id).ToArray());
        Assert.Contains(spec.Modes[1].Fields, f => f.Kind == ConnectFieldKind.FileContents && f.Key == "privateKey");
        Assert.Contains(spec.Modes[0].Fields, f => f.Key == "port" && f.DefaultValue == "22");
        Assert.False(provider.IsAuthenticated);
    }

    [Fact]
    public async Task NotSignedInWithoutCredentials()
    {
        using var provider = new SftpProvider(_accountId, _store, _server.ConnectAsync);
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/"));
        Assert.Equal(CloudErrorKind.NotAuthenticated, error.Kind);
    }

    [Fact]
    public void EditingReopensTheModeTheAccountWasSetUpWith()
    {
        using var password = Signed();
        Assert.Equal("password", password.GetEditableConnectValues()[ConnectSpec.ModeKey]);

        _store.Save(Key, Stored()! with { AuthMethod = SftpAuthMethod.PrivateKey, Password = null, PrivateKey = PemKey });
        using var key = new SftpProvider(_accountId, _store, _server.ConnectAsync);
        Assert.Equal("privateKey", key.GetEditableConnectValues()[ConnectSpec.ModeKey]);
        Assert.Contains(key.ConnectSpec.Modes, m => m.Id == "privateKey");
    }

    [Fact]
    public void RegistryCreatesTheProvider()
    {
        using var provider = CloudProviderRegistry.Create(CloudProviderType.Sftp, _accountId, _store);
        Assert.IsType<SftpProvider>(provider);
    }
}
