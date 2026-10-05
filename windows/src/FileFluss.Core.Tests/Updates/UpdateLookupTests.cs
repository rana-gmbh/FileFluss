// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Updates;

namespace FileFluss.Core.Tests.Updates;

public class UpdateLookupTests
{
    /// <summary>Both apps release from one repository; the Mac's release is newest here.</summary>
    private const string Releases = """
        [
          { "tag_name": "v0.9.0", "draft": false, "prerelease": false, "body": "Mac", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/v0.9.0",
            "assets": [
              { "name": "FileFluss-v0.9.0.dmg", "browser_download_url": "https://github.com/x/FileFluss-v0.9.0.dmg" },
              { "name": "appcast.xml", "browser_download_url": "https://github.com/x/appcast.xml" }
            ] },
          { "tag_name": "win-v1.2.0", "draft": false, "prerelease": true, "body": "beta", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v1.2.0", "assets": [] },
          { "tag_name": "win-v1.3.0", "draft": true, "prerelease": false, "body": "draft", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v1.3.0", "assets": [] },
          { "tag_name": "win-v1.0.2", "draft": false, "prerelease": false, "body": "Fixes", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v1.0.2",
            "assets": [
              { "name": "FileFluss-1.0.2-arm64-portable.zip", "browser_download_url": "https://github.com/x/FileFluss-1.0.2-arm64-portable.zip" },
              { "name": "FileFluss-1.0.2-x64-portable.zip", "browser_download_url": "https://github.com/x/FileFluss-1.0.2-x64-portable.zip" },
              { "name": "FileFluss-Setup-1.0.2-arm64.exe", "browser_download_url": "https://github.com/x/FileFluss-Setup-1.0.2-arm64.exe" },
              { "name": "FileFluss-Setup-1.0.2-x64.exe", "browser_download_url": "https://github.com/x/FileFluss-Setup-1.0.2-x64.exe" },
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/x/SHA256SUMS.txt" }
            ] },
          { "tag_name": "win-v1.0.1", "draft": false, "prerelease": false, "body": "Older", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v1.0.1", "assets": [] }
        ]
        """;

    [Fact]
    public void OnlyWindowsReleases_AreOffered()
    {
        var update = UpdateLookup.Newest(Releases, "1.0.0", "x64");

        Assert.NotNull(update);
        Assert.Equal("1.0.2", update.Version);
        Assert.Equal("Fixes", update.ReleaseNotes);
        Assert.Equal("https://github.com/rana-gmbh/FileFluss/releases/tag/win-v1.0.2", update.ReleasePage.ToString());
    }

    [Fact]
    public void MacReleasesAreIgnored_EvenWhenTheirNumberIsHigher()
        => Assert.Null(UpdateLookup.Newest(Releases, "1.0.2", "x64"));

    [Fact]
    public void PrereleasesAndDrafts_AreSkipped()
    {
        var version = UpdateLookup.Newest(Releases, "1.0.0", "x64")?.Version;
        Assert.NotEqual("1.2.0", version);
        Assert.NotEqual("1.3.0", version);
    }

    [Fact]
    public void UpToDate_IsNull()
    {
        Assert.Null(UpdateLookup.Newest(Releases, "1.0.2", "x64"));
        Assert.Null(UpdateLookup.Newest(Releases, "2.0.0", "x64"));
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("arm64")]
    public void AssetsMatchTheArchitecture(string architecture)
    {
        var update = UpdateLookup.Newest(Releases, "1.0.0", architecture)!;

        Assert.Equal($"https://github.com/x/FileFluss-Setup-1.0.2-{architecture}.exe", update.Installer!.ToString());
        Assert.Equal($"https://github.com/x/FileFluss-1.0.2-{architecture}-portable.zip", update.Portable!.ToString());
        Assert.Equal("https://github.com/x/SHA256SUMS.txt", update.Checksums!.ToString());
    }

