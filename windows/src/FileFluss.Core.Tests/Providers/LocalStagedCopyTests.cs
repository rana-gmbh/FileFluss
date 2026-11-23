// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Providers;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// The platform-neutral staged copy (the Windows app uses CopyFileEx, which shares the same
/// "put it in place" step): a Replace onto a read-only file works and nothing is left behind.
/// </summary>
public sealed class LocalStagedCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-staged-" + Guid.NewGuid().ToString("N"));

    public LocalStagedCopyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ReplacingAReadOnlyFileWorks()
    {
        var source = Path.Combine(_root, "source.txt");
        var target = Path.Combine(_root, "target.txt");
        await File.WriteAllTextAsync(source, "new");
        await File.WriteAllTextAsync(target, "old");
        File.SetAttributes(target, FileAttributes.ReadOnly);

        await LocalFileProvider.StreamCopyAsync(source, target, null, CancellationToken.None);

        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".fileflusspart"));
    }

    [Fact]
    public void DeletePartialRemovesAReadOnlyLeftover()
    {
        var partial = Path.Combine(_root, "x.txt.fileflusspart");
        File.WriteAllText(partial, "data");
        File.SetAttributes(partial, FileAttributes.ReadOnly);

        LocalFileProvider.DeletePartial(partial);

        Assert.False(File.Exists(partial));
    }
}
