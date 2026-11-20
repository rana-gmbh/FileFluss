// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;

namespace FileFluss.Native.Tests;

/// <summary>
/// macOS 1.6 "a refused Trash names the real cause": the look behind a refused Recycle Bin
/// move. Nothing here moves anything to the real Recycle Bin — only the diagnosis runs.
/// </summary>
public sealed class RecycleRefusalTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ff-recycle-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void AFileOpenElsewhereIsNamedAsInUse()
    {
        var path = Path.Combine(_folder, "open.txt");
        File.WriteAllText(path, "x");

        // Open without FILE_SHARE_DELETE, as most programs hold a document.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.Equal(32, ShellFileOperations.ProbeDeleteAccess(path));

            // Whatever code the shell gave, the probe finds the program holding it.
            Assert.Equal(RecycleRefusalKind.InUse, ShellFileOperations.Diagnose(path, 0x78).Kind);
            Assert.Equal(RecycleRefusalKind.InUse, ShellFileOperations.Diagnose(path, 0x402).Kind);
        }
    }

    [Fact]
    public void AFileThatCouldBeDeletedIsNotBlamedOnPermissions()
    {
        var path = Path.Combine(_folder, "free.txt");
        File.WriteAllText(path, "x");

        Assert.Equal(0, ShellFileOperations.ProbeDeleteAccess(path));
        var refusal = ShellFileOperations.Diagnose(path, 0x402);
        Assert.NotEqual(RecycleRefusalKind.NoPermission, refusal.Kind);
        Assert.NotEqual(RecycleRefusalKind.InUse, refusal.Kind);
        Assert.True(File.Exists(path), "the probe must not delete anything");
    }

    [Fact]
    public void ACorruptionCodeBlamesTheDrivesRecycleBin()
    {
        var path = Path.Combine(_folder, "any.txt");
        File.WriteAllText(path, "x");

        var refusal = ShellFileOperations.Diagnose(path, 1392);
        Assert.Equal(RecycleRefusalKind.RecycleBinDamaged, refusal.Kind);
        Assert.Equal(Path.GetPathRoot(path), refusal.Location);
    }

    [Fact]
    public void APathLongerThanTheRecycleBinTakesIsSaidSo()
    {
        var path = Path.Combine(_folder, new string('a', 120), new string('b', 120), "c.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");

        Assert.Equal(RecycleRefusalKind.PathTooLong, ShellFileOperations.Diagnose(path, 0x7C).Kind);
    }

    [Fact]
    public void AnItemThatIsGoneIsNotFound()
        => Assert.Equal(RecycleRefusalKind.NotFound, ShellFileOperations.Diagnose(Path.Combine(_folder, "missing.txt"), 2).Kind);

    [Fact]
    public void ANetworkShareHasNoRecycleBin()
        => Assert.Equal(RecycleRefusalKind.NoRecycleBin, ShellFileOperations.MoveToRecycleBin(@"\\server\share\file.txt")?.Kind);

    [Fact]
    public void ThisUsersRecycleBinOnTheSystemDriveIsUsable()
        => Assert.False(ShellFileOperations.RecycleBinFolderUnusable(Path.GetPathRoot(Environment.SystemDirectory)!));
}