    [Fact]
    public void AMissingArchitecture_HasNoInstaller()
    {
        const string json = """
            [{ "tag_name": "win-v2.0.0", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v2.0.0", "assets": [
                { "name": "FileFluss-Setup-2.0.0-x64.exe", "browser_download_url": "https://github.com/x/FileFluss-Setup-2.0.0-x64.exe" },
                { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/x/SHA256SUMS.txt" }
            ] }]
            """;

        var update = UpdateLookup.Newest(json, "1.0.0", "arm64")!;
        Assert.Equal("2.0.0", update.Version);
        Assert.Null(update.Installer);
        Assert.Null(update.Portable);
    }

    [Fact]
    public void NonHttpsLinks_AreIgnored()
    {
        const string page = """[{ "tag_name": "win-v9.0.0", "html_url": "http://evil.example/x", "assets": [] }]""";
        Assert.Null(UpdateLookup.Newest(page, "1.0.0", "x64"));

        const string asset = """
            [{ "tag_name": "win-v9.0.0", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v9.0.0", "assets": [
                { "name": "FileFluss-Setup-9.0.0-x64.exe", "browser_download_url": "http://evil.example/setup.exe" }
            ] }]
            """;
        Assert.Null(UpdateLookup.Newest(asset, "1.0.0", "x64")!.Installer);
    }

    [Fact]
    public void TagsThatAreNotPlainVersions_AreIgnored()
    {
        const string json = """
            [{ "tag_name": "win-v9.0.0/../../x", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/x", "assets": [] },
             { "tag_name": "win-v", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v", "assets": [] }]
            """;
        Assert.Null(UpdateLookup.Newest(json, "1.0.0", "x64"));
    }

    [Fact]
    public void UnexpectedJson_IsNotAnUpdate()
    {
        Assert.Null(UpdateLookup.Newest("""{ "message": "API rate limit exceeded" }""", "1.0.0", "x64"));
        Assert.Null(UpdateLookup.Newest("""[ 1, "two", { "tag_name": 3 } ]""", "1.0.0", "x64"));
    }

