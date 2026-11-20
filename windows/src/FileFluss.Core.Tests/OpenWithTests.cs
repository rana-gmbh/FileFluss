// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Models;

namespace FileFluss.Core.Tests;

/// <summary>The rules behind right-click → Open With (macOS <c>OpenWithTests</c>): which apps are offered, in what order.</summary>
public class OpenWithCandidatesTests
{
    private sealed record App(string Id, string Name);

    private static readonly App Notepad = new(@"C:\Windows\notepad.exe", "Notepad");
    private static readonly App Photos = new("Photos", "Photos");
    private static readonly App Paint = new(@"C:\Program Files\WindowsApps\Microsoft.Paint\mspaint.exe", "Paint");
    private static readonly App Code = new(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", "Visual Studio Code");
    private static readonly App OldCode = new(@"D:\Portable\VSCode\Code.exe", "Visual Studio Code");

    private static IReadOnlyList<OpenWithCandidate<App>> Rank(IReadOnlyList<IReadOnlyList<App>> perItem, string? defaultId)
        => OpenWithCandidates.Rank(perItem, defaultId, a => a.Id, a => a.Name);

    [Fact]
    public void TheDefaultComesFirstAndTheRestFollowByName()
    {
        var apps = Rank([[Paint, Photos, Notepad]], Photos.Id);

        Assert.Equal(["Photos", "Notepad", "Paint"], apps.Select(a => a.Name));
        Assert.Equal([true, false, false], apps.Select(a => a.IsDefault));
    }

    [Fact]
    public void AMultiSelectionOffersOnlyTheAppsThatOpenEveryItem()
    {
        // Notepad is the first file's default but can't open the second, so it isn't offered
        // at all — and nothing is marked default.
        var apps = Rank([[Paint, Photos, Notepad], [Photos, Paint]], Notepad.Id);

        Assert.Equal(["Paint", "Photos"], apps.Select(a => a.Name));
        Assert.DoesNotContain(apps, a => a.IsDefault);
    }

    [Fact]
    public void IdentityIsComparedIgnoringCase()
    {
        // The shell spells the same executable path differently for different types.
        var upper = Notepad with { Id = Notepad.Id.ToUpperInvariant() };
        var apps = Rank([[Notepad], [upper]], upper.Id);

        var only = Assert.Single(apps);
        Assert.True(only.IsDefault);
        Assert.Same(Notepad, only.App);
    }

    [Fact]
    public void ASecondCopyOfTheSameAppIsListedOnce()
    {
        var apps = Rank([[OldCode, Code, Paint]], Code.Id);

        Assert.Equal(["Visual Studio Code", "Paint"], apps.Select(a => a.Name));
        Assert.Same(Code, apps[0].App);
    }

    [Fact]
    public void NothingToOfferGivesAnEmptyList()
    {
        Assert.Empty(Rank([], null));
        Assert.Empty(Rank([[Notepad], []], Notepad.Id));
        Assert.Empty(Rank([[Notepad], [Photos]], null));
    }

    [Fact]
    public void AnUnknownDefaultMarksNothing()
    {
        var apps = Rank([[Notepad, Paint]], "Something else");

        Assert.Equal(["Notepad", "Paint"], apps.Select(a => a.Name));
        Assert.DoesNotContain(apps, a => a.IsDefault);
    }

    [Fact]
    public void ASelectionIsAskedAboutOncePerType()
    {
        Assert.Equal([".txt", ".png", string.Empty], OpenWithCandidates.Extensions(["a.TXT", "b.txt", "c.png", "README", "odd."]));
    }
}

/// <summary>
/// What happens to the copy of a cloud file already in the open cache (macOS
/// <c>CachedCopyDecisionTests</c>). Changes made in another app to a file opened from a cloud
/// account must never be deleted or replaced by FileFluss.
/// </summary>
public class CachedCopyTests
{
    private static readonly DateTime RemoteModified = DateTime.UnixEpoch.AddSeconds(1_700_000_000);
    private const long RemoteSize = 1_024;

    private static CachedCopyDecision Decide(long localSize, DateTime? localModified)
        => CachedCopy.Decide(localSize, localModified, RemoteSize, RemoteModified);

    [Fact]
    public void AnUntouchedCopyIsReused()
        => Assert.Equal(CachedCopyDecision.Reuse, Decide(RemoteSize, RemoteModified));

    [Fact]
    public void ACopyEditedSinceTheDownloadIsKeptNotDeleted()
    {
        var edited = RemoteModified.AddSeconds(120);

        Assert.Equal(CachedCopyDecision.PreserveEditsThenRefresh, Decide(2_048, edited));

        // Even when the edit happened to leave the size alone.
        Assert.Equal(CachedCopyDecision.PreserveEditsThenRefresh, Decide(RemoteSize, edited));
    }

    [Fact]
    public void AStaleCopyNobodyTouchedIsReplaced()
    {
        // Older than the server's version: the remote changed while ours sat in the cache.
        var older = RemoteModified.AddHours(-1);

        Assert.Equal(CachedCopyDecision.Refresh, Decide(RemoteSize, older));
        Assert.Equal(CachedCopyDecision.Refresh, Decide(99, older));
    }

