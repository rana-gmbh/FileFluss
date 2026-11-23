// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Native.Tests;

/// <summary>CopyFileEx, the copy every local transfer runs through.</summary>
public sealed class CopyFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-copy-" + Guid.NewGuid().ToString("N"));

    public CopyFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ReplacingAReadOnlyFileWorksAndKeepsTheSourcesAttributes()
    {
        // A read-only file replaced by a read-only one (both sides of a copied folder): the
        // partial copy inherits the flag and the move onto the read-only target was refused,
        // which failed the Replace and left "readonly.txt.fileflusspart" behind.
        var source = Path.Combine(_root, "source.txt");
        var target = Path.Combine(_root, "readonly.txt");
        await File.WriteAllTextAsync(source, "new");
        await File.WriteAllTextAsync(target, "old");
        File.SetAttributes(source, FileAttributes.ReadOnly);
        File.SetAttributes(target, FileAttributes.ReadOnly);

        await ShellFileOperations.CopyFileAsync(source, target, null, CancellationToken.None);

        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
        Assert.False(File.Exists(target + ".fileflusspart"));
    }

    [Fact]
    public async Task AWritableCopyReplacesAReadOnlyTarget()
    {
        var source = Path.Combine(_root, "source.txt");
        var target = Path.Combine(_root, "target.txt");
        await File.WriteAllTextAsync(source, "new");
        await File.WriteAllTextAsync(target, "old");
        File.SetAttributes(target, FileAttributes.ReadOnly);

        await ShellFileOperations.CopyFileAsync(source, target, null, CancellationToken.None);

        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
        Assert.False(File.Exists(target + ".fileflusspart"));
    }

    [Fact]
    public async Task ACopyThatCannotBePutInPlaceLeavesNoPartialFile()
    {
        // The target name is taken by a folder: the copy completes, the move into place
        // fails — and the read-only partial copy must not stay behind.
        var source = Path.Combine(_root, "source.txt");
        var target = Path.Combine(_root, "taken");
        await File.WriteAllTextAsync(source, "data");
        File.SetAttributes(source, FileAttributes.ReadOnly);
        Directory.CreateDirectory(target);

        await Assert.ThrowsAnyAsync<Exception>(() => ShellFileOperations.CopyFileAsync(source, target, null, CancellationToken.None));

        Assert.False(File.Exists(target + ".fileflusspart"));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task CopiesBeyondMaxPath()
    {
        // A folder chain well past 260 characters, as deep synced trees produce.
        var deep = _root;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, "a-rather-long-folder-name-0123456789");
        }

        Directory.CreateDirectory(deep);
        var source = Path.Combine(deep, "source.bin");
        var target = Path.Combine(deep, "target.bin");
        await File.WriteAllBytesAsync(source, new byte[100_000]);
        long reported = 0;

        await ShellFileOperations.CopyFileAsync(source, target, d => reported += d, CancellationToken.None);

        Assert.Equal(100_000, new FileInfo(target).Length);
        Assert.Equal(100_000, reported);
        Assert.False(File.Exists(target + ".fileflusspart"));
    }

    [Fact]
    public async Task ACancelledCopyLeavesNothingBehind()
    {
        var source = Path.Combine(_root, "big.bin");
        var target = Path.Combine(_root, "copy.bin");
        await File.WriteAllBytesAsync(source, new byte[8 * 1024 * 1024]);
        using var cancel = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ShellFileOperations.CopyFileAsync(source, target, _ => cancel.Cancel(), cancel.Token));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".fileflusspart"));
    }
}
