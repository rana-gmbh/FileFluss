// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.IO;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Tests;

/// <summary>Tests that install <see cref="LocalFileProvider.RecycleHandler"/>, a static every local-delete test reads.</summary>
[CollectionDefinition("RecycleHandler", DisableParallelization = true)]
public sealed class RecycleHandlerCollection;

/// <summary>macOS 1.6 "a refused Trash names the real cause", for the Recycle Bin.</summary>
[Collection("RecycleHandler")]
public sealed class RecycleRefusalTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ff-recycle-core-").FullName;

    public void Dispose()
    {
        LocalFileProvider.RecycleHandler = null;
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(5, RecycleRefusalKind.AccessDenied)]
    [InlineData(0x78, RecycleRefusalKind.AccessDenied)]
    [InlineData(32, RecycleRefusalKind.InUse)]
    [InlineData(206, RecycleRefusalKind.PathTooLong)]
    [InlineData(0x79, RecycleRefusalKind.PathTooLong)]
    [InlineData(2, RecycleRefusalKind.NotFound)]
    [InlineData(0x7C, RecycleRefusalKind.NotFound)]
    [InlineData(1392, RecycleRefusalKind.RecycleBinDamaged)]
    [InlineData(unchecked((int)0x80070570), RecycleRefusalKind.RecycleBinDamaged)]
    [InlineData(0x402, RecycleRefusalKind.Unknown)]
    public void ShellCodesAreReadAsTheirCause(int code, RecycleRefusalKind expected)
        => Assert.Equal(expected, RecycleRefusal.Classify(code));

    [Theory]
    [InlineData(RecycleRefusalKind.NoPermission, false)]
    [InlineData(RecycleRefusalKind.InUse, false)]
    [InlineData(RecycleRefusalKind.RecycleBinDamaged, true)]
    [InlineData(RecycleRefusalKind.PathTooLong, true)]
    [InlineData(RecycleRefusalKind.Unknown, true)]
    public void DeletingOutrightIsOfferedOnlyWhereItCanWork(RecycleRefusalKind kind, bool offered)
        => Assert.Equal(offered, new RecycleRefusal(kind).OffersPermanentDelete);

    [Fact]
    public void TheExplanationNamesThePlaceAtFault()
    {
        Assert.Contains(@"C:\Locked", new RecycleRefusal(RecycleRefusalKind.NoPermission, 5, @"C:\Locked").Message("\"a.txt\""), StringComparison.Ordinal);
        Assert.Contains(@"D:\", new RecycleRefusal(RecycleRefusalKind.RecycleBinDamaged, 1392, @"D:\").Message("\"a.txt\""), StringComparison.Ordinal);
        Assert.Contains("0x402", new RecycleRefusal(RecycleRefusalKind.Unknown, 0x402).Message("\"a.txt\""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedDeleteCarriesTheCauseAndKeepsTheFile()
    {
        var path = Path.Combine(_folder, "kept.txt");
        await File.WriteAllTextAsync(path, "x");
        var refusal = new RecycleRefusal(RecycleRefusalKind.RecycleBinDamaged, 1392, @"C:\");
        LocalFileProvider.RecycleHandler = _ => refusal;

        var failure = await Assert.ThrowsAsync<RecycleRefusedException>(() => LocalFileProvider.Shared.DeleteItemAsync(path));

        Assert.Same(refusal, failure.Refusal);
        Assert.Equal(path, failure.ItemPath);
        Assert.IsAssignableFrom<IOException>(failure);
        Assert.True(File.Exists(path), "a refused Recycle Bin move must never fall back to deleting");
    }

    [Fact]
    public async Task AnAcceptedMoveDoesNotThrow()
    {
        var path = Path.Combine(_folder, "gone.txt");
        await File.WriteAllTextAsync(path, "x");
        LocalFileProvider.RecycleHandler = p =>
        {
            File.Delete(p);
            return null;
        };

        await LocalFileProvider.Shared.DeleteItemAsync(path);
        Assert.False(File.Exists(path));
    }
}
