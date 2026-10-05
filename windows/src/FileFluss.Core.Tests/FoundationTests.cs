// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using FileFluss.Core;
using FileFluss.Core.Formatting;
using FileFluss.Core.IO;
using FileFluss.Core.Models;
using FileFluss.Core.Providers;
using FileFluss.Core.Security;

namespace FileFluss.Core.Tests;

[Collection("Localization")]
public class LocalizationTests
{
    [Fact]
    public void GermanLookupUsesTheMacCatalogue()
    {
        Localization.Use(AppLanguage.German);
        try
        {
            Assert.Equal("Aktualisieren", Localization.L("Refresh"));
        }
        finally
        {
            Localization.Use(AppLanguage.English);
        }
    }

    [Fact]
    public void CocoaSpecifiersBecomeCompositeItems()
    {
        Localization.Use(AppLanguage.English);
        Assert.Equal("Item not found: /a.txt", Localization.L("Item not found: {0}", "/a.txt"));
    }

    [Fact]
    public void MissingKeyFallsBackToTheKey()
    {
        Assert.Equal("No such key ever", Localization.L("No such key ever"));
    }

    [Fact]
    public void CaseCollidingKeysStayReachable()
    {
        Localization.Use(AppLanguage.German);
        try
        {
            Assert.NotEqual("Show Hidden Files", Localization.L("Show Hidden Files"));
            Assert.NotEqual("Show hidden files", Localization.L("Show hidden files"));
        }
        finally
        {
            Localization.Use(AppLanguage.English);
        }
    }
}

public class SafeLocalPathTests
{
    [Theory]
    [InlineData("../../evil.lnk", "..-..-evil.lnk")]
    [InlineData("a/b", "a-b")]
    [InlineData("what?.txt", "what_.txt")]
    [InlineData("name.", "name")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("..", "unnamed")]
    [InlineData("", "unnamed")]
    [InlineData("CON", "_CON")]
    [InlineData("com1.txt", "_com1.txt")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("a:b|c", "a_b_c")]
    public void FileNameIsOneSafeComponent(string remote, string expected)
        => Assert.Equal(expected, SafeLocalPath.FileName(remote));

    [Fact]
    public void DestinationStaysInsideTheDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ff-safe");
        var result = SafeLocalPath.Destination("..\\..\\x.txt", directory);
        Assert.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LongNamesKeepTheirExtension()
    {
        var name = new string('a', 300) + ".pdf";
        var safe = SafeLocalPath.FileName(name);
        Assert.Equal(200, safe.Length);
        Assert.EndsWith(".pdf", safe);
    }
}

public class CloudPathTests
{
    [Theory]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("a/b/", "/a/b")]
    [InlineData("//a//b", "/a/b")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, CloudPath.Normalize(input));

    [Fact]
    public void ParentNameCombine()
    {
        Assert.Equal("/a", CloudPath.Parent("/a/b"));
        Assert.Equal("/", CloudPath.Parent("/a"));
        Assert.Equal("b", CloudPath.Name("/a/b"));
        Assert.Equal("/a/b", CloudPath.Combine("/a", "b"));
        Assert.Equal("/b", CloudPath.Combine("/", "b"));
    }

    [Fact]
    public void IsWithinRespectsComponentBoundaries()
    {
        Assert.True(CloudPath.IsWithin("/a/b/c", "/a/b"));
        Assert.False(CloudPath.IsWithin("/a/bc", "/a/b"));
    }

    [Fact]
    public void PathCacheEvictsWholeSubtree()
    {
        var cache = new Dictionary<string, string> { ["/"] = "root", ["/a"] = "1", ["/a/b"] = "2", ["/ab"] = "3" };
        cache.RemovePathSubtree("/a");
        Assert.Equal(["/", "/ab"], cache.Keys.Order());
    }

    [Fact]
    public void ListingRetiresVanishedChildren()
    {
        var cache = new Dictionary<string, string> { ["/"] = "root", ["/a"] = "1", ["/a/x"] = "2", ["/b"] = "3" };
        cache.RetainPathChildren("/", new HashSet<string> { "b" });
        Assert.Equal(["/", "/b"], cache.Keys.Order());
    }
}

[Collection("Localization")]
public class ByteFormatterTests
{
    [Fact]
    public void ExplorerStyleUnits()
    {
        Localization.Use(AppLanguage.English);
        Assert.Equal("268 bytes", ByteFormatter.Format(268));
        Assert.Equal("1.00 KB", ByteFormatter.Format(1024));
        Assert.Equal("13.5 MB", ByteFormatter.Format(14_200_000));
        Assert.Equal("999 KB", ByteFormatter.Format(1_023_900));
    }

