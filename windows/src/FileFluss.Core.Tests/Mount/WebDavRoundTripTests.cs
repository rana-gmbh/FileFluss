// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.WebDav;
using FileFluss.Core.Security;

namespace FileFluss.Core.Tests.Mount;

/// <summary>
/// Both ends of the repository's WebDAV code against each other: the app's own
/// <see cref="WebDavProvider"/> (a real HTTP client, signing in through its normal
/// discovery) pointed at the mount server. A WebDAV client that isn't ours would see the
/// same answers.
/// </summary>
public sealed class WebDavRoundTripTests : IAsyncLifetime
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "filefluss-roundtrip-" + Guid.NewGuid().ToString("N"));
    private MountTestServer _t = null!;
    private WebDavProvider _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_local);
        _t = await MountTestServer.StartAsync(p =>
        {
            p.AddFile("/Docs/Report 2026.txt", "quarterly", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
            p.AddFile("/Docs/ümlaut & co.txt", "äöü");
        });

        _client = new WebDavProvider(Guid.NewGuid(), new MemoryCredentialStore());
        var values = new ConnectValues("default", new Dictionary<string, string>
        {
            ["serverUrl"] = _t.Server.BaseUri.AbsoluteUri,
            ["username"] = "anyone",
            ["password"] = "ignored",
        });
        await _client.ConnectAsync(values, SilentConnectPrompt.Instance);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _t.DisposeAsync();
        try
        {
            Directory.Delete(_local, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ListsWhatTheProviderHas()
    {
        var root = await _client.ListDirectoryAsync("/");
        Assert.Equal(["Docs", "folder", "hello.txt"], root.Select(i => i.Name).Order(StringComparer.Ordinal));
        Assert.True(root.Single(i => i.Name == "folder").IsDirectory);
        Assert.Equal(2, root.Single(i => i.Name == "hello.txt").Size);

        var docs = await _client.ListDirectoryAsync("/Docs");
        var report = docs.Single(i => i.Name == "Report 2026.txt");
        Assert.Equal("/Docs/Report 2026.txt", report.Path);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), report.ModificationDate);
        Assert.Contains(docs, i => i.Name == "ümlaut & co.txt");

        var meta = await _client.GetFileMetadataAsync("/Docs/ümlaut & co.txt");
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("äöü"), meta.Size);
        await Assert.ThrowsAsync<CloudProviderException>(() => _client.GetFileMetadataAsync("/nope.txt"));
    }

    [Fact]
    public async Task DownloadsAndUploads()
    {
        var downloaded = Path.Combine(_local, "down.txt");
        await _client.DownloadFileAsync("/Docs/ümlaut & co.txt", downloaded);
        Assert.Equal("äöü", await File.ReadAllTextAsync(downloaded));

        var upload = Path.Combine(_local, "up.bin");
        var bytes = Enumerable.Range(0, 1_500_000).Select(i => (byte)(i * 7)).ToArray();
        await File.WriteAllBytesAsync(upload, bytes);
        long reported = 0;
        await _client.UploadFileAsync(upload, "/folder/Big file.bin", delta => Interlocked.Add(ref reported, delta));
        Assert.Equal(bytes.Length, reported);

        var back = Path.Combine(_local, "back.bin");
        await _client.DownloadFileAsync("/folder/Big file.bin", back);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(back));
    }

    [Fact]
    public async Task CreatesRenamesMovesCopiesAndDeletes()
    {
        await _client.CreateDirectoryAsync("/New Folder");
        Assert.True(_t.Provider.Exists("/New Folder"));

        await _client.RenameItemAsync("/hello.txt", "renamed.txt");
        Assert.Equal("hi", _t.Provider.Read("/renamed.txt"));

        await _client.MoveItemAsync("/renamed.txt", "/New Folder/moved.txt");
        Assert.Equal("hi", _t.Provider.Read("/New Folder/moved.txt"));
        Assert.False(_t.Provider.Exists("/renamed.txt"));

        await _client.CopyItemAsync("/New Folder", "/Copy of Folder");
        Assert.Equal("hi", _t.Provider.Read("/Copy of Folder/moved.txt"));

        // The client sends Overwrite: F, so an existing target is refused, not replaced.
        await Assert.ThrowsAsync<CloudProviderException>(() => _client.CopyItemAsync("/Docs/Report 2026.txt", "/folder/inside.txt"));
        Assert.Equal("nested", _t.Provider.Read("/folder/inside.txt"));

        await _client.DeleteItemAsync("/Copy of Folder");
        Assert.False(_t.Provider.Exists("/Copy of Folder/moved.txt"));
        Assert.DoesNotContain(await _client.ListDirectoryAsync("/"), i => i.Name == "Copy of Folder");
    }

    [Fact]
    public async Task ModificationDateTravelsThroughProppatch()
    {
        var date = new DateTimeOffset(2025, 12, 24, 18, 0, 0, TimeSpan.Zero);
        await _client.SetModificationDateAsync("/hello.txt", date);
        Assert.Equal(date, _t.Provider.ModifiedAt("/hello.txt"));
        Assert.Equal(date, (await _client.GetFileMetadataAsync("/hello.txt")).ModificationDate);
    }

    [Fact]
    public async Task ConnectingWithoutTheSecretFails()
    {
        using var stranger = new WebDavProvider(Guid.NewGuid(), new MemoryCredentialStore());
        var values = new ConnectValues("default", new Dictionary<string, string>
        {
            ["serverUrl"] = $"http://127.0.0.1:{_t.Server.Port}/FileFluss-0123456789abcdef0123456789abcdef",
            ["username"] = "anyone",
            ["password"] = "ignored",
        });
        await Assert.ThrowsAsync<CloudProviderException>(() => stranger.ConnectAsync(values, SilentConnectPrompt.Instance));
    }
}
