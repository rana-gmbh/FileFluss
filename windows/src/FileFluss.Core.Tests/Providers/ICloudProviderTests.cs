// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Providers.ICloud;
using FileFluss.Core.Security;
using FileFluss.Core.Tests.Support;

namespace FileFluss.Core.Tests.Providers;

public sealed class ICloudProviderTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ff-icloud-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly MemoryCredentialStore _store = new();
    private readonly ICloudProvider _provider;

    public ICloudProviderTests()
    {
        _root = Path.Combine(_base, "iCloudDrive");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_base, "outside"));
        _provider = new ICloudProvider(_accountId, _store, () => _root);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_base, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_base, recursive: true);
    }

    private string Local(params string[] parts) => Path.Combine([_root, .. parts]);

    private void Write(string relative, string content)
    {
        var path = Local(relative.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // MARK: - Connect

    [Fact]
    public async Task ConnectSucceedsWhenTheFolderExists()
    {
        var result = await _provider.ConnectAsync(ConnectValues.Empty(), new ScriptedConnectPrompt());

        Assert.Equal("iCloud Drive", result.DisplayName);
        Assert.True(_provider.IsAuthenticated);
        Assert.Equal(_root, _store.Load<ICloudProvider.ICloudSettings>($"icloud.{_accountId}")!.RootPath);
        Assert.Equal(_root, _provider.RootFolder);
        Assert.All(_provider.ConnectSpec.Modes.SelectMany(m => m.Fields), f => Assert.False(f.HasValue));
    }

    [Fact]
    public async Task ConnectWithoutICloudForWindowsExplainsWhatIsMissing()
    {
        _store.Save($"icloud.{_accountId}", new ICloudProvider.ICloudSettings(_root));
        var before = _store.Load($"icloud.{_accountId}");
        var provider = new ICloudProvider(_accountId, _store, () => null);

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ConnectAsync(ConnectValues.Empty(), new ScriptedConnectPrompt()));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Contains("iCloud for Windows", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, _store.Load($"icloud.{_accountId}"));
    }

    [Fact]
    public void NotAuthenticatedWhenTheFolderIsGone()
    {
        var provider = new ICloudProvider(_accountId, _store, () => Path.Combine(_base, "missing"));
        Assert.False(provider.IsAuthenticated);
        Assert.Equal(CloudErrorKind.NotAuthenticated, Assert.Throws<CloudProviderException>(() => provider.LocalPath("/a")).Kind);
    }

    // MARK: - Listing

    [Fact]
    public async Task ListingMapsTheFolderAndFlagsPlaceholders()
    {
        Directory.CreateDirectory(Local("Docs"));
        Write("a.txt", "hello");
        Write(".gitignore", "bin/");
        Write("desktop.ini", "[.ShellClassInfo]");
        File.SetAttributes(Local("desktop.ini"), FileAttributes.Hidden | FileAttributes.System);
        Write("evicted.bin", "0123456789");
        File.SetAttributes(Local("evicted.bin"), FileAttributes.Offline);
        File.SetLastWriteTimeUtc(Local("a.txt"), new DateTime(2026, 4, 8, 10, 0, 0, DateTimeKind.Utc));

        var items = (await _provider.ListDirectoryAsync("/")).ToDictionary(i => i.Name);

        Assert.Equal([".gitignore", "Docs", "a.txt", "evicted.bin"], items.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("/a.txt", 5L, CloudDownloadStatus.Local), (items["a.txt"].Path, items["a.txt"].Size, items["a.txt"].DownloadStatus));
        Assert.Equal(new DateTimeOffset(2026, 4, 8, 10, 0, 0, TimeSpan.Zero), items["a.txt"].ModificationDate);
        Assert.Equal((10L, CloudDownloadStatus.Evicted), (items["evicted.bin"].Size, items["evicted.bin"].DownloadStatus));
        Assert.True(items[".gitignore"].IsHidden);
        Assert.True(items["Docs"].IsDirectory);
        Assert.Equal("/Docs", items["Docs"].Id);
    }

    [Fact]
    public async Task SubfoldersListWithFullPaths()
    {
        Write("Docs/Taxes/2025.pdf", "pdf");

        var items = await _provider.ListDirectoryAsync("/Docs/Taxes/");

        Assert.Equal("/Docs/Taxes/2025.pdf", items.Single().Path);
    }

    [Fact]
    public async Task MissingFoldersAreNotFound()
    {
        var error = await Assert.ThrowsAsync<CloudProviderException>(() => _provider.ListDirectoryAsync("/nope"));
        Assert.Equal(CloudErrorKind.NotFound, error.Kind);
    }

    [Theory]
    [InlineData("/../outside")]
    [InlineData("/Docs/../../outside")]
    [InlineData("/a\\..\\..\\outside")]
    [InlineData("/C:/Windows")]
    public void PathsCannotEscapeTheICloudFolder(string path)
        => Assert.Equal(CloudErrorKind.NotFound, Assert.Throws<CloudProviderException>(() => _provider.LocalPath(path)).Kind);

    [Fact]
    public async Task MetadataForTheRootAndFiles()
    {
        Write("a.txt", "hello");

        var root = await _provider.GetFileMetadataAsync("/");
        var file = await _provider.GetFileMetadataAsync("/a.txt");

        Assert.Equal(("iCloud Drive", true), (root.Name, root.IsDirectory));
        Assert.Equal(5, file.Size);
        Assert.Equal(CloudErrorKind.NotFound, (await Assert.ThrowsAsync<CloudProviderException>(() => _provider.GetFileMetadataAsync("/b.txt"))).Kind);
    }

    [Fact]
    public async Task FolderSizeCountsNestedFilesButNotHiddenOnes()
    {
        Write("Docs/a.txt", "12345");
        Write("Docs/Sub/b.txt", "123");
        Write("Docs/desktop.ini", "ignored");
        File.SetAttributes(Local("Docs", "desktop.ini"), FileAttributes.Hidden | FileAttributes.System);

        Assert.Equal(8, await _provider.FolderSizeAsync("/Docs"));
    }

    // MARK: - Transfers

    [Fact]
    public async Task DownloadCopiesOutWithProgress()
    {
        Write("Docs/report.txt", "quarterly numbers");
        var target = Path.Combine(_base, "outside", "report.txt");

        long reported = 0;
        await _provider.DownloadFileAsync("/Docs/report.txt", target, d => reported += d);

        Assert.Equal("quarterly numbers", await File.ReadAllTextAsync(target));
        Assert.Equal(17, reported);
        Assert.Equal(CloudErrorKind.NotFound, (await Assert.ThrowsAsync<CloudProviderException>(() => _provider.DownloadFileAsync("/Docs/missing.txt", target))).Kind);
    }

    [Fact]
    public async Task UploadCopiesInCreatingFoldersAndReplacing()
    {
        var source = Path.Combine(_base, "outside", "photo.jpg");
        await File.WriteAllTextAsync(source, "jpeg");
        Write("New/Sub/photo.jpg", "old");

        long reported = 0;
        await _provider.UploadFileAsync(source, "/New/Sub/photo.jpg", d => reported += d);
        await _provider.UploadFileAsync(source, "/Fresh/Deeper/photo.jpg");

        Assert.Equal("jpeg", await File.ReadAllTextAsync(Local("New", "Sub", "photo.jpg")));
        Assert.True(File.Exists(Local("Fresh", "Deeper", "photo.jpg")));
        Assert.Equal(4, reported);
    }

    // MARK: - Mutations

    [Fact]
    public async Task CreateRenameMoveCopyDelete()
    {
        await _provider.CreateDirectoryAsync("/Projects");
        await _provider.CreateDirectoryAsync("/Projects");
        Write("Projects/plan.txt", "plan");

        await _provider.RenameItemAsync("/Projects/plan.txt", "Plan v2.txt");
        Assert.True(File.Exists(Local("Projects", "Plan v2.txt")));

        await _provider.RenameItemAsync("/Projects", "projects");
        Assert.Equal("projects", new DirectoryInfo(_root).GetDirectories().Single().Name);

        await _provider.CopyItemAsync("/projects", "/Backup/projects");
        Assert.Equal("plan", await File.ReadAllTextAsync(Local("Backup", "projects", "Plan v2.txt")));

        await _provider.MoveItemAsync("/projects/Plan v2.txt", "/Archive/plan.txt");
        Assert.True(File.Exists(Local("Archive", "plan.txt")));
        Assert.False(File.Exists(Local("projects", "Plan v2.txt")));

        await _provider.DeleteItemAsync("/Backup");
        await _provider.DeleteItemAsync("/Archive/plan.txt");
        Assert.False(Directory.Exists(Local("Backup")));
        Assert.False(File.Exists(Local("Archive", "plan.txt")));

        Assert.Equal(CloudErrorKind.NotFound, (await Assert.ThrowsAsync<CloudProviderException>(() => _provider.DeleteItemAsync("/Backup"))).Kind);
        Assert.Equal(CloudErrorKind.NotFound, (await Assert.ThrowsAsync<CloudProviderException>(() => _provider.DeleteItemAsync("/"))).Kind);
        Assert.Equal(CloudErrorKind.CommandFailed, (await Assert.ThrowsAsync<CloudProviderException>(() => _provider.RenameItemAsync("/Archive", "a/b"))).Kind);
    }

    [Fact]
    public async Task MovingOntoAnExistingFileFails()
    {
        Write("a.txt", "a");
        Write("b.txt", "b");

        var error = await Assert.ThrowsAsync<CloudProviderException>(() => _provider.MoveItemAsync("/a.txt", "/b.txt"));

        Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
        Assert.Equal("b", await File.ReadAllTextAsync(Local("b.txt")));
    }

    [Fact]
    public async Task ModificationDatesCanBeStamped()
    {
        Write("a.txt", "a");
        var date = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

        await _provider.SetModificationDateAsync("/a.txt", date);

        Assert.Equal(date.UtcDateTime, File.GetLastWriteTimeUtc(Local("a.txt")));
    }

    [Theory]
    [InlineData(0x00400000, false, CloudDownloadStatus.Evicted)]   // RECALL_ON_DATA_ACCESS
    [InlineData(0x00001000, false, CloudDownloadStatus.Evicted)]   // OFFLINE
    [InlineData(0x00040000, false, CloudDownloadStatus.Evicted)]   // RECALL_ON_OPEN
    [InlineData(0x00080020, false, CloudDownloadStatus.Local)]     // PINNED | ARCHIVE
    [InlineData(0x00400010, true, CloudDownloadStatus.Local)]      // a folder is never "evicted"
    public void PlaceholderAttributesMapToDownloadStatus(int attributes, bool isDirectory, CloudDownloadStatus expected)
        => Assert.Equal(expected, ICloudProvider.DownloadStatus((FileAttributes)attributes, isDirectory));
}

