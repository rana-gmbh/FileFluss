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
              { "name": "FileFluss-v0.9.0.dmg", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v0.9.0/FileFluss-v0.9.0.dmg" },
              { "name": "appcast.xml", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v0.9.0/appcast.xml" }
            ] },
          { "tag_name": "win-v1.2.0", "draft": false, "prerelease": true, "body": "beta", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v1.2.0", "assets": [] },
          { "tag_name": "win-v1.3.0", "draft": true, "prerelease": false, "body": "draft", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v1.3.0", "assets": [] },
          { "tag_name": "win-v1.0.2", "draft": false, "prerelease": false, "body": "Fixes", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v1.0.2",
            "assets": [
              { "name": "FileFluss-1.0.2-arm64-portable.zip", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/win-v1.0.2/FileFluss-1.0.2-arm64-portable.zip" },
              { "name": "FileFluss-1.0.2-x64-portable.zip", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/win-v1.0.2/FileFluss-1.0.2-x64-portable.zip" },
              { "name": "FileFluss-Setup-1.0.2-arm64.exe", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/win-v1.0.2/FileFluss-Setup-1.0.2-arm64.exe" },
              { "name": "FileFluss-Setup-1.0.2-x64.exe", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/win-v1.0.2/FileFluss-Setup-1.0.2-x64.exe" },
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/win-v1.0.2/SHA256SUMS.txt" }
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

        Assert.Equal($"https://github.com/rana-gmbh/FileFluss/releases/download/win-v1.0.2/FileFluss-Setup-1.0.2-{architecture}.exe", update.Installer!.ToString());
        Assert.Equal($"https://github.com/rana-gmbh/FileFluss/releases/download/win-v1.0.2/FileFluss-1.0.2-{architecture}-portable.zip", update.Portable!.ToString());
        Assert.Equal("https://github.com/rana-gmbh/FileFluss/releases/download/win-v1.0.2/SHA256SUMS.txt", update.Checksums!.ToString());
    }

    [Fact]
    public void AMissingArchitecture_HasNoInstaller()
    {
        const string json = """
            [{ "tag_name": "win-v2.0.0", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v2.0.0", "assets": [
                { "name": "FileFluss-Setup-2.0.0-x64.exe", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/win-v2.0.0/FileFluss-Setup-2.0.0-x64.exe" },
                { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/win-v2.0.0/SHA256SUMS.txt" }
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
    [InlineData("1.6.0", "1.6.0-beta.1", true)]
    [InlineData("1.6.0-beta.2", "1.6.0-beta.1", true)]
    [InlineData("1.6.0-beta.10", "1.6.0-beta.9", true)]
    [InlineData("1.6.0-beta.1", "1.6.0", false)]
    [InlineData("1.6.0-rc.1", "1.6.0-beta.3", true)]
    [InlineData("1.6.0-beta.1+abc", "1.6.0-beta.1", false)]
    public void Versions_CompareNumerically(string latest, string current, bool newer)
        => Assert.Equal(newer, UpdateLookup.IsNewer(latest, current));

    [Fact]
    public void ABetaBuildIsOfferedTheNextBetaAndThenTheRelease()
    {
        const string json = """
        [
          { "tag_name": "win-v1.6.0-beta.2", "draft": false, "prerelease": true, "body": "b2", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v1.6.0-beta.2", "assets": [] },
          { "tag_name": "win-v1.6.0-beta.1", "draft": false, "prerelease": true, "body": "b1", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v1.6.0-beta.1", "assets": [] }
        ]
        """;
        Assert.Equal("1.6.0-beta.2", UpdateLookup.Newest(json, "1.6.0-beta.1", "x64")?.Version);

        // A final-release build never sees betas.
        Assert.Null(UpdateLookup.Newest(json, "1.5.0", "x64"));

        const string withRelease = """
        [
          { "tag_name": "win-v1.6.0", "draft": false, "prerelease": false, "body": "final", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v1.6.0", "assets": [] },
          { "tag_name": "win-v1.6.0-beta.2", "draft": false, "prerelease": true, "body": "b2", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v1.6.0-beta.2", "assets": [] }
        ]
        """;
        Assert.Equal("1.6.0", UpdateLookup.Newest(withRelease, "1.6.0-beta.1", "x64")?.Version);
    }

    /// <summary>
    /// Since the apps ship together, one release under a plain "v" tag carries both. Older
    /// "v" releases are the Mac's alone, and must not be offered — "1.6" is newer than
    /// "1.6.0-beta.2", but there is nothing in it to install on Windows.
    /// </summary>
    private const string JointReleases = """
        [
          { "tag_name": "v1.7.0", "draft": false, "prerelease": false, "body": "Both", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/v1.7.0",
            "assets": [
              { "name": "FileFluss-v1.7.0.dmg", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v1.7.0/FileFluss-v1.7.0.dmg" },
              { "name": "FileFluss-1.7.0.zip", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v1.7.0/FileFluss-1.7.0.zip" },
              { "name": "appcast.xml", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v1.7.0/appcast.xml" },
              { "name": "FileFluss-1.7.0-x64-portable.zip", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v1.7.0/FileFluss-1.7.0-x64-portable.zip" },
              { "name": "FileFluss-Setup-1.7.0-x64.exe", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v1.7.0/FileFluss-Setup-1.7.0-x64.exe" },
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v1.7.0/SHA256SUMS.txt" }
            ] },
          { "tag_name": "v1.7.0-beta.1", "draft": false, "prerelease": true, "body": "Joint beta", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/v1.7.0-beta.1",
            "assets": [
              { "name": "FileFluss-v1.7.0-beta.1.dmg", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v1.7.0-beta.1/FileFluss-v1.7.0-beta.1.dmg" },
              { "name": "FileFluss-Setup-1.7.0-beta.1-x64.exe", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v1.7.0-beta.1/FileFluss-Setup-1.7.0-beta.1-x64.exe" },
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v1.7.0-beta.1/SHA256SUMS.txt" }
            ] },
          { "tag_name": "win-v1.6.0-beta.2", "draft": false, "prerelease": true, "body": "Windows beta", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v1.6.0-beta.2", "assets": [] },
          { "tag_name": "v1.6", "draft": false, "prerelease": false, "body": "Mac only", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/v1.6",
            "assets": [
              { "name": "FileFluss-v1.6.dmg", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v1.6/FileFluss-v1.6.dmg" },
              { "name": "FileFluss-1.6.zip", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v1.6/FileFluss-1.6.zip" },
              { "name": "appcast.xml", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v1.6/appcast.xml" }
            ] }
        ]
        """;

    [Fact]
    public void AJointRelease_IsOfferedWithItsWindowsFiles()
    {
        var update = UpdateLookup.Newest(JointReleases, "1.6.0", "x64")!;

        Assert.Equal("1.7.0", update.Version);
        Assert.Equal("Both", update.ReleaseNotes);
        Assert.Equal("https://github.com/rana-gmbh/FileFluss/releases/download/v1.7.0/FileFluss-Setup-1.7.0-x64.exe", update.Installer!.ToString());
        Assert.Equal("https://github.com/rana-gmbh/FileFluss/releases/download/v1.7.0/FileFluss-1.7.0-x64-portable.zip", update.Portable!.ToString());
        Assert.Equal("https://github.com/rana-gmbh/FileFluss/releases/download/v1.7.0/SHA256SUMS.txt", update.Checksums!.ToString());
    }

    [Fact]
    public void AMacOnlyRelease_IsNotOffered_EvenWhereItsNumberIsNewer()
    {
        const string macOnly = """
            [{ "tag_name": "v1.6", "draft": false, "prerelease": false, "body": "Mac only", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/v1.6",
               "assets": [{ "name": "FileFluss-v1.6.dmg", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/v1.6/FileFluss-v1.6.dmg" }] }]
            """;

        Assert.True(UpdateLookup.IsNewer("1.6", "1.6.0-beta.2"));
        Assert.Null(UpdateLookup.Newest(macOnly, "1.6.0-beta.2", "x64"));
        Assert.Null(UpdateLookup.Newest(macOnly, "1.5.0", "x64"));
    }

    [Fact]
    public void BetaTesters_MoveFromWindowsBetasToJointReleases()
    {
        // The next joint beta, then the joint release — never the Mac-only v1.6.
        Assert.Equal("1.7.0", UpdateLookup.Newest(JointReleases, "1.6.0-beta.2", "x64")?.Version);

        var withoutFinal = JointReleases.Replace("\"tag_name\": \"v1.7.0\", \"draft\": false", "\"tag_name\": \"v1.7.0\", \"draft\": true", StringComparison.Ordinal);
        Assert.Equal("1.7.0-beta.1", UpdateLookup.Newest(withoutFinal, "1.6.0-beta.2", "x64")?.Version);

        // A final-release build still never sees betas.
        Assert.Null(UpdateLookup.Newest(withoutFinal, "1.6.0", "x64"));
    }

    [Fact]
    public void TagsThatMerelyStartWithV_AreIgnored()
    {
        const string json = """
            [{ "tag_name": "vintage", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/vintage", "assets": [
                { "name": "FileFluss-Setup-intage-x64.exe", "browser_download_url": "https://github.com/rana-gmbh/FileFluss/releases/download/vintage/FileFluss-Setup-intage-x64.exe" }
            ] },
             { "tag_name": "v", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/v", "assets": [] }]
            """;
        Assert.Null(UpdateLookup.Newest(json, "1.0.0", "x64"));
    }

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

        using (var verified = await UpdateLookup.DownloadVerifiedInstallerAsync(Update, folder.Path, Signed, new SynchronousProgress(reports.Add), http))
        {
            Assert.Equal(Path.Combine(folder.Path, "FileFluss-Setup-1.0.2-x64.exe"), verified.Path);
            Assert.Equal(installer, await File.ReadAllBytesAsync(verified.Path));
            Assert.Equal(1.0, reports[^1]);
            Assert.Single(Directory.GetFiles(folder.Path));
        }
    }

    [Fact]
    public async Task AVerifiedInstaller_CannotBeSwappedBeforeItIsLaunched()
    {
        var installer = Encoding.UTF8.GetBytes("the real setup");
        using var folder = new TemporaryFolder();

        using (var verified = await UpdateLookup.DownloadVerifiedInstallerAsync(Update, folder.Path, Signed, handler: Release(installer, Sha256(installer))))
        {
            // Another process trying to replace, rename or delete it while the handle is held.
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(verified.Path, [1, 2, 3]));
            Assert.ThrowsAny<IOException>(() => File.Move(verified.Path, verified.Path + ".moved"));
            Assert.ThrowsAny<IOException>(() => File.Delete(verified.Path));

            // …while reading it (as starting the setup does) still works.
            Assert.Equal(installer, await File.ReadAllBytesAsync(verified.Path));
        }

        // Released once disposed.
        File.Delete(Path.Combine(folder.Path, "FileFluss-Setup-1.0.2-x64.exe"));
    }

    [Theory]
    [InlineData("https://evil.example/rana-gmbh/FileFluss/releases/download/win-v9.0.0/FileFluss-Setup-9.0.0-x64.exe")]
    [InlineData("https://github.com/someone-else/FileFluss/releases/download/win-v9.0.0/FileFluss-Setup-9.0.0-x64.exe")]
    [InlineData("https://github.com/rana-gmbh/FileFluss/releases/download/win-v1.0.0/FileFluss-Setup-9.0.0-x64.exe")]
    [InlineData("https://github.com/rana-gmbh/FileFluss/releases/download/win-v9.0.0/../../../../evil/FileFluss-Setup-9.0.0-x64.exe")]
    [InlineData("https://github.com:8443/rana-gmbh/FileFluss/releases/download/win-v9.0.0/FileFluss-Setup-9.0.0-x64.exe")]
    [InlineData("https://user@github.com/rana-gmbh/FileFluss/releases/download/win-v9.0.0/FileFluss-Setup-9.0.0-x64.exe")]
    [InlineData("https://github.com.evil.example/rana-gmbh/FileFluss/releases/download/win-v9.0.0/FileFluss-Setup-9.0.0-x64.exe")]
    public void AssetsOutsideThisReleaseOfThisRepository_AreIgnored(string url)
    {
        var json = $$"""
            [{ "tag_name": "win-v9.0.0", "html_url": "https://github.com/rana-gmbh/FileFluss/releases/tag/win-v9.0.0", "assets": [
                { "name": "FileFluss-Setup-9.0.0-x64.exe", "browser_download_url": "{{url}}" },
                { "name": "SHA256SUMS.txt", "browser_download_url": "{{url.Replace("FileFluss-Setup-9.0.0-x64.exe", "SHA256SUMS.txt", StringComparison.Ordinal)}}" }
            ] }]
            """;

        var update = UpdateLookup.Newest(json, "1.0.0", "x64")!;
        Assert.Null(update.Installer);
        Assert.Null(update.Checksums);
    }

    [Fact]
    public void AReleasePageOutsideTheRepository_IsNotOffered()
    {
        const string json = """[{ "tag_name": "win-v9.0.0", "html_url": "https://github.com/someone-else/FileFluss/releases/tag/win-v9.0.0", "assets": [] }]""";
        Assert.Null(UpdateLookup.Newest(json, "1.0.0", "x64"));
    }

    [Fact]
    public async Task AnInstallerNotMatchingItsChecksum_IsRefusedAndDeleted()
    {
        var http = Release(Encoding.UTF8.GetBytes("tampered setup"), Sha256(Encoding.UTF8.GetBytes("the real setup")));
        using var folder = new TemporaryFolder();

        await Assert.ThrowsAsync<UpdateVerificationException>(() => UpdateLookup.DownloadVerifiedInstallerAsync(Update, folder.Path, Signed, handler: http));

        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task AnInstallerMissingFromTheChecksums_IsNeverDownloaded()
    {
        var http = new FakeHttpHandler()
            .OnText(HttpMethod.Get, "SHA256SUMS.txt", $"{new string('a', 64)}  FileFluss-1.0.2-x64-portable.zip\n")
            .OnBytes(HttpMethod.Get, "FileFluss-Setup-1.0.2-x64.exe", [1, 2, 3]);
        using var folder = new TemporaryFolder();

        await Assert.ThrowsAsync<UpdateVerificationException>(() => UpdateLookup.DownloadVerifiedInstallerAsync(Update, folder.Path, Signed, handler: http));

        Assert.DoesNotContain(http.Requests, r => r.Uri.ToString().EndsWith(".exe", StringComparison.Ordinal));
        Assert.False(Directory.Exists(folder.Path) && Directory.EnumerateFileSystemEntries(folder.Path).Any());
    }

    [Fact]
    public async Task AReleaseWithoutChecksums_IsRefused()
    {
        var update = Update with { Checksums = null };
        using var folder = new TemporaryFolder();

        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            UpdateLookup.DownloadVerifiedInstallerAsync(update, folder.Path, Signed, handler: new FakeHttpHandler()));
    }

    [Fact]
    public async Task AnInstallerMatchingItsChecksumButNotSignedByRanaGmbH_IsRefusedAndDeleted()
    {
        // Both files of a release replaced: the checksum list then vouches for the wrong
        // installer, and only the signature check stands in the way.
        var installer = Encoding.UTF8.GetBytes("a replaced setup");
        using var folder = new TemporaryFolder();

        var refused = await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            UpdateLookup.DownloadVerifiedInstallerAsync(Update, folder.Path, (_, _) => "it has no signature", handler: Release(installer, Sha256(installer))));

        Assert.Contains("not signed by Rana GmbH", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task TheSignatureIsCheckedOnTheFileThatIsHeldAgainstChanges()
    {
        var installer = Encoding.UTF8.GetBytes("the real setup");
        using var folder = new TemporaryFolder();
        string? checkedPath = null;

        using (var verified = await UpdateLookup.DownloadVerifiedInstallerAsync(Update, folder.Path, (path, file) =>
        {
            // Called with the final name and the handle that blocks writes, renames and deletes.
            checkedPath = path;
            Assert.False(file.IsInvalid);
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(path, [1, 2, 3]));
            Assert.ThrowsAny<IOException>(() => File.Delete(path));
            return null;
        }, handler: Release(installer, Sha256(installer))))
        {
            Assert.Equal(verified.Path, checkedPath);
        }
    }

    [Theory]
    [InlineData("CN=Rana GmbH, O=Rana GmbH, L=04275 Leipzig, S=Saxony, C=DE", true)]
    [InlineData("CN=Rana GmbH, O=Rana GmbH, L=Leipzig, C=DE", true)]
    [InlineData("CN=Rana GmbH Evil, O=Rana GmbH, L=04275 Leipzig, S=Saxony, C=DE", false)]
    [InlineData("CN=Evil Rana GmbH, O=Rana GmbH, L=04275 Leipzig, C=DE", false)]
    [InlineData("CN=rana gmbh, O=Rana GmbH, L=04275 Leipzig, C=DE", false)]
    [InlineData("CN=Rana GmbH, O=Someone Else, L=04275 Leipzig, C=DE", false)]
    [InlineData("CN=Rana GmbH, L=04275 Leipzig, C=DE", false)]
    [InlineData("CN=Rana GmbH, O=Rana GmbH, L=Munich, C=DE", false)]
    [InlineData("CN=Rana GmbH, O=Rana GmbH, L=Not-Leipzig, C=DE", false)]
    [InlineData("CN=Rana GmbH, O=Rana GmbH, L=04275 Leipzig, C=AT", false)]
    [InlineData("CN=Rana GmbH, CN=Other, O=Rana GmbH, L=04275 Leipzig, C=DE", false)]
    [InlineData("CN=Rana GmbH + O=Rana GmbH, L=04275 Leipzig, C=DE", false)]
    [InlineData("", false)]
    public void TheSignerPinMatchesRanaGmbHOnly(string subject, bool expected)
        => Assert.Equal(expected, UpdateSigner.IsExpectedSubject(subject));

    [Theory]
    [InlineData("CN=Microsoft ID Verified CS EOC CA 04, O=Microsoft Corporation, C=US", true)]
    [InlineData("CN=Microsoft ID Verified CS AOC CA 02, O=Microsoft Corporation, C=US", true)]
    [InlineData("CN=Some Other CA, O=Microsoft Corporation, C=US", false)]
    [InlineData("CN=Evil Microsoft ID Verified CS EOC CA 04, C=US", false)]
    public void TheIssuerPinAcceptsArtifactSigningCAsOnly(string issuer, bool expected)
        => Assert.Equal(expected, UpdateSigner.IsExpectedIssuer(issuer));

    /// <summary>Stands in for the Authenticode check in tests about the download itself.</summary>
    private static string? Signed(string path, Microsoft.Win32.SafeHandles.SafeFileHandle file) => null;

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