    [Theory]
    [InlineData("1.0.10", "1.0.9", true)]
    [InlineData("1.1", "1.0.9", true)]
    [InlineData("1.0", "1.0.0", false)]
    [InlineData("0.9.9", "1.0.0", false)]
    [InlineData("2.0.0-beta", "1.9.9", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    public void Versions_CompareNumerically(string latest, string current, bool newer)
        => Assert.Equal(newer, UpdateLookup.IsNewer(latest, current));

    [Fact]
    public void ChecksumsAreReadInSha256sumFormat()
    {
        var hash = new string('a', 64);
        var sums = $"{new string('b', 64)}  FileFluss-1.0.0-x64-portable.zip\r\n{hash.ToUpperInvariant()} *FileFluss-Setup-1.0.0-x64.exe\r\n";
        Assert.Equal(hash, UpdateLookup.ExpectedHash(sums, "FileFluss-Setup-1.0.0-x64.exe"));
        Assert.Null(UpdateLookup.ExpectedHash(sums, "FileFluss-Setup-1.0.0-arm64.exe"));
        Assert.Null(UpdateLookup.ExpectedHash("short  FileFluss-Setup-1.0.0-x64.exe", "FileFluss-Setup-1.0.0-x64.exe"));
    }

    [Fact]
    public void TheDailyCheck_HonoursTheSwitchAndTheLastCheck()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        Assert.True(UpdateLookup.IsDue(true, null, now));
        Assert.True(UpdateLookup.IsDue(true, now.AddHours(-25), now));
        Assert.False(UpdateLookup.IsDue(true, now.AddHours(-23), now));
        Assert.False(UpdateLookup.IsDue(false, null, now));
        Assert.False(UpdateLookup.IsDue(false, now.AddDays(-30), now));

        // A clock set back must not stop checks for however long it was ahead.
        Assert.True(UpdateLookup.IsDue(true, now.AddDays(3), now));
    }

    [Fact]
    public async Task FetchAsync_AsksGitHubForTheReleaseList()
    {
        var http = new FakeHttpHandler().OnJson(HttpMethod.Get, "api.github.com/repos/rana-gmbh/FileFluss/releases", Releases);

        var update = await UpdateLookup.FetchAsync("1.0.0", http, "x64");

        Assert.Equal("1.0.2", update?.Version);
        var request = Assert.Single(http.Requests);
        Assert.Contains("FileFluss-Windows/1.0.0", request.Headers["User-Agent"]);
        Assert.Contains("application/vnd.github+json", request.Headers["Accept"]);
    }

    [Fact]
    public async Task FetchAsync_ReportsHttpErrors()
    {
        var http = new FakeHttpHandler().OnJson(HttpMethod.Get, "api.github.com", """{"message":"rate limited"}""", HttpStatusCode.Forbidden);

        await Assert.ThrowsAsync<HttpRequestException>(() => UpdateLookup.FetchAsync("1.0.0", http, "x64"));
    }

    [Fact]
    public async Task AnInstallerMatchingItsChecksum_IsKept()
    {
        var installer = Encoding.UTF8.GetBytes("the real setup");
        var http = Release(installer, Sha256(installer));
        using var folder = new TemporaryFolder();
        var reports = new List<double>();

        var path = await UpdateLookup.DownloadVerifiedInstallerAsync(Update, folder.Path, new SynchronousProgress(reports.Add), http);

        Assert.Equal(Path.Combine(folder.Path, "FileFluss-Setup-1.0.2-x64.exe"), path);
        Assert.Equal(installer, await File.ReadAllBytesAsync(path));
        Assert.Equal(1.0, reports[^1]);
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task AnInstallerNotMatchingItsChecksum_IsRefusedAndDeleted()
    {
        var http = Release(Encoding.UTF8.GetBytes("tampered setup"), Sha256(Encoding.UTF8.GetBytes("the real setup")));
        using var folder = new TemporaryFolder();

        await Assert.ThrowsAsync<UpdateVerificationException>(() => UpdateLookup.DownloadVerifiedInstallerAsync(Update, folder.Path, handler: http));

        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task AnInstallerMissingFromTheChecksums_IsNeverDownloaded()
    {
        var http = new FakeHttpHandler()
            .OnText(HttpMethod.Get, "SHA256SUMS.txt", $"{new string('a', 64)}  FileFluss-1.0.2-x64-portable.zip\n")
            .OnBytes(HttpMethod.Get, "FileFluss-Setup-1.0.2-x64.exe", [1, 2, 3]);
        using var folder = new TemporaryFolder();

        await Assert.ThrowsAsync<UpdateVerificationException>(() => UpdateLookup.DownloadVerifiedInstallerAsync(Update, folder.Path, handler: http));

        Assert.DoesNotContain(http.Requests, r => r.Uri.ToString().EndsWith(".exe", StringComparison.Ordinal));
        Assert.False(Directory.Exists(folder.Path) && Directory.EnumerateFileSystemEntries(folder.Path).Any());
    }

    [Fact]
    public async Task AReleaseWithoutChecksums_IsRefused()
    {
        var update = Update with { Checksums = null };
        using var folder = new TemporaryFolder();

        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            UpdateLookup.DownloadVerifiedInstallerAsync(update, folder.Path, handler: new FakeHttpHandler()));
    }

    private static readonly AvailableUpdate Update = UpdateLookup.Newest(Releases, "1.0.0", "x64")!;

    private static FakeHttpHandler Release(byte[] installer, string listedHash) => new FakeHttpHandler()
        .OnText(HttpMethod.Get, "SHA256SUMS.txt", $"{new string('b', 64)}  FileFluss-1.0.2-x64-portable.zip\n{listedHash}  FileFluss-Setup-1.0.2-x64.exe\n")
        .OnBytes(HttpMethod.Get, "FileFluss-Setup-1.0.2-x64.exe", installer);

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Progress&lt;T&gt; posts to the thread pool; this one reports in order, immediately.</summary>
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FileFluss-UpdateTests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
