// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

// The engine only exists in Debug builds of Core, so its tests do too.
#if DEBUG

using FileFluss.Core.Diagnostics;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests;

public sealed class VersionTestEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-versiontest-" + Guid.NewGuid().ToString("N"));

    public VersionTestEngineTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Testfiles", "Nested"));
        File.WriteAllText(Path.Combine(_root, "Testfiles", "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(_root, "Testfiles", "Nested", "b.txt"), "bravo");
        File.WriteAllText(Path.Combine(_root, "Testfiles", "version-test-report-20260101-000000.md"), "old report");
        File.WriteAllText(Path.Combine(_root, "Testfiles", ".DS_Store"), "hidden");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Testfiles => Path.Combine(_root, "Testfiles");

    private static CloudAccount Account(CloudProviderType type) => new() { ProviderType = type, DisplayName = $"Test {type}", IsConnected = true };

    [Fact]
    public void EnumeratesTestfilesWithoutReportsOrHiddenFiles()
    {
        var files = VersionTestEngine.EnumerateLocalFiles(Testfiles);
        Assert.Equal(["Nested/b.txt", "a.txt"], files.Select(f => f.RelativePath));
        Assert.Equal(5, files[1].Size);
    }

    [Fact]
    public void FindsTestfilesBesideTheGitFolder()
    {
        var deep = Path.Combine(_root, "windows", "bin", "Debug");
        Directory.CreateDirectory(deep);
        Assert.Null(VersionTestEngine.FindRepositoryTestfiles(deep));

        // A worktree's .git is a file, not a folder.
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: elsewhere");
        Assert.Equal(Testfiles, VersionTestEngine.FindRepositoryTestfiles(deep));
    }

    [Fact]
    public async Task FullPassSucceedsAndLeavesNothingBehind()
    {
        var provider = new MemoryCloudProvider();
        provider.AddFile("/keep.txt", "user data");
        var files = VersionTestEngine.EnumerateLocalFiles(Testfiles);
        var progress = new RecordingProgress();

        var result = await VersionTestEngine.RunAccountAsync(Account(CloudProviderType.WebDav), provider, files, progress);

        Assert.All(result.AllSteps, s => Assert.True(s.Ok, $"{s.Label}: {s.Error}"));
        Assert.Equal(["upload Nested/b.txt", "upload a.txt"], result.Uploads.Select(s => s.Label));
        Assert.Equal(2, result.Replaces.Count);
        Assert.Equal(["share link not supported by this provider"], result.ShareLink.Select(s => s.Label));
        Assert.Equal(2, result.Deletes.Count);
        Assert.Equal(["/", "/keep.txt"], provider.AllPaths);
        // create + 2 uploads + 2 replaces + 2 deletes + cleanup, all measured.
        Assert.Equal(8, progress.Finished);
    }

    [Fact]
    public async Task FailedUploadIsNotDeletedAndReportCountsTheFailure()
    {
        var provider = new MemoryCloudProvider();
        provider.FailingUploads.Add("a.txt");
        var files = VersionTestEngine.EnumerateLocalFiles(Testfiles);

        var result = await VersionTestEngine.RunAccountAsync(Account(CloudProviderType.WebDav), provider, files);

        Assert.False(result.Uploads.Single(s => s.Label == "upload a.txt").Ok);
        Assert.NotNull(result.Uploads.Single(s => s.Label == "upload a.txt").Diagnostics);
        Assert.Contains(result.Deletes, s => s.Label == "delete a.txt — skipped, upload had failed" && s.Ok);
        Assert.Single(result.Replaces);
        Assert.Equal(result.TotalCount - 1, result.PassCount);
        Assert.Equal(["/"], provider.AllPaths);

        var markdown = VersionTestEngine.RenderMarkdown(Report(result));
        Assert.Contains("## Overall: FAIL (1 failure) — ", markdown, StringComparison.Ordinal);
        Assert.Contains("| Upload | upload a.txt | FAIL |", markdown, StringComparison.Ordinal);
        Assert.Contains("<details><summary>Diagnostics for failing steps</summary>", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShareLinkIsCreatedAndFetchedAnonymously()
    {
        var provider = new MemoryCloudProvider
        {
            ShareLinkFactory = path => new CloudShareLink(new Uri("https://share.example/page" + path))
            {
                DirectDownloadUrl = new Uri("https://share.example/direct" + path),
                Note = "granted view-only",
            },
        };
        var fetched = new List<Uri>();
        var files = VersionTestEngine.EnumerateLocalFiles(Testfiles);

        var result = await VersionTestEngine.RunAccountAsync(Account(CloudProviderType.Dropbox), provider, files, checkUrl: url =>
        {
            fetched.Add(url);
            // The provider refuses its own direct link; the share page works.
            return Task.FromResult(url.AbsolutePath.StartsWith("/direct", StringComparison.Ordinal)
                ? new PublicUrlCheck.Result(PublicUrlCheck.Outcome.NotPublic, "HTTP 403")
                : new PublicUrlCheck.Result(PublicUrlCheck.Outcome.Reachable, null));
        });

        Assert.Equal(["create share link (Nested/b.txt)", "fetch share link anonymously"], result.ShareLink.Select(s => s.Label));
        Assert.All(result.ShareLink, s => Assert.True(s.Ok));
        Assert.Equal("https://share.example/page/FileFluss%20version%20test/Nested/b.txt", result.ShareLinkUrl);
        Assert.StartsWith("granted view-only · Direct download link rejected by the provider", result.ShareLinkNote, StringComparison.Ordinal);
        Assert.Equal(2, fetched.Count);
    }

    [Fact]
    public async Task UnreachableShareServerIsReportedAsSkip()
    {
        var provider = new MemoryCloudProvider { ShareLinkFactory = path => new CloudShareLink(new Uri("https://share.example" + path)) };
        var result = await VersionTestEngine.RunAccountAsync(
            Account(CloudProviderType.NextCloud), provider, VersionTestEngine.EnumerateLocalFiles(Testfiles),
            checkUrl: _ => Task.FromResult(new PublicUrlCheck.Result(PublicUrlCheck.Outcome.Unverified, "certificate")));

        var fetch = result.ShareLink.Single(s => s.Label == "fetch share link anonymously");
        Assert.True(fetch.Ok);
        Assert.True(fetch.Skipped);
        Assert.Contains("| Share link | fetch share link anonymously | SKIP |", VersionTestEngine.RenderMarkdown(Report(result)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelStillDeletesWhatWasUploaded()
    {
        var provider = new MemoryCloudProvider();
        var progress = new RecordingProgress { CancelAfterSteps = 2 };

        var result = await VersionTestEngine.RunAccountAsync(Account(CloudProviderType.WebDav), provider, VersionTestEngine.EnumerateLocalFiles(Testfiles), progress);

        Assert.Single(result.Uploads);
        Assert.Empty(result.Replaces);
        Assert.Empty(result.ShareLink);
        Assert.Contains(result.Deletes, s => s.Label == "delete a.txt — skipped, cancelled before upload");
        Assert.True(result.CleanupFolder.Ok);
        Assert.Equal(["/"], provider.AllPaths);
    }

    [Fact]
    public async Task BucketRootedAccountRunsInsideItsBucket()
    {
        var provider = new MemoryCloudProvider(CloudProviderType.S3);
        provider.AddFolder("/zeta");
        provider.AddFolder("/alpha");
        var seen = new List<string>();
        var result = await VersionTestEngine.RunAccountAsync(Account(CloudProviderType.S3), provider, VersionTestEngine.EnumerateLocalFiles(Testfiles), new RecordingProgress { Steps = seen });

        Assert.All(result.AllSteps, s => Assert.True(s.Ok, $"{s.Label}: {s.Error}"));
        Assert.Equal(["/", "/alpha", "/zeta"], provider.AllPaths);
    }

    [Fact]
    public async Task SkippedReadOnlyAndUnavailableAccounts()
    {
        var files = VersionTestEngine.EnumerateLocalFiles(Testfiles);

        var wordpress = await VersionTestEngine.RunAccountAsync(Account(CloudProviderType.WordPress), new MemoryCloudProvider(CloudProviderType.WordPress), files);
        Assert.Equal("skipped (WordPress has no folder concept)", wordpress.CreateFolder.Label);
        Assert.Equal(2, wordpress.TotalCount);
        Assert.Equal(2, wordpress.PassCount);

        var gopro = new MemoryCloudProvider(CloudProviderType.GoPro);
        gopro.AddFile("/DCIM/GOPR0001.MP4", "video");
        var readOnly = await VersionTestEngine.RunAccountAsync(Account(CloudProviderType.GoPro), gopro, files);
        Assert.Equal(["list /", "skipped (GoPro Camera is read-only)", "skipped (GoPro Camera is read-only)"], readOnly.AllSteps.Select(s => s.Label));
        Assert.Equal(["/", "/DCIM", "/DCIM/GOPR0001.MP4"], gopro.AllPaths);

        var missing = await VersionTestEngine.RunAccountAsync(Account(CloudProviderType.Box), null, files);
        Assert.Equal(0, missing.PassCount);
        Assert.Equal("Provider unavailable", missing.CreateFolder.Error);
    }

    [Fact]
    public void ReportMatchesTheMacFormat()
    {
        var account = new VersionTestAccountResult("Dropbox (Robert)", "Dropbox")
        {
            CreateFolder = new VersionTestStep("create FileFluss version test", true, 120, null, null),
            Uploads = [new VersionTestStep("upload a|b.txt", true, 300, null, null)],
            ShareLink = [new VersionTestStep("share link not supported by this provider", true, 0, null, null)],
            Deletes = [new VersionTestStep("delete a|b.txt", false, 50, "Verification failed: file still present after delete", "listDirectory(/x) → 1 item(s)")],
            CleanupFolder = new VersionTestStep("cleanup FileFluss version test", true, 80, null, null),
            ShareLinkUrl = "https://example.com/s/1",
        };
        var start = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var report = new VersionTestReport(start, start.AddSeconds(42), @"C:\repo\Testfiles", [new VersionTestLocalFile("a|b.txt", @"C:\repo\Testfiles\a|b.txt", 3)], [account], "1.2.3", "456");

        var markdown = VersionTestEngine.RenderMarkdown(report);

        Assert.StartsWith(
            "# FileFluss Version Test Report\n\n" +
            "- **App version**: 1.2.3 (build 456)\n" +
            "- **Started**: 2026-10-05T12:00:00Z\n" +
            "- **Duration**: 42s\n" +
            "- **Test files**: 1 file(s) from `C:\\repo\\Testfiles`\n\n" +
            "## Overall: FAIL (1 failure) — 4/5 steps\n\n" +
            "## FAIL — Dropbox (Robert) (Dropbox)\n\n" +
            "4/5 steps\n\n" +
            "| Phase | Step | Result | Duration | Error |\n" +
            "|-------|------|--------|----------|-------|\n" +
            "| Create folder | create FileFluss version test | OK | 120 ms |  |\n" +
            "| Upload | upload a\\|b.txt | OK | 300 ms |  |\n" +
            "| Share link | share link not supported by this provider | OK | 0 ms |  |\n" +
            "| Delete | delete a\\|b.txt | FAIL | 50 ms | Verification failed: file still present after delete |\n" +
            "| Cleanup folder | cleanup FileFluss version test | OK | 80 ms |  |\n\n" +
            "Share link created: `https://example.com/s/1`\n\n",
            markdown);
        Assert.EndsWith("**delete a|b.txt**\n\n```\nlistDirectory(/x) → 1 item(s)\n```\n\n</details>\n\n", markdown);
        Assert.Matches(@"^version-test-report-\d{8}-\d{6}\.md$", VersionTestEngine.ReportFileName(start));
    }

    private VersionTestReport Report(VersionTestAccountResult account)
        => new(DateTimeOffset.Now, DateTimeOffset.Now, Testfiles, VersionTestEngine.EnumerateLocalFiles(Testfiles), [account], "0.0.0", "0");

    private sealed class RecordingProgress : IVersionTestProgress
    {
        public int Finished { get; private set; }

        public int? CancelAfterSteps { get; init; }

        public List<string> Steps { get; init; } = [];

        public bool IsCancelled => CancelAfterSteps is { } limit && Finished >= limit;

        public void BeginAccount(string name, int index, int count)
        {
        }

        public void BeginStep(string label) => Steps.Add(label);

        public void FinishStep() => Finished++;
    }
}

#endif
