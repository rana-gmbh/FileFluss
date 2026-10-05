// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

// Debug builds only, exactly like the macOS VersionTestRunner: a Release build of Core must
// not contain any of this (the repository CLAUDE.md, "Version test (debug builds only)").
#if DEBUG

using System.Diagnostics;
using System.Globalization;
using System.Text;
using FileFluss.Core.Models;
using FileFluss.Core.Net;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Diagnostics;

/// <summary>A file from the Testfiles folder; <see cref="RelativePath"/> uses '/' separators.</summary>
public sealed record VersionTestLocalFile(string RelativePath, string FullPath, long Size);

/// <summary>One row of the report.</summary>
public sealed record VersionTestStep(string Label, bool Ok, int DurationMs, string? Error, string? Diagnostics)
{
    /// <summary>
    /// The step couldn't be carried out for a reason outside the app (a server that never
    /// answered). Counted as passing, but reported as SKIP so it isn't mistaken for a clean result.
    /// </summary>
    public bool Skipped { get; init; }

    public static VersionTestStep Note(string label) => new(label, true, 0, null, null);
}

/// <summary>Everything one account's pass produced, in the macOS phase order.</summary>
public sealed record VersionTestAccountResult(string AccountDisplayName, string Provider)
{
    /// <summary>Windows addition: the root listing a read-only device gets instead of the write phases.</summary>
    public IReadOnlyList<VersionTestStep> Reads { get; init; } = [];

    public required VersionTestStep CreateFolder { get; init; }

    public IReadOnlyList<VersionTestStep> Uploads { get; init; } = [];

    public IReadOnlyList<VersionTestStep> Replaces { get; init; } = [];

    /// <summary>Creating the link, then fetching it without credentials. Empty without a sharing API.</summary>
    public IReadOnlyList<VersionTestStep> ShareLink { get; init; } = [];

    /// <summary>Recorded so a human can eyeball it; it dies with the test file moments later.</summary>
    public string? ShareLinkUrl { get; init; }

    /// <summary>The provider's own remark about the link (e.g. Box: downgraded by an admin).</summary>
    public string? ShareLinkNote { get; init; }

    public IReadOnlyList<VersionTestStep> Deletes { get; init; } = [];

    public required VersionTestStep CleanupFolder { get; init; }

    public IReadOnlyList<VersionTestStep> AllSteps
        => [.. Reads, CreateFolder, .. Uploads, .. Replaces, .. ShareLink, .. Deletes, CleanupFolder];

    public int PassCount => AllSteps.Count(s => s.Ok);

    public int TotalCount => AllSteps.Count;
}

public sealed record VersionTestReport(
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    string TestfilesPath,
    IReadOnlyList<VersionTestLocalFile> Files,
    IReadOnlyList<VersionTestAccountResult> Accounts,
    string AppVersion,
    string Build)
{
    public int PassCount => Accounts.Sum(a => a.PassCount);

    public int TotalCount => Accounts.Sum(a => a.TotalCount);
}

/// <summary>Progress sink for a run; called from background threads.</summary>
public interface IVersionTestProgress
{
    /// <summary>Cancelling stops new work but never skips the delete and cleanup phases.</summary>
    bool IsCancelled { get; }

    void BeginAccount(string name, int index, int count);

    void BeginStep(string label);

    void FinishStep();
}

/// <summary>
/// The macOS <c>VersionTestRunner</c> engine: for each connected account, create a test
/// folder → upload every Testfiles file → upload each again (replace) → share link (create +
/// anonymous fetch) → delete each file → remove the folder, verifying every step against the
/// provider's own listing. UI-free so it can run against an in-memory provider in tests.
/// </summary>
public static class VersionTestEngine
{
    public const string TestFolderName = "FileFluss version test";
    public const string ReportPrefix = "version-test-report-";
    public const string LogCategory = "version-test";

    /// <summary>
    /// Providers whose storage model doesn't map to arbitrary folders (WordPress' media
    /// library). The create/upload/delete flow would only produce misleading failures.
    /// </summary>
    private static readonly HashSet<CloudProviderType> SkippedProviders = [CloudProviderType.WordPress];

    /// <summary>Bucket- or library-rooted providers: "/" lists containers, not folders.</summary>
    private static readonly HashSet<CloudProviderType> BucketRooted =
        [CloudProviderType.S3, CloudProviderType.S3Compatible, CloudProviderType.SynologyC2, CloudProviderType.Seafile];

