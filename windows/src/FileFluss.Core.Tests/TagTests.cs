// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Services;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Transfers;

namespace FileFluss.Core.Tests;

/// <summary>The tag model: the same rules as the Mac app's (FileFlussTests/TagTests.swift).</summary>
public sealed class TagModelTests
{
    private static readonly FileTag Red = new("Red", TagColor.Red);
    private static readonly FileTag Blue = new("Blue", TagColor.Blue);
    private static readonly FileTag Green = new("Green", TagColor.Green);
    private static readonly FileTag Project = new("Projekt X", TagColor.Purple);
    private static readonly FileTag Plain = new("Steuer 2026", TagColor.None);

    [Fact]
    public void ColourIndexesAreFinders()
    {
        Assert.Equal(1, (int)TagColor.Gray);
        Assert.Equal(2, (int)TagColor.Green);
        Assert.Equal(3, (int)TagColor.Purple);
        Assert.Equal(4, (int)TagColor.Blue);
        Assert.Equal(5, (int)TagColor.Yellow);
        Assert.Equal(6, (int)TagColor.Red);
        Assert.Equal(7, (int)TagColor.Orange);
        Assert.Equal(
            [TagColor.Red, TagColor.Orange, TagColor.Yellow, TagColor.Green, TagColor.Blue, TagColor.Purple, TagColor.Gray],
            FileTags.MenuOrder);
    }

    [Fact]
    public void TheStandardTagsAreTheSevenColoursUnderTheirEnglishNames()
    {
        Assert.Equal(FileTags.MenuOrder, FileTags.Standard.Select(t => t.Color));
        Assert.Equal(["Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Gray"], FileTags.Standard.Select(t => t.Name));
        Assert.All(FileTags.Standard, tag => Assert.True(tag.IsStandard()));

        // A tag of a standard colour under another name is somebody's own tag.
        Assert.False(Project.IsStandard());
        Assert.False(new FileTag("Red", TagColor.Blue).IsStandard());
        Assert.False(Plain.IsStandard());
    }

    [Fact]
    public void DotsLeaveOutTagsWithoutAColourAndAFolderTakesItsLastColour()
    {
        IReadOnlyList<FileTag> tags = [Red, Plain, Blue];
        Assert.Equal([TagColor.Red, TagColor.Blue], tags.DotColors());
        Assert.Equal(TagColor.Blue, tags.FolderTint());
        Assert.Null(new[] { Plain }.FolderTint());
        Assert.Empty(FileTags.None.DotColors());
    }

    [Fact]
    public void AddingPutsTheTagLastAndNeverTwice()
    {
        Assert.Equal([Red, Blue], new TagChange.Add(Blue).Apply([Red]));
        Assert.Equal([Red, Blue], new TagChange.Add(Red).Apply([Red, Blue]));
    }

    [Fact]
    public void RemovingGoesByNameAndLeavesTheOthersAlone()
    {
        Assert.Equal([Project, Blue], new TagChange.Remove("Red").Apply([Red, Project, Blue]));
        Assert.Equal([Red], new TagChange.Remove("Nope").Apply([Red]));
        Assert.Empty(new TagChange.RemoveAll().Apply([Red, Project]));
    }

    [Fact]
    public void TheMenuShowsWhetherAllSomeOrNoneOfTheSelectionHasATag()
    {
        var rows = TagMenuModel.Rows([Red, Blue, Green], [[Red, Blue], [Red], [Red]]);
        Assert.Equal([Red, Blue, Green], rows.Select(r => r.Tag));
        Assert.Equal([TagMenuModel.Presence.All, TagMenuModel.Presence.Some, TagMenuModel.Presence.None], rows.Select(r => r.Presence));
    }

    [Fact]
    public void ChoosingATagEveryItemHasRemovesItOtherwiseItIsAdded()
    {
        var rows = TagMenuModel.Rows([Red, Blue, Green], [[Red, Blue], [Red]]);
        Assert.Equal(new TagChange.Remove("Red"), rows[0].Change);
        Assert.Equal(new TagChange.Add(Blue), rows[1].Change);
        Assert.Equal(new TagChange.Add(Green), rows[2].Change);
    }

