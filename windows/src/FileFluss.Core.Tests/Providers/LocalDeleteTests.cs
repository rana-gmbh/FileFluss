// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Tests.Providers;

/// <summary>Permanent local deletes: a link to a folder goes, the folder it points to is left exactly as it was.</summary>
public sealed class LocalDeleteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-delete-" + Guid.NewGuid().ToString("N"));

    public LocalDeleteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void DeletingAJunctionLeavesItsTargetUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var file = Path.Combine(target, "keep.txt");
        File.WriteAllText(file, "data");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var link = Path.Combine(_root, "link");
        CreateJunction(link, target);
        Assert.True(File.Exists(Path.Combine(link, "keep.txt")));

        LocalFileProvider.DeletePermanently(link);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(file));
        Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly), "the target's read-only file must keep its flag");
    }

    [Fact]
    public void DeletingAFolderWithReadOnlyFilesStillWorks()
    {
        var folder = Path.Combine(_root, "folder");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        var file = Path.Combine(folder, "sub", "ro.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        LocalFileProvider.DeletePermanently(folder);

        Assert.False(Directory.Exists(folder));
    }

    /// <summary>A directory junction (no admin rights or developer mode needed, unlike a symbolic link).</summary>
    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
