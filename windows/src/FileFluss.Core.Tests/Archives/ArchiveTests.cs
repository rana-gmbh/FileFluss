// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FileFluss.Core.Archives;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Transfers;

namespace FileFluss.Core.Tests.Archives;

public sealed class ArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-archive-" + Guid.NewGuid().ToString("N"));

    public ArchiveTests() => Directory.CreateDirectory(_root);

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

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Zip(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            var created = zip.CreateEntry(entry);
            if (!entry.EndsWith('/'))
            {
                using var stream = created.Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return path;
    }

    /// <summary>Every file below <paramref name="folder"/>, relative, slash-separated.</summary>
    private static List<string> Files(string folder)
        => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    // MARK: - Formats and names

    [Theory]
    [InlineData("a.zip", ArchiveFormat.Zip)]
    [InlineData("Comic.CBZ", ArchiveFormat.Zip)]
    [InlineData("a.rar", ArchiveFormat.Rar)]
    [InlineData("a.7z", ArchiveFormat.SevenZip)]
    [InlineData("a.tar", ArchiveFormat.Tar)]
    [InlineData("a.tar.gz", ArchiveFormat.TarGzip)]
    [InlineData("a.tgz", ArchiveFormat.TarGzip)]
    [InlineData("a.tar.bz2", ArchiveFormat.TarBzip2)]
    [InlineData("a.tar.xz", ArchiveFormat.TarXz)]
    [InlineData("notes.txt.gz", ArchiveFormat.Gzip)]
    [InlineData("notes.txt.bz2", ArchiveFormat.Bzip2)]
    [InlineData("notes.txt.xz", ArchiveFormat.Xz)]
    public void DetectsArchivesByExtension(string name, ArchiveFormat expected) => Assert.Equal(expected, ArchiveFormats.Detect(name));

    [Theory]
    [InlineData("report.docx")]
    [InlineData("book.epub")]
    [InlineData(".zip")]
    [InlineData("readme")]
    public void LeavesOtherFilesAlone(string name) => Assert.Null(ArchiveFormats.Detect(name));

    [Theory]
    [InlineData("photos.tar.gz", "photos")]
    [InlineData("photos.ZIP", "photos")]
    [InlineData("notes.txt.gz", "notes.txt")]
    [InlineData("plain", "plain")]
    public void BaseNameDropsTheWholeArchiveExtension(string name, string expected) => Assert.Equal(expected, ArchiveFormats.BaseName(name));

    [Fact]
    public void ArchiveNamesFollowFinder()
    {
        var taken = new HashSet<string> { "Archive.tar.gz", "Archive 2.tar.gz", "photo.jpg.zip" };
        Assert.Equal("photo.jpg.zip", ArchiveCreationFormat.Zip.ArchiveName(["photo.jpg"], _ => false));
        Assert.Equal("photo.jpg 2.zip", ArchiveCreationFormat.Zip.ArchiveName(["photo.jpg"], taken.Contains));
        Assert.Equal("Archive 3.tar.gz", ArchiveCreationFormat.TarGzip.ArchiveName(["a", "b"], taken.Contains));
    }

    [Fact]
    public void AvailableNameNumbersBeforeTheExtension()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "photos", "photos 2", "a.txt" };
        Assert.Equal("fresh", ArchiveExtractor.AvailableName("fresh", taken.Contains));
        Assert.Equal("photos 3", ArchiveExtractor.AvailableName("photos", taken.Contains));
        Assert.Equal("a 2.txt", ArchiveExtractor.AvailableName("a.txt", taken.Contains));
    }

    // MARK: - Hostile entry names

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..\\..\\evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("C:\\evil.txt")]
    [InlineData("file.txt:payload")]
    [InlineData("CON")]
    [InlineData("docs/nul.txt")]
    [InlineData("COM1.log")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("what?.txt")]
    [InlineData("pipe|name")]
    [InlineData("tab\tname")]
    [InlineData("")]
    [InlineData("/")]
    public void RefusesEscapingAndWindowsHostileNames(string raw) => Assert.Null(ArchiveEntry.Normalise(raw));

    [Theory]
    [InlineData("./a/b.txt", "a/b.txt")]
    [InlineData("/abs/path.txt", "abs/path.txt")]
    [InlineData("dir\\sub\\file.txt", "dir/sub/file.txt")]
    [InlineData("folder/", "folder")]
    [InlineData("Ordner é/日本語.txt", "Ordner é/日本語.txt")]
    [InlineData(".hidden", ".hidden")]
    public void NormalisesOrdinaryNames(string raw, string expected) => Assert.Equal(expected, ArchiveEntry.Normalise(raw));

    [Fact]
    public void HostileZipListsOnlySafeEntriesAndNothingEscapes()
    {
        var archive = Zip("hostile.zip",
            ("good/ok.txt", "ok"),
            ("../escape.txt", "x"),
            ("..\\escape2.txt", "x"),
            ("C:/abs.txt", "x"),
            ("ads.txt:stream", "x"),
            ("aux.c", "x"),
            ("__MACOSX/._ok.txt", "x"));

        var tree = ArchiveReader.List(archive, ArchiveFormat.Zip);
        Assert.Equal(["good", "good/ok.txt"], tree.Entries.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal("../escape.txt", tree.FirstUnsafeEntry);
        Assert.Equal(5, tree.UnsafeEntryCount);

        var destination = Dir("out");
        ArchiveExtractor.ExtractAll(archive, ArchiveFormat.Zip, destination);
        Assert.Equal(["good/ok.txt"], Files(destination));
        Assert.Equal(["hostile.zip", "out"], Directory.GetFileSystemEntries(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TarWithEscapingLinksAndPathsIsContained()
    {
        var path = Path.Combine(_root, "links.tar");
        using (var file = File.Create(path))
        using (var writer = new TarWriter(file, TarEntryFormat.Pax))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "safe.txt") { DataStream = new MemoryStream("safe"u8.ToArray()) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "../../Windows" });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../up.txt") { DataStream = new MemoryStream("x"u8.ToArray()) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "hard.txt") { LinkName = "safe.txt" });
        }

        var tree = ArchiveReader.List(path, ArchiveFormat.Tar);
        Assert.Equal("link", tree.FirstUnsafeEntry);
        Assert.True(tree.Entry("link")!.IsSymlink);
        Assert.Null(tree.Entry("up.txt"));

        var destination = Dir("tar-out");
        ArchiveExtractor.ExtractAll(path, ArchiveFormat.Tar, destination);
        Assert.Equal(["hard.txt", "safe.txt"], Files(destination));
        Assert.Equal("safe", File.ReadAllText(Path.Combine(destination, "hard.txt")));
    }

    [Fact]
    public void TargetPathRefusesWhatSlippedThrough()
    {
        var root = Dir("target");
        Assert.Equal(Path.Combine(root, "a", "b.txt"), ArchiveExtractor.TargetPath("a/b.txt", string.Empty, root));
        Assert.Equal(Path.Combine(root, "b.txt"), ArchiveExtractor.TargetPath("x/a/b.txt", "x/a", root));
        Assert.Throws<ArchiveException>(() => ArchiveExtractor.TargetPath("../b.txt", string.Empty, root));
        Assert.Throws<ArchiveException>(() => ArchiveExtractor.TargetPath("a/C:/b.txt", string.Empty, root));
    }

    // MARK: - The tree

    [Fact]
    public void TreeSynthesisesFoldersAndRollsUpSizes()
    {
        var date = DateTimeOffset.UtcNow;
        var tree = new ArchiveTree(
        [
            new ArchiveEntry("a/b/c.txt", false, false, 10, date, false),
            new ArchiveEntry("a/d.txt", false, false, 5, date, false),
            new ArchiveEntry("top.txt", false, false, 1, date, false),
            new ArchiveEntry("top.txt", false, false, 2, date, false),
        ]);

        Assert.Equal(["a", "top.txt"], tree.TopLevel.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.True(tree.Entry("a/b")!.IsDirectory);
        Assert.Equal(15, tree.RecursiveSize("a"));
        Assert.Equal(10, tree.RecursiveSize("a/b"));
        Assert.Equal(2, tree.Entry("top.txt")!.Size);
        Assert.Equal(["a", "a/b", "a/b/c.txt", "a/d.txt"], tree.Subtree("a").Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal(3, tree.FileCount);
        Assert.Equal(17, tree.TotalSize);

        var row = tree.ItemFor(tree.Entry("a")!);
        Assert.Equal("/a", row.Path);
        Assert.Equal(15, row.Size);
        Assert.True(row.IsDirectory);
    }

    // MARK: - Extraction

    [Fact]
    public void ExtractStripsThePrefixOfTheFolderOnScreen()
    {
        var archive = Zip("nested.zip", ("docs/reports/2026/q1.txt", "q1"), ("docs/reports/2026/q2.txt", "q2"), ("docs/other.txt", "o"));
        var destination = Dir("prefix");
        var tree = ArchiveReader.List(archive, ArchiveFormat.Zip);
        var written = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, tree.Subtree("docs/reports/2026").Select(e => e.Path).ToList(), "docs/reports", destination);

        Assert.Equal([Path.Combine(destination, "2026")], written);
        Assert.Equal(["2026/q1.txt", "2026/q2.txt"], Files(destination));
    }

    [Fact]
    public async Task UnpackLandsOneTopLevelItemAsItself()
    {
        var archive = Zip("project.zip", ("project/src/main.c", "int main;"), ("project/README", "hi"));
        var destination = Dir("one");
        Directory.CreateDirectory(Path.Combine(destination, "project"));

        var result = await ArchiveExtractor.UnpackAsync(archive, "project.zip", ArchiveFormat.Zip, destination,
            name => Directory.Exists(Path.Combine(destination, name)) || File.Exists(Path.Combine(destination, name)));

        Assert.Equal(Path.Combine(destination, "project 2"), result.Destination);
        Assert.Equal(["project 2/README", "project 2/src/main.c"], Files(destination));
        Assert.Equal(["project", "project 2"], Directory.GetDirectories(destination).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.False(new DirectoryInfo(result.Destination).Attributes.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public async Task UnpackPutsSeveralItemsIntoAFolderNamedAfterTheArchive()
    {
        var archive = Zip("photos.zip", ("a.jpg", "a"), ("b.jpg", "b"));
        var destination = Dir("several");

        var result = await ArchiveExtractor.UnpackAsync(archive, "photos.zip", ArchiveFormat.Zip, destination, _ => false);

        Assert.Equal(Path.Combine(destination, "photos"), result.Destination);
        Assert.Equal(["photos/a.jpg", "photos/b.jpg"], Files(destination));
        Assert.False(new DirectoryInfo(result.Destination).Attributes.HasFlag(FileAttributes.Hidden));
        Assert.Single(Directory.GetFileSystemEntries(destination));
    }

    [Fact]
    public async Task UnpackOfASingleFileArchiveLandsTheFile()
    {
        var archive = Zip("single.zip", ("notes.txt", "n"));
        var destination = Dir("single");
        File.WriteAllText(Path.Combine(destination, "notes.txt"), "existing");

        var result = await ArchiveExtractor.UnpackAsync(archive, "single.zip", ArchiveFormat.Zip, destination, name => File.Exists(Path.Combine(destination, name)));

        Assert.Equal(Path.Combine(destination, "notes 2.txt"), result.Destination);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(destination, "notes.txt")));
        Assert.Equal("n", File.ReadAllText(result.Destination));
    }

    [Fact]
    public async Task CancelledUnpackLeavesNothingBehind()
    {
        var archive = Zip("cancel.zip", ("a.txt", new string('a', 100_000)), ("b.txt", "b"));
        var destination = Dir("cancelled");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArchiveExtractor.UnpackAsync(archive, "cancel.zip", ArchiveFormat.Zip, destination, _ => false, null, cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    [Fact]
    public void LargeEntriesAreStreamedNotBuffered()
    {
        const int size = 96 * 1024 * 1024;
        var path = Path.Combine(_root, "large.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("big.bin", CompressionLevel.Fastest).Open())
        {
            var block = new byte[1024 * 1024];
            for (var i = 0; i < size / block.Length; i++)
            {
                stream.Write(block);
            }
        }

        var target = Path.Combine(_root, "big.bin");
        long reported = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        ArchiveExtractor.ExtractFile(path, ArchiveFormat.Zip, "big.bin", target, d => reported += d);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(size, new FileInfo(target).Length);
        Assert.Equal(size, reported);
        Assert.True(allocated < size / 4, $"allocated {allocated:N0} bytes extracting {size:N0}");
        Assert.False(File.Exists(target + ".ffpart"));
    }

    // MARK: - Single compressed streams

    [Fact]
    public void GzipOfOneFileListsAsThatFile()
    {
        var path = Path.Combine(_root, "notes.txt.gz");
        using (var gzip = new GZipStream(File.Create(path), CompressionLevel.Optimal))
        {
            gzip.Write("hello notes"u8);
        }

        var tree = ArchiveReader.List(path, ArchiveFormat.Gzip);
        var entry = Assert.Single(tree.Entries);
        Assert.Equal("notes.txt", entry.Path);
        Assert.Equal(11, entry.Size);

        var target = Path.Combine(_root, "notes.txt");
        ArchiveExtractor.ExtractFile(path, ArchiveFormat.Gzip, "notes.txt", target);
        Assert.Equal("hello notes", File.ReadAllText(target));
    }

    [Fact]
    public void TarballMisnamedAsGzipReadsAsTheTarItIs()
    {
        var path = Path.Combine(_root, "backup.gz");
        using (var gzip = new GZipStream(File.Create(path), CompressionLevel.Optimal))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "one.txt") { DataStream = new MemoryStream("1"u8.ToArray()) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "two.txt") { DataStream = new MemoryStream("2"u8.ToArray()) });
        }

        var tree = ArchiveReader.List(path, ArchiveFormat.Gzip);
        Assert.Equal(["one.txt", "two.txt"], tree.Entries.Select(e => e.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AFileThatIsNoArchiveIsUnreadable()
    {
        var path = Path.Combine(_root, "fake.zip");
        File.WriteAllText(path, "this is not a zip file at all");
        var error = Assert.Throws<ArchiveException>(() => ArchiveReader.List(path, ArchiveFormat.Zip));
        Assert.Equal(ArchiveErrorKind.Unreadable, error.Kind);

        var gz = Path.Combine(_root, "fake.tar.gz");
        File.WriteAllText(gz, "not gzip either");
        Assert.Equal(ArchiveErrorKind.Unreadable, Assert.Throws<ArchiveException>(() => ArchiveReader.List(gz, ArchiveFormat.TarGzip)).Kind);
    }

    // MARK: - Writing

    public static TheoryData<ArchiveCreationFormat> WritableFormats()
    {
        var data = new TheoryData<ArchiveCreationFormat> { ArchiveCreationFormat.Zip, ArchiveCreationFormat.SevenZip, ArchiveCreationFormat.TarGzip, ArchiveCreationFormat.TarBzip2 };
        if (ArchiveWriter.CanCreate(ArchiveCreationFormat.TarXz))
        {
            data.Add(ArchiveCreationFormat.TarXz);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WritableFormats))]
    public async Task EveryWritableFormatRoundTripsUnicodeNames(ArchiveCreationFormat format)
    {
        var folder = Dir("pack-" + format);
        Directory.CreateDirectory(Path.Combine(folder, "Ordner é", "leer"));
        File.WriteAllText(Path.Combine(folder, "Ordner é", "日本語.txt"), "konnichiwa");
        File.WriteAllText(Path.Combine(folder, "-dash.txt"), "dash");
        File.WriteAllBytes(Path.Combine(folder, "random.bin"), RandomBytes(300_000));
        var added = new List<string>();
        long bytes = 0;

        var name = format.ArchiveName(["Ordner é", "-dash.txt", "random.bin"], n => File.Exists(Path.Combine(folder, n)));
        var archive = await ArchiveWriter.CreateAsync(folder, ["Ordner é", "-dash.txt", "random.bin"], name, format, added.Add, d => bytes += d);

        Assert.Equal(Path.Combine(folder, "Archive." + format.FileExtension()), archive);
        Assert.Contains("Ordner é/日本語.txt", added);
        Assert.Equal(10 + 4 + 300_000, bytes);
        Assert.DoesNotContain(Directory.GetFiles(folder), f => f.Contains(".partial", StringComparison.Ordinal));

        var readFormat = ArchiveFormats.Detect(Path.GetFileName(archive))!.Value;
        var tree = ArchiveReader.List(archive, readFormat);
        Assert.True(tree.Entry("Ordner é/leer")!.IsDirectory);
        Assert.Equal(10, tree.Entry("Ordner é/日本語.txt")!.Size);

        var destination = Dir("unpacked-" + format);
        ArchiveExtractor.ExtractAll(archive, readFormat, destination);
        Assert.Equal("konnichiwa", File.ReadAllText(Path.Combine(destination, "Ordner é", "日本語.txt")));
        Assert.Equal("dash", File.ReadAllText(Path.Combine(destination, "-dash.txt")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(folder, "random.bin")), File.ReadAllBytes(Path.Combine(destination, "random.bin")));
        Assert.True(Directory.Exists(Path.Combine(destination, "Ordner é", "leer")));
    }

    [Fact]
    public async Task CreatingNeverOverwritesAnExistingArchive()
    {
        var folder = Dir("exists");
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        File.WriteAllText(Path.Combine(folder, "a.txt.zip"), "older");

        var error = await Assert.ThrowsAsync<ArchiveException>(() => ArchiveWriter.CreateAsync(folder, ["a.txt"], "a.txt.zip", ArchiveCreationFormat.Zip));
        Assert.Equal(ArchiveErrorKind.AlreadyExists, error.Kind);
        Assert.Equal("older", File.ReadAllText(Path.Combine(folder, "a.txt.zip")));
    }

    [Fact]
    public async Task CancelledCompressionLeavesNoPartialArchive()
    {
        var folder = Dir("cancel-pack");
        File.WriteAllBytes(Path.Combine(folder, "big.bin"), RandomBytes(2_000_000));
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArchiveWriter.CreateAsync(folder, ["big.bin"], "big.bin.zip", ArchiveCreationFormat.Zip, null, _ => cancellation.Cancel(), cancellation.Token));
        Assert.Equal(["big.bin"], Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task NamesThatAreNoPlainNamesAreRefused()
    {
        var folder = Dir("refuse");
        await Assert.ThrowsAsync<ArchiveException>(() => ArchiveWriter.CreateAsync(folder, ["..\\x"], "x.zip", ArchiveCreationFormat.Zip));
        await Assert.ThrowsAsync<ArchiveException>(() => ArchiveWriter.CreateAsync(folder, ["a"], "..\\x.zip", ArchiveCreationFormat.Zip));
    }

    [Fact]
    public void ToolArgumentsCantBeReadAsOptions()
    {
        Assert.Equal(".\\-df", ArchiveWriter.CommandLinePath("-df"));
        Assert.Equal(".\\@list.txt", ArchiveWriter.CommandLinePath("@list.txt"));
    }

    [Theory]
    [InlineData("Adding    .\\docs\\file.txt                OK ", "docs\\file.txt")]
    [InlineData("Adding    report.pdf     45%\b\b\b\b  OK", "report.pdf")]
    [InlineData("a sub/file.txt", "sub/file.txt")]
    [InlineData("  adding: sub/file.txt (deflated 62%)", "sub/file.txt")]
    [InlineData("Creating archive x.rar", null)]
    public void ParsesToolProgressLines(string line, string? expected) => Assert.Equal(expected, ArchiveWriter.AddedFileName(line));

    // MARK: - The provider

    [Fact]
    public async Task ProviderBrowsesReadOnly()
    {
        var archive = Zip("browse.zip", ("docs/a.txt", "aaa"), ("docs/sub/b.txt", "bb"), (".hidden", "h"));
        var provider = new ArchiveProvider(archive, ArchiveFormat.Zip, "browse.zip");

        var root = await provider.ListDirectoryAsync("/");
        Assert.Equal(["/.hidden", "/docs"], root.Select(i => i.Path).Order(StringComparer.Ordinal));
        Assert.True(root.Single(i => i.Name == ".hidden").IsHidden);
        Assert.Equal(5, root.Single(i => i.Name == "docs").Size);

        var docs = await provider.ListDirectoryAsync("/docs");
        Assert.Equal(["a.txt", "sub"], docs.Select(i => i.Name).Order(StringComparer.Ordinal));
        Assert.Equal("/docs/sub/b.txt", Assert.Single((await provider.SearchFilesAsync("b.t", "/docs"))!).Path);
        Assert.Equal(5, await provider.FolderSizeAsync("/docs"));

        var missing = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/nope"));
        Assert.Equal(CloudErrorKind.NotFound, missing.Kind);
        await Assert.ThrowsAsync<CloudProviderException>(() => provider.DeleteItemAsync("/docs/a.txt"));
        await Assert.ThrowsAsync<CloudProviderException>(() => provider.RenameItemAsync("/docs/a.txt", "x"));
        await Assert.ThrowsAsync<CloudProviderException>(() => provider.UploadFileAsync(archive, "/x.zip"));
        await Assert.ThrowsAsync<CloudProviderException>(() => provider.CreateDirectoryAsync("/new"));

        var target = Path.Combine(_root, "a-out.txt");
        await provider.DownloadFileAsync("/docs/a.txt", target);
        Assert.Equal("aaa", File.ReadAllText(target));
        Assert.True(provider.IsRandomAccess);
    }

    [Fact]
    public async Task StreamArchivesArePreparedInOnePassAndCopiedOut()
    {
        var path = Path.Combine(_root, "stream.tar.gz");
        using (var gzip = new GZipStream(File.Create(path), CompressionLevel.Optimal))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "dir/"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "dir/one.txt") { DataStream = new MemoryStream("1"u8.ToArray()) });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "dir/two.txt") { DataStream = new MemoryStream("22"u8.ToArray()) });
        }

        var provider = new ArchiveProvider(path, ArchiveFormat.TarGzip, "stream.tar.gz");
        Assert.False(provider.IsRandomAccess);
        var items = await provider.ListDirectoryAsync("/");
        using (provider.Lease())
        {
            await provider.PrepareForTransferAsync(items);

            // The archive can go away now: everything asked for is staged.
            File.Move(path, path + ".moved");
            var target = Path.Combine(_root, "two.txt");
            await provider.DownloadFileAsync("/dir/two.txt", target);
            Assert.Equal("22", File.ReadAllText(target));

            // A panel moving on mid-transfer doesn't pull the staged files away.
            provider.Release();
            await provider.DownloadFileAsync("/dir/one.txt", Path.Combine(_root, "one.txt"));
        }

        var staged = await Assert.ThrowsAnyAsync<Exception>(() => provider.DownloadFileAsync("/dir/one.txt", Path.Combine(_root, "again.txt")));
        Assert.True(staged is ArchiveException or IOException or CloudProviderException);
    }

    [Fact]
    public async Task MaterializeUnpacksFoldersAndReusesWhatIsUnpacked()
    {
        var archive = Zip("mat.zip", ("folder/x.txt", "x"), ("folder/y/z.txt", "z"), ("solo.txt", "s"));
        var provider = new ArchiveProvider(archive, ArchiveFormat.Zip, "mat.zip");

        var staged = await provider.MaterializeAsync(["/folder", "/solo.txt"]);
        Assert.Equal(2, staged.Count);
        Assert.True(Directory.Exists(staged[0]));
        Assert.Equal("z", File.ReadAllText(Path.Combine(staged[0], "y", "z.txt")));
        Assert.Equal("s", File.ReadAllText(staged[1]));

        var again = await provider.MaterializeAsync(["/solo.txt"]);
        Assert.Equal(staged[1], again[0]);

        var root = Path.GetDirectoryName(staged[1])!;
        provider.Release();
        for (var i = 0; i < 50 && Directory.Exists(root); i++)
        {
            await Task.Delay(20);
        }

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task OpeningExtractsIntoAFolderOfItsOwn()
    {
        var archive = Zip("open.zip", ("deep/inside/doc.txt", "doc"));
        var provider = new ArchiveProvider(archive, ArchiveFormat.Zip, "open.zip");
        var opened = await provider.ExtractForOpeningAsync(["/deep/inside/doc.txt"]);
        Assert.Equal("doc.txt", Path.GetFileName(opened[0]));
        Assert.Equal("doc", File.ReadAllText(opened[0]));
        provider.Release();
        Assert.True(File.Exists(opened[0]));
        Directory.Delete(Path.GetDirectoryName(opened[0])!, recursive: true);
    }

    [Fact]
    public async Task ArchivesCopyOutThroughTheTransferEngine()
    {
        var archive = Zip("engine.zip", ("tree/a.txt", "a"), ("tree/b/c.txt", "c"));
        var provider = new ArchiveProvider(archive, ArchiveFormat.Zip, "engine.zip");
        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/dest");
        var items = await provider.ListDirectoryAsync("/");

        var request = new TransferRequest(provider, items, cloud, "/dest", false);
        var progress = TransferEngine.CreateProgress(request, PanelSide.Right);
        await TransferEngine.RunAsync(request, progress);

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal("c", cloud.Read("/dest/tree/b/c.txt"));
    }

    [Fact]
    public async Task SessionsShareProvidersAndReleaseWhenNobodyLooks()
    {
        var archive = Zip("session.zip", ("a.txt", "a"));
        var sessions = new ArchiveSessions();
        var provider = sessions.Open(null, archive, archive, ArchiveFormat.Zip, "session.zip");
        Assert.Same(provider, sessions.Open(null, archive.ToUpperInvariant(), archive, ArchiveFormat.Zip, "session.zip"));

        var location = PanelLocation.Archive(archive, null, "/");
        Assert.Same(provider, sessions.Find(location with { Path = "/sub" }));
        Assert.Null(sessions.Find(PanelLocation.Local(_root)));

        var left = new object();
        var right = new object();
        sessions.Show(left, location);
        sessions.Show(right, location);
        _ = await provider.TreeAsync();
        sessions.Show(left, PanelLocation.Local(_root));
        Assert.NotNull(provider.LoadedTree);
        sessions.Show(right, PanelLocation.Local(_root));
        Assert.Null(provider.LoadedTree);
    }

    [Fact]
    public void ArchiveLocationsKnowTheirContainer()
    {
        var local = PanelLocation.Archive(@"C:\Data\photos.zip", null, "/2026");
        Assert.True(local.IsArchive);
        Assert.False(local.IsLocal);
        Assert.False(local.IsCloud);
        Assert.Null(local.AccountId);
        Assert.Equal(PanelLocation.Local(@"C:\Data"), local.ArchiveContainer);
        Assert.Equal("photos.zip", local.ArchiveName);

        var account = Guid.NewGuid();
        var cloud = PanelLocation.Archive("/Backups/site.tar.gz", account);
        Assert.Equal(PanelLocation.Cloud(account, "/Backups"), cloud.ArchiveContainer);
        Assert.Equal("site.tar.gz", cloud.ArchiveName);

        var json = JsonSerializer.Serialize(cloud);
        Assert.Equal(cloud, JsonSerializer.Deserialize<PanelLocation>(json));
        Assert.DoesNotContain("archive", JsonSerializer.Serialize(PanelLocation.Local(@"C:\x")), StringComparison.Ordinal);
    }

    // MARK: - Jobs

    [Fact]
    public async Task CloudCompressDownloadsPacksAndUploadsBeside()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/work");
        cloud.AddFile("/work/a.txt", "alpha");
        cloud.AddFolder("/work/sub");
        cloud.AddFile("/work/sub/b.txt", "beta");
        var items = await cloud.ListDirectoryAsync("/work");

        var progress = ArchiveJobs.CreateProgress(TransferOperation.Compressing, "Archive.zip", PanelSide.Left);
        var result = await ArchiveJobs.CompressAsync(cloud, "/work", items, "Archive.zip", ArchiveCreationFormat.Zip, progress);

        Assert.Equal("/work/Archive.zip", result);
        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        var downloaded = Path.Combine(_root, "Archive.zip");
        await cloud.DownloadFileAsync("/work/Archive.zip", downloaded);
        var tree = ArchiveReader.List(downloaded, ArchiveFormat.Zip);
        Assert.Equal(["a.txt", "sub", "sub/b.txt"], tree.Entries.Select(e => e.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task LocalCompressPacksInPlace()
    {
        var folder = Dir("local-compress");
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        var items = await LocalFileProvider.Shared.ListDirectoryAsync(folder);
        var progress = ArchiveJobs.CreateProgress(TransferOperation.Compressing, "a.txt.tar.gz", PanelSide.Left);

        var result = await ArchiveJobs.CompressAsync(LocalFileProvider.Shared, folder, items, "a.txt.tar.gz", ArchiveCreationFormat.TarGzip, progress);

        Assert.Equal(Path.Combine(folder, "a.txt.tar.gz"), result);
        Assert.True(progress.IsComplete);
        Assert.Equal(1, progress.SuccessCount);
    }

    [Fact]
    public async Task CloudArchiveExtractsBesideItselfByFindersRule()
    {
        var archive = Zip("site.zip", ("index.html", "<html>"), ("css/site.css", "body{}"));
        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/web");
        cloud.AddFolder("/web/site");
        await cloud.UploadFileAsync(archive, "/web/site.zip");
        var item = await cloud.GetFileMetadataAsync("/web/site.zip");

        var progress = ArchiveJobs.CreateProgress(TransferOperation.Extracting, item.Name, PanelSide.Left);
        var landed = await ArchiveJobs.ExtractAsync(cloud, item, cloud, "/web", progress);

        Assert.Equal("/web/site 2", landed);
        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal("body{}", cloud.Read("/web/site 2/css/site.css"));
    }

    [Fact]
    public async Task LocalArchiveExtractsToALocalFolderAndNamesUnsafeEntries()
    {
        var archive = Zip("mixed.zip", ("ok.txt", "ok"), ("../evil.txt", "x"));
        var destination = Dir("extract-job");
        var item = await LocalFileProvider.Shared.GetFileMetadataAsync(archive);
        var progress = ArchiveJobs.CreateProgress(TransferOperation.Extracting, item.Name, PanelSide.Right);

        var landed = await ArchiveJobs.ExtractAsync(LocalFileProvider.Shared, item, LocalFileProvider.Shared, destination, progress);

        Assert.Equal(Path.Combine(destination, "ok.txt"), landed);
        Assert.Equal(1, progress.SuccessCount);
        Assert.Contains(progress.Results, r => r.Status == TransferItemStatus.Failed && r.Name == "../evil.txt");
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task ExtractJobReportsAnUnreadableArchive()
    {
        var path = Path.Combine(_root, "broken.zip");
        File.WriteAllText(path, "broken");
        var item = await LocalFileProvider.Shared.GetFileMetadataAsync(path);
        var progress = ArchiveJobs.CreateProgress(TransferOperation.Extracting, item.Name, PanelSide.Right);

        Assert.Null(await ArchiveJobs.ExtractAsync(LocalFileProvider.Shared, item, LocalFileProvider.Shared, Dir("broken-out"), progress));
        Assert.True(progress.HasErrors);
        Assert.True(progress.IsComplete);
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        new Random(42).NextBytes(bytes);
        return bytes;
    }
}
