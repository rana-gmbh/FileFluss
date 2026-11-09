// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Security.AccessControl;
using System.Security.Principal;
using FileFluss.Core.Providers;
using FileFluss.Core.Sync;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

/// <summary>
/// macOS 1.6: "Mirror stops on a folder it can't read". A local folder the user may not list
/// used to come back as an empty listing, which a Mirror from here read as "delete everything
/// in it on the other side".
/// </summary>
public sealed class LocalUnreadableFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-unreadable-" + Guid.NewGuid().ToString("N"));
    private readonly string _locked;
    private readonly FileSystemAccessRule? _deny;

    public LocalUnreadableFolderTests()
    {
        _locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(_locked);
        File.WriteAllText(Path.Combine(_locked, "inside.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
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
        if (OperatingSystem.IsWindows() && _deny is not null)
        {
            var info = new DirectoryInfo(_locked);
            var acl = info.GetAccessControl();
            acl.RemoveAccessRule(_deny);
            info.SetAccessControl(acl);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AFolderThatCantBeListedIsAnErrorNotAnEmptyFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await Assert.ThrowsAsync<CloudProviderException>(() => LocalFileProvider.Shared.ListDirectoryAsync(_locked));
    }

    [Fact]
    public async Task AMirrorFromHereStopsInsteadOfDeletingTheOtherSidesCopy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cloud = new MemoryCloudProvider();
        cloud.AddFile("/Dst/a.txt", "a");
        cloud.AddFile("/Dst/locked/inside.txt", "precious");
        var session = new SyncSession(new SyncEndpoint(LocalFileProvider.Shared, _root), new SyncEndpoint(cloud, "/Dst"))
        {
            Mode = SyncMode.Mirror,
            ConfirmDestructive = true,
        };

        await session.CalculateAsync();

        Assert.Null(session.Plan);
        Assert.NotNull(session.ErrorMessage);
        Assert.True(cloud.Exists("/Dst/locked/inside.txt"));
    }
}
