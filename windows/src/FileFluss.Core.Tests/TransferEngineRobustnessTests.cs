// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Text;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Transfers;

namespace FileFluss.Core.Tests;

/// <summary>The ways a transfer can lose data that the happy-path tests don't reach.</summary>
// Asserts English messages: kept out of parallel runs with tests that switch the language.
[Collection("Localization")]
public sealed class TransferEngineRobustnessTests : IDisposable
{
    private static readonly string Nfc = "Café.txt";
    private static readonly string Nfd = "Café.txt";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-transfer-r-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileProvider _local = LocalFileProvider.Shared;

    public TransferEngineRobustnessTests()
    {
        Directory.CreateDirectory(_root);
        LocalFileProvider.RecycleHandler = null;
    }

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

    private string LocalDir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<TransferProgress> Run(TransferRequest request)
    {
        var progress = TransferEngine.CreateProgress(request, PanelSide.Right);
        await TransferEngine.RunAsync(request, progress);
        return progress;
    }

    private static ConflictResolver Always(ConflictChoice choice)
        => (_, _) => Task.FromResult(new ConflictResolution(choice, true));

    [Fact]
    public async Task MoveKeepsTheSourceWhenTheUploadArrivesTruncated()
    {
        var source = LocalDir("trunc");
        var file = Path.Combine(source, "big.bin");
        await File.WriteAllTextAsync(file, new string('x', 1000));
        var cloud = new MemoryCloudProvider();
        cloud.TruncatingUploads.Add("big.bin");

        var progress = await Run(new TransferRequest(_local, [await _local.GetFileMetadataAsync(file)], cloud, "/", IsMove: true));

        Assert.Equal(1, progress.FailureCount);
        Assert.True(File.Exists(file), "the only complete copy must survive");
        Assert.Contains("big.bin", progress.Results[0].ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FolderMoveKeepsTheSourceWhenOneFileArrivesTruncated()
    {
        var a = new MemoryCloudProvider();
        var b = new MemoryCloudProvider();
        a.AddFile("/F/ok.txt", "1234");
        a.AddFile("/F/sub/bad.txt", "12345678");
        b.TruncatingUploads.Add("bad.txt");

        var progress = await Run(new TransferRequest(a, [await a.GetFileMetadataAsync("/F")], b, "/", IsMove: true));

        Assert.Equal(1, progress.FailureCount);
        Assert.True(a.Exists("/F/sub/bad.txt"));
        Assert.True(a.Exists("/F/ok.txt"));
    }

    [Fact]
    public async Task NamesDifferingOnlyInCaseBothSurviveOnACaseInsensitiveTarget()
    {
        var cloud = new MemoryCloudProvider(); // case-sensitive, like S3
        cloud.AddFile("/a.txt", "lower");
        cloud.AddFile("/A.txt", "upper");
        cloud.AddFile("/dir/x.txt", "lower");
        cloud.AddFile("/dir/X.txt", "upper");
        var target = LocalDir("case");
        var items = await cloud.ListDirectoryAsync("/");

        var progress = await Run(new TransferRequest(cloud, items, _local, target, IsMove: true));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        var top = Directory.GetFiles(target).Select(f => File.ReadAllText(f)).Order().ToList();
        Assert.Equal(["lower", "upper"], top);
        var inner = Directory.GetFiles(Path.Combine(target, "dir")).Select(f => File.ReadAllText(f)).Order().ToList();
        Assert.Equal(["lower", "upper"], inner);
        Assert.False(cloud.Exists("/a.txt"));
    }

    [Fact]
    public async Task NamesThatCollideOnceMadeLegalBothSurvive()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/d/x:y.txt", "colon");
        cloud.AddFile("/d/x_y.txt", "underscore");
        cloud.AddFile("/d/trailing.", "dot");
        cloud.AddFile("/d/trailing", "plain");
        var target = LocalDir("legal");

        var progress = await Run(new TransferRequest(cloud, [await cloud.GetFileMetadataAsync("/d")], _local, target, IsMove: false));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        var contents = Directory.GetFiles(Path.Combine(target, "d")).Select(f => File.ReadAllText(f)).Order().ToList();
        Assert.Equal(["colon", "dot", "plain", "underscore"], contents);
    }

    [Fact]
    public async Task ReplaceMatchesAcrossUnicodeFormsOnTheLocalDisk()
    {
        var target = LocalDir("nfc");
        await File.WriteAllTextAsync(Path.Combine(target, Nfc), "old");
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/" + Nfd, "new from a Mac");
        ConflictInfo? asked = null;

        var progress = await Run(new TransferRequest(cloud, [await cloud.GetFileMetadataAsync("/" + Nfd)], _local, target, IsMove: false)
        {
            ResolveConflict = (conflict, _) =>
            {
                asked = conflict;
                return Task.FromResult(new ConflictResolution(ConflictChoice.Replace, false));
            },
        });

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.NotNull(asked);
        var files = Directory.GetFiles(target);
        Assert.Single(files);
        Assert.Equal("new from a Mac", await File.ReadAllTextAsync(files[0]));
    }

    [Fact]
    public async Task ConflictsAreDetectedUnderTheSanitizedLocalName()
    {
        var target = LocalDir("sanitized");
        await File.WriteAllTextAsync(Path.Combine(target, "what_.txt"), "keep me");
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/what?.txt", "incoming");

        var progress = await Run(new TransferRequest(cloud, [await cloud.GetFileMetadataAsync("/what?.txt")], _local, target, IsMove: false)
        {
            ResolveConflict = Always(ConflictChoice.Skip),
        });

        Assert.Equal(1, progress.SkippedCount);
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(target, "what_.txt")));
    }

    [Fact]
    public async Task ReplaceWithoutServerSideMoveDoesNotFailOnTheAlreadyDeletedTarget()
    {
        var cloud = new MemoryCloudProvider { SupportsServerSide = false };
        cloud.AddFile("/a/f.txt", "new");
        cloud.AddFile("/b/f.txt", "old");

        var progress = await Run(new TransferRequest(cloud, [await cloud.GetFileMetadataAsync("/a/f.txt")], cloud, "/b", IsMove: true)
        {
            ResolveConflict = Always(ConflictChoice.Replace),
        });

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.Equal("new", cloud.Read("/b/f.txt"));
        Assert.False(cloud.Exists("/a/f.txt"));
    }

    [Fact]
    public async Task LocalFolderMoveLeavesFilesAddedWhileItTravelled()
    {
        var source = LocalDir("live");
        var folder = Path.Combine(source, "Project");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(folder, "sub", "b.txt"), "b");
        var cloud = new MemoryCloudProvider();
        var late = Path.Combine(folder, "sub", "saved-meanwhile.txt");
        cloud.OnUpload = _ =>
        {
            if (!File.Exists(late))
            {
                File.WriteAllText(late, "unsaved work");
            }
        };

        var progress = await Run(new TransferRequest(_local, [await _local.GetFileMetadataAsync(folder)], cloud, "/", IsMove: true));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.True(cloud.Exists("/Project/a.txt"));
        Assert.True(cloud.Exists("/Project/sub/b.txt"));
        Assert.False(File.Exists(Path.Combine(folder, "a.txt")));
        Assert.True(File.Exists(late), "a file the move never copied must not be deleted");
    }

    [Fact]
    public async Task EmptyFoldersAndEmptyFilesMove()
    {
        var a = new MemoryCloudProvider();
        var b = new MemoryCloudProvider();
        a.AddFolder("/E/empty");
        a.AddFile("/E/zero.bin", string.Empty);

        var progress = await Run(new TransferRequest(a, [await a.GetFileMetadataAsync("/E")], b, "/", IsMove: true));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.True(b.Exists("/E/empty"));
        Assert.Equal(string.Empty, b.Read("/E/zero.bin"));
        Assert.False(a.Exists("/E"));
        Assert.Equal(1.0, progress.Fraction);
    }

    [Fact]
    public async Task ACancellationNobodyAskedForIsAFailureNotASuccess()
    {
        var source = LocalDir("timeout");
        await File.WriteAllTextAsync(Path.Combine(source, "f.txt"), "x");
        var cloud = new MemoryCloudProvider { OnUpload = _ => throw new TaskCanceledException("HttpClient timeout") };

        var progress = await Run(new TransferRequest(_local, await _local.ListDirectoryAsync(source), cloud, "/", IsMove: true));

        Assert.False(progress.IsCancelled);
        Assert.Equal(1, progress.FailureCount);
        Assert.True(File.Exists(Path.Combine(source, "f.txt")));
        Assert.StartsWith("Failed", progress.CompletionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserCancelMidTransferKeepsSourcesAndStops()
    {
        var a = new MemoryCloudProvider();
        var b = new MemoryCloudProvider();
        a.AddFile("/1.txt", "1");
        a.AddFile("/2.txt", "2");
        var request = new TransferRequest(a, await a.ListDirectoryAsync("/"), b, "/", IsMove: true);
        var progress = TransferEngine.CreateProgress(request, PanelSide.Left);
        b.OnUpload = _ =>
        {
            progress.Cancel();
            progress.CancellationToken.ThrowIfCancellationRequested();
        };

        await TransferEngine.RunAsync(request, progress);

        Assert.True(progress.IsCancelled);
        Assert.True(a.Exists("/1.txt"));
        Assert.True(a.Exists("/2.txt"));
    }

    [Fact]
    public async Task WordPressStyleDestinationsSkipVerification()
    {
        // A destination that can't be checked keeps the old behaviour rather than failing every move.
        var source = LocalDir("unverifiable");
        var file = Path.Combine(source, "pic.jpg");
        await File.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("jpeg"));
        var destination = new MemoryCloudProvider { VerifiableArrivals = false };
        destination.TruncatingUploads.Add("pic.jpg");

        var progress = await Run(new TransferRequest(_local, [await _local.GetFileMetadataAsync(file)], destination, "/", IsMove: true));

        Assert.True(progress.CompletedCleanly, progress.CompletionSummary);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void DiskFullReadsAsNotEnoughSpace()
    {
        var full = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        Assert.Equal("Not enough space", TransferEngine.Describe(full));
        Assert.Equal("The request timed out.", TransferEngine.Describe(new TaskCanceledException()));
    }
}