    /// <summary>Backoff for list-after-write checks (pCloud, Internxt, Synology C2 are eventually consistent).</summary>
    private static readonly int[] VerifyDelaysMs = [0, 250, 500, 1000];

    /// <summary>Steps per account for the progress bar: create + 3 per file + up to two share steps + cleanup.</summary>
    public static int EstimatedStepsPerAccount(int fileCount) => 3 * fileCount + 4;

    // MARK: - Discovery

    /// <summary>
    /// The Testfiles folder of the source checkout the app was built from — the Windows
    /// counterpart of the macOS <c>#filePath</c> walk: the first ancestor of
    /// <paramref name="startDirectory"/> that holds <c>.git</c> (a folder, or a file in a
    /// worktree). Null when not running from a checkout or it has no Testfiles folder.
    /// </summary>
    public static string? FindRepositoryTestfiles(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                var candidate = Path.Combine(dir.FullName, "Testfiles");
                return Directory.Exists(candidate) ? candidate : null;
            }
        }

        return null;
    }

    /// <summary>Every regular, non-hidden file under <paramref name="root"/>, minus earlier reports, sorted by relative path.</summary>
    public static IReadOnlyList<VersionTestLocalFile> EnumerateLocalFiles(string root)
    {
        var files = new List<VersionTestLocalFile>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        var rootLength = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Length + 1;
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = dir.GetFileSystemInfos();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.Name.StartsWith('.') || entry.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    continue;
                }

                if (entry is DirectoryInfo sub)
                {
                    pending.Push(sub);
                    continue;
                }

                var relative = entry.FullName[rootLength..].Replace('\\', '/');
                // Prior reports, so repeated runs don't cascade.
                if (relative.StartsWith(ReportPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                files.Add(new VersionTestLocalFile(relative, entry.FullName, ((FileInfo)entry).Length));
            }
        }

        return files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList();
    }

    // MARK: - Per account

    /// <summary>
    /// Runs the full pass for one account. <paramref name="provider"/> is null when the
    /// account has no live provider. <paramref name="checkUrl"/> replaces the anonymous
    /// share-link fetch in tests.
    /// </summary>
    public static async Task<VersionTestAccountResult> RunAccountAsync(
        CloudAccount account,
        CloudProvider? provider,
        IReadOnlyList<VersionTestLocalFile> localFiles,
        IVersionTestProgress? progress = null,
        Func<Uri, Task<PublicUrlCheck.Result>>? checkUrl = null)
    {
        checkUrl ??= url => PublicUrlCheck.IsReachableAsync(url);
        var displayName = account.DisplayName;
        var providerName = account.ProviderType.DisplayName();
        SupportLogger.Shared.Log($"--- Account: {displayName} ({providerName}) ---", LogCategory, LogLevel.Notice);

        if (SkippedProviders.Contains(account.ProviderType))
        {
            var note = VersionTestStep.Note($"skipped ({providerName} has no folder concept)");
            return new VersionTestAccountResult(displayName, providerName) { CreateFolder = note, CleanupFolder = note };
        }

        if (provider is null)
        {
            var miss = new VersionTestStep("resolve provider", false, 0, "Provider unavailable", null);
            return new VersionTestAccountResult(displayName, providerName) { CreateFolder = miss, CleanupFolder = miss };
        }

        // Read-only devices (a GoPro camera) accept no writes: prove the account answers and
        // stop there, rather than reporting a wall of failures that say nothing.
        if (account.ProviderType.IsReadOnly())
        {
            var root = account.EffectiveRootPath;
            var list = await MeasureAsync($"list {root}", progress, null, async () =>
            {
                _ = await provider.ListDirectoryAsync(root).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var note = VersionTestStep.Note($"skipped ({providerName} is read-only)");
            return new VersionTestAccountResult(displayName, providerName) { Reads = [list], CreateFolder = note, CleanupFolder = note };
        }

        var testRoot = await ResolveTestRootAsync(provider, account).ConfigureAwait(false);
        var testFolderPath = provider.CombinePath(testRoot, TestFolderName);

        // Phase 1 — create folder.
        var createStep = await MeasureAsync(
            $"create {TestFolderName}",
            progress,
            () => ListingDiagnosticAsync(provider, testRoot, TestFolderName),
            async () =>
            {
                await provider.CreateDirectoryAsync(testFolderPath).ConfigureAwait(false);
                await VerifyPresentAsync(provider, testFolderPath, expectedDirectory: true).ConfigureAwait(false);
            }).ConfigureAwait(false);

        if (!createStep.Ok)
        {
            // Still attempt cleanup so nothing is left behind.
            var cleanupAfterFailure = await MeasureAsync($"cleanup {TestFolderName}", null, null, async () =>
            {
                try
                {
                    await provider.DeleteItemAsync(testFolderPath).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Best effort, as on macOS (`try?`).
                }
            }).ConfigureAwait(false);
            return new VersionTestAccountResult(displayName, providerName) { CreateFolder = createStep, CleanupFolder = cleanupAfterFailure };
        }

        bool Cancelled() => progress?.IsCancelled == true;

        // Phase 2 — upload. Files that never arrived are not deleted later: that would report
        // "item not found" and bury the real cause under a cascade of failures.
        var uploads = new List<VersionTestStep>();
        var uploaded = new HashSet<string>(StringComparer.Ordinal);
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var local in localFiles)
        {
            if (Cancelled())
            {
                break;
            }

            var destPath = RemotePath(provider, testFolderPath, local.RelativePath);
            var parentPath = provider.GetParentPath(destPath);
            var fileName = provider.GetName(destPath);
            var step = await MeasureAsync(
                $"upload {local.RelativePath}",
                progress,
                () => ListingDiagnosticAsync(provider, parentPath, fileName),
                async () =>
                {
                    await EnsureFoldersAsync(provider, testFolderPath, local.RelativePath).ConfigureAwait(false);
                    await provider.UploadFileAsync(local.FullPath, destPath).ConfigureAwait(false);
                    await VerifyPresentAsync(provider, destPath, expectedDirectory: false).ConfigureAwait(false);
                }).ConfigureAwait(false);
            uploads.Add(step);
            attempted.Add(local.RelativePath);
            if (step.Ok)
            {
                uploaded.Add(local.RelativePath);
            }
        }

        // Phase 3 — replace (upload again).
        var replaces = new List<VersionTestStep>();
        foreach (var local in localFiles.Where(f => uploaded.Contains(f.RelativePath)))
        {
            if (Cancelled())
            {
                break;
            }

            var destPath = RemotePath(provider, testFolderPath, local.RelativePath);
            var parentPath = provider.GetParentPath(destPath);
            var fileName = provider.GetName(destPath);
            replaces.Add(await MeasureAsync(
                $"replace {local.RelativePath}",
                progress,
                () => ListingDiagnosticAsync(provider, parentPath, fileName),
                async () =>
                {
                    await provider.UploadFileAsync(local.FullPath, destPath).ConfigureAwait(false);
                    await VerifyPresentAsync(provider, destPath, expectedDirectory: false).ConfigureAwait(false);
                    var meta = await provider.GetFileMetadataAsync(destPath).ConfigureAwait(false);
                    if (meta.Size == 0 && local.Size > 0)
                    {
                        throw new VersionTestVerificationException($"size 0 after replace (expected {local.Size})");
                    }
                }).ConfigureAwait(false));
        }

        // Phase 3.5 — share link, on the first uploaded test file (never a real file of the
        // user's); the delete phase takes the link down with the file.
        var shareSteps = new List<VersionTestStep>();
        string? shareUrl = null;
        string? shareNote = null;
        var caps = provider.ShareLinkCapabilities;
        if (localFiles.Count > 0 && uploads.Count > 0 && uploads[0].Ok && !Cancelled())
        {
            var firstFile = localFiles[0];
            var sharePath = RemotePath(provider, testFolderPath, firstFile.RelativePath);
            if (caps.CanCreate)
            {
                CloudShareLink? created = null;
                shareSteps.Add(await MeasureAsync($"create share link ({firstFile.RelativePath})", progress, null, async () =>
                {
                    // No password and no expiry unless the provider insists: both are paid-plan
                    // features almost everywhere, and a plan rejection would look like a bug.
                    var options = new ShareLinkOptions { Expiry = caps.RequiresExpiry ? DateTimeOffset.Now.AddHours(1) : null };
                    created = await provider.CreateShareLinkAsync(sharePath, options).ConfigureAwait(false);
                }).ConfigureAwait(false));

                if (created is { } link)
                {
                    shareNote = link.Note;
                    // The real proof: fetch the link with no credentials at all. Mirror the
                    // app's fallback — a provider that refuses its own direct-download URL
                    // gets the share page copied instead, so that is what must be reachable.
                    var candidate = link.PreferredUrl;
                    if (link.DirectDownloadUrl is { } direct && direct != link.Url)
                    {
                        var directResult = await checkUrl(direct).ConfigureAwait(false);
                        // A check that never reached the server is not the provider refusing direct links.
                        if (!directResult.Ok && !directResult.CouldNotCheck)
                        {
                            candidate = link.Url;
                            var reason = directResult.Detail ?? "no detail";
                            shareNote = string.Join(" · ", new[] { shareNote, $"Direct download link rejected by the provider, fell back to the share page — {reason}" }.Where(s => s is not null));
                        }
                    }

                    shareUrl = candidate.AbsoluteUri;
                    shareSteps.Add(await ShareFetchStepAsync(candidate, progress, checkUrl).ConfigureAwait(false));
                }
            }
            else
            {
                shareSteps.Add(VersionTestStep.Note("share link not supported by this provider"));
            }
        }

        // Phase 4 — delete. Runs even after a cancel: the test folder must not be left behind.
        var deletes = new List<VersionTestStep>();
        foreach (var local in localFiles)
        {
            if (!uploaded.Contains(local.RelativePath))
            {
                deletes.Add(VersionTestStep.Note(!attempted.Contains(local.RelativePath)
                    ? $"delete {local.RelativePath} — skipped, cancelled before upload"
                    : $"delete {local.RelativePath} — skipped, upload had failed"));
                continue;
            }

            var destPath = RemotePath(provider, testFolderPath, local.RelativePath);
            var parentPath = provider.GetParentPath(destPath);
            var fileName = provider.GetName(destPath);
            deletes.Add(await MeasureAsync(
                $"delete {local.RelativePath}",
                progress,
                () => ListingDiagnosticAsync(provider, parentPath, fileName),
                async () =>
                {
                    await provider.DeleteItemAsync(destPath).ConfigureAwait(false);
                    await VerifyAbsentAsync(provider, parentPath, fileName, "file").ConfigureAwait(false);
                }).ConfigureAwait(false));
        }

        // Phase 5 — cleanup, always attempted. Verified in the folder the test ran in (the
        // macOS runner checks "/", which for a bucket-rooted account lists buckets and so
        // can never see the test folder).
        var cleanup = await MeasureAsync(
            $"cleanup {TestFolderName}",
            progress,
            () => ListingDiagnosticAsync(provider, testRoot, TestFolderName),
            async () =>
            {
                await provider.DeleteItemAsync(testFolderPath).ConfigureAwait(false);
                await VerifyAbsentAsync(provider, testRoot, TestFolderName, "folder").ConfigureAwait(false);
            }).ConfigureAwait(false);

        return new VersionTestAccountResult(displayName, providerName)
        {
            CreateFolder = createStep,
            Uploads = uploads,
            Replaces = replaces,
            ShareLink = shareSteps,
            ShareLinkUrl = shareUrl,
            ShareLinkNote = shareNote,
            Deletes = deletes,
            CleanupFolder = cleanup,
        };
    }

    /// <summary>
    /// The anonymous fetch has three outcomes, not two: a server that never answered (an
    /// untrusted certificate on a self-hosted box) says nothing about the link and is SKIP.
    /// </summary>
    private static async Task<VersionTestStep> ShareFetchStepAsync(Uri url, IVersionTestProgress? progress, Func<Uri, Task<PublicUrlCheck.Result>> checkUrl)
    {
        const string name = "fetch share link anonymously";
        progress?.BeginStep(name);
        try
        {
            var watch = Stopwatch.StartNew();
            var result = await checkUrl(url).ConfigureAwait(false);
            var ms = (int)watch.ElapsedMilliseconds;
            switch (result.Outcome)
            {
                case PublicUrlCheck.Outcome.Reachable:
                    SupportLogger.Shared.Log($"OK  {name} [{ms}ms]", LogCategory);
                    return new VersionTestStep(name, true, ms, null, null);
                case PublicUrlCheck.Outcome.Unverified:
                {
                    var message = $"not checked — {result.Detail ?? "the server could not be reached"}";
                    SupportLogger.Shared.Log($"SKIP {name} — {message} [{ms}ms]", LogCategory);
                    return new VersionTestStep(name, true, ms, message, null) { Skipped = true };
                }

                default:
                {
                    var message = $"share link is not reachable anonymously — {result.Detail ?? "no detail"}";
                    SupportLogger.Shared.Log($"FAIL {name} — {message} [{ms}ms]", LogCategory, LogLevel.Error);
                    return new VersionTestStep(name, false, ms, message, null);
                }
            }
        }
        finally
        {
            progress?.FinishStep();
        }
    }

    // MARK: - Helpers

    /// <summary>
    /// Where the test runs: the account root for most providers; for bucket-rooted ones the
    /// account's own bucket scope, else the first bucket (falling back to "/" so the create
    /// step surfaces the underlying error).
    /// </summary>
    private static async Task<string> ResolveTestRootAsync(CloudProvider provider, CloudAccount account)
    {
        if (!BucketRooted.Contains(account.ProviderType))
        {
            return "/";
        }

        // A key scoped to one bucket may not be allowed to list buckets (macOS issue #53).
        if (account.EffectiveRootPath != "/")
        {
            return account.EffectiveRootPath;
        }

        try
        {
            var first = (await provider.ListDirectoryAsync("/").ConfigureAwait(false))
                .Where(i => i.IsDirectory)
                .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();
            return first is null ? "/" : provider.CombinePath("/", first.Name);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return "/";
        }
    }

    private static string RemotePath(FileProvider provider, string folder, string relativePath)
        => relativePath.Split('/').Aggregate(folder, provider.CombinePath);

    /// <summary>Creates the intermediate folders of a nested test file (CreateDirectory is idempotent).</summary>
    private static async Task EnsureFoldersAsync(FileProvider provider, string folder, string relativePath)
    {
        var segments = relativePath.Split('/');
        var current = folder;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = provider.CombinePath(current, segments[i]);
            await provider.CreateDirectoryAsync(current).ConfigureAwait(false);
        }
    }

    private static async Task VerifyPresentAsync(FileProvider provider, string path, bool expectedDirectory)
    {
        var parent = provider.GetParentPath(path);
        var name = provider.GetName(path);
        Exception lastError = new VersionTestVerificationException($"not present in {parent} listing");
        foreach (var delay in VerifyDelaysMs)
        {
            if (delay > 0)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }

            try
            {
                var contents = await provider.ListDirectoryAsync(parent).ConfigureAwait(false);
                if (contents.Any(i => provider.NameComparer.Equals(i.Name, name) && i.IsDirectory == expectedDirectory))
                {
                    return;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                lastError = e;
            }
        }

        throw lastError;
    }

    private static async Task VerifyAbsentAsync(FileProvider provider, string parent, string name, string kindLabel)
    {
        foreach (var delay in VerifyDelaysMs)
        {
            if (delay > 0)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }

            IReadOnlyList<CloudFileItem> contents;
            try
            {
                contents = await provider.ListDirectoryAsync(parent).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // As on macOS (`try? … ?? []`): an unlistable parent counts as "gone".
                contents = [];
            }

            if (!contents.Any(i => provider.NameComparer.Equals(i.Name, name)))
            {
                return;
            }
        }

        throw new VersionTestVerificationException($"{kindLabel} still present after delete");
    }

    private static async Task<VersionTestStep> MeasureAsync(
        string name,
        IVersionTestProgress? progress,
        Func<Task<string>>? diagnostics,
        Func<Task> work)
    {
        progress?.BeginStep(name);
        var watch = Stopwatch.StartNew();
        try
        {
            await work().ConfigureAwait(false);
            var ms = (int)watch.ElapsedMilliseconds;
            SupportLogger.Shared.Log($"OK  {name} [{ms}ms]", LogCategory);
            return new VersionTestStep(name, true, ms, null, null);
        }
        catch (Exception e)
        {
            var ms = (int)watch.ElapsedMilliseconds;
            SupportLogger.Shared.Log($"FAIL {name} — {e.Message} [{ms}ms]", LogCategory, LogLevel.Error);
            var diag = diagnostics is null ? null : await diagnostics().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(diag))
            {
                SupportLogger.Shared.Log($"     diag: {diag}", LogCategory, LogLevel.Error);
            }

            return new VersionTestStep(name, false, ms, e.Message, diag);
        }
        finally
        {
            progress?.FinishStep();
        }
    }

    /// <summary>A compact, deterministic dump of a folder, captured when a step fails.</summary>
    private static async Task<string> ListingDiagnosticAsync(FileProvider provider, string parent, string? expectedName)
    {
        try
        {
            var contents = await provider.ListDirectoryAsync(parent).ConfigureAwait(false);
            var header = expectedName is null
                ? $"listDirectory({parent}) → {contents.Count} item(s)"
                : $"listDirectory({parent}) → {contents.Count} item(s); matches for '{expectedName}': {contents.Count(i => i.Name == expectedName)}";
            var lines = contents
                .OrderBy(i => i.Name, StringComparer.Ordinal)
                .Take(40)
                .Select(i => $"  [{(i.IsDirectory ? "d" : "f")}] {i.Name} (size={i.Size}, id={i.Id})");
            var more = contents.Count > 40 ? $"\n  … {contents.Count - 40} more" : string.Empty;
            return string.Join("\n", lines.Prepend(header)) + more;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"listDirectory({parent}) threw: {e.Message}";
        }
    }

    // MARK: - Report

    /// <summary>"version-test-report-20261005-143012.md", local time as on macOS.</summary>
    public static string ReportFileName(DateTimeOffset start)
        => $"{ReportPrefix}{start.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.md";

    /// <summary>The macOS Markdown report, line for line.</summary>
    public static string RenderMarkdown(VersionTestReport report)
    {
        var started = report.StartDate.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var duration = (int)(report.EndDate - report.StartDate).TotalSeconds;

        var output = new StringBuilder();
        output.Append("# FileFluss Version Test Report\n\n");
        output.Append($"- **App version**: {report.AppVersion} (build {report.Build})\n");
        output.Append($"- **Started**: {started}\n");
        output.Append($"- **Duration**: {duration}s\n");
        output.Append($"- **Test files**: {report.Files.Count} file(s) from `{report.TestfilesPath}`\n\n");

        var totalPass = report.PassCount;
        var totalCount = report.TotalCount;
        var failures = totalCount - totalPass;
        var overall = failures == 0 ? "PASS" : $"FAIL ({failures} failure{(failures == 1 ? string.Empty : "s")})";
        output.Append($"## Overall: {overall} — {totalPass}/{totalCount} steps\n\n");

        foreach (var account in report.Accounts)
        {
            var mark = account.PassCount == account.TotalCount ? "PASS" : "FAIL";
            output.Append($"## {mark} — {account.AccountDisplayName} ({account.Provider})\n\n");
            output.Append($"{account.PassCount}/{account.TotalCount} steps\n\n");
            output.Append("| Phase | Step | Result | Duration | Error |\n");
            output.Append("|-------|------|--------|----------|-------|\n");

            void Row(string phase, VersionTestStep step)
            {
                var result = step.Skipped ? "SKIP" : step.Ok ? "OK" : "FAIL";
                var error = (step.Error ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ').Replace("\r", string.Empty, StringComparison.Ordinal);
                var label = step.Label.Replace("|", "\\|", StringComparison.Ordinal);
                output.Append($"| {phase} | {label} | {result} | {step.DurationMs} ms | {error} |\n");
            }

            foreach (var step in account.Reads)
            {
                Row("List", step);
            }

            Row("Create folder", account.CreateFolder);
            foreach (var step in account.Uploads)
            {
                Row("Upload", step);
            }

            foreach (var step in account.Replaces)
            {
                Row("Replace", step);
            }

            foreach (var step in account.ShareLink)
            {
                Row("Share link", step);
            }

            foreach (var step in account.Deletes)
            {
                Row("Delete", step);
            }

            Row("Cleanup folder", account.CleanupFolder);
            output.Append('\n');

            if (account.ShareLinkNote is { } note)
            {
                output.Append($"Share link note from the provider: {note}\n\n");
            }

            if (account.ShareLinkUrl is { } link)
            {
                output.Append($"Share link created: `{link}`\n\n");
                output.Append("_The shared file is deleted in the delete phase above, so this link is already dead._\n\n");
            }

            var failing = account.AllSteps.Where(s => !s.Ok && !string.IsNullOrEmpty(s.Diagnostics)).ToList();
            if (failing.Count > 0)
            {
                output.Append("<details><summary>Diagnostics for failing steps</summary>\n\n");
                foreach (var step in failing)
                {
                    output.Append($"**{step.Label}**\n\n```\n{step.Diagnostics}\n```\n\n");
                }

                output.Append("</details>\n\n");
            }
        }

        return output.ToString();
    }
}

/// <summary>A step did what the provider said it did, but the listing disagrees.</summary>
public sealed class VersionTestVerificationException(string message) : Exception("Verification failed: " + message);

#endif