    [Fact]
    public void SmallTimestampDifferencesAreNotAnEdit()
    {
        Assert.Equal(CachedCopyDecision.Reuse, Decide(RemoteSize, RemoteModified.AddSeconds(0.4)));
        Assert.Equal(CachedCopyDecision.Reuse, Decide(RemoteSize, RemoteModified.AddSeconds(-0.4)));

        // FAT32/exFAT keep two-second steps.
        Assert.Equal(CachedCopyDecision.Reuse, Decide(RemoteSize, RemoteModified.AddSeconds(2)));

        // Comfortably past the tolerance is an edit.
        Assert.Equal(CachedCopyDecision.PreserveEditsThenRefresh, Decide(RemoteSize, RemoteModified.AddSeconds(5)));
    }

    [Fact]
    public void AnUnknownRemoteSizeDoesntInvalidateACurrentCopy()
        => Assert.Equal(CachedCopyDecision.Reuse, CachedCopy.Decide(4_096, RemoteModified, 0, RemoteModified));

    [Fact]
    public void ACopyWithNoReadableDateIsTreatedAsOursAndStale()
        => Assert.Equal(CachedCopyDecision.Refresh, Decide(RemoteSize, null));

    [Fact]
    public void WithoutAServerDateTheDownloadTimeIsTheStamp()
    {
        var created = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal(created, CachedCopy.StampOf(null, created));
        Assert.Equal(RemoteModified, CachedCopy.StampOf(RemoteModified, created));
        Assert.Equal(CachedCopyDecision.Reuse, CachedCopy.Decide(RemoteSize, created, RemoteSize, CachedCopy.StampOf(null, created)));
        Assert.Equal(CachedCopyDecision.PreserveEditsThenRefresh, CachedCopy.Decide(RemoteSize, created.AddMinutes(5), RemoteSize, CachedCopy.StampOf(null, created)));
    }

    [Fact]
    public void KeptCopiesAreNamedWithTheTimeTheyWereWritten()
    {
        var edited = new DateTime(2026, 11, 9, 14, 30, 5);

        Assert.Equal("Report (edited 2026-11-09 14.30.05).docx", CachedCopy.PreservedName("Report.docx", edited));
        Assert.Equal("Makefile (edited 2026-11-09 14.30.05)", CachedCopy.PreservedName("Makefile", edited));
    }

    [Fact]
    public void PreservingMovesTheCopyAsideAndKeepsItsDates()
    {
        var folder = Directory.CreateTempSubdirectory("filefluss-cachedcopy-").FullName;
        try
        {
            var path = Path.Combine(folder, "Report.docx");
            File.WriteAllText(path, "my edits");
            var created = DateTime.UtcNow.AddDays(-1);
            var modified = DateTime.UtcNow.AddHours(-1);
            File.SetCreationTimeUtc(path, created);
            File.SetLastWriteTimeUtc(path, modified);

            var kept = CachedCopy.Preserve(path);

            Assert.False(File.Exists(path));
            Assert.Equal("my edits", File.ReadAllText(kept));
            Assert.StartsWith("Report (edited ", Path.GetFileName(kept), StringComparison.Ordinal);
            Assert.True(CachedCopy.HoldsEdits(File.GetCreationTimeUtc(kept), File.GetLastWriteTimeUtc(kept)));

            // A second edit kept within the same second doesn't overwrite the first.
            File.WriteAllText(path, "more edits");
            File.SetLastWriteTimeUtc(path, modified);
            var second = CachedCopy.Preserve(path);
            Assert.NotEqual(kept, second);
            Assert.Equal("my edits", File.ReadAllText(kept));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AStampedDownloadDoesNotLookEditedEvenOverAnOldCopy()
    {
        var folder = Directory.CreateTempSubdirectory("filefluss-cachedcopy-").FullName;
        try
        {
            var path = Path.Combine(folder, "a.txt");
            File.WriteAllText(path, "x");

            // An old copy's creation time survives a download renamed over it.
            File.SetCreationTimeUtc(path, DateTime.UtcNow.AddYears(-1));
            CachedCopy.Stamp(path, DateTime.UtcNow.AddDays(-2));
            Assert.False(CachedCopy.HoldsEdits(File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path)));

            CachedCopy.Stamp(path, null);
            Assert.False(CachedCopy.HoldsEdits(File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\Temp\FileFluss-Cache\open\acc\0123abcd\Report.docx", 60, true)]
    [InlineData(@"C:\Temp\FileFluss-Cache\open\acc\0123abcd\Report.docx", 1, false)]
    [InlineData(@"C:\Temp\FileFluss-Cache\open\acc\0123abcd\Report.docx", -3600, false)]
    [InlineData(@"C:\Temp\FileFluss-Cache\transfer-0123\Report.docx", 60, false)]
    [InlineData(@"C:\Temp\FileFluss-Cache\dl-0123", 60, false)]
    public void MaintenanceSparesOpenedCopiesTheUserEdited(string path, int secondsAfterCaching, bool spared)
    {
        var created = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

        Assert.Equal(spared, CacheManager.HoldsUserEdits(path, created, created.AddSeconds(secondsAfterCaching)));
    }
}