    [Fact]
    public void Durations()
    {
        Assert.Equal("4 min", ByteFormatter.FormatDuration(TimeSpan.FromSeconds(245)));
        Assert.Equal("47 s", ByteFormatter.FormatDuration(TimeSpan.FromSeconds(47)));
        Assert.Equal("2 h 5 min", ByteFormatter.FormatDuration(TimeSpan.FromMinutes(125)));
    }
}

[Collection("Localization")]
public class ModelTests
{
    [Fact]
    public void AccountDisplayNameComposition()
    {
        Assert.Equal("Dropbox (Robert)", CloudAccount.ComposeDisplayName(CloudProviderType.Dropbox, "Robert"));
        Assert.Equal("Dropbox", CloudAccount.ComposeDisplayName(CloudProviderType.Dropbox, "Unknown"));
    }

    [Fact]
    public void AccountJsonUsesMacRawValues()
    {
        var account = new CloudAccount { ProviderType = CloudProviderType.WebDav, DisplayName = "x" };
        var json = System.Text.Json.JsonSerializer.Serialize(account);
        Assert.Contains("\"providerType\":\"webDAV\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<CloudAccount>(json);
        Assert.Equal(CloudProviderType.WebDav, back!.ProviderType);
    }

    [Fact]
    public void CredentialStoreRoundTripsJson()
    {
        var store = new MemoryCredentialStore();
        store.Save("k", new Dictionary<string, string> { ["token"] = "t" });
        Assert.Equal("t", store.Load<Dictionary<string, string>>("k")!["token"]);
        store.Delete("k");
        Assert.Null(store.Load<Dictionary<string, string>>("k"));
    }
}

public class LocalFileProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ff-local-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileProvider _provider = LocalFileProvider.Shared;

    public LocalFileProviderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ListCopyRenameMoveDelete()
    {
        var file = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(file, "hello");
        await _provider.CreateDirectoryAsync(Path.Combine(_root, "sub"));

        var items = await _provider.ListDirectoryAsync(_root);
        Assert.Contains(items, i => i is { Name: "a.txt", IsDirectory: false, Size: 5 });
        Assert.Contains(items, i => i is { Name: "sub", IsDirectory: true });

        long reported = 0;
        await _provider.DownloadFileAsync(file, Path.Combine(_root, "sub", "b.txt"), d => reported += d);
        Assert.Equal(5, reported);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_root, "sub", "b.txt")));

        await _provider.RenameItemAsync(Path.Combine(_root, "sub", "b.txt"), "B.txt");
        Assert.Contains(await _provider.ListDirectoryAsync(Path.Combine(_root, "sub")), i => i.Name == "B.txt");

        await _provider.MoveItemAsync(Path.Combine(_root, "sub"), Path.Combine(_root, "moved"));
        Assert.True(File.Exists(Path.Combine(_root, "moved", "B.txt")));
        Assert.Equal(5, await _provider.FolderSizeAsync(_root + "\\moved"));

        LocalFileProvider.RecycleHandler = null;
        await _provider.DeleteItemAsync(Path.Combine(_root, "moved"));
        Assert.False(Directory.Exists(Path.Combine(_root, "moved")));
    }

    [Fact]
    public void PathsAreNative()
    {
        Assert.Equal(string.Empty, _provider.GetParentPath("C:\\"));
        Assert.Equal("C:\\", _provider.GetParentPath("C:\\Users"));
        Assert.Equal("Users", _provider.GetName("C:\\Users\\"));
        Assert.True(_provider.IsRoot(string.Empty));
    }

    [Fact]
    public async Task ThisPcListsDrives()
    {
        var drives = await _provider.ListDirectoryAsync(string.Empty);
        Assert.NotEmpty(drives);
        Assert.All(drives, d => Assert.True(d.IsDirectory));
    }
}

public class RegistryTests
{
    [Fact]
    public void ConnectSpecInlineReauthOnlyForPureBrowserFlows()
    {
        Assert.True(ConnectSpec.Browser(ConnectField.Info("x")).SupportsInlineReauthentication);
        Assert.False(new ConnectSpec(new ConnectField("u", ConnectFieldKind.Text, "User")).SupportsInlineReauthentication);
    }
}
