// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

namespace FileFluss.Native.Tests;

/// <summary>CopyFileEx with a managed progress callback, which runs inside the native copy.</summary>
public sealed class ShellFileOperationsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ff-copyfileex-").FullName;

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
    public async Task CopiesAndReportsEveryByte()
    {
        var source = Source(3 * 1024 * 1024);
        var destination = Path.Combine(_folder, "copy.bin");
        long reported = 0;
        await ShellFileOperations.CopyFileAsync(source, destination, delta => Interlocked.Add(ref reported, delta), CancellationToken.None);

        Assert.Equal(new FileInfo(source).Length, new FileInfo(destination).Length);
        Assert.Equal(new FileInfo(source).Length, reported);
        Assert.False(File.Exists(destination + ".fileflusspart"));
    }

    [Fact]
    public async Task AProgressHandlerThatThrowsFailsTheCopyInsteadOfTheProcess()
    {
        // An exception escaping the callback would unwind through CopyFileEx's native frames,
        // which ends the process. It must come back out of the task instead.
        var source = Source(3 * 1024 * 1024);
        var destination = Path.Combine(_folder, "copy.bin");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ShellFileOperations.CopyFileAsync(source, destination, _ => throw new InvalidOperationException("progress broke"), CancellationToken.None));

        Assert.Equal("progress broke", failure.Message);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".fileflusspart"));
    }

    private string Source(int size)
    {
        var path = Path.Combine(_folder, "source.bin");
        var bytes = new byte[size];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
