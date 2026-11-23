// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.IO;
using FileFluss.App.Services;

namespace FileFluss.App.Tests;

/// <summary>What a path typed into the path bar means, before anything asks the disk.</summary>
public sealed class TypedPathTests
{
    [Theory]
    [InlineData(@"C:\Users\rober")]
    [InlineData("C:/Users/rober")]
    [InlineData("c:")]
    [InlineData(@"D:\")]
    [InlineData(@"\\server\share\folder")]
    [InlineData("\"C:\\Program Files\"")]
    [InlineData("%USERPROFILE%")]
    [InlineData(@"%SystemRoot%\System32")]
    [InlineData("~")]
    [InlineData(@"~\Documents")]
    public void PathsOnThisPcAreRecognisedInAnyPanel(string typed)
        => Assert.True(TypedPath.NamesThisPc(typed));

    [Theory]
    [InlineData("/Photos")]
    [InlineData("Photos/2024")]
    [InlineData("/")]
    [InlineData("Reports")]
    [InlineData("100% done")]
    [InlineData("%NO_SUCH_VARIABLE_FOR_FILEFLUSS%")]
    [InlineData("C")]
    [InlineData("C:folder")]
    [InlineData(@"\folder")]
    public void CloudStylePathsStayInTheAccount(string typed)
        => Assert.False(TypedPath.NamesThisPc(typed));

    [Fact]
    public void ABareDriveLetterMeansTheDrivesRoot()
        => Assert.Equal(@"D:\", TypedPath.ResolveLocal("D:", @"C:\Users"));

    [Fact]
    public void RelativePathsResolveAgainstTheCurrentFolder()
        => Assert.Equal(@"C:\Users\rober\Documents", TypedPath.ResolveLocal(@"rober\Documents", @"C:\Users"));

    [Fact]
    public void VariablesAndTheHomeTildeExpand()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "Documents"), TypedPath.ResolveLocal(@"~\Documents", string.Empty));
        Assert.Equal(home, TypedPath.ResolveLocal("%USERPROFILE%", string.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void EmptyInputResolvesToNothing(string typed)
        => Assert.Null(TypedPath.ResolveLocal(typed, @"C:\Users"));

    [Fact]
    public void ProbeTellsFoldersFromFilesFromNothing()
    {
        var folder = Directory.CreateTempSubdirectory("FileFluss-typed-").FullName;
        var file = Path.Combine(folder, "a.txt");
        File.WriteAllText(file, "x");
        try
        {
            Assert.Equal(TypedPathKind.Folder, TypedPath.Probe(folder));
            Assert.Equal(TypedPathKind.File, TypedPath.Probe(file));
            Assert.Equal(TypedPathKind.None, TypedPath.Probe(Path.Combine(folder, "missing")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