    [Fact]
    public void OtherTagsAreListedAfterTheStandardOnesOnceEach()
    {
        var rows = TagMenuModel.Rows([Red, Blue], [[Project, Red], [Project, Plain]]);
        Assert.Equal([Red, Blue, Project, Plain], rows.Select(r => r.Tag));
        Assert.Equal(TagMenuModel.Presence.All, rows[2].Presence);
        Assert.Equal(TagMenuModel.Presence.Some, rows[3].Presence);
        Assert.True(TagMenuModel.HasAnyTag([[], [Plain]]));
        Assert.False(TagMenuModel.HasAnyTag([[], []]));
    }

    [Fact]
    public void TheDotsTakeTheRoomOfUpToThreeOverlapping()
    {
        Assert.Equal(0, TagDotsLayout.Width(0));
        Assert.Equal(TagDotsLayout.Diameter, TagDotsLayout.Width(1));
        Assert.Equal(TagDotsLayout.Diameter + TagDotsLayout.Step, TagDotsLayout.Width(2));
        Assert.Equal(TagDotsLayout.Diameter + (2 * TagDotsLayout.Step), TagDotsLayout.Width(7));

        // The last three: the newest tag is the one on top.
        Assert.Equal(
            [TagColor.Yellow, TagColor.Green, TagColor.Blue],
            TagDotsLayout.Shown([TagColor.Red, TagColor.Orange, TagColor.Yellow, TagColor.Green, TagColor.Blue]));
        Assert.Equal([TagColor.Red], TagDotsLayout.Shown([TagColor.Red]));
    }

    [Fact]
    public void ATintRecoloursTheIconAndKeepsItsShape()
    {
        // A Windows folder's yellow, a darker shade of it, a half-transparent edge and a hole.
        byte[] pixels =
        [
            0x4A, 0xC8, 0xF4, 0xFF,
            0x2E, 0x8C, 0xB0, 0xFF,
            0x25, 0x64, 0x7A, 0x80,
            0x00, 0x00, 0x00, 0x00,
        ];
        var original = (byte[])pixels.Clone();
        TagTint.Recolour(pixels, TagColor.Red);

        // Red now leads where yellow's red and green were level.
        Assert.True(pixels[2] > pixels[1] && pixels[2] > pixels[0]);
        Assert.True(pixels[6] > pixels[5] && pixels[6] > pixels[4]);

        // The shading survives: the darker pixel is still the darker one. (Its red is at
        // full strength in both, so the difference is in the other two channels.)
        Assert.True(pixels[4] + pixels[5] + pixels[6] < pixels[0] + pixels[1] + pixels[2]);

        // Alpha is untouched, and a premultiplied colour never exceeds its alpha.
        Assert.Equal([original[3], original[7], original[11], original[15]], new[] { pixels[3], pixels[7], pixels[11], pixels[15] });
        Assert.True(pixels[8] <= 0x80 && pixels[9] <= 0x80 && pixels[10] <= 0x80);
        Assert.Equal([0, 0, 0, 0], pixels[12..16]);
    }

    [Fact]
    public void NoColourLeavesTheIconAsItIs()
    {
        byte[] pixels = [0x4A, 0xC8, 0xF4, 0xFF];
        TagTint.Recolour(pixels, TagColor.None);
        Assert.Equal([0x4A, 0xC8, 0xF4, 0xFF], pixels);
    }

    [Fact]
    public void GreenAndBlueTintsLeadWithTheirOwnChannel()
    {
        byte[] green = [0x4A, 0xC8, 0xF4, 0xFF];
        TagTint.Recolour(green, TagColor.Green, premultiplied: false);
        Assert.True(green[1] > green[0] && green[1] > green[2]);

        byte[] blue = [0x4A, 0xC8, 0xF4, 0xFF];
        TagTint.Recolour(blue, TagColor.Blue, premultiplied: false);
        Assert.True(blue[0] > blue[1] && blue[0] > blue[2]);
    }
}

/// <summary>The store that keeps the tags, for this PC's files and for every account.</summary>
public sealed class TagStoreTests : IDisposable
{
    private static readonly FileTag Red = new("Red", TagColor.Red);
    private static readonly FileTag Blue = new("Blue", TagColor.Blue);
    private static readonly FileTag Green = new("Green", TagColor.Green);
    private static readonly FileTag Project = new("Projekt X", TagColor.Purple);

