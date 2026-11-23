// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Security.AccessControl;
using System.Security.Principal;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Tests.Support;
using FileFluss.Core.Transfers;

namespace FileFluss.Core.Tests;

/// <summary>
/// A folder with a part that can't be read: the rest is copied, the unreadable part is
/// reported as failed, and a move never deletes what wasn't copied. Copying such a folder
/// used to fail the whole transfer before anything was written ("You don't have permission
/// to open 'Detection'").
/// </summary>
public sealed class TransferUnreadableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-xfer-unreadable-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _locked;
    private readonly FileSystemAccessRule? _deny;

    public TransferUnreadableTests()
    {
        _source = Path.Combine(_root, "Folder");
        _locked = Path.Combine(_source, "locked");
        Directory.CreateDirectory(_locked);
        Directory.CreateDirectory(Path.Combine(_source, "sub"));
        File.WriteAllText(Path.Combine(_locked, "inside.txt"), "precious");
        File.WriteAllText(Path.Combine(_source, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_source, "sub", "b.txt"), "b");
        LocalFileProvider.RecycleHandler = null;
        if (OperatingSystem.IsWindows())
        {
            var me = WindowsIdentity.GetCurrent().User!;
            _deny = new FileSystemAccessRule(me, FileSystemRights.ListDirectory, AccessControlType.Deny);
            var info = new DirectoryInfo(_locked);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(_deny);
            info.SetAccessControl(acl);
        }
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows() && _deny is not null && Directory.Exists(_locked))
        {
            var info = new DirectoryInfo(_locked);
            var acl = info.GetAccessControl();
            acl.RemoveAccessRule(_deny);
            info.SetAccessControl(acl);
        }

        Directory.Delete(_root, recursive: true);
    }

    private static async Task<TransferProgress> Run(TransferRequest request)
    {
        var progress = TransferEngine.CreateProgress(request, PanelSide.Right);
        await TransferEngine.RunAsync(request, progress);
        return progress;
    }

    private static CloudFileItem Folder(string path) => LocalFileProvider.ToItem(new DirectoryInfo(path));

    [Fact]
    public async Task ALocalCopyCopiesTheRestAndReportsTheUnreadableFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Path.Combine(_root, "out");
        Directory.CreateDirectory(target);

        var progress = await Run(new TransferRequest(LocalFileProvider.Shared, [Folder(_source)], LocalFileProvider.Shared, target, IsMove: false));

        Assert.Null(progress.ErrorMessage);
        Assert.Equal("a", File.ReadAllText(Path.Combine(target, "Folder", "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(target, "Folder", "sub", "b.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "Folder", "locked")), "an unreadable folder must not be created empty");
        var failure = Assert.Single(progress.Results, r => r.Status == TransferItemStatus.Failed);
        Assert.Equal(Path.Combine("Folder", "locked"), failure.Name);
        Assert.Contains(progress.Results, r => r.Status == TransferItemStatus.Succeeded && r.Name == "Folder");
        Assert.DoesNotContain("Folder", progress.SucceededNames);
        Assert.False(progress.CompletedCleanly);
    }

    [Fact]
    public async Task AMoveToAnAccountKeepsTheUnreadableFolderAndRemovesOnlyWhatArrived()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cloud = new MemoryCloudProvider();
        cloud.AddFolder("/Dst");

        var progress = await Run(new TransferRequest(LocalFileProvider.Shared, [Folder(_source)], cloud, "/Dst", IsMove: true));

        Assert.True(cloud.Exists("/Dst/Folder/a.txt"));
        Assert.True(cloud.Exists("/Dst/Folder/sub/b.txt"));
        Assert.False(cloud.Exists("/Dst/Folder/locked"));

        // What arrived left the source; the unreadable folder, its contents and the folder
        // holding it stayed.
        Assert.False(File.Exists(Path.Combine(_source, "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(_source, "sub")));
        Assert.True(Directory.Exists(_locked));
        Assert.True(Directory.Exists(_source));
        Assert.Contains(progress.Results, r => r.Status == TransferItemStatus.Failed && r.Name == Path.Combine("Folder", "locked"));
    }

    [Fact]
    public async Task ACopyFromAnAccountSkipsAFolderItCantList()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Src/a.txt", "a");
        cloud.AddFile("/Src/locked/inside.txt", "precious");
        cloud.UnlistableFolders.Add("/Src/locked");
        var target = Path.Combine(_root, "down");
        Directory.CreateDirectory(target);

        var progress = await Run(new TransferRequest(cloud, [await cloud.GetFileMetadataAsync("/Src")], LocalFileProvider.Shared, target, IsMove: false));

        Assert.Null(progress.ErrorMessage);
        Assert.Equal("a", File.ReadAllText(Path.Combine(target, "Src", "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "Src", "locked")));
        Assert.Contains(progress.Results, r => r.Status == TransferItemStatus.Failed && r.Name == "Src/locked");
    }

    [Fact]
    public async Task AMoveFromAnAccountNeverDeletesAFolderItCouldntList()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Src/a.txt", "a");
        cloud.AddFile("/Src/full/b.txt", "b");
        cloud.AddFile("/Src/half/c.txt", "c");
        cloud.AddFile("/Src/half/locked/inside.txt", "precious");
        cloud.UnlistableFolders.Add("/Src/half/locked");
        var target = Path.Combine(_root, "moved");
        Directory.CreateDirectory(target);

        var progress = await Run(new TransferRequest(cloud, [await cloud.GetFileMetadataAsync("/Src")], LocalFileProvider.Shared, target, IsMove: true));

        Assert.True(File.Exists(Path.Combine(target, "Src", "a.txt")));
        Assert.True(File.Exists(Path.Combine(target, "Src", "full", "b.txt")));
        Assert.True(File.Exists(Path.Combine(target, "Src", "half", "c.txt")));

        // The moved folder used to be deleted in one call, unreadable part and all.
        Assert.True(cloud.Exists("/Src/half/locked/inside.txt"));
        Assert.True(cloud.Exists("/Src/half"));
        Assert.True(cloud.Exists("/Src"));
        Assert.False(cloud.Exists("/Src/a.txt"));
        Assert.False(cloud.Exists("/Src/full"));
        Assert.False(cloud.Exists("/Src/half/c.txt"));
        Assert.Contains(progress.Results, r => r.Status == TransferItemStatus.Failed && r.Name == "Src/half/locked");
    }

    [Fact]
    public async Task AFolderThatCantBeListedAtAllFailsAloneAndTheOtherItemsStillGo()
    {
        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Locked/inside.txt", "precious");
        cloud.AddFile("/b.txt", "b");
        cloud.UnlistableFolders.Add("/Locked");
        var target = Path.Combine(_root, "both");
        Directory.CreateDirectory(target);

        var progress = await Run(new TransferRequest(
            cloud, [await cloud.GetFileMetadataAsync("/Locked"), await cloud.GetFileMetadataAsync("/b.txt")], LocalFileProvider.Shared, target, IsMove: true));

        Assert.Null(progress.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(target, "b.txt")));
        Assert.Contains(progress.Results, r => r.Status == TransferItemStatus.Failed && r.Name == "Locked");
        Assert.True(cloud.Exists("/Locked/inside.txt"));
        Assert.False(cloud.Exists("/b.txt"));
    }
}
