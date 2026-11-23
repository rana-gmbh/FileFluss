// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Sync;

namespace FileFluss.Core.Tests.Sync;

/// <summary>Port of the macOS SyncPlannerTests (net destination delta) plus the plan rules themselves.</summary>
public class SyncPlannerTests
{
    private static DateTimeOffset T(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    private static SyncEntry File(string path, long size, DateTimeOffset? modified = null)
        => new(path, false, size, modified ?? T(1_000));

    private static SyncEntry Folder(string path) => new(path, true, 0, T(1_000));

    private static SyncPlan Plan(SyncEntry[] source, SyncEntry[] destination, SyncMode mode, bool sourceIsCloud = false, bool destinationIsCloud = true, bool ignoreCase = false)
        => SyncPlanner.Plan(source, destination, mode, PlanDirection.LeftToRight, sourceIsCloud, destinationIsCloud, ignoreCase);

    // MARK: - macOS: net destination delta

    [Fact]
    public void PureAddsSumTheirSizes()
    {
        var plan = Plan([File("a.txt", 100), File("b.txt", 50)], [], SyncMode.Newer);
        Assert.Equal(150, plan.NetDestinationDelta);
    }

    [Fact]
    public void ReplaceCountsOnlyGrowth()
    {
        var plan = Plan([File("a.txt", 300, T(9_000))], [File("a.txt", 100, T(1_000))], SyncMode.Newer);
        Assert.Equal(200, plan.NetDestinationDelta);
    }

    [Fact]
    public void ReplaceWithASmallerFileFreesSpace()
    {
        var plan = Plan([File("a.txt", 40, T(9_000))], [File("a.txt", 100, T(1_000))], SyncMode.Mirror);
        Assert.Equal(-60, plan.NetDestinationDelta);
    }

    [Fact]
    public void AdditiveModeAddsTheRenamedCopyInFull()
    {
        var plan = Plan([File("a.txt", 80)], [File("a.txt", 100)], SyncMode.Additive);
        Assert.Equal(80, plan.NetDestinationDelta);
    }

    [Fact]
    public void MirrorDeleteFreesTheRemovedFilesBytes()
    {
        var plan = Plan([File("keep.txt", 10)], [File("keep.txt", 10), File("stale.txt", 500)], SyncMode.Mirror);
        Assert.Equal(-500, plan.NetDestinationDelta);
    }

    // MARK: - Plan rules

    [Fact]
    public void MirrorReplacesADestinationFileWithTheSourcesFolder()
    {
        // A file "x" at the destination, a folder "x" at the source: skipped as "exists", the
        // folder's contents were planned beneath a file and failed on every run.
        var plan = Plan([Folder("x"), File("x/a.txt", 10)], [File("x", 70)], SyncMode.Mirror);

        Assert.Collection(
            plan.Operations,
            op => Assert.Equal((SyncOperationKind.Delete, "x", false, true), (op.Kind, op.RelativePath, op.IsDirectory, op.ClearsTheWay)),
            op => Assert.Equal((SyncOperationKind.Add, "x", true), (op.Kind, op.DestinationRelativePath, op.IsDirectory)),
            op => Assert.Equal((SyncOperationKind.Add, "x/a.txt", false), (op.Kind, op.DestinationRelativePath, op.IsDirectory)));
        Assert.Equal((1, 1, 1, 0), (plan.FilesToAdd, plan.FilesToDelete, plan.FoldersToAdd, plan.FoldersToDelete));
        Assert.Equal(10 - 70, plan.NetDestinationDelta);

        // Once it ran, the next mirror finds nothing to do.
        Assert.True(Plan([Folder("x"), File("x/a.txt", 10)], [Folder("x"), File("x/a.txt", 10)], SyncMode.Mirror).IsEmpty);
    }

    [Theory]
    [InlineData(SyncMode.Newer)]
    [InlineData(SyncMode.Additive)]
    public void ModesThatNeverDeleteLeaveAFolderOverAFileAlone(SyncMode mode)
    {
        var plan = Plan([Folder("x"), File("x/a.txt", 10), Folder("x/sub"), File("y.txt", 5)], [File("x", 70)], mode);

        Assert.Equal(["y.txt"], plan.Operations.Select(o => o.RelativePath).ToArray());
        Assert.Equal(1, plan.FolderTypeClashes);
        Assert.Equal(0, plan.TypeClashes);
    }

    [Fact]
    public void MirrorCountsAndOrdersOperations()
    {
        var plan = Plan(
            [File("sub/x.txt", 10), Folder("sub"), File("a.txt", 5)],
            [File("old.txt", 3), Folder("gone"), File("gone/y.txt", 4)],
            SyncMode.Mirror);

        Assert.Equal(
            ["a.txt", "sub", "sub/x.txt", "gone/y.txt", "old.txt", "gone"],
            plan.Operations.Select(o => o.RelativePath).ToArray());
        Assert.Equal(
            [SyncOperationKind.Add, SyncOperationKind.Add, SyncOperationKind.Add, SyncOperationKind.Delete, SyncOperationKind.Delete, SyncOperationKind.Delete],
            plan.Operations.Select(o => o.Kind).ToArray());
        Assert.Equal(2, plan.FilesToAdd);
        Assert.Equal(1, plan.FoldersToAdd);
        Assert.Equal(2, plan.FilesToDelete);
        Assert.Equal(1, plan.FoldersToDelete);
        Assert.Equal(0, plan.FilesToReplace);
        Assert.Equal(15, plan.TotalBytes);
        Assert.Equal(15, plan.UploadBytes);
        Assert.Equal(0, plan.DownloadBytes);
    }

    [Fact]
    public void MirrorSkipsFilesWithinTheDateTolerance()
    {
        Assert.True(Plan([File("a", 10, T(1_001))], [File("a", 10, T(1_000))], SyncMode.Mirror).IsEmpty);

        var plan = Plan([File("a", 10, T(1_003))], [File("a", 10, T(1_000))], SyncMode.Mirror);
        Assert.Equal(SyncOperationKind.Replace, Assert.Single(plan.Operations).Kind);
        Assert.Equal(1, plan.FilesToReplace);
    }

    [Fact]
    public void MirrorReplacesASameDatedFileOfAnotherSize()
    {
        var plan = Plan([File("a", 11)], [File("a", 10)], SyncMode.Mirror);
        Assert.Equal(SyncOperationKind.Replace, Assert.Single(plan.Operations).Kind);
    }

    [Fact]
    public void NewerReplacesOnlyWhatIsNewerAndNeverDeletes()
    {
        var plan = Plan(
            [File("older", 1, T(500)), File("slightly", 1, T(1_001)), File("newer", 1, T(5_000))],
            [File("older", 9, T(1_000)), File("slightly", 9, T(1_000)), File("newer", 9, T(1_000)), File("extra", 9)],
            SyncMode.Newer);

        var op = Assert.Single(plan.Operations);
        Assert.Equal(SyncOperationKind.Replace, op.Kind);
        Assert.Equal("newer", op.RelativePath);
        Assert.Equal(T(5_000), op.SourceModificationDate);
    }

    [Fact]
    public void AdditiveRenamesClashesToFreeNamesOnEitherSide()
    {
        var plan = Plan(
            [File("a.txt", 1), File("a (2).txt", 2), Folder("sub"), File("sub/r.tar.gz", 3), File("sub/.hidden", 4), File("noext", 5)],
            [File("a.txt", 1), Folder("sub"), File("sub/r.tar.gz", 3), File("sub/.hidden", 4), File("noext", 5)],
            SyncMode.Additive,
            sourceIsCloud: true); // cloud ↔ cloud: a sync touching the local disk leaves dotfiles alone

        var renamed = plan.Operations.Where(o => o.Kind == SyncOperationKind.AddRenamed)
            .ToDictionary(o => o.RelativePath, o => o.DestinationRelativePath);

        // "a (2).txt" is taken by a source file that is itself being added.
        Assert.Equal("a (3).txt", renamed["a.txt"]);
        Assert.Equal("sub/r.tar (2).gz", renamed["sub/r.tar.gz"]);
        Assert.Equal("sub/.hidden (2)", renamed["sub/.hidden"]);
        Assert.Equal("noext (2)", renamed["noext"]);
        Assert.Contains(plan.Operations, o => o is { Kind: SyncOperationKind.Add, RelativePath: "a (2).txt" });
        Assert.Equal(5, plan.FilesToAdd);
        Assert.DoesNotContain(plan.Operations, o => o.Kind == SyncOperationKind.Delete);
        Assert.Equal("a (3).txt", plan.Operations.First(o => o.RelativePath == "a.txt").DisplayName);
    }

    [Fact]
    public void ByteFiguresFollowWhichSidesAreCloud()
    {
        SyncEntry[] source = [File("a", 100)];
        var download = Plan(source, [], SyncMode.Newer, sourceIsCloud: true, destinationIsCloud: false);
        Assert.Equal((100, 0, 100), (download.DownloadBytes, download.UploadBytes, download.TotalBytes));

        var both = Plan(source, [], SyncMode.Newer, sourceIsCloud: true, destinationIsCloud: true);
        Assert.Equal((100, 100), (both.DownloadBytes, both.UploadBytes));

        var local = Plan(source, [], SyncMode.Newer, sourceIsCloud: false, destinationIsCloud: false);
        Assert.Equal((0, 0, 100), (local.DownloadBytes, local.UploadBytes, local.TotalBytes));
    }

    [Fact]
    public void IgnoringCaseKeepsAMirrorFromDeletingWhatItJustCopied()
    {
        SyncEntry[] source = [File("Photo.JPG", 10)];
        SyncEntry[] destination = [File("photo.jpg", 10)];

        Assert.True(Plan(source, destination, SyncMode.Mirror, ignoreCase: true).IsEmpty);

        var caseSensitive = Plan(source, destination, SyncMode.Mirror);
        Assert.Equal([SyncOperationKind.Add, SyncOperationKind.Delete], caseSensitive.Operations.Select(o => o.Kind).ToArray());
    }

    [Fact]
    public void FoldersOnBothSidesAreNotOperations()
    {
        var plan = Plan([Folder("d"), File("d/x", 1)], [Folder("d"), File("d/x", 1)], SyncMode.Mirror);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void ModesDescribeThemselves()
    {
        Assert.Equal([SyncMode.Mirror, SyncMode.Newer, SyncMode.Additive], SyncModeExtensions.All);
        Assert.True(SyncMode.Mirror.IsDestructive());
        Assert.False(SyncMode.Newer.IsDestructive());
        Assert.False(SyncMode.Additive.IsDestructive());
        Assert.Equal(Transfers.TransferOperation.Mirroring, SyncMode.Mirror.ToTransferOperation());
        Assert.Equal(Transfers.TransferOperation.SyncingNewer, SyncMode.Newer.ToTransferOperation());
        Assert.Equal(Transfers.TransferOperation.Adding, SyncMode.Additive.ToTransferOperation());
    }

    // MARK: - Names the local disk already holds

    private static readonly string LongName = new string('x', 230) + ".txt";

    [Fact]
    public void LocalToLocalKeepsNamesTheDiskAlreadyHolds()
    {
        // A leading space and a 234-character name are legal on NTFS. "Fixing" them planned a
        // copy under another name and the deletion of the correctly named file.
        SyncEntry[] side = [File(" notes.txt", 5), File(LongName, 7), Folder(" folder"), File(" folder/" + LongName, 9)];

        var plan = Plan(side, side, SyncMode.Mirror, sourceIsCloud: false, destinationIsCloud: false, ignoreCase: true);

        Assert.True(plan.IsEmpty, string.Join(", ", plan.Operations.Select(o => $"{o.Kind} {o.RelativePath} → {o.DestinationRelativePath}")));
    }

    [Fact]
    public void LocalToLocalAddsUnderTheSourcesOwnName()
    {
        var plan = Plan([File(" notes.txt", 5), File(LongName, 7)], [], SyncMode.Mirror, sourceIsCloud: false, destinationIsCloud: false, ignoreCase: true);

        Assert.Equal([" notes.txt", LongName], plan.Operations.Select(o => o.DestinationRelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CloudToLocalStillMakesServerNamesLegal()
    {
        var plan = Plan([File(" notes.txt", 5), File("a:b.txt", 1)], [], SyncMode.Mirror, sourceIsCloud: true, destinationIsCloud: false, ignoreCase: true);

        Assert.Equal(["a_b.txt", "notes.txt"], plan.Operations.Select(o => o.DestinationRelativePath).Order(StringComparer.Ordinal));
    }

    // MARK: - Source names that collide on the destination

    private static string Ops(SyncPlan plan)
        => string.Join(", ", plan.Operations.Select(o => $"{o.Kind} {o.RelativePath} → {o.DestinationRelativePath}"));

    [Fact]
    public void CaseVariantsOntoTheLocalDiskBothArriveAndARerunPlansNothing()
    {
        SyncEntry[] source = [File("README.md", 5), File("Readme.md", 6)];

        var plan = Plan(source, [], SyncMode.Mirror, sourceIsCloud: true, destinationIsCloud: false, ignoreCase: true);

        Assert.Equal(2, plan.FilesToAdd);
        var kept = Assert.Single(plan.Operations, o => o.RelativePath == "README.md");
        Assert.Equal((SyncOperationKind.Add, "README.md", false), (kept.Kind, kept.DestinationRelativePath, kept.IsCollisionRename));
        var renamed = Assert.Single(plan.Operations, o => o.RelativePath == "Readme.md");
        Assert.Equal((SyncOperationKind.AddRenamed, "Readme (2).md", true), (renamed.Kind, renamed.DestinationRelativePath, renamed.IsCollisionRename));

        var rerun = Plan(source, [File("README.md", 5), File("Readme (2).md", 6)], SyncMode.Mirror, sourceIsCloud: true, destinationIsCloud: false, ignoreCase: true);
        Assert.True(rerun.IsEmpty, Ops(rerun));
    }

    [Fact]
    public void AChangedCollidingFileReplacesItsOwnCopyNotTheOther()
    {
        SyncEntry[] source = [File("README.md", 5), File("Readme.md", 60, T(9_000))];

        var plan = Plan(source, [File("README.md", 5), File("Readme (2).md", 6)], SyncMode.Newer, sourceIsCloud: true, destinationIsCloud: false, ignoreCase: true);

        var replace = Assert.Single(plan.Operations);
        Assert.Equal((SyncOperationKind.Replace, "Readme.md", "Readme (2).md", true), (replace.Kind, replace.RelativePath, replace.DestinationRelativePath, replace.IsCollisionRename));
    }

    [Fact]
    public void NfcAndNfdSpellingsOfOneNameBothArriveAndMirrorDeletesNeither()
    {
        var nfd = "Café.txt";
        var nfc = "Café.txt";
        SyncEntry[] source = [File(nfc, 5), File(nfd, 6)];

        var plan = Plan(source, [], SyncMode.Mirror, sourceIsCloud: true, destinationIsCloud: false, ignoreCase: true);

        Assert.Equal(2, plan.FilesToAdd);
        Assert.Single(plan.Operations, o => o.IsCollisionRename && o.DestinationRelativePath == "Café (2).txt");

        // What a first run leaves: the name, and the "(2)". The next mirror keeps both.
        var rerun = Plan(source, [File(nfd, 6), File("Café (2).txt", 5)], SyncMode.Mirror, sourceIsCloud: true, destinationIsCloud: false, ignoreCase: true);
        Assert.True(rerun.IsEmpty, Ops(rerun));
    }

    [Fact]
    public void ARenamedCollidingFolderTakesItsContentsAlong()
    {
        SyncEntry[] source = [Folder("Docs"), File("Docs/a.txt", 1), Folder("docs"), File("docs/a.txt", 2)];

        var plan = Plan(source, [], SyncMode.Mirror, sourceIsCloud: true, destinationIsCloud: false, ignoreCase: true);

        Assert.Equal(
            ["Docs", "Docs/a.txt", "docs (2)", "docs (2)/a.txt"],
            plan.Operations.Select(o => o.DestinationRelativePath).Order(StringComparer.Ordinal));
        Assert.All(plan.Operations.Where(o => o.IsDirectory), o => Assert.Equal(SyncOperationKind.Add, o.Kind));

        var rerun = Plan(source, [Folder("Docs"), File("Docs/a.txt", 1), Folder("docs (2)"), File("docs (2)/a.txt", 2)], SyncMode.Mirror, sourceIsCloud: true, destinationIsCloud: false, ignoreCase: true);
        Assert.True(rerun.IsEmpty, Ops(rerun));
    }

    [Fact]
    public void ACollisionNameNeverTakesAnotherSourceItemsName()
    {
        // "Readme (2).md" is a real source file, so the colliding "Readme.md" becomes "(3)".
        SyncEntry[] source = [File("README.md", 5), File("Readme (2).md", 7), File("Readme.md", 6)];

        var plan = Plan(source, [], SyncMode.Mirror, sourceIsCloud: true, destinationIsCloud: false, ignoreCase: true);

        Assert.Equal(
            ["README.md", "Readme (2).md", "Readme (3).md"],
            plan.Operations.Select(o => o.DestinationRelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CaseVariantsOntoACaseSensitiveCloudKeepTheirNames()
    {
        var plan = Plan([File("README.md", 5), File("Readme.md", 6)], [], SyncMode.Mirror, sourceIsCloud: true, destinationIsCloud: true, ignoreCase: false);

        Assert.DoesNotContain(plan.Operations, o => o.IsCollisionRename);
        Assert.Equal(["README.md", "Readme.md"], plan.Operations.Select(o => o.DestinationRelativePath).Order(StringComparer.Ordinal));
    }
}