    private readonly string _root = Directory.CreateTempSubdirectory("ff-tags-").FullName;
    private readonly Guid _accountId = Guid.NewGuid();

    private TagScope Account => TagScope.Account(_accountId, lastingItemIds: false);

    private TagScope Lasting => TagScope.Account(_accountId, lastingItemIds: true);

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

    private static CloudFileItem Item(string id, string path)
        => new(id, path[(path.LastIndexOf('/') + 1)..], path, false, 1, DateTimeOffset.UnixEpoch);

    [Fact]
    public void TagsSetOnAPathAreReturnedForItAndGoneWhenCleared()
    {
        var store = new TagStore(_root);
        store.SetTags(Account, "/Docs/a.pdf", [Red, Blue]);
        Assert.Equal([Red, Blue], store.Tags(Account, "/Docs/a.pdf"));
        Assert.Empty(store.Tags(Account, "/Docs/b.pdf"));
        store.SetTags(Account, "/Docs/a.pdf", []);
        Assert.Empty(store.Tags(Account, "/Docs/a.pdf"));
    }

    [Fact]
    public void AccountsAndThisPcAreKeptApart()
    {
        var store = new TagStore(_root);
        var other = TagScope.Account(Guid.NewGuid(), lastingItemIds: false);
        store.SetTags(Account, "/a", [Red]);
        Assert.Empty(store.Tags(other, "/a"));
        Assert.Empty(store.Tags(TagScope.Local, "/a"));
    }

    [Fact]
    public void TagsSurviveARestartInTheMacAppsFormat()
    {
        var first = new TagStore(_root);
        first.SetTags(Lasting, "/Docs/a.pdf", [Project, Red], itemId: "id-1");
        first.Flush();

        // The file is named like the Mac app's, and reads the same there.
        var file = Path.Combine(_root, _accountId.ToString("D").ToUpperInvariant() + ".json");
        Assert.Equal(
            """{"version":1,"entries":{"/Docs/a.pdf":{"tags":[{"name":"Projekt X","color":3},{"name":"Red","color":6}],"itemID":"id-1"}}}""",
            File.ReadAllText(file));

        Assert.Equal([Project, Red], new TagStore(_root).Tags(Lasting, "/Docs/a.pdf"));
    }

    [Fact]
    public void AFileWrittenByTheMacAppIsRead()
    {
        // As the Mac writes it: keys sorted, no identifier for an item that has only just been uploaded.
        var file = Path.Combine(_root, _accountId.ToString("D").ToUpperInvariant() + ".json");
        File.WriteAllText(file, """{"entries":{"/Photos/Trip":{"tags":[{"color":2,"name":"Grün"}]}},"version":1}""");
        Assert.Equal([new FileTag("Grün", TagColor.Green)], new TagStore(_root).Tags(Account, "/Photos/Trip"));
    }

    /// <summary>The account's tags file, written once with Red on "/a"; no backup yet.</summary>
    private string WriteAccountFile()
    {
        using (var store = new TagStore(_root))
        {
            store.SetTags(Account, "/a", [Red]);
        }

        var file = Directory.GetFiles(_root, "*.json").Single();
        File.Delete(AtomicFile.BackupPath(file));
        return file;
    }

