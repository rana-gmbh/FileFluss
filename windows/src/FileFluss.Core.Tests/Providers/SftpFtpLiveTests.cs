// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Providers;
using FileFluss.Core.Providers.Ftp;
using FileFluss.Core.Providers.Sftp;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// Runs only with <c>FILEFLUSS_LIVE_TESTS=1</c>: these talk to the network, which CI doesn't have.
/// </summary>
internal sealed class RebexLiveFactAttribute : FactAttribute
{
    public RebexLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FILEFLUSS_LIVE_TESTS") != "1")
        {
            Skip = "Live test against a public server; set FILEFLUSS_LIVE_TESTS=1 to run.";
        }
    }
}

/// <summary>
/// End-to-end checks against Rebex's public read-only demo server (user "demo", password
/// "password"), which speaks SFTP, FTP and explicit FTPS: sign-in, listing and download
/// through the real SSH.NET and FluentFTP code paths.
/// </summary>
public class SftpFtpLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Host = "test.rebex.net";

    [RebexLiveFact]
    public async Task SftpSignsInListsAndDownloads()
    {
        var store = new MemoryCredentialStore();
        var id = Guid.NewGuid();
        using var provider = new SftpProvider(id, store);

        var result = await provider.ConnectAsync(
            new ConnectValues("password", new Dictionary<string, string>
            {
                ["host"] = Host,
                ["port"] = "22",
                ["username"] = "demo",
                ["password"] = "password",
                ["remotePath"] = "/",
            }),
            new ScriptedConnectPrompt());

        Assert.Equal("demo@test.rebex.net", result.AccountLabel);
        Assert.StartsWith("SHA256:", store.Load<SftpCredentials>($"sftp.{id}")!.HostKeyFingerprint);

        var items = await provider.ListDirectoryAsync("/");
        Assert.Contains(items, i => i.Name == "pub" && i.IsDirectory);
        var readme = Assert.Single(items, i => i.Name == "readme.txt");
        Assert.True(readme.Size > 0);
        Assert.True(readme.HasKnownModificationDate);

        var metadata = await provider.GetFileMetadataAsync("/readme.txt");
        Assert.Equal(readme.Size, metadata.Size);

        // lstat through SSH.NET's internal session reports a missing path as NotFound.
        var missing = await Assert.ThrowsAsync<CloudProviderException>(() => provider.GetFileMetadataAsync("/no-such-file.txt"));
        Assert.Equal(CloudErrorKind.NotFound, missing.Kind);

        var local = Path.Combine(Path.GetTempPath(), $"ff-live-sftp-{Guid.NewGuid():N}.txt");
        try
        {
            long reported = 0;
            await provider.DownloadFileAsync("/readme.txt", local, d => Interlocked.Add(ref reported, d));
            Assert.Equal(readme.Size, new FileInfo(local).Length);
            Assert.Equal(readme.Size, reported);
        }
        finally
        {
            File.Delete(local);
        }

        // A fresh provider from the stored credentials reconnects with the pinned key.
        using var restored = new SftpProvider(id, store);
        Assert.NotEmpty(await restored.ListDirectoryAsync("/pub"));

        // Server-side copy on a read-only demo server: whatever its shell offers, the attempt
        // ends promptly with a clean refusal (fall back, or the server's error), never a hang.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var copy = await Assert.ThrowsAsync<CloudProviderException>(() => provider.CopyItemAsync("/readme.txt", $"/readme-{Guid.NewGuid():N}.txt", timeout.Token));
        output.WriteLine($"Server-side copy: {copy.Kind}: {copy.Message}");
        Assert.Contains(copy.Kind, new[] { CloudErrorKind.NotImplemented, CloudErrorKind.CommandFailed });
    }

    [RebexLiveFact]
    public Task FtpSignsInListsAndDownloads() => FtpRoundTrip(useTls: false);

    [RebexLiveFact]
    public Task FtpsSignsInListsAndDownloads() => FtpRoundTrip(useTls: true);

    private static async Task FtpRoundTrip(bool useTls)
    {
        var store = new MemoryCredentialStore();
        using var provider = new FtpProvider(Guid.NewGuid(), store);

        var result = await provider.ConnectAsync(
            new ConnectValues("default", new Dictionary<string, string>
            {
                ["host"] = Host,
                ["port"] = "21",
                ["username"] = "demo",
                ["password"] = "password",
                ["remotePath"] = "/",
                ["useTLS"] = useTls ? "true" : "false",
                ["allowInvalidCertificate"] = "false",
            }),
            new ScriptedConnectPrompt());

        Assert.Equal("/", result.RootPath);
        var items = await provider.ListDirectoryAsync("/");
        Assert.Contains(items, i => i.Name == "pub" && i.IsDirectory);
        var readme = Assert.Single(items, i => i.Name == "readme.txt");
        Assert.True(readme.Size > 0);

        var local = Path.Combine(Path.GetTempPath(), $"ff-live-ftp-{Guid.NewGuid():N}.txt");
        try
        {
            long reported = 0;
            await provider.DownloadFileAsync("/readme.txt", local, d => Interlocked.Add(ref reported, d));
            Assert.Equal(readme.Size, new FileInfo(local).Length);
            Assert.Equal(readme.Size, reported);
        }
        finally
        {
            File.Delete(local);
        }

        // Parallel operations each get their own control connection.
        var pub = provider.ListDirectoryAsync("/pub");
        var root = provider.ListDirectoryAsync("/");
        var metadata = provider.GetFileMetadataAsync("/readme.txt");
        await Task.WhenAll(pub, root, metadata);
        Assert.NotEmpty(await pub);
        Assert.Equal(readme.Size, (await metadata).Size);
    }
}