public sealed class ICloudDriveLocatorTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ff-icloud-locator-" + Guid.NewGuid().ToString("N"));
    private readonly string _profile;

    public ICloudDriveLocatorTests()
    {
        _profile = Path.Combine(_base, "Users", "robert");
        Directory.CreateDirectory(_profile);
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_base, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeRegistry : IRegistryReader
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public bool Broken { get; init; }

        public FakeRegistry Set(string key, string name, string value)
        {
            if (!_keys.TryGetValue(key, out var values))
            {
                _keys[key] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            values[name] = value;
            return this;
        }

        public IReadOnlyList<string> GetSubKeyNames(string keyPath)
        {
            if (Broken)
            {
                throw new UnauthorizedAccessException("denied");
            }

            var prefix = keyPath + "\\";
            return _keys.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => k[prefix.Length..].Split('\\')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyDictionary<string, string> GetStringValues(string keyPath)
            => Broken ? throw new UnauthorizedAccessException("denied")
                : _keys.TryGetValue(keyPath, out var values) ? values : new Dictionary<string, string>();
    }

    [Fact]
    public void DefaultLocationWithoutRegistry()
    {
        var drive = Folder("Users", "robert", "iCloudDrive");
        Assert.Equal(drive, ICloudDriveLocator.FindRoot(null, _profile, Directory.Exists));
    }

    [Fact]
    public void SpacedFallbackAndNothing()
    {
        Assert.Null(ICloudDriveLocator.FindRoot(null, _profile, Directory.Exists));
        var spaced = Folder("Users", "robert", "iCloud Drive");
        Assert.Equal(spaced, ICloudDriveLocator.FindRoot(null, _profile, Directory.Exists));
    }

    [Fact]
    public void NavigationPaneEntryWinsOverTheDefault()
    {
        Folder("Users", "robert", "iCloudDrive");
        var moved = Folder("D", "Apple", "iCloud Drive");
        const string clsid = "{12345678-1234-1234-1234-123456789ABC}";
        var registry = new FakeRegistry()
            .Set($@"{ICloudDriveLocator.NavigationPaneKey}\{{018D5C66-4533-4307-9B53-224DE2ED1FE6}}", string.Empty, "OneDrive - Personal")
            .Set($@"{ICloudDriveLocator.NavigationPaneKey}\{clsid}", string.Empty, "iCloud Drive")
            .Set($@"{ICloudDriveLocator.ClassesClsidKey}\{clsid}\Instance\InitPropertyBag", "TargetFolderPath", moved);

        Assert.Equal(moved, ICloudDriveLocator.FindRoot(registry, _profile, Directory.Exists));
    }

    [Fact]
    public void SyncRootIsTakenOnlyForThisUser()
    {
        var mine = Folder("E", "iCloudDrive");
        var theirs = Folder("F", "iCloudDrive");
        var registry = new FakeRegistry()
            .Set($@"{ICloudDriveLocator.ProfileListKey}\S-1-5-21-9-9-9-1002", "ProfileImagePath", Path.Combine(_base, "Users", "other"))
            .Set($@"{ICloudDriveLocator.ProfileListKey}\{Sid}", "ProfileImagePath", _profile)
            .Set($@"{ICloudDriveLocator.SyncRootManagerKey}\iCloudDrive!S-1-5-21-9-9-9-1002!x\UserSyncRoots", "S-1-5-21-9-9-9-1002", theirs)
            .Set($@"{ICloudDriveLocator.SyncRootManagerKey}\OneDrive!{Sid}!Personal\UserSyncRoots", Sid, Folder("OneDrive"))
            .Set($@"{ICloudDriveLocator.SyncRootManagerKey}\iCloudDrive!{Sid}!x\UserSyncRoots", Sid, mine);

        Assert.Equal(mine, ICloudDriveLocator.FindRoot(registry, _profile, Directory.Exists));
    }

    [Fact]
    public void ApplePathValuesAreFound()
    {
        var drive = Folder("G", "iCloudDrive");
        var registry = new FakeRegistry()
            .Set($@"{ICloudDriveLocator.AppleKey}\Internet Services", "Version", "15.0")
            .Set($@"{ICloudDriveLocator.AppleKey}\iCloud\Drive", "RootPath", "\"" + drive + "\"");

        Assert.Equal(drive, ICloudDriveLocator.FindRoot(registry, _profile, Directory.Exists));
    }

    [Fact]
    public void ABrokenRegistryFallsBackToTheDefault()
    {
        var drive = Folder("Users", "robert", "iCloudDrive");
        Assert.Equal(drive, ICloudDriveLocator.FindRoot(new FakeRegistry { Broken = true }, _profile, Directory.Exists));
    }

    [Fact]
    public void RecordedButMissingFoldersAreSkipped()
    {
        var drive = Folder("Users", "robert", "iCloudDrive");
        const string clsid = "{AAAAAAAA-1234-1234-1234-123456789ABC}";
        var registry = new FakeRegistry()
            .Set($@"{ICloudDriveLocator.NavigationPaneKey}\{clsid}", string.Empty, "iCloud Drive")
            .Set($@"{ICloudDriveLocator.ClassesClsidKey}\{clsid}\Instance\InitPropertyBag", "TargetFolderPath", Path.Combine(_base, "gone"));

        Assert.Equal(drive, ICloudDriveLocator.FindRoot(registry, _profile, Directory.Exists));
    }
}
