// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Search;
using static FileFluss.Core.Tests.Search.SearchTestData;

namespace FileFluss.Core.Tests.Search;

[Collection("Localization")]
public sealed class OfflineIndexProviderTests : IDisposable
{
    private readonly TempIndex _temp = new();
    private readonly Guid _account = Guid.NewGuid();

    public OfflineIndexProviderTests() => Localization.Use(AppLanguage.English);

    public void Dispose() => _temp.Dispose();

    private OfflineIndexProvider OfflineAccount()
    {
        _temp.Index.UpsertItems(
        [
            CloudItem("/Photos", isDirectory: true),
            CloudItem("/Photos/2026", isDirectory: true),
            CloudItem("/Photos/2026/beach.jpg", size: 300),
            CloudItem("/Photos/cover.png", size: 200),
            CloudItem("/Empty", isDirectory: true),
            CloudItem("/.hidden"),
            CloudItem("/readme.txt", size: 5),
        ], _account);
        return OfflineIndexProvider.ForCloudAccount(_temp.Index, Account(_account, "Dropbox (Robert)", connected: false));
    }

    private OfflineIndexProvider OfflineDrive()
    {
        _temp.Index.ReplaceFiles("vol:9", DriveKind.External.IndexKind(), "Shelf",
        [
            new IndexedFile("vol:9", "/Scans", "/", "Scans", true, 0, Stamp),
            new IndexedFile("vol:9", "/Scans/tax.pdf", "/Scans", "tax.pdf", false, 1000, Stamp),
            new IndexedFile("vol:9", "/index.html", "/", "index.html", false, 10, Stamp),
        ]);
        return OfflineIndexProvider.ForDrive(_temp.Index, "vol:9", "Shelf");
    }

    /// <summary>The ordinary panel can browse an offline account folder by folder, from the index.</summary>
    [Fact]
    public async Task AnOfflineAccountIsBrowsableFolderByFolder()
    {
        var provider = OfflineAccount();

        var root = await provider.ListDirectoryAsync(provider.RootPath);
        Assert.Equal(["Empty", "Photos", ".hidden", "readme.txt"], root.Select(i => i.Name));
        Assert.True(root.Single(i => i.Name == ".hidden").IsHidden);

        var photos = await provider.ListDirectoryAsync("/Photos");
        Assert.Equal(["2026", "cover.png"], photos.Select(i => i.Name));
        Assert.Equal(200, photos.Single(i => i.Name == "cover.png").Size);
        Assert.Equal(Stamp, photos.Single(i => i.Name == "cover.png").ModificationDate);

        Assert.Empty(await provider.ListDirectoryAsync("/Empty"));
        var missing = await Assert.ThrowsAsync<CloudProviderException>(() => provider.ListDirectoryAsync("/Nowhere"));
        Assert.Equal(CloudErrorKind.NotFound, missing.Kind);

        var file = await provider.GetFileMetadataAsync("/Photos/2026/beach.jpg");
        Assert.Equal(300, file.Size);
        Assert.Equal(500, await provider.FolderSizeAsync("/Photos"));
        Assert.Equal(515, await provider.FolderSizeAsync("/"));
    }

    [Fact]
    public async Task AnUnpluggedDriveIsBrowsableFromItsIndex()
    {
        var provider = OfflineDrive();

        Assert.Equal(["Scans", "index.html"], (await provider.ListDirectoryAsync("/")).Select(i => i.Name));
        var scans = await provider.ListDirectoryAsync("/Scans");
        Assert.Equal("/Scans/tax.pdf", Assert.Single(scans).Path);
        Assert.True((await provider.GetFileMetadataAsync("/Scans")).IsDirectory);
        Assert.Equal(1000, await provider.FolderSizeAsync("/Scans"));
        Assert.Equal("/Scans/tax.pdf", Assert.Single((await provider.SearchFilesAsync("tax", "/Scans"))!).Path);
        Assert.Empty((await provider.SearchFilesAsync("index", "/Scans"))!);
    }

    [Fact]
    public async Task ANeverIndexedSourceIsAnEmptyRootNotAnError()
    {
        var provider = OfflineIndexProvider.ForDrive(_temp.Index, "vol:none", "Blank");

        Assert.Empty(await provider.ListDirectoryAsync("/"));
        var info = provider.GetInfo();
        Assert.Equal("Never indexed", info.LastIndexedText);
        Assert.Equal("Offline view — Blank", info.Title);
        Assert.Equal("No indexed contents", OfflineSourceInfo.EmptyTitle);
    }

    [Fact]
    public void TheBannerSaysWhenTheSourceWasIndexed()
    {
        OfflineDrive();
        var info = OfflineIndexProvider.ForDrive(_temp.Index, "vol:9", "Shelf").GetInfo();

        Assert.Equal("Indexed today", info.LastIndexedText);
        Assert.Equal("Shelf", info.DisplayName);
        Assert.Equal("Read only", OfflineSourceInfo.ReadOnlyBadge);
    }

    /// <summary>Read only: every change is refused with a message that says why, through the same exception any provider throws.</summary>
    [Fact]
    public async Task ChangesAreRefusedWithAReason()
    {
        var provider = OfflineAccount();
        var local = Path.Combine(Path.GetTempPath(), "ff-offline-" + Guid.NewGuid().ToString("N"));

        var attempts = new Func<Task>[]
        {
            () => provider.DownloadFileAsync("/readme.txt", local),
            () => provider.UploadFileAsync(local, "/new.txt"),
            () => provider.DeleteItemAsync("/readme.txt"),
            () => provider.CreateDirectoryAsync("/New"),
            () => provider.RenameItemAsync("/readme.txt", "x.txt"),
            () => provider.MoveItemAsync("/readme.txt", "/Photos/readme.txt"),
            () => provider.CopyItemAsync("/readme.txt", "/Photos/readme.txt"),
            () => provider.SetModificationDateAsync("/readme.txt", DateTimeOffset.Now),
        };

        foreach (var attempt in attempts)
        {
            var error = await Assert.ThrowsAsync<CloudProviderException>(attempt);
            Assert.Equal(CloudErrorKind.CommandFailed, error.Kind);
            Assert.StartsWith("\"Dropbox (Robert)\" is offline.", error.Message);
        }

        Assert.False(File.Exists(local));
        Assert.True(provider.IsReadOnly);
    }

    [Fact]
    public async Task AnAccountWithItsOwnRootStartsThere()
    {
        _temp.Index.UpsertItems([CloudItem("/home/robert", isDirectory: true), CloudItem("/home/robert/notes.txt")], _account);
        var provider = OfflineIndexProvider.ForCloudAccount(_temp.Index, _account, "SFTP", "/home/robert/");

        Assert.Equal("/home/robert", provider.RootPath);
        Assert.Equal("notes.txt", Assert.Single(await provider.ListDirectoryAsync(provider.RootPath)).Name);
        Assert.Equal(OfflineSourceKind.Cloud, provider.SourceKind);
        Assert.Equal(_account, provider.AccountId);
    }
}
