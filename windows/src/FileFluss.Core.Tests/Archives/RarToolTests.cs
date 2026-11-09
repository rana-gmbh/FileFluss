// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using FileFluss.Core.Archives;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Archives;

/// <summary>Fetching RARLAB's rar tool: nothing unverified is ever unpacked, let alone run.</summary>
public sealed class RarToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-rar-" + Guid.NewGuid().ToString("N"));
    private readonly string _savedInstallDirectory = RarTool.InstallDirectory;

    public RarToolTests()
    {
        Directory.CreateDirectory(_root);
        RarTool.InstallDirectory = Path.Combine(_root, "Tools", "rar");
    }

    public void Dispose()
    {
        RarTool.InstallDirectory = _savedInstallDirectory;
        RarTool.SearchPathsOverride = null;
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void TheDownloadIsPinnedToRarlabOverHttps()
    {
        Assert.Equal("https", RarTool.InstallerUrl.Scheme);
        Assert.Equal("www.rarlab.com", RarTool.InstallerUrl.Host);
        Assert.Matches("^[0-9a-f]{64}$", RarTool.InstallerSha256);
        Assert.Contains(RarTool.Version.Replace(".", string.Empty, StringComparison.Ordinal), RarTool.InstallerUrl.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMismatchedDownloadIsDiscarded()
    {
        var http = new FakeHttpHandler().OnBytes(HttpMethod.Get, "rarlab.com/rar/", "MZ not the real installer"u8.ToArray());

        var error = await Assert.ThrowsAsync<ArchiveException>(() => RarTool.InstallAsync(http));

        Assert.Contains("fingerprint", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(RarTool.InstallDirectory));
    }

    [Fact]
    public async Task AFailedDownloadSaysSo()
    {
        var http = new FakeHttpHandler().OnBytes(HttpMethod.Get, "rarlab.com/rar/", [], HttpStatusCode.NotFound);

        var error = await Assert.ThrowsAsync<ArchiveException>(() => RarTool.InstallAsync(http));

        Assert.Contains("404", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(RarTool.InstallDirectory));
    }

    [Fact]
    public void FindsTheFirstToolThatIsThere()
    {
        var installed = Path.Combine(_root, "WinRAR", "Rar.exe");
        RarTool.SearchPathsOverride = [Path.Combine(_root, "missing", "Rar.exe"), installed];
        Assert.Null(RarTool.Find());
        Assert.False(ArchiveCreationFormat.Rar.IsAvailable());

        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllBytes(installed, []);
        Assert.Equal(installed, RarTool.Find());
        Assert.True(ArchiveCreationFormat.Rar.IsAvailable());
    }

    [Fact]
    public void AnInstallerThatIsNoRarArchiveYieldsNoTool()
    {
        var fake = Path.Combine(_root, "fake.exe");
        File.WriteAllBytes(fake, new byte[4096]);
        var destination = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;

        Assert.Throws<ArchiveException>(() => RarTool.ExtractTool(fake, destination, CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }
}
