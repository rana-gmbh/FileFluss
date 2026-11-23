// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using FileFluss.App.ViewModels;
using FileFluss.Core.IO;

namespace FileFluss.App.Tests;

/// <summary>
/// Opening downloaded files: programs and scripts never start from the mapped-account drive
/// (where the mark of the web can't be relied on), and a cached copy is marked before it opens.
/// </summary>
public sealed class OpenMarkTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ff-openmark-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("Report.docx", true)]
    [InlineData("Budget.xlsx", true)]
    [InlineData("photo.jpg", true)]
    [InlineData("setup.exe", false)]
    [InlineData("invoice.pdf.exe", false)]
    [InlineData("tool.bat", false)]
    [InlineData("shortcut.lnk", false)]
    public void ProgramsAndScriptsOpenFromAMarkedCopyNotTheDrive(string name, bool onTheDrive)
        => Assert.Equal(onTheDrive, MainViewModel.OpensOnTheDrive(name));

    [Fact]
    public void ACachedCopyIsMarkedWhereItIs()
    {
        var path = Path.Combine(_folder, "setup.exe");
        File.WriteAllText(path, "not really a program");

        var opened = MainViewModel.MarkedForOpening(path, "setup.exe");

        Assert.Equal(path, opened);
        Assert.Equal(3, MarkOfTheWeb.Zone(path));
    }

    [Fact]
    public void ADocumentThatCantBeMarkedStillOpensWhereItIs()
    {
        // A missing file can't carry the mark; a document then opens unmarked from where it is,
        // as a browser download on a FAT stick would — no copy is made for it.
        var path = Path.Combine(_folder, "missing.docx");

        Assert.Equal(path, MainViewModel.MarkedForOpening(path, "missing.docx"));
    }
}
