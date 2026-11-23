// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Diagnostics;
using FileFluss.Core.Providers;

namespace FileFluss.Core.Tests.Providers;

/// <summary>The drive-name cache and its reader are shared: these tests run on their own.</summary>
[CollectionDefinition("DriveLabels", DisableParallelization = true)]
public sealed class DriveLabelsCollection;

/// <summary>
/// The path bar and window title ask for a drive's name on the UI thread. Reading it waits for
/// the volume — up to a minute for a mapped drive whose server is gone — so the UI-facing call
/// must answer at once and refresh in the background.
/// </summary>
[Collection("DriveLabels")]
public sealed class DriveLabelCacheTests : IDisposable
{
    private readonly Func<string, string> _originalReader = LocalFileProvider.DriveLabelReader;

    public DriveLabelCacheTests() => LocalFileProvider.ForgetDriveLabels();

    public void Dispose()
    {
        LocalFileProvider.DriveLabelReader = _originalReader;
        LocalFileProvider.ForgetDriveLabels();
    }

    [Fact]
    public async Task AnUnreachableDriveDoesNotBlockAndItsNameArrivesLater()
    {
        using var release = new ManualResetEventSlim();
        LocalFileProvider.DriveLabelReader = root =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return $"Archive ({root.TrimEnd('\\')})";
        };
        var changed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<string> handler = (_, root) => changed.TrySetResult(root);
        LocalFileProvider.DriveLabelChanged += handler;
        try
        {
            var watch = Stopwatch.StartNew();
            var first = LocalFileProvider.CachedDriveLabel(@"Q:\");
            var display = LocalFileProvider.DisplayName(@"Q:\");
            watch.Stop();

            Assert.Equal("Q:", first);
            Assert.Equal("Q:", display);
            Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");

            release.Set();
            Assert.Equal(@"Q:\", await changed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(@"Archive (Q:)", LocalFileProvider.CachedDriveLabel(@"Q:\"));
            Assert.Equal(@"Archive (Q:)", LocalFileProvider.DisplayName(@"Q:\"));
        }
        finally
        {
            LocalFileProvider.DriveLabelChanged -= handler;
        }
    }

    [Fact]
    public async Task OnlyOneBackgroundReadRunsPerDrive()
    {
        var reads = 0;
        using var release = new ManualResetEventSlim();
        LocalFileProvider.DriveLabelReader = root =>
        {
            Interlocked.Increment(ref reads);
            release.Wait(TimeSpan.FromSeconds(10));
            return "Disk (R:)";
        };
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<string> handler = (_, _) => changed.TrySetResult();
        LocalFileProvider.DriveLabelChanged += handler;
        try
        {
            for (var i = 0; i < 20; i++)
            {
                LocalFileProvider.CachedDriveLabel(@"R:\");
            }

            release.Set();
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, Volatile.Read(ref reads));
        }
        finally
        {
            LocalFileProvider.DriveLabelChanged -= handler;
        }
    }

    [Fact]
    public void FolderNamesNeverAskTheVolume()
    {
        LocalFileProvider.DriveLabelReader = _ => throw new InvalidOperationException("must not read the volume");

        Assert.Equal("Documents", LocalFileProvider.DisplayName(@"Q:\Users\Documents"));
        Assert.Equal("Documents", LocalFileProvider.DisplayName(@"Q:\Users\Documents\"));
    }
}
