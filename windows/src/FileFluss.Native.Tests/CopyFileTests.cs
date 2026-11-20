// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Native.Tests;

/// <summary>CopyFileEx, the copy every local transfer runs through.</summary>
public sealed class CopyFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-copy-" + Guid.NewGuid().ToString("N"));

    public CopyFileTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

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