    [Fact]
    public void AFileHeldByAnotherProgramAtLaunchIsNotOverwritten()
    {
        var file = WriteAccountFile();
        var original = File.ReadAllBytes(file);

        // A scanner holds the file while the store first reads it, and has let go by the save.
        using (var store = new TagStore(_root))
        {
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Empty(store.Tags(Account, "/a"));
            }

            store.SetTags(Account, "/b", [Blue]);
            store.Flush();
        }

        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.False(File.Exists(AtomicFile.BackupPath(file)));
        Assert.Equal([Red], new TagStore(_root).Tags(Account, "/a"));
    }

    [Fact]
    public void ADamagedFileFallsBackToTheBackup()
    {
        var file = WriteAccountFile();
        File.Copy(file, AtomicFile.BackupPath(file));
        File.WriteAllText(file, "{ \"version\": 1, \"entr");

        Assert.Equal([Red], new TagStore(_root).Tags(Account, "/a"));
    }

    [Fact]
    public void WhenNeitherFileNorBackupCanBeReadBothAreLeftAsTheyAre()
    {
        var file = WriteAccountFile();
        var backup = AtomicFile.BackupPath(file);
        File.WriteAllText(file, "not json");
        File.WriteAllText(backup, "{ broken");

        using (var store = new TagStore(_root))
        {
            store.SetTags(Account, "/b", [Blue]);
            store.SetTags(Account, "/b", []);
            store.Flush();
        }

        Assert.Equal("not json", File.ReadAllText(file));
        Assert.Equal("{ broken", File.ReadAllText(backup));
    }

    [Fact]
    public void RemovingTheAccountStillRemovesAnUnreadableFile()
    {
        var file = WriteAccountFile();
        File.WriteAllText(file, "not json");

        using (var store = new TagStore(_root))
        {
            Assert.Empty(store.Tags(Account, "/a"));
            store.RemoveAccount(_accountId);
        }

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void AFailedSaveIsKeptPendingAndWrittenByTheNextOne()
    {
        // A file where the tags folder should be: nothing can be saved.
        var folder = Path.Combine(_root, "Tags");
        File.WriteAllText(folder, "x");
        using (var store = new TagStore(folder))
        {
            store.SetTags(Account, "/a", [Red]);
            store.Flush();

            File.Delete(folder);
            store.Flush();
        }

        Assert.Equal([Red], new TagStore(folder).Tags(Account, "/a"));
    }

    [Fact]
    public async Task ChangesAreWrittenWithoutBeingAskedAMomentLater()
    {
        var first = new TagStore(_root);
        first.SetTags(Account, "/a", [Red]);
        await Task.Delay(1500);
        Assert.Equal([Red], new TagStore(_root).Tags(Account, "/a"));
    }

    [Fact]
    public void ARenameTakesTheItemsTagsAndThoseOfEverythingInsideIt()
    {
        var store = new TagStore(_root);
        store.SetTags(Account, "/Old", [Red]);
        store.SetTags(Account, "/Old/inner/a.txt", [Blue]);
        store.SetTags(Account, "/Older/untouched.txt", [Green]);

        store.MoveSubtree(Account, "/Old", "/New");

        Assert.Equal([Red], store.Tags(Account, "/New"));
        Assert.Equal([Blue], store.Tags(Account, "/New/inner/a.txt"));
        Assert.Empty(store.Tags(Account, "/Old"));
        Assert.Empty(store.Tags(Account, "/Old/inner/a.txt"));

        // A sibling whose name merely starts the same way is not inside it.
        Assert.Equal([Green], store.Tags(Account, "/Older/untouched.txt"));
    }

    [Fact]
    public void AMoveOntoAnItemReplacesThatItemsTags()
    {
        var store = new TagStore(_root);
        store.SetTags(Account, "/a.txt", [Red]);
        store.SetTags(Account, "/b.txt", [Blue]);
        store.MoveSubtree(Account, "/a.txt", "/b.txt");
        Assert.Equal([Red], store.Tags(Account, "/b.txt"));
        Assert.Empty(store.Tags(Account, "/a.txt"));
    }

    [Fact]
    public void ACopyLeavesTheOriginalTaggedWithinAnAccountAndAcrossTwo()
    {
        var store = new TagStore(_root);
        var other = TagScope.Account(Guid.NewGuid(), lastingItemIds: false);
        store.SetTags(Account, "/Folder", [Red]);
        store.SetTags(Account, "/Folder/a.txt", [Blue]);

        store.CopySubtree(Account, "/Folder", Account, "/Copy");
        store.CopySubtree(Account, "/Folder", other, "/Backup/Folder");

        Assert.Equal([Blue], store.Tags(Account, "/Folder/a.txt"));
        Assert.Equal([Red], store.Tags(Account, "/Copy"));
        Assert.Equal([Blue], store.Tags(Account, "/Copy/a.txt"));
        Assert.Equal([Red], store.Tags(other, "/Backup/Folder"));
        Assert.Equal([Blue], store.Tags(other, "/Backup/Folder/a.txt"));
    }

    [Fact]
    public void ACopyOnlyAddsUnlessItReplaces()
    {
        var store = new TagStore(_root);
        store.SetTags(Account, "/dst/a.txt", [Blue]);

        // An untagged source leaves the tag at the destination alone…
        store.CopySubtree(Account, "/src/a.txt", Account, "/dst/a.txt");
        Assert.Equal([Blue], store.Tags(Account, "/dst/a.txt"));

        // …a tagged one brings its own…
        store.SetTags(Account, "/src/a.txt", [Red]);
        store.CopySubtree(Account, "/src/a.txt", Account, "/dst/a.txt");
        Assert.Equal([Red], store.Tags(Account, "/dst/a.txt"));

        // …and a copy that replaces takes away what the source doesn't have.
        store.SetTags(Account, "/dst/b.txt", [Blue]);
        store.CopySubtree(Account, "/src/b.txt", Account, "/dst/b.txt", replaceDestination: true);
        Assert.Empty(store.Tags(Account, "/dst/b.txt"));
    }

    [Fact]
    public void DeletingAFolderForgetsItAndWhatWasInside()
    {
        var store = new TagStore(_root);
        store.SetTags(Account, "/Folder", [Red]);
        store.SetTags(Account, "/Folder/a.txt", [Blue]);
        store.SetTags(Account, "/keep.txt", [Green]);
        store.RemoveSubtree(Account, "/Folder");
        Assert.Empty(store.Tags(Account, "/Folder"));
        Assert.Empty(store.Tags(Account, "/Folder/a.txt"));
        Assert.Equal([Green], store.Tags(Account, "/keep.txt"));
    }

    [Fact]
    public void RemovingAnAccountRemovesItsFile()
    {
        var store = new TagStore(_root);
        store.SetTags(Account, "/a", [Red]);
        store.Flush();
        var file = Path.Combine(_root, _accountId.ToString("D").ToUpperInvariant() + ".json");
        Assert.True(File.Exists(file));

        store.RemoveAccount(_accountId);
        store.Flush();
        Assert.False(File.Exists(file));
        Assert.Empty(store.Tags(Account, "/a"));
    }

    [Fact]
    public void AListingReturnsTheTagsOfItsTaggedItemsByPath()
    {
        var store = new TagStore(_root);
        store.SetTags(Account, "/Docs/a.pdf", [Red]);
        var found = store.Resolve(Account, [Item("/Docs/a.pdf", "/Docs/a.pdf"), Item("/Docs/b.pdf", "/Docs/b.pdf")]);
        Assert.Equal(["/Docs/a.pdf"], found.Keys);
        Assert.Equal([Red], found["/Docs/a.pdf"]);
    }

    [Fact]
    public void WithoutLastingIdentifiersARenameMadeElsewhereLeavesTheTagBehind()
    {
        var store = new TagStore(_root);
        store.SetTags(Account, "/a.pdf", [Red], itemId: "ignored");
        Assert.Empty(store.Resolve(Account, [Item("ignored", "/renamed.pdf")]));
    }

    [Fact]
    public void WithLastingIdentifiersATagFollowsAnItemRenamedElsewhere()
    {
        var store = new TagStore(_root);
        store.SetTags(Lasting, "/a.pdf", [Red], itemId: "id-1");

        var found = store.Resolve(Lasting, [Item("id-1", "/renamed.pdf")]);
        Assert.Equal([Red], found["/renamed.pdf"]);

        // And the record now knows the new path.
        Assert.Equal([Red], store.Tags(Lasting, "/renamed.pdf"));
        Assert.Empty(store.Tags(Lasting, "/a.pdf"));
    }

    [Fact]
    public void ANewlyWrittenItemIsRecognisedByItsPathAndRememberedByItsIdentifier()
    {
        var store = new TagStore(_root);

        // As a transfer leaves it: tagged, identifier not yet known.
        store.SetTags(Lasting, "/up.txt", [Blue]);
        store.Resolve(Lasting, [Item("id-9", "/up.txt")]);

        // Renamed elsewhere afterwards: found again by the identifier.
        var later = store.Resolve(Lasting, [Item("id-9", "/moved.txt")]);
        Assert.Equal([Blue], later["/moved.txt"]);
    }

    [Fact]
    public void AFileReplacedInPlaceKeepsItsTagAlthoughItsIdentifierIsNew()
    {
        var store = new TagStore(_root);
        store.SetTags(Lasting, "/report.pdf", [Red], itemId: "old-id");
        Assert.Equal([Red], store.Resolve(Lasting, [Item("new-id", "/report.pdf")])["/report.pdf"]);
    }

    [Fact]
    public void ANewFileAtARenamedItemsOldPathDoesNotTakeOverItsTag()
    {
        var store = new TagStore(_root);
        store.SetTags(Lasting, "/a.pdf", [Red], itemId: "id-1");

        // Elsewhere: a.pdf was renamed to b.pdf and a new a.pdf was made.
        var found = store.Resolve(Lasting, [Item("id-2", "/a.pdf"), Item("id-1", "/b.pdf")]);
        Assert.Equal(["/b.pdf"], found.Keys);
    }

    [Fact]
    public void TwoItemsThatSwappedNamesKeepTheirOwnTags()
    {
        var store = new TagStore(_root);
        store.SetTags(Lasting, "/a", [Red], itemId: "id-a");
        store.SetTags(Lasting, "/b", [Blue], itemId: "id-b");
        var found = store.Resolve(Lasting, [Item("id-a", "/b"), Item("id-b", "/a")]);
        Assert.Equal([Red], found["/b"]);
        Assert.Equal([Blue], found["/a"]);
    }

    [Fact]
    public void ADamagedFileIsNoTagsRatherThanAFailure()
    {
        File.WriteAllText(Path.Combine(_root, _accountId.ToString("D").ToUpperInvariant() + ".json"), "not json");
        var store = new TagStore(_root);
        Assert.Empty(store.Tags(Account, "/a"));
        store.SetTags(Account, "/a", [Red]);
        Assert.Equal([Red], store.Tags(Account, "/a"));
    }

    [Fact]
    public void AColourThisVersionDoesntKnowIsShownAsNoColour()
    {
        File.WriteAllText(
            Path.Combine(_root, _accountId.ToString("D").ToUpperInvariant() + ".json"),
            """{"version":1,"entries":{"/a":{"tags":[{"name":"Odd","color":42}]}}}""");
        Assert.Equal([new FileTag("Odd", TagColor.None)], new TagStore(_root).Tags(Account, "/a"));
    }

    [Fact]
    public void FilesOnThisPcAreMatchedAsWindowsMatchesThem()
    {
        var store = new TagStore(_root);
        store.SetTags(TagScope.Local, @"C:\Users\Robert\Documents\Report.pdf", [Red]);

        // NTFS folds case, so the same file under another spelling is the same file.
        Assert.Equal([Red], store.Tags(TagScope.Local, @"c:\users\robert\documents\report.pdf"));

        // A folder and its neighbour with a longer name are told apart at the separator.
        store.SetTags(TagScope.Local, @"C:\Docs\a.txt", [Blue]);
        store.SetTags(TagScope.Local, @"C:\Docs2\b.txt", [Green]);
        store.MoveSubtree(TagScope.Local, @"C:\Docs", @"C:\Archive");
        Assert.Equal([Blue], store.Tags(TagScope.Local, @"C:\Archive\a.txt"));
        Assert.Equal([Green], store.Tags(TagScope.Local, @"C:\Docs2\b.txt"));

        // A whole drive's worth goes by the drive root.
        store.RemoveSubtree(TagScope.Local, @"C:\");
        Assert.Empty(store.Tags(TagScope.Local, @"C:\Archive\a.txt"));
    }

    [Fact]
    public void WhichProvidersItemsHaveTagsAndHow()
    {
        Assert.Same(TagScope.Local, TagScope.For(LocalFileProvider.Shared));

        var dropbox = new MemoryCloudProvider(CloudProviderType.Dropbox);
        var scope = TagScope.For(dropbox);
        Assert.NotNull(scope);
        Assert.True(scope.LastingItemIds);
        Assert.Equal(dropbox.AccountId.ToString("D").ToUpperInvariant(), scope.Key);

        // Their identifiers are the path, so they say nothing a path doesn't.
        Assert.False(TagScope.For(new MemoryCloudProvider(CloudProviderType.Sftp))!.LastingItemIds);
        Assert.False(CloudProviderType.Box.HasLastingItemIds());
        Assert.False(CloudProviderType.Koofr.HasLastingItemIds());
        Assert.True(CloudProviderType.Mega.HasLastingItemIds());
        Assert.True(CloudProviderType.GoogleDrive.HasLastingItemIds());

        Assert.Null(TagScope.For(null));
    }
}

/// <summary>Tags travel with what FileFluss copies or moves — in every direction, through the one transfer engine.</summary>
public sealed class TagTransferTests : IDisposable
{
    private static readonly FileTag Red = new("Red", TagColor.Red);
    private static readonly FileTag Blue = new("Blue", TagColor.Blue);
    private static readonly FileTag Green = new("Green", TagColor.Green);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-tagtransfer-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileProvider _local = LocalFileProvider.Shared;
    private readonly TagStore _store;

    public TagTransferTests()
    {
        Directory.CreateDirectory(_root);
        LocalFileProvider.RecycleHandler = null;
        _store = new TagStore(LocalDir("store"));
    }

    public void Dispose()
    {
        // The store saves on a timer; deleting the folder while that save was writing its
        // temporary file failed the test now and then on CI ("being used by another
        // process"). Disposing waits for a save in progress and writes what's pending first.
        _store.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string LocalDir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static TagScope Scope(FileProvider provider) => TagScope.For(provider)!;

    private async Task<TransferProgress> Run(TransferRequest request)
    {
        request = request with { Tags = _store };
        var progress = TransferEngine.CreateProgress(request, PanelSide.Right);
        await TransferEngine.RunAsync(request, progress);
        return progress;
    }

    [Fact]
    public async Task AnUploadTakesTheFilesTagsToTheAccount()
    {
        var source = LocalDir("up");
        var file = Path.Combine(source, "a.txt");
        await File.WriteAllTextAsync(file, "aaa");
        _store.SetTags(TagScope.Local, file, [Red]);
        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/Docs");

        var progress = await Run(new TransferRequest(_local, [await _local.GetFileMetadataAsync(file)], cloud, "/Docs", IsMove: false));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal([Red], _store.Tags(Scope(cloud), "/Docs/a.txt"));

        // A copy: the original keeps its tag.
        Assert.Equal([Red], _store.Tags(TagScope.Local, file));
    }

    [Fact]
    public async Task AFolderBringsTheTagsOfEverythingInsideIt()
    {
        var source = LocalDir("up");
        var folder = Path.Combine(source, "Trip");
        Directory.CreateDirectory(Path.Combine(folder, "day1"));
        var photo = Path.Combine(folder, "day1", "photo.jpg");
        await File.WriteAllTextAsync(photo, "jpg");
        await File.WriteAllTextAsync(Path.Combine(folder, "untagged.txt"), "x");
        _store.SetTags(TagScope.Local, folder, [Green]);
        _store.SetTags(TagScope.Local, photo, [Red]);
        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/Photos");

        var progress = await Run(new TransferRequest(_local, [await _local.GetFileMetadataAsync(folder)], cloud, "/Photos", IsMove: false));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal([Green], _store.Tags(Scope(cloud), "/Photos/Trip"));
        Assert.Equal([Red], _store.Tags(Scope(cloud), "/Photos/Trip/day1/photo.jpg"));
        Assert.Empty(_store.Tags(Scope(cloud), "/Photos/Trip/untagged.txt"));
    }

    [Fact]
    public async Task ADownloadBringsTheItemsTagsToThisPc()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Docs/a.txt", "aaa");
        _store.SetTags(Scope(cloud), "/Docs/a.txt", [Blue]);
        var target = LocalDir("down");

        var progress = await Run(new TransferRequest(cloud, [await cloud.GetFileMetadataAsync("/Docs/a.txt")], _local, target, IsMove: false));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal([Blue], _store.Tags(TagScope.Local, Path.Combine(target, "a.txt")));
    }

    [Fact]
    public async Task FromOneAccountToAnother()
    {
        var a = new MemoryCloudProvider();
        var b = new MemoryCloudProvider();
        a.AddFile("/Folder/one.txt", "12345");
        b.AddFolder("/Inbox");
        _store.SetTags(Scope(a), "/Folder", [Green]);
        _store.SetTags(Scope(a), "/Folder/one.txt", [Red]);

        var progress = await Run(new TransferRequest(a, [await a.GetFileMetadataAsync("/Folder")], b, "/Inbox", IsMove: false));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal([Green], _store.Tags(Scope(b), "/Inbox/Folder"));
        Assert.Equal([Red], _store.Tags(Scope(b), "/Inbox/Folder/one.txt"));
        Assert.Equal([Red], _store.Tags(Scope(a), "/Folder/one.txt"));
    }

    [Fact]
    public async Task AMoveTheServerDoesTakesTheTagsAlong()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Folder/one.txt", "12345");
        cloud.AddFolder("/Archive");
        _store.SetTags(Scope(cloud), "/Folder", [Green]);
        _store.SetTags(Scope(cloud), "/Folder/one.txt", [Red]);

        var progress = await Run(new TransferRequest(cloud, [await cloud.GetFileMetadataAsync("/Folder")], cloud, "/Archive", IsMove: true));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.True(cloud.ServerSideCalls > 0);
        Assert.Equal([Green], _store.Tags(Scope(cloud), "/Archive/Folder"));
        Assert.Equal([Red], _store.Tags(Scope(cloud), "/Archive/Folder/one.txt"));
        Assert.Empty(_store.Tags(Scope(cloud), "/Folder"));
        Assert.Empty(_store.Tags(Scope(cloud), "/Folder/one.txt"));
    }

    [Fact]
    public async Task ACopyTheServerDoesLeavesTheOriginalTagged()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/one.txt", "12345");
        cloud.AddFolder("/Archive");
        _store.SetTags(Scope(cloud), "/one.txt", [Red]);

        var progress = await Run(new TransferRequest(cloud, [await cloud.GetFileMetadataAsync("/one.txt")], cloud, "/Archive", IsMove: false));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal([Red], _store.Tags(Scope(cloud), "/Archive/one.txt"));
        Assert.Equal([Red], _store.Tags(Scope(cloud), "/one.txt"));
    }

    [Fact]
    public async Task AMoveByBytesForgetsTheOldPlace()
    {
        var source = LocalDir("move");
        var folder = Path.Combine(source, "Trip");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "a.txt");
        await File.WriteAllTextAsync(file, "aaa");
        _store.SetTags(TagScope.Local, folder, [Green]);
        _store.SetTags(TagScope.Local, file, [Red]);
        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/Photos");

        var progress = await Run(new TransferRequest(_local, [await _local.GetFileMetadataAsync(folder)], cloud, "/Photos", IsMove: true));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.False(Directory.Exists(folder));
        Assert.Equal([Green], _store.Tags(Scope(cloud), "/Photos/Trip"));
        Assert.Equal([Red], _store.Tags(Scope(cloud), "/Photos/Trip/a.txt"));
        Assert.Empty(_store.Tags(TagScope.Local, folder));
        Assert.Empty(_store.Tags(TagScope.Local, file));
    }

    [Fact]
    public async Task AnUntaggedFileCopiedOverATaggedOneLeavesThatTagAlone()
    {
        // What a sync does when it refreshes a file: the copy replaces without asking.
        var source = LocalDir("sync");
        var file = Path.Combine(source, "a.txt");
        await File.WriteAllTextAsync(file, "newer");
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Docs/a.txt", "older");
        _store.SetTags(Scope(cloud), "/Docs/a.txt", [Blue]);

        var progress = await Run(new TransferRequest(_local, [await _local.GetFileMetadataAsync(file)], cloud, "/Docs", IsMove: false));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal("newer", cloud.Read("/Docs/a.txt"));
        Assert.Equal([Blue], _store.Tags(Scope(cloud), "/Docs/a.txt"));
    }

    [Fact]
    public async Task ATransferWithNoTagsWritesNothing()
    {
        var source = LocalDir("plain");
        var file = Path.Combine(source, "a.txt");
        await File.WriteAllTextAsync(file, "aaa");
        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/Docs");

        var progress = await Run(new TransferRequest(_local, [await _local.GetFileMetadataAsync(file)], cloud, "/Docs", IsMove: true));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        _store.Flush();
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "store")));
    }
}
